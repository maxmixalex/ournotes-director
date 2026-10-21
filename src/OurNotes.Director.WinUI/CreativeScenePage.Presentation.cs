namespace OurNotes.Director.WinUI;

internal sealed partial class CreativeScenePage
{
    Microsoft.UI.Dispatching.DispatcherQueueTimer? presentationTimer;
    bool fullscreenPreviewSized;

    void InitializePresentationSize()
    {
        presentationTimer = DispatcherQueue.CreateTimer(); presentationTimer.IsRepeating = false;
        presentationTimer.Interval = TimeSpan.FromMilliseconds(120);
        presentationTimer.Tick += (_, _) => _ = Safe(ApplyPresentationSizeAsync);
        viewport.PresentationSizeChanged += (_, _) =>
        {
            if (disposed || !IsLoaded || !viewport.HasCurrentSource || viewport.IsCompositionFixture
                || !viewport.IsFullscreenRendering && !fullscreenPreviewSized) return;
            presentationTimer.Stop(); presentationTimer.Start();
        };
    }

    internal async Task ApplyPresentationSizeAsync()
    {
        presentationTimer?.Stop();
        if (disposed || !IsLoaded || !viewport.HasCurrentSource || importingStory || viewport.IsCompositionFixture
            || !viewport.IsFullscreenRendering && !fullscreenPreviewSized) return;
        var owner = document; long version = loadVersion;
        await loadGate.WaitAsync();
        try
        {
            if (disposed || !IsLoaded || !viewport.HasCurrentSource || importingStory
                || !ReferenceEquals(owner, document) || version != loadVersion) return;
            int width = (int)N(document.Canvas, "width"), height = (int)N(document.Canvas, "height");
            bool fullscreen = viewport.IsFullscreenRendering;
            if (fullscreen) fullscreenPreviewSized = true;
            var normal = PreviewSize(width, height);
            var size = fullscreen ? viewport.RecommendedPreviewSize(width, height, 1)
                : viewport.WindowedPreviewSize ?? (normal.Width, normal.Height, 1);
            // Fullscreen always renders the complete scene at its display resolution.
            // An unfinished low-quality scrub must not leave the monitor blurred.
            bool restoreQuality = fullscreen && (viewport.ScenePreviewScale != 1 || viewport.LastFrame?.ScenePreviewScale != 1);
            if (restoreQuality)
                await viewport.SetScenePreviewScaleAsync(1);
            if (restoreQuality || viewport.LastFrame is not { } frame || frame.Pixels.Width != size.Width
                || frame.Pixels.Height != size.Height || viewport.PreviewSupersample != size.Supersample)
                await viewport.ResizePreviewAsync(size.Width, size.Height, size.Supersample);
            fullscreenPreviewSized = fullscreen;
            if (fullscreen != viewport.IsFullscreenRendering) { presentationTimer!.Stop(); presentationTimer.Start(); }
        }
        finally { loadGate.Release(); }
    }
}
