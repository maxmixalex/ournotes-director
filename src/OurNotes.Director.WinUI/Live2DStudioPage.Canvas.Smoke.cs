using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.WinUI;

internal sealed partial class Live2DStudioPage
{
    // Uses the visible sidebar, the ordinary export dialog and the packaged native
    // job queue. The small canvas bounds the cost, not the motion-analysis interval.
    async Task RunCanvasSmokeTestAsync(Action<bool, string> check)
    {
        var current = document ?? throw new InvalidOperationException("完整动作画布回归需要真实模型。");
        SelectInspector("controls");
        widthBox.Value = 320; heightBox.Value = 480; durationBox.Value = .4;
        transformControls["x"].Number.Value = 65;
        transformControls["scale"].Number.Value = 115;
        transformControls["rotation"].Number.Value = 8;
        await FlushEditsAsync(); await SeekAsync(0);
        await SelectSmokeAnimationAsync(false, check);
        SelectInspector("parameters");
        var parameter = current.Parameters.First(value => value.Maximum > value.Minimum
            && value.Id.Contains("Angle", StringComparison.OrdinalIgnoreCase));
        (await BringParameterIntoViewSmokeAsync(parameter.Id)).Number.Value = parameter.Minimum;
        await FlushEditsAsync(); await SeekAsync(.2);
        (await BringParameterIntoViewSmokeAsync(parameter.Id)).Number.Value = parameter.Maximum;
        await FlushEditsAsync(); await SeekAsync(0);
        SelectInspector("controls"); UpdateLayout();
        var transform = current.Transform;
        var commands = current.ToSceneDocument().Root["commands"]!.DeepClone();
        actorCanvasMultiplier.Value = 1.15;
        fitActorCanvasButton!.StartBringIntoView();
        Task previous = actorCanvasTask;
        new ButtonAutomationPeer(fitActorCanvasButton!).Invoke();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (ReferenceEquals(previous, actorCanvasTask) && DateTime.UtcNow < deadline) await Task.Delay(20);
        check(!ReferenceEquals(previous, actorCanvasTask), "实际侧栏的仅角色按钮启动完整动作范围计算");
        await actorCanvasTask.WaitAsync(TimeSpan.FromSeconds(45));
        var canvas = current.Canvas;
        check(canvas is not null && current.CanvasMultiplier == 1.15
            && canvas.ReferenceWidth == 320 && canvas.ReferenceHeight == 480
            && widthBox.Value == current.Width && heightBox.Value == current.Height,
            "侧栏按 1.15 倍设置完整动作画布，同时更新宽高并保存原始参考坐标");
        check(current.Transform == transform && JsonNode.DeepEquals(commands, current.ToSceneDocument().Root["commands"]),
            "调整画布保留原始人物变换和包含前后不同姿态的完整动作历史");
        var full = TimelineRange.Full(current.Duration, 30);
        bool contained = true;
        for (int frame = 0; frame < full.EndFrameExclusive; frame++)
        {
            await SeekAsync(frame / 30d);
            var target = viewport.LastFrame!.Targets.Single(value => value.Id == current.ActorId);
            contained &= target.X >= 0 && target.Y >= 0 && target.X + target.Width <= 1 && target.Y + target.Height <= 1;
        }
        check(contained && full.FrameCount == 12, "完整动作的全部 12 帧，包括稍后参数极值，均处于同一个固定画布内");
        await SeekAsync(0);
        Undo(false); await FlushEditsAsync();
        check(current.Canvas is null && current.Width == 320 && current.Height == 480 && current.Transform == transform,
            "一次撤销恢复原画布，人物姿态和动作历史不受影响");
        Undo(true); await FlushEditsAsync();
        check(current.Canvas == canvas && current.CanvasMultiplier == 1.15,
            "一次重做恢复相同完整动作范围及画布倍数");
        string savedPath = App.SmokeEvidencePath("live2d-actor-canvas.live2d.json");
        SaveTo(savedPath); await OpenAsync(savedPath); await WaitForReadyAsync(); current = document!;
        check(current.Canvas == canvas && current.CanvasMultiplier == 1.15 && !HasUnsavedChanges
            && actorCanvasMultiplier.Value == 1.15,
            "重新打开 Live2D 文件后，侧栏与原生预览恢复保存的动作画布和倍率");
        fitActorCanvasButton!.StartBringIntoView(); UpdateLayout(); await Task.Delay(40);
        await App.CaptureCheckpointAsync("live2d-actor-canvas-sidebar");
        string saved = current.ToJson();
        var report = new JsonObject
        {
            ["framesAnalyzed"] = full.FrameCount, ["referenceWidth"] = 320, ["referenceHeight"] = 480,
            ["sidebarMultiplier"] = 1.15, ["exportMultiplier"] = 1.25,
            ["sidebarOutput"] = current.ToSceneDocument().Root["output"]!.DeepClone(), ["exports"] = new JsonArray()
        };
        string id = Guid.NewGuid().ToString("N");
        string picture = App.SmokeEvidencePath("live2d-actor-canvas-" + id + ".png");
        string sequence = App.SmokeEvidencePath("live2d-actor-canvas-frames-" + id);
        string movie = App.SmokeEvidencePath("live2d-actor-canvas-" + id + ".mov");
        var pngJob = await ExportActorCanvasSmokeAsync("frame", picture, check);
        RecordJob(report, pngJob);
        var png = Png.Decode(await File.ReadAllBytesAsync(picture));
        CheckCanvasPixels(png, check, "仅角色 PNG");
        var sequenceJob = await ExportActorCanvasSmokeAsync("sequence", sequence, check);
        RecordJob(report, sequenceJob);
        var frames = Directory.GetFiles(sequence, "*.png", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        check(frames.Length == full.FrameCount, "仅角色帧序列通过正常导出流程输出完整的 12 帧动作");
        bool fixedCanvas = true, clearEdges = true;
        Pixels? first = null, last = null;
        foreach (string file in frames)
        {
            var pixels = Png.Decode(await File.ReadAllBytesAsync(file));
            first ??= pixels; last = pixels;
            fixedCanvas &= pixels.Width == png.Width && pixels.Height == png.Height;
            clearEdges &= CanvasEdgesAreTransparent(pixels);
        }
        check(fixedCanvas && clearEdges, "实际 PNG 序列全程保持固定尺寸且四边透明，完整动作没有触边裁切");
        check(first is not null && first.Rgba.SequenceEqual(png.Rgba) && last is not null && !last.Rgba.SequenceEqual(first.Rgba),
            "同一姿态 PNG 与帧序列首帧逐像素一致，后续真实姿态发生变化");
        var movieJob = await ExportActorCanvasSmokeAsync("clip", movie, check);
        RecordJob(report, movieJob);
        string decodedPath = App.SmokeEvidencePath("live2d-actor-canvas-mov.png");
        await Task.Run(() => Media.Run(NativeDependencies.MediaTool("ffmpeg", Empty(window.Settings.FFmpegPath)),
            ["-v", "error", "-y", "-i", movie, "-frames:v", "1", "-pix_fmt", "rgba", decodedPath]));
        var decoded = Png.Decode(await File.ReadAllBytesAsync(decodedPath));
        CheckCanvasPixels(decoded, check, "仅角色透明 MOV 的实际解码帧");
        check(decoded.Width >= png.Width && decoded.Height >= png.Height
            && decoded.Width % 16 == 0 && decoded.Height % 8 == 0,
            "CineForm 将同一动作范围向外对齐到编码尺寸，保留角色像素比例与透明留白");
        check(current.ToJson() == saved && !HasUnsavedChanges && current.CanvasMultiplier == 1.15,
            "导出窗口使用 1.25 倍画布，已保存的侧栏 1.15 倍项目仍保持原值");
        report["pngWidth"] = png.Width; report["pngHeight"] = png.Height;
        report["movieWidth"] = decoded.Width; report["movieHeight"] = decoded.Height;
        DesktopSettings.AtomicWrite(App.SmokeEvidencePath("live2d-actor-canvas-report.json"), report.ToJsonString(DesktopSettings.JsonOptions));
    }

    async Task<DesktopTask> ExportActorCanvasSmokeAsync(string kind, string destination, Action<bool, string> check)
    {
        var before = window.Jobs.Tasks.Select(task => task.Id).ToHashSet(StringComparer.Ordinal);
        Task exporting = ExportAsync(kind); ContentDialog? dialog = null;
        try
        {
            dialog = await WaitForSmokeDialogAsync(exporting);
            check(Ui.Selected(SmokeControl<ComboBox>(dialog, "画面尺寸")) == "actor",
                "保存过角色画布的项目默认使用完整动作范围，透明视频自动向外对齐编码尺寸");
            var multiplier = SmokeControl<NumberBox>(dialog, "画布倍数（X）");
            multiplier.Value = 1.25;
            if (kind != "frame") SmokeChoice(dialog, "时间范围", "complete");
            if (kind == "clip") SmokeChoice(dialog, "视频格式", "cineform");
            SmokeControl<TextBox>(dialog, "保存到").Text = destination;
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (!dialog.IsPrimaryButtonEnabled && DateTime.UtcNow < deadline) await Task.Delay(25);
            check(dialog.IsPrimaryButtonEnabled && Ui.Selected(SmokeControl<ComboBox>(dialog, "画面尺寸")) == "actor"
                && multiplier.Value == 1.25, "实际 " + kind + " 导出窗口可选择仅角色完整动作画布及 1.25 倍留白");
            dialog.UpdateLayout();
            await App.CapturePopupCheckpointAsync(dialog, "live2d-actor-canvas-export-" + kind);
            await PressSmokeButtonAsync(dialog); await exporting.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally { dialog?.Hide(); }
        var added = window.Jobs.Tasks.Where(task => !before.Contains(task.Id)).ToArray();
        check(added.Length == 1 && added[0].Request.CommandId == "render." + kind,
            "仅角色 " + kind + " 由实际主按钮向正常任务队列提交一次");
        var job = added.Single(); EngineResult result;
        try { result = await job.Completion.WaitAsync(TimeSpan.FromSeconds(90)); }
        catch (TimeoutException) { window.Jobs.Cancel(job); throw; }
        check(result.Success && job.Status == DesktopTaskStatus.Succeeded,
            "仅角色 " + kind + " 由随包原生渲染器完成实际输出" + (result.Success ? "" : "：" + result.Error));
        var queued = SceneDocument.Open(job.Request.Arguments[2]);
        var crop = ActorIsolation.ReadCanvas(queued.Root["output"]?["isolateCanvas"], document!.ActorId);
        check(crop is not null && crop.ReferenceWidth == 320 && crop.ReferenceHeight == 480
            && queued.Root["output"]?["isolateActor"]?.ToString() == document.ActorId
            && JsonNode.DeepEquals(queued.Root["commands"], document.ToSceneDocument().Root["commands"]),
            "仅角色 " + kind + " 快照使用原始参考画布与完整动作，不叠加上一次裁剪");
        File.Copy(job.Request.Arguments[2], App.SmokeEvidencePath("live2d-actor-canvas-export-" + kind + ".project.json"), true);
        return job;
    }

    static void CheckCanvasPixels(Pixels pixels, Action<bool, string> check, string label)
    {
        bool visible = false;
        for (int index = 3; index < pixels.Rgba.Length; index += 4) visible |= pixels.Rgba[index] > 128;
        check(visible && CanvasEdgesAreTransparent(pixels), label + " 包含可见真实人物，画布四边保留透明留白");
    }
    static bool CanvasEdgesAreTransparent(Pixels pixels)
    {
        for (int x = 0; x < pixels.Width; x++)
            if (pixels.Rgba[x * 4 + 3] != 0 || pixels.Rgba[((pixels.Height - 1) * pixels.Width + x) * 4 + 3] != 0) return false;
        for (int y = 0; y < pixels.Height; y++)
            if (pixels.Rgba[y * pixels.Width * 4 + 3] != 0 || pixels.Rgba[(y * pixels.Width + pixels.Width - 1) * 4 + 3] != 0) return false;
        return true;
    }
}
