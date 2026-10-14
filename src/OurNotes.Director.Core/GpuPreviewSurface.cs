using System.Runtime.InteropServices;

namespace OurNotes.Director.Core;

/// <summary>Owns the WGL/D3D interop registration on its creating render thread. A UI consumer
/// acquires an independent COM reference, so a late frame cannot access a destroyed GL surface.</summary>
public sealed class GpuPreviewSurface : IDisposable
{
    const string Library = "OurNotes.GpuPreview.dll";
    readonly object gate = new();
    readonly int owner = Environment.CurrentManagedThreadId;
    nint handle;
    public int Width { get; }
    public int Height { get; }
    public bool IsDisposed { get { lock (gate) return handle == 0; } }
    private GpuPreviewSurface(nint handle, int width, int height) { this.handle = handle; Width = width; Height = height; }
    public static bool TryCreate(int width, int height, out GpuPreviewSurface? surface, out string? reason)
    {
        surface = null; reason = null;
        if (!OperatingSystem.IsWindows()) { reason = "当前平台使用 OpenGL 像素显示"; return false; }
        try
        {
            if (OurNotesGpuAbi() != 1) { reason = "GPU 显示组件版本不匹配"; return false; }
            int hr = OurNotesGpuCreate((uint)width, (uint)height, out nint native);
            if (hr < 0)
            { reason = hr == unchecked((int)0x887A0004) ? "显卡驱动未提供 OpenGL / D3D11 共享纹理" : $"共享纹理初始化失败（0x{hr:X8}）"; return false; }
            surface = new(native, width, height); return true;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        { reason = "GPU 显示组件不可用：" + error.Message; return false; }
    }
    internal bool Present(uint framebuffer, int width, int height, out string? error)
    {
        VerifyThread();
        lock (gate)
        {
            if (handle == 0) { error = "共享纹理已经释放"; return false; }
            int hr = OurNotesGpuPresent(handle, framebuffer, (uint)width, (uint)height);
            error = hr < 0 ? $"GPU 显示失败（0x{hr:X8}），已退回兼容模式" : null;
            return hr >= 0;
        }
    }
    /// <summary>The caller must release this reference, even when attachment fails.</summary>
    public nint AcquireSwapChain() { lock (gate) return handle == 0 ? 0 : OurNotesGpuAcquireSwapChain(handle); }
    public static void ReleaseSwapChain(nint swapChain) { if (swapChain != 0) OurNotesGpuReleaseSwapChain(swapChain); }
    public void SetDisplaySize(double width, double height)
    {
        nint swap = AcquireSwapChain(); if (swap == 0) return;
        try { Marshal.ThrowExceptionForHR(OurNotesGpuScaleSwapChain(swap, (float)(width / Width), (float)(height / Height))); }
        finally { ReleaseSwapChain(swap); }
    }
    public static int AttachPanel(nint panel, nint swapChain) => OurNotesGpuAttachPanel(panel, swapChain);
    public void Dispose()
    {
        VerifyThread(); lock (gate) { if (handle == 0) return; OurNotesGpuDestroy(handle); handle = 0; }
    }
    void VerifyThread() { if (Environment.CurrentManagedThreadId != owner) throw new InvalidOperationException("GPU resources must be released by their rendering thread."); }
    // Test-only WARP surface: exercises native ABI, pixels, COM references and real XAML attachment.
    // It deliberately does NOT report hardware or WGL interop coverage.
    public static GpuPreviewSurface CreateCompositionTest(int width, int height)
    {
        Marshal.ThrowExceptionForHR(OurNotesGpuCreateTest((uint)width, (uint)height, out nint native));
        return new(native, width, height);
    }
    public uint ReadCompositionTestPixel()
    { VerifyThread(); Marshal.ThrowExceptionForHR(OurNotesGpuTestPixel(handle, out uint pixel)); return pixel; }
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] static extern uint OurNotesGpuAbi();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] static extern int OurNotesGpuCreate(uint width, uint height, out nint surface);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] static extern int OurNotesGpuPresent(nint surface, uint framebuffer, uint width, uint height);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] static extern void OurNotesGpuDestroy(nint surface);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] static extern nint OurNotesGpuAcquireSwapChain(nint surface);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] static extern void OurNotesGpuReleaseSwapChain(nint swapChain);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] static extern int OurNotesGpuScaleSwapChain(nint swapChain, float x, float y);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] static extern int OurNotesGpuAttachPanel(nint panel, nint swapChain);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] static extern int OurNotesGpuCreateTest(uint width, uint height, out nint surface);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] static extern int OurNotesGpuTestPixel(nint surface, out uint rgba);
}
