using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;
using Windows.System;

namespace OurNotes.Director.WinUI;

internal sealed partial class Live2DStudioPage
{
    internal async Task RunModelQualitySmokeAsync(string archive, Action<bool, string> check)
    {
        await scrubber.FlushAsync();
        Pause();
        await FlushEditsAsync();
        if (document is not null) SaveTo(App.SmokeEvidencePath("before-hotaru-quality.live2d.json"));
        window.Settings.ArchivePath = archive;
        const string model = "adv_live2d_hotaru_016_live_01";
        await OpenModelAsync(model, "汐见萤 · 演出服", site: "intl", language: "zh-Hans");
        await WaitForReadyAsync();
        await ApplyPreviewSizeAsync();
        var current = document ?? throw new InvalidOperationException("萤的演出服项目未打开。");
        var before = viewport.LastFrame ?? throw new InvalidOperationException("萤的演出服预览未呈现。");
        check(current.ModelId == model && viewport.HasCurrentSource && viewport.PreviewSupersample >= 2
            && before.Targets.Count == 1,
            "真实 Live2D 工作台选择萤演出服，正常预览使用超采样");
        bool windowedCpu = before.Pixels.ByteLength == before.Pixels.Width * before.Pixels.Height * 4;
        if (windowedCpu)
            check(before.Pixels.Rgba.Where((_, index) => index % 4 == 3).Any(alpha => alpha is > 0 and < 255),
                "萤演出服 CPU 预览保留真实半透明角色边缘");
        check(current.ToSceneDocument().Output.CineformQuality == "film3+",
            "新独立角色项目默认使用 film3+ 保留较弱的透明边缘覆盖率");
        await VerifyPreviewAntialiasingSmokeAsync(check);
        string unchanged = current.ToJson();
        var exporting = ExportAsync("clip");
        ContentDialog? dialog = null;
        try
        {
            dialog = await WaitForSmokeDialogAsync(exporting);
            var quality = SmokeControl<ComboBox>(dialog, "CineForm 质量");
            check(Ui.Selected(quality) == "film3+" && quality.Items.Count == 13,
                "萤演出服的真实导出窗口显示全部质量档位，默认 Film 3+ 没有被回退成 Film 1");
            SmokeChoice(dialog, "CineForm 质量", "film1");
            check(Ui.Selected(quality) == "film1", "真实质量控件仍允许明确选择均衡 Film 1");
            SmokeChoice(dialog, "CineForm 质量", "film3+");
            check(Ui.Selected(quality) == "film3+", "真实质量控件可重新选中最高 Film 3+");
            await App.CapturePopupCheckpointAsync(dialog, "hotaru-export-quality");
        }
        finally
        {
            dialog ??= VisualTreeHelper.GetOpenPopupsForXamlRoot(window.RootXaml).Select(popup => popup.Child)
                .OfType<DependencyObject>().SelectMany(SmokeTree).OfType<ContentDialog>()
                .FirstOrDefault(candidate => candidate.PrimaryButtonText == "开始导出");
            dialog?.Hide();
            await exporting.WaitAsync(TimeSpan.FromSeconds(20));
        }
        check(ReferenceEquals(document, current) && current.ToJson() == unchanged,
            "检查并关闭导出窗口保持萤的模型项目和预览姿态");
        if (windowedCpu)
            await File.WriteAllBytesAsync(App.SmokeEvidencePath("hotaru-live-windowed-native.png"), Png.Encode(before.Pixels));
        try
        {
            viewport.Focus(FocusState.Keyboard);
            await viewport.SendShellKeySmokeAsync(window, VirtualKey.F1);
            (window.Content as FrameworkElement)?.UpdateLayout();
            await ApplyPreviewSizeAsync();
            await viewport.WaitForGpuLayoutAsync();
            viewport.VerifyFullscreenResolutionSmoke("hotaru-live", check);
            var fullscreen = viewport.LastFrame ?? throw new InvalidOperationException("萤的演出服全屏预览未呈现。");
            check(window.IsMonitorFullscreen && viewport.PreviewSupersample >= 2 && fullscreen.Frame == before.Frame
                && fullscreen.SessionId == before.SessionId && !viewport.IsPlaying,
                "萤演出服的实际 F1 全屏保持超采样、同一模型与暂停姿态");
            if (fullscreen.Pixels.ByteLength == fullscreen.Pixels.Width * fullscreen.Pixels.Height * 4)
                await File.WriteAllBytesAsync(App.SmokeEvidencePath("hotaru-live-fullscreen-native.png"), Png.Encode(fullscreen.Pixels));
            await App.CaptureCheckpointAsync("hotaru-live-fullscreen");
            await viewport.CapturePresentedDesktopSmokeAsync(window, "hotaru-live-fullscreen-desktop");
            await viewport.SendShellKeySmokeAsync(window, VirtualKey.F1);
            (window.Content as FrameworkElement)?.UpdateLayout();
            await ApplyPreviewSizeAsync();
            var restored = viewport.LastFrame ?? throw new InvalidOperationException("萤的窗口预览未恢复。");
            check(!window.IsMonitorFullscreen && restored.Frame == before.Frame && restored.SessionId == before.SessionId
                && restored.Pixels.Width == before.Pixels.Width && restored.Pixels.Height == before.Pixels.Height,
                "萤演出服全屏往返恢复窗口尺寸，没有重建模型或推进动画");
            bool restoredCpu = restored.Pixels.ByteLength == restored.Pixels.Width * restored.Pixels.Height * 4;
            if (windowedCpu && restoredCpu)
                check(restored.Pixels.Rgba.AsSpan().SequenceEqual(before.Pixels.Rgba),
                    "萤演出服 CPU 画面全屏往返后逐像素恢复窗口姿态");
            DesktopSettings.AtomicWrite(App.SmokeEvidencePath("hotaru-live-workbench.json"), new JsonObject
            {
                ["schema"] = "ournotes-director/model-quality-smoke/1", ["ok"] = true, ["model"] = model,
                ["cineformQuality"] = current.ToSceneDocument().Output.CineformQuality,
                ["windowedWidth"] = before.Pixels.Width, ["windowedHeight"] = before.Pixels.Height,
                ["fullscreenWidth"] = fullscreen.Pixels.Width, ["fullscreenHeight"] = fullscreen.Pixels.Height,
                ["supersample"] = viewport.PreviewSupersample, ["frame"] = before.Frame,
                ["cpuPixelRestorationVerified"] = windowedCpu && restoredCpu,
                ["windowedSha256"] = windowedCpu ? Archive.Hash(before.Pixels.Rgba) : null,
                ["restoredSha256"] = restoredCpu ? Archive.Hash(restored.Pixels.Rgba) : null, ["device"] = before.Device
            }.ToJsonString(DesktopSettings.JsonOptions));
        }
        finally { Pause(); window.ExitMonitorFullscreen(); }
    }

