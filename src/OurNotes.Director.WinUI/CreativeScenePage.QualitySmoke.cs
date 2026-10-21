using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;
using Windows.System;

namespace OurNotes.Director.WinUI;

internal sealed partial class CreativeScenePage
{
    // Exercise the reported long original story through the packaged editor's
    // import, timeline and presentation paths, in addition to native shader tests.
    internal async Task RunOriginalQualitySmokeAsync(string archive, Action<bool, string> check)
    {
        await scrubber.FlushAsync();
        viewport.Pause();
        // Preserve the preceding smoke project through its regular save path so
        // the real import does not stop at the unsaved-document dialog.
        SaveTo(App.SmokeEvidencePath("before-millsage-quality.creative.json"));
        window.Settings.ArchivePath = archive;
        await ImportNativeAsync(SceneTemplates.Create("story-patch", new SceneSource("10060", "intl", "zh-Hans")).Text, archive);
        check(S(document.Source, "adv") == "10060" && document.Duration > 116
            && viewport.HasCurrentSource && !previewDirty && LastStoryImportReport is not null,
            "真实工作台导入 millsage「彩虹色的光芒」完整原剧情，完成可编辑预览与原始时序解析");
        var timer = Stopwatch.StartNew();
        await SeekAsync(116);
        double seekMilliseconds = timer.Elapsed.TotalMilliseconds;
        var before = viewport.LastFrame ?? throw new InvalidOperationException("116 秒原剧情未呈现。");
        check(before.Frame == 3480 && before.ScenePreviewScale == 1 && before.Targets.Count > 0,
            "真实剧情监视器定位 01:56 的第 3480 帧，停止拖动后呈现完整质量");
        bool windowedCpu = before.Pixels.ByteLength == before.Pixels.Width * before.Pixels.Height * 4;
        if (windowedCpu)
            await File.WriteAllBytesAsync(App.SmokeEvidencePath("millsage-blur-116-windowed-native.png"), Png.Encode(before.Pixels));
        await App.CaptureCheckpointAsync("millsage-blur-116-windowed");

        try
        {
            viewport.Focus(FocusState.Keyboard);
            await viewport.SendShellKeySmokeAsync(window, VirtualKey.F1);
            (window.Content as FrameworkElement)?.UpdateLayout();
            await ApplyPresentationSizeAsync();
            await viewport.WaitForGpuLayoutAsync();
            viewport.VerifyFullscreenResolutionSmoke("millsage-blur", check);
            var fullscreen = viewport.LastFrame ?? throw new InvalidOperationException("116 秒全屏画面未呈现。");
            check(window.IsMonitorFullscreen && fullscreen.Frame == before.Frame
                && fullscreen.SessionId == before.SessionId && fullscreen.ScenePreviewScale == 1 && !viewport.IsPlaying,
                "116 秒景深画面调整全屏尺寸后保留同一原生模型、播放头和完整质量");
            if (fullscreen.Pixels.ByteLength == fullscreen.Pixels.Width * fullscreen.Pixels.Height * 4)
                await File.WriteAllBytesAsync(App.SmokeEvidencePath("millsage-blur-116-fullscreen-native.png"), Png.Encode(fullscreen.Pixels));
            await App.CaptureCheckpointAsync("millsage-blur-116-fullscreen");
            await viewport.CapturePresentedDesktopSmokeAsync(window, "millsage-blur-116-fullscreen-desktop");
            await viewport.SendShellKeySmokeAsync(window, VirtualKey.F1);
            (window.Content as FrameworkElement)?.UpdateLayout();
            await ApplyPresentationSizeAsync();
            var restored = viewport.LastFrame ?? throw new InvalidOperationException("116 秒窗口画面未恢复。");
            check(!window.IsMonitorFullscreen && restored.Frame == before.Frame && restored.SessionId == before.SessionId
                && restored.Pixels.Width == before.Pixels.Width && restored.Pixels.Height == before.Pixels.Height,
                "原剧情景深目标随全屏尺寸重建，退出后恢复窗口尺寸与同帧会话");
            bool restoredCpu = restored.Pixels.ByteLength == restored.Pixels.Width * restored.Pixels.Height * 4;
            if (windowedCpu && restoredCpu)
                check(restored.Pixels.Rgba.AsSpan().SequenceEqual(before.Pixels.Rgba),
                    "原剧情 116 秒 CPU 画面在全屏往返后逐像素恢复");
            DesktopSettings.AtomicWrite(App.SmokeEvidencePath("millsage-blur-116-workbench.json"), new JsonObject
            {
                ["schema"] = "ournotes-director/original-quality-smoke/1", ["ok"] = true,
                ["adv"] = "10060", ["time"] = 116, ["frame"] = before.Frame,
                ["seekMilliseconds"] = seekMilliseconds, ["import"] = LastStoryImportReport?.DeepClone(),
                ["windowedWidth"] = before.Pixels.Width, ["windowedHeight"] = before.Pixels.Height,
                ["fullscreenWidth"] = fullscreen.Pixels.Width, ["fullscreenHeight"] = fullscreen.Pixels.Height,
                ["cpuPixelRestorationVerified"] = windowedCpu && restoredCpu,
                ["windowedSha256"] = windowedCpu ? Archive.Hash(before.Pixels.Rgba) : null,
                ["restoredSha256"] = restoredCpu ? Archive.Hash(restored.Pixels.Rgba) : null, ["device"] = before.Device
            }.ToJsonString(DesktopSettings.JsonOptions));
            await RunOriginalFrameHistorySmokeAsync(check);
        }
        finally { viewport.Pause(); window.ExitMonitorFullscreen(); }
    }
}
