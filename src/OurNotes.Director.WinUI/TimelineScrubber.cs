namespace OurNotes.Director.WinUI;

/// <summary>Owned by the window thread. Keep only the latest position while replay is busy.</summary>
internal sealed class TimelineScrubber(Func<double, Task> seek, Action<Exception> failed)
{
    double? pending;
    Task completion = Task.CompletedTask;
    bool running;
    internal bool IsBusy => running;
    internal void Request(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) return;
        pending = seconds;
        if (running) return;
        running = true; completion = DrainAsync();
    }
    internal void Cancel() => pending = null;
    internal Task FlushAsync() => completion;
    async Task DrainAsync()
    {
        try
        {
            await Task.Delay(20);
            while (pending is { } time) { pending = null; await seek(time); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { pending = null; failed(error); }
        finally { running = false; }
    }
}
