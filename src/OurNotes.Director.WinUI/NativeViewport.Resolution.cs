using Microsoft.UI.Xaml;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

namespace OurNotes.Director.WinUI;

public sealed partial class NativeViewport
{
    int logicalCanvasWidth, logicalCanvasHeight;
    XamlRoot? observedXamlRoot;
    double lastRasterizationScale;
    public event EventHandler? PresentationSizeChanged;
    public int PreviewSupersample { get; private set; } = 1;

    public void SetCanvasDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (logicalCanvasWidth == width && logicalCanvasHeight == height) return;
        logicalCanvasWidth = width; logicalCanvasHeight = height; LayoutCanvas();
    }

    public (int Width, int Height, int Supersample) RecommendedPreviewSize(int canvasWidth, int canvasHeight)
    {
        double width = surface.ActualWidth > 0 ? surface.ActualWidth : 960;
        double height = surface.ActualHeight > 0 ? surface.ActualHeight : 540;
        double scale = Math.Min(width / canvasWidth, height / canvasHeight)
            * (XamlRoot?.RasterizationScale ?? 1) * navigation.Zoom;
        // Render the pixels actually displayed, including monitor DPI and view zoom.
        // A two-megapixel presentation budget bounds readback/upload on fallback GPUs;
        // the renderer resolves 2x supersampling into this buffer for smooth edges.
        double desiredHeight = Math.Max(16, Math.Ceiling(canvasHeight * scale / 16) * 16);
        scale = desiredHeight / canvasHeight;
        scale *= Math.Min(1, Math.Min(4096d / (Math.Max(canvasWidth, canvasHeight) * scale),
            Math.Sqrt(2_097_152d / (canvasWidth * (double)canvasHeight * scale * scale))));
        return (Math.Max(16, (int)Math.Round(canvasWidth * scale)),
            Math.Max(16, (int)Math.Round(canvasHeight * scale)), 2);
    }

    public async Task ResizePreviewAsync(int width, int height, int supersample = 1,
        CancellationToken cancellationToken = default)
    {
        RecordPreviewOperation("resize.begin", width: width, height: height, supersample: supersample);
        await session.ResizePreviewAsync(width, height, supersample, cancellationToken);
        PreviewSupersample = supersample; await AwaitPresentationAsync();
        RecordPreviewOperation("resize.end");
    }

    public async Task SetScenePreviewScaleAsync(double scale, CancellationToken cancellationToken = default)
    {
        await session.SetScenePreviewScaleAsync(scale, cancellationToken);
        // Even a fast settled frame must replace the temporary quality label;
        // a paused monitor may never publish another frame after this one.
        statusClock = 0;
    }

    public async Task ApplySceneEditsAsync(IReadOnlyList<JsonObject> rows, IsolatedActorTransform? transform)
    {
        RecordPreviewOperation("edits.begin", rows.Count, transform is not null);
        await session.ApplySceneEditsAsync(rows, transform); await AwaitPresentationAsync();
        RecordPreviewOperation("edits.end", rows.Count, transform is not null);
    }

    void InitializePresentationResolution()
    {
        Loaded += (_, _) =>
        {
            if (!ReferenceEquals(observedXamlRoot, XamlRoot))
            {
                if (observedXamlRoot is not null) observedXamlRoot.Changed -= OnXamlRootChanged;
                observedXamlRoot = XamlRoot;
                if (observedXamlRoot is not null) observedXamlRoot.Changed += OnXamlRootChanged;
            }
            NotifyRasterizationScale();
        };
        Unloaded += (_, _) =>
        {
            if (observedXamlRoot is not null) observedXamlRoot.Changed -= OnXamlRootChanged;
            observedXamlRoot = null;
        };
    }
    void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => NotifyRasterizationScale();
    void NotifyRasterizationScale()
    {
        double scale = XamlRoot?.RasterizationScale ?? 1;
        if (lastRasterizationScale == scale) return;
        lastRasterizationScale = scale; PresentationSizeChanged?.Invoke(this, EventArgs.Empty);
    }
}
