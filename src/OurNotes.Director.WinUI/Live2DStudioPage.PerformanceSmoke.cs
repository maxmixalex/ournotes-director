using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;
using Windows.System;

namespace OurNotes.Director.WinUI;

internal sealed partial class Live2DStudioPage
{
    async Task RunPreviewResponsivenessSmokeAsync(Action<bool, string> check)
    {
        await FlushEditsAsync(); await ApplyPreviewSizeAsync();
        var current = document!; double time = current.Time;
        string original = current.ToJson(); var transform = current.Transform;
        long session = viewport.LastFrame!.SessionId, commits = viewport.SceneProjectCommitCount;
        var before = viewport.LastFrame!;
        var trace = await viewport.SendContinuousDragSmokeAsync(window, current.ActorId, .12, .035, () =>
        {
            check(current.ToJson() == original && current.Transform == transform
                && viewport.SceneProjectCommitCount == commits,
                "实际鼠标连续按住移动期间不克隆文档历史、不写预览项目，原生渲染直接跟随输入");
        });
        await FlushEditsAsync();
        var moving = trace.Frames.Where(sample => sample.Frame.Targets.Any(target => target.Id == current.ActorId))
            .Select(sample => (sample.Milliseconds, sample.Frame,
                Target: sample.Frame.Targets.Single(target => target.Id == current.ActorId)))
            .Where(sample => sample.Target.X - trace.Origin.X > .005)
            .DistinctBy(sample => Math.Round(sample.Target.X, 5)).ToArray();
        var latency = new List<double>();
        var samples = new JsonArray();
        foreach (var sample in moving)
        {
            double x = sample.Target.X - trace.Origin.X, y = sample.Target.Y - trace.Origin.Y;
            var input = trace.Inputs.Where(input => input.DeliveredMilliseconds <= sample.Milliseconds)
                .OrderBy(input => Math.Abs(input.X - x) + Math.Abs(input.Y - y)).FirstOrDefault();
            double error = input is null ? double.PositiveInfinity : Math.Abs(input.X - x) + Math.Abs(input.Y - y);
            double delay = input is null ? double.PositiveInfinity : sample.Milliseconds - input.InjectedMilliseconds;
            if (error < .002 && double.IsFinite(delay)) latency.Add(delay);
            samples.Add(new JsonObject { ["presentedMs"] = sample.Milliseconds, ["dx"] = x, ["dy"] = y,
                ["matchingInputError"] = double.IsFinite(error) ? error : null,
                ["inputToPresentationMs"] = double.IsFinite(delay) ? delay : null,
                ["renderMs"] = sample.Frame.RenderMilliseconds, ["sequence"] = sample.Frame.Sequence });
        }
        double p95 = Percentile(latency, .95);
        var report = new JsonObject
        {
            ["previewWidth"] = before.Pixels.Width, ["previewHeight"] = before.Pixels.Height,
            ["supersample"] = viewport.PreviewSupersample, ["heldMs"] = trace.HeldMilliseconds,
            ["deliveredInputs"] = trace.Inputs.Count, ["nativePositionsWhileHeld"] = moving.Length,
            ["inputToPresentationP95Ms"] = double.IsFinite(p95) ? p95 : null, ["dragFrames"] = samples,
            ["device"] = before.Device, ["presentationFallback"] = before.PresentationFallback
        };
        string reportPath = App.SmokeEvidencePath("live2d-preview-performance.json");
        DesktopSettings.AtomicWrite(reportPath, report.ToJsonString(DesktopSettings.JsonOptions));
        check(moving.Length >= 4 && latency.Count >= 4 && p95 < 250,
            "真实连续拖动在松手前呈现至少四个中间原生位置，软件GPU输入到显示P95低于250毫秒");
        check(moving.All(sample => sample.Frame.SessionId == session && sample.Frame.Frame == before.Frame)
            && viewport.LastFrame!.SessionId == session && viewport.LastFrame.Frame == before.Frame,
            "拖动中的真实命中边界持续更新，所有中间画面保留同一模型会话和演出帧");
        if (before.Pixels.Rgba.Length > 0)
        {
            var distinct = moving.Where(sample => sample.Frame.Pixels.Rgba.Length > 0)
                .Select(sample => Convert.ToHexString(SHA256.HashData(sample.Frame.Pixels.Rgba))).Distinct().Take(4).Count();
            check(distinct >= 4, "按住鼠标时至少四张原生像素画面发生改变，没有用XAML平移冒充模型重绘");
        }
        check(viewport.SceneProjectCommitCount == commits + 1 && current.Transform != transform,
            "松手只提交一次Live2D项目，保留最后一个实际鼠标位置");
        await App.CaptureCheckpointAsync("live2d-continuous-drag");
        string moved = current.ToJson();
        await viewport.SendViewportKeySmokeAsync(window, VirtualKey.Z, control: true); await FlushEditsAsync();
        check(current.ToJson() == original && current.Transform == transform,
            "真实Ctrl+Z一次撤销整个连续拖动，所有项目内容恢复到按下鼠标前");
        await viewport.SendViewportKeySmokeAsync(window, VirtualKey.Y, control: true); await FlushEditsAsync();
        check(current.ToJson() == moved, "真实Ctrl+Y一次恢复完整拖动，未拆成每个PointerMoved一条历史");
        await viewport.SendViewportKeySmokeAsync(window, VirtualKey.Z, control: true); await FlushEditsAsync();
        await SeekAsync(time); viewport.SelectedTargetId = current.ActorId;
    }

