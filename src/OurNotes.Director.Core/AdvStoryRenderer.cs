using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

// Host for the native game-command player. A script is an episode command array, not a new scripting language.
public sealed class AdvStoryRenderer : IDisposable
{
    public AdvLoop Loop{get;}=new();
    public AdvPlayback Player{get;}
    public AdvScene Scene{get;}
    public AdvAudio Audio{get;}
    public AdvUi Ui{get;}
    public AdvFrames Frames{get;}
    public AdvStills Stills{get;}
    public AdvVideo Video{get;}
    public string Device=>gl.Device;
    public bool PreferGpuPresentation { get => gl.PreferGpuPresentation; set => gl.PreferGpuPresentation = value; }
    public bool SegmentedCpuFrames { get => gl.SegmentedCpuFrames; set => gl.SegmentedCpuFrames = value; }
    public GpuPreviewSurface? GpuSurface => gl.GpuSurface;
    public string? GpuFallbackReason => gl.GpuFallbackReason;
    internal bool CapturePreviewPixels { get=>gl.CapturePreviewPixels; set=>gl.CapturePreviewPixels=value; }
    internal Pixels? CapturedPreviewPixels=>gl.CapturedPreviewPixels;
    internal double PreviewCaptureMilliseconds=>gl.PreviewCaptureMilliseconds;
    internal double PreviewCaptureScale{get=>gl.PreviewCaptureScale;set=>gl.PreviewCaptureScale=value;}
    public void DisableGpuPresentation(string reason) => gl.DisableGpuPresentation(reason);

