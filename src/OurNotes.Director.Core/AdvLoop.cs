using System.Runtime.CompilerServices;

namespace OurNotes.Director.Core;

// Port of ournotes-player engine/loop.js. All continuations run on the owning thread.
// No Task.Delay, wall-clock sampling, browser runtime, or thread-pool frame scheduling.
public sealed class AdvLoop
{
    private readonly int owner = Environment.CurrentManagedThreadId;
    private readonly Queue<Action> continuations = new();
    private readonly List<DelayEntry> delays = [];
    private readonly List<(int Target, AdvWait Wait)> frames = [];
    private List<AdvWait> update = [], postLate = [];
    private readonly Dictionary<AdvPhase, List<Action>> hooks = Enum.GetValues<AdvPhase>().ToDictionary(p => p, _ => new List<Action>());
    private bool stepping;
    public int Frame { get; private set; }
    public float Time { get; private set; }
    public double DeltaTime { get; private set; }
    public double TimeScale { get; set; } = 1;
    public void VerifyThread() { if (owner != Environment.CurrentManagedThreadId) throw new InvalidOperationException("ADV simulation must remain on its owning thread"); }
    public void On(AdvPhase phase, Action action) { VerifyThread(); hooks[phase].Add(action); }
    internal void Post(Action action) { VerifyThread(); continuations.Enqueue(action); }
    public void Drain()
    {
        VerifyThread(); int count = 0;
        while (continuations.TryDequeue(out var action))
        {
            if (++count > 100000) throw new InvalidOperationException("ADV continuation budget exceeded; possible instantaneous script loop");
            action();
        }
    }
    public AdvWait Ready(bool result = true) { var w = new AdvWait(this); w.Resolve(result); return w; }
    public AdvWait All(params AdvWait[] waits)
    {
        VerifyThread();if(waits.Length==0)return Ready();
        var all=new AdvWait(this);int remaining=waits.Length;bool ok=true;
        foreach(var wait in waits)wait.OnCompleted(()=>{ok&=wait.GetResult();if(--remaining==0)all.Resolve(ok);});
        return all;
    }
    public AdvWait YieldUpdate() { VerifyThread(); var w = new AdvWait(this); update.Add(w); return w; }
    public AdvWait YieldPostLate() { VerifyThread(); var w = new AdvWait(this); postLate.Add(w); return w; }
    public AdvWait DelayFrames(int count)
    {
        VerifyThread(); if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        var w = new AdvWait(this); frames.Add((checked(Frame + count), w)); return w;
    }
    public static float DelaySeconds(double seconds)
    {
        if (!double.IsFinite(seconds) || Math.Abs(seconds * 1000) > 922337203685477) throw new ArgumentOutOfRangeException(nameof(seconds));
        return (float)(Math.Truncate(seconds * 1000 + (seconds >= 0 ? .5 : -.5)) * 10000 * 1e-7);
    }
    public AdvWait Delay(double seconds, object? group = null)
    {
        VerifyThread(); if (DelaySeconds(seconds) < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        var w = new AdvWait(this); delays.Add(new(seconds, Frame, w, group)); return w;
    }
    public void CancelDelays(object group, bool result)
    {
        VerifyThread();
        var pending = delays.Where(d => ReferenceEquals(d.Group, group)).ToArray();
        delays.RemoveAll(d => ReferenceEquals(d.Group, group));
        if (pending.Length == 0) return;
        YieldUpdate().OnCompleted(() => { foreach (var d in pending) d.Wait.Resolve(result); });
    }
    public void Step()
    {
        VerifyThread(); if (stepping) throw new InvalidOperationException("Reentrant ADV frame");
        if (!double.IsFinite(TimeScale) || TimeScale < 0) throw new ArgumentOutOfRangeException(nameof(TimeScale));
        stepping = true;
        try
        {
            Frame++; DeltaTime = TimeScale == 1 ? 1.0 / 30 : 1.0 / 30 * TimeScale; Time = (float)(Time + DeltaTime);
            var ys = update; update = []; foreach (var w in ys) w.Resolve(true);
            for(int i=0;i<delays.Count;)
            {
                var d=delays[i];
                if (d.Elapsed == 0 && d.Created == Frame) {i++;continue;}
                d.Elapsed = (float)(d.Elapsed + (float)DeltaTime);
                if (DelaySeconds(d.Seconds) <= d.Elapsed) { delays.RemoveAt(i); d.Wait.Resolve(true); }
                else i++;
            }
            for(int i=0;i<frames.Count;){var w=frames[i];if(Frame>=w.Target){frames.RemoveAt(i);w.Wait.Resolve(true);}else i++;}
            Drain(); Run(AdvPhase.Update); Drain(); Run(AdvPhase.Tweens); Drain();
            Run(AdvPhase.Animation); Run(AdvPhase.LateUpdate); Run(AdvPhase.PreLateEnd); Run(AdvPhase.PostLate);
            ys = postLate; postLate = []; foreach (var w in ys) w.Resolve(true);
            Drain(); Run(AdvPhase.Render);
        }
        finally { stepping = false; }
    }
    private void Run(AdvPhase phase) { var phaseHooks=hooks[phase];int count=phaseHooks.Count;for(int i=0;i<count;i++)phaseHooks[i](); }
    private sealed class DelayEntry(double seconds, int created, AdvWait wait, object? group)
    {
        public double Seconds { get; } = seconds;
        public int Created { get; } = created;
        public AdvWait Wait { get; } = wait;
        public object? Group { get; } = group;
        public float Elapsed;
    }
}
public enum AdvPhase { Update, Tweens, Animation, LateUpdate, PreLateEnd, PostLate, Render }

// Awaiting an already resolved signal still queues a continuation, like a JS promise.
public sealed class AdvWait(AdvLoop loop) : INotifyCompletion
{
    private bool resolved, result;
    private readonly List<Action> waiting = [];
    public AdvWait GetAwaiter() => this;
    public bool IsCompleted => false;
    public bool GetResult() { if (!resolved) throw new InvalidOperationException("Pending ADV wait"); return result; }
    public void OnCompleted(Action action) { loop.VerifyThread(); if (resolved) loop.Post(action); else waiting.Add(action); }
    internal void Resolve(bool value)
    {
        loop.VerifyThread(); if (resolved) return;
        resolved = true; result = value;
        foreach (var action in waiting) loop.Post(action); waiting.Clear();
    }
}
