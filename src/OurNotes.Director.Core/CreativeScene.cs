using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

/// <summary>The creative document owns actors and time-positioned clips. Native command order
/// is a compiled output, not the editor's source of truth. Imported performances retain every
/// observed presentation row and a copy of their original script for advanced editing.</summary>
public sealed class CreativeScene
{
    public const string Format = "ournotes-director/scene";
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public JsonObject Root { get; private set; }
    public JsonArray Actors => Root["actors"]!.AsArray();
    public JsonArray Clips => Root["clips"]!.AsArray();
    public JsonObject Source => Root["source"]!.AsObject();
    public JsonObject Canvas => Root["canvas"]!.AsObject();
    public string Site => Text(Source, "site", "intl");
    public string Language => Text(Source, "language", "zh-Hans");
    public double Duration => Math.Max(Number(Root, "minimumDuration", 0), Clips.OfType<JsonObject>()
        .Where(c => c["enabled"]?.GetValue<bool>() != false).Select(c => Number(c, "start") + Number(c, "duration", 1d / 30)).DefaultIfEmpty(3).Max());
    public CreativeScene(JsonObject root) { Root = (JsonObject)root.DeepClone(); Validate(); }
    public static CreativeScene Blank(string site, string language) => new(new JsonObject
    {
        ["format"] = Format, ["version"] = 1,
        ["source"] = new JsonObject { ["site"] = site, ["language"] = language },
        ["canvas"] = new JsonObject { ["width"] = 1920, ["height"] = 1080, ["transparent"] = true },
        ["actors"] = new JsonArray(), ["clips"] = new JsonArray()
    });
    public static CreativeScene Parse(string text)
    {
        text = text.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            int start = text.IndexOf('\n'), end = text.LastIndexOf("```", StringComparison.Ordinal);
            if (start < 0 || end <= start) throw new ArgumentException("JSON 代码块不完整。");
            text = text[(start + 1)..end];
        }
        return new(JsonNode.Parse(text)?.AsObject() ?? throw new ArgumentException("场景必须是 JSON 对象。"));
    }
    public string ToJson() => Root.ToJsonString(Json);
    public static string Id() => Guid.NewGuid().ToString("N");
    public static string Text(JsonObject value, string key, string fallback = "") => value[key]?.ToString() ?? fallback;
    public static double Number(JsonObject value, string key, double fallback = 0)
        => value[key] is null ? fallback : double.TryParse(value[key]!.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number)
            ? number : throw new ArgumentException(key + " 必须是有限数值。");
    public JsonObject Actor(string id) => Actors.OfType<JsonObject>().FirstOrDefault(a => Text(a, "id") == id) ?? throw new ArgumentException("角色不存在。");
    public JsonObject Clip(string id) => Clips.OfType<JsonObject>().FirstOrDefault(c => Text(c, "id") == id) ?? throw new ArgumentException("片段不存在。");
    public string AddActor(string model, string name)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("请选择角色模型。");
        string id = "actor_" + Id()[..8];
        Actors.Add(new JsonObject { ["id"] = id, ["model"] = model, ["name"] = name,
            ["x"] = .5, ["y"] = .5, ["height"] = .95, ["opacity"] = 1 });
        return id;
    }
    public string AddClip(string actor, string kind, double start, double duration, JsonObject data, string label)
    {
        string id = Id();
        Clips.Add(new JsonObject { ["id"] = id, ["actor"] = actor, ["kind"] = kind,
            ["start"] = Math.Round(start * 30) / 30, ["duration"] = Math.Max(1d / 30, duration),
            ["label"] = label, ["data"] = data.DeepClone() });
        Validate(); return id;
    }
    public void MoveClip(string id, double seconds) { Clip(id)["start"] = Math.Max(0, Math.Round(seconds * 30) / 30); Validate(); }
    public void ResizeClip(string id, double seconds)
    {
        var clip = Clip(id); var data = clip["data"]!.AsObject();
        seconds = Math.Max(1d / 30, Math.Round(seconds * 30) / 30);
        if (Text(clip, "kind") == "voice" && data["AudioPath"] is not null) data["AudioDuration"] = seconds;
        else if (Text(data, "cmd").StartsWith("Director.", StringComparison.Ordinal)) data["Duration"] = seconds;
        else throw new InvalidOperationException("此源片段的时长由素材或原剧情决定；可以移动或替换素材，不能只改显示长度。");
        clip["duration"] = seconds; Validate();
    }
    public bool CanResize(JsonObject clip) => Text(clip["data"]!.AsObject(), "cmd").StartsWith("Director.", StringComparison.Ordinal) || clip["data"]?["AudioPath"] is not null;
    public void RemoveClip(string id) => Clips.Remove(Clip(id));

    public void Validate()
    {
        if (Text(Root, "format") != Format || Number(Root, "version") != 1) throw new ArgumentException("需要 ournotes-director/scene version 1。");
        if (Root["source"] is not JsonObject || Root["canvas"] is not JsonObject || Root["actors"] is not JsonArray || Root["clips"] is not JsonArray)
            throw new ArgumentException("场景需要 source、canvas、actors 和 clips。");
        int width = checked((int)Number(Canvas, "width", 1920)), height = checked((int)Number(Canvas, "height", 1080));
        RenderDimensions.Validate(width, height, 1);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var actor in Actors)
        {
            if (actor is not JsonObject a || string.IsNullOrWhiteSpace(Text(a, "id")) || !ids.Add(Text(a, "id")) || string.IsNullOrWhiteSpace(Text(a, "model")))
                throw new ArgumentException("角色必须有唯一 id 和实际 model。");
            foreach (string key in new[] { "x", "y", "height", "opacity" }) _ = Number(a, key);
            if (Number(a, "height", .95) <= 0 || Number(a, "opacity", 1) is < 0 or > 1) throw new ArgumentException("角色尺寸或透明度无效。");
        }
        ids.Clear();
        foreach (var clip in Clips)
        {
            if (clip is not JsonObject c || string.IsNullOrWhiteSpace(Text(c, "id")) || !ids.Add(Text(c, "id")) || c["data"] is not JsonObject data)
                throw new ArgumentException("每个片段必须有唯一 id 和 data。");
            double start = Number(c, "start"), duration = Number(c, "duration", 1d / 30);
            if (start < 0 || duration <= 0 || start + duration > 36000) throw new ArgumentException("片段时间必须在十小时范围内。");
            string command = Text(data, "cmd");
            if (!AdvProgram.KnownCommand(command) || command is "GoTo" or "Wait" or "ForceAuto") throw new ArgumentException("时间片段不能包含交互跳转或等待点击；请用高级原脚本编辑。");
            DirectorValidation.Check(AdvProgram.Row(data, "clip " + Text(c, "id")));
        }
        if (Number(Root, "minimumDuration") is < 0 or > 36000 || Duration > 36000) throw new ArgumentException("场景时长无效。");
    }

    public JsonObject Compile()
    {
        Validate();
        var output = Root["nativeOutput"]?.DeepClone() as JsonObject ?? new JsonObject
        { ["clean"] = true, ["letterbox"] = "none", ["codec"] = "cineform", ["cineformQuality"] = "film1", ["supersample"] = 1 };
        output["width"] = (int)Number(Canvas, "width", 1920); output["height"] = (int)Number(Canvas, "height", 1080);
        var rows = new List<(int At, int Order, JsonObject Row)>(); int order = 0;
        void Add(JsonObject row, int at) { row["AtFrame"] = at; rows.Add((at, order++, row)); }
        if (Root["imported"]?.GetValue<bool>() != true)
        {
            Add(new() { ["cmd"] = "Director.Scene", ["Values"] = new JsonObject { ["stageVisible"] = false,
                ["background"] = Canvas["transparent"]?.GetValue<bool>() != false ? new JsonArray(0, 0, 0, 0) : new JsonArray(0, 0, 0, 1) } }, 0);
            Add(new() { ["cmd"] = "Director.Camera", ["Values"] = new JsonObject { ["framing"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["zoom"] = 1, ["roll"] = 0 } } }, 0);
        }
        foreach (var a in Actors.OfType<JsonObject>().Where(a => a["fromSource"]?.GetValue<bool>() != true))
        {
            string id = Text(a, "id");
            Add(new() { ["cmd"] = "Character", ["TargetName"] = id, ["Model"] = Text(a, "model") }, 0);
            Add(new() { ["cmd"] = "Director.Actor", ["TargetName"] = id, ["Space"] = "screen",
                ["Values"] = new JsonObject { ["position"] = new JsonArray(Number(a, "x", .5), Number(a, "y", .5)), ["height"] = Number(a, "height", .95),
                    ["opacity"] = Number(a, "opacity", 1), ["visible"] = true, ["animation"] = "director", ["physics"] = true, ["blink"] = true, ["breath"] = true, ["loop"] = true } }, 0);
        }
        foreach (var c in Clips.OfType<JsonObject>())
        {
            if (c["enabled"]?.GetValue<bool>() == false) continue;
            var row = (JsonObject)c["data"]!.DeepClone();
            if (Text(c, "actor").Length > 0) row["TargetName"] = Text(c, "actor");
            Add(row, checked((int)Math.Round(Number(c, "start") * 30)));
        }
        // A deliberate terminal boundary, rather than a fixed 5-second editor limit.
        Add(new() { ["cmd"] = "Delay", ["Frames"] = 0 }, Math.Max(1, checked((int)Math.Ceiling(Duration * 30))));
        return new JsonObject { ["format"] = DirectorProject.Format, ["version"] = 1,
            ["source"] = Source.DeepClone(), ["output"] = output, ["initialize"] = new JsonArray(), ["finalize"] = new JsonArray(),
            ["commands"] = new JsonArray(rows.OrderBy(r => r.At).ThenBy(r => r.Order).Select(r => (JsonNode)r.Row).ToArray()),
            ["text"] = Root["text"]?.DeepClone() ?? new JsonObject() };
    }

    public static JsonObject Materialize(Archive archive, JsonObject project)
    {
        var result = (JsonObject)project.DeepClone(); var source = result["source"]!;
        string site = source["site"]?.ToString() ?? "intl", adv = DirectorProject.SourceId(source);
        var manifest = archive.Manifest(site, "stories", adv);
        var definition = JsonNode.Parse(archive.Logical(site, manifest, "story.json"))!;
        var episode = JsonNode.Parse(archive.Logical(site, manifest, definition["episode"]!.ToString()))!;
        var scene = JsonNode.Parse(archive.Logical(site, manifest, definition["scene"]!.ToString()))!;
        var settings = scene["settings"]!["playerSettings"]!;
        result["initialize"] ??= settings["_initializeEpisodes"]?.DeepClone() ?? new JsonArray();
        result["commands"] ??= episode["commands"]!.DeepClone();
        result["finalize"] ??= settings["_finalizeEpisodes"]?.DeepClone() ?? new JsonArray();
        var text = episode["text"]?.DeepClone() as JsonObject ?? new JsonObject();
        if (result["text"] is JsonObject overrides) foreach (var (key, value) in overrides) text[key] = value?.DeepClone();
        result["text"] = text;
        return result;
    }

    public static CreativeScene FromPerformance(JsonObject native, NativePlaybackTiming timing)
    {
        if (!timing.IsComplete) throw new InvalidOperationException("原剧情还未解析完成，不能猜测语音片段时间。");
        var scene = Blank(native["source"]?["site"]?.ToString() ?? "intl", native["source"]?["language"]?.ToString() ?? "zh-Hans");
        scene.Root["source"] = native["source"]!.DeepClone(); scene.Root["nativeOutput"] = native["output"]?.DeepClone();
        scene.Root["imported"] = true; scene.Root["originalProject"] = native.DeepClone(); scene.Root["text"] = native["text"]?.DeepClone();
        scene.Root["minimumDuration"] = timing.EndFrame!.Value / 30d;
        scene.Canvas["width"] = native["output"]?["width"]?.DeepClone() ?? JsonValue.Create(1920);
        scene.Canvas["height"] = native["output"]?["height"]?.DeepClone() ?? JsonValue.Create(1080);
        foreach (var t in timing.Commands.OrderBy(t => t.Ordinal))
        {
            if (native[t.Section] is not JsonArray section || t.Index >= section.Count || section[t.Index] is not JsonObject data) continue;
            if (t.Command is "Delay" or "Wait" or "GoTo" or "ForceAuto" or "CancelDelay") continue;
            var row = (JsonObject)data.DeepClone(); row["cmd"] = t.Command;
            string actor = Text(row, "TargetName"), kind = Kind(t.Command);
            if (t.Command == "Character" && actor.Length > 0 && Text(row, "Model").Length > 0 && !scene.Actors.OfType<JsonObject>().Any(a => Text(a, "id") == actor))
                scene.Actors.Add(new JsonObject { ["id"] = actor, ["name"] = actor, ["model"] = Text(row, "Model"), ["fromSource"] = true });
            string label = t.Command is "Talk" or "Voice" ? DialogueLabel(native, row, scene.Language) : Text(row, "MotionName", Text(row, "ExpressionName", t.Command));
            scene.AddClip(actor, kind, t.StartFrame / 30d, Math.Max(1, (t.EndFrame ?? t.StartFrame + 30) - t.StartFrame) / 30d, row, label);
        }
        // Not every original Character row uses Model. Keep such actors addressable as clips,
        // but never manufacture a model asset ID in the character library.
        foreach (var actor in scene.Actors.OfType<JsonObject>().Where(a => string.IsNullOrEmpty(Text(a, "model"))).ToArray()) scene.Actors.Remove(actor);
        scene.Validate(); return scene;
    }
    static string DialogueLabel(JsonObject root, JsonObject row, string language)
    {
        string field = language switch { "ja" => "japanese", "en" => "english", "zh-Hant" => "traditionalChinese", _ => "simplifiedChinese" };
        return root["text"]?[Text(row, "AdvTextID")]?[field]?.ToString() ?? "语音";
    }
    public static string Kind(string command) => command switch
    { "Talk" or "Voice" => "voice", "Motion" or "MotionLoop" => "motion", "Expression" => "expression",
        "Director.Actor" or "In" or "Out" => "transform", "Director.Camera" or "Pan" or "Zoom" => "camera",
        "Director.Scene" or "PostEffect" => "grade", "Character" => "setup", _ => "source" };
}
