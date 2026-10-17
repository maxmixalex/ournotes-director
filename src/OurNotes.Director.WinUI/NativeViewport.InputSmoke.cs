using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace OurNotes.Director.WinUI;

public sealed partial class NativeViewport
{
    /// <summary>Test-only Windows input. Exercises real hit testing, capture and pointer
    /// handlers; does not call the page's edit function or write a scene document.</summary>
    internal async Task SendDragSmokeAsync(Window window, string actor, double dx, double dy)
    {
        if (Environment.GetEnvironmentVariable("OURNOTES_REAL_SCENE_FIXTURE") is not { Length: > 0 })
            throw new InvalidOperationException("Synthetic input requires the dedicated smoke-test fixture.");
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var target = LastFrame!.Targets.Single(t => t.Id == actor);
        var local = new Point(imageRect.X + (target.X + target.Width * .45) * imageRect.Width,
            imageRect.Y + Math.Clamp(target.Y + target.Height * .4, .05, .90) * imageRect.Height);
        Point rootPoint = surface.TransformToVisual(XamlRoot.Content).TransformPoint(local);
        var origin = new ScreenPoint();
        if (!ClientToScreen(hwnd, ref origin)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        double raster = XamlRoot.RasterizationScale;
        int x = origin.X + (int)Math.Round(rootPoint.X * raster), y = origin.Y + (int)Math.Round(rootPoint.Y * raster);
        _ = SetForegroundWindow(hwnd); await Task.Delay(80);
        if (GetForegroundWindow() != hwnd) throw new InvalidOperationException("The actual editor window did not receive input focus.");
        void Move(int px, int py)
        {
            int left = GetSystemMetrics(76), top = GetSystemMetrics(77);
            int width = GetSystemMetrics(78), height = GetSystemMetrics(79);
            var input = new[] { new MouseInput { Type = 0,
                X = (int)Math.Round((px - left) * 65535d / Math.Max(1, width - 1)),
                Y = (int)Math.Round((py - top) * 65535d / Math.Max(1, height - 1)),
                Flags = 0x0001 | 0x8000 | 0x4000 | 0x2000 } };
            if (SendInput(1, input, Marshal.SizeOf<MouseInput>()) != 1)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        void Button(uint flag)
        {
            var input = new[] { new MouseInput { Type = 0, Flags = flag } };
            if (SendInput(1, input, Marshal.SizeOf<MouseInput>()) != 1) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        PointerEventHandler observe = (_, args) =>
        {
            var point = args.GetCurrentPoint(surface);
            TracePointer($"id={args.Pointer.PointerId} xy={point.Position} left={point.Properties.IsLeftButtonPressed} captured={surface.PointerCaptures?.Count ?? 0} drag={dragTarget} moved={dragMoved} handled={args.Handled}");
        };
        var events = new[] { UIElement.PointerPressedEvent, UIElement.PointerMovedEvent,
            UIElement.PointerReleasedEvent, UIElement.PointerCanceledEvent, UIElement.PointerCaptureLostEvent };
        foreach (var routed in events) surface.AddHandler(routed, observe, true);
        TracePointer($"inject actor={actor} screen=({x},{y}) root=({rootPoint.X},{rootPoint.Y}) raster={raster} image={imageRect}");
        Move(x, y); await Task.Delay(80); Button(0x0002); await Task.Delay(100);
        try
        {
            for (int i = 1; i <= 5; i++)
            {
                Move(x + (int)Math.Round(dx * imageRect.Width * raster * i / 5),
                    y + (int)Math.Round(dy * imageRect.Height * raster * i / 5));
                await Task.Delay(45);
            }
        }
        finally
        {
            Button(0x0004); await Task.Delay(100);
            foreach (var routed in events) surface.RemoveHandler(routed, observe);
        }
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
    }
    [DllImport("user32.dll", SetLastError = true)] static extern bool ClientToScreen(nint window, ref ScreenPoint point);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, MouseInput[] input, int size);
}
