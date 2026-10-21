using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;

namespace OurNotes.Director.WinUI;

public sealed partial class NativeViewport
{
    internal (double Zoom, Point Pan, Rect Canvas) NavigationStateForSmoke
        => (navigation.Zoom, new(navigation.PanX, navigation.PanY), imageRect);
    /// <summary>Test-only Windows input. Exercises real hit testing, capture and pointer
    /// handlers; does not call the page's edit function or write a scene document.</summary>
    internal async Task SendDragSmokeAsync(Window window, string actor, double dx, double dy)
    {
        nint hwnd = await PrepareSmokeInputAsync(window);
        var screen = SurfaceScreenPoint(hwnd, ActorSmokePoint(actor));
        double raster = XamlRoot.RasterizationScale;
        int x = screen.X, y = screen.Y;
        PointerEventHandler observe = (_, args) =>
        {
            var point = args.GetCurrentPoint(surface);
            TracePointer($"id={args.Pointer.PointerId} xy={point.Position} left={point.Properties.IsLeftButtonPressed} captured={surface.PointerCaptures?.Count ?? 0} drag={dragTarget} moved={dragMoved} handled={args.Handled}");
        };
        var events = new[] { UIElement.PointerPressedEvent, UIElement.PointerMovedEvent,
            UIElement.PointerReleasedEvent, UIElement.PointerCanceledEvent, UIElement.PointerCaptureLostEvent };
        foreach (var routed in events) surface.AddHandler(routed, observe, true);
        TracePointer($"inject actor={actor} screen=({x},{y}) raster={raster} image={imageRect}");
        MoveSmokePointer(x, y); await Task.Delay(80); SendSmokeInput(new() { Flags = 0x0002 }); await Task.Delay(100);
        try
        {
            for (int i = 1; i <= 5; i++)
            {
                MoveSmokePointer(x + (int)Math.Round(dx * imageRect.Width * raster * i / 5),
                    y + (int)Math.Round(dy * imageRect.Height * raster * i / 5));
                await Task.Delay(45);
            }
        }
        finally
        {
            SendSmokeInput(new() { Flags = 0x0004 }); await Task.Delay(100);
            foreach (var routed in events) surface.RemoveHandler(routed, observe);
        }
    }

    /// <summary>Injects a real wheel gesture while leaving object selection untouched.</summary>
    internal async Task<Point> SendWheelSmokeAsync(Window window, string? hoverActor, int delta, bool control = false)
    {
        nint hwnd = await PrepareSmokeInputAsync(window);
        var local = hoverActor is null ? new Point(surface.ActualWidth * .35, surface.ActualHeight * .4) : ActorSmokePoint(hoverActor);
        var screen = SurfaceScreenPoint(hwnd, local);
        var completed = new TaskCompletionSource<Point>(TaskCreationOptions.RunContinuationsAsynchronously);
        PointerEventHandler observe = (_, args) =>
        {
            var point = args.GetCurrentPoint(surface);
            if (point.Properties.MouseWheelDelta != 0) completed.TrySetResult(point.Position);
        };
        surface.AddHandler(PointerWheelChangedEvent, observe, true);
        try
        {
            MoveSmokePointer(screen.X, screen.Y); await Task.Delay(80);
            if (control) { SendSmokeKey(VirtualKey.Control, false); await Task.Delay(30); }
            SendSmokeInput(new() { Flags = 0x0800, Data = unchecked((uint)delta) });
            return await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            if (control) SendSmokeKey(VirtualKey.Control, true);
            surface.RemoveHandler(PointerWheelChangedEvent, observe);
        }
    }

    internal async Task SendViewportKeySmokeAsync(Window window, VirtualKey key, bool control = false)
    {
        await PrepareSmokeInputAsync(window);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        KeyEventHandler observe = (_, args) => { if (args.Key == key) completed.TrySetResult(); };
        AddHandler(KeyDownEvent, observe, true);
        try
        {
            if (control) SendSmokeKey(VirtualKey.Control, false);
            SendSmokeKey(key, false);
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            SendSmokeKey(key, true); if (control) SendSmokeKey(VirtualKey.Control, true);
            RemoveHandler(KeyDownEvent, observe);
        }
    }

