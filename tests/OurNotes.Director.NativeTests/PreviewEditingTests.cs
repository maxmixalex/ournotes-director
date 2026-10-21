using System.Collections.Concurrent;
using System.Diagnostics;
using OurNotes.Director.Core;

static class PreviewEditingTests
{
    public static async Task<int> RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "ournotes-seek-" + Guid.NewGuid().ToString("N"));
        int checks = 0;
        void Check(bool result, string label)
        { if (!result) throw new InvalidOperationException(label); checks++; Console.WriteLine(label); }
        try
        {
            NativeTestGraphics.Configure();
            string path = RealtimeSceneFixture.Write(root);
            await Task.Run(() =>
            {
                Pixels after;
                using (var cancelled = new AdvStoryRenderer(new Archive(root), "intl", "", "43", "zh-Hans", 128, 96, path, "", 1))
                {
                    using var token = new CancellationTokenSource();
                    cancelled.Loop.On(AdvPhase.PostLate, () => { if (cancelled.Loop.Frame == 10) token.Cancel(); });
                    bool interrupted = false;
                    try { cancelled.Frame(300, true, cancellationToken: token.Token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { interrupted = true; }
                    Check(interrupted && cancelled.Loop.Frame == 10, "Native replay stops at a complete simulation step, before drawing the stale target");
                    after = cancelled.Frame(12, true);
                }
                using var reference = new AdvStoryRenderer(new Archive(root), "intl", "", "43", "zh-Hans", 128, 96, path, "", 1);
                Check(after.Rgba.SequenceEqual(reference.Frame(12, true).Rgba), "Continuing after cancelled simulation yields identical actual pixels");
            });

            await using var session = new RealtimePreviewSession();
            var failures = new ConcurrentQueue<Exception>(); session.Failed += (_, error) => failures.Enqueue(error);
            await session.LoadSceneAsync(new(root, path, "", "", 128, 96, 2000));
            var initial = session.LastFrame!;
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.CancelAfter(25); bool interrupted = false; var watch = Stopwatch.StartNew();
                try { await session.SeekAsync(1999, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { interrupted = true; }
                Check(interrupted && ReferenceEquals(session.LastFrame, initial) && failures.IsEmpty,
                    "Superseded session seeks leave the last displayed picture intact and emit no error");
                Console.WriteLine($"Cancelled a 59,970-frame target in {watch.Elapsed.TotalMilliseconds:0.0} ms (software test environment).");
            }
            await session.SeekAsync(.1);
            Check(session.LastFrame!.Frame == 3 && failures.IsEmpty,
                "Seeking backwards from an unpublished partial state reconstructs correctly");
            var frame3 = session.LastFrame.Pixels;
            await session.SeekAsync(0); await session.SeekAsync(.1);
            Check(frame3.Rgba.SequenceEqual(session.LastFrame!.Pixels.Rgba), "Cancellation recovery is deterministic at the same selected frame");
            await session.SeekAsync(0);
            var ended = new TaskCompletionSource<PreviewFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            int maximum = 0;
            session.FrameReady += (_, f) =>
            {
                maximum = Math.Max(maximum, f.Frame);
                if (!f.IsPlaying && f.Frame == 5) ended.TrySetResult(f);
            };
            session.Play(.2);
            var last = await ended.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(last.Frame == 5 && maximum == 5 && !session.IsPlaying,
                "Native playback stops at the selected exclusive out boundary, without overshooting the work area");
            var timing=await session.ResolveSceneTimingAsync().WaitAsync(TimeSpan.FromSeconds(20));
            var pose=session.LastFrame!;
            var independent=await Task.Factory.StartNew(()=>StoryTimingAnalysis.Analyze(new(root,path,"","",128,96,2000)),
                CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default).WaitAsync(TimeSpan.FromSeconds(20));
            Check(independent.IsComplete&&independent.ProjectHash==timing.ProjectHash&&independent.EndFrame==timing.EndFrame
                &&independent.Duration==timing.Duration&&independent.Commands.SequenceEqual(timing.Commands),
                "Standalone import analysis matches background preview analysis command-for-command, including actual end frame");
            Check(session.LastFrame!.SessionId==pose.SessionId&&session.LastFrame.Frame==pose.Frame
                &&session.LastFrame.Pixels.Rgba.SequenceEqual(pose.Pixels.Rgba),
                "Standalone analysis does not replace the existing program monitor or move its paused pose");
            using(var cancelled=new CancellationTokenSource())
            {
                cancelled.Cancel();bool stopped=false;
                try{StoryTimingAnalysis.Analyze(new(root,"missing-project.json","",""),cancelled.Token);}
                catch(OperationCanceledException){stopped=true;}
                Check(stopped,"A cancelled import is rejected before file IO or native resource initialization");
            }
            Console.WriteLine($"Passed {checks} native seek cancellation, recovery, playback-range and story-analysis checks.");
            return 0;
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
