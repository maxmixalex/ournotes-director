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
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var presented = new List<double>();
        bool cancelled = false; Exception? failure = null;
        var coordinator = new TimelineScrubber(async (at, token) =>
        {
            if (at == 100)
            {
                started.TrySetResult();
                try { await release.Task.WaitAsync(token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { cancelled = true; throw; }
            }
            token.ThrowIfCancellationRequested(); presented.Add(at);
        }, error => failure = error);
        coordinator.Request(100);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        coordinator.Request(2); coordinator.Request(3); coordinator.Request(4);
        check(!cancelled && coordinator.IsBusy, "New pointer samples do not cancel the in-flight native frame");
        release.SetResult(); await coordinator.FlushAsync().WaitAsync(TimeSpan.FromSeconds(3));
        check(presented.SequenceEqual(new double[] { 100, 4 }) && failure is null && !coordinator.IsBusy,
            "Active frame reaches the monitor before latest pending input, without starvation or a stale backlog");
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool interrupted = false;
        var leaving = new TimelineScrubber(async (_, token) =>
        {
            stopping.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { interrupted = true; throw; }
        }, error => failure = error);
        leaving.Request(1); await stopping.Task.WaitAsync(TimeSpan.FromSeconds(3)); leaving.Request(2); leaving.Cancel();
        await leaving.FlushAsync().WaitAsync(TimeSpan.FromSeconds(3));
        check(interrupted && !leaving.IsBusy && failure is null, "Leaving the workspace explicitly cancels replay and discards queued targets");
        var recovered = new List<double>(); int errors = 0;
        var faulty = new TimelineScrubber((at, _) => { if (at == 1) throw new IOException("fixture"); recovered.Add(at); return Task.CompletedTask; }, _ => errors++);
        faulty.Request(1); await faulty.FlushAsync(); faulty.Request(2); await faulty.FlushAsync();
        faulty.Request(double.NaN); faulty.Request(-1); faulty.Request(double.PositiveInfinity); await faulty.FlushAsync();
        check(errors == 1 && recovered.SequenceEqual(new double[] { 2 }), "Seek failure does not wedge future interaction and invalid samples are ignored");
    }
}