    async Task VerifyPreviewAntialiasingSmokeAsync(Action<bool, string> check)
    {
        var current = document ?? throw new InvalidOperationException("抗锯齿回归缺少实际模型项目。");
        var before = viewport.LastFrame ?? throw new InvalidOperationException("抗锯齿回归缺少实际模型画面。");
        string unchanged = current.ToJson();
        check(viewport.PreviewSupersample == 2 && previewSampling == 2,
            "Live2D 工作台默认使用 2× 预览，没有强制增加交互负担");
        await InvokePreviewAntialiasingSmokeAsync(4, check);
        var fine = viewport.LastFrame ?? throw new InvalidOperationException("4× 角色预览未呈现。");
        check(viewport.PreviewSupersample == 4 && fine.SessionId == before.SessionId && fine.Frame == before.Frame
            && fine.Pixels.Width == before.Pixels.Width && fine.Pixels.Height == before.Pixels.Height && !viewport.IsPlaying,
            "实际预览抗锯齿菜单切到 4×，保留同一模型会话、帧与显示尺寸");
        bool fineCpu = fine.Pixels.ByteLength == fine.Pixels.Width * fine.Pixels.Height * 4;
        if (fineCpu)
            await File.WriteAllBytesAsync(App.SmokeEvidencePath("hotaru-live-preview-4x-native.png"), Png.Encode(fine.Pixels));
        await App.CaptureCheckpointAsync("hotaru-live-preview-4x");
        await viewport.CapturePresentedDesktopSmokeAsync(window, "hotaru-live-preview-4x-desktop");
        await InvokePreviewAntialiasingSmokeAsync(2, check);
        var restored = viewport.LastFrame ?? throw new InvalidOperationException("2× 角色预览未恢复。");
        check(viewport.PreviewSupersample == 2 && restored.SessionId == before.SessionId && restored.Frame == before.Frame
            && restored.Pixels.Width == before.Pixels.Width && restored.Pixels.Height == before.Pixels.Height,
            "实际预览抗锯齿菜单切回 2×，保留帧、会话和显示尺寸");
        bool originalCpu = before.Pixels.ByteLength == before.Pixels.Width * before.Pixels.Height * 4;
        bool restoredCpu = restored.Pixels.ByteLength == restored.Pixels.Width * restored.Pixels.Height * 4;
        if (originalCpu && restoredCpu)
            check(restored.Pixels.Rgba.AsSpan().SequenceEqual(before.Pixels.Rgba),
                "萤演出服 2×→4×→2× 的 CPU 预览逐像素恢复");
        check(ReferenceEquals(document, current) && current.ToJson() == unchanged,
            "切换预览抗锯齿不修改角色项目、姿态或导出质量");
        DesktopSettings.AtomicWrite(App.SmokeEvidencePath("hotaru-preview-antialiasing.json"), new JsonObject
        {
            ["schema"] = "ournotes-director/model-preview-antialiasing-smoke/1", ["ok"] = true,
            ["model"] = current.ModelId, ["width"] = before.Pixels.Width, ["height"] = before.Pixels.Height,
            ["frame"] = before.Frame, ["sessionId"] = before.SessionId,
            ["defaultSupersample"] = 2, ["selectedSupersample"] = 4, ["restoredSupersample"] = viewport.PreviewSupersample,
            ["cpuPixelRestorationVerified"] = originalCpu && restoredCpu,
            ["originalSha256"] = originalCpu ? Archive.Hash(before.Pixels.Rgba) : null,
            ["fineSha256"] = fineCpu ? Archive.Hash(fine.Pixels.Rgba) : null,
            ["restoredSha256"] = restoredCpu ? Archive.Hash(restored.Pixels.Rgba) : null,
            ["device"] = before.Device
        }.ToJsonString(DesktopSettings.JsonOptions));
    }

