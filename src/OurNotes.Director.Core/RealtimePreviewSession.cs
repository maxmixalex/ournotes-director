using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

/// <summary>
/// A long-lived, thread-affine native renderer. All GL, animation and resource lifetime
/// operations stay on one dedicated thread. Only final raw frames cross to the UI.
/// </summary>
public sealed class RealtimePreviewSession : IAsyncDisposable
{
    readonly object gate=new();
    readonly Queue<Work> work=[];
    readonly AutoResetEvent wake=new(false);
    readonly Thread thread;
    readonly TaskCompletionSource stopped=new(TaskCreationOptions.RunContinuationsAsynchronously);
    Update? pendingUpdate;
    RendererState? renderer;
    PreviewFrame? lastFrame;
    bool closing,playing,resumeAudio;
    int playRequested;
    long sequence,sessionId;
    int frame;
    long playClock,rateClock;
    int playFrom,rateFrames;
    double measuredRate;
    TimingJob? timingJob;
    readonly Dictionary<string,NativePlaybackTiming> timingCache=new(StringComparer.Ordinal);
    readonly List<Task> timingTasks=[];
    public IPreviewPlaybackClock? PlaybackClock { get; set; }
    public event EventHandler<PreviewFrame>? FrameReady;
    public event EventHandler<Exception>? Failed;
    public PreviewFrame? LastFrame=>Volatile.Read(ref lastFrame);
    public bool IsPlaying=>Volatile.Read(ref playRequested)!=0;
    public NativePlaybackTiming? NativeTiming=>LastFrame?.Timing;

    public RealtimePreviewSession()
    {
        thread=new Thread(Run){IsBackground=true,Name="OurNotes native preview"};
        thread.Start();
    }

    public Task LoadSceneAsync(ScenePreviewSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Enqueue(()=>Load(()=>new SceneState(source),source.Duration));
    }

    public Task LoadHomeAsync(HomePreviewSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        // Snapshot mutable collection implementations supplied by a caller. The native
        // thread never enumerates a live UI document while the user edits it.
        var frozen=source with{Project=HomeProject.FromJson(source.Project.ToJson())};
        return Enqueue(()=>Load(()=>new HomeState(frozen),frozen.Project.Duration));
    }

    /// <summary>
    /// Commits a document whose current pose has already been applied with live edits.
    /// Keeps its pixels, time, context and models now; replays the new document when the
    /// next seek or playback actually needs its changed command history.
    /// </summary>
    public Task CommitSceneProjectAsync(ScenePreviewSource source)=>Enqueue(()=>
    {
        if(renderer is not SceneState scene)throw new InvalidOperationException("Load a scene before committing its edits.");
        if(playing)throw new InvalidOperationException("Pause the scene before committing its current edits.");
        scene.Commit(source);
        StartTiming(scene);
    });

