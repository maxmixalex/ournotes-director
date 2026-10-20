using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OurNotes.Director.Core;
using System.Runtime.InteropServices;

namespace OurNotes.Director.WinUI;

public sealed partial class NativeViewport
{
    readonly SwapChainPanel gpuPanel = new()
    {
        Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false
    };
    GpuPreviewSurface? attachedGpu;
    long attachedGpuSession;
    string? displayFailure;
    Windows.Foundation.Rect lastGpuRect;
    GpuPreviewSurface? scaledGpu;
    (GpuPreviewSurface Surface, long SessionId, double Width, double Height)? pendingGpuLayout;
    Task gpuLayoutWork = Task.CompletedTask;
    bool gpuLayoutRunning;
    // Explicit WARP fixture owns a separate D3D thread. Production always uses
    // the persistent native render thread below.
    Func<GpuPreviewSurface, double, double, Task>? compositionTestLayout;
    internal string? PresentationAdapterName => gpuPanel.Visibility == Visibility.Visible ? attachedGpu?.AdapterName : null;
    internal string? PresentationFailure => displayFailure ?? LastFrame?.PresentationFallback;
    void InitializeGpuPresentation()
    {
        surface.Children.Insert(surface.Children.IndexOf(overlay), gpuPanel);
        gpuPanel.CompositionScaleChanged += (_, _) => { scaledGpu = null; LayoutCanvas(); };
    }
    bool PresentGpu(PreviewFrame frame)
    {
        var gpu = frame.GpuSurface!;
        try
        {
            if (!ReferenceEquals(attachedGpu, gpu))
            {
                nint swap = gpu.AcquireSwapChain(); if (swap == 0) return false; // Newer frame already queued after a seek.
                try
                {
                    var panel = ((WinRT.IWinRTObject)gpuPanel).NativeObject;
                    Marshal.ThrowExceptionForHR(GpuPreviewSurface.AttachPanel(panel.ThisPtr, swap));
                    GC.KeepAlive(gpuPanel);
                }
                finally { GpuPreviewSurface.ReleaseSwapChain(swap); }
                attachedGpu = gpu;
            }
            attachedGpuSession = frame.SessionId;
            image.Visibility = Visibility.Collapsed; gpuPanel.Visibility = Visibility.Visible;
            checker.Visibility = Visibility.Collapsed; displayFailure = null;
            return true;
        }
        catch (Exception error) when (error is COMException or InvalidCastException or DllNotFoundException or EntryPointNotFoundException)
        {
            displayFailure = "GPU 画布绑定失败，已请求兼容显示：" + error.Message;
            _ = DisableGpuAsync(frame.SessionId, displayFailure); return false;
        }
    }
    async Task DisableGpuAsync(long expectedSession, string reason)
    { try { await session.DisableGpuPresentationAsync(reason, expectedSession); } catch (Exception error) { PresentFailure(error); } }
    void DetachGpu()
    {
        pendingGpuLayout = null; scaledGpu = null; attachedGpuSession = 0;
        if (attachedGpu is not null)
        {
            var panel = ((WinRT.IWinRTObject)gpuPanel).NativeObject;
            Marshal.ThrowExceptionForHR(GpuPreviewSurface.AttachPanel(panel.ThisPtr, 0));
            attachedGpu = null;
        }
        gpuPanel.Visibility = Visibility.Collapsed; image.Visibility = checker.Visibility = Visibility.Visible;
    }
    void LayoutGpu(Windows.Foundation.Rect rect)
    {
        if (gpuPanel.Visibility != Visibility.Visible) return;
        if (lastGpuRect == rect && ReferenceEquals(scaledGpu, attachedGpu)) return;
        bool sizeChanged = lastGpuRect.Width != rect.Width || lastGpuRect.Height != rect.Height
            || !ReferenceEquals(scaledGpu, attachedGpu);
        gpuPanel.Width = rect.Width; gpuPanel.Height = rect.Height;
        gpuPanel.RenderTransform = new TranslateTransform { X = rect.X, Y = rect.Y };
        lastGpuRect = rect; scaledGpu = attachedGpu;
        if (!sizeChanged || attachedGpu is not { } gpu || rect.Width <= 0 || rect.Height <= 0) return;
        // The matrix maps texture pixels to XAML DIPs. Composition scale belongs
        // to XAML; multiplying it here would apply monitor/ancestor scale twice.
        pendingGpuLayout = (gpu, attachedGpuSession, rect.Width, rect.Height);
        if (!gpuLayoutRunning) gpuLayoutWork = PresentGpuLayoutsAsync();
    }

