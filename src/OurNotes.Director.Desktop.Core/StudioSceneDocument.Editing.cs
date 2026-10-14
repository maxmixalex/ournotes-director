using System.Globalization;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

namespace OurNotes.Director.Desktop.Core;

public sealed partial class StudioSceneDocument
{
    readonly SceneDocument document;
    readonly Stack<Revision> undo = new(), redo = new();
    Revision? transaction;
    string? selected;
    int frame;
    sealed record Revision(string Text, string BaseDirectory, int Frame, string? Selected, string Label,
        Dictionary<string, string> CommandIds, string? SelectedCommand);

    StudioSceneDocument(SceneDocument document) { this.document = document; Reload(); }

    public static StudioSceneDocument FromSceneDocument(SceneDocument document)
        => new(document ?? throw new ArgumentNullException(nameof(document)));
    public static StudioSceneDocument Parse(string json, string? filePath = null, string? baseDirectory = null)
        => FromSceneDocument(SceneDocument.Parse(json, filePath, baseDirectory));
    public static StudioSceneDocument Open(string path) => FromSceneDocument(SceneDocument.Open(path));

    public static StudioSceneDocument CreateBlank(string site = "intl", string language = "zh-Hans", double duration = 5)
        => Create(new SceneSource("", site, language), duration);

    public static StudioSceneDocument Create(SceneSource source, double duration = 3)
    {
        ArgumentNullException.ThrowIfNull(source);
        int count = ToFrame(duration, positive: true);
        var root = new JsonObject
        {
            ["format"] = DirectorProject.Format, ["version"] = 1,
            ["source"] = new JsonObject { ["adv"] = source.Adv, ["site"] = source.Site, ["language"] = source.Language },
            ["output"] = new JsonObject { ["width"] = 1920, ["height"] = 1080, ["supersample"] = 2,
                ["codec"] = "cineform", ["cineformQuality"] = VideoEncoding.DefaultCineFormQuality, ["clean"] = true, ["letterbox"] = "none" },
            ["initialize"] = new JsonArray(), ["finalize"] = new JsonArray(),
            ["commands"] = new JsonArray(
                (JsonNode)new JsonObject { ["cmd"] = "Director.Scene", ["Values"] = new JsonObject
                    { ["stageVisible"] = false, ["background"] = new JsonArray(0, 0, 0, 0) } },
                (JsonNode)new JsonObject { ["cmd"] = "Director.Camera", ["Values"] = new JsonObject
                    { ["framing"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["zoom"] = 1, ["roll"] = 0 } } },
                (JsonNode)new JsonObject { ["cmd"] = "Delay", ["Frames"] = count },
                (JsonNode)new JsonObject { ["cmd"] = "Delay", ["Frames"] = 0, ["Key"] = Marker + "duration/" + count.ToString(CultureInfo.InvariantCulture) })
        };
        if (source.IsLibrary) root["source"]!.AsObject().Remove("adv");
        return FromSceneDocument(SceneDocument.Parse(root.ToJsonString(DesktopSettings.JsonOptions)));
    }

    /// <summary>Use EditDocument/ReplaceJson for mutations so history and derived tracks stay synchronized.</summary>
    public SceneDocument Document => document;
    /// <summary>Current playback range. For Automatic projects, consult ResolvedDuration before displaying it as the story's total duration.</summary>
    public double Duration => PlaybackFrameCount / (double)FramesPerSecond;
    public int Frame { get => frame; set => Time = value / (double)FramesPerSecond; }
    public double Time
    {
        get => frame / (double)FramesPerSecond;
        set { int next = ClampPlayheadFrame(ToFrame(value)); if (next != frame) { frame = next; PlayheadChanged?.Invoke(); } }
    }
    public string? SelectedId
    {
        get => selected;
        set { if (selected != value) { selected = value; SelectionChanged?.Invoke(); } }
    }
    public IReadOnlyList<StudioActor> Actors => Sample(Time).Actors;
    public bool CanUndo => undo.Count > 0 || transaction is not null && transaction.Text != document.Text;
    public bool CanRedo => transaction is null && redo.Count > 0;
    public string? UndoLabel => transaction?.Label ?? (undo.TryPeek(out var previous) ? previous.Label : null);
    public string? RedoLabel => redo.TryPeek(out var next) ? next.Label : null;
    public event Action? Changed;
    public event Action? PlayheadChanged;
    public event Action? SelectionChanged;

