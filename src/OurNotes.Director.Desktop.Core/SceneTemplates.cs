using System.Text.Json.Nodes;
using OurNotes.Director.Core;

namespace OurNotes.Director.Desktop.Core;

public sealed record SceneTemplate(string Key, string Label, string Description);

/// <summary>Examples use only a selected source/model and generated colours; they do not invent archive paths.</summary>
public static class SceneTemplates
{
    public static IReadOnlyList<SceneTemplate> All { get; } = Array.AsReadOnly<SceneTemplate>([
        new("transparent-actor", "自由透明人物", "一个已选模型，透明背景与完整画布，3 秒；CineForm film1。动作和表情可从归档列表继续添加。"),
        new("duet-background", "双人纯色背景", "两个已选模型左右构图，使用内置纯色背景；不依赖额外图片或虚构舞台资源。"),
        new("story-patch", "原剧情补丁", "保留原剧情初始化、正文和收尾，只提供来源、输出与文本覆盖入口。省略指令段表示继承，[] 表示清空。"),
        new("legacy-shot", "旧 Shot 导入示例", "保留旧版 actors、camera、tracks、events 结构；原生 render 会经兼容适配器执行。编码通过导出选项设置。")
    ]);

    public static SceneTemplate Get(string key) => All.FirstOrDefault(template => template.Key == key)
        ?? throw new ArgumentException("未知场景模板：" + key, nameof(key));

    public static SceneDocument Create(string key, string adv, string? model = null, string? secondModel = null)
        => Create(key, new SceneSource(adv, "intl", "zh-Hans"), model, secondModel);

    public static SceneDocument Create(string key, SceneSource source, string? model = null, string? secondModel = null)
    {
        _ = Get(key);
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(source.Adv) || string.IsNullOrWhiteSpace(source.Site) || string.IsNullOrWhiteSpace(source.Language))
            throw new ArgumentException("模板需要已选来源的 adv、site 和 language。", nameof(source));
        if (key != "story-patch" && string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("请从归档模型列表选择人物模型。", nameof(model));
        if (key == "duet-background" && string.IsNullOrWhiteSpace(secondModel))
            throw new ArgumentException("双人模板需要第二个人物模型。", nameof(secondModel));

        JsonObject sourceJson = new() { ["adv"] = source.Adv, ["site"] = source.Site, ["language"] = source.Language };
        if (key == "legacy-shot")
        {
            var legacy = new JsonObject
            {
                ["version"] = 1, ["duration"] = 3, ["fps"] = 30, ["warmupFrames"] = 30,
                ["source"] = sourceJson,
                ["output"] = new JsonObject { ["width"] = 1920, ["height"] = 1080, ["renderScale"] = 2 },
                ["background"] = new JsonArray(0, 0, 0, 0),
                ["camera"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["zoom"] = 1, ["roll"] = 0 },
                ["actors"] = new JsonArray(new JsonObject { ["name"] = "actor", ["model"] = model,
                    ["x"] = .5, ["y"] = .5, ["height"] = .95, ["physics"] = true, ["blink"] = true, ["breath"] = true }),
                ["tracks"] = new JsonArray(), ["events"] = new JsonArray()
            };
            var legacyDocument = SceneDocument.Parse(legacy.ToJsonString());
            legacyDocument.Format();
            return legacyDocument;
        }

        bool original = key == "story-patch", duet = key == "duet-background";
        var root = new JsonObject
        {
            ["format"] = DirectorProject.Format, ["version"] = 1, ["source"] = sourceJson,
            ["output"] = new JsonObject { ["width"] = 1920, ["height"] = 1080, ["supersample"] = 2,
                ["codec"] = "cineform", ["cineformQuality"] = VideoEncoding.DefaultCineFormQuality,
                ["clean"] = !original, ["letterbox"] = original ? "game" : "none" }
        };
        if (original)
        {
            // Do not add empty sections here: they would erase the source story rather than inherit it.
            root["text"] = new JsonObject();
        }
        else
        {
            root["initialize"] = new JsonArray();
            root["finalize"] = new JsonArray();
            var commands = new JsonArray
            {
                (JsonNode)new JsonObject { ["cmd"] = "Director.Scene", ["Values"] = new JsonObject {
                    ["stageVisible"] = false,
                    ["background"] = duet ? new JsonArray(.07, .09, .14, 1) : new JsonArray(0, 0, 0, 0) } },
                (JsonNode)new JsonObject { ["cmd"] = "Director.Camera", ["Values"] = new JsonObject {
                    ["framing"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["zoom"] = 1, ["roll"] = 0 } } }
            };
            Actor(commands, "actor", model!, duet ? .35 : .5);
            if (duet) Actor(commands, "actor2", secondModel!, .65);
            commands.Add((JsonNode)new JsonObject { ["cmd"] = "Delay", ["Duration"] = 3 });
            root["commands"] = commands;
        }
        var document = SceneDocument.Parse(root.ToJsonString());
        document.Format();
        return document;
    }

    static void Actor(JsonArray commands, string target, string model, double x)
    {
        commands.Add((JsonNode)new JsonObject { ["cmd"] = "Character", ["TargetName"] = target, ["Model"] = model });
        commands.Add((JsonNode)new JsonObject { ["cmd"] = "Director.Actor", ["TargetName"] = target, ["Space"] = "screen",
            ["Values"] = new JsonObject { ["visible"] = true, ["position"] = new JsonArray(x, .5), ["height"] = .95,
                ["animation"] = "director", ["loop"] = true, ["physics"] = true, ["breath"] = true, ["blink"] = true } });
    }
}
