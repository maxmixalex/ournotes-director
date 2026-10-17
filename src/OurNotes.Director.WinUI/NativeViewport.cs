using System.Runtime.InteropServices.WindowsRuntime;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using OurNotes.Director.Core;
using Windows.Foundation;
using Windows.UI;

namespace OurNotes.Director.WinUI;

/// <summary>
/// Native XAML presentation of the persistent OpenGL session's raw frames. WriteableBitmap
/// is updated in place; no CLI invocation, disk frame, decoder or video proxy is involved.
/// </summary>
public sealed partial class NativeViewport : UserControl, IAsyncDisposable
{
    readonly RealtimePreviewSession session=new();
    readonly Grid surface=new(){Background=Brush(18,21,28)};
    readonly Image image=new(){Stretch=Stretch.Fill,IsHitTestVisible=false,HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top};
    readonly Canvas checker=new(){IsHitTestVisible=false};
    readonly Canvas overlay=new(){IsHitTestVisible=false};
    readonly Rectangle outline=new(){Stroke=Brush(91,202,255),StrokeThickness=2,RadiusX=3,RadiusY=3};
    readonly TextBlock selectionText=new(){FontSize=12,Foreground=Brush(231,247,255)};
    readonly Border selectionLabel=new(){Background=Brush(18,66,92),CornerRadius=new(3),Padding=new(7,3,7,3)};
    readonly Rectangle[] handles=Enumerable.Range(0,4).Select(_=>new Rectangle{Width=7,Height=7,Fill=Brush(217,243,255),Stroke=Brush(24,83,109),StrokeThickness=1}).ToArray();
    readonly RectangleGeometry selectionClip=new(),surfaceClip=new();
    readonly ToolTip diagnostics=new();
    readonly TextBlock status=new(){Text="打开项目，开始预览",FontSize=12,Foreground=Brush(205,215,230),TextWrapping=TextWrapping.Wrap};
    readonly object frameGate=new();
    readonly List<(long After,TaskCompletionSource<PreviewFrame> Completion)> waiters=[];
    WriteableBitmap? bitmap;
    Stream? pixelStream;
    byte[] bgra=[];
    PreviewFrame? lastFrame,pendingFrame;
    bool presentationQueued,disposed;
    bool loading;
    long sourceVersion;
    string? selected,dragTarget;
    uint dragPointer;
    Point dragStart,dragLast;
    bool dragMoved;
    Rect imageRect;
    Rect selectionRect;
    PreviewTarget? displayedTarget;
    long statusClock;
    bool statusPlaying,diagnosticFailure;
    public double PresentationMilliseconds{get;private set;}
    public event EventHandler<PreviewFrame>? FramePresented;
    public event EventHandler<Exception>? Failed;
    public event EventHandler<string?>? SelectionChanged;
    public event EventHandler<PreviewDrag>? ObjectDragged;
    public event EventHandler<PreviewScale>? ObjectScaled;
    public PreviewFrame? LastFrame=>Volatile.Read(ref lastFrame);
    public Func<PreviewTarget,string>? TargetLabel { get; set; }
    public bool IsPlaying=>session.IsPlaying;
    public bool HasCurrentSource{get;private set;}
    public string? SelectedTargetId
    {
        get=>selected;
        set
        {
            if(selected==value)return;
            selected=value;DrawSelection();SelectionChanged?.Invoke(this,value);
        }
    }

