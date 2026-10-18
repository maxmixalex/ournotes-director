using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.WinUI;

internal sealed partial class Live2DStudioPage
{
    internal Task RunSmokeTestAsync(Action<bool, string> check)
    {
        check(Content is StudioLayout { ActualWidth: > 0, ActualHeight: > 0 }
            && inspectorBody.IsLoaded && viewport.IsLoaded, "Live2D 独立双区工作台在真实窗口完成布局");
        check(document is null && viewport.LastFrame is null && !pngButton.IsEnabled
            && inspectorTabs.Count == 4, "Live2D 空白工作台显示四类控制，尚未选择模型时不生成虚构参数或输出");
        return Task.CompletedTask;
    }

    internal async Task VerifySceneHandoffAsync(Action<bool, string> check, string actor, double time, TimelineRange range)
    {
        await WaitForReadyAsync();
        check(document is { IsCapturedPerformance: true } && document.ActorId == actor
            && Math.Abs(document.Time - time) < 1e-8 && document.Range == range,
            "场景右键进入独立 Live2D 项目，保持所选人物、真实播放头与原工作区间");
        var current = document ?? throw new InvalidOperationException("场景人物没有进入 Live2D 工作台。");
        var saved = JsonNode.Parse(current.ToJson())!;
        check(handoffSource is not null && JsonNode.DeepEquals(saved["performance"], handoffSource)
            && JsonNode.DeepEquals(current.ToSceneDocument().Root["source"], handoffSource["source"]),
            "Live2D 保存原场景的独立完整副本，包含所有历史指令和 source 上下文");
        check(viewport.LastFrame is { } frame && frame.Frame == (int)Math.Round(time * 30)
            && frame.Targets.Count == 1 && frame.Targets[0].Id == actor
            && current.ToSceneDocument().Root["output"]?["isolateActor"]?.ToString() == actor,
            "独立 Live2D 原生预览在原帧只绘制被选人物，没有重建或改写原演出");
    }

    // Only invoked by the packaged Windows --smoke-test workflow. Every edit is
    // made through the actual visible WinUI controls and every export uses its dialog.
    internal async Task RunRealSmokeTestAsync(Action<bool, string> check)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OURNOTES_REAL_SCENE_FIXTURE"))) return;
        await WaitForReadyAsync();
        check(document is not null && viewport.HasCurrentSource, "Live2D 实窗回归沿用场景交接的真实人物与演出");
        var current = document!;
        string actor = current.ActorId;
        double poseTime = current.Time;
        var workRange = current.Range;
        SelectInspector("parameters"); UpdateLayout(); await Task.Delay(80);
        await ReadParametersAsync();
        var editable = current.Parameters.Where(parameter => parameter.Maximum > parameter.Minimum).ToArray();
        check(editable.Length > 0 && parameterControls.Count == current.Parameters.Count,
            "真实模型加载实际 Cubism 参数、取值范围、默认值、滑块和数值框");
        var parameter = editable.FirstOrDefault(value => value.Id.Contains("Angle", StringComparison.OrdinalIgnoreCase)) ?? editable[0];
        var controls = parameterControls[parameter.Id];
        double firstValue = parameter.Minimum + (parameter.Maximum - parameter.Minimum) * .73;
        double finalValue = parameter.Minimum + (parameter.Maximum - parameter.Minimum) * .31;
        long session = viewport.LastFrame!.SessionId;
        int frameNumber = viewport.LastFrame.Frame;
        double automaticValue = parameter.Value;
        controls.Slider.Value = firstValue;
        await FlushEditsAsync(); await ReadParametersAsync();
        var pinned = (await viewport.ReadActorParametersAsync(actor)).Single(value => value.Id == parameter.Id);
        check(pinned.IsOverridden && Math.Abs(pinned.Value - firstValue) < 1e-4
            && viewport.LastFrame!.SessionId == session && viewport.LastFrame.Frame == frameNumber,
            "拖动真实参数滑块直接更新原生模型，保持会话与同一帧，不重载或推进演出");
        controls.Number.Value = finalValue;
        await FlushEditsAsync(); await ReadParametersAsync();
        pinned = (await viewport.ReadActorParametersAsync(actor)).Single(value => value.Id == parameter.Id);
        check(Math.Abs(pinned.Value - finalValue) < 1e-4
            && Math.Abs(controls.Slider.Value - finalValue) < 1e-4,
            "真实数值框与参数滑块同步，并使用模型的实际范围");
        new ButtonAutomationPeer(controls.Reset).Invoke();
        await FlushEditsAsync(); await ReadParametersAsync();
        var released = (await viewport.ReadActorParametersAsync(actor)).Single(value => value.Id == parameter.Id);
        check(!released.IsOverridden && Math.Abs(released.Value - automaticValue) < 1e-4
            && viewport.LastFrame!.SessionId == session && viewport.LastFrame.Frame == frameNumber,
            "真实恢复自动按钮释放参数固定并恢复同帧自动姿态，保留播放时间");

