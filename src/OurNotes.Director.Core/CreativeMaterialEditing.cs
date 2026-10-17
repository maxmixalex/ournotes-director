using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

/// <summary>Material changes preserve a clip's original command semantics. Expression-track
/// authoring is an explicit compiler policy, never a change to the game's Motion handler.</summary>
public static class CreativeMaterialEditing
{
    public const string ExpressionMode = "expressionMode";
    public static bool KeepsExpression(JsonObject clip) => clip[ExpressionMode]?.ToString() == "timeline";
    static string S(JsonObject node, string key) => CreativeScene.Text(node, key);
    static double N(JsonObject node, string key) => CreativeScene.Number(node, key);

    public static string Insert(CreativeScene scene, string actor, string kind, string asset,
        string label, double start, double duration)
    {
        RequireMaterial(kind, asset);
        var row = new JsonObject { ["cmd"] = kind == "motion" ? "Motion" : "Expression",
            ["TargetName"] = actor, [kind == "motion" ? "MotionName" : "ExpressionName"] = asset,
            ["MotionFadeIn"] = -1 };
        string id = scene.AddClip(actor, kind, start, duration, row, label);
        if (kind == "motion")
        {
            try { SetKeepsExpression(scene, id, true); }
            catch { scene.RemoveClip(id); throw; }
        }
        return id;
    }

    public static void Replace(CreativeScene scene, string id, string kind, string asset, string label)
    {
        RequireMaterial(kind, asset);
        var clip = scene.Clip(id);
        if (S(clip, "kind") != kind || clip["data"]?["cmd"]?.ToString() != (kind == "motion" ? "Motion" : "Expression"))
            throw new InvalidOperationException("所选素材与片段类型不一致。");
        Change(scene, clip, () =>
        {
            // Do not replace the entire row: MotionWait, ExpressionName, fades, Ignore,
            // source metadata and unknown native fields all belong to the original clip.
            clip["data"]![kind == "motion" ? "MotionName" : "ExpressionName"] = asset;
            clip["label"] = label;
        });
    }

    public static void SetKeepsExpression(CreativeScene scene, string id, bool enabled)
    {
        var clip = scene.Clip(id);
        if (clip["data"]?["cmd"]?.ToString() != "Motion") throw new InvalidOperationException("请选择动作片段。");
        if (enabled && N(clip["data"]!.AsObject(), "MotionWait") > 0)
            throw new InvalidOperationException("延后执行的原作动作应保留其表情设置，不能按片段起点推定表情。");
        Change(scene, clip, () => clip[ExpressionMode] = enabled ? "timeline" : "native");
    }

    internal static void Validate(CreativeScene scene)
    {
        foreach (var clip in scene.Clips.OfType<JsonObject>())
        {
            if (clip[ExpressionMode] is not { } mode) continue;
            if (mode is not JsonValue value || !value.TryGetValue<string>(out var text) || text is not ("native" or "timeline")
                || clip["data"]?["cmd"]?.ToString() != "Motion")
                throw new ArgumentException("expressionMode 只适用于 Motion，值为 native 或 timeline。");
            if (KeepsExpression(clip) && N(clip["data"]!.AsObject(), "MotionWait") > 0)
                throw new ArgumentException("延后动作不能按片段起点继承表情，请沿用原指令表情设置。");
        }
        if (!scene.Clips.OfType<JsonObject>().Any(KeepsExpression)) return;
        var inherited = new HashSet<JsonObject>(ReferenceEqualityComparer.Instance);
        var rows = scene.Clips.OfType<JsonObject>().Select((clip, index) => (clip, index))
            .Where(p => p.clip["enabled"]?.GetValue<bool>() != false)
            .OrderBy(p => Story.Frames(N(p.clip, "start"))).ThenBy(p => p.index)
            .Select(p =>
            {
                var row = (JsonObject)p.clip["data"]!.DeepClone();
                if (S(p.clip, "actor").Length > 0) row["TargetName"] = S(p.clip, "actor");
                if (KeepsExpression(p.clip)) inherited.Add(row);
                return row;
            }).ToArray();
        ApplyExpressionPolicy(rows, inherited);
    }

    /// <summary>Linear pass over the already ordered compiled rows. Only explicitly opted-in
    /// Motion rows receive an ExpressionName; all imported/default rows remain untouched.</summary>
    internal static void ApplyExpressionPolicy(IReadOnlyList<JsonObject> rows, IReadOnlySet<JsonObject> inherit)
    {
        if (inherit.Count == 0) return;
        var expressions = new Dictionary<string, string>(StringComparer.Ordinal);
        var deferred = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var parsed = AdvProgram.Row(row, "expression-track compilation");
            if (parsed.Ignored) continue;
            string actor = S(row, "TargetName");
            switch (S(row, "cmd"))
            {
                case "In":
                case "Expression":
                    expressions[actor] = S(row, "ExpressionName");
                    break;
                case "Costume":
                    expressions.Remove(actor);
                    break;
                case "Motion":
                    if (N(row, "MotionWait") > 0) { deferred.Add(actor); break; }
                    if (inherit.Contains(row))
                    {
                        if (deferred.Contains(actor))
                            throw new InvalidOperationException("角色存在延后执行的原作动作，无法静态推定表情。请让此动作沿用原指令表情设置。");
                        row["ExpressionName"] = expressions.GetValueOrDefault(actor, "");
                    }
                    expressions[actor] = S(row, "ExpressionName");
                    // Immediate Motion clears the native pending-motion queue.
                    deferred.Remove(actor);
                    break;
            }
        }
    }

    /// <summary>Expressions persist until another actual expression-changing event, including
    /// an original Motion reset. Ignored/disabled commands cannot shorten the displayed span.</summary>
    public static double ExpressionEnd(CreativeScene scene, JsonObject clip)
    {
        string actor = S(clip, "actor");
        int ownIndex = scene.Clips.IndexOf(clip);
        if (ownIndex < 0) return scene.Duration;
        double start = N(clip, "start"), end = scene.Duration;
        int startFrame = Story.Frames(start), endFrame = int.MaxValue, endIndex = int.MaxValue;
        // Match the compiler's stable (frame, insertion order) ordering with one scan.
        // Re-sorting every row separately for each expression made long timelines costly.
        for (int index = 0; index < scene.Clips.Count; index++)
        {
            if (index == ownIndex || scene.Clips[index] is not JsonObject node
                || node["enabled"]?.GetValue<bool>() == false || S(node, "actor") != actor
                || node["data"] is not JsonObject row) continue;
            string command = S(row, "cmd");
            if (command is not ("Expression" or "In" or "Out" or "Costume")
                && !(command == "Motion" && !KeepsExpression(node) && N(row, "MotionWait") <= 0)) continue;
            double at = N(node, "start");
            int frame = Story.Frames(at);
            if (frame < startFrame || frame == startFrame && index < ownIndex
                || AdvProgram.Row(row, "expression span").Ignored) continue;
            if (frame < endFrame || frame == endFrame && index < endIndex)
            { end = at; endFrame = frame; endIndex = index; }
        }
        return Math.Max(start, end);
    }

    static void RequireMaterial(string kind, string asset)
    {
        if (kind is not ("motion" or "expression") || string.IsNullOrWhiteSpace(asset))
            throw new ArgumentException("请选择有效的动作或表情素材。");
    }
    static void Change(CreativeScene scene, JsonObject clip, Action change)
    {
        int index = scene.Clips.IndexOf(clip); var before = clip.DeepClone();
        try { change(); scene.Validate(); }
        catch { scene.Clips[index] = before; throw; }
    }
}