    public NativeViewport()
    {
        HorizontalContentAlignment=HorizontalAlignment.Stretch;
        VerticalContentAlignment=VerticalAlignment.Stretch;
        MinWidth=100;MinHeight=100;
        var root=new Grid();
        root.RowDefinitions.Add(new(){Height=new(1,GridUnitType.Star)});
        root.RowDefinitions.Add(new(){Height=GridLength.Auto});
        surface.Children.Add(checker);surface.Children.Add(image);surface.Children.Add(overlay);surface.Clip=surfaceClip;
        selectionLabel.Child=selectionText;overlay.Children.Add(outline);overlay.Children.Add(selectionLabel);
        foreach(var handle in handles)overlay.Children.Add(handle);
        overlay.Clip=selectionClip;overlay.Visibility=Visibility.Collapsed;
        root.Children.Add(surface);
        var bar=new Border{Background=Brush(27,31,40),Padding=new(12,7,12,7),Child=status};
        Grid.SetRow(bar,1);root.Children.Add(bar);Content=root;
        AutomationProperties.SetName(this,"实时画布");
        AutomationProperties.SetHelpText(this,"点击选择、拖动角色；滚轮缩放画布，Ctrl+滚轮缩放角色。");
        ToolTipService.SetToolTip(status,diagnostics);
        diagnostics.Opened+=(_,_)=>
        {
            if(diagnosticFailure||LastFrame is not {} frame)return;
            diagnostics.Content=$"{(frame.IsGpuPresented?"OpenGL → D3D11 共享纹理；无 CPU 帧传输":frame.PresentationFallback??"OpenGL 绘制，CPU 像素显示")}\n实际刷新 {frame.FramesPerSecond:0.0} fps · 渲染 {frame.RenderMilliseconds:0.0} ms · 显示 {PresentationMilliseconds:0.0} ms\n{frame.Pixels.Width} × {frame.Pixels.Height} · 第 {frame.Frame} 帧\n{frame.Device}";
        };
        surface.SizeChanged+=(_,_)=>LayoutCanvas();
        surface.PointerPressed+=OnPointerPressed;
        surface.PointerMoved+=OnPointerMoved;
        surface.PointerReleased+=OnPointerReleased;
        surface.PointerCanceled+=OnPointerCanceled;
        surface.PointerCaptureLost+=OnPointerCanceled;
        surface.PointerWheelChanged+=OnPointerWheelChanged;
        InitializeWorkspaceNavigation();InitializeGpuPresentation();InitializeAudioPresentation();
        Unloaded+=(_,_)=>{if(!disposed)session.Pause();};
        session.FrameReady+=OnFrameReady;session.Failed+=OnFailed;
    }

    static SolidColorBrush Brush(byte r,byte g,byte b)=>new(Color.FromArgb(255,r,g,b));

    public async Task LoadSceneAsync(ScenePreviewSource source)
    {
        long version=BeginLoad("正在载入场景…");
        try
        {
            await session.LoadSceneAsync(source with{GpuPresentation=true});await AwaitPresentationAsync();
            if(version==sourceVersion)ConfigureAudio(source);
            if(version==sourceVersion){HasCurrentSource=true;loading=false;UpdateStatus();DrawSelection();}
        }
        catch(Exception exception){if(version==sourceVersion){loading=false;PresentFailure(exception);}throw;}
    }
    public async Task LoadHomeAsync(HomePreviewSource source)
    {
        long version=BeginLoad("正在载入主页…");
        try
        {
            await session.LoadHomeAsync(source with{GpuPresentation=true});await AwaitPresentationAsync();
            if(version==sourceVersion){HasCurrentSource=true;loading=false;UpdateStatus();DrawSelection();}
        }
        catch(Exception exception){if(version==sourceVersion){loading=false;PresentFailure(exception);}throw;}
    }
    long BeginLoad(string message)
    {
        previewAudio.Clear();
        showingEmptyCanvas=false;HasCurrentSource=false;loading=true;dragTarget=null;dragMoved=false;
        surface.ReleasePointerCaptures();HideSelection();status.Text=message;diagnostics.Content=null;diagnosticFailure=false;
        return ++sourceVersion;
    }
    /// <summary>
    /// Invalidates an old picture as soon as the editor switches documents, including
    /// cases where archive/dependency validation fails before a native load can begin.
    /// </summary>
    public void InvalidateSource()
    {
        previewAudio.Clear();session.Pause();HasCurrentSource=false;loading=false;sourceVersion++;
        dragTarget=null;dragMoved=false;surface.ReleasePointerCaptures();HideSelection();
        status.Text=LastFrame is null?"打开项目，开始预览":"等待载入，保留上一画面";
        diagnostics.Content=null;diagnosticFailure=false;
    }
    public async Task SeekAsync(double seconds,CancellationToken cancellationToken=default)
    {
        await session.SeekAsync(seconds,cancellationToken);
        await AwaitPresentationAsync();
    }
    public async Task PauseForEditingAsync()
    { session.Pause(); await session.SynchronizeAsync(); await AwaitPresentationAsync(); }
    public async Task PreviewStagePositionAsync(string actor,int slot,System.Numerics.Vector3 position,long expectedSession,int expectedFrame)
    {
        await session.PreviewStagePositionAsync(actor,slot,position,expectedSession,expectedFrame);
        await AwaitPresentationAsync();
    }
    public async Task ApplySceneEditsAsync(IReadOnlyList<JsonObject> rows)
    {
        await session.ApplySceneEditsAsync(rows);
        await AwaitPresentationAsync();
    }
    public async Task SetHomeEditStateAsync(HomeEditState state)
    {
        await session.SetHomeEditStateAsync(state);
        await AwaitPresentationAsync();
    }
    public async Task CommitSceneProjectAsync(ScenePreviewSource source){await session.CommitSceneProjectAsync(source with{GpuPresentation=true});ConfigureAudio(source);}
    public Task<NativePlaybackTiming> ResolveSceneTimingAsync(CancellationToken cancellationToken=default)=>session.ResolveSceneTimingAsync(cancellationToken);
    async Task AwaitPresentationAsync()
    {
        if(session.LastFrame is {} rendered)await WaitForFrameAsync(rendered.Sequence-1);
    }
    public void Play(double? exclusiveEndSeconds=null)=>session.Play(exclusiveEndSeconds);
    public void Pause()=>session.Pause();