    internal async Task SendControlKeySmokeAsync(Window window, Microsoft.UI.Xaml.Controls.Control control, VirtualKey key)
    {
        await PrepareSmokeInputAsync(window);
        if (!control.Focus(FocusState.Keyboard)) throw new InvalidOperationException("The requested editor control did not receive keyboard focus.");
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        KeyEventHandler observe = (_, args) => { if (args.Key == key) completed.TrySetResult(); };
        control.AddHandler(PreviewKeyDownEvent, observe, true);
        try { SendSmokeKey(key, false); await completed.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
        finally { SendSmokeKey(key, true); control.RemoveHandler(PreviewKeyDownEvent, observe); }
    }

    internal async Task SendBlankClickSmokeAsync(Window window, bool outsideCanvas = false)
    {
        nint hwnd = await PrepareSmokeInputAsync(window);
        var candidates = from y in new[] { 4d, surface.ActualHeight / 2, surface.ActualHeight - 4 }
                         from x in new[] { 4d, surface.ActualWidth / 2, surface.ActualWidth - 4 }
                         select new Point(x, y);
        Point? blank = candidates.Where(p => !InCanvas(p) || (!outsideCanvas &&
            HitTest(Normalize(p).X, Normalize(p).Y) is null)).Select(p => (Point?)p).FirstOrDefault();
        if (blank is null) throw new InvalidOperationException("The fixture does not expose the requested blank canvas area.");
        var screen = SurfaceScreenPoint(hwnd, blank.Value);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PointerEventHandler observe = (_, _) => completed.TrySetResult();
        surface.AddHandler(PointerPressedEvent, observe, true);
        try
        {
            MoveSmokePointer(screen.X, screen.Y); await Task.Delay(80);
            SendSmokeInput(new() { Flags = 0x0002 });
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            SendSmokeInput(new() { Flags = 0x0004 });
            surface.RemoveHandler(PointerPressedEvent, observe);
        }
    }

    Point ActorSmokePoint(string actor)
    {
        var target = LastFrame?.Targets.Single(t => t.Id == actor)
            ?? throw new InvalidOperationException("The smoke actor is not present in the current frame.");
        double left = Math.Max(2, Math.Max(imageRect.X, imageRect.X + target.X * imageRect.Width));
        double top = Math.Max(2, Math.Max(imageRect.Y, imageRect.Y + target.Y * imageRect.Height));
        double right = Math.Min(surface.ActualWidth - 2, Math.Min(imageRect.Right, imageRect.X + (target.X + target.Width) * imageRect.Width));
        double bottom = Math.Min(surface.ActualHeight - 2, Math.Min(imageRect.Bottom, imageRect.Y + (target.Y + target.Height) * imageRect.Height));
        if (right <= left || bottom <= top) throw new InvalidOperationException("The smoke actor is outside the visible canvas.");
        foreach (double y in new[] { .4, .2, .6, .8 })
        foreach (double x in new[] { .45, .2, .7, .9 })
        {
            var point = new Point(left + (right - left) * x, top + (bottom - top) * y);
            var normalized = Normalize(point);
            if (HitTest(normalized.X, normalized.Y)?.Id == actor) return point;
        }
        throw new InvalidOperationException("The smoke actor has no visible hit-testable area.");
    }
    async Task<nint> PrepareSmokeInputAsync(Window window)
    {
        if (Environment.GetEnvironmentVariable("OURNOTES_REAL_SCENE_FIXTURE") is not { Length: > 0 })
            throw new InvalidOperationException("Synthetic input requires the dedicated smoke-test fixture.");
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _ = SetForegroundWindow(hwnd); await Task.Delay(80);
        if (GetForegroundWindow() != hwnd) throw new InvalidOperationException("The actual editor window did not receive input focus.");
        return hwnd;
    }
    ScreenPoint SurfaceScreenPoint(nint hwnd, Point local)
    {
        Point root = surface.TransformToVisual(XamlRoot.Content).TransformPoint(local);
        var origin = new ScreenPoint();
        if (!ClientToScreen(hwnd, ref origin)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return new() { X = origin.X + (int)Math.Round(root.X * XamlRoot.RasterizationScale),
            Y = origin.Y + (int)Math.Round(root.Y * XamlRoot.RasterizationScale) };
    }
    static void MoveSmokePointer(int x, int y)
    {
        int left = GetSystemMetrics(76), top = GetSystemMetrics(77), width = GetSystemMetrics(78), height = GetSystemMetrics(79);
        SendSmokeInput(new() { X = (int)Math.Round((x - left) * 65535d / Math.Max(1, width - 1)),
            Y = (int)Math.Round((y - top) * 65535d / Math.Max(1, height - 1)), Flags = 0x0001 | 0x8000 | 0x4000 | 0x2000 });
    }
    static void SendSmokeKey(VirtualKey key, bool release)
        => SendSmokeInput(new() { Type = 1, Key = (ushort)key, KeyboardFlags = release ? 0x0002u : 0 });
    static void SendSmokeInput(MouseInput input)
    {
        if (SendInput(1, [input], Marshal.SizeOf<MouseInput>()) != 1)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    internal static void TracePointer(string message)
    {
        if (Environment.GetEnvironmentVariable("OURNOTES_REAL_SCENE_FIXTURE") is { Length: > 0 })
            Console.Error.WriteLine("[pointer] " + message);
    }
    [StructLayout(LayoutKind.Sequential)] struct ScreenPoint { public int X, Y; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] struct MouseInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public int X;
        [FieldOffset(12)] public int Y;
        [FieldOffset(16)] public uint Data;
        [FieldOffset(20)] public uint Flags;
        [FieldOffset(24)] public uint Time;
        [FieldOffset(32)] public nuint Extra;
        [FieldOffset(8)] public ushort Key;
        [FieldOffset(12)] public uint KeyboardFlags;
    }
    [DllImport("user32.dll", SetLastError = true)] static extern bool ClientToScreen(nint window, ref ScreenPoint point);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, MouseInput[] input, int size);
}