    public void BeginEdit(string label = "编辑场景")
    {
        if (transaction is not null) throw new InvalidOperationException("已有编辑事务尚未结束。");
        transaction = Capture(label);
    }
    public void CommitEdit()
    {
        if (transaction is null) return;
        var previous = transaction; transaction = null;
        if (previous.Text != document.Text) { undo.Push(previous); redo.Clear(); }
    }
    public void CancelEdit()
    {
        if (transaction is null) return;
        var previous = transaction; transaction = null; Restore(previous); Changed?.Invoke();
    }
    public bool Undo()
    {
        CommitEdit();
        if (!undo.TryPop(out var previous)) return false;
        redo.Push(Capture(previous.Label)); Restore(previous); Changed?.Invoke(); return true;
    }
    public bool Redo()
    {
        if (transaction is not null || !redo.TryPop(out var next)) return false;
        undo.Push(Capture(next.Label)); Restore(next); Changed?.Invoke(); return true;
    }
    Revision Capture(string label) => new(document.Text, document.BaseDirectory, frame, selected, label, CaptureCommandIds(), selectedCommand);
    void Restore(Revision revision)
    {
        document.ReplaceText(SceneDocument.Parse(revision.Text, baseDirectory: revision.BaseDirectory).TextAtDirectory(document.BaseDirectory));
        pendingCommandIds = revision.CommandIds; selectedCommand = revision.SelectedCommand;
        RefreshCommandIndex(); Reload(); frame = ClampPlayheadFrame(revision.Frame); selected = revision.Selected;
    }
    void Edit(string label, Action action)
    {
        var previous = Capture(label);
        try { action(); }
        catch { Restore(previous); throw; }
        if (previous.Text == document.Text) return;
        if (transaction is null) { undo.Push(previous); redo.Clear(); }
        Changed?.Invoke();
    }
    public void EditDocument(Action<SceneDocument> edit, string label = "编辑 JSON")
    {
        ArgumentNullException.ThrowIfNull(edit);
        Edit(label, () => { edit(document); Reload(); });
    }
    public void ReplaceJson(string json) => EditDocument(value => value.ReplaceText(json), "编辑高级 JSON");
    public string ToJson(bool indented = true) => document.ToJson(indented);
    public SceneDocument ToSceneDocument() => SceneDocument.Parse(document.Text, document.FilePath, document.BaseDirectory);
    public void Save(string? path = null) { CommitEdit(); document.Save(path); Reload(); Changed?.Invoke(); }

    public void SetDuration(double seconds)
    {
        int count = ToFrame(seconds, positive: true);
        if (!timingKnown) { SetDurationMode(StudioDurationMode.Fixed, seconds); return; }
        EnsureTimeline();
        if (keys.Any(key => key.Frame > count)) throw new ArgumentException("请先移动或删除结束时间以后的关键帧。");
        if (originals.Any(row => !row.IsDelay && row.EndFrame > count)) throw new ArgumentException("结束时间早于已有原始指令，请先编辑对应指令。");
        Edit("修改片段时长", () =>
        {
            durationFrames = count; durationMode = StudioDurationMode.Fixed;
            if (playbackMarkerPresent) playbackRangeFrames = count;
            Compile();
        });
    }

    /// <summary>Changes only the editor playback/export range for scripts whose native duration is not yet known.</summary>
    public void SetPlaybackDuration(double seconds)
    {
        if (timingKnown) { SetDuration(seconds); return; }
        durationFrames = ToFrame(seconds, positive: true); durationMode = StudioDurationMode.Fixed;
        playbackRangeFrames = durationFrames; frame = Math.Min(frame, durationFrames);
        InvalidateSamples(); PlayheadChanged?.Invoke();
    }

