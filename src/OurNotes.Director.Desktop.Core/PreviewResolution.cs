using OurNotes.Director.Core;

namespace OurNotes.Director.Desktop.Core;

/// <summary>Native pixel dimensions for the displayed canvas, independent of export settings.</summary>
public static class PreviewResolution
{
    public static (int Width, int Height, int Supersample) Choose(int canvasWidth, int canvasHeight,
        double viewWidth, double viewHeight, double dpi = 1, double zoom = 1,
        bool fullscreen = false, int supersample = 2)
    {
        RenderDimensions.Validate(canvasWidth, canvasHeight);
        if (!double.IsFinite(viewWidth) || !double.IsFinite(viewHeight) || viewWidth <= 0 || viewHeight <= 0
            || !double.IsFinite(dpi) || dpi <= 0 || !double.IsFinite(zoom) || zoom <= 0)
            throw new ArgumentOutOfRangeException(nameof(viewWidth));
        if (supersample is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(supersample));
        double scale = Math.Min(viewWidth / canvasWidth, viewHeight / canvasHeight) * dpi * zoom;
        // Editor panes use a bounded buffer. Fullscreen must sample every displayed
        // pixel; its only limit is the renderer's real maximum, not the 2 MP budget.
        double maxSide = fullscreen ? RenderDimensions.MaxSide / (double)supersample : 4096;
        double maxPixels = fullscreen ? RenderDimensions.MaxPixels / (double)(supersample * supersample) : 2_097_152;
        scale = Math.Min(scale, Math.Min(maxSide / Math.Max(canvasWidth, canvasHeight),
            Math.Sqrt(maxPixels / (canvasWidth * (double)canvasHeight))));
        int width = Math.Max(16, (int)Math.Ceiling(canvasWidth * scale - 1e-7));
        int height = Math.Max(16, (int)Math.Ceiling(canvasHeight * scale - 1e-7));
        // Ceiling covers fractional display pixels. At the maximum render budget,
        // remove only a rounding pixel instead of rejecting an otherwise valid view.
        while ((long)width * height * supersample * supersample > RenderDimensions.MaxPixels)
        { if (width >= height && width > 16) width--; else height--; }
        RenderDimensions.Validate(width, height, supersample);
        return (width, height, supersample);
    }
}