        SelectInspector("controls"); UpdateLayout();
        var originalTransform = current.Transform;
        transformControls["x"].Number.Value = originalTransform.X * 100 + 3;
        transformControls["scale"].Slider.Value = originalTransform.Scale * 100 - 8;
        await FlushEditsAsync();
        check(viewport.LastFrame!.SessionId == session && viewport.LastFrame.Frame == frameNumber
            && Math.Abs(current.Transform.X - originalTransform.X - .03) < 1e-8
            && Math.Abs(current.Transform.Scale - originalTransform.Scale + .08) < 1e-8,
            "真实位置和缩放控件即时调整隔离画布，同一模型会话与帧保持不变");
        bool supportsPhysics = physicsBox.IsEnabled;
        breathBox.IsChecked = false; blinkBox.IsChecked = false; if (supportsPhysics) physicsBox.IsChecked = false;
        await FlushEditsAsync();
        check(!current.Breath && !current.Blink && (!supportsPhysics || !current.Physics)
            && current.CurrentEditRows().Any(row => row["Values"]?["controls"] is JsonObject)
            && viewport.LastFrame!.SessionId == session && viewport.LastFrame.Frame == frameNumber,
            "呼吸、眨眼和模型可用的物理开关写入原生控制器指令，并即时保留当前画面");
        await RunPreviewResponsivenessSmokeAsync(check);

        string? motion = await SelectSmokeAnimationAsync(false, check);
        string? expression = await SelectSmokeAnimationAsync(true, check);
        SelectInspector("parameters"); UpdateLayout();
        controls = parameterControls[parameter.Id]; controls.Number.Value = finalValue;
        await FlushEditsAsync();
        await App.CaptureCheckpointAsync("live2d-parameters");

        // A small real output canvas makes the preview and exported pixel buffers
        // directly comparable without scaling either result inside the test.
        SelectInspector("controls"); widthBox.Value = 256; heightBox.Value = 384;
        await FlushEditsAsync(); await SeekAsync(poseTime);
        await SetPreviewSizeOverrideAsync((256, 384, 1));
        string savedPath = App.SmokeEvidencePath("live2d-workflow.live2d.json");
        SaveTo(savedPath); string saved = current.ToJson();
        await OpenAsync(savedPath); await WaitForReadyAsync(); current = document!;
        await SetPreviewSizeOverrideAsync((256, 384, 1));
        check(!HasUnsavedChanges && current.ToJson() == saved && current.Range == workRange
            && current.ActorId == actor && Math.Abs(current.Time - poseTime) < 1e-8,
            "通过正常保存和打开入口重开 Live2D 项目，完整演出、区间、画布和播放头保持一致");
        var reopened = (await viewport.ReadActorParametersAsync(actor)).Single(value => value.Id == parameter.Id);
        check(reopened.IsOverridden && Math.Abs(reopened.Value - finalValue) < 1e-4
            && current.Motion == motion && current.Expression == expression
            && !current.Breath && !current.Blink && (!supportsPhysics || !current.Physics),
            "重开项目后，真实渲染器恢复所选动作、表情、控制器和参数固定值");
        await App.CaptureCheckpointAsync("live2d-saved-project");

