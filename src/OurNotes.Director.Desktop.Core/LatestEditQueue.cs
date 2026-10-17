namespace OurNotes.Director.Desktop.Core;

/// <summary>Coalesces waiting pointer samples without overlapping asynchronous edits.
/// A terminal sample seals its batch: a following gesture cannot replace mouse-up.
/// Post on the owning UI context; continuations deliberately stay on that context.</summary>
public sealed class LatestEditQueue<T>(Func<T, Task> apply)
{
    sealed class Batch(T value)
    {
        public T Value = value;
        public readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    readonly object gate = new();
    readonly Queue<Batch> waiting = new();
    Batch? tail;
    bool running;
    public Task Post(T value, bool terminal = false)
    {
        Batch item; bool start;
        lock (gate)
        {
            if (tail is null) { tail = new(value); waiting.Enqueue(tail); }
            else tail.Value = value;
            item = tail;
            if (terminal) tail = null;
            start = !running; running = true;
        }
        if (start) _ = DrainAsync();
        return item.Completion.Task;
    }
    async Task DrainAsync()
    {
        while (true)
        {
            Batch item;
            lock (gate)
            {
                if (!waiting.TryDequeue(out item!)) { running = false; return; }
                if (ReferenceEquals(tail, item)) tail = null;
            }
            try { await apply(item.Value); item.Completion.TrySetResult(); }
            catch (OperationCanceledException) { item.Completion.TrySetCanceled(); }
            catch (Exception error) { item.Completion.TrySetException(error); }
        }
    }
}
