using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;

namespace OurNotes.Director.WinUI;

public sealed partial class NativeViewport
{
    internal Func<MenuFlyout>? ContextMenuFactory { get; set; }
    bool showingEmptyCanvas, panning;
    int emptyWidth = 1920, emptyHeight = 1080;
    double viewZoom = 1;
    Point viewPan, panStart, panOrigin;
    uint panPointer;

    public void SetEmptyCanvas(int width, int height)
    {
        if (width < 16 || height < 16) throw new ArgumentOutOfRangeException(nameof(width));
        previewAudio.Clear(); showingEmptyCanvas = true; sourceVersion++; session.Pause(); HasCurrentSource = false; loading = false;
        emptyWidth = width; emptyHeight = height;
        lock (frameGate) { pendingFrame = null; Volatile.Write(ref lastFrame, null); }
        DetachGpu(); image.Source = null; HideSelection(); status.Text = "空白画布 · 添加角色或素材开始创作";
        FitCanvas();
    }
    public void FitCanvas() { viewZoom = 1; viewPan = default; LayoutCanvas(); }

    void InitializeWorkspaceNavigation()
    {
        IsTabStop = true;
        ToolTipService.SetToolTip(surface, "滚轮缩放画布 · 中键拖动平移 · Ctrl+滚轮缩放角色 · F 适应窗口 · 右键对象操作");
        surface.ContextRequested += (_, args) =>
        {
            if (ContextMenuFactory is null) return;
            var options = new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions();
            if (args.TryGetPosition(surface, out Point point))
            {
                options.Position = point;
                if (InCanvas(point))
                { var normalized = Normalize(point); SelectedTargetId = HitTest(normalized.X, normalized.Y)?.Id; }
            }
            ContextMenuFactory().ShowAt(surface, options); args.Handled = true;
        };
        surface.PointerPressed += (_, args) =>
        {
            var point = args.GetCurrentPoint(surface);
            if (!point.Properties.IsMiddleButtonPressed) return;
            panning = true; panPointer = args.Pointer.PointerId; panStart = point.Position; panOrigin = viewPan;
            surface.CapturePointer(args.Pointer); args.Handled = true;
        };
        surface.PointerMoved += (_, args) =>
        {
            if (!panning || args.Pointer.PointerId != panPointer) return;
            var point = args.GetCurrentPoint(surface).Position;
            viewPan = new(panOrigin.X + point.X - panStart.X, panOrigin.Y + point.Y - panStart.Y);
            LayoutCanvas(); args.Handled = true;
        };
        surface.PointerReleased += (_, args) =>
        { if (panning && args.Pointer.PointerId == panPointer) { panning = false; surface.ReleasePointerCapture(args.Pointer); args.Handled = true; } };
        surface.PointerCaptureLost += (_, _) => panning = false;
        KeyDown += (_, args) => { if (args.Key == VirtualKey.F) { FitCanvas(); args.Handled = true; } };
    }
    void ZoomCanvas(Point anchor, int delta)
    {
        if (delta == 0 || imageRect.Width <= 0) return;
        var coordinate = Normalize(anchor);
        double next = Math.Clamp(viewZoom * Math.Pow(1.12, delta / 120d), .1, 12);
        double ratio = next / viewZoom;
        viewPan = new(anchor.X - surface.ActualWidth / 2 + (.5 - coordinate.X) * imageRect.Width * ratio,
            anchor.Y - surface.ActualHeight / 2 + (.5 - coordinate.Y) * imageRect.Height * ratio);
        viewZoom = next; LayoutCanvas();
    }
}