        string id = Guid.NewGuid().ToString("N");
        string picture = App.SmokeEvidencePath("live2d-pose-" + id + ".png");
        string movie = App.SmokeEvidencePath("live2d-animation-" + id + ".mov");
        string sequence = App.SmokeEvidencePath("live2d-frames-" + id);
        var report = new JsonObject
        {
            ["project"] = savedPath, ["actor"] = actor, ["time"] = poseTime, ["parameter"] = parameter.Id,
            ["minimum"] = parameter.Minimum, ["maximum"] = parameter.Maximum, ["value"] = finalValue,
            ["motion"] = motion, ["expression"] = expression, ["exports"] = new JsonArray()
        };
        string reportPath = App.SmokeEvidencePath("live2d-workflow.json");
        var frameJob = await ExportSmokeAsync("frame", picture, check);
        RecordJob(report, frameJob); DesktopSettings.AtomicWrite(reportPath, report.ToJsonString(DesktopSettings.JsonOptions));
        var png = Png.Decode(await File.ReadAllBytesAsync(picture));
        var counts = CheckPixels(png, check, "Live2D 当前姿态 PNG");
        var preview = viewport.LastFrame!.Pixels;
        if (preview.Rgba.Length == preview.Width * preview.Height * 4)
            await File.WriteAllBytesAsync(App.SmokeEvidencePath("live2d-preview-pose.png"), Png.Encode(preview));
        check(preview.Width == png.Width && preview.Height == png.Height && preview.Rgba.SequenceEqual(png.Rgba),
            "同尺寸当前帧的 Live2D 原生预览与实际 PNG 输出逐像素一致");
        report["transparentPixels"] = counts.Transparent; report["visiblePixels"] = counts.Visible;
        var sequenceJob = await ExportSmokeAsync("sequence", sequence, check);
        RecordJob(report, sequenceJob); DesktopSettings.AtomicWrite(reportPath, report.ToJsonString(DesktopSettings.JsonOptions));
        string[] files = Directory.GetFiles(sequence, "*.png", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        check(files.Length == 2, "Live2D 正常帧序列导出在所设区间产生准确的两帧 PNG");
        var first = Png.Decode(await File.ReadAllBytesAsync(files[0])); CheckPixels(first, check, "Live2D 帧序列首帧");
        check(first.Rgba.SequenceEqual(png.Rgba), "独立 PNG 与帧序列首帧使用相同演出时间，逐像素一致");
        var movieJob = await ExportSmokeAsync("clip", movie, check);
        RecordJob(report, movieJob); DesktopSettings.AtomicWrite(reportPath, report.ToJsonString(DesktopSettings.JsonOptions));
        string decodedMovie = App.SmokeEvidencePath("live2d-mov-first-frame.png");
        await Task.Run(() => Media.Run(NativeDependencies.MediaTool("ffmpeg", Empty(window.Settings.FFmpegPath)),
            ["-v", "error", "-y", "-i", movie, "-frames:v", "1", "-pix_fmt", "rgba", decodedMovie]));
        var decoded = Png.Decode(await File.ReadAllBytesAsync(decodedMovie)); CheckPixels(decoded, check, "Live2D 透明 MOV 实际解码帧");
        var probe = await window.QueryAsync("media.probe", new Dictionary<string, string?> { ["input"] = movie });
        report["movieProbe"] = probe.Result?.DeepClone();
        check(probe.Success && probe.Result?.ToJsonString().Contains("cfhd", StringComparison.Ordinal) == true,
            "实际输出 MOV 使用 CineForm 编码，并可解码出透明区域与可见人物");
        check(current.ToJson() == saved && !HasUnsavedChanges && current.Range == workRange,
            "三种实际导出完成后，已保存的独立项目和原工作区间保持不变");
        DesktopSettings.AtomicWrite(reportPath, report.ToJsonString(DesktopSettings.JsonOptions));

        await OpenModelAsync(current.ModelId, DisplayNames.Model(current.ModelId), site: current.Site, language: current.Language);
        await WaitForReadyAsync();
        var standalone = document!; var standaloneFrame = viewport.LastFrame!;
        check(!standalone.IsCapturedPerformance && standalone.ToSceneDocument().Source.IsLibrary && standalone.Range is null,
            "正常模型入口创建独立素材库项目，不携带场景副本或旧工作区间");
        check(standaloneFrame.Targets.Count == 1 && standaloneFrame.Targets[0].Id == standalone.ActorId
            && standaloneFrame.Pixels.Rgba.Where((_, index) => index % 4 == 3).Any(alpha => alpha > 128),
            "独立 Create 初始化在真实原生画布中显示一个非空人物");
        await RunPlaybackResponsivenessSmokeAsync(check);
        SelectInspector("controls"); viewport.FitCanvas(); UpdateLayout();
        await App.CaptureCheckpointAsync("live2d-standalone");
        string standalonePicture = App.SmokeEvidencePath("live2d-standalone-" + id + ".png");
        var standaloneJob = await ExportSmokeAsync("frame", standalonePicture, check, "live2d-standalone-export");
        var standalonePixels = Png.Decode(await File.ReadAllBytesAsync(standalonePicture));
        var standaloneCounts = CheckPixels(standalonePixels, check, "独立模型正常导出的 PNG");
        report["standalone"] = new JsonObject { ["model"] = standalone.ModelId, ["canvasWidth"] = standalone.Width,
            ["canvasHeight"] = standalone.Height, ["picture"] = standalonePicture,
            ["transparentPixels"] = standaloneCounts.Transparent, ["visiblePixels"] = standaloneCounts.Visible };
        RecordJob(report, standaloneJob); DesktopSettings.AtomicWrite(reportPath, report.ToJsonString(DesktopSettings.JsonOptions));
        await RunCanvasSmokeTestAsync(check);
    }

