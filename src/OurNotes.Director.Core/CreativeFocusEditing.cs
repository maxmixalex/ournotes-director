using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

/// <summary>Preserves the distinction between game camera focus, per-layer blur and a
/// composite rack-focus operation. Inspecting a native DoF row must not reinterpret it.</summary>
public static class CreativeFocusEditing
{
    static string S(JsonObject value, string key) => CreativeScene.Text(value, key);
    static double N(JsonObject value, string key) => CreativeScene.Number(value, key);
    public static string Mode(JsonObject clip) => clip["rackFocus"] is JsonObject ? "rack"
        : clip["data"]?["cmd"]?.ToString() == "Focus" ? "native" : "layer";

    public static string StageAt(CreativeScene scene, double time)
    {
        string result = scene.Root["staging"]?["stage"]?.ToString() ?? "";
        foreach (var clip in scene.Clips.OfType<JsonObject>().Where(c =>
                     c["enabled"]?.GetValue<bool>() != false && N(c, "start") <= time)
                 .OrderBy(c => N(c, "start")))
            if (clip["data"]?["cmd"]?.ToString() == "Stage")
                result = clip["data"]?["TargetAssetName"]?.ToString() ?? "";
        return result;
    }

    public static void SetMode(CreativeScene scene, string id, string mode, int cameraDistance = 3)
    {
        if (mode is not ("rack" or "native" or "layer")) throw new ArgumentException("未知对焦方式。");
        Change(scene, id, clip =>
        {
            string actor = S(clip, "actor"); double at = N(clip, "start");
            if (mode is "rack" or "native" && !CreativeStaging.Slots.Contains(CreativeStaging.SlotAt(scene, actor, at)))
            {
                actor = scene.Actors.OfType<JsonObject>().Select(a => S(a, "id"))
                    .FirstOrDefault(a => CreativeStaging.Slots.Contains(CreativeStaging.SlotAt(scene, a, at)))
                    ?? throw new InvalidOperationException("播放头没有可对焦的舞台人物。");
                clip["actor"] = actor;
            }
            var row = clip["data"]!.AsObject();
            row["cmd"] = mode == "native" ? "Focus" : "DoF";
            if (mode == "native")
            {
                row["CameraDistance"] = cameraDistance; row.Remove("Parameter1"); row.Remove("CanvasLayers");
            }
            else
            {
                row.Remove("CameraDistance"); row["Parameter1"] = 0;
                row["CanvasLayers"] = new JsonArray(actor.Length == 0 ? 0 : 2);
            }
            row["TargetName"] = actor;
            row["PositionType"] = actor.Length == 0 ? 0 : CreativeStaging.SlotAt(scene, actor, at);
            clip["actorSlot"] = actor.Length != 0;
            clip.Remove("rackFocus");
            if (mode == "rack") clip["rackFocus"] = new JsonObject { ["others"] = 1, ["background"] = .5 };
            clip["label"] = mode switch { "rack" => "转移焦点", "native" => "镜头对焦", _ => "单层虚化" };
        });
    }

    public static string LayerActor(CreativeScene scene, JsonObject clip)
    {
        var row = clip["data"]!.AsObject(); int slot = (int)N(row, "PositionType");
        // Explicit native slot takes precedence; TargetName can be retained for unrelated state.
        if (slot != 0) return scene.Actors.OfType<JsonObject>().Select(a => S(a, "id"))
            .FirstOrDefault(a => CreativeStaging.SlotAt(scene, a, N(clip, "start")) == slot) ?? "";
        return S(clip, "actor").Length > 0 ? S(clip, "actor") : S(row, "TargetName");
    }

    public static (bool Background, bool Character) LayerScope(JsonObject clip)
    {
        var layers = clip["data"]?["CanvasLayers"] as JsonArray;
        return (layers?.Any(v => v?.ToString() == "0") == true,
            layers is null || layers.Count == 0 || layers.Any(v => v?.ToString() == "2"));
    }

