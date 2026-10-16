using System.Globalization;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

/// <summary>Stage authoring uses the game's slot, focus and animation semantics. These
/// operations compile to existing native rows; they do not invent a second renderer.</summary>
public static class CreativeStaging
{
    public static readonly int[] Slots = [1, 3, 5, 7, 9];
    public static string SlotName(int slot) => slot switch
    { 1 => "最左", 3 => "左侧", 5 => "中央", 7 => "右侧", 9 => "最右", _ => "未入场" };
    static string S(JsonObject value, string key) => CreativeScene.Text(value, key);
    static double N(JsonObject value, string key, double fallback = 0) => CreativeScene.Number(value, key, fallback);
    static bool Enabled(JsonObject clip) => clip["enabled"]?.GetValue<bool>() != false;
    public static bool IsNativeActor(CreativeScene scene, string id) => scene.Actors.OfType<JsonObject>()
        .Any(a => S(a, "id") == id && (a["stageSlot"] is not null || a["fromSource"]?.GetValue<bool>() == true));
    public static int SlotAt(CreativeScene scene, string id, double time)
    {
        var actor = scene.Actors.OfType<JsonObject>().FirstOrDefault(a => S(a, "id") == id);
        int slot = actor?["stageSlot"] is null ? 0 : (int)N(actor, "stageSlot");
        foreach (var clip in scene.Clips.OfType<JsonObject>().Where(c => Enabled(c) && N(c, "start") <= time)
                     .OrderBy(c => N(c, "start")))
        {
            var row = clip["data"]!.AsObject();
            string target = S(clip, "actor"); if (target.Length == 0) target = S(row, "TargetName");
            if (target != id) continue;
            if (S(row, "cmd") == "In") slot = (int)N(row, "PositionType");
            else if (S(row, "cmd") == "Out") slot = 0;
        }
        return slot;
    }
    public static void SetStage(CreativeScene scene, string stage, string? label = null)
    {
        if (string.IsNullOrWhiteSpace(stage)) throw new ArgumentException("请选择实际舞台素材。");
        if (scene.Root["imported"]?.GetValue<bool>() == true)
            throw new InvalidOperationException("原剧情舞台通过对应舞台片段修改，不能覆盖其全部舞台历史。");
        var free = scene.Actors.OfType<JsonObject>().Where(a => a["fromSource"]?.GetValue<bool>() != true).ToArray();
        if (free.Length > 5) throw new InvalidOperationException("原作舞台最多有五个角色站位；请先减少同台角色。");
        scene.Root["staging"] = new JsonObject { ["stage"] = stage, ["name"] = label ?? stage };
        var used = free.Where(a => a["stageSlot"] is not null).Select(a => (int)N(a, "stageSlot")).ToHashSet();
        foreach (var actor in free.Where(a => a["stageSlot"] is null))
        {
            int slot = new[] { 3, 7, 5, 1, 9 }.First(p => !used.Contains(p));
            actor["stageSlot"] = slot; used.Add(slot);
        }
        scene.Validate();
    }
    public static void AssignSlot(CreativeScene scene, string actorId, int slot)
    {
        if (!Slots.Contains(slot)) throw new ArgumentException("不是有效的舞台站位。");
        var actor = scene.Actor(actorId);
        if (scene.Root["staging"] is null || actor["fromSource"]?.GetValue<bool>() == true)
            throw new InvalidOperationException("此操作用于新建舞台的初始站位，原剧情保留其入场片段。");
        int previous = (int)N(actor, "stageSlot");
        var other = scene.Actors.OfType<JsonObject>().FirstOrDefault(a => S(a, "id") != actorId && N(a, "stageSlot") == slot);
        if (other is not null) other["stageSlot"] = previous;
        actor["stageSlot"] = slot; scene.Validate();
    }
    public static string AddFocus(CreativeScene scene, string actor, double at, double duration,
        bool keepCamera = true, int cameraDistance = 3, double otherBlur = 1, double backgroundBlur = .5, string ease = "InOutQuad")
    {
        if (!Slots.Contains(SlotAt(scene, actor, at))) throw new InvalidOperationException("这个角色在播放头尚未进入原作舞台。");
        var row = new JsonObject { ["cmd"] = keepCamera ? "DoF" : "Focus", ["TargetName"] = actor,
            ["PositionType"] = SlotAt(scene, actor, at), ["Duration"] = duration, ["Parameter2"] = ease };
        if (keepCamera) { row["Parameter1"] = 0; row["CanvasLayers"] = new JsonArray(2); }
        else row["CameraDistance"] = cameraDistance;
        // All metadata is placed on the clip, never sent as unknown game fields.
        string id = scene.AddClip(actor, "focus", at, Math.Max(1d / 30, duration), row, keepCamera ? "转移焦点" : "镜头对焦");
        var clip = scene.Clip(id); clip["actorSlot"] = true;
        if (keepCamera) clip["rackFocus"] = new JsonObject { ["others"] = otherBlur, ["background"] = backgroundBlur };
        try { scene.Validate(); } catch { scene.RemoveClip(id); throw; }
        return id;
    }
    public static string AddDepth(CreativeScene scene, string actor, double at, double delta, double duration)
    {
        int slot = SlotAt(scene, actor, at);
        if (!Slots.Contains(slot)) throw new InvalidOperationException("先把角色放到原作舞台。");
        string id = scene.AddClip(actor, "depth", at, Math.Max(1d / 30, duration), new JsonObject
        { ["cmd"] = "MoveToDirection", ["TargetName"] = actor, ["PositionType"] = slot,
            ["Parameter1"] = 0, ["Parameter2"] = 0, ["Parameter3"] = delta, ["Duration"] = duration }, "前后移动");
        scene.Clip(id)["actorSlot"] = true; return id;
    }
    public static void Validate(CreativeScene scene)
    {
        if (scene.Root["staging"] is JsonObject stage && string.IsNullOrWhiteSpace(S(stage, "stage")))
            throw new ArgumentException("舞台资源不能为空。");
        if (scene.Root["staging"] is not null && scene.Root["staging"] is not JsonObject)
            throw new ArgumentException("舞台设置必须是对象。");
        var used = new HashSet<int>();
        foreach (var actor in scene.Actors.OfType<JsonObject>().Where(a => a["stageSlot"] is not null))
        {
            double slot = N(actor, "stageSlot");
            if (slot % 1 != 0 || !Slots.Contains((int)slot) || !used.Add((int)slot)) throw new ArgumentException("角色站位无效或冲突。");
            if (scene.Root["staging"] is null) throw new ArgumentException("舞台站位需要舞台资源。");
            if (S(actor, "layer") is not ("" or "Forward" or "Back")) throw new ArgumentException("遮挡顺序无效。");
            if (Math.Abs(N(actor, "depth")) > 1000) throw new ArgumentException("前后距离过大。");
        }
        foreach (var clip in scene.Clips.OfType<JsonObject>())
        {
            var row = clip["data"]!.AsObject();
            string id = S(clip, "actor"); if (id.Length == 0) id = S(row, "TargetName");
            if (Enabled(clip) && scene.Actors.OfType<JsonObject>().Any(a => S(a, "id") == id && a["stageSlot"] is not null)
                && S(row, "cmd") == "Director.Actor" && row["Values"]?["overlay"] is null)
                throw new ArgumentException("此镜头含二维人物控制。原作站位不能混用二维位置或速度片段；请先移除对应片段。动作、表情和语音不受影响。");
            if (clip["rackFocus"] is JsonObject rack)
            {
                foreach (string key in new[] { "others", "background" })
                    if (N(rack, key) is < 0 or > 1) throw new ArgumentException("景深强度应在 0 到 1 之间。");
                if (S(row, "cmd") != "DoF") throw new ArgumentException("转移焦点需要 DoF 片段。");
            }
            if (Enabled(clip) && clip["actorSlot"]?.GetValue<bool>() == true && !Slots.Contains(SlotAt(scene, S(clip, "actor"), N(clip, "start"))))
                throw new ArgumentException("景深或前后移动时，目标角色尚未入场。");
            if (S(row, "cmd") is "DoF" or "Focus" or "MoveToDirection")
            {
                if (N(row, "Duration") < 0 || N(row, "Duration") > 36000) throw new ArgumentException("过渡时长无效。");
                if (S(row, "cmd") == "DoF" && N(row, "Parameter1") is < 0 or > 1) throw new ArgumentException("景深强度应在 0 到 1 之间。");
                if (S(row, "cmd") == "Focus" && (N(row, "CameraDistance") % 1 != 0 || N(row, "CameraDistance") is < 0 or > 6)) throw new ArgumentException("原作景别无效。");
            }
        }
    }
    internal static IEnumerable<JsonObject> Expand(CreativeScene scene, JsonObject clip)
    {
        var row = (JsonObject)clip["data"]!.DeepClone(); string actor = S(clip, "actor"); double at = N(clip, "start");
        if (actor.Length > 0) row["TargetName"] = actor;
        if (clip["actorSlot"]?.GetValue<bool>() == true) row["PositionType"] = SlotAt(scene, actor, at);
        if (clip["rackFocus"] is not JsonObject rack) { yield return row; yield break; }
        foreach (var target in scene.Actors.OfType<JsonObject>())
        {
            string id = S(target, "id"); int slot = SlotAt(scene, id, at); if (!Slots.Contains(slot)) continue;
            var blur = (JsonObject)row.DeepClone(); blur["TargetName"] = id; blur["PositionType"] = slot;
            blur["Parameter1"] = id == actor ? 0 : N(rack, "others"); blur["CanvasLayers"] = new JsonArray(2); yield return blur;
        }
        var background = (JsonObject)row.DeepClone(); background["TargetName"] = ""; background["PositionType"] = 0;
        background["Parameter1"] = N(rack, "background"); background["CanvasLayers"] = new JsonArray(0); yield return background;
    }
}

