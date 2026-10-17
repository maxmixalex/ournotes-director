using System.Numerics;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

/// <summary>One undoable canvas gesture is one native translation, not a Director overlay.</summary>
public static class CreativeStagePlacement
{
    public static string MoveHere(CreativeScene scene, string actor, double time, Vector3 delta)
    {
        StageDragPlane.CheckVector(delta);
        if (!double.IsFinite(time) || time < 0 || time > 36000) throw new ArgumentOutOfRangeException(nameof(time));
        int slot = CreativeStaging.SlotAt(scene, actor, time);
        if (!CreativeStaging.Slots.Contains(slot)) throw new InvalidOperationException("角色尚未入场，不能移动原作站位。");
        int frame = Story.Frames(time);
        var existing = scene.Clips.OfType<JsonObject>().LastOrDefault(c =>
            c["placementGesture"]?.GetValue<bool>() == true && c["enabled"]?.GetValue<bool>() != false &&
            c["sourcePerformance"]?.GetValue<bool>() != true && c["data"]?["cmd"]?.ToString() == "MoveToDirection" &&
            CreativeScene.Number(c["data"]!.AsObject(), "Duration") == 0 &&
            c["actor"]?.ToString() == actor && Story.Frames(CreativeScene.Number(c, "start")) == frame);
        if (existing is not null)
        {
            var before = (JsonObject)existing.DeepClone(); int index = scene.Clips.IndexOf(existing);
            var data = existing["data"]!.AsObject();
            var total = new Vector3((float)CreativeScene.Number(data, "Parameter1"),
                (float)CreativeScene.Number(data, "Parameter2"), (float)CreativeScene.Number(data, "Parameter3")) + delta;
            StageDragPlane.CheckVector(total);
            try
            {
                data["Parameter1"] = total.X; data["Parameter2"] = total.Y; data["Parameter3"] = total.Z;
                scene.Validate(); return existing["id"]!.ToString();
            }
            catch { scene.Clips[index] = before; throw; }
        }
        string id = scene.AddClip(actor, "placement", time, 1d / 30, new JsonObject
        {
            ["cmd"] = "MoveToDirection", ["TargetName"] = actor, ["PositionType"] = slot,
            ["Parameter1"] = delta.X, ["Parameter2"] = delta.Y, ["Parameter3"] = delta.Z, ["Duration"] = 0
        }, "人物摆位");
        var clip = scene.Clip(id); clip["actorSlot"] = true; clip["placementGesture"] = true;
        return id;
    }
}
