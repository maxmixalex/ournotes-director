using OurNotes.Director.Core;

static class HomePreviewResolutionTests
{
    public static async Task<int> RunAsync()
    {
        NativeTestGraphics.Configure();
        string root = Path.Combine(Path.GetTempPath(), "ournotes-home-resize-" + Guid.NewGuid().ToString("N"));
        HomeRenderingTests.WriteFixture(root);
        int checks = 0;
        void Check(bool result, string label)
        { if (!result) throw new InvalidOperationException(label); checks++; Console.WriteLine(label); }
        try
        {
            var project = new HomeProject(new(77, "intl", 42))
            { Duration = 15, Output = new(128, 96, 30, 1), EditState = new() { Background = false } };
            await using var session = new RealtimePreviewSession();
            await session.LoadHomeAsync(new(root, project, Width: 128, Height: 96));
            await session.SeekAsync(.5);
            var small = session.LastFrame!;
            await session.ResizePreviewAsync(256, 192, 2);
            var enlarged = session.LastFrame!;
            Check(enlarged.Pixels.Width == 256 && enlarged.Pixels.Height == 192
                && enlarged.SessionId == small.SessionId && enlarged.Frame == small.Frame && !session.IsPlaying
                && enlarged.Targets.SequenceEqual(small.Targets),
                "Home fullscreen resize retains native animation, paused time and camera geometry");
            var reference = await Task.Run(() =>
            {
                var scene = new HomeCatalog(new Archive(root)).Open(77, "intl", 42);
                using var renderer = new HomeRenderer(new(new HomeResources(scene)), 256, 192, 2,
                    new(0, 450, 30), editState: project.EditState);
                return renderer.Frame(15);
            });
            Check(enlarged.Pixels.Rgba.SequenceEqual(reference.Rgba),
                "Resized home pixels equal a real high-resolution renderer instead of an enlarged low-resolution bitmap");
            await session.ResizePreviewAsync(128, 96, 1);
            Check(session.LastFrame!.Pixels.Rgba.SequenceEqual(small.Pixels.Rgba),
                "Restoring the home preview size restores its exact paused pixels");
            session.Play();
            var advanced = new TaskCompletionSource<PreviewFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnFrame(object? sender, PreviewFrame frame)
            { if (frame.IsPlaying && frame.Time > small.Time) advanced.TrySetResult(frame); }
            session.FrameReady += OnFrame;
            try
            {
                await session.ResizePreviewAsync(192, 144, 1);
                var playing = await advanced.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Check(playing.SessionId == small.SessionId && session.IsPlaying,
                    "Home native time advances through a fullscreen resize without restarting playback");
            }
            finally { session.FrameReady -= OnFrame; session.Pause(); }
            Console.WriteLine($"Passed {checks} native home resolution checks.");
            return 0;
        }
        finally { Directory.Delete(root, true); }
    }
}
