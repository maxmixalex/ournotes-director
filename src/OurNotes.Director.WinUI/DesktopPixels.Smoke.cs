using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using OurNotes.Director.Core;

namespace OurNotes.Director.WinUI;

// RenderTargetBitmap omits SwapChainPanel pixels. This explicit smoke helper
// captures the composed desktop client area, including the real DXGI surface.
internal static class DesktopPixels
{
    internal static Pixels CaptureClient(Window window)
    {
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        if (!GetClientRect(hwnd, out var bounds)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var origin = new ScreenPoint();
        if (!ClientToScreen(hwnd, ref origin)) throw new Win32Exception(Marshal.GetLastWin32Error());
        int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
        if (width <= 0 || height <= 0) throw new InvalidOperationException("屏幕截图没有可见客户区。");
        Marshal.ThrowExceptionForHR(DwmFlush());
        nint screen = GetDC(0), memory = 0, bitmap = 0, previous = 0;
        try
        {
            if (screen == 0 || (memory = CreateCompatibleDC(screen)) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            var info = new BitmapInfo { Header = new BitmapHeader
            { Size = (uint)Marshal.SizeOf<BitmapHeader>(), Width = width, Height = -height, Planes = 1, BitCount = 32 } };
            bitmap = CreateDIBSection(screen, ref info, 0, out nint bits, 0, 0);
            if (bitmap == 0 || bits == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            previous = SelectObject(memory, bitmap);
            if (!BitBlt(memory, 0, 0, width, height, screen, origin.X, origin.Y, 0x00CC0020 | 0x40000000))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var rgba = new byte[checked(width * height * 4)]; Marshal.Copy(bits, rgba, 0, rgba.Length);
            for (int i = 0; i < rgba.Length; i += 4) { (rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]); rgba[i + 3] = 255; }
            return new(width, height, rgba);
        }
        finally
        {
            if (previous != 0) SelectObject(memory, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            if (memory != 0) DeleteDC(memory);
            if (screen != 0) ReleaseDC(0, screen);
        }
    }
    [StructLayout(LayoutKind.Sequential)] struct ClientRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct ScreenPoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct BitmapHeader
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ColorsUsed, ColorsImportant;
    }
    [StructLayout(LayoutKind.Sequential)] struct BitmapInfo { public BitmapHeader Header; public uint Color; }
    [DllImport("user32.dll", SetLastError = true)] static extern bool GetClientRect(nint window, out ClientRect rect);
    [DllImport("user32.dll", SetLastError = true)] static extern bool ClientToScreen(nint window, ref ScreenPoint point);
    [DllImport("user32.dll")] static extern nint GetDC(nint window);
    [DllImport("user32.dll")] static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll", SetLastError = true)] static extern bool BitBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(nint dc);
    [DllImport("dwmapi.dll")] static extern int DwmFlush();
}