public sealed record NativeStageOption(string Key, string Label, IReadOnlyList<int> FocusDistances);
public static class NativeStageCatalog
{
    public static IReadOnlyList<NativeStageOption> Read(Archive archive, string site, string source = DirectorProject.LibrarySource)
    {
        var manifest = archive.Manifest(site, "stories", source);
        var definition = JsonNode.Parse(archive.Logical(site, manifest, "story.json"))!;
        var scene = JsonNode.Parse(archive.Logical(site, manifest, definition["scene"]!.ToString()))!;
        var settings = scene["settings"]!["playerSettings"]!;
        var map = settings["_focusDataSettingsMap"]!["_list"]!.AsArray();
        return scene["stages"]!.AsObject().Select(pair =>
        {
            var component = pair.Value!["nodes"]!.AsArray().SelectMany(n => n!["components"]!.AsArray()).First(n => n?["class"]?.ToString() == "AdvStage")!;
            string key = component["_usingFocusDataSettingsKey"]?.ToString() ?? "";
            var entry = map.FirstOrDefault(m => m?["Key"]?.ToString() == key) ?? map.First(m => m?["Key"]?.ToString() == settings["_defaultFocusDataSettingsKey"]?.ToString());
            var distances = entry!["Value"]!["_focusData"]!.AsArray().Select(n => (int)Actor.N(n!["_cameraDistance"])).ToArray();
            return new NativeStageOption(pair.Key, pair.Key.Split('/').Last().Replace("adv_bkg_stage_", "舞台 ", StringComparison.Ordinal), distances);
        }).ToArray();
    }
}
