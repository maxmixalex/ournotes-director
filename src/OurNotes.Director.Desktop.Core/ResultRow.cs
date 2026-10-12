using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Desktop.Core;

public sealed record ResultSelection(string Value, IReadOnlyDictionary<string, string?> RelatedValues);
public sealed record ResultAction(string Label, string CommandId, IReadOnlyDictionary<string, string?> Values);

/// <summary>Command-aware rows for a CLI result; source JSON is copied, never rewritten.</summary>
public sealed record ResultRow(string Id, string Title, string Subtitle, JsonNode Data)
{
    public string Kind { get; init; } = "resource";
    public IReadOnlyDictionary<string, string?> Context { get; init; } = new Dictionary<string, string?>();
    public string SearchText => string.Join(" ", Title, Subtitle, Id,
        Data is JsonObject obj ? string.Join(" ", new[] { "titles", "names", "text", "name" }.SelectMany(key => TextValues(obj[key]))) : "");

    public static IReadOnlyList<ResultRow> Read(JsonNode? node, string command, IReadOnlyDictionary<string, string?>? context = null)
    {
        var inherited = Merge(context, null, command);
        node = Unwrap(node, inherited);
        inherited = Merge(inherited, node as JsonObject, command);
        if (node is null) return [];
        var rows = new List<ResultRow>();
        void Add(JsonArray? values, string kind)
        {
            if (values is null) return;
            foreach (var value in values.Where(value => value is not null)) rows.Add(Create(value!, kind, command, inherited));
        }

        if (node is JsonObject root)
        {
            if (command is "model.show" or "probe.model" && root["id"] is JsonValue modelId) inherited["modelId"] = modelId.ToString();
            if (command == "home.show") { Add(root["characters"] as JsonArray, "home.character"); return rows; }
            if (command == "home.animations") { Add(root["parts"] as JsonArray, "home.part"); return rows; }
            if (command is "model.show" or "probe.model" or "model.parameters" or "expression.show")
            {
                if (command is "model.show" or "probe.model") rows.Add(Create(root, "model", command, inherited));
                Add(root["parameters"] as JsonArray, "parameter");
                Add(root["motions"] as JsonArray, "motion");
                Add(root["expressions"] as JsonArray, "expression");
                return rows;
            }
            if (root["resource"] is JsonObject resource) { rows.Add(Create(resource, "resource", command, inherited)); return rows; }
            if (root["items"] is JsonArray items) { Add(items, KindFor(command)); return rows; }
            if (root["stories"] is JsonArray stories) { Add(stories, "story"); return rows; }
            if (root["exports"] is JsonArray exports) { Add(exports, "export"); return rows; }
            if (command is "story.show") { rows.Add(Create(root, "story", command, inherited)); return rows; }
            if (command == "story.dialogue-plan") { Add(root["lines"] as JsonArray, "dialogue"); return rows; }
            if (command == "story.native-program")
            {
                foreach (string key in new[] { "initialize", "rows", "commands", "finalize" }) Add(root[key] as JsonArray, "command");
                return rows;
            }
            foreach (string key in new[] { "files", "parameters", "resources", "models", "characters", "parts", "commands", "cells", "streams" })
                if (root[key] is JsonArray values) { Add(values, key == "parameters" ? "parameter" : "data"); return rows; }
            return [];
        }
        if (node is JsonArray array) Add(array, KindFor(command));
        return rows;
    }