    async Task PresentGpuLayoutsAsync()
    {
        gpuLayoutRunning = true;
        try
        {
            while (pendingGpuLayout is { } next)
            {
                pendingGpuLayout = null;
                if (!ReferenceEquals(attachedGpu, next.Surface) || attachedGpuSession != next.SessionId) continue;
                try
                {
                    if (compositionTestLayout is { } fixture)
                        await fixture(next.Surface, next.Width, next.Height);
                    else await session.SetGpuDisplaySizeAsync(next.Surface, next.SessionId, next.Width, next.Height);
                }
                catch (Exception error) when (error is COMException or ObjectDisposedException or DllNotFoundException or EntryPointNotFoundException)
                {
                    // A layout may finish after a project/surface replacement.
                    // An old failure must never disable the newer render session.
                    if (!disposed && ReferenceEquals(attachedGpu, next.Surface) && attachedGpuSession == next.SessionId
                        && LastFrame?.SessionId == next.SessionId)
                    {
                        scaledGpu = null;
                        await DisableGpuAsync(next.SessionId, "GPU 画布缩放失败：" + error.Message);
                    }
                    // Continue draining any newer size received during the await.
                }
            }
        }
        finally { gpuLayoutRunning = false; }
    }
    internal async Task WaitForGpuLayoutAsync()
    { do { await gpuLayoutWork; } while (gpuLayoutRunning || pendingGpuLayout is not null); }

    internal void RunGpuCompositionSmoke(Action<bool, string> check)
    {
        using var test = GpuPreviewSurface.CreateCompositionTest(64, 64);
        check(!string.IsNullOrWhiteSpace(test.AdapterName) && test.AdapterName != "未取得 D3D11 设备名称",
            "呈现设备名称由真实 D3D11 设备返回，不把请求的显卡名称当作当前设备");
        uint rgba = test.ReadCompositionTestPixel();
        // Repeated blocking Presents need an attached, visible compositor. The
        // resize/present-count checks run in RunGpuLayoutSmokeAsync below, with
        // actual desktop pixels and a dedicated D3D owner thread.
        check((rgba >> 24) == 255 && ((rgba >> 8) & 255) is >= 127 and <= 128 && ((rgba >> 16) & 255) is >= 191 and <= 192,
            "独立 WARP 检查：D3D11 交换链生成真实像素，不冒充硬件互操作验证");
        nint borrowed = test.AcquireSwapChain();
        nint previous = attachedGpu?.AcquireSwapChain() ?? 0;
        try
        {
            var panel = ((WinRT.IWinRTObject)gpuPanel).NativeObject;
            check(borrowed != 0 && GpuPreviewSurface.AttachPanel(panel.ThisPtr, borrowed) >= 0,
                "原生 WinUI SwapChainPanel 接受独立 COM 引用的交换链");
            check(GpuPreviewSurface.AttachPanel(panel.ThisPtr, 0) >= 0, "交换链可在窗口线程解除绑定");
        }
        finally
        {
            try
            {
                var panel = ((WinRT.IWinRTObject)gpuPanel).NativeObject;
                Marshal.ThrowExceptionForHR(GpuPreviewSurface.AttachPanel(panel.ThisPtr, previous));
            }
            finally
            {
                GpuPreviewSurface.ReleaseSwapChain(previous);
                GpuPreviewSurface.ReleaseSwapChain(borrowed);
            }
        }
    }
}
