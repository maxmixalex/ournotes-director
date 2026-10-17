using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
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
        void Move(int px, int py) { if (!SetCursorPos(px, py)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
        void Button(uint flag)
        {
            var input = new[] { new MouseInput { Type = 0, Flags = flag } };
            if (SendInput(1, input, Marshal.SizeOf<MouseInput>()) != 1) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        Move(x, y); await Task.Delay(40); Button(0x0002);
        try
        {
            for (int i = 1; i <= 5; i++)
            {
                Move(x + (int)Math.Round(dx * imageRect.Width * raster * i / 5),
                    y + (int)Math.Round(dy * imageRect.Height * raster * i / 5));
                await Task.Delay(45);
            }
        }
        finally { Button(0x0004); }
    }
    [StructLayout(LayoutKind.Sequential)] struct ScreenPoint { public int X, Y; }
    // x64 INPUT + MOUSEINPUT union; explicit offsets keep padding ABI-correct.
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
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, MouseInput[] input, int size);
}