    public string AddActor(string model, string? name = null)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("请选择归档中的实际人物模型。", nameof(model));
        string id = name?.Trim() ?? "";
        var used = ActorDefinitions().Select(actor => actor.Id).ToHashSet(StringComparer.Ordinal);
        if (id.Length == 0) { int index = 1; do { id = "actor" + (index++).ToString(CultureInfo.InvariantCulture); } while (used.Contains(id)); }
        if (id is CameraId or SceneId || used.Contains(id)) throw new ArgumentException("人物名称已使用，请选择另一个名称。", nameof(name));
        if (!timingKnown)
        {
            string target = id;
            EditNativeCommands("添加人物", maps =>
            {
                document.InsertCommand("commands", 0, new() { ["cmd"] = "Character", ["TargetName"] = target, ["Model"] = model });
                maps["commands"].Insert(0, NewId());
                document.InsertCommand("commands", 1, StudioCommandCatalog.Create("Director.Actor", target));
                maps["commands"].Insert(1, NewId()); selected = target;
            });
            return id;
        }
        Edit("添加人物", () =>
        {
            AddOriginal(new() { ["cmd"] = "Character", ["TargetName"] = id, ["Model"] = model }, 0);
            AddOriginal(new() { ["cmd"] = "Director.Actor", ["TargetName"] = id, ["Space"] = "screen", ["Values"] = new JsonObject
            {
                ["position"] = new JsonArray(.5, .5), ["height"] = .95, ["rotation"] = new JsonArray(0, 0, 0),
                ["scale"] = new JsonArray(1, 1, 1), ["opacity"] = 1, ["visible"] = true,
                ["animation"] = "director", ["loop"] = true, ["physics"] = true, ["breath"] = true, ["blink"] = true,
                ["order"] = used.Count
            } }, 0);
            selected = id; Compile();
        });
        return id;
    }

    public void RemoveActor(string id)
    {
        EnsureTimeline(); RequireActor(id);
        // Other rows (dialogue, voice, effects) retain their complete original data.
        string[] ownedCommands = ["Character", "Director.Actor", "Director.Parameter", "Motion", "Expression", "Pause", "Resume"];
        Edit("删除人物", () =>
        {
            originals.RemoveAll(row => Text(row.Data["TargetName"]) == id && ownedCommands.Contains(row.Command));
            keys.RemoveAll(key => key.Target == id);
            if (selected == id) selected = null;
            Compile();
        });
    }

    public void SetActorTransform(string id, double x, double y, double height, double roll, double time, bool autokey)
    {
        var actor = RequireActor(id);
        if (actor.Space != "screen") throw new InvalidOperationException("此人物使用世界或游戏槽位坐标，请使用对应坐标字段，不能按屏幕百分比拖动。");
        SetActorValues(id, new() { ["position"] = new JsonArray(x, y), ["height"] = height, ["rotationZ"] = roll }, time, autokey);
    }
    public void SetActorTransform(string id, StudioTransform transform, double time, bool autokey)
    {
        ArgumentNullException.ThrowIfNull(transform);
        var actor = RequireActor(id);
        JsonArray position = actor.Space == "world" ? new(transform.X, transform.Y, transform.Z) : new(transform.X, transform.Y);
        SetActorValues(id, new() { ["position"] = position, ["height"] = transform.Height,
            ["rotation"] = new JsonArray(transform.RotationX, transform.RotationY, transform.Roll),
            ["scale"] = new JsonArray(transform.ScaleX, transform.ScaleY, transform.ScaleZ), ["opacity"] = transform.Opacity }, time, autokey);
    }
    public void SetActorValues(string id, JsonObject values, double time = 0, bool autokey = false)
    {
        var actor = RequireActor(id);
        if (actor.Space is not ("screen" or "world")) throw new InvalidOperationException("此人物仍由游戏槽位控制；请在高级 JSON 中明确切换为 Director.Actor。");
        CheckValues("Director.Actor", id, values, actor.Space);
        SetValues(id, "actor", values, time, autokey, "修改人物", actor.Space);
    }
    public void SetActorOpacity(string id, double opacity, double time = 0, bool autokey = false)
        => SetActorValues(id, new() { ["opacity"] = opacity }, time, autokey);
    public void SetActorVisibility(string id, bool visible, double time = 0, bool autokey = false)
        => SetActorValues(id, new() { ["visible"] = visible }, time, autokey);
    public void SetCamera(StudioCamera camera, double time = 0, bool autokey = false)
    {
        ArgumentNullException.ThrowIfNull(camera);
        var values = (JsonObject)camera.WorldValues.DeepClone();
        values["framing"] = new JsonObject { ["x"] = camera.X, ["y"] = camera.Y, ["zoom"] = camera.Zoom, ["roll"] = camera.Roll };
        CheckValues("Director.Camera", null, values);
        SetValues(CameraId, "camera", values, time, autokey, "修改相机");
    }
    public void SetCameraValues(JsonObject values, double time = 0, bool autokey = false)
    { CheckValues("Director.Camera", null, values); SetValues(CameraId, "camera", values, time, autokey, "修改相机"); }
    public void SetGrade(StudioGrade grade, double time = 0, bool autokey = false)
    {
        ArgumentNullException.ThrowIfNull(grade);
        SetValues(SceneId, "scene", new() { ["grade"] = new JsonObject { ["exposure"] = grade.Exposure,
            ["contrast"] = grade.Contrast, ["saturation"] = grade.Saturation, ["vignette"] = grade.Vignette } }, time, autokey, "修改后处理");
    }
    public void SetBackground(JsonArray rgba, double time = 0, bool autokey = false)
        => SetValues(SceneId, "scene", new() { ["background"] = rgba.DeepClone() }, time, autokey, "修改背景");
    public void SetBackgroundImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("背景图片路径不能为空。", nameof(path));
        SetValues(SceneId, "scene", new() { ["backgroundImage"] = new JsonObject { ["path"] = path }, ["stageVisible"] = false }, 0, false, "设置背景图片");
    }
    public void SetMotion(string id, string motion, double time, double fade = 0)
        => SetEvent(id, "motion", motion, time, fade);
    public void SetExpression(string id, string expression, double time, double fade = 0)
        => SetEvent(id, "expression", expression, time, fade);

    void SetValues(string target, string category, JsonObject values, double time, bool autokey, string label, string? space = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        string command = category switch { "actor" => "Director.Actor", "camera" => "Director.Camera", _ => "Director.Scene" };
        CheckValues(command, category == "actor" ? target : null, values, space);
        int at = ToFrame(time);
        if (autokey) EnsureTimeline();
        Edit(label, () =>
        {
            if (!autokey)
            {
                if (!timingKnown) { PatchOriginalBase(command, target, values, space); return; }
                var entry = originals.FirstOrDefault(row => row.Command == command && row.Frame == 0 && !Ignored(row.Data)
                    && (category != "actor" || Text(row.Data["TargetName"]) == target));
                if (entry is null)
                {
                    var row = new JsonObject { ["cmd"] = command, ["Values"] = values.DeepClone() };
                    if (category == "actor") { row["TargetName"] = target; row["Space"] = space; }
                    AddOriginal(row, 0);
                }
                else Merge((JsonObject)(entry.Data["Values"] ??= new JsonObject()), values);
            }
            else foreach (var (property, value) in Leaves(values))
            {
                string track = category + "." + property;
                var existing = keys.FirstOrDefault(key => key.Target == target && key.Track == track && key.Frame == at);
                var row = new JsonObject { ["cmd"] = command, ["Values"] = new JsonObject() };
                SetPath(row["Values"]!.AsObject(), property, value.DeepClone());
                if (category == "actor") { row["TargetName"] = target; row["Space"] = space; }
                if (existing is null) keys.Add(new(NewId(), target, track, at, "Linear", row));
                else existing.Row = row;
            }
            durationFrames = Math.Max(durationFrames, at); Compile();
        });
    }

    void SetEvent(string id, string kind, string name, double time, double fade)
    {
        EnsureTimeline(); RequireActor(id);
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("请选择实际动作或表情名称。", nameof(name));
        if (!double.IsFinite(fade) || fade < 0 || fade > 36000) throw new ArgumentOutOfRangeException(nameof(fade));
        int at = ToFrame(time);
        Edit(kind == "motion" ? "修改动作" : "修改表情", () =>
        {
            var row = new JsonObject { ["cmd"] = kind == "motion" ? "Motion" : "Expression", ["TargetName"] = id,
                [kind == "motion" ? "MotionName" : "ExpressionName"] = name, ["MotionFadeIn"] = fade };
            var existing = keys.FirstOrDefault(key => key.Target == id && key.Track == kind && key.Frame == at);
            if (existing is null) keys.Add(new(NewId(), id, kind, at, "Step", row)); else existing.Row = row;
            durationFrames = Math.Max(durationFrames, at); Compile();
        });
    }
    public void MoveKeyframe(string keyId, double time)
    {
        EnsureTimeline(); int at = ToFrame(time);
        var key = keys.FirstOrDefault(key => key.Id == keyId);
        if (key is null)
        {
            var raw = originals.FirstOrDefault(row => row.Id == keyId && IsOriginalKey(row))
                ?? throw new ArgumentException("关键帧已不存在。", nameof(keyId));
            if (raw.Section != "commands") throw new InvalidOperationException("初始化与结束指令属于固定指令段，请在高级 JSON 中移动。");
            int start = at - (raw.EndFrame - raw.Frame);
            if (start < 0) throw new ArgumentException("原始补间整体移动后不能早于第 0 帧。", nameof(time));
            Edit("移动原始指令", () => { raw.Frame = start; raw.EndFrame = at; durationFrames = Math.Max(durationFrames, at); Compile(); });
            return;
        }
        if (keys.Any(other => other.Id != key.Id && other.Track == key.Track && other.Target == key.Target && other.Frame == at))
            throw new ArgumentException("同一轨道在此帧已有关键帧，请先移动或删除它。");
        Edit("移动关键帧", () => { key.Frame = at; durationFrames = Math.Max(durationFrames, at); Compile(); });
    }
    public void DeleteKeyframe(string keyId)
    {
        EnsureTimeline();
        if (!keys.Any(key => key.Id == keyId) && !originals.Any(row => row.Id == keyId && IsOriginalKey(row)))
            throw new ArgumentException("关键帧已不存在。", nameof(keyId));
        Edit("删除关键帧", () => { keys.RemoveAll(key => key.Id == keyId); originals.RemoveAll(row => row.Id == keyId && IsOriginalKey(row)); Compile(); });
    }
    public void SetKeyframeEase(string keyId, string ease)
    {
        EnsureTimeline(); CheckEase(ease);
        var key = keys.FirstOrDefault(key => key.Id == keyId);
        if (key is null)
        {
            var raw = originals.FirstOrDefault(row => row.Id == keyId && IsOriginalKey(row))
                ?? throw new ArgumentException("关键帧已不存在。", nameof(keyId));
            if (!raw.Command.StartsWith("Director.", StringComparison.Ordinal)) throw new InvalidOperationException("动作事件没有数值插值。");
            Edit("修改原始指令插值", () =>
            {
                raw.Data["Ease"] = ease == "Step" ? "Linear" : ease;
                if (ease == "Step") { raw.Frame = raw.EndFrame; raw.Data["Duration"] = 0; }
                Compile();
            });
            return;
        }
        Edit("修改插值", () => { key.Ease = ease; Compile(); });
    }

    void PatchOriginalBase(string command, string target, JsonObject values, string? space)
    {
        var root = document.Root;
        foreach (string section in Sections)
            if (root[section] is JsonArray rows)
                foreach (var row in rows.OfType<JsonObject>())
                    if (Command(row) == command && !Ignored(row) && (command != "Director.Actor" || Text(row["TargetName"]) == target))
                    {
                        Merge((JsonObject)(row["Values"] ??= new JsonObject()), values);
                        if (space is not null) row["Space"] = space;
                        document.ReplaceText(root.ToJsonString(DesktopSettings.JsonOptions)); Reload(); return;
                    }
        throw new InvalidOperationException("原始剧情尚未解析时间；请先在高级 JSON 中添加明确的初始 Director 指令，或读取原剧情后再编辑。");
    }
    static void CheckValues(string command, string? target, JsonObject values, string? space = null)
    {
        var row = new JsonObject { ["cmd"] = command, ["Values"] = values.DeepClone() };
        if (target is not null) row["TargetName"] = target;
        if (space is not null) row["Space"] = space;
        DirectorValidation.Check(AdvProgram.Row(row, "studio"));
        if (command == "Director.Actor" && values["position"] is JsonArray position
            && position.Count != (space == "world" ? 3 : 2)) throw new ArgumentException("位置分量数量必须与人物坐标空间一致。");
    }
    StudioActor RequireActor(string id) => Sample(Time).Actors.FirstOrDefault(actor => actor.Id == id)
        ?? throw new ArgumentException("人物已不存在：" + id, nameof(id));
    static int ToFrame(double seconds, bool positive = false)
    {
        if (!double.IsFinite(seconds) || seconds < (positive ? 1d / FramesPerSecond : 0) || seconds > 36000)
            throw new ArgumentOutOfRangeException(nameof(seconds), "时间必须在 0 到 36000 秒之间，片段至少为一帧。");
        return checked((int)Math.Round(seconds * FramesPerSecond, MidpointRounding.AwayFromZero));
    }
}
