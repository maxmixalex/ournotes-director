using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;

namespace OurNotes.Director.Core;

public sealed record HomeProjectSource(long SpotId, string Site = "intl", long? AdvId = null);
public sealed record HomeProjectOutput(int Width = 1920, int Height = 1080, int Fps = 30, int Supersample = 2,
    string Codec = "cineform", string CineformQuality = VideoEncoding.DefaultCineFormQuality);
/// <summary>Offsets are fractions of the output canvas; positive Y is down. Scale uses a stable source pivot.</summary>
public sealed record HomeTransform(double X = 0, double Y = 0, double Scale = 1, bool Visible = true);
public sealed record HomeTransformKey(double Time, HomeTransform Value);
public sealed record HomeAnimationCue(double Time, string? Name = null, bool? Loop = null, double Speed = 1, double SourceTime = 0);
public sealed record HomeGrade(double Exposure = 0, double Contrast = 1, double Saturation = 1, double Vignette = 0)
{
    public GradeSpec ToGradeSpec() => new() { Exposure = (float)Exposure, Contrast = (float)Contrast, Saturation = (float)Saturation, Vignette = (float)Vignette };
}
public sealed record HomeGradeKey(double Time, HomeGrade Value);
public sealed record HomeCharacterTrack
{
    public HomeTransform Transform { get; init; } = new();
    public IReadOnlyList<HomeTransformKey> Keys { get; init; } = [];
    public IReadOnlyList<HomeAnimationCue> Animations { get; init; } = [];

    public HomeTransform At(double time)
    {
        HomeTransform before = Transform; double start = 0;
        foreach (var key in Keys)
        {
            if (key.Time <= time) { before = key.Value; start = key.Time; continue; }
            double p = key.Time <= start ? 1 : Math.Clamp((time - start) / (key.Time - start), 0, 1);
            return new(Lerp(before.X, key.Value.X, p), Lerp(before.Y, key.Value.Y, p), Lerp(before.Scale, key.Value.Scale, p), before.Visible);
        }
        return before;
    }

    public HomeAnimationCue? AnimationAt(double time) => Animations.LastOrDefault(cue => cue.Time <= time + 1e-9);
    public HomeCharacterTrack WithTransformAt(double time, HomeTransform value, bool keyframe)
    {
        if (keyframe) return this with { Keys = Keys.Where(k => Math.Abs(k.Time - time) > 1e-9).Append(new(time, value)).OrderBy(k => k.Time).ToArray() };
        var current = At(time); double dx = value.X - current.X, dy = value.Y - current.Y, ratio = value.Scale / current.Scale;
        HomeTransform Shift(HomeTransform original) => new(original.X + dx, original.Y + dy, original.Scale * ratio,
            value.Visible == current.Visible ? original.Visible : value.Visible);
        return this with { Transform = Shift(Transform), Keys = Keys.Select(key => key with { Value = Shift(key.Value) }).ToArray() };
    }
    internal static double Lerp(double a, double b, double p) => a + (b - a) * p;
}

/// <summary>The same evaluated edits are used by native preview and offline export.</summary>
public sealed record HomeEditState
{
    public bool Background { get; init; } = true;
    public bool PostProcessing { get; init; } = true;
    public string Framing { get; init; } = "scene";
    public HomeGrade Grade { get; init; } = new();
    public IReadOnlyList<HomeGradeKey> GradeKeys { get; init; } = [];
    public IReadOnlyDictionary<string, HomeCharacterTrack> Characters { get; init; } = new Dictionary<string, HomeCharacterTrack>(StringComparer.Ordinal);

    public HomeGrade GradeAt(double time)
    {
        HomeGrade before = Grade; double start = 0;
        foreach (var key in GradeKeys)
        {
            if (key.Time <= time) { before = key.Value; start = key.Time; continue; }
            double p = key.Time <= start ? 1 : Math.Clamp((time - start) / (key.Time - start), 0, 1);
            return new(HomeCharacterTrack.Lerp(before.Exposure, key.Value.Exposure, p), HomeCharacterTrack.Lerp(before.Contrast, key.Value.Contrast, p),
                HomeCharacterTrack.Lerp(before.Saturation, key.Value.Saturation, p), HomeCharacterTrack.Lerp(before.Vignette, key.Value.Vignette, p));
        }
        return before;
    }
}