    public static void SetLayerScope(CreativeScene scene, string id, string actor, bool background, bool character)
    {
        if (!background && !character) throw new ArgumentException("至少选择一个虚化层。");
        Change(scene, id, clip =>
        {
            if (Mode(clip) != "layer") throw new InvalidOperationException("此操作只改变单层虚化目标。");
            int slot = character ? CreativeStaging.SlotAt(scene, actor, N(clip, "start")) : 0;
            if (character && !CreativeStaging.Slots.Contains(slot)) throw new InvalidOperationException("所选角色尚未入场。");
            var row = clip["data"]!.AsObject(); clip["actor"] = character ? actor : ""; clip["actorSlot"] = character;
            row["TargetName"] = character ? actor : ""; row["PositionType"] = slot;
            var layers = new JsonArray(); if (background) layers.Add((JsonNode)JsonValue.Create(0)); if (character) layers.Add((JsonNode)JsonValue.Create(2));
            row["CanvasLayers"] = layers;
        });
    }

    /// <summary>Repeatedly pointing at a subject at the same frame updates one authored
    /// rack-focus clip; it does not stack mutually overriding tweens.</summary>
    public static string FocusHere(CreativeScene scene, string actor, double time)
    {
        if (!double.IsFinite(time) || time < 0) throw new ArgumentOutOfRangeException(nameof(time));
        int frame = Story.Frames(time);
        var existing = scene.Clips.OfType<JsonObject>().LastOrDefault(c => c["enabled"]?.GetValue<bool>() != false
            && c["sourcePerformance"]?.GetValue<bool>() != true && c["rackFocus"] is JsonObject
            && Story.Frames(N(c, "start")) == frame);
        if (existing is null) return CreativeStaging.AddFocus(scene, actor, time, .5);
        string id = S(existing, "id");
        Change(scene, id, clip =>
        {
            int slot = CreativeStaging.SlotAt(scene, actor, N(clip, "start"));
            if (!CreativeStaging.Slots.Contains(slot)) throw new InvalidOperationException("所选角色尚未入场。");
            clip["actor"] = actor; clip["actorSlot"] = true;
            clip["data"]!["TargetName"] = actor; clip["data"]!["PositionType"] = slot;
        });
        return id;
    }

    public static string OrderHere(CreativeScene scene, string actor, double time, bool forward)
    {
        if (!double.IsFinite(time) || time < 0) throw new ArgumentOutOfRangeException(nameof(time));
        int slot = CreativeStaging.SlotAt(scene, actor, time);
        if (!CreativeStaging.Slots.Contains(slot)) throw new InvalidOperationException("所选角色尚未入场。");
        string command = forward ? "Forward" : "Back", label = forward ? "置于前面" : "置于后面";
        var existing = scene.Clips.OfType<JsonObject>().LastOrDefault(c => c["enabled"]?.GetValue<bool>() != false
            && c["sourcePerformance"]?.GetValue<bool>() != true && S(c, "actor") == actor
            && c["data"]?["cmd"]?.ToString() is "Forward" or "Back" && Story.Frames(N(c, "start")) == Story.Frames(time));
        if (existing is null)
        {
            string added = scene.AddClip(actor, "transform", time, 1d / 30, new JsonObject
            { ["cmd"] = command, ["TargetName"] = actor, ["PositionType"] = slot }, label);
            scene.Clip(added)["actorSlot"] = true; return added;
        }
        string id = S(existing, "id");
        Change(scene, id, clip =>
        {
            clip["actorSlot"] = true; clip["label"] = label;
            clip["data"]!["cmd"] = command; clip["data"]!["PositionType"] = slot;
        });
        return id;
    }

    static void Change(CreativeScene scene, string id, Action<JsonObject> edit)
    {
        var clip = scene.Clip(id); var before = (JsonObject)clip.DeepClone(); int index = scene.Clips.IndexOf(clip);
        try { edit(clip); scene.Validate(); }
        catch { scene.Clips[index] = before; throw; }
    }
}
