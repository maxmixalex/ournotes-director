using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.WinUI;

internal sealed partial class CreativeScenePage
{
    // Runs only from the packaged Windows --smoke-test entry point. The editor,
    // real dialog controls, task queue and shipped renderer own every operation.
    private async Task VerifyProjectExportWorkflowAsync(Action<bool, string> check, string actor)
    {
        check(document.Source["adv"] is null && document.Actors.Count >= 2,
            "保存与导出回归使用从空白界面建立的真实双人场景，没有原剧情编号");
        string project = App.SmokeEvidencePath("export-workflow.scene.json");
        string picture = App.SmokeEvidencePath("export-current-pose-" + Guid.NewGuid().ToString("N") + ".png");
        double poseTime = playhead;
        SaveTo(project);
        string saved = document.ToJson();
        string[] actorIds = document.Actors.OfType<JsonObject>().Select(a => S(a, "id")).ToArray();
        var workRange = new TimelineRange(15, 46, 30);
        timeline.SetRange(workRange);
        check(timeline.WorkRange == workRange, "真实时间轴设置包含31帧的预览与导出区间");
        await OpenAsync(project);
        check(timeline.WorkRange is null && !HasUnsavedChanges,
            "通过实际打开入口重开同一素材来源的项目，会清除旧入出点并保持已保存状态");
        check(document.ToJson() == saved && document.Source["adv"] is null
            && document.Actors.OfType<JsonObject>().Select(a => S(a, "id")).SequenceEqual(actorIds),
            "保存并重开自由创作场景后，双人、素材与完整演出内容保持一致，不要求source.adv");
        await SeekAsync(poseTime);
        check(viewport.HasCurrentSource && viewport.LastFrame?.Targets.Any(target => target.Id == actor) == true,
            "重开的自由创作项目在真实画布中恢复所选人物及当前演出姿态");
        selectedActor = actor; selectedClip = replaceAssetClip = null;
        Refresh(); viewport.SelectedTargetId = actor;
        timeline.SetRange(workRange);
        string beforeExport = document.ToJson();

        var actorMenu = ActorMenu();
        check(actorMenu.Items.OfType<MenuFlyoutItem>().Any(item => item.Text == "在 Live2D 工作台中打开…")
            && !actorMenu.Items.OfType<MenuFlyoutItem>().Any(item => item.Text.StartsWith("导出", StringComparison.Ordinal)),
            "场景角色菜单只提供独立Live2D入口，单模型输出已迁出场景编排");

        var clip = document.Clips.OfType<JsonObject>().First(c => S(c, "actor") == actor && S(c, "kind") == "motion");
        selectedClip = S(clip, "id"); selectedActor = actor;
        timeline.Select(selectedClip, true);
        TimelineRange clipRange = RangeForClip(clip);
        check(clipRange != workRange, "片段导出回归使用不同于工作区间的真实动作片段");
        check(timeline.SelectedRange() == clipRange && Math.Abs(clipRange.Duration - N(clip, "duration")) > 1e-8,
            "片段导出范围等于真实时间轴显示的保持区间，覆盖素材时长与下次切换位置不同的情况");
        await VerifyMenuExportRangeAsync(check, () => ClipMenu(clip), "导出这一段…", clipRange,
            "export-clip-range-dialog", "片段右键导出按所选片段范围填写实际对话框");
        check(timeline.WorkRange == workRange && document.ToJson() == beforeExport,
            "打开并取消片段导出，不覆盖工作区间，也不修改项目内容");

        selectedActor = actor; selectedClip = null; Refresh(); viewport.SelectedTargetId = actor;
        await SeekAsync(poseTime);
        var oldTasks = window.Jobs.Tasks.Select(job => job.Id).ToHashSet(StringComparer.Ordinal);
        Task exporting = ExportAsync("clip");
        ContentDialog? dialog = null;
        try
        {
            dialog = await WaitForExportSmokeDialogAsync(exporting);
            check(Ui.Selected(ExportSmokeControl<ComboBox>(dialog, "时间范围")) == "work"
                && Math.Abs(ExportSmokeControl<NumberBox>(dialog, "开始（秒）").Value - workRange.Start) < 1e-9
                && Math.Abs(ExportSmokeControl<NumberBox>(dialog, "时长（秒）").Value - workRange.Duration) < 1e-9,
                "场景总导出沿用当前31帧工作区间");
            ExportSmokeChoice(dialog, "导出内容", "frame");
            ExportSmokeChoice(dialog, "画面尺寸", "custom");
            ExportSmokeControl<NumberBox>(dialog, "宽度").Value = 256;
            ExportSmokeControl<NumberBox>(dialog, "高度").Value = 384;
            ExportSmokeControl<TextBox>(dialog, "保存到").Text = picture;
            dialog.UpdateLayout();
            check(Ui.Selected(ExportSmokeControl<ComboBox>(dialog, "导出内容")) == "frame"
                && ExportSmokeControl<NumberBox>(dialog, "宽度").Value == 256
                && ExportSmokeControl<NumberBox>(dialog, "高度").Value == 384,
                "在实际导出窗口切换PNG并填写256×384像素与输出路径");
            await App.CapturePopupCheckpointAsync(dialog, "export-current-pose-dialog");
            await PressExportSmokeButtonAsync(dialog, true);
            await exporting.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally { dialog?.Hide(); }

        var jobs = window.Jobs.Tasks.Where(job => !oldTasks.Contains(job.Id)).ToArray();
        check(jobs.Length == 1 && jobs[0].Request.CommandId == "render.frame"
            && jobs[0].Request.OutputPath == picture,
            "实际开始导出按钮通过命令构建器向正常队列提交一次PNG任务");
        var job = jobs[0];
        int timeIndex = job.Request.Arguments.ToList().IndexOf("--time");
        check(timeIndex >= 0 && timeIndex + 1 < job.Request.Arguments.Count
            && double.TryParse(job.Request.Arguments[timeIndex + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double renderTime)
            && Math.Abs(renderTime - poseTime) < 1e-9,
            "PNG队列使用实际播放头姿态，不被工作区间入点或先前右键片段的起点替换");
        string queuedProject = job.Request.Arguments[2];
        var input = SceneDocument.Open(queuedProject);
        check(input.Source.IsLibrary && input.Root["source"]?["adv"] is null
            && input.Root["output"]?["isolateActor"] is null && !input.Validate().HasErrors,
            "场景导出队列持久化有效的无source.adv完整场景，不偷偷隔离当前所选人物");
        File.Copy(queuedProject, App.SmokeEvidencePath("export-queued-input.project.json"), true);
        EngineResult result;
        try { result = await job.Completion.WaitAsync(TimeSpan.FromSeconds(60)); }
        catch (TimeoutException) { window.Jobs.Cancel(job); throw; }
        var report = new JsonObject
        {
            ["project"] = project, ["picture"] = picture, ["actor"] = actor,
            ["command"] = job.Request.CommandId,
            ["arguments"] = new JsonArray(job.Request.Arguments.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            ["exitCode"] = result.ExitCode, ["success"] = result.Success, ["error"] = result.Error, ["log"] = result.Log,
            ["startFrame"] = workRange.StartFrame, ["endFrameExclusive"] = workRange.EndFrameExclusive,
            ["result"] = result.Json?.DeepClone()
        };
        string reportPath = App.SmokeEvidencePath("export-workflow.json");
        DesktopSettings.AtomicWrite(reportPath, report.ToJsonString(DesktopSettings.JsonOptions));
        check(result.Success && job.Status == DesktopTaskStatus.Succeeded && File.Exists(picture),
            "随包advkit执行真实PNG导出并成功落盘" + (result.Success ? "" : "：" + result.Error));
        var png = Png.Decode(await File.ReadAllBytesAsync(picture));
        int transparent = 0, visible = 0;
        for (int i = 3; i < png.Rgba.Length; i += 4)
        {
            if (png.Rgba[i] == 0) transparent++;
            if (png.Rgba[i] > 128) visible++;
        }
        check(png.Width == 256 && png.Height == 384 && visible > 0,
            "解码实际场景PNG，确认尺寸正确且完整场景并非空白");
        check(timeline.WorkRange == workRange && document.ToJson() == beforeExport,
            "真实导出完成后，原场景与时间轴工作区间仍保持不变");
        report["width"] = png.Width; report["height"] = png.Height;
        report["transparentPixels"] = transparent; report["visiblePixels"] = visible;
        DesktopSettings.AtomicWrite(reportPath, report.ToJsonString(DesktopSettings.JsonOptions));
        await VerifyLive2DHandoffAsync(check, actor, poseTime, workRange);
    }

    async Task VerifyLive2DHandoffAsync(Action<bool,string> check, string actor, double time, TimelineRange range)
    {
        selectedActor = actor; selectedClip = null; Refresh(); await SeekAsync(time); timeline.SetRange(range);
        string original = document.ToJson();
        var menu = ActorMenu(); menu.ShowAt(viewport);
        try
        {
            var item = menu.Items.OfType<MenuFlyoutItem>().Single(entry => entry.Text == "在 Live2D 工作台中打开…");
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!item.IsLoaded && DateTime.UtcNow < deadline) await Task.Delay(20);
            new MenuFlyoutItemAutomationPeer(item).Invoke();
            deadline = DateTime.UtcNow.AddSeconds(30);
            while (window.ActivePage != "live2d" && DateTime.UtcNow < deadline) await Task.Delay(30);
            check(window.ActivePage == "live2d", "实际角色右键菜单导航到独立Live2D工作台");
            await window.Live2D.WaitForReadyAsync();
            check(document.ToJson() == original && timeline.WorkRange == range,
                "场景送到Live2D工作台后，原项目与入出点保持不变");
            await window.Live2D.VerifySceneHandoffAsync(check, actor, time, range);
            await App.CaptureCheckpointAsync("live2d-from-scene");
        }
        finally { menu.Hide(); }
    }

    private async Task VerifyMenuExportRangeAsync(Action<bool, string> check, Func<MenuFlyout> create,
        string itemText, TimelineRange expected, string captureName, string description)
    {
        var originalRange = timeline.WorkRange;
        string original = document.ToJson();
        int taskCount = window.Jobs.Tasks.Count;
        var menu = create(); ContentDialog? dialog = null;
        try
        {
            menu.ShowAt(timeline);
            var item = menu.Items.OfType<MenuFlyoutItem>().Single(entry => entry.Text == itemText);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while ((!item.IsLoaded || item.ActualWidth <= 0) && DateTime.UtcNow < deadline) await Task.Delay(25);
            if (!item.IsLoaded || item.ActualWidth <= 0 || !item.IsEnabled)
                throw new InvalidOperationException("实际右键菜单项没有完成布局或不可用：" + itemText);
            new MenuFlyoutItemAutomationPeer(item).Invoke();
            dialog = await WaitForExportSmokeDialogAsync();
            check(Ui.Selected(ExportSmokeControl<ComboBox>(dialog, "时间范围")) == "work"
                && Math.Abs(ExportSmokeControl<NumberBox>(dialog, "开始（秒）").Value - expected.Start) < 1e-9
                && Math.Abs(ExportSmokeControl<NumberBox>(dialog, "时长（秒）").Value - expected.Duration) < 1e-9,
                description);
            await App.CapturePopupCheckpointAsync(dialog, captureName);
            await PressExportSmokeButtonAsync(dialog, false);
            await Task.Delay(25);
            check(timeline.WorkRange == originalRange && document.ToJson() == original && window.Jobs.Tasks.Count == taskCount,
                "取消“" + itemText + "”后保留原区间和项目，且没有排入导出任务");
        }
        finally { dialog?.Hide(); menu.Hide(); }
    }

    private async Task<ContentDialog> WaitForExportSmokeDialogAsync(Task? operation = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (operation?.IsCompleted == true)
            {
                await operation;
                throw new InvalidOperationException("导出操作结束，但没有打开实际导出窗口。");
            }
            var dialogs = VisualTreeHelper.GetOpenPopupsForXamlRoot(window.RootXaml)
                .Select(popup => popup.Child).OfType<DependencyObject>().SelectMany(ExportSmokeTree)
                .OfType<ContentDialog>().ToArray();
            var dialog = dialogs.FirstOrDefault(candidate => candidate.PrimaryButtonText == "开始导出");
            if (dialog is not null)
            {
                dialog.UpdateLayout();
                if (dialog.IsLoaded && dialog.ActualWidth > 0
                    && ExportSmokeTree(dialog).OfType<Button>().Any(button => button.Content?.ToString() == "开始导出"))
                    return dialog;
            }
            var unexpected = dialogs.FirstOrDefault(candidate => candidate.PrimaryButtonText != "开始导出");
            if (unexpected is not null)
                throw new InvalidOperationException("导出被另一个窗口阻断：" + unexpected.Title + "；"
                    + string.Join("；", ExportSmokeTree(unexpected).OfType<TextBlock>().Select(text => text.Text)));
            await Task.Delay(25);
        }
        throw new TimeoutException("等待实际导出窗口与主按钮超时。");
    }

    private static async Task PressExportSmokeButtonAsync(ContentDialog dialog, bool primary)
    {
        string text = primary ? dialog.PrimaryButtonText : dialog.CloseButtonText;
        var button = ExportSmokeTree(dialog).OfType<Button>().Single(candidate => candidate.Content?.ToString() == text);
        if (!button.IsEnabled) throw new InvalidOperationException("导出窗口按钮不可用：" + text);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args) => closed.TrySetResult();
        dialog.Closed += OnClosed;
        try
        {
            new ButtonAutomationPeer(button).Invoke();
            try { await closed.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (TimeoutException)
            {
                string errors = string.Join("；", ExportSmokeTree(dialog).OfType<InfoBar>()
                    .Where(error => error.IsOpen).Select(error => error.Title + "：" + error.Message));
                throw new InvalidOperationException("实际“" + text + "”没有关闭导出窗口。" + errors);
            }
        }
        finally { dialog.Closed -= OnClosed; }
    }

    private static void ExportSmokeChoice(ContentDialog dialog, string header, string key)
    {
        var box = ExportSmokeControl<ComboBox>(dialog, header);
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().Single(item => item.Tag?.ToString() == key);
    }

    private static T ExportSmokeControl<T>(ContentDialog dialog, string header) where T : DependencyObject
        => ExportSmokeTree(dialog).OfType<T>().Single(control => control switch
        {
            ComboBox box => box.Header?.ToString() == header,
            NumberBox box => box.Header?.ToString() == header,
            TextBox box => box.Header?.ToString() == header,
            _ => false
        });

    private static IEnumerable<DependencyObject> ExportSmokeTree(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in ExportSmokeTree(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
