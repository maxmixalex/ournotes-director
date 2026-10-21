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
    // Tests cancel after the old native context has been released, rather than
    // racing a timer against a queued request. Ordinary clients leave this unset.
    internal Action? SceneReconstructionStarted{get;set;}
    // A same-binary correctness/performance control for native regression tests.
    // Ordinary editor clients always retain bounded exact frame history.
    internal bool EnableFrameHistory{get;set;}=true;

    public RealtimePreviewSession()
    {
        thread=new Thread(Run){IsBackground=true,Name="OurNotes native preview"};
        thread.Start();
    }

    public Task LoadSceneAsync(ScenePreviewSource source,CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        return Enqueue(()=>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Load(()=>new SceneState(source,cancellationToken),source.Duration,cancellationToken);
        });
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
        SynchronizeDisplayedScene();
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

    /// <summary>Re-presents retained pixels at the latest XAML display size on
    /// their owning render thread. Layout never evaluates animation or advances time.</summary>
    public Task SetGpuDisplaySizeAsync(GpuPreviewSurface surface, long expectedSessionId, double width, double height)=>Enqueue(()=>
    {
        if (sessionId != expectedSessionId || !ReferenceEquals(renderer?.GpuSurface, surface)) return;
        surface.SetDisplaySize(width, height);
    });

    public Task SeekAsync(double seconds,CancellationToken cancellationToken=default)
        =>Seek(seconds,false,false,cancellationToken);

    /// <summary>Interactive pointer motion may present an exact previously rendered
    /// frame without moving the native interpreter. Finish a gesture with SeekAsync;
    /// playback, edits and parameter reads also synchronize the native pose first.</summary>
    public Task ScrubAsync(double seconds,CancellationToken cancellationToken=default,bool allowReducedQuality=true)
        =>Seek(seconds,true,allowReducedQuality,cancellationToken);

    Task Seek(double seconds,bool allowCachedFrame,bool allowReducedQuality,CancellationToken cancellationToken)
    {
        if(!double.IsFinite(seconds)||seconds<0||seconds>36000)throw new ArgumentOutOfRangeException(nameof(seconds));
        cancellationToken.ThrowIfCancellationRequested();
        return UpdateState(update=>{update.Time=seconds;update.AllowCachedFrame=allowCachedFrame;
            update.AllowReducedQuality=allowReducedQuality;update.SeekCancellation=cancellationToken;});
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

    public Task SynchronizeAsync()=>Enqueue(()=>SynchronizeDisplayedScene());

    void SynchronizeDisplayedScene(CancellationToken cancellationToken=default)
    {
        if(renderer is SceneState scene
            &&(!scene.NativeReady||scene.NativeFrame!=frame||LastFrame?.IsCached==true&&scene.NeedsReplay))
            Render(frame,[],replay:true,cancellationToken:cancellationToken,allowIntermediateHistory:false);
    }

    public Task SetIsolatedTransformAsync(IsolatedActorTransform transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        _=ActorIsolation.ReadTransform(ActorIsolation.TransformJson(transform),"preview");
        return UpdateState(update=>update.IsolationTransform=transform);
    }

    /// <summary>Resizes a preview without reloading its native player,
    /// resetting physics or moving its playhead. Final export dimensions are untouched.</summary>
    public Task ResizePreviewAsync(int width,int height,int supersample=1,CancellationToken cancellationToken=default)
    {
        RenderDimensions.Validate(width,height,supersample);
        cancellationToken.ThrowIfCancellationRequested();
        return Enqueue(()=>
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool resized=renderer switch
            {
                SceneState scene=>scene.Resize(width,height,supersample),
                HomeState home=>home.Resize(width,height,supersample),
                _=>throw new InvalidOperationException("Load a project before resizing its preview.")
            };
            bool qualityChanged=renderer is SceneState active&&active.ScenePreviewScale!=(LastFrame?.ScenePreviewScale??1);
            if(!resized&&!qualityChanged)return;
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
            SynchronizeDisplayedScene(cancellationToken);
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
                if(restoreQuality||state.NeedsReplay||state.NativeFrame!=frame)Render(frame,[],replay:true,allowIntermediateHistory:false);
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

    void Load(Func<RendererState> factory,double duration,CancellationToken cancellationToken=default)
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
        Render(0,[],cancellationToken:cancellationToken);
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
                    if(move.Session!=sessionId||move.Frame!=frame)throw new InvalidOperationException("画面已改变，请重新拖动。");
                SynchronizeDisplayedScene(update.SeekCancellation);
                foreach(var move in update.StagePositions.Values)
                {
                    stage.PreviewStagePosition(move.Actor,move.Slot,move.Position);
                }
            }
            int next=update.Time.HasValue?FrameAt(update.Time.Value):frame;
            bool imageOnly=update.AllowCachedFrame&&update.Time.HasValue&&update.SceneEdits.Count==0
                &&update.IsolationTransform is null&&update.StagePositions.Count==0&&!playing&&Volatile.Read(ref playRequested)==0;
            Render(next,update.SceneEdits.Values.ToArray(),replay:update.Time.HasValue,cancellationToken:update.SeekCancellation,
                isolationTransform:update.IsolationTransform,allowCachedFrame:imageOnly,allowReducedQuality:update.AllowReducedQuality);
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

    void Render(int next,IReadOnlyList<JsonObject> edits,bool replay=false,CancellationToken cancellationToken=default,
        IsolatedActorTransform? isolationTransform=null,bool allowCachedFrame=false,bool allowReducedQuality=false,
        bool allowIntermediateHistory=true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var active=renderer!;long stamp=Stopwatch.GetTimestamp(),allocated=GC.GetAllocatedBytesForCurrentThread();
        if(active is SceneState cacheOwner)cacheOwner.FrameHistoryEnabled=EnableFrameHistory;
        int gen0=GC.CollectionCount(0),gen1=GC.CollectionCount(1),gen2=GC.CollectionCount(2);
        TimeSpan gcPause=GC.GetTotalPauseDuration();long processAllocated=GC.GetTotalAllocatedBytes(false);
        var workBefore=PreviewNativeWork.Read();
        if(allowCachedFrame&&active is SceneState cachedScene&&cachedScene.TryCachedFrame(next,allowReducedQuality,out var cached))
        {
            cancellationToken.ThrowIfCancellationRequested();
            frame=next;
            Publish(cached!.Pixels,Stopwatch.GetElapsedTime(stamp).TotalMilliseconds,cached.Targets,cached.LipSync,
                new(0,0,0,0,GC.GetAllocatedBytesForCurrentThread()-allocated,0),cached.Animations,cached.Scale,true);
            return;
        }
        // A cached picture can move the displayed playhead independently. Count
        // actual interpreter steps, never distance from that displayed picture.
        int from=active is SceneState origin?Math.Max(0,origin.NativeFrame):frame;
        // Restoring the displayed native state must not pay for optional nearby
        // pictures, even when catching up to a cached frame only moves forward.
        if(!allowCachedFrame&&next==frame&&LastFrame?.IsCached==true)allowIntermediateHistory=false;
        if(active is SceneState scene&&(!scene.NativeReady||next<scene.NativeFrame||replay&&scene.NeedsReplay))
        {
            // Native ADV has a stateful interpreter and physics. Reconstruct only for
            // a backward seek; normal playback and live edits retain models/context.
            scene.Rebuild(cancellationToken,SceneReconstructionStarted);sessionId++;from=0;
            allowIntermediateHistory=false;
            Volatile.Write(ref scenePreviewScale,scene.ScenePreviewScale);
        }
        if(isolationTransform is not null)((SceneState)active).SetIsolatedTransform(isolationTransform);
        if(active is SceneState history){history.HistoryBudgetMilliseconds=allowIntermediateHistory?(playing?8:200):0;history.PlaybackRender=playing;}
        var pixels=active.Render(next,edits,cancellationToken);
        frame=active is SceneState current?current.NativeFrame:next;
        if(active is SceneState finished&&finished.Finished&&!finished.NeedsReplay&&finished.Source.DurationMode==PreviewDurationMode.Automatic)
        {
            var timing=finished.FinishedTiming();ApplyTiming(finished,timing);CancelTiming();
        }
        long targetsStart=Stopwatch.GetTimestamp();var targets=active.Targets();var lips=active.LipSync();var animations=active.Animations();
        double targetsMs=Stopwatch.GetElapsedTime(targetsStart).TotalMilliseconds;
        var measured=active.Performance;
        if(active is SceneState rendered)rendered.Remember(frame,pixels,targets,lips,animations);
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
        IReadOnlyList<PreviewLipSync>? lips=null,PreviewPerformance? performance=null,IReadOnlyList<PreviewActorAnimation>? animations=null,
        double? actualScale=null,bool cached=false)
    {
        long now=Stopwatch.GetTimestamp();
        bool changed=!ReferenceEquals(LastFrame?.Pixels,pixels);
        if(!changed)cached=LastFrame?.IsCached??cached;
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
            scene?.Timing,performance??LastFrame?.Performance,lips??LastFrame?.LipSync,animations??LastFrame?.ActorAnimations,
                cached?null:active.GpuSurface,active.GpuFallbackReason)
            { ScenePreviewScale = actualScale??(changed ? scene?.ScenePreviewScale ?? 1 : LastFrame?.ScenePreviewScale ?? 1),
                Initialization = scene?.Initialization, IsCached=cached,
                NativeStateSynchronized=scene is null||scene.NativeReady&&!scene.NeedsReplay&&scene.NativeFrame==frame,
                FrameCache=scene?.FrameCacheDiagnostics };
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
        int referenceWidth=scene.ReferenceWidth,referenceHeight=scene.ReferenceHeight;
        scene.Timing=new(hash,false,null,null,[]);
        job.Task=Task.Factory.StartNew(()=>
        {
            try
            {
                Thread.CurrentThread.Name??="OurNotes story timing";
                Thread.CurrentThread.Priority=ThreadPriority.BelowNormal;
                job.Cancellation.Token.ThrowIfCancellationRequested();
                using var native=new AdvStoryRenderer(new Archive(source.ArchivePath),project.Site,source.CubismPath,project.Adv,project.Language,
                    referenceWidth,referenceHeight,source.ProjectPath,source.FfmpegPath,1,JsonNode.Parse(json),true,cueCache);
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
            if(scene.NativeReady&&LastFrame is {} previous&&previous.SessionId==sessionId)Publish(previous.Pixels,0);
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
        public bool AllowCachedFrame;
        public bool AllowReducedQuality;
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
        AdvStoryRenderer? native;
        readonly bool clean;
        readonly PreviewTextureCache textureCache=new();
        readonly PreviewModelCache modelCache=new();
        readonly PreviewFrameCache frameCache=new();
        bool frameCacheAllowed=true;
        double lastHistoryMilliseconds=40,historyMilliseconds;
        int capturedFrames;
        public double HistoryBudgetMilliseconds{get;set;}=200;
        int nextGpuCaptureFrame;
        bool historyEnabled=true;
        public bool PlaybackRender{get;set;}
        public bool FrameHistoryEnabled
        {
            get=>historyEnabled;
            set{if(historyEnabled&&!value)frameCache.Clear();historyEnabled=value;}
        }
        readonly int referenceSupersample;
        double previewScale=1;
        string device="";
        IsolatedActorTransform isolationTransform;
        DirectorProject project;
        public DirectorProject Project=>project;
        public JsonNode Document{get;private set;}
        public string ProjectHash{get;private set;}
        public string CacheKey=>Path.GetFullPath(Source.ArchivePath)+"\0"+ReferenceWidth+"x"+ReferenceHeight+"\0"+ProjectHash;
        public AdvCueCache CueCache{get;}=new();
        public NativePlaybackTiming? Timing{get;set;}
        public PreviewInitializationDiagnostics? Initialization{get;private set;}
        public PreviewFrameCacheDiagnostics FrameCacheDiagnostics=>new(frameCache.Count,frameCache.RetainedBytes,frameCache.LimitBytes,
            frameCache.Hits,frameCache.Misses,frameCache.StoreMilliseconds,frameCache.RestoreMilliseconds,NativeFrame,
                capturedFrames,historyMilliseconds,frameCache.Ranges());
        public ScenePreviewSource Source{get;private set;}
        public int ReferenceWidth{get;}
        public int ReferenceHeight{get;}
        public bool NativeReady=>native is not null;
        AdvStoryRenderer Native=>native??throw new InvalidOperationException("The cancelled scene reconstruction must resume before accessing native state.");
        public double ScenePreviewScale=>previewScale;
        public void SetScenePreviewScale(double scale)
        {
            if(scale is not (1 or .5 or .25))throw new ArgumentOutOfRangeException(nameof(scale));
            if(!string.IsNullOrWhiteSpace(Archive.S(project.Output,"isolateActor"))&&scale!=1)
                throw new InvalidOperationException("Isolated models use their own adaptive presentation canvas.");
            native?.SetScenePreviewScale(scale);previewScale=scale;
        }
        public bool NeedsReplay{get;private set;}
        public override int Fps=>30;
        public override string Device=>native?.Device??device;
        public override GpuPreviewSurface? GpuSurface=>native?.GpuSurface;
        public override string? GpuFallbackReason=>native?.GpuFallbackReason;
        public override void DisableGpu(string reason)
        {
            native?.DisableGpuPresentation(reason);Source=Source with{GpuPresentation=false};
        }
        public int NativeFrame=>native?.Loop.Frame??-1;
        public bool Finished=>native is {} active&&active.Player.Finished&&!active.Player.Cancelled;
        public override int EndFrame=>Source.DurationMode==PreviewDurationMode.Automatic?Timing?.EndFrame??30*36000:Math.Max(0,Story.Frames(Duration)-1);
        PreviewPerformance measured=new(0,0,0,0,0,0);
        public override PreviewPerformance Performance=>measured;
        public SceneState(ScenePreviewSource source,CancellationToken cancellationToken=default)
        {
            long start=Stopwatch.GetTimestamp();cancellationToken.ThrowIfCancellationRequested();Source=source;
            ReferenceWidth=source.Width;ReferenceHeight=source.Height;referenceSupersample=source.Supersample;
            if(!Enum.IsDefined(source.DurationMode))throw new ArgumentOutOfRangeException(nameof(source));
            project=DirectorProject.Read(source.ProjectPath);
            Document=DirectorProject.Document(source.ProjectPath);
            ProjectHash=Archive.Hash(Encoding.UTF8.GetBytes(Document.ToJsonString()));
            clean=project.Output["clean"]?.GetValue<bool>()??false;
            isolationTransform=ActorIsolation.ReadTransform(project.Output["isolateTransform"],project.Output["isolateActor"]?.ToString());
            Construct(start,Stopwatch.GetElapsedTime(start).TotalMilliseconds,0,cancellationToken);
        }
        public void Rebuild(CancellationToken cancellationToken,Action? started)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long start=Stopwatch.GetTimestamp();NeedsReplay=true;
            var previous=native;native=null;previous?.Dispose();
            started?.Invoke();
            Construct(start,0,Stopwatch.GetElapsedTime(start).TotalMilliseconds,cancellationToken);
        }
        void Construct(long start,double sourceMilliseconds,double disposeMilliseconds,CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long hits=textureCache.Hits,decodes=textureCache.Decodes,sourceBytes=textureCache.SourceBytes,decodedBytes=textureCache.DecodedBytes;
            double decodeMilliseconds=textureCache.DecodeMilliseconds;
            var modelStart=modelCache.Capture();
            var archive=new Archive(Source.ArchivePath){PreviewTextures=textureCache,PreviewModels=modelCache,LoadCancellation=cancellationToken};
            AdvStoryRenderer? ready=null;
            try
            {
                ready=new(archive,project.Site,Source.CubismPath,project.Adv,project.Language,
                    ReferenceWidth,ReferenceHeight,Source.ProjectPath,Source.FfmpegPath,referenceSupersample,Document,false,CueCache,
                    initializationCancellationToken:cancellationToken)
                    {PreferGpuPresentation=Source.GpuPresentation,SegmentedCpuFrames=Source.SegmentedCpuFrames};
                long presentation=Stopwatch.GetTimestamp();
                if(!string.IsNullOrWhiteSpace(Archive.S(project.Output,"isolateActor")))ready.SetIsolatedTransform(isolationTransform);
                ready.ResizePreview(Source.Width,Source.Height,Source.Supersample);
                ready.SetScenePreviewScale(previewScale);
                cancellationToken.ThrowIfCancellationRequested();
                var timings=new Dictionary<string,double>(ready.InitializationTimings)
                {
                    ["previousRendererDisposeMs"]=disposeMilliseconds,
                    ["restorePresentationMs"]=Stopwatch.GetElapsedTime(presentation).TotalMilliseconds
                };
                Initialization=new(Stopwatch.GetElapsedTime(start).TotalMilliseconds,sourceMilliseconds,timings,
                    textureCache.Hits-hits,textureCache.Decodes-decodes,textureCache.SourceBytes-sourceBytes,
                    textureCache.DecodedBytes-decodedBytes,textureCache.DecodeMilliseconds-decodeMilliseconds,
                    textureCache.RetainedBytes,textureCache.Count,textureCache.LimitBytes,modelCache.DiagnosticsSince(modelStart));
                device=ready.Device;native=ready;ready=null;NeedsReplay=false;frameCacheAllowed=true;nextGpuCaptureFrame=0;
            }
            finally
            {
                // A gesture token must not leak into subsequent playback or the
                // independent timing worker which can share this scene's PCM cache.
                archive.LoadCancellation=default;ready?.Dispose();
            }
        }
        public void Commit(ScenePreviewSource source)
        {
            ArgumentNullException.ThrowIfNull(source);
            var next=DirectorProject.Read(source.ProjectPath);
            if(next.Adv!=project.Adv||next.Site!=project.Site||next.Language!=project.Language
                ||source.ArchivePath!=Source.ArchivePath||source.CubismPath!=Source.CubismPath||source.FfmpegPath!=Source.FfmpegPath
                ||(next.Output["clean"]?.GetValue<bool>()??false)!=clean||Archive.S(next.Output,"letterbox")!=Archive.S(project.Output,"letterbox")
                ||Archive.S(next.Output,"isolateActor")!=Archive.S(project.Output,"isolateActor")
                ||!JsonNode.DeepEquals(next.Output["width"],project.Output["width"])
                ||!JsonNode.DeepEquals(next.Output["height"],project.Output["height"])
                ||ActorIsolation.ReadTransform(next.Output["isolateTransform"],next.Output["isolateActor"]?.ToString())!=isolationTransform
                ||!JsonNode.DeepEquals(next.Output["isolateCanvas"],project.Output["isolateCanvas"])
                ||SceneContent.Read(next.Output["content"])!=SceneContent.Read(project.Output["content"])
                ||!JsonNode.DeepEquals(next.Output["playbackViewport"],project.Output["playbackViewport"]))
                throw new ArgumentException("Changing source, viewport or presentation requires reloading the scene.");
            if(!double.IsFinite(source.Duration)||source.Duration<=0||source.Duration>36000)throw new ArgumentOutOfRangeException(nameof(source));
            var document=DirectorProject.Document(source.ProjectPath);
            if(!JsonNode.DeepEquals(Document,document))frameCache.Clear();
            bool changedProgram=new[]{"source","initialize","commands","finalize","text"}
                .Any(key=>!JsonNode.DeepEquals(Document[key],document[key]))
                ||!string.Equals(Path.GetDirectoryName(Path.GetFullPath(source.ProjectPath)),
                    Path.GetDirectoryName(Path.GetFullPath(Source.ProjectPath)),
                    OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal);
            if(changedProgram)frameCache.Clear();
            // Adaptive presentation can have resized while a UI snapshot was saving.
            // The live FBO dimensions are authoritative; output canvas checks stay strict.
            // CPU snapshot storage is a live client's transport preference, not a
            // saved project's setting. A fresh export snapshot must not disable it.
            Source=source with{Width=Source.Width,Height=Source.Height,Supersample=Source.Supersample,
                SegmentedCpuFrames=Source.SegmentedCpuFrames};
            Duration=source.Duration;NeedsReplay|=changedProgram;project=next;
            Document=document;ProjectHash=Archive.Hash(Encoding.UTF8.GetBytes(Document.ToJsonString()));
            Timing=!changedProgram&&Timing is {} timing?timing with{ProjectHash=ProjectHash}:null;
            frameCacheAllowed=true;
        }
        public override Pixels Render(int frame,IReadOnlyList<JsonObject> edits,CancellationToken cancellationToken=default)
        {
            var native=Native;
            if(edits.Count>0)InvalidateLiveFrames();
            // GPU playback readback is sampled using the last measured readback
            // plus compression cost, targeting at most 2 ms per source frame on
            // average. Paused seeks can retain their requested frame immediately.
            native.CapturePreviewPixels=frameCacheAllowed&&FrameHistoryEnabled
                &&(long)Source.Width*Source.Height*4<=frameCache.LimitBytes&&!frameCache.Contains(frame,ScenePreviewScale)
                &&(!PlaybackRender||frame>=nextGpuCaptureFrame);
            native.PreviewCaptureScale=PlaybackRender&&native.CanCaptureHistory ? .25 : 1;
            double simulation=native.Timings.GetValueOrDefault("simulation"),readback=native.Timings.GetValueOrDefault("readbackAndResolve"),total=native.Timings.Values.Sum();
            // At most six nearby exact frames, adapted to the measured
            // cost of a quarter-size draw. A slow sample may finish after the soft
            // budget, but no subsequent sample starts beyond it. Frame zero never
            // incurs this work, and normal playback has a much smaller allowance.
            double spent=0,budget=HistoryBudgetMilliseconds;
            int count=Math.Clamp((int)(budget/Math.Max(1,lastHistoryMilliseconds)),1,6);
            int first=Math.Max(native.Loop.Frame,frame-count);
            bool collect=frameCacheAllowed&&FrameHistoryEnabled&&native.CanCaptureHistory&&frame>native.Loop.Frame
                &&edits.Count==0&&budget>0&&budget>=lastHistoryMilliseconds;
            void Capture(int current)
            {
                if(current<first||spent>=budget||!native.CanDrawHistory||frameCache.Contains(current,.25))return;
                cancellationToken.ThrowIfCancellationRequested();long started=Stopwatch.GetTimestamp();
                var sample=native.CaptureHistoryFrame(clean);
                cancellationToken.ThrowIfCancellationRequested();frameCache.Store(sample);
                double elapsed=Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                spent+=elapsed;historyMilliseconds+=elapsed;
                // One cold driver draw must not permanently disable useful later
                // attempts. Keep the estimate conservative and the actual budget
                // counter honest; every attempt still observes cancellation.
                lastHistoryMilliseconds=Math.Min(200,elapsed);capturedFrames++;
            }
            var pixels=native.FrameWithHistory(frame,clean,edits,Source.DurationMode==PreviewDurationMode.Automatic,cancellationToken,collect?Capture:null);
            double simulationDelta=native.Timings.GetValueOrDefault("simulation")-simulation,readbackDelta=native.Timings.GetValueOrDefault("readbackAndResolve")-readback;
            measured=new(simulationDelta,Math.Max(0,native.Timings.Values.Sum()-total-simulationDelta-readbackDelta),readbackDelta,0,0,0);
            return pixels;
        }
        public NativePlaybackTiming FinishedTiming()=>new(ProjectHash,true,Native.Player.CompletionFrame,
            (Native.Player.CompletionFrame!.Value+1)/30d,Native.Player.CommandTimings(),Native.Loop.Frame);
        void InvalidateLiveFrames(){frameCache.Clear();frameCacheAllowed=false;}
        public bool TryCachedFrame(int number,bool allowReducedQuality,out PreviewFrameCache.Frame? cached)
        {
            cached=null;
            return frameCacheAllowed&&FrameHistoryEnabled&&!NeedsReplay&&frameCache.TryGet(number,
                allowReducedQuality&&Native.CanCaptureHistory ? .25 : ScenePreviewScale,out cached);
        }
        public void Remember(int number,Pixels pixels,IReadOnlyList<PreviewTarget> targets,
            IReadOnlyList<PreviewLipSync> lips,IReadOnlyList<PreviewActorAnimation> animations)
        {
            if(frameCacheAllowed&&FrameHistoryEnabled&&!NeedsReplay)
            {
                if(PlaybackRender&&number<nextGpuCaptureFrame)return;
                double before=frameCache.StoreMilliseconds;
                double scale=Native.CapturedPreviewPixels is not null?Math.Min(ScenePreviewScale,Native.PreviewCaptureScale):ScenePreviewScale;
                frameCache.Store(new(number,Native.CapturedPreviewPixels??pixels,scale,targets,lips,animations));
                if(Native.CapturedPreviewPixels is not null)
                {
                    double cost=Native.PreviewCaptureMilliseconds+frameCache.StoreMilliseconds-before;
                    nextGpuCaptureFrame=number+Math.Clamp((int)Math.Ceiling(cost/2),1,30);
                }
                else if(PlaybackRender)nextGpuCaptureFrame=number+Math.Clamp((int)Math.Ceiling((frameCache.StoreMilliseconds-before)/2),1,30);
            }
        }
        public void PreviewStagePosition(string actor,int slot,System.Numerics.Vector3 position)
        {InvalidateLiveFrames();Native.Scene.PreviewStagePosition(actor,slot,position);}
        public void SetIsolatedTransform(IsolatedActorTransform transform)
        {
            if(transform!=isolationTransform)frameCache.Clear();
            Native.SetIsolatedTransform(transform);isolationTransform=Native.IsolationTransform;
        }
        public bool Resize(int width,int height,int supersample)
        {
            if(width==Source.Width&&height==Source.Height&&supersample==Source.Supersample)return false;
            frameCache.Clear();
            native?.ResizePreview(width,height,supersample);
            Source=Source with{Width=width,Height=height,Supersample=supersample};return true;
        }
        public IReadOnlyList<PreviewParameter> Parameters(string actor)=>(Native.Scene.Character(actor)
            ??throw new ArgumentException("Unknown actor: "+actor)).PreviewParameters();
        public override IReadOnlyList<PreviewTarget> Targets()=>Native.PreviewTargets();
        public override IReadOnlyList<PreviewLipSync> LipSync()=>Native.Scene.PreviewLipSyncState();
        public override IReadOnlyList<PreviewActorAnimation> Animations()=>Native.Scene.PreviewAnimationState();
        public override void Dispose(){var previous=native;native=null;previous?.Dispose();}
    }

    sealed class HomeState : RendererState
    {
        readonly HomeAnimation animation;
        readonly HomeRenderer native;
        readonly int fps;
        int width,height,supersample=1;
        int last=-1;
        public override int Fps=>fps;
        public override string Device=>native.Device;
        public override GpuPreviewSurface? GpuSurface=>native.GpuSurface;
        public override string? GpuFallbackReason=>native.GpuFallbackReason;
        public override void DisableGpu(string reason)=>native.DisableGpuPresentation(reason);
        public HomeState(HomePreviewSource source)
        {
            source.Project.Validate();
            width=source.Width;height=source.Height;
            var project=source.Project;
            var scene=new HomeCatalog(new Archive(source.ArchivePath)).Open(project.Source.SpotId,project.Source.Site,project.Source.AdvId);
            var character=string.IsNullOrWhiteSpace(source.Character)?null:scene.SelectCharacter(source.Character);
            animation=new(new HomeResources(scene),character);fps=project.Output.Fps;
            native=new(animation,source.Width,source.Height,1,HomeTiming.Plan(project.Duration,fps:fps),
                project.EditState.Framing,project.EditState.Background,project.EditState){PreferGpuPresentation=source.GpuPresentation};
        }
        public bool Resize(int nextWidth,int nextHeight,int sampling)
        {
            if(width==nextWidth&&height==nextHeight&&supersample==sampling)return false;
            native.ResizePreview(nextWidth,nextHeight,sampling);
            width=nextWidth;height=nextHeight;supersample=sampling;return true;
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