    /// <summary>Completes only after the raw native pixels have been copied into the XAML image.</summary>
    public async Task<PreviewFrame> WaitForFrameAsync(long afterSequence=0,TimeSpan? timeout=null)
    {
        var completion=new TaskCompletionSource<PreviewFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock(frameGate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            if(lastFrame is {} current&&current.Sequence>afterSequence)return current;
            waiters.Add((afterSequence,completion));
        }
        try{return await completion.Task.WaitAsync(timeout??TimeSpan.FromSeconds(30));}
        finally{lock(frameGate)waiters.RemoveAll(waiter=>ReferenceEquals(waiter.Completion,completion));}
    }

    void OnFrameReady(object? sender,PreviewFrame frame)
    {
        lock(frameGate)
        {
            if(disposed||showingEmptyCanvas)return;
            pendingFrame=frame;
            if(presentationQueued)return;
            presentationQueued=true;
        }
        if(!DispatcherQueue.TryEnqueue(PresentLatest))
            lock(frameGate){presentationQueued=false;pendingFrame=null;}
    }

    void PresentLatest()
    {
        PreviewFrame? frame;
        lock(frameGate)
        {
            frame=pendingFrame;pendingFrame=null;presentationQueued=false;
            if(disposed||frame is null)return;
        }
        try
        {
            long stamp=Stopwatch.GetTimestamp();
            var pixels=frame.Pixels;
            if(frame.GpuSurface is not null)
            {
                if(!PresentGpu(frame))return;
            }
            else
            {
            DetachGpu();
            if(pixels.Rgba.Length!=checked(pixels.Width*pixels.Height*4))throw new InvalidDataException("Incomplete CPU preview frame");
            if(bitmap is null||bitmap.PixelWidth!=pixels.Width||bitmap.PixelHeight!=pixels.Height)
            {
                pixelStream?.Dispose();bitmap=new(pixels.Width,pixels.Height);
                pixelStream=bitmap.PixelBuffer.AsStream();bgra=new byte[pixels.Rgba.Length];
            }
            if(!ReferenceEquals(image.Source,bitmap))image.Source=bitmap;
            // Metadata-only progress/pause events retain the same pixel object.
            // Copy only a newly rendered image, using the vectorized display converter.
            if(!ReferenceEquals(LastFrame?.Pixels,pixels))
            {
                PreviewPixelConverter.CopyBgraPremultiplied(pixels.Rgba,bgra);
                pixelStream!.Position=0;pixelStream.Write(bgra);bitmap.Invalidate();
            }
            }
            Volatile.Write(ref lastFrame,frame);
            if(HasCurrentSource)UpdateStatus();
            LayoutCanvas();
            lock(frameGate)
            {
                for(int i=waiters.Count-1;i>=0;i--)if(frame.Sequence>waiters[i].After)
                {waiters[i].Completion.TrySetResult(frame);waiters.RemoveAt(i);}
            }
            PresentationMilliseconds=Stopwatch.GetElapsedTime(stamp).TotalMilliseconds;
            FramePresented?.Invoke(this,frame);
        }
        catch(Exception exception){PresentFailure(exception);}
    }

    void OnFailed(object? sender,Exception exception)
    {
        DispatcherQueue.TryEnqueue(()=>{if(!disposed)PresentFailure(exception);});
    }
    void PresentFailure(Exception exception)
    {
        if(disposed)return;
        status.Text=HasCurrentSource?"画面暂时无法显示":LastFrame is null?"载入失败":"载入失败，保留上一画面";
        diagnostics.Content=exception.Message;diagnosticFailure=true;
        lock(frameGate)
        {
            foreach(var waiter in waiters)waiter.Completion.TrySetException(exception);
            waiters.Clear();
        }
        Failed?.Invoke(this,exception);
    }