    readonly OffscreenGl gl;
    int outputWidth,outputHeight,supersample;
    int viewportY,viewportHeight;
    readonly int referenceOutputHeight,referenceViewportHeight;
    OffscreenGl.DrawingCanvas? previewCanvas;
    OffscreenGl.DrawingCanvas? interactiveSceneCanvas;
    OffscreenGl.DrawingCanvas? historyCanvas;
    internal bool CanCaptureHistory=>isolateActor is null&&!charactersOnly;
    // Media post-processing updates its camera history only when drawn. An extra
    // small draw could change that history; copying its final FBO is still safe.
    internal bool CanDrawHistory=>CanCaptureHistory&&!Video.Visible&&!Stills.Visible;
    /// <summary>Transient scene-only preview scale. Text/UI, native timing, captures,
    /// hit testing and the exported project keep their reference dimensions.</summary>
    public double ScenePreviewScale { get; private set; } = 1;
    public void SetScenePreviewScale(double scale)
    {
        if (scale is not (1 or .5 or .25)) throw new ArgumentOutOfRangeException(nameof(scale));
        if (isolateActor is not null && scale != 1)
            throw new InvalidOperationException("Isolated models use their own adaptive presentation canvas.");
        if (ScenePreviewScale == scale) return;
        var next = scale == 1 ? null : gl.CreateDrawingCanvas(
            Math.Max(16, (int)Math.Round(outputWidth * supersample * scale)), Math.Max(16, (int)Math.Round(viewportHeight * supersample * scale)));
        interactiveSceneCanvas?.Dispose(); interactiveSceneCanvas = next;
        ScenePreviewScale = scale;
    }
    readonly string? isolateActor;
    IsolatedActorTransform isolateTransform;
    readonly IsolatedActorCanvas? isolateCanvas;
    public long PresentationCompositePasses=>gl.PresentationCompositePasses;
    public IsolatedActorTransform IsolationTransform => isolateTransform;
    public void SetIsolatedTransform(IsolatedActorTransform transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        if(isolateActor is null)throw new InvalidOperationException("Load an isolated actor before editing its presentation.");
        isolateTransform=ActorIsolation.ReadTransform(ActorIsolation.TransformJson(transform),isolateActor);
    }
    /// <summary>Changes only the presentation FBO. The ADV player,
    /// Cubism model, masks, captures and original UI coordinate system stay alive.</summary>
    public void ResizePreview(int width,int height,int sampling=1)
    {
        RenderDimensions.Validate(width,height,sampling);
        if(PendingFrames>0)throw new InvalidOperationException("Read queued exports before resizing a preview.");
        if(outputWidth==width&&outputHeight==height&&supersample==sampling)return;
        int nextViewportHeight=Math.Clamp((int)Math.Round(height*(referenceViewportHeight/(double)referenceOutputHeight)),1,height);
        int sampledWidth=checked(width*sampling),sampledHeight=checked(nextViewportHeight*sampling);
        var next=sampledWidth==gl.Width&&sampledHeight==gl.Height?null:gl.CreateDrawingCanvas(sampledWidth,sampledHeight);
        OffscreenGl.DrawingCanvas? nextInteractive=null;
        try
        {
            if(ScenePreviewScale!=1)nextInteractive=gl.CreateDrawingCanvas(
                Math.Max(16,(int)Math.Round(sampledWidth*ScenePreviewScale)),Math.Max(16,(int)Math.Round(sampledHeight*ScenePreviewScale)));
        }
        catch{next?.Dispose();throw;}
        Scene.ReleasePresentationTargets();
        historyCanvas?.Dispose();historyCanvas=null;
        previewCanvas?.Dispose();previewCanvas=next;
        interactiveSceneCanvas?.Dispose();interactiveSceneCanvas=nextInteractive;
        outputWidth=width;outputHeight=height;supersample=sampling;
        viewportHeight=nextViewportHeight;viewportY=(int)MathF.Floor((height-viewportHeight)/2f+.5f);
    }
    readonly bool charactersOnly;
    bool TransparentOutput => charactersOnly || isolateActor is not null || Scene.DirectorTransparent;
    readonly Dictionary<string,double> milliseconds=[];
    readonly Dictionary<string,double> initializationMilliseconds=[];
    readonly StoryAudioPrefetch? timingAudioPrefetch;
    T Measure<T>(string name,Func<T> action){long start=Stopwatch.GetTimestamp();try{return action();}finally{milliseconds[name]=milliseconds.GetValueOrDefault(name)+Stopwatch.GetElapsedTime(start).TotalMilliseconds;}}
    void Measure(string name,Action action)=>Measure(name,()=>{action();return 0;});
    public IReadOnlyDictionary<string,double> Timings=>milliseconds;
    public IReadOnlyDictionary<string,double> InitializationTimings=>initializationMilliseconds;
    internal Func<Func<long,CancellationToken,long,AdvCue>>? PrefetchAudio { get; }
    public AdvStoryRenderer(Archive archive,string site,string dll,string id,string language,int width,int height,string? script,string ffmpeg,int supersample=2,
        JsonNode? projectDocument=null,bool timingOnly=false,AdvCueCache? cueCache=null,CancellationToken cancellationToken=default,
        bool prefetchAudio=false,CancellationToken initializationCancellationToken=default)
    {
        using var initializationCancellation=initializationCancellationToken.CanBeCanceled
            ?CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,initializationCancellationToken):null;
        var constructorToken=initializationCancellation?.Token??cancellationToken;
        long phase=Stopwatch.GetTimestamp();
        void Mark(string name){initializationMilliseconds[name]=Stopwatch.GetElapsedTime(phase).TotalMilliseconds;phase=Stopwatch.GetTimestamp();}
        constructorToken.ThrowIfCancellationRequested();
        RenderDimensions.Validate(width,height,supersample);
        outputWidth=width;outputHeight=height;this.supersample=supersample;
        float target=width>height?2.1666667f:1.7777778f;int viewportHeight=height;
        var supplied=projectDocument??(script is null?null:DirectorProject.Document(script));
        var sourceOutput = (supplied as JsonObject)?["output"] as JsonObject;
        isolateActor = sourceOutput?["isolateActor"]?.ToString();
        isolateTransform = ActorIsolation.ReadTransform(sourceOutput?["isolateTransform"],isolateActor);
        isolateCanvas = ActorIsolation.ReadCanvas(sourceOutput?["isolateCanvas"],isolateActor);
        charactersOnly = SceneContent.Read(sourceOutput?["content"]) == "characters";
        var playbackViewport = sourceOutput?["playbackViewport"];
        ActorIsolation.ValidateViewport(playbackViewport);
        bool noLetterbox=isolateActor is not null || supplied is JsonObject projectDoc&&Archive.S(projectDoc,"format")==DirectorProject.Format&&Archive.S(projectDoc["output"]??new JsonObject(),"letterbox")=="none";
        if(!noLetterbox&&width/(float)height+.0001f<target&&(height-width/target)/2>1)viewportHeight=(int)MathF.Floor(width/target+.5f);
        viewportY=(int)MathF.Floor((height-viewportHeight)/2f+.5f);
        this.viewportHeight=referenceViewportHeight=viewportHeight;referenceOutputHeight=height;
        var manifest=archive.Manifest(site,"stories",id);var definition=JsonNode.Parse(archive.Logical(site,manifest,"story.json"))!;
        var episode=JsonNode.Parse(archive.Logical(site,manifest,Archive.S(definition,"episode")))!;
        var scene=JsonNode.Parse(archive.Logical(site,manifest,Archive.S(definition,"scene")))!;var settings=scene["settings"]!["playerSettings"]!.DeepClone();
        if(id==DirectorProject.LibrarySource)
        {
            // The library pack provides materials, never a source episode's performance.
            episode["commands"]=new JsonArray(); episode["text"]=new JsonObject();
            settings["_initializeEpisodes"]=new JsonArray(); settings["_finalizeEpisodes"]=new JsonArray();
        }
        if(supplied is not null){var custom=supplied;bool project=custom is JsonObject&&Archive.S(custom,"format")==DirectorProject.Format;
            if(project){if(projectDocument is null)_=DirectorProject.Read(script!);if(DirectorProject.SourceId(custom["source"])!=id)throw new ArgumentException("Project source does not match story ID");}
            var commands=custom is JsonArray?custom:custom["commands"];
            if(commands is not null)episode["commands"]=commands.DeepClone();else if(!project)throw new ArgumentException("Script needs a commands array");
            if(custom is JsonObject){if(custom["text"] is JsonObject text)foreach(var pair in text)episode["text"]![pair.Key]=pair.Value?.DeepClone();if(custom["initialize"] is JsonArray init)settings["_initializeEpisodes"]=init.DeepClone();if(custom["finalize"] is JsonArray fin)settings["_finalizeEpisodes"]=fin.DeepClone();}
        }
        var program=AdvProgram.From(episode,settings);Player=new(Loop,program);var tweens=new AdvTweens(Loop);
        Mark("sourceAndProgramMs");
        constructorToken.ThrowIfCancellationRequested();
        gl=new(checked(width*supersample),checked(viewportHeight*supersample));
        Mark("graphicsContextMs");
        try{
            constructorToken.ThrowIfCancellationRequested();
            // Start the same bounded independent decoders before native model
            // setup. Their immutable source snapshots never touch playback's
            // lazy JsonNodes, and their lifetime includes constructor failures.
            if(timingOnly)
            {
                PrefetchAudio=AdvAudio.IndependentDecoder(archive,site,manifest,episode,ffmpeg);
                if(prefetchAudio)
                {
                    var voices=StoryAudioPrefetch.VoiceCandidates(program).ToArray();
                    if(voices.Length>0)timingAudioPrefetch=new(cueCache??=new(),voices,PrefetchAudio,cancellationToken);
                }
            }
            Mark("audioPrefetchSetupMs");
            Scene=new(archive,site,dll,manifest,definition,scene,language,gl,Loop,tweens){TimingOnly=timingOnly};Scene.Bind(Player);
            if(script is not null)Scene.ProjectDirectory=Path.GetDirectoryName(Path.GetFullPath(script))!;
            var rows=program.Initialize.Concat(program.Rows).Concat(program.Finalize).Where(r=>!r.Ignored).ToArray();
            foreach(var row in rows)DirectorValidation.Check(row);
            Audio=new(Loop,[]){ResolveCue=n=>timingAudioPrefetch?.Get(n)??cueCache?.Get(n,()=>AdvAudio.Decode(archive,site,manifest,episode,n,ffmpeg,cancellationToken))??AdvAudio.Decode(archive,site,manifest,episode,n,ffmpeg,cancellationToken)};Audio.Bind(Player);
            Ui=new(archive,site,manifest,definition,episode,settings,language,gl,Loop,Audio,supersample){HasLetterbox=playbackViewport is null ? viewportHeight<height : ActorIsolation.HasLetterbox(
                playbackViewport["width"]!.GetValue<int>(),playbackViewport["height"]!.GetValue<int>(),playbackViewport["letterbox"]!.ToString()),MasterIds=scene["settings"]?["masterIdSettings"],TimingOnly=timingOnly};Ui.Bind(Player);Ui.CheckTexts(rows);
            Ui.AudioProjectDirectory=script is null?Environment.CurrentDirectory:Path.GetDirectoryName(Path.GetFullPath(script))!;Ui.AudioFfmpegPath=ffmpeg;
            Ui.ResolveCharacter=Scene.Character;
            Ui.ShowingCharacters=()=>Scene.VisibleActors;
            Scene.ForegroundItem=()=>Ui.Chat is {Visible:true} chat?new(chat.SortingOrder,chat.PlaneDistance*chat.PlaneDistance,()=>chat.Draw(Scene.Camera)):null;
            Frames=new(archive,site,manifest,definition,language,Loop,tweens,gl,supersample,timingOnly){Localize=Ui.Localize};Frames.Bind(Player);Frames.Preload(rows);
            Stills=new(archive,site,manifest,definition,language,Loop,tweens,gl,rows);Stills.Bind(Player);
            Video=new(archive,site,manifest,definition,settings,Loop,tweens,Audio,Ui,gl,ffmpeg){TimingOnly=timingOnly};Video.Bind(Player);
            AdvAlphaCommands.Bind(Player,Loop,Stills.SetAlpha,Video.SetAlpha);
            Scene.TalkShakeGet=()=>Ui.ShakeOffset;Scene.TalkShakeSet=v=>Ui.ShakeOffset=v;
            Scene.StillShakeGet=()=>Stills.ShakeOffset;Scene.StillShakeSet=v=>Stills.ShakeOffset=v;
            if(Player.MissingCommands.Length>0)throw new NotSupportedException("Native story commands not connected: "+string.Join(", ",Player.MissingCommands));
            Mark("sceneAndUiMs");
            constructorToken.ThrowIfCancellationRequested();
            var modelInitializationBefore=archive.ModelInitializationTimings.ToDictionary(pair=>pair.Key,pair=>pair.Value);
            Scene.Preload(rows,constructorToken);
            Mark("nativeModelsMs");
            initializationMilliseconds["focusMaterialsMs"]=gl.FocusPreparationMilliseconds;
            foreach(var (name,milliseconds) in archive.ModelInitializationTimings)
                initializationMilliseconds[name]=milliseconds-modelInitializationBefore.GetValueOrDefault(name);
            constructorToken.ThrowIfCancellationRequested();
            foreach(var row in rows.Where(r=>r.Command=="Director.Parameter"))_=(Scene.Character(row.Text("TargetName"))??throw new ArgumentException("Unknown actor: "+row.Text("TargetName"))).ParameterValue(row.Text("Parameter"));
            Player.Prepare=p=>Ui.Prepare();Player.Start();
            constructorToken.ThrowIfCancellationRequested();
            Mark("startPlaybackMs");
        }catch{timingAudioPrefetch?.Dispose();Video?.Dispose();Stills?.Dispose();Frames?.Dispose();Ui?.Dispose();Scene?.Dispose();gl.Dispose();throw;}
    }
    public Pixels Frame(int frame,bool clean=false,IReadOnlyList<JsonObject>? previewEdits=null,bool stopAtEnd=false,CancellationToken cancellationToken=default)
        =>FrameWithHistory(frame,clean,previewEdits,stopAtEnd,cancellationToken,null);

    internal Pixels FrameWithHistory(int frame,bool clean,IReadOnlyList<JsonObject>? previewEdits,bool stopAtEnd,
        CancellationToken cancellationToken,Action<int>? capture)
    {
        AdvanceFrame(frame,previewEdits,stopAtEnd,cancellationToken,capture);
        Pixels pixels;
        using(gl.UseDrawingCanvas(previewCanvas))
        {
            Draw(clean);
            pixels=Measure("readbackAndResolve",()=>gl.Present(outputWidth,outputHeight,supersample,viewportY,DrawOutputLetterbox,TransparentOutput,isolateActor is not null));
        }
        Loop.Drain();Player.ThrowIfFailed();return pixels;
    }

    /// <summary>Draws the actual current pose into a small presentation canvas.
    /// Simulation, stage captures and native coordinates remain on the reference
    /// canvas. This method never advances or drains the interpreter.</summary>
    internal PreviewFrameCache.Frame CaptureHistoryFrame(bool clean)
    {
        if(!CanDrawHistory)throw new InvalidOperationException("Extra history draws require a complete ADV scene without active media.");
        int width=Math.Max(16,(outputWidth+3)/4),height=Math.Max(16,(outputHeight+3)/4);
        int cropped=Math.Clamp((int)Math.Round(height*(referenceViewportHeight/(double)referenceOutputHeight)),1,height);
        historyCanvas??=gl.CreateDrawingCanvas(width,cropped);
        int oldWidth=outputWidth,oldHeight=outputHeight,oldSample=supersample,oldY=viewportY,oldViewport=viewportHeight;
        var oldCanvas=previewCanvas;var oldInteractive=interactiveSceneCanvas;
        bool gpu=gl.PreferGpuPresentation,capture=gl.CapturePreviewPixels;
        try
        {
            outputWidth=width;outputHeight=height;supersample=1;viewportHeight=cropped;
            viewportY=(int)MathF.Floor((height-cropped)/2f+.5f);previewCanvas=historyCanvas;interactiveSceneCanvas=null;
            gl.PreferGpuPresentation=false;gl.CapturePreviewPixels=false;
            Pixels pixels;
            using(gl.UseDrawingCanvas(historyCanvas))
            {
                Draw(clean);
                pixels=Measure("readbackAndResolve",()=>gl.Present(width,height,1,viewportY,DrawOutputLetterbox,TransparentOutput));
            }
            return new(Loop.Frame,pixels,.25,PreviewTargets(),Scene.PreviewLipSyncState(),Scene.PreviewAnimationState());
        }
        finally
        {
            outputWidth=oldWidth;outputHeight=oldHeight;supersample=oldSample;viewportY=oldY;viewportHeight=oldViewport;
            previewCanvas=oldCanvas;interactiveSceneCanvas=oldInteractive;gl.PreferGpuPresentation=gpu;gl.CapturePreviewPixels=capture;
        }
    }
    public IReadOnlyList<PreviewTarget> PreviewTargets()
    {
        if(isolateActor is not null)return Scene.IsolatedPreviewTarget is {} isolated?[isolated]:[];
        using var canvas=gl.UseDrawingCanvas(previewCanvas);
        double top=viewportY/(double)outputHeight,height=viewportHeight/(double)outputHeight;
        return Scene.PreviewTargets().Select(target=>target with{Y=top+target.Y*height,Height=target.Height*height,StagePlacement=target.StagePlacement is {} plane?plane with{ViewportHeight=height}:null}).ToArray();
    }
    void AdvanceFrame(int frame,IReadOnlyList<JsonObject>? previewEdits=null,bool stopAtEnd=false,CancellationToken cancellationToken=default,
        Action<int>? capture=null)
    {
        if(frame<Loop.Frame)throw new ArgumentException("Frame requests must be increasing");
        double captureMilliseconds=0;
        try
        {
            Measure("simulation",()=>
            {
                while(Loop.Frame<frame&&(!stopAtEnd||!Player.Finished))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Scene.AdvanceRenderState();Frames.AdvanceRenderState();Ui.Chat?.AdvanceRenderState();Loop.Drain();
                    // Pending Stage captures have already consumed their original
                    // render phase. Optional image history cannot resolve them early.
                    if(capture is not null)
                    {
                        long started=Stopwatch.GetTimestamp();
                        try{capture(Loop.Frame);}finally{captureMilliseconds+=Stopwatch.GetElapsedTime(started).TotalMilliseconds;}
                    }
                    Player.Step();
                }
            });
        }
        finally{milliseconds["simulation"]=milliseconds.GetValueOrDefault("simulation")-captureMilliseconds;}
        cancellationToken.ThrowIfCancellationRequested();
        if(previewEdits is {Count:>0})Scene.ApplyPreviewEdits(previewEdits,Player);
    }
    /// <summary>Runs every preceding native step, then reads current model geometry
    /// in the original isolation canvas. No final scene draw or pixel readback occurs.</summary>
    public PreviewTarget? MeasureIsolatedBounds(int frame,int referenceWidth,int referenceHeight,CancellationToken cancellationToken=default)
    {
        if(isolateActor is null)throw new InvalidOperationException("Load an isolated actor before measuring its bounds.");
        RenderDimensions.Validate(referenceWidth,referenceHeight,1);
        AdvanceFrame(frame,cancellationToken:cancellationToken);
        var result=Scene.MeasureIsolatedActorBounds(isolateActor,isolateTransform,referenceWidth,referenceHeight);
        Loop.Drain();Player.ThrowIfFailed();return result;
    }
    /// <summary>
    /// Runs the same handlers and resource-based waits as export, without drawing or
    /// reading every frame. Render-dependent capture waits still use AdvanceRenderState.
    /// </summary>
    public NativePlaybackTiming AnalyzeTiming(string projectHash,Action<NativePlaybackTiming>? progress=null,
        CancellationToken cancellationToken=default,int maximumFrames=30*36000)
    {
        if(maximumFrames<1||maximumFrames>30*36000)throw new ArgumentOutOfRangeException(nameof(maximumFrames));
        long reported=Stopwatch.GetTimestamp();
        while(!Player.Finished&&Loop.Frame<maximumFrames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Scene.AdvanceRenderState();Frames.AdvanceRenderState();Ui.Chat?.AdvanceRenderState();Loop.Drain();Player.Step();
            if(Stopwatch.GetElapsedTime(reported).TotalMilliseconds>=250)
            {
                progress?.Invoke(new(projectHash,false,null,null,[],Loop.Frame));
                reported=Stopwatch.GetTimestamp();
            }
        }
        Loop.Drain();Player.ThrowIfFailed();
        int? end=Player.Finished&&!Player.Cancelled?Player.CompletionFrame:null;
        return new(projectHash,end.HasValue,end,end.HasValue?(end.Value+1)/30d:null,Player.CommandTimings(),Loop.Frame,
            end.HasValue?null:"剧情未在十小时分析上限内结束，请检查循环指令或使用自由时长。");
    }
    public int PendingFrames=>gl.PendingPixels;
    public void QueueFrame(int frame,bool clean=false)
    {
        if(PendingFrames>=3)throw new InvalidOperationException("Read a queued frame before advancing the story");
        AdvanceFrame(frame);
        using(gl.UseDrawingCanvas(previewCanvas))
        {
            Draw(clean);
            Measure("readbackAndResolve",()=>gl.QueuePresent(outputWidth,outputHeight,supersample,viewportY,DrawOutputLetterbox,TransparentOutput,isolateActor is not null));
        }
        Loop.Drain();Player.ThrowIfFailed();
    }
    public Pixels ReadQueuedFrame()=>Measure("readbackAndResolve",gl.ReadQueued);
    void DrawOutputLetterbox(int width,int height,int viewportHeight){if(!charactersOnly)Ui.DrawLetterbox(width,height,viewportHeight);}
    void Draw(bool clean){if(isolateActor is not null){Measure("isolatedActor",()=>Scene.DrawIsolatedActor(isolateActor,isolateTransform,isolateCanvas));return;}if(charactersOnly){Measure("characters",Scene.DrawCharacters);Scene.ApplyDirectorGrade();return;}Measure("scene",DrawScenePreview);if(Video.Visible||Stills.Visible)Measure("media",()=>Scene.DrawMedia(()=>{Video.Draw();Stills.Draw();},Stills.PostEffectSuppressed));Measure("frames",Frames.Draw);Measure("ui",()=>Ui.Draw(clean));if(!Scene.DirectorTransparent)Measure("finalPost",()=>Scene.FinalPost());Scene.ApplyDirectorGrade();}
    void DrawScenePreview()
    {
        if (interactiveSceneCanvas is null) { Scene.Draw(); return; }
        // Scale only the expensive 3D/background/DoF pass. Simulation (including
        // render-dependent capture commands) is evaluated on the reference canvas.
        // Preserve linear HDR until the ordinary final colour/alpha resolve.
        OffscreenGl.GlSurface reduced;
        using (gl.UseDrawingCanvas(interactiveSceneCanvas))
        {
            Scene.Draw(); reduced = gl.CurrentSurface;
        }
        gl.CopySurface(reduced, gl.CurrentSurface, linear: true);
    }
    public void Dispose(){timingAudioPrefetch?.Dispose();historyCanvas?.Dispose();interactiveSceneCanvas?.Dispose();Video.Dispose();Stills.Dispose();Frames.Dispose();Ui.Dispose();Scene.Dispose();previewCanvas?.Dispose();gl.Dispose();}
    public static object Contact(Archive archive,string site,string dll,string id,string language,string? script,string output,int width,int height,double[] times,int columns,bool clean,bool overwrite,string ffmpeg,int supersample=2)
    {
        if(times.Length is <1 or >64||times.Any(t=>!double.IsFinite(t)||t<0||t>36000))throw new ArgumentException("Contact needs 1..64 times within ten hours");
        if(columns<1||columns>16)throw new ArgumentException("Contact columns must be 1..16");
        int cols=Math.Min(columns,times.Length),rows=(times.Length+cols-1)/cols;
        if(width<16||height<16||(long)width*height*cols*rows>64000000)throw new ArgumentException("Contact dimensions exceed 64 megapixels");
        int outWidth=checked(width*cols),outHeight=checked(height*rows);
        output=Path.GetFullPath(output);if(File.Exists(output)&&!overwrite)throw new IOException("Output exists; use --overwrite");
        var wall=Stopwatch.StartNew();var pixels=new byte[checked(outWidth*outHeight*4)];var cells=times.Select((t,i)=>new ContactCell(i,(int)Math.Floor(t*30+1e-8),Math.Floor(t*30+1e-8)/30,i%cols*width,i/cols*height)).ToArray();
        using var renderer=new AdvStoryRenderer(archive,site,dll,id,language,width,height,script,ffmpeg,supersample);
        foreach(var group in cells.GroupBy(c=>c.Frame).OrderBy(g=>g.Key)){var image=renderer.Frame(group.Key,clean);foreach(var cell in group)for(int y=0;y<height;y++)Buffer.BlockCopy(image.Rgba,y*width*4,pixels,((cell.Y+y)*outWidth+cell.X)*4,width*4);}
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);string temp=output+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllBytes(temp,Png.Encode(new(outWidth,outHeight,pixels)));File.Move(temp,output,overwrite);}finally{if(File.Exists(temp))File.Delete(temp);}
        return JsonOutput.Object(("output", output), ("width", outWidth), ("height", outHeight), ("cells", cells), ("performance", JsonOutput.Object(("wallSeconds", wall.Elapsed.TotalSeconds), ("cpuSubmissionMilliseconds", renderer.Timings))));
    }
    public static object Run(Archive archive,string site,string dll,string id,string language,string? script,string mode,string output,int width,int height,double time,double duration,string codec,bool clean,bool overwrite,string ffmpeg,int supersample=2,bool untilEnd=false,double speed=1,string cineformQuality=VideoEncoding.DefaultCineFormQuality)
    {
        if(mode is not ("frame" or "clip"))throw new ArgumentException("Story output must be frame or clip");
        if(untilEnd&&mode!="clip")throw new ArgumentException("--until-end requires story clip");
        if(!double.IsFinite(speed)||speed<.25||speed>4)throw new ArgumentException("Export speed must be between 0.25 and 4");
        if(mode!="clip"&&speed!=1)throw new ArgumentException("--speed requires story clip");
        if(!double.IsFinite(time)||time<0||!double.IsFinite(duration)||duration<=0||time+duration>36000)throw new ArgumentException("Invalid time/duration; maximum ten hours");
        codec=VideoEncoding.NormalizeCodec(codec);
        if(mode=="clip"){VideoEncoding.Validate(codec,width,height,cineformQuality);VideoEncoding.ValidateOutputPath(codec,output);}
        output=Path.GetFullPath(output);if(File.Exists(output)&&!overwrite)throw new IOException("Output exists; use --overwrite");Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        string prefix=Path.Combine(Path.GetDirectoryName(output)!,".adv-"+Guid.NewGuid().ToString("N")),video=prefix+".video",audio=prefix+".f32",mux=prefix+".mux";
        Process? encoder=null;Task<string>? errors=null;int start=(int)Math.Floor(time*30+1e-8),count=Math.Max(1,Story.Frames(duration)),written=0;bool finished=false;var wall=Stopwatch.StartNew();double encodeMs=0,audioMs=0;
        try{
            using var renderer=new AdvStoryRenderer(archive,site,dll,id,language,width,height,script,ffmpeg,supersample);
            double initializationMs=wall.Elapsed.TotalMilliseconds;
            if(mode=="frame"){File.WriteAllBytes(mux,Png.Encode(renderer.Frame(start,clean)));File.Move(mux,output,overwrite);return JsonOutput.Object(("output", output), ("frame", start));}
            var info=VideoEncoding.RawVideoStartInfo(ffmpeg,codec,width,height,30*speed,video,cineformQuality);
            string format=VideoEncoding.Format(codec);
            encoder=Process.Start(info)??throw new IOException("Cannot start video encoder");errors=encoder.StandardError.ReadToEndAsync();
            void EncodeQueued(){var pixels=renderer.ReadQueuedFrame();long stamp=Stopwatch.GetTimestamp();encoder.StandardInput.BaseStream.Write(pixels.Rgba);encodeMs+=Stopwatch.GetElapsedTime(stamp).TotalMilliseconds;}
            var mixedPcm=new float[AdvAudio.SamplesPerFrame*2];
            using(var pcm=File.Create(audio))for(int i=0;i<count;i++){
                renderer.QueueFrame(start+i,clean);if(renderer.PendingFrames>=3)EncodeQueued();
                if(codec=="h264"&&renderer.TransparentOutput)throw new ArgumentException("Transparent scenes require --codec cineform, prores, or ffv1");
                long stamp=Stopwatch.GetTimestamp();renderer.Audio.MixFrame(mixedPcm);pcm.Write(MemoryMarshal.AsBytes(mixedPcm.AsSpan()));audioMs+=Stopwatch.GetElapsedTime(stamp).TotalMilliseconds;
                written++;finished=renderer.Player.Finished;
                if(i%30==0)Console.Error.WriteLine($"story render {i+1}/{count}");
                if(untilEnd&&finished)break;
            }
            while(renderer.PendingFrames>0)EncodeQueued();
            encoder.StandardInput.Close();encoder.WaitForExit();if(encoder.ExitCode!=0)throw VideoEncoding.EncoderFailure(encoder.ExitCode,errors.GetAwaiter().GetResult());
            if(untilEnd&&!finished)throw new InvalidOperationException("Story did not finish within --duration; increase the cap or inspect its command timeline. No incomplete video was published.");
            var merge=new ProcessStartInfo(ffmpeg){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true};
            var mergeArgs=new List<string>{"-v","error","-y","-i",video,"-f","f32le","-ar","48000","-ac","2","-i",audio,"-map","0:v:0","-map","1:a:0","-c:v","copy"};
            double outputDuration=written/(30*speed);
            if(speed!=1)mergeArgs.AddRange(["-af",Media.TempoFilter(speed)+",apad,atrim=duration="+outputDuration.ToString("R",CultureInfo.InvariantCulture)]);
            mergeArgs.AddRange(["-c:a",codec=="h264"?"aac":"pcm_s24le","-t",outputDuration.ToString("R",CultureInfo.InvariantCulture),"-f",format,mux]);
            foreach(var a in mergeArgs)merge.ArgumentList.Add(a);
            using var joined=Process.Start(merge)??throw new IOException("Cannot start audio mux");var err=joined.StandardError.ReadToEndAsync();joined.WaitForExit();if(joined.ExitCode!=0)throw new IOException(err.GetAwaiter().GetResult());File.Move(mux,output,overwrite);
            return JsonOutput.Object(("output", output), ("frames", written), ("width", width), ("height", height), ("supersample", supersample), ("fps", 30*speed), ("speed", speed), ("sourceDuration", written/30d), ("duration", outputDuration), ("audioHz", 48000), ("codec", codec), ("cineformQuality", codec=="cineform"?VideoEncoding.NormalizeCineFormQuality(cineformQuality):null), ("alpha", codec=="h264"?"none":"straight"), ("finished", finished), ("performance", JsonOutput.Object(("Device", renderer.Device), ("initializationMilliseconds", initializationMs), ("wallSeconds", wall.Elapsed.TotalSeconds), ("framesPerSecond", written/wall.Elapsed.TotalSeconds), ("cpuSubmissionMilliseconds", renderer.Timings), ("encoderWriteMilliseconds", encodeMs), ("audioMixMilliseconds", audioMs))));
        }catch(IOException error)when(encoder is not null&&encoder.HasExited&&encoder.ExitCode!=0)
        {throw VideoEncoding.EncoderFailure(encoder.ExitCode,errors?.GetAwaiter().GetResult()??"",error);}
        finally{VideoEncoding.StopEncoder(encoder,errors);foreach(var path in new[]{video,audio,mux})if(File.Exists(path))File.Delete(path);}
    }
}
