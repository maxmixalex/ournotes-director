namespace OurNotes.Director.WinUI;

internal sealed partial class HomeStudioPage
{
    Microsoft.UI.Dispatching.DispatcherQueueTimer? presentationTimer;
    bool fullscreenPreviewSized;

    void InitializePresentationSize()
    {
        presentationTimer = DispatcherQueue.CreateTimer(); presentationTimer.IsRepeating = false;
        presentationTimer.Interval = TimeSpan.FromMilliseconds(120);
        presentationTimer.Tick += (_, _) => _ = SafeAsync(ApplyPresentationSizeAsync);
        viewport.PresentationSizeChanged += (_, _) =>
        {
            if (!active || disposed || document is null || !viewport.HasCurrentSource || viewport.IsCompositionFixture
                || !viewport.IsFullscreenRendering && !fullscreenPreviewSized) return;
            presentationTimer.Stop(); presentationTimer.Start();
        };
    }

    internal async Task ApplyPresentationSizeAsync()
    {
        presentationTimer?.Stop();
        if (!active || disposed || document is null || !viewport.HasCurrentSource || viewport.IsCompositionFixture
            || !viewport.IsFullscreenRendering && !fullscreenPreviewSized) return;
        var owner = document; long version = loadVersion;
        await previewGate.WaitAsync(lifetime.Token);
        try
        {
            if (!active || disposed || !viewport.HasCurrentSource || !ReferenceEquals(document, owner)
                || version != loadVersion) return;
            var output = owner.Project.Output;
            bool fullscreen = viewport.IsFullscreenRendering;
            if (fullscreen) fullscreenPreviewSized = true;
            var normal = PreviewSize(output.Width, output.Height);
            var size = fullscreen ? viewport.RecommendedPreviewSize(output.Width, output.Height, 1)
                : viewport.WindowedPreviewSize ?? (normal.Width, normal.Height, 1);
            if (viewport.LastFrame is not { } frame || frame.Pixels.Width != size.Width
                || frame.Pixels.Height != size.Height || viewport.PreviewSupersample != size.Supersample)
                await viewport.ResizePreviewAsync(size.Width, size.Height, size.Supersample, lifetime.Token);
            fullscreenPreviewSized = fullscreen;
            if (fullscreen != viewport.IsFullscreenRendering) { presentationTimer!.Stop(); presentationTimer.Start(); }
        }
        finally { previewGate.Release(); }
    }
}
