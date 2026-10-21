using Microsoft.UI.Xaml;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.WinUI;

public sealed partial class NativeViewport
{
    int logicalCanvasWidth, logicalCanvasHeight;
    XamlRoot? observedXamlRoot;
    double lastRasterizationScale;
    public event EventHandler? PresentationSizeChanged;
    public int PreviewSupersample { get; private set; } = 1;
    internal bool IsFullscreenRendering { get; private set; }
    internal bool IsCompositionFixture => compositionTestLayout is not null;
    (int Width, int Height, int Supersample)? windowedPreviewSize;
    long windowedSourceVersion;
    internal (int Width, int Height, int Supersample)? WindowedPreviewSize
        => windowedSourceVersion == sourceVersion ? windowedPreviewSize : null;

    internal void SetFullscreenRendering(bool enabled)
    {
        if (IsFullscreenRendering == enabled) return;
        // A quick exit/re-entry can precede the debounced windowed redraw. Its
        // LastFrame is still fullscreen; keep this source's original pane size.
        if (enabled && HasCurrentSource && !loading
            && (windowedPreviewSize is null || windowedSourceVersion != sourceVersion))
        {
            windowedPreviewSize = LastFrame is { } frame
                ? (frame.Pixels.Width, frame.Pixels.Height, PreviewSupersample) : null;
            windowedSourceVersion = sourceVersion;
        }
        IsFullscreenRendering = enabled;
        PresentationSizeChanged?.Invoke(this, EventArgs.Empty);
    }

    void RememberWindowedPreviewSize(int width, int height, int supersample)
    {
        windowedPreviewSize = (width, height, supersample);
        windowedSourceVersion = sourceVersion;
    }

    public void SetCanvasDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (logicalCanvasWidth == width && logicalCanvasHeight == height) return;
        logicalCanvasWidth = width; logicalCanvasHeight = height; LayoutCanvas();
    }

    public (int Width, int Height, int Supersample) RecommendedPreviewSize(int canvasWidth, int canvasHeight, int supersample = 2)
    {
        double width = surface.ActualWidth > 0 ? surface.ActualWidth : 960;
        double height = surface.ActualHeight > 0 ? surface.ActualHeight : 540;
        return PreviewResolution.Choose(canvasWidth, canvasHeight, width, height,
            XamlRoot?.RasterizationScale ?? 1, navigation.Zoom, IsFullscreenRendering, supersample);
    }

    public async Task ResizePreviewAsync(int width, int height, int supersample = 1,
        CancellationToken cancellationToken = default)
    {
        long version = sourceVersion;
        bool windowed = !IsFullscreenRendering;
        RecordPreviewOperation("resize.begin", width: width, height: height, supersample: supersample);
        await session.ResizePreviewAsync(width, height, supersample, cancellationToken);
        PreviewSupersample = supersample; await AwaitPresentationAsync();
        if (windowed && version == sourceVersion) RememberWindowedPreviewSize(width, height, supersample);
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
