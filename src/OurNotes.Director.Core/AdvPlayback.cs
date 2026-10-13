using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

public sealed class AdvJob
{
    private readonly IEnumerator<AdvWait> routine;
    private readonly AdvLoop loop;
    public AdvWait Completed { get; }
    public bool Finished { get; private set; }
    public int? EndFrame { get; private set; }
    public Exception? Error { get; private set; }
    public AdvJob(AdvLoop loop, IEnumerable<AdvWait> routine)
    {
        this.loop=loop; this.routine=routine.GetEnumerator(); Completed=new(loop);
        Advance();
    }
    private void Advance()
    {
        if(Finished)return;
        try
        {
            if(routine.MoveNext())routine.Current.OnCompleted(Advance);
            else { Finished=true;EndFrame=loop.Frame;routine.Dispose();Completed.Resolve(true); }
        }
        catch(Exception ex){Error=ex;Finished=true;EndFrame=loop.Frame;routine.Dispose();Completed.Resolve(false);}
    }
    public void ThrowIfFailed(){if(Error is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(Error).Throw();}
}

// Native ADV interpreter. Presentation handlers are bound explicitly; missing handlers refuse playback.
// This is not a duration-only trace: each handler owns its awaited/forked work and can overlap other rows.
public sealed class AdvPlayback
{
    public delegate IEnumerable<AdvWait> Handler(AdvRow row, AdvPlayback player);
    private readonly Dictionary<string,Handler> handlers=new(StringComparer.Ordinal);
    private readonly Dictionary<string,int> keys=new(StringComparer.Ordinal);
    private readonly List<AdvJob> detached=[];
    private readonly object delayGroup=new();
    private readonly object playbackDelayGroup=new();
    private readonly AdvProgram program;
    private readonly List<ObservedCommand> observed=[];
    private AdvJob? main;
    private int nextStep;
    private bool auto;
    private float speed=1;
    public AdvLoop Loop{get;}
    public AdvProgram Program=>program;
    public int ListIndex{get;private set;}=-1;
    public bool Cancelled{get;private set;}
    public bool Finished=>main?.Finished??false;
    public int? CompletionFrame=>main?.EndFrame;
    public IReadOnlyList<NativeCommandTiming> CommandTimings()=>observed.Select((item,ordinal)=>
        new NativeCommandTiming(item.Section,item.Index,item.Row.Command,item.StartFrame,
            item.Job?.EndFrame,ordinal,item.Row.Text("Key"))).ToArray();
    public bool Paused{get;set;}
    public bool ForcedAuto{get;private set;}
    public bool AutoPlay=>auto||ForcedAuto;
    public float Speed
    {
        get=>speed;
        set{if(!float.IsFinite(value)||value<=0||value>3)throw new ArgumentOutOfRangeException(nameof(value));speed=value;}
    }
    public Action<AdvRow,int>? OnCommand{get;set;}
    public Action? OnStop{get;set;}
    public Func<AdvPlayback,IEnumerable<AdvWait>>? Prepare{get;set;}
    public Func<AdvRow,AdvPlayback,IEnumerable<AdvWait>>? VideoDelay{get;set;}
    public string[] SupportedCommands=>handlers.Keys.Order().ToArray();
    public string[] MissingCommands=>program.RequiredCommands.Where(c=>!handlers.ContainsKey(c)).ToArray();
    public AdvPlayback(AdvLoop loop,AdvProgram program,bool autoPlay=true)
    {
        Loop=loop;this.program=program;auto=autoPlay;
        for(int i=0;i<program.Rows.Length;i++){var key=program.Rows[i].Text("Key");if(key.Length>0)keys.TryAdd(key,i);}
        Register("Delay",(c,p)=>FixedDelayFrames(c) is int frames?p.DelayFrames(frames):p.VideoDelay?.Invoke(c,p)??p.DelayAdjusted(p.CalcDuration(c.Number("Duration"))));
        Register("Wait",WaitCommand);
        Register("CancelDelay",(c,p)=>Immediate(()=>p.Loop.CancelDelays(p.delayGroup,true)));
        Register("ForceAuto",(c,p)=>Immediate(()=>p.ForcedAuto=!p.ForcedAuto));
        Register("ChoiceSet",(c,p)=>[]);
        Register("GoTo",(c,p)=>Immediate(()=>{if(p.keys.TryGetValue(c.Text("Parameter1"),out int index))p.ListIndex=index;}));
    }
    private static IEnumerable<AdvWait> Immediate(Action action){action();yield break;}
    public void Register(string command,Handler handler)
    {
        Loop.VerifyThread();if(main is not null)throw new InvalidOperationException("Cannot change command registry during playback");
        if(!AdvProgram.KnownCommand(command))throw new ArgumentException("Unknown ADV command");
        if(!handlers.TryAdd(command,handler))throw new ArgumentException("Command already registered: "+command);
    }
    public void Start()
    {
        Loop.VerifyThread();if(main is not null)throw new InvalidOperationException("Playback already started");
        if(program.PlaybackMode!=0)throw new NotSupportedException("Only normal ADV is supported; SimpleADV home/result dialogue is excluded from this tool's scope");
        var missing=MissingCommands;if(missing.Length>0)throw new NotSupportedException("Native presentation handlers missing: "+string.Join(", ",missing));
        if(Prepare is null)throw new InvalidOperationException("A presentation backend must bind Prepare (load UI/scene state and fade letterbox)");
        main=new(Loop,Play());Loop.Drain();ThrowIfFailed();
    }
    public void Step(){Loop.Step();ThrowIfFailed();}
    public void ThrowIfFailed(){main?.ThrowIfFailed();foreach(var job in detached)job.ThrowIfFailed();detached.RemoveAll(j=>j.Finished);}
    public void SetAuto(bool value){auto=value;if(AutoPlay&&nextStep==1)nextStep=2;else if(!AutoPlay&&nextStep==2)nextStep=1;}
    public void Tap(){if(!ForcedAuto&&nextStep==1)nextStep=2;}
    public void Stop(){Cancelled=true;Loop.CancelDelays(delayGroup,false);Loop.CancelDelays(playbackDelayGroup,false);}
    public float CalcDuration(float duration,float fallback=0)=>(duration!=0?Math.Max(duration,0):fallback)/Speed;
    // Explicit editor frame waits do not change the original game's duration/video timing.
    public static int? FixedDelayFrames(AdvRow row)
    {
        if(row.Command!="Delay"||row.Data is not JsonObject fields||!fields.ContainsKey("Frames"))return null;
        if(row.Data["Frames"] is not JsonValue value||!value.TryGetValue<int>(out int frames)||frames<0||frames>30*36000)
            throw new ArgumentException("Delay.Frames must be an integer between 0 and 1080000");
        if(row.Data["Duration"] is not null&&row.Number("Duration")!=0)
            throw new ArgumentException("Delay must choose Frames or a nonzero Duration, not both");
        return frames;
    }
    private IEnumerable<AdvWait> DelayFrames(int frames){if(frames>0)yield return Loop.DelayFrames(frames);}
    public AdvWait CommonDelay(double seconds)=>seconds>0?Loop.Delay(seconds,delayGroup):Loop.Ready();
    public AdvWait NoWait(AdvRow row,IEnumerable<AdvWait> routine)
    {
        var job=new AdvJob(Loop,routine);detached.Add(job);
        return row.Flag("IsNoWait")?Loop.Ready():job.Completed;
    }
    public IEnumerable<AdvWait> DelayAdjusted(float duration)
    {
        float remaining=duration,previous=Speed;
        while(remaining>0&&!Cancelled)
        {
            if(Paused){do{yield return Loop.YieldUpdate();}while(Paused&&!Cancelled);continue;}
            if(Speed!=previous){remaining=(float)(remaining*(float)(previous/Speed));previous=Speed;}
            yield return Loop.YieldUpdate();
            remaining=(float)(remaining-(float)Loop.DeltaTime);
        }
    }
    private static IEnumerable<AdvWait> WaitCommand(AdvRow row,AdvPlayback p)
    {
        p.nextStep=p.AutoPlay?2:1;
        do{yield return p.Loop.YieldUpdate();}while(!p.Cancelled&&p.nextStep!=2);
        if(p.Cancelled||!p.AutoPlay)yield break;
        yield return p.Loop.Delay(p.CalcDuration(p.program.AutoWaitSeconds),p.playbackDelayGroup);
    }
    private AdvJob Execute(AdvRow row,string? section=null,int index=-1)
    {
        OnCommand?.Invoke(row,Loop.Frame);
        ObservedCommand? item=null;
        if(section is not null){item=new(section,index,row,Loop.Frame);observed.Add(item);}
        var job=new AdvJob(Loop,handlers[row.Command](row,this));
        if(item is not null)item.Job=job;
        return job;
    }
    private sealed class ObservedCommand(string section,int index,AdvRow row,int startFrame)
    {
        public string Section{get;}=section;
        public int Index{get;}=index;
        public AdvRow Row{get;}=row;
        public int StartFrame{get;}=startFrame;
        public AdvJob? Job{get;set;}
    }
    private IEnumerable<AdvWait> Play()
    {
        var prepared=new AdvJob(Loop,Prepare!(this));yield return prepared.Completed;prepared.ThrowIfFailed();
        if(Cancelled){OnStop?.Invoke();yield break;}
        var initial=Execute(new("-1","TalkWindow",new JsonObject{["TargetName"]="UIDefaultTalkWindow"}));
        detached.Add(initial); // The reference starts this without awaiting it.
        for(int i=0;i<program.Initialize.Length;i++)
        {
            if(Cancelled){OnStop?.Invoke();yield break;}
            var row=program.Initialize[i];if(row.Ignored)continue;
            var job=Execute(row,"initialize",i);yield return job.Completed;job.ThrowIfFailed();
        }
        ListIndex=0;
        while(ListIndex<program.Rows.Length)
        {
            if(Cancelled){OnStop?.Invoke();yield break;}
            var index=ListIndex;var row=program.Rows[index];
            if(row.Ignored){ListIndex++;continue;}
            var job=Execute(row,"commands",index);yield return job.Completed;job.ThrowIfFailed();
            if(index==ListIndex)ListIndex++;
            while(Paused&&!Cancelled)yield return Loop.YieldUpdate();
        }
        if(Cancelled){OnStop?.Invoke();yield break;}
        for(int i=0;i<program.Finalize.Length;i++){
            var row=program.Finalize[i];if(row.Ignored)continue;
            var job=Execute(row,"finalize",i);yield return job.Completed;job.ThrowIfFailed();
        }
        OnStop?.Invoke();
    }
}