    /// <summary>Waits for a resource-backed timing result without blocking the XAML thread.</summary>
    public async Task<NativePlaybackTiming> ResolveSceneTimingAsync(CancellationToken cancellationToken=default)
    {
        Task<NativePlaybackTiming>? task=null;
        await Enqueue(()=>
        {
            if(renderer is not SceneState scene)throw new InvalidOperationException("Load a scene before measuring its duration.");
            if(scene.Timing?.IsComplete==true){task=Task.FromResult(scene.Timing);return;}
            StartTiming(scene,true);task=scene.Timing?.IsComplete==true?Task.FromResult(scene.Timing):timingJob!.Task;
        }).ConfigureAwait(false);
        return await task!.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task DisableGpuPresentationAsync(string reason,long expectedSessionId)=>Enqueue(()=>
    {
        if(renderer is null||sessionId!=expectedSessionId)return;
        renderer.DisableGpu(reason);Render(frame,[]);
    });

    public Task SeekAsync(double seconds)
    {
        if(!double.IsFinite(seconds)||seconds<0||seconds>36000)throw new ArgumentOutOfRangeException(nameof(seconds));
        return UpdateState(update=>update.Time=seconds);
    }

    public Task ApplySceneEditsAsync(IReadOnlyList<JsonObject> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var copy=rows.Select(row=>(JsonObject)row.DeepClone()).ToArray();
        return UpdateState(update=>
        {
            // Merge pending drag values by native command/target/parameter. Moving the
            // pointer faster than rendering cannot build an unbounded redraw backlog.
            foreach(var row in copy)
            {
                string key=Archive.S(row,"cmd")+"\0"+Archive.S(row,"TargetName")+"\0"+Archive.S(row,"Parameter");
                if(update.SceneEdits.TryGetValue(key,out var prior)&&prior["Values"] is JsonObject previous&&row["Values"] is JsonObject values)
                    foreach(var (name,value) in previous)if(!values.ContainsKey(name))values[name]=value?.DeepClone();
                update.SceneEdits[key]=row;
            }
        });
    }

    public Task SetHomeEditStateAsync(HomeEditState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var frozen=HomeProject.FromJson(new HomeProject(new HomeProjectSource(1)){EditState=state,Duration=36000}.ToJson()).EditState;
        return UpdateState(update=>update.HomeEdits=frozen);
    }

    public void Play()
    {
        if(Interlocked.Exchange(ref playRequested,1)!=0)return;
        Observe(Enqueue(()=>
        {
            RequireRenderer();
            if(Volatile.Read(ref playRequested)==0)return;
            if(renderer is SceneState {NeedsReplay:true})Render(frame,[],replay:true);
            playing=true;playFrom=frame;playClock=Stopwatch.GetTimestamp();
            if(!resumeAudio||PlaybackClock?.Resume()!=true)PlaybackClock?.Start(frame/(double)renderer!.Fps);
            resumeAudio=false;ResetRate();
        }));
    }

    public void Pause()
    {
        PlaybackClock?.Pause();
        if(Interlocked.Exchange(ref playRequested,0)==0)return;
        Observe(Enqueue(()=>
        {
            resumeAudio=true;playing=false;
            if(renderer is not null&&LastFrame is {} previous)Publish(previous.Pixels,0);
        }));
    }

    static async void Observe(Task task)
    {
        // Enqueue already publishes failures. Void playback controls must still observe
        // their task so an event handler is not exposed to an unhandled async exception.
        try{await task.ConfigureAwait(false);}catch{ }
    }

    Task Enqueue(Action action)
    {
        var item=new Work(action);
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(closing,this);
            pendingUpdate=null;work.Enqueue(item);wake.Set();
        }
        return item.Completion!.Task;
    }