    async Task InvokePreviewAntialiasingSmokeAsync(int sampling, Action<bool, string> check)
    {
        var menu = ViewMenu();
        menu.ShowAt(viewport);
        try
        {
            var group = menu.Items.OfType<MenuFlyoutSubItem>().Single(item => item.Text == "预览抗锯齿");
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while ((!group.IsLoaded || group.ActualWidth <= 0) && DateTime.UtcNow < deadline) await Task.Delay(20);
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(group);
            if (peer?.GetPattern(PatternInterface.ExpandCollapse) is not IExpandCollapseProvider expand)
                throw new InvalidOperationException("预览抗锯齿子菜单缺少原生展开接口。");
            expand.Expand();
            var choices = group.Items.OfType<ToggleMenuFlyoutItem>().ToArray();
            var choice = choices.Single(item => item.Tag is int value && value == sampling);
            deadline = DateTime.UtcNow.AddSeconds(5);
            while ((!choice.IsLoaded || choice.ActualWidth <= 0) && DateTime.UtcNow < deadline) await Task.Delay(20);
            if (!choice.IsLoaded || choice.ActualWidth <= 0 || !choice.IsEnabled)
                throw new InvalidOperationException("实际预览抗锯齿菜单尚未完成布局。");
            check(choices.Length == 2 && choices.Count(item => item.IsChecked) == 1,
                "实际预览抗锯齿子菜单显示互斥的 2× 与 4× 选项");
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            RoutedEventHandler clicked = (_, _) => completed.TrySetResult();
            choice.Click += clicked;
            try
            {
                new ToggleMenuFlyoutItemAutomationPeer(choice).Toggle();
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
                await previewQualityTask.WaitAsync(TimeSpan.FromSeconds(60));
            }
            finally { choice.Click -= clicked; }
            check(choice.IsChecked && choices.Count(item => item.IsChecked) == 1,
                $"实际菜单 Action 选中 {sampling}× 并取消其他预览档位");
        }
        finally { menu.Hide(); }
    }
}
