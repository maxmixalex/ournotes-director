using System.Text.Json.Nodes;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.WinUI;

internal sealed partial class CreativeScenePage
{
    internal async Task RunTimelineInteractionSmokeAsync(Action<bool, string> check)
    {
        await SeekAsync(0);
        string saved = document.ToJson();
        await timeline.VerifyGroupAndHeightSmokeAsync(check);
        check(document.ToJson() == saved, "轨道缩放与分组不改写演出 JSON");
        long session = viewport.LastFrame!.SessionId;
        var points = timeline.ScrubCoordinates(.1, Math.Min(2.2, document.Duration));
        var samples = await viewport.SendTimelineScrubSmokeAsync(window, points.Element, points.StartX, points.EndX);
        await scrubber.FlushAsync();
        var completed = viewport.LastFrame!;
        var settled = new JsonObject
        {
            ["frame"] = completed.Frame, ["session"] = completed.SessionId,
            ["renderMs"] = completed.RenderMilliseconds,
            ["simulationMs"] = completed.Performance?.SimulationMilliseconds,
            ["sceneMs"] = completed.Performance?.SceneMilliseconds,
            ["readbackMs"] = completed.Performance?.ReadbackMilliseconds,
            ["gcPauseMs"] = completed.Performance?.Diagnostics?.GcPauseMilliseconds,
            ["shaderMs"] = completed.Performance?.Diagnostics?.ShaderCompilationMilliseconds,
            ["framesWhileHeld"] = samples.Count
        };
        DesktopSettings.AtomicWrite(App.SmokeEvidencePath("timeline-scrub-settled.json"), settled.ToJsonString(DesktopSettings.JsonOptions));
        check(samples.Select(sample => sample.Frame).Distinct().Count() >= 4
            && samples.All(sample => !sample.Playing && sample.Session == session),
            "暂停时持续按住真实时间轴标尺拖动，松手前节目监视器呈现至少四个不同的原生帧");
        var hashes = samples.Where(sample => sample.Pixels is not null).Select(sample => sample.Pixels).Distinct().Count();
        check(hashes >= 4, "时间轴拖动的原生像素确实变化，不只是播放头或时间码变化");
        check(viewport.LastFrame!.Frame == (int)Math.Round(points.EndTime * 30) && !viewport.IsPlaying
            && document.ToJson() == saved, "松手后定位到最后一个输入帧并保持暂停，不改写项目");
        var values = new JsonArray(samples.Select(sample => (JsonNode)new JsonObject { ["timeMs"] = sample.Milliseconds,
            ["frame"] = sample.Frame, ["session"] = sample.Session, ["playing"] = sample.Playing, ["pixels"] = sample.Pixels }).ToArray());
        DesktopSettings.AtomicWrite(App.SmokeEvidencePath("timeline-scrub.json"), new JsonObject
            { ["framesWhileHeld"] = values, ["lastFrame"] = viewport.LastFrame.Frame, ["expectedFrame"] = (int)Math.Round(points.EndTime * 30) }.ToJsonString(DesktopSettings.JsonOptions));
        await App.CaptureCheckpointAsync("timeline-paused-scrub");
    }
}
