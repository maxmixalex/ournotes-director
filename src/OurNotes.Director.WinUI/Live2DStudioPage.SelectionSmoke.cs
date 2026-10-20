using System.Security.Cryptography;
using System.Text.Json.Nodes;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.WinUI;

internal sealed partial class Live2DStudioPage
{
    // Run after real motion/expression selections and a live parameter commit.
    // This specifically catches load/commit/export using different projections.
    async Task RunSelectionConsistencySmokeAsync(Action<bool, string> check)
    {
        await FlushEditsAsync();
        var current = document!;
        var before = viewport.LastFrame!;
        double time = current.Time;
        string? motion = current.Motion, expression = current.Expression;
        string saved = current.ToJson();
        string beforeHash = Convert.ToHexString(SHA256.HashData(before.Pixels.Rgba));
        var rows = current.ToWorkbenchSceneDocument().Root["commands"]!.AsArray().OfType<JsonObject>().ToArray();
        check(rows.Count(row => row["cmd"]?.ToString() == "Motion") == 1
            && rows.Count(row => row["cmd"]?.ToString() == "Expression") == 1,
            "独立 Live2D 实际预览项目只有一个动作和一个表情，不重新混入原场景轨道");

        await SeekAsync(time);
        var replayed = viewport.LastFrame!;
        string replayHash = Convert.ToHexString(SHA256.HashData(replayed.Pixels.Rgba));
        check(replayed.Frame == before.Frame && beforeHash == replayHash,
            "实时参数提交后重新求值同一帧，人物完整像素不发生位置或姿态跳变");
        await SeekAsync(0);
        var first = viewport.LastFrame!.ActorAnimations!.Single(actor => actor.TargetId == current.ActorId);
        check(first.Motion == motion && first.Expression == expression,
            "回到第一帧仍使用当前唯一动作和表情，不恢复旧的时间轴选择");
        await SeekAsync(time);
        var restored = viewport.LastFrame!;
        string restoredHash = Convert.ToHexString(SHA256.HashData(restored.Pixels.Rgba));
        check(restored.Frame == before.Frame && restoredHash == beforeHash && current.ToJson() == saved,
            "往返定位保留相同画布像素、参数历史和项目内容");
        DesktopSettings.AtomicWrite(App.SmokeEvidencePath("live2d-selection-consistency.json"),
            new JsonObject { ["frame"] = before.Frame, ["motion"] = motion, ["expression"] = expression,
                ["beforeHash"] = beforeHash, ["replayHash"] = replayHash, ["restoredHash"] = restoredHash,
                ["sameDocument"] = current.ToJson() == saved }.ToJsonString(DesktopSettings.JsonOptions));
    }
}