    async Task RunPlaybackResponsivenessSmokeAsync(Action<bool, string> check)
    {
        var current = document!; double time = current.Time;
        check(!current.IsCapturedPerformance && current.Range is null && current.Duration >= 1,
            "连续播放性能回归使用独立模型完整动画，不以两帧循环的反复重放替代播放");
        await SeekAsync(0); await ApplyPreviewSizeAsync();
        SelectInspector("parameters"); UpdateLayout(); await ReadParametersAsync();
        viewport.Focus(FocusState.Programmatic);
        string reportPath = App.SmokeEvidencePath("live2d-preview-performance.json");
        var report = JsonNode.Parse(await File.ReadAllTextAsync(reportPath))!.AsObject();
        var played = new List<PreviewFrame>(); var pulses = new List<double>(); var clock = Stopwatch.StartNew();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pulse = DispatcherQueue.CreateTimer(); pulse.IsRepeating = true; pulse.Interval = TimeSpan.FromMilliseconds(20);
        double previousPulse = 0;
        pulse.Tick += (_, _) => { double now = clock.Elapsed.TotalMilliseconds; pulses.Add(now - previousPulse); previousPulse = now; };
        void OnPlayed(object? sender, PreviewFrame frame)
        { if (frame.IsPlaying) { played.Add(frame); started.TrySetResult(); } }
        viewport.FramePresented += OnPlayed;
        double pauseMilliseconds;
        try
        {
            pulse.Start(); await viewport.SendViewportKeySmokeAsync(window, VirtualKey.Space);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(900);
            long pauseStamp = Stopwatch.GetTimestamp();
            await viewport.SendViewportKeySmokeAsync(window, VirtualKey.Space); await viewport.PauseForEditingAsync();
            pauseMilliseconds = Stopwatch.GetElapsedTime(pauseStamp).TotalMilliseconds;
        }
        finally { pulse.Stop(); viewport.FramePresented -= OnPlayed; Pause(); }
        report["playbackFrames"] = played.Count;
        report["playbackWithParameterInspector"] = true;
        report["playbackParameterCount"] = current.Parameters.Count;
        report["playbackPreviewWidth"] = viewport.LastFrame!.Pixels.Width;
        report["playbackPreviewHeight"] = viewport.LastFrame.Pixels.Height;
        report["playbackRenderP95Ms"] = played.Count > 0 ? Percentile(played.Select(frame => frame.RenderMilliseconds).ToArray(), .95) : null;
        var performance = played.Where(frame => frame.Performance is not null).Select(frame => frame.Performance!).ToArray();
        if (performance.Length > 0)
        {
            report["simulationP95Ms"] = Percentile(performance.Select(value => value.SimulationMilliseconds).ToArray(), .95);
            report["sceneP95Ms"] = Percentile(performance.Select(value => value.SceneMilliseconds).ToArray(), .95);
            report["readbackP95Ms"] = Percentile(performance.Select(value => value.ReadbackMilliseconds).ToArray(), .95);
            report["targetP95Ms"] = Percentile(performance.Select(value => value.TargetMilliseconds).ToArray(), .95);
            report["allocatedBytesP95"] = Percentile(performance.Select(value => (double)value.AllocatedBytes).ToArray(), .95);
            report["simulationFrames"] = performance.Sum(value => value.SimulationFrames);
        }
        report["dispatcherGapP95Ms"] = pulses.Count > 0 ? Percentile(pulses, .95) : null;
        report["pauseResponseMs"] = pauseMilliseconds;
        DesktopSettings.AtomicWrite(reportPath, report.ToJsonString(DesktopSettings.JsonOptions));
        check(played.Select(frame => (frame.SessionId, frame.Frame)).Distinct().Count() >= 4
            && Percentile(pulses, .95) < 250 && pauseMilliseconds < 500 && !viewport.IsPlaying,
            "真实空格播放连续原生帧，播放期间UI心跳P95低于250毫秒且再次空格在500毫秒内暂停");
        await SeekAsync(time); viewport.SelectedTargetId = current.ActorId; SelectInspector("controls");
    }

    static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0) return double.PositiveInfinity;
        var sorted = values.Order().ToArray();
        return sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1)];
    }
}
