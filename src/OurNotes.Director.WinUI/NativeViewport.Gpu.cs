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
    string? displayFailure;
    Windows.Foundation.Rect lastGpuRect;
    GpuPreviewSurface? scaledGpu;
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
        gpuPanel.Width = rect.Width; gpuPanel.Height = rect.Height;
        try { attachedGpu?.SetDisplaySize(rect.Width, rect.Height); }
        catch (COMException error) { if (LastFrame is { } frame) _ = DisableGpuAsync(frame.SessionId, "GPU 画布缩放失败：" + error.Message); return; }
        gpuPanel.RenderTransform = new TranslateTransform { X = rect.X, Y = rect.Y };
        lastGpuRect = rect; scaledGpu = attachedGpu;
    }
    internal void RunGpuCompositionSmoke(Action<bool, string> check)
    {
        using var test = GpuPreviewSurface.CreateCompositionTest(64, 64);
        check(!string.IsNullOrWhiteSpace(test.AdapterName) && test.AdapterName != "未取得 D3D11 设备名称",
            "呈现设备名称由真实 D3D11 设备返回，不把请求的显卡名称当作当前设备");
        uint rgba = test.ReadCompositionTestPixel();
        test.SetDisplaySize(128, 96);
        check((rgba >> 24) == 255 && ((rgba >> 8) & 255) is >= 127 and <= 128 && ((rgba >> 16) & 255) is >= 191 and <= 192,
            "独立 WARP 检查：D3D11 交换链生成真实像素，不冒充硬件互操作验证");
        nint borrowed = test.AcquireSwapChain();
        try
        {
            var panel = ((WinRT.IWinRTObject)gpuPanel).NativeObject;
            check(borrowed != 0 && GpuPreviewSurface.AttachPanel(panel.ThisPtr, borrowed) >= 0,
                "原生 WinUI SwapChainPanel 接受独立 COM 引用的交换链");
            check(GpuPreviewSurface.AttachPanel(panel.ThisPtr, 0) >= 0, "交换链可在窗口线程解除绑定");
        }
        finally { GpuPreviewSurface.ReleaseSwapChain(borrowed); }
    }
}