    async Task<string?> SelectSmokeAnimationAsync(bool expressions, Action<bool, string> check)
    {
        SelectInspector(expressions ? "expression" : "motion"); UpdateLayout(); await Task.Delay(40);
        var list = expressions ? expressionList : motionList;
        var candidates = list.Items.OfType<ListViewItem>().Where(item => !string.IsNullOrEmpty(item.Tag?.ToString())).ToArray();
        check(candidates.Length > 0 && list.IsLoaded && list.ActualHeight > 0,
            expressions ? "真实表情标签列出模型自带表情" : "真实动作标签列出模型自带动作");
        string? preferred = document!.Metadata[expressions ? "defaultExpression" : "defaultMotion"]?.ToString();
        var chosen = candidates.FirstOrDefault(item => item.Tag?.ToString() == preferred) ?? candidates[0];
        list.SelectedItem = null; list.SelectedItem = chosen;
        await FlushEditsAsync();
        string? id = chosen.Tag?.ToString();
        var native = viewport.LastFrame!.ActorAnimations!.Single(animation => animation.TargetId == document!.ActorId);
        check((expressions ? native.Expression : native.Motion) == id,
            expressions ? "选择实际表情列表后，原生人物使用该表情" : "选择实际动作列表后，原生人物使用该动作");
        return id;
    }
    async Task<DesktopTask> ExportSmokeAsync(string kind, string destination, Action<bool, string> check, string? evidenceName = null)
    {
        var before = window.Jobs.Tasks.Select(task => task.Id).ToHashSet(StringComparer.Ordinal);
        Task exporting = ExportAsync(kind); ContentDialog? dialog = null;
        try
        {
            dialog = await WaitForSmokeDialogAsync(exporting);
            if (document!.Range is { } range && kind != "frame")
                check(Ui.Selected(SmokeControl<ComboBox>(dialog, "时间范围")) == "work"
                    && Math.Abs(SmokeControl<NumberBox>(dialog, "开始（秒）").Value - range.Start) < 1e-8
                    && Math.Abs(SmokeControl<NumberBox>(dialog, "时长（秒）").Value - range.Duration) < 1e-8,
                    "Live2D " + kind + " 实际导出窗口默认保持转入的工作区间");
            SmokeChoice(dialog, "画面尺寸", "custom");
            SmokeControl<NumberBox>(dialog, "宽度").Value = 256; SmokeControl<NumberBox>(dialog, "高度").Value = 384;
            if (kind != "frame")
            {
                SmokeChoice(dialog, "时间范围", "range");
                SmokeControl<NumberBox>(dialog, "开始（秒）").Value = document.Time;
                SmokeControl<NumberBox>(dialog, "时长（秒）").Value = 2d / 30;
            }
            if (kind == "clip") SmokeChoice(dialog, "视频格式", "cineform");
            SmokeControl<TextBox>(dialog, "保存到").Text = destination;
            dialog.UpdateLayout(); await App.CapturePopupCheckpointAsync(dialog, evidenceName ?? "live2d-export-" + kind);
            await PressSmokeButtonAsync(dialog); await exporting.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally { dialog?.Hide(); }
        var added = window.Jobs.Tasks.Where(task => !before.Contains(task.Id)).ToArray();
        check(added.Length == 1 && added[0].Request.CommandId == "render." + kind,
            "Live2D " + kind + " 实际主按钮通过正常队列提交一次导出任务");
        var job = added[0]; EngineResult result;
        try { result = await job.Completion.WaitAsync(TimeSpan.FromSeconds(90)); }
        catch (TimeoutException) { window.Jobs.Cancel(job); throw; }
        check(result.Success && job.Status == DesktopTaskStatus.Succeeded,
            "Live2D " + kind + " 随包原生渲染器完成实际输出" + (result.Success ? "" : "：" + result.Error + "；" + result.Log));
        var queued = SceneDocument.Open(job.Request.Arguments[2]);
        check(queued.Root["output"]?["isolateActor"]?.ToString() == document!.ActorId
            && JsonNode.DeepEquals(queued.Root["output"]?["isolateTransform"], document.ToSceneDocument().Root["output"]?["isolateTransform"]),
            "Live2D " + kind + " 队列快照保留同一角色与实际变换");
        File.Copy(job.Request.Arguments[2], App.SmokeEvidencePath((evidenceName ?? "live2d-export-" + kind) + ".project.json"), true);
        return job;
    }
    static void RecordJob(JsonObject report, DesktopTask job) => report["exports"]!.AsArray().Add(new JsonObject
    {
        ["command"] = job.Request.CommandId, ["arguments"] = new JsonArray(job.Request.Arguments.Select(arg => (JsonNode?)JsonValue.Create(arg)).ToArray()),
        ["success"] = job.Result?.Success, ["exitCode"] = job.Result?.ExitCode, ["result"] = job.Result?.Json?.DeepClone()
    });
    static (int Transparent, int Visible) CheckPixels(Pixels pixels, Action<bool, string> check, string label)
    {
        int transparent = 0, visible = 0;
        for (int index = 3; index < pixels.Rgba.Length; index += 4)
        { if (pixels.Rgba[index] == 0) transparent++; if (pixels.Rgba[index] > 128) visible++; }
        check(pixels.Width == 256 && pixels.Height == 384 && transparent > 0 && visible > 0,
            label + " 为 256×384，包含真实透明区域与可见人物");
        return (transparent, visible);
    }
    async Task<ContentDialog> WaitForSmokeDialogAsync(Task operation)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (operation.IsCompleted) { await operation; throw new InvalidOperationException("Live2D 导出没有打开实际窗口。"); }
            var dialog = VisualTreeHelper.GetOpenPopupsForXamlRoot(window.RootXaml).Select(popup => popup.Child)
                .OfType<DependencyObject>().SelectMany(SmokeTree).OfType<ContentDialog>()
                .FirstOrDefault(candidate => candidate.PrimaryButtonText == "开始导出");
            if (dialog is not null)
            {
                dialog.UpdateLayout();
                if (dialog.IsLoaded && dialog.ActualWidth > 0 && SmokeTree(dialog).OfType<Button>().Any(button => button.Content?.ToString() == "开始导出")) return dialog;
            }
            await Task.Delay(25);
        }
        throw new TimeoutException("Live2D 实际导出窗口未完成布局。");
    }
    static async Task PressSmokeButtonAsync(ContentDialog dialog)
    {
        var button = SmokeTree(dialog).OfType<Button>().Single(candidate => candidate.Content?.ToString() == dialog.PrimaryButtonText);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Closed(ContentDialog sender, ContentDialogClosedEventArgs args) => closed.TrySetResult();
        dialog.Closed += Closed;
        try { new ButtonAutomationPeer(button).Invoke(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
        finally { dialog.Closed -= Closed; }
    }
    static void SmokeChoice(ContentDialog dialog, string header, string value)
    {
        var box = SmokeControl<ComboBox>(dialog, header);
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().Single(item => item.Tag?.ToString() == value);
    }
    static T SmokeControl<T>(ContentDialog dialog, string header) where T : FrameworkElement =>
        SmokeTree(dialog).OfType<T>().Single(item => (item switch { ComboBox box => box.Header, NumberBox box => box.Header, TextBox box => box.Header, _ => null })?.ToString() == header);
    static IEnumerable<DependencyObject> SmokeTree(DependencyObject node)
    {
        yield return node;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            foreach (var child in SmokeTree(VisualTreeHelper.GetChild(node, i))) yield return child;
    }
}
