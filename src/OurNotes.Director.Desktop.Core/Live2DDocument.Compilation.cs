using System.Text.Json.Nodes;
using OurNotes.Director.Core;

namespace OurNotes.Director.Desktop.Core;

public sealed partial class Live2DDocument
{
    /// <summary>The normal scene/export project. A captured performance is cloned in
    /// full; only explicit workbench edits are inserted into its existing frame schedule.</summary>
    public SceneDocument ToSceneDocument(bool selectedMotionOnly = false)
    {
        Validate();
        var project = root["performance"]?.DeepClone() as JsonObject ?? Standalone();
        var output = ActorIsolation.Output(project["output"] as JsonObject, ActorId, Width, Height);
        output["isolateTransform"] = root["transform"]!.DeepClone();
        if (root["output"]?["isolateCanvas"] is { } canvas) output["isolateCanvas"] = canvas.DeepClone();
        else output.Remove("isolateCanvas");
        project["output"] = output;
        if (OwnRows().Any()) EnsureEditablePerformance();
        // The independent Live2D workbench has one selected motion/expression,
        // never a second copy of the source scene's time-varying action tracks.
        // Preserve the rest of the original performance and its exact timing.
        var sourceRows = project["commands"]!.AsArray().OfType<JsonObject>();
        if (selectedMotionOnly && OwnRows().Any(row => Text(row, "cmd") is "Motion" or "Expression"))
            sourceRows = sourceRows.Where(row => !(Text(row, "TargetName") == ActorId
                && Text(row, "cmd") is "Motion" or "Expression" or "In"));
        project["commands"] = new JsonArray(sourceRows.Concat(OwnRows()).OrderBy(Frame)
            .Select(row => (JsonNode)row.DeepClone()).ToArray());
        // Extend a scheduled source only when the user extended the document. Existing
        // later commands stay intact when the export range is shorter than the source.
        if (project["commands"] is JsonArray commands && commands.OfType<JsonObject>().All(row => row["AtFrame"] is not null)
            && commands.OfType<JsonObject>().Select(Frame).DefaultIfEmpty(-1).Max() < FrameAt(Duration))
            commands.Add((JsonNode)new JsonObject { ["cmd"] = "Delay", ["Frames"] = 0, ["AtFrame"] = FrameAt(Duration) });
        NormalizeDependencies(project, baseDirectory);
        var result = SceneDocument.Parse(project.ToJsonString(DesktopSettings.JsonOptions), baseDirectory: baseDirectory);
        var validation = result.Validate();
        if (validation.HasErrors) throw new ArgumentException(string.Join("\n", validation.Issues
            .Where(issue => issue.Severity == SceneIssueSeverity.Error).Select(issue => issue.Message)));
        return result;
    }
    public SceneSnapshot CreateSnapshot(string directory, bool selectedMotionOnly = false) => ToSceneDocument(selectedMotionOnly).CreateSnapshot(directory);

    JsonObject Standalone() => new()
    {
        ["format"] = DirectorProject.Format, ["version"] = 1,
        ["source"] = new JsonObject { ["adv"] = DirectorProject.LibrarySource, ["site"] = Site, ["language"] = Language },
        ["output"] = new JsonObject { ["width"] = Canvas?.ReferenceWidth ?? Width, ["height"] = Canvas?.ReferenceHeight ?? Height, ["supersample"] = 1,
            ["clean"] = true, ["letterbox"] = "none", ["codec"] = "cineform", ["cineformQuality"] = "film1" },
        ["initialize"] = new JsonArray(), ["finalize"] = new JsonArray(), ["text"] = new JsonObject(),
        ["commands"] = new JsonArray(
            new JsonObject { ["cmd"] = "Character", ["TargetName"] = ActorId, ["Model"] = ModelId, ["AtFrame"] = 0 },
            new JsonObject { ["cmd"] = "Director.Actor", ["TargetName"] = ActorId, ["Space"] = "screen", ["AtFrame"] = 0,
                ["Values"] = new JsonObject { ["visible"] = true, ["animation"] = "director", ["physics"] = true,
                    ["breath"] = true, ["blink"] = true, ["loop"] = true } },
            new JsonObject { ["cmd"] = "Delay", ["Frames"] = 0, ["AtFrame"] = FrameAt(Duration) })
    };

