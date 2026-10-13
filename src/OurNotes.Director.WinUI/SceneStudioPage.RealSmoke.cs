using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.WinUI;

internal sealed partial class SceneStudioPage
{
    // Opt-in release validation with public assets fetched separately from the program.
    // This is invoked only by the existing --smoke-test entry point.
    internal async Task RunRealSmokeTestAsync(Action<bool, string> check)
    {
        if (Environment.GetEnvironmentVariable("OURNOTES_REAL_SCENE_FIXTURE") is not { Length: > 0 } fixture) return;
        _window.Settings.ArchivePath = Path.GetFullPath(fixture);
        SetDocument(StudioSceneDocument.Open(Path.Combine(fixture, "original.project.json")));
        _commandSearch.Text = ""; _commandSection.SelectedIndex = 0; _commandsTab!.IsChecked = true;
        await ReloadAsync();
        var first = await _viewport.WaitForFrameAsync();
        check(first.Frame == 0 && _viewport.HasCurrentSource && first.Pixels.Rgba.Length > 0,
            "真实原剧情10462在原生窗口显示首帧");
        check(_document!.CommandSections.All(section => !section.IsInherited) && _commands.Items.Count > 20
            && _document.Commands.Any(command => command.Command == "Talk"),
            "真实源剧情三段完整展开为可选中、可编辑的原生指令");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var timing = await _viewport.ResolveSceneTimingAsync(timeout.Token);
        await SeekAsync(0);
        check(timing is { IsComplete: true, EndFrame: > 300, Error: null }
            && _document.DurationMode == StudioDurationMode.Automatic && _document.ResolvedDuration == timing.Duration,
            "真实剧情自动时长同步到工作台，包含对白与末帧");
        var spoken = timing.Commands.First(row => row.Section == "commands" && row.Command == "Talk"
            && _document.CommandSections.First(section => section.Section == "commands").Commands[row.Index].Data["VoiceIDs"] is System.Text.Json.Nodes.JsonArray { Count: > 0 });
        var command = _document.CommandSections.First(section => section.Section == "commands").Commands[spoken.Index];
        SelectCommand(command.Id);
        string original = _document.GetCommandText(command.Id);
        check(original.Length > 0 && _dialogueText?.Text == original && SelectedCommand()?.Id == command.Id,
            "选择真实角色对白不被画布选择回调清除，右侧显示真实台词");
        _suppressReload = true;
        try
        {
            _dialogueText!.Text = original + " " + original; ApplyCommandDraft();
            check(_document.GetCommandText(command.Id) == original + " " + original,
                "原剧情对白通过原生控件直接修改，原资源编号保持有效");
            _document.Undo();
            check(_document.GetCommandText(command.Id) == original, "真实对白编辑可以完整撤销");
        }
        finally { _suppressReload = false; }
        await ReloadAsync();
        await SeekAsync((spoken.StartFrame + 10) / 30d);
        var voiceFrame = _viewport.LastFrame!;
        check(voiceFrame.Targets.Count >= 1 && voiceFrame.ActorAnimations is { Count: > 0 },
            "工作台暴露真实可见模型与当前动作状态，供单人镜头使用");
        long sequence = voiceFrame.Sequence;
        await TogglePlaybackAsync();
        bool mouth = false; int frames = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(2) && frames < 45)
        {
            var frame = await _viewport.WaitForFrameAsync(sequence, TimeSpan.FromSeconds(15));
            sequence = frame.Sequence; frames++;
            mouth |= frame.LipSync?.Any(lip => lip.IsActive && lip.MouthOpen > .01f) == true;
        }
        _viewport.Pause(); _timeline.IsPlaying = false;
        check(frames > 1 && mouth, "真实语音在持续播放的WinUI画布中驱动角色口型");
        await SeekAsync((spoken.StartFrame + 10) / 30d);
        SelectCommand(command.Id); UpdateLayout();
        _savedJson = _document.ToJson(); RefreshHeader();
    }
}