    /// <summary>Flatten selectable voices and expose animation names valid on every selected skeleton.</summary>
    public static IReadOnlyList<ResultRow> ForPicker(JsonNode? node, string command, string fieldKey,
        IReadOnlyDictionary<string, string?>? context = null)
    {
        var inherited = Merge(context, null, command);
        node = Unwrap(node, inherited);
        inherited = Merge(inherited, node as JsonObject, command);
        if (command == "home.show" && fieldKey == "adv" && node is JsonObject home)
            return (home["sources"] as JsonArray ?? []).OfType<JsonObject>().Select(source => Create(source, "home.source", command, inherited)).ToArray();
        if (command == "home.animations" && fieldKey == "animation" && node is JsonObject description)
        {
            var parts = (description["parts"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
            if (parts.Length == 0) return [];
            var common = parts.Select(part => (part["animations"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(animation => Text(animation, "name")).Where(name => name.Length > 0).ToHashSet(StringComparer.Ordinal)).ToArray();
            var names = new HashSet<string>(common[0], StringComparer.Ordinal);
            foreach (var partNames in common.Skip(1)) names.IntersectWith(partNames);
            return names.Order(StringComparer.Ordinal).Select(name =>
            {
                var details = new JsonArray(parts.Select(part => (JsonNode?)new JsonObject
                {
                    ["path"] = part["path"]?.DeepClone(), ["skeleton"] = part["skeleton"]?.DeepClone(),
                    ["duration"] = (part["animations"] as JsonArray)?.OfType<JsonObject>().First(animation => Text(animation, "name") == name)["duration"]?.DeepClone()
                }).ToArray());
                return new ResultRow(name, name, "全部 " + parts.Length + " 个骨架均可使用", new JsonObject { ["name"] = name, ["parts"] = details })
                    { Kind = "home.animation", Context = ReadOnly(inherited) };
            }).ToArray();
        }
        var rows = Read(node, command, inherited);
        if (command is "voice.search" or "dialogue.search" && fieldKey is "voiceId" or "soundId")
        {
            var voices = new List<ResultRow>();
            foreach (var row in rows)
            {
                if (row.Data is not JsonObject line) continue;
                foreach (var voice in (line["voices"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    string id = Text(voice, "soundId");
                    if (id.Length == 0) continue;
                    var selected = Create(voice, "voice", command, Merge(row.Context, line, command));
                    voices.Add(selected with { Title = row.Title == "资源" ? id : row.Title, Subtitle = Join(id, selected.Subtitle, Text(voice, "cue")) });
                }
            }
            return voices.DistinctBy(row => (row.Id, row.Context.GetValueOrDefault("site"), row.Context.GetValueOrDefault("adv"), Text(row.Data as JsonObject, "asset"))).ToArray();
        }
        return rows;
    }

    public ResultSelection Selection(string fieldKey)
    {
        var obj = Data as JsonObject;
        var related = new Dictionary<string, string?>(StringComparer.Ordinal);
        void Include(string key, string? value) { if (value is { Length: > 0 }) related[key] = value; }
        foreach (string key in new[] { "site", "archive" }) Include(key, Context.GetValueOrDefault(key));
        string value = fieldKey switch
        {
            "logicalPath" => Text(obj, "logicalPath", fallback: Id),
            "character" => Text(obj, "key", fallback: Id),
            "animation" => Text(obj, "name", fallback: Id),
            "voiceId" or "soundId" => Text(obj, "soundId", fallback: Id),
            "adv" or "advId" => Text(obj, "advId", fallback: Id),
            "spotId" => Text(obj, "spotId", fallback: Id),
            _ => Id
        };
        if (fieldKey == "logicalPath")
        {
            bool model = Text(obj, "scope") == "model" || Text(obj, "scope").Length == 0 && Text(obj, "modelId").Length > 0;
            related["adv"] = model ? "" : Text(obj, "advId", fallback: Context.GetValueOrDefault("adv") ?? "");
            related["model"] = model ? Text(obj, "modelId", fallback: Context.GetValueOrDefault("modelId") ?? "") : "";
            // The selected common-language row must not inherit an unrelated language override.
            related["lang"] = Text(obj, "language");
        }
        else if (fieldKey is "voiceId" or "soundId") Include("adv", Context.GetValueOrDefault("adv"));
        else if (fieldKey == "expressionId") Include("modelId", Context.GetValueOrDefault("modelId"));
        if (Kind.StartsWith("home.", StringComparison.Ordinal) && fieldKey is "character" or "animation" or "adv")
        {
            Include("spotId", Context.GetValueOrDefault("spotId"));
            Include("adv", Context.GetValueOrDefault("adv"));
            if (fieldKey == "animation") Include("character", Context.GetValueOrDefault("character"));
        }
        if (fieldKey == "spotId") related["adv"] = value == "all" ? "" : Context.GetValueOrDefault("adv") ?? "";
        if (fieldKey == "adv" && Kind == "home.source") related.Remove("adv");
        return new(value, ReadOnly(related));
    }

    public IReadOnlyList<ResultAction> Actions(string command)
    {
        var result = new List<ResultAction>();
        var obj = Data as JsonObject;
        string ContextValue(string key) => Context.GetValueOrDefault(key) ?? "";
        void Add(string label, string target, params (string Key, string? Value)[] values)
        {
            var definition = CommandCatalog.Get(target);
            var allowed = definition.Fields.Select(field => field.Key).ToHashSet(StringComparer.Ordinal);
            var selection = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (string key in new[] { "archive", "site", "lang" })
                if (allowed.Contains(key) && Context.TryGetValue(key, out string? value)) selection[key] = value;
            foreach (var item in values) if (allowed.Contains(item.Key)) selection[item.Key] = item.Value;
            result.Add(new(label, target, ReadOnly(selection)));
        }
        string adv = Text(obj, "advId", fallback: ContextValue("advId"));
        if (adv.Length == 0) adv = ContextValue("adv");
        string model = ContextValue("modelId");
        if (Kind.StartsWith("home.", StringComparison.Ordinal))
        {
            string spot = Text(obj, "spotId", fallback: ContextValue("spotId"));
            if (spot.Length == 0) return result;
            string character = Kind == "home.character" ? Text(obj, "key", fallback: Id) : ContextValue("character");
            string animation = Kind == "home.animation" ? Id : ContextValue("animation");
            (string Key, string? Value)[] scene = [("spotId", spot), ("adv", adv)];
            (string Key, string? Value)[] selected = [.. scene, ("character", character), ("animation", animation)];
            Add("查看主页与角色", "home.show", scene);
            if (Text(obj, "exportSupported") != "false")
            {
                Add("预览动画图集", "home.contact", selected);
                Add(character.Length == 0 ? "导出场景与所有角色" : "导出此角色", "home.export", selected);
                Add("查看可用动画", "home.animations", selected);
            }
            else Add("导出完整场景", "home.clip", [.. scene, ("character", ""), ("animation", "")]);
        }
        else if (Kind is "story" or "command" or "dialogue" or "voice.group" or "voice")
        {
            if (adv.Length > 0)
            {
                Add("查看剧情详情", "story.show", ("advId", adv));
                Add("预览剧情图集", "story.contact", ("advId", adv));
                Add("导出剧情视频", "story.clip", ("advId", adv));
                Add("导出配音与字幕", "story.export-dialogue", ("advId", adv));
            }
            if (Kind == "voice" && Id.Length > 0) Add("提取语音 " + Id, "voice.extract", ("voiceId", Id), ("adv", adv));
            else if (obj?["voices"] is JsonArray voices)
                foreach (string voice in voices.OfType<JsonObject>().Select(voice => Text(voice, "soundId")).Where(id => id.Length > 0).Distinct(StringComparer.Ordinal))
                    Add("提取语音 " + voice, "voice.extract", ("voiceId", voice), ("adv", adv));
        }
        else if (Kind == "model")
        {
            model = Id;
            Add("查看模型详情", "model.show", ("modelId", model));
            Add("模型参数", "model.parameters", ("modelId", model));
            Add("动作列表", "motion.list", ("modelId", model));
            Add("表情列表", "expression.list", ("modelId", model));
            Add("生成预览图集", "model.gallery", ("modelId", model));
        }
        else if (Kind == "expression" && model.Length > 0)
        {
            Add("查看表情参数", "expression.show", ("modelId", model), ("expressionId", Id));
            Add("生成表情图集", "model.gallery", ("modelId", model), ("kind", "expression"));
        }
        else if (Kind == "motion" && model.Length > 0)
        {
            Add("生成动作图集", "model.gallery", ("modelId", model), ("kind", "motion"));
            Add("查看模型详情", "model.show", ("modelId", model));
        }
        else if (Kind == "asset")
        {
            var selection = Selection("logicalPath");
            var values = selection.RelatedValues.Select(pair => (pair.Key, pair.Value)).Append(("logicalPath", (string?)selection.Value)).ToArray();
            Add("提取此素材", "asset.extract", values); Add("校验此素材", "asset.verify", values);
            if (Path.GetExtension(selection.Value).Equals(".json", StringComparison.OrdinalIgnoreCase)) Add("查看素材数据", "asset.show", values);
        }
        else if (Kind == "resource")
        {
            string target = command.Split('.')[0] + ".show";
            if (CommandCatalog.All.Any(definition => definition.Id == target && definition.Fields.Any(field => field.Key == "resourceId")))
                Add("查看资源详情", target, ("resourceId", Id));
        }
        return result;
    }

    private static ResultRow Create(JsonNode node, string kind, string command, IReadOnlyDictionary<string, string?> inherited)
    {
        var obj = node as JsonObject;
        var context = Merge(inherited, obj, command);
        if (kind == "model" && obj?["id"] is JsonValue modelId) context["modelId"] = modelId.ToString();
        if (node is JsonValue scalar)
        {
            string scalarValue = scalar.ToString();
            return new(scalarValue, scalarValue, kind == "expression" ? "表情" : kind == "motion" ? "动作" : "", node.DeepClone()) { Kind = kind, Context = ReadOnly(context) };
        }
        string id = kind switch
        {
            "home.spot" => Text(obj, "spotId"), "home.source" or "story" => Text(obj, "advId", fallback: context.GetValueOrDefault("advId") ?? ""),
            "home.character" => Text(obj, "key"), "home.part" => Text(obj, "path", "skeleton"),
            "asset" => Text(obj, "logicalPath"), "voice" => Text(obj, "soundId"),
            "voice.group" or "dialogue" => Join(Text(obj, "advId", fallback: context.GetValueOrDefault("advId") ?? ""), Text(obj, "commandIndex", "textId")),
            "model" => Text(obj, "id", "modelId", fallback: context.GetValueOrDefault("modelId") ?? ""),
            "command" => Text(obj, "i", "commandIndex"), "export" => Text(obj, "output"),
            _ => Text(obj, "id", "name", "Name", "key", "output", "index")
        };
        string preferred = context.GetValueOrDefault("lang") ?? "zh-Hans";
        string title = kind is "dialogue" or "voice.group" ? Localized(obj?["text"], preferred) : "";
        if (title.Length == 0) title = Localized(obj?["titles"], preferred);
        if (title.Length == 0) title = Localized(obj?["names"], preferred);
        if (title.Length == 0) title = Localized(obj?["title"], preferred);
        if (title.Length == 0) title = Text(obj, "displayName", "display", "name", "Name", "key", "logicalPath");
        if (title.Length == 0 && kind == "home.part") title = Text(obj, "skeleton", "path");
        if (title.Length == 0 && kind == "command") title = Text(obj, "cmd", "name");
        if (title.Length == 0) title = id.Length > 0 ? id : "资源";
        if (kind == "home.source") title = "来源剧情 " + id;
        var details = new List<string>();
        if (id.Length > 0 && id != title) details.Add(id);
        string site = context.GetValueOrDefault("site") ?? "";
        if (site.Length > 0) details.Add(site == "jp" ? "日服" : site == "intl" ? "国际服" : site);
        if (kind is "expression" or "motion" or "parameter") details.Add(kind == "expression" ? "表情" : kind == "motion" ? "动作" : "参数");
        string speaker = Text(obj, "speakerTarget", "speaker"); if (speaker.Length > 0) details.Add(speaker);
        if (obj?["characters"] is JsonArray characters) details.Add(characters.Count + " 名角色");
        if (obj?["voices"] is JsonArray voices) details.Add(voices.Count + " 段语音");
        string duration = Text(obj, "sourceDuration", "duration"); if (duration.Length > 0) details.Add(duration + " 秒");
        if (kind == "home.part")
        {
            string animation = Text(obj, "animation"); if (animation.Length > 0) details.Add("当前动画：" + animation);
            if (obj?["animations"] is JsonArray animations) details.Add(animations.Count + " 个可用动画");
        }
        if (Text(obj, "exportSupported") == "false") details.Add("独立导出暂不支持");
        if (kind == "asset") details.Add(Text(obj, "scope") == "model" ? "模型来源：" + Text(obj, "modelId") : "剧情来源：" + Text(obj, "advId"));
        return new(id, title, Join(details.ToArray()), node.DeepClone()) { Kind = kind, Context = ReadOnly(context) };
    }

    private static string KindFor(string command) => command switch
    {
        "home.list" => "home.spot", "home.show" => "home.character", "home.animations" => "home.part",
        "story.list" or "story.search" or "story.show" or "story.audit" or "story.native-check" => "story",
        "story.commands" or "commands.list" or "story.native-program" => "command",
        "dialogue.search" or "story.dialogue-plan" => "dialogue", "voice.search" => "voice.group",
        "model.list" or "model.show" or "probe.model" => "model", "motion.list" => "motion", "expression.list" => "expression",
        "asset.search" => "asset", _ => "resource"
    };

    private static Dictionary<string, string?> Merge(IReadOnlyDictionary<string, string?>? inherited, JsonObject? data, string command)
    {
        var context = inherited is null ? new Dictionary<string, string?>(StringComparer.Ordinal) : new(inherited, StringComparer.Ordinal);
        if (data is null) return context;
        void Copy(string source, string destination)
        { if (data[source] is JsonValue value && value.ToString().Length > 0) context[destination] = value.ToString(); }
        Copy("site", "site"); Copy("spotId", "spotId"); Copy("advId", "advId"); Copy("advId", "adv");
        Copy("modelId", "modelId"); Copy("model", "modelId"); Copy("language", "lang");
        if (command.StartsWith("home.", StringComparison.Ordinal)) Copy("character", "character");
        return context;
    }

    private static JsonNode? Unwrap(JsonNode? node, Dictionary<string, string?> context)
    {
        if (node is not JsonObject envelope || !envelope.ContainsKey("result")
            || !envelope.ContainsKey("schema") && !envelope.ContainsKey("ok") && !envelope.ContainsKey("provenance")) return node;
        if (envelope["provenance"] is JsonObject provenance)
            foreach (string key in new[] { "archive", "site" }) if (provenance[key] is JsonValue value) context[key] = value.ToString();
        return envelope["result"];
    }
    private static IReadOnlyDictionary<string, string?> ReadOnly(Dictionary<string, string?> values) => new ReadOnlyDictionary<string, string?>(new Dictionary<string, string?>(values, StringComparer.Ordinal));
    private static string Text(JsonObject? obj, string first, string? second = null, string? third = null,
        string? fourth = null, string? fifth = null, string? sixth = null, string? fallback = null)
        => new[] { first, second, third, fourth, fifth, sixth }.Where(key => key is not null).Select(key => obj?[key!])
            .OfType<JsonValue>().Select(value => value.ToString()).FirstOrDefault(value => value.Length > 0) ?? fallback ?? "";
    private static string Localized(JsonNode? node, string preferred)
    {
        if (node is JsonValue scalar) return scalar.ToString();
        if (node is not JsonObject map) return "";
        return new[] { preferred, "zh-Hans", "ja", "zh-Hant", "en", "ko" }.Select(key => map[key]).OfType<JsonValue>()
            .Select(value => value.ToString()).FirstOrDefault(text => text.Length > 0) ?? map.Select(pair => pair.Value).OfType<JsonValue>().FirstOrDefault()?.ToString() ?? "";
    }
    private static IEnumerable<string> TextValues(JsonNode? node) => node is JsonObject obj ? obj.Select(pair => pair.Value).OfType<JsonValue>().Select(value => value.ToString())
        : node is JsonValue value ? [value.ToString()] : [];
    private static string Join(params string[] values) => string.Join(" · ", values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal));
}
