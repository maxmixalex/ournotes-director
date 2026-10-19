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
    int? playbackEndFrame;
    double measuredRate;
    double scenePreviewScale=1;
    TimingJob? timingJob;
    readonly Dictionary<string,NativePlaybackTiming> timingCache=new(StringComparer.Ordinal);
    readonly List<Task> timingTasks=[];
    public IPreviewPlaybackClock? PlaybackClock { get; set; }
    public event EventHandler<PreviewFrame>? FrameReady;
    public event EventHandler<Exception>? Failed;
    public PreviewFrame? LastFrame=>Volatile.Read(ref lastFrame);
    public bool IsPlaying=>Volatile.Read(ref playRequested)!=0;
    /// <summary>The active drawing budget, including a change whose next seek was
    /// cancelled before it could publish pixels. LastFrame describes only pixels.</summary>
    public double ScenePreviewScale=>Volatile.Read(ref scenePreviewScale);
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
    /// Keeps its pixels, time, context and models now; replays changed command history
    /// at the next seek/playback. Presentation-only saves do not invalidate native state.
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

    public Task SeekAsync(double seconds,CancellationToken cancellationToken=default)
    {
        if(!double.IsFinite(seconds)||seconds<0||seconds>36000)throw new ArgumentOutOfRangeException(nameof(seconds));
        cancellationToken.ThrowIfCancellationRequested();
        return UpdateState(update=>{update.Time=seconds;update.SeekCancellation=cancellationToken;});
    }

    public Task ApplySceneEditsAsync(IReadOnlyList<JsonObject> rows)=>ApplySceneEditsAsync(rows,null);

    /// <summary>Applies parameter/controller edits and an optional isolation transform
    /// to one native pose, with a single draw and presentation.</summary>
    public Task ApplySceneEditsAsync(IReadOnlyList<JsonObject> rows,IsolatedActorTransform? transform)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if(transform is not null)_=ActorIsolation.ReadTransform(ActorIsolation.TransformJson(transform),"preview");
        var copy=rows.Select(row=>(JsonObject)row.DeepClone()).ToArray();
        return UpdateState(update=>
        {
            if(transform is not null)update.IsolationTransform=transform;
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

    public Task PreviewStagePositionAsync(string actor, int slot, System.Numerics.Vector3 position, long expectedSession, int expectedFrame)
    {
        StageDragPlane.CheckVector(position);
        return UpdateState(update=>update.StagePositions[actor]=new(actor,slot,position,expectedSession,expectedFrame));
    }

    /// <summary>Changes a transient scene drawing budget without drawing an extra
    /// old frame. The next normal seek publishes the requested native pose.</summary>
    public Task SetScenePreviewScaleAsync(double scale, CancellationToken cancellationToken = default)
    {
        if (scale is not (1 or .5 or .25)) throw new ArgumentOutOfRangeException(nameof(scale));
        cancellationToken.ThrowIfCancellationRequested();
        return Enqueue(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (renderer is not SceneState scene) throw new InvalidOperationException("Load a scene before changing its preview scale.");
            scene.SetScenePreviewScale(scale);
            Volatile.Write(ref scenePreviewScale,scene.ScenePreviewScale);
        });
    }

    public Task SynchronizeAsync()=>Enqueue(()=>{});

    public Task SetIsolatedTransformAsync(IsolatedActorTransform transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        _=ActorIsolation.ReadTransform(ActorIsolation.TransformJson(transform),"preview");
        return UpdateState(update=>update.IsolationTransform=transform);
    }

    /// <summary>Resizes an isolated model preview without reloading its native player,
    /// resetting physics or moving its playhead. Final export dimensions are untouched.</summary>
    public Task ResizePreviewAsync(int width,int height,int supersample=1,CancellationToken cancellationToken=default)
    {
        RenderDimensions.Validate(width,height,supersample);
        cancellationToken.ThrowIfCancellationRequested();
        return Enqueue(()=>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(renderer is not SceneState scene)throw new InvalidOperationException("Load an isolated actor before resizing its preview.");
            if(!scene.Resize(width,height,supersample))return;
            Render(frame,[],cancellationToken:cancellationToken);
        });
    }

    /// <summary>Reads actual model ranges/defaults and displayed values on the native
    /// renderer thread; callers never touch Cubism memory from the UI thread.</summary>
    public async Task<IReadOnlyList<PreviewParameter>> ReadActorParametersAsync(string actorId,CancellationToken cancellationToken=default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<PreviewParameter>? result=null;
        await Enqueue(()=>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(renderer is not SceneState scene)throw new InvalidOperationException("Load a scene before reading model parameters.");
            result=scene.Parameters(actorId);
        }).ConfigureAwait(false);
        return result!;
    }

    public Task SetHomeEditStateAsync(HomeEditState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var frozen=HomeProject.FromJson(new HomeProject(new HomeProjectSource(1)){EditState=state,Duration=36000}.ToJson()).EditState;
        return UpdateState(update=>update.HomeEdits=frozen);
    }

    public void Play(double? exclusiveEndSeconds=null)
    {
        if(exclusiveEndSeconds is {} end&&(!double.IsFinite(end)||end<=0||end>36000))throw new ArgumentOutOfRangeException(nameof(exclusiveEndSeconds));
        if(Interlocked.Exchange(ref playRequested,1)!=0)return;
        Observe(Enqueue(()=>
        {
            RequireRenderer();
            if(Volatile.Read(ref playRequested)==0)return;
            if(renderer is SceneState state)
            {
                // Either half of a cancelled quality transition can remain:
                // the budget may already differ, or the displayed pixels may.
                bool restoreQuality=state.ScenePreviewScale!=1||LastFrame?.ScenePreviewScale!=1;
                if(restoreQuality){state.SetScenePreviewScale(1);Volatile.Write(ref scenePreviewScale,1);}
                if(restoreQuality||state.NeedsReplay||state.NativeFrame!=frame)Render(frame,[],replay:true);
            }
            if(Volatile.Read(ref playRequested)==0)return;
            playbackEndFrame=exclusiveEndSeconds is {} stop?Math.Min(renderer!.EndFrame,Math.Max(frame,checked((int)Math.Ceiling(stop*renderer.Fps-1e-8))-1)):null;
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
            if(renderer is not null&&LastFrame is {} previous&&previous.SessionId==sessionId)Publish(previous.Pixels,0);
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
        Volatile.Write(ref scenePreviewScale,1);
        // A context must be created after the old context is disposed on this thread.
        // Creating both and disposing the old one could silently unbind the new WGL one.
        resumeAudio=false;playbackEndFrame=null;renderer=factory();renderer.Duration=duration;
        sessionId++;frame=0;ResetRate();
        if(renderer is SceneState scene)StartTiming(scene);
        Render(0,[]);
    }

    void ApplyUpdate(Update update)
    {
        lock(gate){if(ReferenceEquals(update,pendingUpdate))pendingUpdate=null;}
        try
        {
            update.SeekCancellation.ThrowIfCancellationRequested();
            RequireRenderer();
            if(update.HomeEdits is {} home)
            {
                if(renderer is not HomeState state)throw new InvalidOperationException("Load a home project before editing its tracks.");
                state.SetEdits(home);
            }
            if(update.SceneEdits.Count>0&&renderer is not SceneState)throw new InvalidOperationException("Load a scene project before applying scene edits.");
            if(update.IsolationTransform is not null&&renderer is not SceneState)throw new InvalidOperationException("Load an isolated actor before editing its presentation.");
            if(update.Time.HasValue)resumeAudio=false;
            if(update.StagePositions.Count>0)
            {
                if(update.Time.HasValue)throw new InvalidOperationException("定位过程中不能同时拖动人物。");
                if(renderer is not SceneState stage)throw new InvalidOperationException("当前不是原作舞台。");
                foreach(var move in update.StagePositions.Values)
                {
                    if(move.Session!=sessionId||move.Frame!=frame)throw new InvalidOperationException("画面已改变，请重新拖动。");
                    stage.PreviewStagePosition(move.Actor,move.Slot,move.Position);
                }
            }
            int next=update.Time.HasValue?FrameAt(update.Time.Value):frame;
            Render(next,update.SceneEdits.Values.ToArray(),replay:update.Time.HasValue,cancellationToken:update.SeekCancellation,isolationTransform:update.IsolationTransform);
            if(playing){playFrom=frame;playClock=Stopwatch.GetTimestamp();PlaybackClock?.Start(frame/(double)renderer!.Fps);}
            foreach(var waiter in update.Waiters)waiter.TrySetResult();
        }
        catch(OperationCanceledException) when(update.SeekCancellation.IsCancellationRequested)
        {
            // A superseded seek is not a renderer failure. Retain the previous displayed frame.
            foreach(var waiter in update.Waiters)waiter.TrySetCanceled(update.SeekCancellation);
        }
        catch(Exception exception)
        {
            foreach(var waiter in update.Waiters)waiter.TrySetException(exception);
            throw;
        }
    }

    int FrameAt(double time)=>Math.Min(renderer!.EndFrame,checked((int)Math.Floor(time*renderer.Fps+1e-8)));
    void RequireRenderer(){if(renderer is null)throw new InvalidOperationException("Load a project into the native preview first.");}

    void Render(int next,IReadOnlyList<JsonObject> edits,bool replay=false,CancellationToken cancellationToken=default,IsolatedActorTransform? isolationTransform=null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var active=renderer!;long stamp=Stopwatch.GetTimestamp(),allocated=GC.GetAllocatedBytesForCurrentThread();
        int gen0=GC.CollectionCount(0),gen1=GC.CollectionCount(1),gen2=GC.CollectionCount(2);
        TimeSpan gcPause=GC.GetTotalPauseDuration();long processAllocated=GC.GetTotalAllocatedBytes(false);
        var workBefore=PreviewNativeWork.Read();
        int from=frame;
        if(active is SceneState scene&&(next<scene.NativeFrame||replay&&scene.NeedsReplay))
        {
            // Native ADV has a stateful interpreter and physics. Reconstruct only for
            // a backward seek; normal playback and live edits retain models/context.
            var source=scene.Source;double duration=active.Duration;var document=scene.Document;var project=scene.Project;var cache=scene.CueCache;var timing=scene.Timing;
            double previewScale = scene.ScenePreviewScale;
            renderer=null;active.Dispose();
            var replayed = new SceneState(source,document,project,cache){Duration=duration,Timing=timing};
            active=renderer=replayed;
            replayed.SetScenePreviewScale(previewScale);sessionId++;from=0;
            Volatile.Write(ref scenePreviewScale,replayed.ScenePreviewScale);
        }
        if(isolationTransform is not null)((SceneState)active).SetIsolatedTransform(isolationTransform);
        var pixels=active.Render(next,edits,cancellationToken);
        frame=active is SceneState current?current.NativeFrame:next;
        if(active is SceneState finished&&finished.Finished&&!finished.NeedsReplay&&finished.Source.DurationMode==PreviewDurationMode.Automatic)
        {
            var timing=finished.FinishedTiming();ApplyTiming(finished,timing);CancelTiming();
        }
        long targetsStart=Stopwatch.GetTimestamp();var targets=active.Targets();var lips=active.LipSync();var animations=active.Animations();
        double targetsMs=Stopwatch.GetElapsedTime(targetsStart).TotalMilliseconds;
        var measured=active.Performance;
        var workAfter=PreviewNativeWork.Read();
        var diagnostics=new PreviewRenderDiagnostics(pixels.Width,pixels.Height,(active as SceneState)?.Source.Supersample??1,
            GC.CollectionCount(0)-gen0,GC.CollectionCount(1)-gen1,GC.CollectionCount(2)-gen2,
            (GC.GetTotalPauseDuration()-gcPause).TotalMilliseconds,GC.GetTotalAllocatedBytes(false)-processAllocated,
            workAfter.CompiledPrograms-workBefore.CompiledPrograms,
            workAfter.ShaderCompilationMilliseconds-workBefore.ShaderCompilationMilliseconds,
            workAfter.MeshUploads-workBefore.MeshUploads,workAfter.MeshUploadBytes-workBefore.MeshUploadBytes,
            workAfter.PoseRefreshes-workBefore.PoseRefreshes,workAfter.MaskPasses-workBefore.MaskPasses,
            workAfter.ModelDraws-workBefore.ModelDraws,workAfter.PresentationCopies-workBefore.PresentationCopies);
        var performance=new PreviewPerformance(measured.SimulationMilliseconds,measured.SceneMilliseconds,
            measured.ReadbackMilliseconds,targetsMs,GC.GetAllocatedBytesForCurrentThread()-allocated,Math.Max(0,frame-from),diagnostics);
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
            scene?.Timing,performance??LastFrame?.Performance,lips??LastFrame?.LipSync,animations??LastFrame?.ActorAnimations,active.GpuSurface,active.GpuFallbackReason)
            { ScenePreviewScale = changed ? scene?.ScenePreviewScale ?? 1 : LastFrame?.ScenePreviewScale ?? 1 };
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
                    catch(OperationCanceledException exception) when(exception.CancellationToken.IsCancellationRequested)
                    {
                        // A queued resize/quality request cancelled before execution
                        // is not a broken renderer or a reason to stop playback.
                        item.Completion?.TrySetCanceled(exception.CancellationToken);
                    }
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
                    int endFrame=Math.Min(renderer.EndFrame,playbackEndFrame??renderer.EndFrame);
                    int target=Math.Min(endFrame,FrameAt(audioPosition??(playFrom/(double)renderer.Fps+elapsed)));
                    if(target>frame)Render(target,[]);
                    if(frame>=endFrame&&(audioPosition is null||audioPosition.Value>=(endFrame+1)/(double)renderer.Fps-0.00001))
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
            if(LastFrame is {} previous&&previous.SessionId==sessionId)Publish(previous.Pixels,0);
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

    sealed record StagePosition(string Actor,int Slot,System.Numerics.Vector3 Position,long Session,int Frame);

    sealed class Update
    {
        public double? Time;
        public CancellationToken SeekCancellation;
        public HomeEditState? HomeEdits;
        public IsolatedActorTransform? IsolationTransform;
        public Dictionary<string,StagePosition> StagePositions{get;}=new(StringComparer.Ordinal);
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
        public abstract Pixels Render(int frame,IReadOnlyList<JsonObject> edits,CancellationToken cancellationToken=default);
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
        public double ScenePreviewScale => native.ScenePreviewScale;
        public void SetScenePreviewScale(double scale) => native.SetScenePreviewScale(scale);
        public bool NeedsReplay{get;private set;}
        public override int Fps=>30;
        public override string Device=>native.Device;
        public override GpuPreviewSurface? GpuSurface=>native.GpuSurface;
        public override string? GpuFallbackReason=>native.GpuFallbackReason;
        public override void DisableGpu(string reason)=>native.DisableGpuPresentation(reason);
        public int NativeFrame=>native.Loop.Frame;
        public bool Finished=>native.Player.Finished&&!native.Player.Cancelled;
        public override int EndFrame=>Source.DurationMode==PreviewDurationMode.Automatic?Timing?.EndFrame??30*36000:Math.Max(0,Story.Frames(Duration)-1);
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
                source.Width,source.Height,source.ProjectPath,source.FfmpegPath,source.Supersample,Document,false,CueCache){PreferGpuPresentation=source.GpuPresentation,SegmentedCpuFrames=source.SegmentedCpuFrames};
        }
        public void Commit(ScenePreviewSource source)
        {
            ArgumentNullException.ThrowIfNull(source);
            var next=DirectorProject.Read(source.ProjectPath);
            bool isolated=!string.IsNullOrWhiteSpace(Archive.S(project.Output,"isolateActor"));
            if(next.Adv!=project.Adv||next.Site!=project.Site||next.Language!=project.Language
                ||!isolated&&(source.Width!=Source.Width||source.Height!=Source.Height||source.Supersample!=Source.Supersample)
                ||source.ArchivePath!=Source.ArchivePath||source.CubismPath!=Source.CubismPath||source.FfmpegPath!=Source.FfmpegPath
                ||(next.Output["clean"]?.GetValue<bool>()??false)!=clean||Archive.S(next.Output,"letterbox")!=Archive.S(project.Output,"letterbox")
                ||Archive.S(next.Output,"isolateActor")!=Archive.S(project.Output,"isolateActor")
                ||isolated&&(!JsonNode.DeepEquals(next.Output["width"],project.Output["width"])
                    ||!JsonNode.DeepEquals(next.Output["height"],project.Output["height"]))
                ||ActorIsolation.ReadTransform(next.Output["isolateTransform"],next.Output["isolateActor"]?.ToString())!=native.IsolationTransform
                ||!JsonNode.DeepEquals(next.Output["isolateCanvas"],project.Output["isolateCanvas"])
                ||SceneContent.Read(next.Output["content"])!=SceneContent.Read(project.Output["content"])
                ||!JsonNode.DeepEquals(next.Output["playbackViewport"],project.Output["playbackViewport"]))
                throw new ArgumentException("Changing source, viewport or presentation requires reloading the scene.");
            if(!double.IsFinite(source.Duration)||source.Duration<=0||source.Duration>36000)throw new ArgumentOutOfRangeException(nameof(source));
            var document=DirectorProject.Document(source.ProjectPath);
            bool changedProgram=new[]{"source","initialize","commands","finalize","text"}
                .Any(key=>!JsonNode.DeepEquals(Document[key],document[key]))
                ||!string.Equals(Path.GetDirectoryName(Path.GetFullPath(source.ProjectPath)),
                    Path.GetDirectoryName(Path.GetFullPath(Source.ProjectPath)),
                    OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal);
            // Adaptive presentation can have resized while a UI snapshot was saving.
            // The live FBO dimensions are authoritative; output canvas checks stay strict.
            // CPU snapshot storage is a live client's transport preference, not a
            // saved project's setting. A fresh export snapshot must not disable it.
            Source=(isolated?source with{Width=Source.Width,Height=Source.Height,Supersample=Source.Supersample}:source)
                with{SegmentedCpuFrames=Source.SegmentedCpuFrames};
            Duration=source.Duration;NeedsReplay|=changedProgram;project=next;
            Document=document;ProjectHash=Archive.Hash(Encoding.UTF8.GetBytes(Document.ToJsonString()));
            Timing=!changedProgram&&Timing is {} timing?timing with{ProjectHash=ProjectHash}:null;
        }
        public override Pixels Render(int frame,IReadOnlyList<JsonObject> edits,CancellationToken cancellationToken=default)
        {
            double simulation=native.Timings.GetValueOrDefault("simulation"),readback=native.Timings.GetValueOrDefault("readbackAndResolve"),total=native.Timings.Values.Sum();
            var pixels=native.Frame(frame,clean,edits,Source.DurationMode==PreviewDurationMode.Automatic,cancellationToken);
            double simulationDelta=native.Timings.GetValueOrDefault("simulation")-simulation,readbackDelta=native.Timings.GetValueOrDefault("readbackAndResolve")-readback;
            measured=new(simulationDelta,Math.Max(0,native.Timings.Values.Sum()-total-simulationDelta-readbackDelta),readbackDelta,0,0,0);
            return pixels;
        }
        public NativePlaybackTiming FinishedTiming()=>new(ProjectHash,true,native.Player.CompletionFrame,
            (native.Player.CompletionFrame!.Value+1)/30d,native.Player.CommandTimings(),native.Loop.Frame);
        public void PreviewStagePosition(string actor,int slot,System.Numerics.Vector3 position)=>native.Scene.PreviewStagePosition(actor,slot,position);
        public void SetIsolatedTransform(IsolatedActorTransform transform)=>native.SetIsolatedTransform(transform);
        public bool Resize(int width,int height,int supersample)
        {
            if(width==Source.Width&&height==Source.Height&&supersample==Source.Supersample)return false;
            native.ResizePreview(width,height,supersample);
            Source=Source with{Width=width,Height=height,Supersample=supersample};return true;
        }
        public IReadOnlyList<PreviewParameter> Parameters(string actor)=>(native.Scene.Character(actor)
            ??throw new ArgumentException("Unknown actor: "+actor)).PreviewParameters();
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
        public override Pixels Render(int frame,IReadOnlyList<JsonObject> edits,CancellationToken cancellationToken=default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(frame<last)animation.Reset();
            last=frame;return native.Frame(frame);
        }
        public override IReadOnlyList<PreviewTarget> Targets()=>native.PreviewTargets();
        public override void Dispose()=>native.Dispose();
    }
}