    void UpdateStatus()
    {
        if(!HasCurrentSource||loading||LastFrame is not {} frame)return;
        if(frame.IsPlaying==statusPlaying&&Stopwatch.GetElapsedTime(statusClock).TotalMilliseconds<100)return;
        statusClock=Stopwatch.GetTimestamp();statusPlaying=frame.IsPlaying;diagnosticFailure=false;
        string mode=frame.IsGpuPresented?"GPU 直连":frame.PresentationFallback is not null?"兼容显示":"像素显示";
        string text=$"{mode}  ·  {(frame.IsPlaying?"播放中":"已暂停")}  ·  {TimeSpan.FromSeconds(frame.Time):mm\\:ss\\.ff}";
        if(status.Text!=text)status.Text=text;
    }

    void LayoutCanvas()
    {
        if(surface.ActualWidth<=0||surface.ActualHeight<=0)return;
        int width=showingEmptyCanvas?emptyWidth:LastFrame?.Pixels.Width??emptyWidth;
        int height=showingEmptyCanvas?emptyHeight:LastFrame?.Pixels.Height??emptyHeight;
        double scale=Math.Min(surface.ActualWidth/width,surface.ActualHeight/height)*viewZoom;
        var rect=new Rect((surface.ActualWidth-width*scale)/2+viewPan.X,(surface.ActualHeight-height*scale)/2+viewPan.Y,
            width*scale,height*scale);
        LayoutGpu(rect);
        if(imageRect!=rect)
        {
            image.Width=rect.Width;image.Height=rect.Height;
            image.RenderTransform=new TranslateTransform{X=rect.X,Y=rect.Y};
            imageRect=rect;checker.Children.Clear();
            const int cell=24;
            var light=Brush(57,63,75);var dark=Brush(43,49,60);
            int firstX=Math.Max(0,(int)Math.Floor(-rect.X/cell)),firstY=Math.Max(0,(int)Math.Floor(-rect.Y/cell));
            int cols=(int)Math.Ceiling(Math.Min(rect.Width,surface.ActualWidth-rect.X)/cell),rows=(int)Math.Ceiling(Math.Min(rect.Height,surface.ActualHeight-rect.Y)/cell);
            for(int y=firstY;y<rows;y++)for(int x=firstX;x<cols;x++)
            {
                var tile=new Rectangle{Width=Math.Min(cell,rect.Width-x*cell),Height=Math.Min(cell,rect.Height-y*cell),Fill=(x+y)%2==0?light:dark};
                Canvas.SetLeft(tile,rect.X+x*cell);Canvas.SetTop(tile,rect.Y+y*cell);checker.Children.Add(tile);
            }
        }
        var clip=new Rect(0,0,surface.ActualWidth,surface.ActualHeight);
        if(selectionClip.Rect!=clip)selectionClip.Rect=clip;
        if(surfaceClip.Rect!=clip)surfaceClip.Rect=clip;
        DrawSelection();
    }

    void DrawSelection()
    {
        if(!HasCurrentSource){HideSelection();return;}
        var target=LastFrame?.Targets.LastOrDefault(target=>target.Id==selected);
        if(target is null||imageRect.Width<=0){HideSelection();return;}
        string label=TargetLabel?.Invoke(target)??target.Label;
        if(displayedTarget==target&&selectionRect==imageRect&&overlay.Visibility==Visibility.Visible&&selectionText.Text==label)return;
        displayedTarget=target;selectionRect=imageRect;overlay.Visibility=Visibility.Visible;
        double x=imageRect.X+target.X*imageRect.Width,y=imageRect.Y+target.Y*imageRect.Height;
        double width=target.Width*imageRect.Width,height=target.Height*imageRect.Height;
        outline.Width=width;outline.Height=height;
        Canvas.SetLeft(outline,x);Canvas.SetTop(outline,y);
        if(selectionText.Text!=label)selectionText.Text=label;
        Canvas.SetLeft(selectionLabel,Math.Clamp(x,0,Math.Max(0,surface.ActualWidth-80)));
        Canvas.SetTop(selectionLabel,Math.Max(0,y-26));
        for(int i=0;i<handles.Length;i++)
        {
            Canvas.SetLeft(handles[i],x+(i%2)*width-3.5);Canvas.SetTop(handles[i],y+(i/2)*height-3.5);
        }
    }
    void HideSelection(){overlay.Visibility=Visibility.Collapsed;displayedTarget=null;}

