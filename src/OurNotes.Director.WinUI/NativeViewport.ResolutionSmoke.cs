using System.Text.Json.Nodes;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.WinUI;

public sealed partial class NativeViewport
{
    internal void VerifyFullscreenResolutionSmoke(string workspace, Action<bool, string> check)
    {
        var frame = LastFrame ?? throw new InvalidOperationException("全屏分辨率检查需要真实原生帧。");
        double dpi = XamlRoot?.RasterizationScale ?? 1;
        double displayedWidth = imageRect.Width * dpi, displayedHeight = imageRect.Height * dpi;
        bool passed = IsFullscreenRendering && frame.Pixels.Width + 1e-6 >= displayedWidth
            && frame.Pixels.Height + 1e-6 >= displayedHeight && frame.ScenePreviewScale == 1;
        DesktopSettings.AtomicWrite(App.SmokeEvidencePath(workspace + "-fullscreen-resolution.json"), new JsonObject
        {
            ["workspace"] = workspace, ["passed"] = passed, ["dpi"] = dpi,
            ["displayedPixelWidth"] = displayedWidth, ["displayedPixelHeight"] = displayedHeight,
            ["nativeWidth"] = frame.Pixels.Width, ["nativeHeight"] = frame.Pixels.Height,
            ["supersample"] = PreviewSupersample, ["scenePreviewScale"] = frame.ScenePreviewScale,
            ["sessionId"] = frame.SessionId, ["frame"] = frame.Frame, ["playing"] = frame.IsPlaying,
            ["windowedWidth"] = WindowedPreviewSize?.Width, ["windowedHeight"] = WindowedPreviewSize?.Height
        }.ToJsonString(DesktopSettings.JsonOptions));
        check(passed, workspace + "：F1 全屏原生像素覆盖实际显示画布，场景不是较小预览帧的放大图像");
    }
}