/// <summary>Portable editable home composition. Archive locations stay in application settings.</summary>
public sealed record HomeProject(HomeProjectSource Source)
{
    public const string Format = "ournotes-home-project/1";
    public double Duration { get; init; } = 10;
    public HomeProjectOutput Output { get; init; } = new();
    public HomeEditState EditState { get; init; } = new();

    public static HomeProject Read(string path) => Parse(File.ReadAllText(path));
    public static HomeProject Parse(string text)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(text); }
        catch (JsonException exception) { throw new InvalidDataException("主页项目 JSON 无效：" + exception.Message, exception); }
        return FromJson(node as JsonObject ?? throw new InvalidDataException("主页项目必须是 JSON 对象。"));
    }

    public static HomeProject FromJson(JsonObject root)
    {
        Fields(root, "project", "format", "source", "duration", "output", "scene", "characters");
        if (Text(root, "format") != Format) throw new InvalidDataException("主页项目格式必须为 " + Format + "。");
        var source = Object(root, "source", required: true);
        Fields(source, "source", "spotId", "site", "advId");
        long spot = Integer(source, "spotId", 0); long? adv = source["advId"] is null ? null : Integer(source, "advId", 0);
        var output = Object(root, "output");
        Fields(output, "output", "width", "height", "fps", "supersample", "codec", "cineformQuality");
        var scene = Object(root, "scene");
        Fields(scene, "scene", "background", "postProcessing", "framing", "grade", "gradeKeys");
        var characters = new Dictionary<string, HomeCharacterTrack>(StringComparer.Ordinal);
        foreach (var entry in Object(root, "characters"))
        {
            var track = entry.Value as JsonObject ?? throw new InvalidDataException("角色轨道必须是对象：" + entry.Key);
            Fields(track, "characters." + entry.Key, "transform", "keys", "animations");
            var keys = Array(track, "keys").Select(n =>
            {
                var key = n as JsonObject ?? throw new InvalidDataException("角色关键帧必须是对象。");
                Fields(key, "transform key", "time", "x", "y", "scale", "visible");
                return new HomeTransformKey(Number(key, "time", 0), Transform(key, true));
            }).ToArray();
            var animations = Array(track, "animations").Select(n =>
            {
                var cue = n as JsonObject ?? throw new InvalidDataException("动作片段必须是对象。");
                Fields(cue, "animation cue", "time", "name", "loop", "speed", "sourceTime");
                string name = Text(cue, "name");
                return new HomeAnimationCue(Number(cue, "time", 0), name.Length == 0 ? null : name,
                    cue["loop"] is null ? null : Boolean(cue, "loop", false), Number(cue, "speed", 1), Number(cue, "sourceTime", 0));
            }).ToArray();
            characters.Add(entry.Key, new() { Transform = Transform(Object(track, "transform")), Keys = keys, Animations = animations });
        }
        var gradeKeys = Array(scene, "gradeKeys").Select(n =>
        {
            var key = n as JsonObject ?? throw new InvalidDataException("调色关键帧必须是对象。");
            Fields(key, "grade key", "time", "exposure", "contrast", "saturation", "vignette");
            return new HomeGradeKey(Number(key, "time", 0), Grade(key, true));
        }).ToArray();
        var project = new HomeProject(new HomeProjectSource(spot, Text(source, "site", "intl"), adv))
        {
            Duration = Number(root, "duration", 10),
            Output = new(Int(output, "width", 1920), Int(output, "height", 1080), Int(output, "fps", 30),
                Int(output, "supersample", 2), Text(output, "codec", "cineform"), Text(output, "cineformQuality", VideoEncoding.DefaultCineFormQuality)),
            EditState = new()
            {
                Background = Boolean(scene, "background", true), PostProcessing = Boolean(scene, "postProcessing", true), Framing = Text(scene, "framing", "scene"),
                Grade = Grade(Object(scene, "grade")), GradeKeys = gradeKeys, Characters = characters
            }
        };
        project.Validate(); return project;
    }

    public void Validate()
    {
        if (Source.SpotId <= 0 || Source.AdvId is <= 0 || Source.Site is not ("intl" or "jp")) throw new InvalidDataException("主页来源需要正整数 spotId、intl/jp 站点和可选的正整数 advId。");
        Range(Duration, .001, 36000, "duration");
        HomeRenderer.ValidateDimensions(Output.Width, Output.Height, Output.Supersample);
        if (Output.Fps is < 1 or > 120) throw new InvalidDataException("主页项目 fps 必须为 1..120。");
        _ = VideoEncoding.NormalizeCodec(Output.Codec); _ = VideoEncoding.NormalizeCineFormQuality(Output.CineformQuality);
        ValidateEdits(EditState, Duration);
    }

    public static void ValidateEdits(HomeEditState edits, double duration = 36000)
    {
        if (edits.Framing is not ("scene" or "fit")) throw new InvalidDataException("主页 framing 必须为 scene 或 fit。");
        CheckGrade(edits.Grade); CheckTimes(edits.GradeKeys.Select(k => k.Time), duration, "调色");
        foreach (var key in edits.GradeKeys) CheckGrade(key.Value);
        foreach (var entry in edits.Characters)
        {
            if (string.IsNullOrWhiteSpace(entry.Key)) throw new InvalidDataException("角色轨道名称不能为空。");
            CheckTransform(entry.Value.Transform); CheckTimes(entry.Value.Keys.Select(k => k.Time), duration, entry.Key + " 变换");
            foreach (var key in entry.Value.Keys) CheckTransform(key.Value);
            CheckTimes(entry.Value.Animations.Select(k => k.Time), duration, entry.Key + " 动作");
            foreach (var cue in entry.Value.Animations)
            {
                Range(cue.Speed, .05, 8, "动作速度"); Range(cue.SourceTime, 0, 36000, "动作起始时间");
                if (cue.Name is not null && string.IsNullOrWhiteSpace(cue.Name)) throw new InvalidDataException("动作名称为空时请使用 null。");
            }
        }
    }

    public JsonObject ToJson()
    {
        Validate();
        var characters = new JsonObject();
        foreach (var entry in EditState.Characters.OrderBy(p => p.Key, StringComparer.Ordinal))
            characters[entry.Key] = new JsonObject
            {
                ["transform"] = TransformJson(entry.Value.Transform),
                ["keys"] = new JsonArray(entry.Value.Keys.Select(k => { var node = TransformJson(k.Value); node["time"] = k.Time; return (JsonNode)node; }).ToArray()),
                ["animations"] = new JsonArray(entry.Value.Animations.Select(k => (JsonNode)new JsonObject
                    { ["time"] = k.Time, ["name"] = k.Name, ["loop"] = k.Loop, ["speed"] = k.Speed, ["sourceTime"] = k.SourceTime }).ToArray())
            };
        return new()
        {
            ["format"] = Format, ["source"] = new JsonObject { ["spotId"] = Source.SpotId, ["site"] = Source.Site, ["advId"] = Source.AdvId },
            ["duration"] = Duration,
            ["output"] = new JsonObject { ["width"] = Output.Width, ["height"] = Output.Height, ["fps"] = Output.Fps, ["supersample"] = Output.Supersample,
                ["codec"] = VideoEncoding.NormalizeCodec(Output.Codec), ["cineformQuality"] = VideoEncoding.NormalizeCineFormQuality(Output.CineformQuality) },
            ["scene"] = new JsonObject { ["background"] = EditState.Background, ["postProcessing"] = EditState.PostProcessing, ["framing"] = EditState.Framing,
                ["grade"] = GradeJson(EditState.Grade), ["gradeKeys"] = new JsonArray(EditState.GradeKeys.Select(k => { var node = GradeJson(k.Value); node["time"] = k.Time; return (JsonNode)node; }).ToArray()) },
            ["characters"] = characters
        };
    }

    public static JsonObject TransformJson(HomeTransform value) => new() { ["x"] = value.X, ["y"] = value.Y, ["scale"] = value.Scale, ["visible"] = value.Visible };
    public static JsonObject GradeJson(HomeGrade value) => new() { ["exposure"] = value.Exposure, ["contrast"] = value.Contrast, ["saturation"] = value.Saturation, ["vignette"] = value.Vignette };
    static void CheckTransform(HomeTransform value) { Range(value.X, -10, 10, "水平位移"); Range(value.Y, -10, 10, "垂直位移"); Range(value.Scale, .01, 20, "缩放"); }
    static void CheckGrade(HomeGrade value) { Range(value.Exposure, -10, 10, "曝光"); Range(value.Contrast, 0, 4, "对比度"); Range(value.Saturation, 0, 4, "饱和度"); Range(value.Vignette, 0, 1, "暗角"); }
    static void CheckTimes(IEnumerable<double> times, double duration, string label)
    {
        double previous = -1;
        foreach (double time in times)
        {
            Range(time, 0, duration, label + " 时间");
            if (time <= previous) throw new InvalidDataException(label + "关键帧必须按时间严格递增，且不能重叠。");
            previous = time;
        }
    }
    static void Range(double value, double minimum, double maximum, string label)
    { if (!double.IsFinite(value) || value < minimum || value > maximum) throw new InvalidDataException($"{label} 必须为 {minimum}..{maximum} 的有限数值。"); }
    static HomeTransform Transform(JsonObject node, bool key = false)
    { if (!key) Fields(node, "transform", "x", "y", "scale", "visible"); return new(Number(node, "x", 0), Number(node, "y", 0), Number(node, "scale", 1), Boolean(node, "visible", true)); }
    static HomeGrade Grade(JsonObject node, bool key = false)
    { if (!key) Fields(node, "grade", "exposure", "contrast", "saturation", "vignette"); return new(Number(node, "exposure", 0), Number(node, "contrast", 1), Number(node, "saturation", 1), Number(node, "vignette", 0)); }
    static JsonObject Object(JsonObject node, string key, bool required = false)
        => node[key] is JsonObject value ? value : node[key] is null && !required ? new() : throw new InvalidDataException(key + " 必须是对象。");
    static JsonArray Array(JsonObject node, string key) => node[key] is JsonArray value ? value : node[key] is null ? [] : throw new InvalidDataException(key + " 必须是数组。");
    static string Text(JsonObject node, string key, string fallback = "") => node[key] is null ? fallback
        : node[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : throw new InvalidDataException(key + " 必须是字符串。");
    static bool Boolean(JsonObject node, string key, bool fallback) => node[key] is null ? fallback
        : node[key] is JsonValue value && value.TryGetValue<bool>(out bool flag) ? flag : throw new InvalidDataException(key + " 必须是布尔值。");
    static double Number(JsonObject node, string key, double fallback)
    {
        if (node[key] is null) return fallback;
        if (node[key] is JsonValue value && !value.TryGetValue<string>(out _)
            && double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number)) return number;
        throw new InvalidDataException(key + " 必须是有限数值。");
    }
    static long Integer(JsonObject node, string key, long fallback)
    {
        if (node[key] is null) return fallback;
        if (node[key] is JsonValue value && long.TryParse(value.ToJsonString(), NumberStyles.None, CultureInfo.InvariantCulture, out long number) && number >= 0) return number;
        throw new InvalidDataException(key + " 必须是有效正整数。");
    }
    static int Int(JsonObject node, string key, int fallback)
    { long value = Integer(node, key, fallback); return value <= int.MaxValue ? (int)value : throw new InvalidDataException(key + " 超出整数范围。"); }
    static void Fields(JsonObject node, string label, params string[] supported)
    { foreach (string key in node.Select(p => p.Key)) if (!supported.Contains(key, StringComparer.Ordinal)) throw new InvalidDataException(label + " 不支持字段：" + key); }
}
