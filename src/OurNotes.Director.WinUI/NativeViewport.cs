using System.Runtime.InteropServices.WindowsRuntime;
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
public sealed class NativeViewport : UserControl, IAsyncDisposable
{
    readonly RealtimePreviewSession session=new();
    readonly Grid surface=new(){Background=Brush(18,21,28)};
    readonly Image image=new(){Stretch=Stretch.Uniform,IsHitTestVisible=false};
    readonly Canvas checker=new(){IsHitTestVisible=false};
    readonly Canvas overlay=new(){IsHitTestVisible=false};
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
    public event EventHandler<PreviewFrame>? FramePresented;
    public event EventHandler<Exception>? Failed;
    public event EventHandler<string?>? SelectionChanged;
    public event EventHandler<PreviewDrag>? ObjectDragged;
    public event EventHandler<PreviewScale>? ObjectScaled;
    public PreviewFrame? LastFrame=>Volatile.Read(ref lastFrame);
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
        surface.Children.Add(checker);surface.Children.Add(image);surface.Children.Add(overlay);
        root.Children.Add(surface);
        var bar=new Border{Background=Brush(27,31,40),Padding=new(12,7,12,7),Child=status};
        Grid.SetRow(bar,1);root.Children.Add(bar);Content=root;
        AutomationProperties.SetName(this,"实时画布");
        AutomationProperties.SetHelpText(this,"点击角色选择，拖动改变位置，鼠标滚轮缩放。");
        surface.SizeChanged+=(_,_)=>LayoutCanvas();
        surface.PointerPressed+=OnPointerPressed;
        surface.PointerMoved+=OnPointerMoved;
        surface.PointerReleased+=OnPointerReleased;
        surface.PointerCanceled+=OnPointerCanceled;
        surface.PointerCaptureLost+=OnPointerCanceled;
        surface.PointerWheelChanged+=OnPointerWheelChanged;
        Unloaded+=(_,_)=>{if(!disposed)session.Pause();};
        session.FrameReady+=OnFrameReady;session.Failed+=OnFailed;
    }

    static SolidColorBrush Brush(byte r,byte g,byte b)=>new(Color.FromArgb(255,r,g,b));

    public async Task LoadSceneAsync(ScenePreviewSource source)
    {
        long version=BeginLoad("正在载入场景…");
        try
        {
            await session.LoadSceneAsync(source);await AwaitPresentationAsync();
            if(version==sourceVersion){HasCurrentSource=true;loading=false;UpdateStatus();DrawSelection();}
        }
        catch(Exception exception){if(version==sourceVersion){loading=false;PresentFailure(exception);}throw;}
    }
    public async Task LoadHomeAsync(HomePreviewSource source)
    {
        long version=BeginLoad("正在载入主页…");
        try
        {
            await session.LoadHomeAsync(source);await AwaitPresentationAsync();
            if(version==sourceVersion){HasCurrentSource=true;loading=false;UpdateStatus();DrawSelection();}
        }
        catch(Exception exception){if(version==sourceVersion){loading=false;PresentFailure(exception);}throw;}
    }
    long BeginLoad(string message)
    {
        HasCurrentSource=false;loading=true;dragTarget=null;dragMoved=false;
        surface.ReleasePointerCaptures();overlay.Children.Clear();status.Text=message;ToolTipService.SetToolTip(status,null);
        return ++sourceVersion;
    }
    /// <summary>
    /// Invalidates an old picture as soon as the editor switches documents, including
    /// cases where archive/dependency validation fails before a native load can begin.
    /// </summary>
    public void InvalidateSource()
    {
        session.Pause();HasCurrentSource=false;loading=false;sourceVersion++;
        dragTarget=null;dragMoved=false;surface.ReleasePointerCaptures();overlay.Children.Clear();
        status.Text=LastFrame is null?"打开项目，开始预览":"等待载入，保留上一画面";
        ToolTipService.SetToolTip(status,null);
    }
    public async Task SeekAsync(double seconds)
    {
        await session.SeekAsync(seconds);
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
    public Task CommitSceneProjectAsync(ScenePreviewSource source)=>session.CommitSceneProjectAsync(source);
    async Task AwaitPresentationAsync()
    {
        if(session.LastFrame is {} rendered)await WaitForFrameAsync(rendered.Sequence-1);
    }
    public void Play()=>session.Play();
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
            if(disposed)return;
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
            var pixels=frame.Pixels;
            if(bitmap is null||bitmap.PixelWidth!=pixels.Width||bitmap.PixelHeight!=pixels.Height)
            {
                pixelStream?.Dispose();bitmap=new(pixels.Width,pixels.Height);
                pixelStream=bitmap.PixelBuffer.AsStream();bgra=new byte[pixels.Rgba.Length];image.Source=bitmap;
            }
            // XAML composes premultiplied BGRA. The export/native buffer stays straight
            // RGBA; convert only this display copy, keeping its genuine alpha edges.
            for(int i=0;i<bgra.Length;i+=4)
            {
                int alpha=pixels.Rgba[i+3];
                bgra[i]=(byte)((pixels.Rgba[i+2]*alpha+127)/255);
                bgra[i+1]=(byte)((pixels.Rgba[i+1]*alpha+127)/255);
                bgra[i+2]=(byte)((pixels.Rgba[i]*alpha+127)/255);
                bgra[i+3]=(byte)alpha;
            }
            pixelStream!.Position=0;pixelStream.Write(bgra);bitmap.Invalidate();
            Volatile.Write(ref lastFrame,frame);
            if(HasCurrentSource)UpdateStatus();
            LayoutCanvas();
            lock(frameGate)
            {
                foreach(var waiter in waiters.Where(waiter=>frame.Sequence>waiter.After).ToArray())
                {waiter.Completion.TrySetResult(frame);waiters.Remove(waiter);}
            }
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
        ToolTipService.SetToolTip(status,exception.Message);
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
        status.Text=$"{(frame.IsPlaying?"播放中":"已暂停")}  ·  {TimeSpan.FromSeconds(frame.Time):mm\\:ss\\.ff}";
        ToolTipService.SetToolTip(status,$"实际刷新 {frame.FramesPerSecond:0.0} fps · {frame.RenderMilliseconds:0} ms\n{frame.Pixels.Width} × {frame.Pixels.Height} · 第 {frame.Frame} 帧\n{frame.Device}");
    }

    void LayoutCanvas()
    {
        if(LastFrame is not {} frame||surface.ActualWidth<=0||surface.ActualHeight<=0)return;
        double scale=Math.Min(surface.ActualWidth/frame.Pixels.Width,surface.ActualHeight/frame.Pixels.Height);
        var rect=new Rect((surface.ActualWidth-frame.Pixels.Width*scale)/2,(surface.ActualHeight-frame.Pixels.Height*scale)/2,
            frame.Pixels.Width*scale,frame.Pixels.Height*scale);
        if(imageRect!=rect)
        {
            imageRect=rect;checker.Children.Clear();
            const int cell=24;
            var light=Brush(57,63,75);var dark=Brush(43,49,60);
            int cols=(int)Math.Ceiling(rect.Width/cell),rows=(int)Math.Ceiling(rect.Height/cell);
            for(int y=0;y<rows;y++)for(int x=0;x<cols;x++)
            {
                var tile=new Rectangle{Width=Math.Min(cell,rect.Width-x*cell),Height=Math.Min(cell,rect.Height-y*cell),Fill=(x+y)%2==0?light:dark};
                Canvas.SetLeft(tile,rect.X+x*cell);Canvas.SetTop(tile,rect.Y+y*cell);checker.Children.Add(tile);
            }
        }
        overlay.Clip=new RectangleGeometry{Rect=new(0,0,surface.ActualWidth,surface.ActualHeight)};
        DrawSelection();
    }

    void DrawSelection()
    {
        overlay.Children.Clear();
        if(!HasCurrentSource)return;
        var target=LastFrame?.Targets.LastOrDefault(target=>target.Id==selected);
        if(target is null||imageRect.Width<=0)return;
        double x=imageRect.X+target.X*imageRect.Width,y=imageRect.Y+target.Y*imageRect.Height;
        double width=target.Width*imageRect.Width,height=target.Height*imageRect.Height;
        var outline=new Rectangle{Width=width,Height=height,Stroke=Brush(91,202,255),StrokeThickness=2,RadiusX=3,RadiusY=3};
        Canvas.SetLeft(outline,x);Canvas.SetTop(outline,y);overlay.Children.Add(outline);
        var label=new Border{Background=Brush(18,66,92),CornerRadius=new(3),Padding=new(7,3,7,3),
            Child=new TextBlock{Text=target.Label,FontSize=12,Foreground=Brush(231,247,255)}};
        Canvas.SetLeft(label,Math.Clamp(x,0,Math.Max(0,surface.ActualWidth-80)));
        Canvas.SetTop(label,Math.Max(0,y-26));overlay.Children.Add(label);
        foreach(var corner in new[]{new Point(x,y),new Point(x+width,y),new Point(x,y+height),new Point(x+width,y+height)})
        {
            var handle=new Rectangle{Width=7,Height=7,Fill=Brush(217,243,255),Stroke=Brush(24,83,109),StrokeThickness=1};
            Canvas.SetLeft(handle,corner.X-3.5);Canvas.SetTop(handle,corner.Y-3.5);overlay.Children.Add(handle);
        }
    }

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
        if(!HasCurrentSource)return;
        var point=args.GetCurrentPoint(surface);
        if(!InCanvas(point.Position))return;
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
        await session.DisposeAsync();
        pixelStream?.Dispose();pixelStream=null;
    }
}