    Task UpdateState(Action<Update> edit)
    {
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(closing,this);
            if(pendingUpdate is null)
            {
                var update=pendingUpdate=new();
                work.Enqueue(new(()=>ApplyUpdate(update),update));
            }
            edit(pendingUpdate);pendingUpdate.Waiters.Add(completion);wake.Set();
        }
        return completion.Task;
    }

    void Load(Func<RendererState> factory,double duration)
    {
        if(!double.IsFinite(duration)||duration<=0||duration>36000)throw new ArgumentOutOfRangeException(nameof(duration));
        PlaybackClock?.Pause();playing=false;Interlocked.Exchange(ref playRequested,0);
        CancelTiming();
        renderer?.Dispose();renderer=null;
        // A context must be created after the old context is disposed on this thread.
        // Creating both and disposing the old one could silently unbind the new WGL one.
        resumeAudio=false;renderer=factory();renderer.Duration=duration;
        sessionId++;frame=0;ResetRate();
        if(renderer is SceneState scene)StartTiming(scene);
        Render(0,[]);
    }

    void ApplyUpdate(Update update)
    {
        lock(gate){if(ReferenceEquals(update,pendingUpdate))pendingUpdate=null;}
        try
        {
            RequireRenderer();
            if(update.HomeEdits is {} home)
            {
                if(renderer is not HomeState state)throw new InvalidOperationException("Load a home project before editing its tracks.");
                state.SetEdits(home);
            }
            if(update.SceneEdits.Count>0&&renderer is not SceneState)throw new InvalidOperationException("Load a scene project before applying scene edits.");
            if(update.Time.HasValue)resumeAudio=false;
            int next=update.Time.HasValue?FrameAt(update.Time.Value):frame;
            Render(next,update.SceneEdits.Values.ToArray(),replay:update.Time.HasValue);
            if(playing){playFrom=frame;playClock=Stopwatch.GetTimestamp();PlaybackClock?.Start(frame/(double)renderer!.Fps);}
            foreach(var waiter in update.Waiters)waiter.TrySetResult();
        }
        catch(Exception exception)
        {
            foreach(var waiter in update.Waiters)waiter.TrySetException(exception);
            throw;
        }
    }

    int FrameAt(double time)=>Math.Min(renderer!.EndFrame,checked((int)Math.Floor(time*renderer.Fps+1e-8)));
    void RequireRenderer(){if(renderer is null)throw new InvalidOperationException("Load a project into the native preview first.");}

    void Render(int next,IReadOnlyList<JsonObject> edits,bool replay=false)
    {
        var active=renderer!;long stamp=Stopwatch.GetTimestamp(),allocated=GC.GetAllocatedBytesForCurrentThread();
        int from=frame;
        if(active is SceneState scene&&(next<frame||replay&&scene.NeedsReplay))
        {
            // Native ADV has a stateful interpreter and physics. Reconstruct only for
            // a backward seek; normal playback and live edits retain models/context.
            var source=scene.Source;double duration=active.Duration;var document=scene.Document;var project=scene.Project;var cache=scene.CueCache;var timing=scene.Timing;
            renderer=null;active.Dispose();
            active=renderer=new SceneState(source,document,project,cache){Duration=duration,Timing=timing};sessionId++;from=0;
        }
        var pixels=active.Render(next,edits);
        frame=active is SceneState current?current.NativeFrame:next;
        if(active is SceneState finished&&finished.Finished&&!finished.NeedsReplay&&finished.Source.DurationMode==PreviewDurationMode.Automatic)
        {
            var timing=finished.FinishedTiming();ApplyTiming(finished,timing);CancelTiming();
        }
        long targetsStart=Stopwatch.GetTimestamp();var targets=active.Targets();var lips=active.LipSync();var animations=active.Animations();
        double targetsMs=Stopwatch.GetElapsedTime(targetsStart).TotalMilliseconds;
        var measured=active.Performance;
        var performance=new PreviewPerformance(measured.SimulationMilliseconds,measured.SceneMilliseconds,
            measured.ReadbackMilliseconds,targetsMs,GC.GetAllocatedBytesForCurrentThread()-allocated,Math.Max(0,frame-from));
        Publish(pixels,Stopwatch.GetElapsedTime(stamp).TotalMilliseconds,targets,lips,performance,animations);
    }

    void Publish(Pixels pixels,double milliseconds,IReadOnlyList<PreviewTarget>? targets=null,
        IReadOnlyList<PreviewLipSync>? lips=null,PreviewPerformance? performance=null,IReadOnlyList<PreviewActorAnimation>? animations=null)
    {
        long now=Stopwatch.GetTimestamp();
        bool changed=!ReferenceEquals(LastFrame?.Pixels,pixels);
        if(playing&&changed)
        {
            rateFrames++;
            double elapsed=Stopwatch.GetElapsedTime(rateClock,now).TotalSeconds;
            if(elapsed>=.5){measuredRate=rateFrames/elapsed;rateFrames=0;rateClock=now;}
        }
        var active=renderer!;
        var scene=active as SceneState;
        var result=new PreviewFrame(++sequence,sessionId,frame,frame/(double)active.Fps,pixels,targets??LastFrame?.Targets??[],
            milliseconds,playing?measuredRate:0,playing,active.Device,active.Duration,scene?.Source.DurationMode??PreviewDurationMode.Fixed,
            scene?.Timing,performance??LastFrame?.Performance,lips??LastFrame?.LipSync,animations??LastFrame?.ActorAnimations,active.GpuSurface,active.GpuFallbackReason);
        Volatile.Write(ref lastFrame,result);
        FrameReady?.Invoke(this,result);
    }

    void ResetRate(){rateClock=Stopwatch.GetTimestamp();rateFrames=0;measuredRate=0;}

    void Run()
    {
        try
        {
            while(true)
            {
                Work? item;
                lock(gate)
                {
                    if(closing)break;
                    item=work.Count>0?work.Dequeue():null;
                }
                if(item is not null)
                {
                    try{item.Action();item.Completion?.TrySetResult();}
                    catch(Exception exception){item.Completion?.TrySetException(exception);ReportFailure(exception);}
                    continue;
                }
                if(!playing||renderer is null){wake.WaitOne();continue;}
                try
                {
                    // Wall time selects the requested timeline position. If rendering
                    // is slower, intermediate presentation frames are skipped while
                    // the underlying animation still evaluates its required steps.
                    double elapsed=Stopwatch.GetElapsedTime(playClock).TotalSeconds;
                    double? audioPosition=PlaybackClock?.PositionSeconds;
                    int target=FrameAt(audioPosition??(playFrom/(double)renderer.Fps+elapsed));
                    if(target>frame)Render(target,[]);
                    if(frame>=renderer.EndFrame&&(audioPosition is null||audioPosition.Value>=(renderer.EndFrame+1)/(double)renderer.Fps-0.00001))
                    {
                        resumeAudio=false;PlaybackClock?.Pause();playing=false;Interlocked.Exchange(ref playRequested,0);
                        Publish(LastFrame!.Pixels,0);
                        continue;
                    }
                    // Rendering consumed wall time too. Waiting against the timestamp
                    // taken before it would add a second frame interval on a busy GPU.
                    if(audioPosition is not null){wake.WaitOne(5);continue;}
                    double remaining=(frame-playFrom+1)/(double)renderer.Fps-Stopwatch.GetElapsedTime(playClock).TotalSeconds;
                    if(remaining>0)wake.WaitOne(Math.Clamp((int)Math.Ceiling(remaining*1000),1,34));
                }
                catch(Exception exception){ReportFailure(exception);}
            }
        }
        finally
        {
            PlaybackClock?.Pause();playing=false;Interlocked.Exchange(ref playRequested,0);
            CancelTiming();
            try{renderer?.Dispose();renderer=null;}
            catch(Exception exception){ReportFailure(exception);}
            lock(gate)
            {
                closing=true;
                var error=new ObjectDisposedException(nameof(RealtimePreviewSession));
                foreach(var item in work)
                {
                    item.Completion?.TrySetException(error);
                    foreach(var waiter in item.Update?.Waiters??[])waiter.TrySetException(error);
                }
                work.Clear();pendingUpdate=null;
            }
            wake.Dispose();
            stopped.TrySetResult();
        }
    }

    void ReportFailure(Exception exception)
    {
        PlaybackClock?.Pause();playing=false;Interlocked.Exchange(ref playRequested,0);
        // A failed native draw never replaces the last successfully rendered pixels.
        try{Failed?.Invoke(this,exception);}catch{ }
    }

    public ValueTask DisposeAsync()
    {
        lock(gate)
        {
            if(!closing){closing=true;wake.Set();}
        }
        return new(WaitForStopAsync());
    }
    async Task WaitForStopAsync()
    {
        await stopped.Task.ConfigureAwait(false);
        Task[] tasks;lock(gate)tasks=timingTasks.ToArray();
        foreach(var task in tasks)try{await task.ConfigureAwait(false);}catch(OperationCanceledException){ }catch{ }
    }

    void CancelTiming(){timingJob?.Cancellation.Cancel();timingJob=null;}
    void StartTiming(SceneState scene,bool force=false)
    {
        if(!force&&scene.Source.DurationMode!=PreviewDurationMode.Automatic){CancelTiming();return;}
        if(scene.Timing?.IsComplete==true)return;
        if(timingCache.TryGetValue(scene.CacheKey,out var cached)){ApplyTiming(scene,cached);return;}
        if(timingJob is {} running&&running.Key==scene.CacheKey)return;
        CancelTiming();
        var job=new TimingJob(scene.CacheKey);timingJob=job;
        string json=scene.Document.ToJsonString();var source=scene.Source;var project=scene.Project;var cueCache=scene.CueCache;string hash=scene.ProjectHash;
        scene.Timing=new(hash,false,null,null,[]);
        job.Task=Task.Factory.StartNew(()=>
        {
            try
            {
                Thread.CurrentThread.Name??="OurNotes story timing";
                Thread.CurrentThread.Priority=ThreadPriority.BelowNormal;
                job.Cancellation.Token.ThrowIfCancellationRequested();
                using var native=new AdvStoryRenderer(new Archive(source.ArchivePath),project.Site,source.CubismPath,project.Adv,project.Language,
                    source.Width,source.Height,source.ProjectPath,source.FfmpegPath,1,JsonNode.Parse(json),true,cueCache);
                var result=native.AnalyzeTiming(hash,value=>PostTiming(job,value),job.Cancellation.Token);
                PostTiming(job,result);return result;
            }
            catch(OperationCanceledException){throw;}
            catch(Exception error){var result=new NativePlaybackTiming(hash,false,null,null,[],Error:error.Message);PostTiming(job,result);return result;}
        },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        lock(gate){timingTasks.RemoveAll(task=>task.IsCompleted);timingTasks.Add(job.Task);}
    }
    void PostTiming(TimingJob job,NativePlaybackTiming value)
    {
        try{Observe(Enqueue(()=>
        {
            if(renderer is not SceneState scene||scene.CacheKey!=job.Key||scene.Timing?.IsComplete==true)return;
            if(job.Cancellation.IsCancellationRequested)return;
            ApplyTiming(scene,value);
            if(LastFrame is {} previous)Publish(previous.Pixels,0);
        }));}catch(ObjectDisposedException){ }
    }
    void ApplyTiming(SceneState scene,NativePlaybackTiming value)
    {
        scene.Timing=value;
        if(value.IsComplete)
        {
            if(scene.Source.DurationMode==PreviewDurationMode.Automatic)scene.Duration=value.Duration!.Value;
            if(timingCache.Count>=16)timingCache.Remove(timingCache.Keys.First());
            timingCache[scene.CacheKey]=value;
        }
    }
    sealed class TimingJob(string key)
    {
        public string Key{get;}=key;
        public CancellationTokenSource Cancellation{get;}=new();
        public Task<NativePlaybackTiming> Task{get;set;}=null!;
    }

    sealed class Work(Action action,Update? update=null)
    {
        public Action Action{get;}=action;
        public Update? Update{get;}=update;
        public TaskCompletionSource? Completion{get;}=update is null?new(TaskCreationOptions.RunContinuationsAsynchronously):null;
    }

    sealed class Update
    {
        public double? Time;
        public HomeEditState? HomeEdits;
        public Dictionary<string,JsonObject> SceneEdits{get;}=new(StringComparer.Ordinal);
        public List<TaskCompletionSource> Waiters{get;}=[];
    }

    abstract class RendererState : IDisposable
    {
        public double Duration{get;set;}
        public virtual GpuPreviewSurface? GpuSurface=>null;
        public virtual string? GpuFallbackReason=>null;
        public abstract void DisableGpu(string reason);
        public abstract int Fps{get;}
        public abstract string Device{get;}
        public virtual int EndFrame=>checked((int)Math.Floor(Duration*Fps+1e-8));
        public virtual PreviewPerformance Performance=>new(0,0,0,0,0,0);
        public virtual IReadOnlyList<PreviewLipSync> LipSync()=>[];
        public virtual IReadOnlyList<PreviewActorAnimation> Animations()=>[];
        public abstract Pixels Render(int frame,IReadOnlyList<JsonObject> edits);
        public abstract IReadOnlyList<PreviewTarget> Targets();
        public abstract void Dispose();
    }

    sealed class SceneState : RendererState
    {
        readonly AdvStoryRenderer native;
        readonly bool clean;
        DirectorProject project;
        public DirectorProject Project=>project;
        public JsonNode Document{get;private set;}
        public string ProjectHash{get;private set;}
        public string CacheKey=>Path.GetFullPath(Source.ArchivePath)+"\0"+Source.Width+"x"+Source.Height+"\0"+ProjectHash;
        public AdvCueCache CueCache{get;}
        public NativePlaybackTiming? Timing{get;set;}
        public ScenePreviewSource Source{get;private set;}
        public bool NeedsReplay{get;private set;}
        public override int Fps=>30;
        public override string Device=>native.Device;
        public override GpuPreviewSurface? GpuSurface=>native.GpuSurface;
        public override string? GpuFallbackReason=>native.GpuFallbackReason;
        public override void DisableGpu(string reason)=>native.DisableGpuPresentation(reason);
        public int NativeFrame=>native.Loop.Frame;
        public bool Finished=>native.Player.Finished&&!native.Player.Cancelled;
        public override int EndFrame=>Source.DurationMode==PreviewDurationMode.Automatic?Timing?.EndFrame??30*36000:Math.Max(0,checked((int)Math.Ceiling(Duration*Fps))-1);
        PreviewPerformance measured=new(0,0,0,0,0,0);
        public override PreviewPerformance Performance=>measured;
        public SceneState(ScenePreviewSource source,JsonNode? document=null,DirectorProject? metadata=null,AdvCueCache? cueCache=null)
        {
            Source=source;
            if(!Enum.IsDefined(source.DurationMode))throw new ArgumentOutOfRangeException(nameof(source));
            project=metadata??DirectorProject.Read(source.ProjectPath);
            Document=document??DirectorProject.Document(source.ProjectPath);
            ProjectHash=Archive.Hash(Encoding.UTF8.GetBytes(Document.ToJsonString()));
            CueCache=cueCache??new();
            clean=project.Output["clean"]?.GetValue<bool>()??false;
            native=new(new Archive(source.ArchivePath),project.Site,source.CubismPath,project.Adv,project.Language,
                source.Width,source.Height,source.ProjectPath,source.FfmpegPath,1,Document,false,CueCache){PreferGpuPresentation=source.GpuPresentation};
        }
        public void Commit(ScenePreviewSource source)
        {
            ArgumentNullException.ThrowIfNull(source);
            var next=DirectorProject.Read(source.ProjectPath);
            if(next.Adv!=project.Adv||next.Site!=project.Site||next.Language!=project.Language||source.Width!=Source.Width||source.Height!=Source.Height
                ||source.ArchivePath!=Source.ArchivePath||source.CubismPath!=Source.CubismPath||source.FfmpegPath!=Source.FfmpegPath
                ||(next.Output["clean"]?.GetValue<bool>()??false)!=clean||Archive.S(next.Output,"letterbox")!=Archive.S(project.Output,"letterbox")
                ||Archive.S(next.Output,"isolateActor")!=Archive.S(project.Output,"isolateActor")
                ||!JsonNode.DeepEquals(next.Output["playbackViewport"],project.Output["playbackViewport"]))
                throw new ArgumentException("Changing source, viewport or presentation requires reloading the scene.");
            if(!double.IsFinite(source.Duration)||source.Duration<=0||source.Duration>36000)throw new ArgumentOutOfRangeException(nameof(source));
            Source=source;Duration=source.Duration;NeedsReplay=true;project=next;
            Document=DirectorProject.Document(source.ProjectPath);ProjectHash=Archive.Hash(Encoding.UTF8.GetBytes(Document.ToJsonString()));Timing=null;
        }
        public override Pixels Render(int frame,IReadOnlyList<JsonObject> edits)
        {
            double simulation=native.Timings.GetValueOrDefault("simulation"),readback=native.Timings.GetValueOrDefault("readbackAndResolve"),total=native.Timings.Values.Sum();
            var pixels=native.Frame(frame,clean,edits,Source.DurationMode==PreviewDurationMode.Automatic);
            double simulationDelta=native.Timings.GetValueOrDefault("simulation")-simulation,readbackDelta=native.Timings.GetValueOrDefault("readbackAndResolve")-readback;
            measured=new(simulationDelta,Math.Max(0,native.Timings.Values.Sum()-total-simulationDelta-readbackDelta),readbackDelta,0,0,0);
            return pixels;
        }
        public NativePlaybackTiming FinishedTiming()=>new(ProjectHash,true,native.Player.CompletionFrame,
            (native.Player.CompletionFrame!.Value+1)/30d,native.Player.CommandTimings(),native.Loop.Frame);
        public override IReadOnlyList<PreviewTarget> Targets()=>native.PreviewTargets();
        public override IReadOnlyList<PreviewLipSync> LipSync()=>native.Scene.PreviewLipSyncState();
        public override IReadOnlyList<PreviewActorAnimation> Animations()=>native.Scene.PreviewAnimationState();
        public override void Dispose()=>native.Dispose();
    }

    sealed class HomeState : RendererState
    {
        readonly HomeAnimation animation;
        readonly HomeRenderer native;
        readonly int fps;
        int last=-1;
        public override int Fps=>fps;
        public override string Device=>native.Device;
        public override GpuPreviewSurface? GpuSurface=>native.GpuSurface;
        public override string? GpuFallbackReason=>native.GpuFallbackReason;
        public override void DisableGpu(string reason)=>native.DisableGpuPresentation(reason);
        public HomeState(HomePreviewSource source)
        {
            source.Project.Validate();
            var project=source.Project;
            var scene=new HomeCatalog(new Archive(source.ArchivePath)).Open(project.Source.SpotId,project.Source.Site,project.Source.AdvId);
            var character=string.IsNullOrWhiteSpace(source.Character)?null:scene.SelectCharacter(source.Character);
            animation=new(new HomeResources(scene),character);fps=project.Output.Fps;
            native=new(animation,source.Width,source.Height,1,HomeTiming.Plan(project.Duration,fps:fps),
                project.EditState.Framing,project.EditState.Background,project.EditState){PreferGpuPresentation=source.GpuPresentation};
        }
        public void SetEdits(HomeEditState state)=>native.SetEditState(state);
        public override Pixels Render(int frame,IReadOnlyList<JsonObject> edits)
        {
            if(frame<last)animation.Reset();
            last=frame;return native.Frame(frame);
        }
        public override IReadOnlyList<PreviewTarget> Targets()=>native.PreviewTargets();
        public override void Dispose()=>native.Dispose();
    }
}
