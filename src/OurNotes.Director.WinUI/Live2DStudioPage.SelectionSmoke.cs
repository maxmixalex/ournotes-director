using System.Security.Cryptography;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;
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
        await RunInitialSelectionPoseSmokeAsync(check);
    }

    async Task RunInitialSelectionPoseSmokeAsync(Action<bool, string> check)
    {
        // Use the actual chosen model/action on a private native session. Turning
        // automatic controllers off isolates the initial animation sample from
        // legitimate breathing/blinking/physics movement. No workbench state is
        // edited, and this deliberately starts at frame zero before any seek.
        var current = document!;
        string saved = current.ToJson();
        var initial = Live2DDocument.Parse(saved);
        initial.Time = 0;
        initial.SetControllers(breath: false, blink: false, physics: false, loop: false);
        initial.SetDuration(1d / 30);
        initial.SetOutput(160, 240);
        using var input = initial.CreateWorkbenchSnapshot(Path.Combine(DesktopSettings.DataDirectory, "live2d-initial-pose"));
        var source = new ScenePreviewSource(window.Settings.ArchivePath, input.FilePath,
            NativeDependencies.CoreLibrary(Empty(window.Settings.NativeLibraryPath), Empty(window.Settings.CubismSdkPath)),
            NativeDependencies.MediaTool("ffmpeg", Empty(window.Settings.FFmpegPath)),
            160, 240, 2d / 30, Supersample: 1, SegmentedCpuFrames: true);
        await using var session = new RealtimePreviewSession();
        await session.LoadSceneAsync(source);
        var paused = session.LastFrame!;
        var parameters = await session.ReadActorParametersAsync(initial.ActorId, lifetime.Token);
        var firstTick = new TaskCompletionSource<PreviewFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFrame(object? sender, PreviewFrame frame) { if (frame.Frame == 1) firstTick.TrySetResult(frame); }
        session.FrameReady += OnFrame;
        try
        {
            session.Play(2d / 30);
            await firstTick.Task.WaitAsync(TimeSpan.FromSeconds(10), lifetime.Token);
        }
        finally
        {
            session.Pause(); await session.SynchronizeAsync(); session.FrameReady -= OnFrame;
        }
        var played = session.LastFrame!;
        var nextParameters = await session.ReadActorParametersAsync(initial.ActorId, lifetime.Token);
        string pausedHash = Convert.ToHexString(SHA256.HashData(paused.Pixels.Rgba));
        string playedHash = Convert.ToHexString(SHA256.HashData(played.Pixels.Rgba));
        var initialChoice = paused.ActorAnimations!.Single();
        check(paused.Frame == 0 && initialChoice.Motion == initial.Motion
            && initialChoice.Expression == initial.Expression
            && played.Frame == 1 && !session.IsPlaying && pausedHash == playedHash
            && parameters.Count == nextParameters.Count
            && parameters.All(value => nextParameters.Any(next => next.Id == value.Id && next.Value == value.Value)),
            "关闭自动控制器后，所选动作与表情在暂停第零帧已完成真实采样，首次播放不会从默认姿态跳位");
        check(current.ToJson() == saved && viewport.LastFrame!.Frame == (int)Math.Round(current.Time * 30),
            "首帧原生回归使用独立会话，工作台播放头、参数历史与当前画面不受影响");
        DesktopSettings.AtomicWrite(App.SmokeEvidencePath("live2d-initial-selection-pose.json"),
            new JsonObject { ["frame"] = paused.Frame, ["playedFrame"] = played.Frame,
                ["motion"] = initial.Motion, ["expression"] = initial.Expression,
                ["pausedHash"] = pausedHash, ["playedHash"] = playedHash,
                ["parameters"] = parameters.Count, ["sameDocument"] = current.ToJson() == saved }
            .ToJsonString(DesktopSettings.JsonOptions));
    }
}
