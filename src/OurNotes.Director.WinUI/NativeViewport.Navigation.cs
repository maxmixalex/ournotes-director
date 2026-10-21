using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;
using Windows.Foundation;
using Windows.System;

namespace OurNotes.Director.WinUI;

public sealed partial class NativeViewport
{
    internal Func<MenuFlyout>? ContextMenuFactory { get; set; }
    public Func<PreviewTarget, string>? ObjectWheelHint { get; set; }
    readonly ViewportNavigation navigation = new();
    readonly ToolTip navigationHelp = new();
    bool showingEmptyCanvas, panning;
    int emptyWidth = 1920, emptyHeight = 1080;
    Point panStart, panOrigin;
    uint panPointer;

    public void SetEmptyCanvas(int width, int height)
    {
        if (width < 16 || height < 16) throw new ArgumentOutOfRangeException(nameof(width));
        previewAudio.Clear(); showingEmptyCanvas = true; sourceVersion++; session.Pause(); HasCurrentSource = false; loading = false;
        emptyWidth = width; emptyHeight = height;
        lock (frameGate) { pendingFrame = null; Volatile.Write(ref lastFrame, null); }
        DetachGpu(); image.Source = null; SelectedTargetId = null; HideSelection(); status.Text = "空白画布 · 添加角色或素材开始创作";
        FitCanvas();
    }
    public void FitCanvas() { navigation.Fit(); LayoutCanvas(); }

    public void RefreshNavigationHint()
    {
        var target = HasCurrentSource ? LastFrame?.Targets.LastOrDefault(t => t.Id == selected) : null;
        string wheel = target is not null ? ObjectWheelHint?.Invoke(target) ?? "滚轮缩放所选人物"
            : selected is not null ? "所选人物在当前帧不可见" : "滚轮缩放画布";
        string hint = wheel + " · Ctrl+滚轮缩放画布 · 中键拖动平移 · F 适应窗口 · Esc 取消选择";
        navigationHelp.Content = hint; AutomationProperties.SetHelpText(this, "点击选择、拖动人物；" + hint);
    }

    void InitializeWorkspaceNavigation()
    {
        IsTabStop = true;
        ToolTipService.SetToolTip(surface, navigationHelp);
        navigationHelp.Opened += (_, _) => RefreshNavigationHint(); RefreshNavigationHint();
        surface.ContextRequested += (_, args) =>
        {
            if (ContextMenuFactory is null) return;
            Focus(FocusState.Pointer);
            var options = new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions();
            if (args.TryGetPosition(surface, out Point point))
            {
                options.Position = point;
                var normalized = InCanvas(point) ? Normalize(point) : new Point(-1, -1);
                SelectedTargetId = InCanvas(point) ? HitTest(normalized.X, normalized.Y)?.Id : null;
            }
            ContextMenuFactory().ShowAt(surface, options); args.Handled = true;
        };
        surface.PointerPressed += (_, args) =>
        {
            var point = args.GetCurrentPoint(surface);
            if (!point.Properties.IsMiddleButtonPressed) return;
            Focus(FocusState.Pointer);
            panning = true; panPointer = args.Pointer.PointerId; panStart = point.Position; panOrigin = new(navigation.PanX, navigation.PanY);
            surface.CapturePointer(args.Pointer); args.Handled = true;
        };
        surface.PointerMoved += (_, args) =>
        {
            if (!panning || args.Pointer.PointerId != panPointer) return;
            var point = args.GetCurrentPoint(surface).Position;
            navigation.PanTo(panOrigin.X + point.X - panStart.X, panOrigin.Y + point.Y - panStart.Y);
            LayoutCanvas(); args.Handled = true;
        };
        surface.PointerReleased += (_, args) =>
        { if (panning && args.Pointer.PointerId == panPointer) { panning = false; surface.ReleasePointerCapture(args.Pointer); args.Handled = true; } };
        surface.PointerCaptureLost += (_, _) => panning = false;
        KeyDown += (_, args) =>
        {
            if (args.Handled || (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0) return;
            if (args.Key == VirtualKey.F) { FitCanvas(); args.Handled = true; }
            else if (args.Key == VirtualKey.Escape && dragTarget is null) { SelectedTargetId = null; args.Handled = true; }
        };
    }
    void ZoomCanvas(Point anchor, int delta)
    {
        if (navigation.ZoomAt(anchor.X, anchor.Y, delta)) LayoutCanvas();
    }
}