    void EnsureEditablePerformance()
    {
        if (root["performance"] is not JsonObject performance) return;
        if (new[] { "initialize", "commands", "finalize" }.Any(section => performance[section] is not JsonArray)
            || performance["commands"]!.AsArray().OfType<JsonObject>().Any(row => row["AtFrame"] is null))
            throw new InvalidOperationException("请从场景时间轴打开已解析的完整演出，再编辑角色动作或参数。原剧情的等待与历史不能用猜测的时间替换。");
    }
    static void NormalizeDependencies(JsonObject? project, string directory)
    {
        if (project is null) return;
        foreach (string section in new[] { "initialize", "commands", "finalize" })
            foreach (var row in project[section]?.AsArray().OfType<JsonObject>() ?? [])
            {
                if (row["AudioPath"] is JsonValue audio && audio.TryGetValue<string>(out var audioPath) && !string.IsNullOrWhiteSpace(audioPath))
                    row["AudioPath"] = Path.GetFullPath(audioPath, directory);
                if (Text(row, "cmd") == "Director.Scene" && row["Values"]?["backgroundImage"] is JsonObject image
                    && image["path"] is JsonValue file && file.TryGetValue<string>(out var imagePath) && !string.IsNullOrWhiteSpace(imagePath))
                    image["path"] = Path.GetFullPath(imagePath, directory);
            }
    }
    public void Validate()
    {
        static void Keys(JsonObject value, string[] allowed, string context)
        {
            if (value.Any(pair => !allowed.Contains(pair.Key, StringComparer.Ordinal)))
                throw new ArgumentException(context + " 包含不支持的字段。");
        }
        Keys(root, ["format", "version", "model", "actor", "site", "language", "duration", "time", "range",
            "output", "transform", "metadata", "performance", "edits"], "Live2D 项目");
        if (Text(root, "format") != Format || root["version"]?.GetValue<int>() != 1)
            throw new ArgumentException("Live2D 项目需要 format ournotes-director/live2d 和 version 1。");
        foreach (string field in new[] { "model", "actor", "site", "language" })
            if (string.IsNullOrWhiteSpace(Text(root, field))) throw new ArgumentException("Live2D 项目缺少 " + field + "。");
        if (root["output"] is not JsonObject output) throw new ArgumentException("Live2D 项目缺少画布尺寸。");
        Keys(output, ["width", "height", "isolateCanvas", "canvasMultiplier"], "Live2D 画布"); RenderDimensions.Validate(Width, Height, 1);
        ValidateCanvas();
        if (!double.IsFinite(Duration) || Duration < 0 || Duration > 36000 - 1d / 30
            || Math.Abs(Duration * 30 - Math.Round(Duration * 30)) > 1e-6)
            throw new ArgumentException("Live2D 时长必须位于 30 fps 帧边界且不超过十小时。");
        if (!double.IsFinite(Time) || Time < 0 || Time > Duration) throw new ArgumentException("Live2D 播放头超出时间范围。");
        if (root["range"] is JsonObject range)
        {
            Keys(range, ["startFrame", "endFrameExclusive"], "Live2D 区间");
            if (Range!.EndFrameExclusive > TimelineRange.Full(Duration, 30).EndFrameExclusive)
                throw new ArgumentException("Live2D 区间超出项目。");
        }
        else if (root["range"] is not null) throw new ArgumentException("Live2D 区间必须是对象。");
        if (root["transform"] is not JsonObject) throw new ArgumentException("Live2D 项目缺少模型变换。");
        _ = ActorIsolation.ReadTransform(root["transform"], ActorId);
        if (root["metadata"] is not JsonObject) throw new ArgumentException("Live2D 模型信息必须是对象。");
        var parameters = Definitions().ToDictionary(parameter => parameter.Id, StringComparer.Ordinal);
        if (root["edits"] is not JsonArray edits) throw new ArgumentException("Live2D 项目缺少编辑指令数组。");
        foreach (var entry in edits)
        {
            if (entry is not JsonObject row || Text(row, "TargetName") != ActorId
                || row["AtFrame"] is not JsonValue frame || !frame.TryGetValue<int>(out int at) || at < 0 || at >= 1080000)
                throw new ArgumentException("Live2D 编辑指令缺少目标或有效帧号。");
            var command = AdvProgram.Row(row, "live2d edit");
            if (command.Command is not ("Motion" or "Expression" or "Director.Parameter" or "Director.Actor"))
                throw new ArgumentException("Live2D 编辑包含不支持的指令。");
            if (command.Command == "Director.Actor" && row["Values"]?["controls"] is not JsonObject)
                throw new ArgumentException("Live2D 控制器编辑不能改变原剧情的舞台归属。");
            DirectorValidation.Check(command);
            if (command.Command == "Director.Parameter")
            {
                if (!parameters.TryGetValue(Text(row, "Parameter"), out var parameter))
                    throw new ArgumentException("Live2D 编辑缺少对应模型的真实参数定义。");
                if (Text(row, "Operation") != "release" && (Number(row, "Value") < parameter.Minimum || Number(row, "Value") > parameter.Maximum))
                    throw new ArgumentException("Live2D 编辑参数超出模型范围。");
            }
        }
        if (root["performance"] is JsonObject performance)
        {
            var source = SceneDocument.Parse(performance.ToJsonString(), baseDirectory: baseDirectory);
            if (source.Kind != SceneDocumentKind.UnifiedProject || source.Validate().HasErrors)
                throw new ArgumentException("Live2D 项目的原演出不是有效的统一场景。");
            if (source.Source.Site != Site || source.Source.Language != Language)
                throw new ArgumentException("Live2D 模型与原演出的站点和语言必须一致。");
            if (edits.Count > 0) EnsureEditablePerformance();
        }
        else if (root["performance"] is not null) throw new ArgumentException("Live2D 原演出必须是完整项目对象。");
    }
}
