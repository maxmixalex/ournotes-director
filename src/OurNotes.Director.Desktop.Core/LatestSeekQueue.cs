namespace OurNotes.Director.Desktop.Core;

/// <summary>Serial preview seeks with latest-waiting-target coalescing. An ordinary
/// pointer sample never cancels an in-flight frame: that frame must reach the monitor
/// even while the pointer is moving. Only leaving/replacing the document cancels it.
/// Continuations retain the caller's synchronization context for UI-owned renderers.</summary>
public sealed class LatestSeekQueue(Func<double, CancellationToken, Task> seek, Action<Exception> failed)
{
    readonly object gate = new();
    double? pending;
    CancellationTokenSource? active;
    TaskCompletionSource completion = Completed();
    bool running;
    public bool IsBusy { get { lock (gate) return running; } }
    static TaskCompletionSource Completed()
    {
        var value = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        value.SetResult(); return value;
    }
    public void Request(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) return;
        TaskCompletionSource? start = null;
        lock (gate)
        {
            pending = seconds;
            if (!running)
            {
                running = true;
                completion = start = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
        if (start is not null) _ = DrainAsync(start);
    }
    public void Cancel()
    {
        lock (gate) { pending = null; active?.Cancel(); }
    }
    public Task FlushAsync() { lock (gate) return completion.Task; }
    async Task DrainAsync(TaskCompletionSource batch)
    {
        await Task.Yield();
        while (true)
        {
            double time; CancellationTokenSource request;
            lock (gate)
            {
                if (pending is not { } next) { running = false; batch.TrySetResult(); return; }
                time = next; pending = null; active = request = new();
            }
            try { await seek(time, request.Token); }
            catch (OperationCanceledException) when (request.IsCancellationRequested) { }
            catch (Exception error)
            {
                try { failed(error); }
                catch (Exception observerError)
                {
                    lock (gate) { pending = null; running = false; }
                    batch.TrySetException(observerError); return;
                }
            }
            finally
            {
                lock (gate) { if (ReferenceEquals(active, request)) active = null; request.Dispose(); }
            }
        }
    }
}
