using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

namespace OurNotes.Director.Desktop.Core;

public sealed partial class StudioSceneDocument
{
    const string PlaybackMarker = Marker + "playback/";
    StudioDurationMode durationMode;
    int? playbackRangeFrames;
    bool playbackMarkerPresent;
    NativePlaybackTiming? nativeTiming;
    string? hashedText, hashedBaseDirectory, projectHash, snapshotHash;

    public StudioDurationMode DurationMode => durationMode;
    /// <summary>Automatic duration exists only after real native execution reaches the end, including its final frame.</summary>
    public double? ResolvedDuration => durationMode == StudioDurationMode.Fixed ? Duration
        : nativeTiming is { IsComplete: true } timing ? timing.Duration : null;
    public bool IsDurationResolved => ResolvedDuration.HasValue;
    public double PreviewLimitDuration => durationMode == StudioDurationMode.Automatic ? 36000 : Duration;
    public double PlaybackEndTime => durationMode == StudioDurationMode.Automatic && nativeTiming?.EndFrame is int end
        ? end / (double)FramesPerSecond : Duration;
    public string? NativeTimingError => nativeTiming?.Error;
    public string NativeProjectHash { get { RefreshNativeHashes(); return snapshotHash!; } }
    public event Action? TimingChanged;

    int PlaybackFrameCount => playbackRangeFrames ?? (durationMode == StudioDurationMode.Automatic
        && nativeTiming is { IsComplete: true, EndFrame: int end } ? end + 1 : durationFrames);
    int ClampPlayheadFrame(int value) => durationMode == StudioDurationMode.Automatic
        ? nativeTiming?.EndFrame is int end ? Math.Clamp(value, 0, end) : Math.Clamp(value, 0, 1080000)
        : Math.Clamp(value, 0, PlaybackFrameCount);

