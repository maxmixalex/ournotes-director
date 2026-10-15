using OurNotes.Director.Desktop.Core;
using OurNotes.Director.WinUI;

namespace OurNotes.Director.Desktop.Tests;

static class TimelineEditingTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var full = TimelineRange.Full(4, 30);
        check(full.FrameCount == 121 && full.End == 121d / 30, "Full range includes the displayed terminal pose exactly once");
        var marked = full.MarkIn(1).MarkOut(2);
        check(marked.StartFrame == 30 && marked.EndFrameExclusive == 61 && marked.Duration == 31d / 30,
            "I/O marks define sample duration and include the marked out frame");
        var one = full.MarkIn(2).MarkOut(2);
        check(one.FrameCount == 1, "Marking the same frame in and out makes a valid one-frame range");
        var clip = TimelineRange.ForClip(1, 1, 30, 4);
        check(clip.StartFrame == 30 && clip.EndFrameExclusive == 60, "A clip's end is exclusive, unlike a marked out-frame");
        check(marked.ClampTo(TimelineRange.Full(.5, 30)).FrameCount == 1,
            "Shortening a project clamps existing marks without creating an invalid empty range");
        check(TimelineRange.Full(36000, 120).EndFrameExclusive == 4320000,
            "The maximum project boundary remains within the native ten-hour limit");
        foreach (double bad in new[] { double.NaN, double.PositiveInfinity, -1d })
        {
            bool rejected = false;
            try { full.MarkIn(bad); } catch (ArgumentOutOfRangeException) { rejected = true; }
            check(rejected, "Invalid marker input is rejected without mutating an existing range");
        }

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool cancelled = false; double reached = -1; Exception? failure = null;
        var coordinator = new TimelineScrubber(async (at, token) =>
        {
            if (at == 100)
            {
                started.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { cancelled = true; throw; }
            }
            token.ThrowIfCancellationRequested(); reached = at;
        }, error => failure = error);
        coordinator.Request(100);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        coordinator.Request(2);
        await coordinator.FlushAsync().WaitAsync(TimeSpan.FromSeconds(3));
        check(cancelled && reached == 2 && failure is null && !coordinator.IsBusy,
            "A newer scrub target interrupts old replay instead of waiting for it to finish");
        reached = -1;
        coordinator.Request(10); coordinator.Request(20); coordinator.Request(30);
        await coordinator.FlushAsync().WaitAsync(TimeSpan.FromSeconds(3));
        check(reached == 30, "Requests inside the input debounce execute only the latest target");
        reached = -1; coordinator.Request(50); coordinator.Cancel();
        await coordinator.FlushAsync().WaitAsync(TimeSpan.FromSeconds(3));
        check(reached == -1 && !coordinator.IsBusy, "Leaving a workspace discards pending scrubs");
    }
}
