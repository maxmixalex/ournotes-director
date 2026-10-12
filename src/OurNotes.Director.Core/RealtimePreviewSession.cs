using System.Diagnostics;
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
    bool closing,playing;
    int playRequested;
    long sequence,sessionId;
    int frame;
    long playClock,rateClock;
    int playFrom,rateFrames;
    double measuredRate;
    public event EventHandler<PreviewFrame>? FrameReady;
    public event EventHandler<Exception>? Failed;
    public PreviewFrame? LastFrame=>Volatile.Read(ref lastFrame);
    public bool IsPlaying=>Volatile.Read(ref playRequested)!=0;

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
            playing=true;playFrom=frame;playClock=Stopwatch.GetTimestamp();ResetRate();
        }));
    }

    public void Pause()
    {
        if(Interlocked.Exchange(ref playRequested,0)==0)return;
        Observe(Enqueue(()=>
        {
            playing=false;
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
        playing=false;Interlocked.Exchange(ref playRequested,0);
        renderer?.Dispose();renderer=null;
        // A context must be created after the old context is disposed on this thread.
        // Creating both and disposing the old one could silently unbind the new WGL one.
        renderer=factory();renderer.Duration=duration;
        sessionId++;frame=0;ResetRate();
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
            int next=update.Time.HasValue?FrameAt(update.Time.Value):frame;
            Render(next,update.SceneEdits.Values.ToArray(),replay:update.Time.HasValue);
            if(playing){playFrom=frame;playClock=Stopwatch.GetTimestamp();}
            foreach(var waiter in update.Waiters)waiter.TrySetResult();
        }
        catch(Exception exception)
        {
            foreach(var waiter in update.Waiters)waiter.TrySetException(exception);
            throw;
        }
    }

    int FrameAt(double time)=>checked((int)Math.Floor(Math.Min(time,renderer!.Duration)*renderer.Fps+1e-8));
    void RequireRenderer(){if(renderer is null)throw new InvalidOperationException("Load a project into the native preview first.");}

    void Render(int next,IReadOnlyList<JsonObject> edits,bool replay=false)
    {
        var active=renderer!;long stamp=Stopwatch.GetTimestamp();
        if(active is SceneState scene&&(next<frame||replay&&scene.NeedsReplay))
        {
            // Native ADV has a stateful interpreter and physics. Reconstruct only for
            // a backward seek; normal playback and live edits retain models/context.
            var source=scene.Source;double duration=active.Duration;
            renderer=null;active.Dispose();
            active=renderer=new SceneState(source){Duration=duration};sessionId++;
        }
        var pixels=active.Render(next,edits);
        frame=next;
        Publish(pixels,Stopwatch.GetElapsedTime(stamp).TotalMilliseconds);
    }

    void Publish(Pixels pixels,double milliseconds)
    {
        long now=Stopwatch.GetTimestamp();
        if(playing)
        {
            rateFrames++;
            double elapsed=Stopwatch.GetElapsedTime(rateClock,now).TotalSeconds;
            if(elapsed>=.5){measuredRate=rateFrames/elapsed;rateFrames=0;rateClock=now;}
        }
        var active=renderer!;
        var result=new PreviewFrame(++sequence,sessionId,frame,frame/(double)active.Fps,pixels,active.Targets(),
            milliseconds,playing?measuredRate:0,playing,active.Device);
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
                    int target=FrameAt(playFrom/(double)renderer.Fps+elapsed);
                    if(target>frame)Render(target,[]);
                    if(frame>=FrameAt(renderer.Duration))
                    {
                        playing=false;Interlocked.Exchange(ref playRequested,0);
                        Publish(LastFrame!.Pixels,0);
                        continue;
                    }
                    double next=(frame-playFrom+1)/(double)renderer.Fps-elapsed;
                    wake.WaitOne(Math.Clamp((int)Math.Ceiling(next*1000),1,34));
                }
                catch(Exception exception){ReportFailure(exception);}
            }
        }
        finally
        {
            playing=false;Interlocked.Exchange(ref playRequested,0);
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
        playing=false;Interlocked.Exchange(ref playRequested,0);
        // A failed native draw never replaces the last successfully rendered pixels.
        try{Failed?.Invoke(this,exception);}catch{ }
    }

    public ValueTask DisposeAsync()
    {
        lock(gate)
        {
            if(!closing){closing=true;wake.Set();}
        }
        return new(stopped.Task);
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
        public abstract int Fps{get;}
        public abstract string Device{get;}
        public abstract Pixels Render(int frame,IReadOnlyList<JsonObject> edits);
        public abstract IReadOnlyList<PreviewTarget> Targets();
        public abstract void Dispose();
    }

    sealed class SceneState : RendererState
    {
        readonly AdvStoryRenderer native;
        readonly bool clean;
        readonly DirectorProject project;
        public ScenePreviewSource Source{get;private set;}
        public bool NeedsReplay{get;private set;}
        public override int Fps=>30;
        public override string Device=>native.Device;
        public SceneState(ScenePreviewSource source)
        {
            Source=source;
            project=DirectorProject.Read(source.ProjectPath);
            clean=project.Output["clean"]?.GetValue<bool>()??false;
            native=new(new Archive(source.ArchivePath),project.Site,source.CubismPath,project.Adv,project.Language,
                source.Width,source.Height,source.ProjectPath,source.FfmpegPath,1);
        }
        public void Commit(ScenePreviewSource source)
        {
            ArgumentNullException.ThrowIfNull(source);
            var next=DirectorProject.Read(source.ProjectPath);
            if(next.Adv!=project.Adv||next.Site!=project.Site||next.Language!=project.Language||source.Width!=Source.Width||source.Height!=Source.Height
                ||source.ArchivePath!=Source.ArchivePath||source.CubismPath!=Source.CubismPath||source.FfmpegPath!=Source.FfmpegPath
                ||(next.Output["clean"]?.GetValue<bool>()??false)!=clean||Archive.S(next.Output,"letterbox")!=Archive.S(project.Output,"letterbox"))
                throw new ArgumentException("Changing source, viewport or presentation requires reloading the scene.");
            if(!double.IsFinite(source.Duration)||source.Duration<=0||source.Duration>36000)throw new ArgumentOutOfRangeException(nameof(source));
            Source=source;Duration=source.Duration;NeedsReplay=true;
        }
        public override Pixels Render(int frame,IReadOnlyList<JsonObject> edits)=>native.Frame(frame,clean,edits);
        public override IReadOnlyList<PreviewTarget> Targets()=>native.PreviewTargets();
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
        public HomeState(HomePreviewSource source)
        {
            source.Project.Validate();
            var project=source.Project;
            var scene=new HomeCatalog(new Archive(source.ArchivePath)).Open(project.Source.SpotId,project.Source.Site,project.Source.AdvId);
            var character=string.IsNullOrWhiteSpace(source.Character)?null:scene.SelectCharacter(source.Character);
            animation=new(new HomeResources(scene),character);fps=project.Output.Fps;
            native=new(animation,source.Width,source.Height,1,HomeTiming.Plan(project.Duration,fps:fps),
                project.EditState.Framing,project.EditState.Background,project.EditState);
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
