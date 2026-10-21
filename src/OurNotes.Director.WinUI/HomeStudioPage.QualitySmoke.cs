using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;
using Windows.System;

namespace OurNotes.Director.WinUI;

internal sealed partial class HomeStudioPage
{
    // Use the reported homepage in the actual packaged editor. The native quality
    // suite separately compares the railing pixels against supersampled references.
    internal async Task RunRealQualitySmokeAsync(string archive, Action<bool, string> check)
    {
        var savedDocument = document; var savedMetadata = metadata; string? savedArchive = archiveOverride;
        await scrubber.FlushAsync();
        Pause();
        try
        {
            archiveOverride = archive;
            var data = await Task.Run(() => ReadMetadata(archive, new(40001, "intl", 10844), "zh-Hans"));
            check(data.Source.SpotId == 40001 && data.Source.AdvId == 10844 && data.Characters.Count > 0,
                "真实主页回归载入 millsage「一如既往的光景」40001 的原始房间与角色");
            var project = new HomeProject(data.Source)
            {
                Duration = Math.Max(1d / 30, data.Duration), Output = new(1920, 1080, 30, 1)
            };
            SetDocument(new(project), data);
            await ReloadPreviewAsync();
            await viewport.PauseForEditingAsync();
            var before = viewport.LastFrame ?? throw new InvalidOperationException("真实主页没有呈现画面。");
            check(before.Targets.Count > 0 && before.Pixels.Width > 0 && before.Pixels.Height > 0,
                "真实主页通过工作台生产加载路径呈现房间与可选择角色");
            bool windowedCpu = before.Pixels.ByteLength == before.Pixels.Width * before.Pixels.Height * 4;
            if (windowedCpu)
                await File.WriteAllBytesAsync(App.SmokeEvidencePath("millsage-home-windowed-native.png"), Png.Encode(before.Pixels));

            viewport.Focus(FocusState.Keyboard);
            await viewport.SendShellKeySmokeAsync(window, VirtualKey.F1);
            (window.Content as FrameworkElement)?.UpdateLayout();
            await ApplyPresentationSizeAsync();
            await viewport.WaitForGpuLayoutAsync();
            viewport.VerifyFullscreenResolutionSmoke("millsage-home", check);
            var fullscreen = viewport.LastFrame ?? throw new InvalidOperationException("全屏主页没有呈现画面。");
            check(window.IsMonitorFullscreen && viewport.PreviewSupersample >= 2,
                "真实主页 F1 使用超采样输出，原生尺寸正确时仍对房间栏杆几何边缘抗锯齿");
            check(fullscreen.SessionId == before.SessionId && fullscreen.Frame == before.Frame && !viewport.IsPlaying,
                "真实主页提高全屏采样质量保留同一原生会话和暂停时刻");
            bool fullscreenCpu = fullscreen.Pixels.ByteLength == fullscreen.Pixels.Width * fullscreen.Pixels.Height * 4;
            if (fullscreenCpu)
                await File.WriteAllBytesAsync(App.SmokeEvidencePath("millsage-home-fullscreen-native.png"), Png.Encode(fullscreen.Pixels));
            await App.CaptureCheckpointAsync("millsage-home-fullscreen");
            await viewport.CapturePresentedDesktopSmokeAsync(window, "millsage-home-fullscreen-desktop");

            await viewport.SendShellKeySmokeAsync(window, VirtualKey.F1);
            (window.Content as FrameworkElement)?.UpdateLayout();
            await ApplyPresentationSizeAsync();
            var restored = viewport.LastFrame ?? throw new InvalidOperationException("主页窗口尺寸未恢复。");
            check(!window.IsMonitorFullscreen && restored.SessionId == before.SessionId
                && restored.Frame == before.Frame && restored.Pixels.Width == before.Pixels.Width
                && restored.Pixels.Height == before.Pixels.Height,
                "真实主页退出 F1 恢复窗口像素尺寸与同一时刻");
            bool restoredCpu = restored.Pixels.ByteLength == restored.Pixels.Width * restored.Pixels.Height * 4;
            if (windowedCpu && restoredCpu)
                check(restored.Pixels.Rgba.AsSpan().SequenceEqual(before.Pixels.Rgba),
                    "真实主页 CPU 画面全屏往返后逐像素恢复，不改变原房间或动画状态");
            DesktopSettings.AtomicWrite(App.SmokeEvidencePath("millsage-home-quality.json"), new JsonObject
            {
                ["schema"] = "ournotes-director/home-quality-smoke/1", ["ok"] = true,
                ["spot"] = data.Source.SpotId, ["adv"] = data.Source.AdvId, ["name"] = data.Name,
                ["windowedWidth"] = before.Pixels.Width, ["windowedHeight"] = before.Pixels.Height,
                ["fullscreenWidth"] = fullscreen.Pixels.Width, ["fullscreenHeight"] = fullscreen.Pixels.Height,
                ["frame"] = fullscreen.Frame, ["session"] = fullscreen.SessionId,
                ["cpuPixelRestorationVerified"] = windowedCpu && restoredCpu,
                ["windowedSha256"] = windowedCpu ? Archive.Hash(before.Pixels.Rgba) : null,
                ["fullscreenSha256"] = fullscreenCpu ? Archive.Hash(fullscreen.Pixels.Rgba) : null,
                ["restoredSha256"] = restoredCpu ? Archive.Hash(restored.Pixels.Rgba) : null, ["device"] = fullscreen.Device
            }.ToJsonString(DesktopSettings.JsonOptions));
        }
        finally
        {
            Pause(); window.ExitMonitorFullscreen();
            archiveOverride = savedArchive;
            if (savedDocument is not null)
            {
                SetDocument(savedDocument, savedMetadata);
                await ReloadPreviewAsync();
            }
            else
            {
                viewport.InvalidateSource(); document = null; metadata = null; previewLoaded = false;
                welcome.Visibility = Visibility.Visible;
            }
        }
    }
}
