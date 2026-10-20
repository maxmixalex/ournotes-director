using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

namespace OurNotes.Director.Desktop.Core;

public sealed record Live2DTransform(double X = .5, double Y = .5, double Scale = 1,
    double Rotation = 0, double Opacity = 1);
public sealed record Live2DParameter(string Id, string Name, double Minimum, double Maximum,
    double Default, double Value, bool IsOverridden);

/// <summary>A single native model and its complete source performance. Edits are ordinary
/// native commands; the workbench does not keep a second animation evaluator.</summary>
public sealed partial class Live2DDocument
{
    public const string Format = "ournotes-director/live2d";
    readonly Stack<JsonObject> undo = new(), redo = new();
    readonly Dictionary<string, PreviewParameter> observed = new(StringComparer.Ordinal);
    JsonObject root;
    string savedContent = "";
    string baseDirectory;
    public string? FilePath { get; private set; }
    public string ModelId => Text(root, "model");
    public string ActorId => Text(root, "actor");
    public string Site => Text(root, "site", "intl");
    public string Language => Text(root, "language", "zh-Hans");
    public JsonNode Metadata => root["metadata"]!.DeepClone();
    /// <summary>The last addressable frame time, as in TimelineRange.Full(Duration, 30).</summary>
    public double Duration => Number(root, "duration", 5);
    public int Width => root["output"]!["width"]!.GetValue<int>();
    public int Height => root["output"]!["height"]!.GetValue<int>();
    public bool HasUnsavedChanges => Content() != savedContent;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public bool IsCapturedPerformance => root["performance"] is JsonObject;
    public double Time
    {
        get => Number(root, "time");
        set
        {
            if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
            double snapped = Snap(value);
            if (Time == snapped) return;
            root["time"] = snapped; observed.Clear();
        }
    }
    public TimelineRange? Range
    {
        get => root["range"] is JsonObject range ? new(range["startFrame"]!.GetValue<int>(),
            range["endFrameExclusive"]!.GetValue<int>(), 30) : null;
        set
        {
            if (value is not null && value.FramesPerSecond != 30) throw new ArgumentException("Live2D 时间轴使用 30 fps。");
            var clamped = value?.ClampTo(TimelineRange.Full(Duration, 30));
            Change(next => next["range"] = clamped is null ? null : new JsonObject
            { ["startFrame"] = clamped.StartFrame, ["endFrameExclusive"] = clamped.EndFrameExclusive });
        }
    }
    public Live2DTransform Transform
    {
        get
        {
            var value = ActorIsolation.ReadTransform(root["transform"], ActorId);
            return new(value.X, value.Y, value.Scale, value.Rotation, value.Opacity);
        }
    }
    public string? Motion => CurrentAnimation("Motion", "MotionName", "defaultMotion");
    public string? Expression => CurrentAnimation("Expression", "ExpressionName", "defaultExpression");
    public bool Breath => Controller("breath");
    public bool Blink => Controller("blink");
    public bool Physics => Controller("physics");
    public bool Loop => Controller("loop");
    public IReadOnlyList<Live2DParameter> Parameters
    {
        get
        {
            // Captured performances can contain thousands of rows. Resolve their active
            // pins once per inspector refresh, rather than parsing that history per slider.
            var pins = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var row in ActiveRows())
                if (Text(row, "cmd") == "Director.Parameter") pins[Text(row, "Parameter")] = row;
            return Definitions().Select(definition =>
            {
                var pin = pins.GetValueOrDefault(definition.Id);
                bool pinned = pin is not null && Text(pin, "Operation") != "release";
                double value = observed.TryGetValue(definition.Id, out var live) ? live.Value
                    : pinned ? Number(pin!, "Value", definition.Default) : definition.Default;
                return definition with { Value = Math.Clamp(value, definition.Minimum, definition.Maximum),
                    IsOverridden = live?.IsOverridden ?? pinned };
            }).ToArray();
        }
    }

    Live2DDocument(JsonObject value, string? filePath = null, string? directory = null)
    {
        root = (JsonObject)value.DeepClone();
        FilePath = filePath is null ? null : Path.GetFullPath(filePath);
        baseDirectory = FilePath is not null ? Path.GetDirectoryName(FilePath)! : Path.GetFullPath(directory ?? Environment.CurrentDirectory);
        Validate(); NormalizeDependencies(root["performance"] as JsonObject, baseDirectory);
    }

    public static Live2DDocument Create(string modelId, string site = "intl", string language = "zh-Hans", JsonNode? metadata = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        var output = new JsonObject { ["width"] = 1080, ["height"] = 1920 };
        if (metadata?["canvas"] is not null)
        {
            var canvas = SceneAssetExport.Canvas(metadata);
            output["width"] = canvas.Width; output["height"] = canvas.Height;
        }
        return new(new JsonObject
        {
            ["format"] = Format, ["version"] = 1, ["model"] = modelId, ["actor"] = "live2d",
            ["site"] = site, ["language"] = language, ["duration"] = 5, ["time"] = 0,
            ["output"] = output, ["transform"] = TransformJson(new()),
            ["metadata"] = metadata?.DeepClone() ?? new JsonObject(), ["edits"] = new JsonArray()
        });
    }

    public static Live2DDocument FromSceneActor(SceneDocument source, string actorId, string modelId,
        JsonNode metadata, double playhead, double duration, TimelineRange? range = null)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(metadata);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        if (source.Kind != SceneDocumentKind.UnifiedProject || source.Validate().HasErrors)
            throw new ArgumentException("请先将场景转换为有效的统一项目。");
        var document = Create(modelId, source.Source.Site, source.Source.Language, metadata);
        document.root["actor"] = actorId;
        document.root["performance"] = source.Root;
        document.baseDirectory = source.BaseDirectory;
        NormalizeDependencies(document.root["performance"] as JsonObject, source.BaseDirectory);
        document.SetDuration(duration); document.Time = playhead; document.Range = range;
        // Only capture a single selection when the source has an exact native frame
        // schedule. Sequential legacy performances remain intact and read-only rather
        // than guessing where their current motion/expression should begin.
        bool hasNativeSchedule = document.root["performance"]?["commands"] is JsonArray sourceRows
            && sourceRows.OfType<JsonObject>().All(row => row["AtFrame"] is not null);
        if (hasNativeSchedule)
        {
            string? selectedMotion = document.Motion;
            string? selectedExpression = document.Expression;
            document.Time = 0;
            if (selectedMotion is { Length: > 0 }) document.SetMotion(selectedMotion);
            if (selectedExpression is { Length: > 0 }) document.SetExpression(selectedExpression);
            document.Time = 0;
        }
        document.undo.Clear(); document.redo.Clear();
        document.Validate();
        return document;
    }

    public static Live2DDocument Parse(string text, string? filePath = null, string? baseDirectory = null)
    {
        // The common parser rejects duplicate keys and preserves original numeric tokens.
        var parsed = SceneDocument.Parse(text, filePath, baseDirectory);
        var document = new Live2DDocument(parsed.Root, filePath, baseDirectory);
        if (filePath is not null) document.savedContent = document.Content();
        return document;
    }
    public static Live2DDocument Open(string path)
    {
        string full = Path.GetFullPath(path); return Parse(File.ReadAllText(full), full);
    }
    public string ToJson() => root.ToJsonString(DesktopSettings.JsonOptions);
    public void Save(string? path = null)
    {
        string destination = Path.GetFullPath(path ?? FilePath ?? throw new ArgumentException("新 Live2D 项目需要保存路径。"));
        Validate(); DesktopSettings.AtomicWrite(destination, ToJson());
        FilePath = destination; baseDirectory = Path.GetDirectoryName(destination)!; savedContent = Content();
    }
    public JsonObject ToDraft() => new()
    { ["project"] = root.DeepClone(), ["filePath"] = FilePath, ["baseDirectory"] = baseDirectory, ["savedContent"] = savedContent };
    public static Live2DDocument FromDraft(JsonObject draft)
    {
        var document = new Live2DDocument(draft["project"] as JsonObject ?? throw new ArgumentException("Live2D 草稿缺少项目。"),
            draft["filePath"]?.GetValue<string>(), draft["baseDirectory"]?.GetValue<string>());
        document.savedContent = draft["savedContent"]?.GetValue<string>() ?? ""; return document;
    }
    public void MarkUnsaved() => savedContent = "";

    public void SetMotion(string? motion) => SetAnimation("Motion", "MotionName", "motions", motion);
    public void SetExpression(string? expression) => SetAnimation("Expression", "ExpressionName", "expressions", expression);
    public void SetParameter(string id, double value)
    {
        var definition = Definitions().FirstOrDefault(parameter => parameter.Id == id)
            ?? throw new ArgumentException("请先读取模型的真实参数；未知参数：" + id);
        if (!double.IsFinite(value) || value < definition.Minimum || value > definition.Maximum)
            throw new ArgumentOutOfRangeException(nameof(value), "参数超出模型的真实范围。");
        SetEdit(new() { ["cmd"] = "Director.Parameter", ["TargetName"] = ActorId,
            ["Parameter"] = id, ["Value"] = value, ["Duration"] = 0 });
    }
    /// <summary>Releases the pin at the current time while retaining its earlier history.</summary>
    public void ResetParameter(string id)
    {
        if (!Definitions().Any(parameter => parameter.Id == id)) throw new ArgumentException("未知模型参数：" + id);
        SetEdit(new() { ["cmd"] = "Director.Parameter", ["TargetName"] = ActorId,
            ["Parameter"] = id, ["Operation"] = "release", ["Duration"] = 0 });
    }
    public void ResetParameters()
    {
        EnsureEditablePerformance();
        var pins = Parameters.Where(parameter => parameter.IsOverridden).Select(parameter => parameter.Id).ToArray();
        int at = FrameAt(Time);
        Change(next =>
        {
            foreach (string id in pins)
            {
                RemoveEdits(next, row => Text(row, "cmd") == "Director.Parameter" && Text(row, "Parameter") == id && Frame(row) == at);
                next["edits"]!.AsArray().Add((JsonNode)new JsonObject { ["cmd"] = "Director.Parameter", ["TargetName"] = ActorId,
                    ["Parameter"] = id, ["Operation"] = "release", ["Duration"] = 0, ["AtFrame"] = at });
            }
        });
    }
    /// <summary>Immediate native edits authored at this playhead. Safe to apply to the
    /// current renderer before committing ToSceneDocument, without replaying the model.</summary>
    public IReadOnlyList<JsonObject> CurrentEditRows() => OwnRows().Where(row => Frame(row) == FrameAt(Time)
        && Text(row, "cmd") is "Director.Parameter" or "Director.Actor")
        .Select(row => (JsonObject)row.DeepClone()).ToArray();
    public void SetTransform(Live2DTransform value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var json = TransformJson(value); _ = ActorIsolation.ReadTransform(json, ActorId);
        Change(next => next["transform"] = json.DeepClone());
    }
    public void SetControllers(bool? breath = null, bool? blink = null, bool? physics = null, bool? loop = null)
    {
        var values = new JsonObject();
        if (breath.HasValue) values["breath"] = breath.Value;
        if (blink.HasValue) values["blink"] = blink.Value;
        if (physics.HasValue) values["physics"] = physics.Value;
        if (loop.HasValue) values["loop"] = loop.Value;
        if (values.Count == 0) return;
        var current = OwnRows().LastOrDefault(row => Frame(row) == FrameAt(Time) && Text(row, "cmd") == "Director.Actor");
        if (current?["Values"]?["controls"] is JsonObject previous)
            foreach (var (key, value) in previous) if (!values.ContainsKey(key)) values[key] = value?.DeepClone();
        SetEdit(new() { ["cmd"] = "Director.Actor", ["TargetName"] = ActorId,
            ["Values"] = new JsonObject { ["controls"] = values }, ["Duration"] = 0 });
    }
    public void SetDuration(double duration)
    {
        if (!double.IsFinite(duration) || duration < 0 || duration > 36000 - 1d / 30)
            throw new ArgumentOutOfRangeException(nameof(duration));
        double snapped = Math.Floor(duration * 30 + 1e-8) / 30;
        Change(next =>
        {
            next["duration"] = snapped; next["time"] = Math.Min(Number(next, "time"), snapped);
            if (next["range"] is JsonObject range)
            {
                var clamped = new TimelineRange(range["startFrame"]!.GetValue<int>(), range["endFrameExclusive"]!.GetValue<int>(), 30)
                    .ClampTo(TimelineRange.Full(snapped, 30));
                range["startFrame"] = clamped.StartFrame; range["endFrameExclusive"] = clamped.EndFrameExclusive;
            }
        });
    }
    public void SetOutput(int width, int height)
    {
        RenderDimensions.Validate(width, height, 1);
        Change(next => next["output"] = new JsonObject { ["width"] = width, ["height"] = height });
    }
    public void SetParameterMetadata(JsonNode description)
    {
        ArgumentNullException.ThrowIfNull(description);
        if (description["parameters"] is not JsonArray parameters) throw new ArgumentException("原生模型信息缺少参数定义。");
        var metadata = (JsonObject)root["metadata"]!.DeepClone();
        metadata["parameters"] = parameters.DeepClone();
        if (description["physics"] is JsonValue physical && physical.TryGetValue<bool>(out _)) metadata["physics"] = physical.DeepClone();
        _ = ReadDefinitions(metadata).ToArray();
        root["metadata"] = metadata;
    }
    /// <summary>Observation only: this never creates keyframes, pins, undo entries or dirty state.</summary>
    public void UpdateParameterValues(IReadOnlyList<PreviewParameter> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        foreach (var parameter in parameters)
        {
            if (!double.IsFinite(parameter.Value)) continue;
            observed[parameter.Id] = parameter;
        }
    }
    public void Undo() { if (undo.TryPop(out var previous)) { redo.Push(root); Restore(previous); } }
    public void Redo() { if (redo.TryPop(out var next)) { undo.Push(root); Restore(next); } }

    void Restore(JsonObject value)
    {
        double time = Time; var metadata = root["metadata"]!.DeepClone();
        root = value; root["metadata"] = metadata; root["time"] = Snap(time); observed.Clear();
    }
    void Change(Action<JsonObject> update)
    {
        var next = (JsonObject)root.DeepClone(); update(next);
        if (JsonNode.DeepEquals(next, root)) return;
        undo.Push(root); redo.Clear(); root = next; observed.Clear();
    }
    string Content()
    {
        var content = (JsonObject)root.DeepClone(); content.Remove("time"); content.Remove("metadata");
        return content.ToJsonString();
    }
    static JsonObject TransformJson(Live2DTransform value) => new()
    { ["x"] = value.X, ["y"] = value.Y, ["scale"] = value.Scale, ["rotation"] = value.Rotation, ["opacity"] = value.Opacity };
    static string Text(JsonNode? value, string key, string fallback = "") => value?[key]?.GetValue<string>() ?? fallback;
    static double Number(JsonNode? value, string key, double fallback = 0)
    {
        if (value?[key] is null) return fallback;
        var node = value[key]!;
        if (node is JsonValue number && (number.TryGetValue<double>(out double result)
            || node.GetValueKind() == JsonValueKind.Number && double.TryParse(node.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result)))
            return result;
        throw new ArgumentException("Live2D 数值字段无效：" + key);
    }
    static int FrameAt(double time) => checked((int)Math.Floor(time * 30 + 1e-8));
    static int Frame(JsonObject row) => row["AtFrame"]?.GetValue<int>() ?? 0;
    double Snap(double time) => Math.Clamp(Math.Round(time * 30) / 30, 0, Duration);
    IEnumerable<JsonObject> OwnRows() => root["edits"]!.AsArray().OfType<JsonObject>();
    IEnumerable<JsonObject> ActiveRows() => PerformanceRows().Concat(OwnRows()).Where(row => Frame(row) <= FrameAt(Time)
        && Text(row, "TargetName") == ActorId).OrderBy(Frame);
    IEnumerable<JsonObject> PerformanceRows() => root["performance"] is JsonObject performance
        ? new[] { "initialize", "commands", "finalize" }.SelectMany(section => performance[section]?.AsArray().OfType<JsonObject>() ?? []) : [];
    bool Controller(string key)
    {
        bool value = true, director = !IsCapturedPerformance;
        bool? nativeLoop = null;
        foreach (var row in ActiveRows().Where(row => Text(row, "cmd") == "Director.Actor"))
        {
            if (row["Values"]?["animation"] is JsonValue mode) director = mode.ToString() == "director";
            if ((row["Values"]?["controls"]?[key] ?? row["Values"]?[key]) is JsonValue flag && flag.TryGetValue<bool>(out bool parsed)) value = parsed;
            if (row["Values"]?["controls"]?["loop"] is JsonValue loop && loop.TryGetValue<bool>(out bool overridden)) nativeLoop = overridden;
        }
        if (key == "loop" && !director) return nativeLoop ?? Motion == root["metadata"]?["defaultMotion"]?.ToString();
        return value;
    }
    string? CurrentAnimation(string command, string field, string defaultField)
    {
        string? result = root["metadata"]?[defaultField]?.GetValue<string>();
        bool ownsChoice = OwnRows().Any(row => Text(row, "cmd") == command
            || command == "Expression" && Text(row, "cmd") == "Motion");
        var rows = ownsChoice ? OwnRows().Where(row => Frame(row) <= FrameAt(Time)).OrderBy(Frame) : ActiveRows();
        foreach (var row in rows)
        {
            string kind = Text(row, "cmd");
            if ((kind == command || kind == "In" || command == "Expression" && kind == "Motion")
                && row[field]?.GetValue<string>() is { Length: > 0 } value) result = value;
        }
        return result;
    }
    void SetAnimation(string command, string field, string choices, string? value)
    {
        EnsureEditablePerformance();
        if (command is not ("Motion" or "Expression")) throw new ArgumentOutOfRangeException(nameof(command));
        if (!string.IsNullOrWhiteSpace(value)
            && root["metadata"]?[choices] is JsonArray available
            && !available.Any(item => (item is JsonObject ? Text(item, "id") : item?.GetValue<string>()) == value))
            throw new ArgumentException("模型不包含所选" + (command == "Motion" ? "动作：" : "表情：") + value);

        // This workbench deliberately edits one motion and one expression, not a
        // second timeline. A selection replaces the previous choice at frame zero;
        // complex, time-varying performances belong in the Scene workbench.
        string? other = command == "Motion" ? Expression : Motion;
        var row = string.IsNullOrWhiteSpace(value) ? null
            : new JsonObject { ["cmd"] = command, ["TargetName"] = ActorId, [field] = value,
                ["MotionFadeIn"] = 0, ["AtFrame"] = 0 };
        if (row is not null && command == "Motion" && other is { Length: > 0 })
            row["ExpressionName"] = other;
        Change(next =>
        {
            RemoveEdits(next, previous => Text(previous, "cmd") == command);
            next["time"] = 0;
            if (row is not null) next["edits"]!.AsArray().Add(row.DeepClone());
        });
    }
    void SetEdit(JsonObject row)
    {
        EnsureEditablePerformance(); row["AtFrame"] = FrameAt(Time);
        Change(next =>
        {
            RemoveEdits(next, previous => Frame(previous) == Frame(row) && Text(previous, "cmd") == Text(row, "cmd")
                && Text(previous, "Parameter") == Text(row, "Parameter"));
            next["edits"]!.AsArray().Add(row.DeepClone());
        });
    }
    static void RemoveEdits(JsonObject value, Func<JsonObject, bool> predicate)
    {
        var edits = value["edits"]!.AsArray();
        for (int i = edits.Count - 1; i >= 0; i--) if (edits[i] is JsonObject row && predicate(row)) edits.RemoveAt(i);
    }
    IEnumerable<Live2DParameter> Definitions() => ReadDefinitions(root["metadata"]!);
    static IEnumerable<Live2DParameter> ReadDefinitions(JsonNode metadata)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in metadata["parameters"]?.AsArray() ?? [])
        {
            if (entry is not JsonObject parameter) throw new ArgumentException("模型参数定义必须是对象。");
            string id = Text(parameter, "id");
            double minimum = Number(parameter, "min", double.NaN), maximum = Number(parameter, "max", double.NaN), initial = Number(parameter, "default", double.NaN);
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id) || !double.IsFinite(minimum) || !double.IsFinite(maximum)
                || !double.IsFinite(initial) || minimum > maximum || initial < minimum || initial > maximum)
                throw new ArgumentException("模型参数的真实范围或默认值无效：" + id);
            yield return new(id, Text(parameter, "name"), minimum, maximum, initial, initial, false);
        }
    }
}
