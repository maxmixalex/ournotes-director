namespace OurNotes.Director.Desktop.Core;

public enum ViewportWheelAction { None, Canvas, SelectedObject }

/// <summary>View-only canvas geometry. Object transforms never pass through this state.</summary>
public sealed class ViewportNavigation
{
    double viewportWidth, viewportHeight, fitWidth, fitHeight;
    public double Zoom { get; private set; } = 1;
    public double PanX { get; private set; }
    public double PanY { get; private set; }
    public (double X, double Y, double Width, double Height) Bounds =>
        ((viewportWidth - fitWidth * Zoom) / 2 + PanX,
         (viewportHeight - fitHeight * Zoom) / 2 + PanY, fitWidth * Zoom, fitHeight * Zoom);

    public void Resize(double viewWidth, double viewHeight, double canvasWidth, double canvasHeight)
    {
        if (!double.IsFinite(viewWidth) || !double.IsFinite(viewHeight) || viewWidth <= 0 || viewHeight <= 0
            || !double.IsFinite(canvasWidth) || !double.IsFinite(canvasHeight) || canvasWidth <= 0 || canvasHeight <= 0)
            return;
        double scale = Math.Min(viewWidth / canvasWidth, viewHeight / canvasHeight);
        double width = canvasWidth * scale, height = canvasHeight * scale;
        // Resizing a panel keeps the same canvas point under the panel's centre.
        // Pan is measured in display pixels, so it follows the change in fit scale.
        if (fitWidth > 0) PanX *= width / fitWidth;
        if (fitHeight > 0) PanY *= height / fitHeight;
        viewportWidth = viewWidth; viewportHeight = viewHeight; fitWidth = width; fitHeight = height;
    }

    public void Fit() { Zoom = 1; PanX = PanY = 0; }
    public void PanTo(double x, double y)
    { if (double.IsFinite(x) && double.IsFinite(y)) { PanX = x; PanY = y; } }

    public bool ZoomAt(double anchorX, double anchorY, int wheelDelta)
    {
        if (wheelDelta == 0 || fitWidth <= 0 || fitHeight <= 0 || !double.IsFinite(anchorX) || !double.IsFinite(anchorY)) return false;
        var before = Bounds;
        double next = Math.Clamp(Zoom * Math.Pow(1.12, wheelDelta / 120d), .1, 12);
        if (next == Zoom) return false;
        double x = (anchorX - before.X) / before.Width, y = (anchorY - before.Y) / before.Height;
        PanX = anchorX - viewportWidth / 2 + (.5 - x) * fitWidth * next;
        PanY = anchorY - viewportHeight / 2 + (.5 - y) * fitHeight * next;
        Zoom = next; return true;
    }

    // Hover is deliberately absent: the wheel must never pick a different actor.
    public static ViewportWheelAction WheelAction(bool control, bool hasSelection, bool selectionVisible)
        => control || !hasSelection ? ViewportWheelAction.Canvas
            : selectionVisible ? ViewportWheelAction.SelectedObject : ViewportWheelAction.None;
}