    /// <summary>
    /// Stores a playback/export range as a zero-frame native metadata row. This does not truncate or
    /// retime original commands; Automatic export follows the native interpreter until it finishes.
    /// </summary>
    public void SetDurationMode(StudioDurationMode mode, double? fixedDuration = null)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (mode == StudioDurationMode.Automatic && fixedDuration is not null)
            throw new ArgumentException("自动原剧情时长不接受固定秒数。", nameof(fixedDuration));
        int? count = mode == StudioDurationMode.Fixed ? ToFrame(fixedDuration ?? Duration, positive: true) : null;
        if (!CanEditCommands) throw new InvalidOperationException("旧 Shot 的播放时长由其 duration 定义；请先导入统一原生项目。");
        var root = document.Root;
        if (root["commands"] is not JsonArray commands)
        {
            if (mode == StudioDurationMode.Automatic && document.IsSectionInherited("commands")) return;
            throw new InvalidOperationException("请先从归档展开正文指令，再保存固定播放范围；不会覆盖继承原剧情。");
        }
        if (durationMode == mode && (mode == StudioDurationMode.Automatic || playbackRangeFrames == count)) return;
        for (int index = commands.Count - 1; index >= 0; index--)
            if (commands[index] is JsonObject row && IsPlaybackMarker(row)) commands.RemoveAt(index);
        commands.Insert(0, PlaybackRow(mode, count));
        EditDocument(value => value.ReplaceText(root.ToJsonString(DesktopSettings.JsonOptions)), "修改播放范围");
    }

    /// <summary>Returns false for a stale project or regressing analysis; never marks the document dirty or adds undo entries.</summary>
    public bool ApplyNativeTiming(NativePlaybackTiming timing)
    {
        ArgumentNullException.ThrowIfNull(timing); RefreshNativeHashes();
        if (timing.ProjectHash != projectHash && timing.ProjectHash != snapshotHash) return false;
        if (timing.AnalyzedFrame is < 0 or > 1080000) throw new ArgumentOutOfRangeException(nameof(timing));
        if (timing.IsComplete)
        {
            if (timing.EndFrame is not int end || end is < 0 or >= 1080000
                || timing.Duration is not double duration || !double.IsFinite(duration)
                || Math.Abs(duration - (end + 1d) / FramesPerSecond) > 1e-7 || timing.Error is not null)
                throw new ArgumentException("完成时长必须来自原生结束帧，并包含该末帧。", nameof(timing));
        }
        else if (timing.EndFrame is not null || timing.Duration is not null)
            throw new ArgumentException("尚未运行结束，不能将时长标记为已知。", nameof(timing));
        ArgumentNullException.ThrowIfNull(timing.Commands);
        if (timing.Commands.Any(command => !Sections.Contains(command.Section, StringComparer.Ordinal)
            || command.Index < 0 || command.StartFrame is < 0 or > 1080000 || command.Ordinal < 0
            || command.EndFrame is int end && (end < command.StartFrame || end > 1080000)))
            throw new ArgumentException("原生命令时序包含无效段、位置或帧。", nameof(timing));
        if (nativeTiming is { IsComplete: true } && !timing.IsComplete) return false;
        if (nativeTiming is { IsComplete: false } previous && !timing.IsComplete
            && timing.AnalyzedFrame < previous.AnalyzedFrame) return false;
        nativeTiming = timing with { Commands = Array.AsReadOnly(timing.Commands.ToArray()) };
        if (durationMode == StudioDurationMode.Automatic)
        {
            durationFrames = timing.IsComplete ? timing.EndFrame!.Value + 1 : Math.Max(durationFrames, timing.AnalyzedFrame + 1);
            frame = ClampPlayheadFrame(frame);
        }
        InvalidateSamples(); TimingChanged?.Invoke(); return true;
    }

    void ReadPlaybackSettings()
    {
        var root = document.Root;
        playbackRangeFrames = null; playbackMarkerPresent = false;
        durationMode = document.Kind == SceneDocumentKind.LegacyShot ? StudioDurationMode.Fixed : StudioDurationMode.Automatic;
        foreach (string section in Sections)
            if (root[section] is JsonArray rows)
                foreach (var row in rows.OfType<JsonObject>())
                {
                    if (row.Count == 3 && NativeCommandName(row) == "Delay" && Number(row["Frames"], -1) == 0
                        && Text(row["Key"]) is string key && key.StartsWith(Marker + "duration/", StringComparison.Ordinal)
                        && int.TryParse(key[(Marker.Length + 9)..], NumberStyles.None, CultureInfo.InvariantCulture, out int declared)
                        && declared is >= 1 and <= 1080000) durationMode = StudioDurationMode.Fixed;
                    if (!TryPlaybackMarker(row, out var mode, out var count)) continue;
                    playbackMarkerPresent = true; durationMode = mode; playbackRangeFrames = count;
                }
        // An explicit mode wins regardless of where the native duration marker occurs.
        foreach (string section in Sections)
            if (root[section] is JsonArray rows)
                foreach (var row in rows.OfType<JsonObject>())
                    if (TryPlaybackMarker(row, out var mode, out var count)) { durationMode = mode; playbackRangeFrames = count; }
        InvalidateNativeTiming();
    }

    static JsonObject PlaybackRow(StudioDurationMode mode, int? count) => new()
    {
        ["cmd"] = "Delay", ["Frames"] = 0,
        ["Key"] = PlaybackMarker + (mode == StudioDurationMode.Automatic ? "automatic" : "fixed/" + count!.Value.ToString(CultureInfo.InvariantCulture))
    };
    static bool IsPlaybackMarker(JsonObject row) => TryPlaybackMarker(row, out _, out _);
    static bool TryPlaybackMarker(JsonObject row, out StudioDurationMode mode, out int? count)
    {
        mode = StudioDurationMode.Automatic; count = null;
        if (row.Count != 3 || NativeCommandName(row) != "Delay" || Number(row["Frames"], -1) != 0) return false;
        string key = Text(row["Key"]);
        if (key == PlaybackMarker + "automatic") return true;
        if (!key.StartsWith(PlaybackMarker + "fixed/", StringComparison.Ordinal)
            || !int.TryParse(key[(PlaybackMarker.Length + 6)..], NumberStyles.None, CultureInfo.InvariantCulture, out int frames)
            || frames is < 1 or > 1080000) return false;
        mode = StudioDurationMode.Fixed; count = frames; return true;
    }

    void RefreshNativeHashes()
    {
        if (hashedText == document.Text && hashedBaseDirectory == document.BaseDirectory) return;
        projectHash = Archive.Hash(Encoding.UTF8.GetBytes(document.Root.ToJsonString()));
        snapshotHash = Archive.Hash(Encoding.UTF8.GetBytes(document.RenderRoot().ToJsonString()));
        hashedText = document.Text; hashedBaseDirectory = document.BaseDirectory;
    }
    void InvalidateNativeTiming()
    {
        if (nativeTiming is null) return;
        RefreshNativeHashes();
        if (nativeTiming.ProjectHash != projectHash && nativeTiming.ProjectHash != snapshotHash) nativeTiming = null;
    }

    public static StudioSceneDocument CreateSingleActor(SceneSource source, string model,
        int width = 1920, int height = 1080, double duration = 3, string? motion = null, string? expression = null)
    {
        VideoEncoding.Validate("cineform", width, height);
        var scene = Create(source, duration);
        scene.SetOutput(width, height, supersample: 1, codec: "cineform", clean: true, letterbox: "none");
        string id = scene.AddActor(model);
        scene.SetActorTransform(id, .5, .5, 1, 0, 0, false);
        if (!string.IsNullOrWhiteSpace(motion)) scene.SetMotion(id, motion, 0);
        if (!string.IsNullOrWhiteSpace(expression)) scene.SetExpression(id, expression, 0);
        scene.undo.Clear(); scene.redo.Clear();
        return scene;
    }

    /// <summary>Uses Archive.Model's actual canvas pixels, padding only to CineForm's documented dimension multiples.</summary>
    public static StudioSceneDocument CreateSingleActor(SceneSource source, string model, JsonObject modelInfo,
        double duration = 3, string? motion = null, string? expression = null)
    {
        ArgumentNullException.ThrowIfNull(modelInfo);
        var canvas = modelInfo["canvas"] as JsonObject ?? throw new ArgumentException("模型信息缺少原生 canvas。", nameof(modelInfo));
        double canvasWidth = Number(canvas["width"], double.NaN), canvasHeight = Number(canvas["height"], double.NaN);
        if (!double.IsFinite(canvasWidth) || !double.IsFinite(canvasHeight) || canvasWidth <= 0 || canvasHeight <= 0
            || canvasWidth > RenderDimensions.MaxSide || canvasHeight > RenderDimensions.MaxSide)
            throw new ArgumentException("模型原生画布尺寸无效或超过输出上限，请明确选择输出尺寸。", nameof(modelInfo));
        int width = Math.Max(16, (int)Math.Ceiling(canvasWidth / 16) * 16);
        int height = Math.Max(32, (int)Math.Ceiling(canvasHeight / 8) * 8);
        RenderDimensions.Validate(width, height, 1);
        double originX = Number(canvas["originX"], canvasWidth / 2), originY = Number(canvas["originY"], canvasHeight / 2);
        if (!double.IsFinite(originX) || !double.IsFinite(originY)) throw new ArgumentException("模型画布原点必须为有限数值。", nameof(modelInfo));
        var scene = CreateSingleActor(source, model, width, height, duration,
            motion ?? Text(modelInfo["defaultMotion"]), expression ?? Text(modelInfo["defaultExpression"]));
        scene.SetActorTransform(scene.Actors.Single().Id, .5 + (originX - canvasWidth / 2) / width,
            .5 + (canvasHeight / 2 - originY) / height, canvasHeight / height, 0, 0, false);
        scene.undo.Clear(); scene.redo.Clear();
        return scene;
    }
}