    public PreviewTarget? HitTest(double normalizedX,double normalizedY)=>!HasCurrentSource?null:LastFrame?.Targets.LastOrDefault(target=>
        normalizedX>=target.X&&normalizedX<=target.X+target.Width&&normalizedY>=target.Y&&normalizedY<=target.Y+target.Height);

    Point Normalize(Point point)=>new((point.X-imageRect.X)/imageRect.Width,(point.Y-imageRect.Y)/imageRect.Height);
    bool InCanvas(Point point)=>imageRect.Width>0&&imageRect.Height>0&&imageRect.Contains(point);

    void OnPointerPressed(object sender,PointerRoutedEventArgs args)
    {
        if(!HasCurrentSource)return;
        var point=args.GetCurrentPoint(surface);
        if(args.Pointer.PointerDeviceType==Microsoft.UI.Input.PointerDeviceType.Mouse&&!point.Properties.IsLeftButtonPressed)return;
        if(!InCanvas(point.Position))return;
        var normalized=Normalize(point.Position);var target=HitTest(normalized.X,normalized.Y);
        SelectedTargetId=target?.Id;
        if(target is null)return;
        Pause();dragTarget=target.Id;dragPointer=args.Pointer.PointerId;dragStart=dragLast=normalized;dragMoved=false;
        surface.CapturePointer(args.Pointer);args.Handled=true;
    }
    void OnPointerMoved(object sender,PointerRoutedEventArgs args)
    {
        if(!HasCurrentSource||dragTarget is null||args.Pointer.PointerId!=dragPointer||imageRect.Width<=0)return;
        dragLast=Normalize(args.GetCurrentPoint(surface).Position);
        if(Math.Abs(dragLast.X-dragStart.X)+Math.Abs(dragLast.Y-dragStart.Y)<.001&&!dragMoved)return;
        dragMoved=true;
        ObjectDragged?.Invoke(this,new(dragTarget,dragLast.X-dragStart.X,dragLast.Y-dragStart.Y,false));args.Handled=true;
    }
    void OnPointerReleased(object sender,PointerRoutedEventArgs args)
    {
        if(dragTarget is null||args.Pointer.PointerId!=dragPointer)return;
        dragLast=Normalize(args.GetCurrentPoint(surface).Position);FinishDrag();surface.ReleasePointerCapture(args.Pointer);args.Handled=true;
    }
    void OnPointerCanceled(object sender,PointerRoutedEventArgs args)
    {
        if(dragTarget is not null&&args.Pointer.PointerId==dragPointer)FinishDrag();
    }
    void FinishDrag()
    {
        string? target=dragTarget;dragTarget=null;
        if(target is not null&&dragMoved)ObjectDragged?.Invoke(this,new(target,dragLast.X-dragStart.X,dragLast.Y-dragStart.Y,true));
        dragMoved=false;
    }
    void OnPointerWheelChanged(object sender,PointerRoutedEventArgs args)
    {
        var point=args.GetCurrentPoint(surface);
        bool control=(Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)&Windows.UI.Core.CoreVirtualKeyStates.Down)!=0;
        if(!control){ZoomCanvas(point.Position,point.Properties.MouseWheelDelta);args.Handled=true;return;}
        if(!HasCurrentSource||!InCanvas(point.Position))return;
        var normalized=Normalize(point.Position);
        var target=HitTest(normalized.X,normalized.Y)??LastFrame?.Targets.LastOrDefault(target=>target.Id==selected);
        if(target is null||point.Properties.MouseWheelDelta==0)return;
        SelectedTargetId=target.Id;Pause();
        ObjectScaled?.Invoke(this,new(target.Id,Math.Pow(1.1,point.Properties.MouseWheelDelta/120d),true));args.Handled=true;
    }

    public async ValueTask DisposeAsync()
    {
        lock(frameGate)
        {
            if(disposed)return;
            disposed=true;pendingFrame=null;
            foreach(var waiter in waiters)waiter.Completion.TrySetException(new ObjectDisposedException(nameof(NativeViewport)));
            waiters.Clear();
        }
        session.FrameReady-=OnFrameReady;session.Failed-=OnFailed;
        DetachGpu();await session.DisposeAsync();await previewAudio.DisposeAsync();
        pixelStream?.Dispose();pixelStream=null;
    }
}
