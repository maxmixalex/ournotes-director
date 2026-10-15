namespace OurNotes.Director.WinUI;

/// <summary>Window-thread seek coordinator. Superseded work is cancelled at a complete native
/// simulation step; only the newest target may publish a replacement image.</summary>
internal sealed class TimelineScrubber(Func<double, CancellationToken, Task> seek, Action<Exception> failed)
{
    double? pending;
    Task completion = Task.CompletedTask;
    CancellationTokenSource? active;
    bool running;
    internal bool IsBusy => running;

    internal void Request(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) return;
        pending = seconds;
        active?.Cancel();
        if (running) return;
        running = true;
        completion = DrainAsync();
    }

    internal void Cancel() { pending = null; active?.Cancel(); }
    internal Task FlushAsync() => completion;

    async Task DrainAsync()
    {
        try
        {
            await Task.Delay(20);
            while (pending is { } time)
            {
                pending = null;
                using var request = new CancellationTokenSource();
                active = request;
                try { await seek(time, request.Token); }
                catch (OperationCanceledException) when (request.IsCancellationRequested) { }
                finally { active = null; }
            }
        }
        catch (OperationCanceledException) { pending = null; }
        catch (Exception error) { pending = null; failed(error); }
        finally { running = false; }
    }
}
