using System.Text.Json.Nodes;
using OurNotes.Director.Core;

namespace OurNotes.Director.Desktop.Tests;

static class CreativeStagingTests
{
    public static void Run(Action<bool, string> check)
    {
        var scene = CreativeScene.Blank("intl", "zh-Hans");
        CreativeStaging.SetStage(scene, "a-real-stage-key");
        string a = scene.AddActor("model-a", "A"), b = scene.AddActor("model-b", "B");
        check(scene.Source["adv"] is null, "Staging a new shot never invents an original story source");
        check(CreativeStaging.SlotAt(scene, a, 0) == 3 && CreativeStaging.SlotAt(scene, b, 0) == 7, "Two actors take distinct left and right original stage slots");
        scene.Actor(a)["depth"] = -1.25; scene.Actor(b)["depth"] = 1.5;
        string motion = scene.AddClip(a, "motion", 1, 1, new JsonObject { ["cmd"] = "Motion", ["MotionName"] = "motion-a", ["MotionFadeIn"] = -1 }, "动作");
        check(!scene.CanResize(scene.Clip(motion)), "Native motions do not advertise a trim that would change only the timeline rectangle");
        string focus = CreativeStaging.AddFocus(scene, a, .5, .5);
        CreativeStaging.AddFocus(scene, b, 2, .5);
        var rows = scene.Compile()["commands"]!.AsArray().OfType<JsonObject>().ToArray();
        check(rows.Count(r => r["cmd"]?.ToString() == "In") == 2 && !rows.Any(r => r["cmd"]?.ToString() == "Director.Actor"), "Game stage motion must not convert staged actors into screen-space drawables");
        check(rows.Where(r => r["cmd"]?.ToString() == "MoveToDirection").Select(r => r["Parameter3"]!.GetValue<double>()).SequenceEqual(new[] { -1.25, 1.5 }), "Initial spatial depth is native Z movement, not a resized sprite");
        check(rows.Count(r => r["cmd"]?.ToString() == "DoF") == 6 && !rows.Any(r => r["cmd"]?.ToString() == "Focus"), "Rack focus changes three actual layer-blur tracks without moving the camera");
        scene.ResizeClip(focus, .8);
        check(scene.Clip(focus)["data"]!["Duration"]!.GetValue<double>() == .8, "Focus duration editing affects native tweens, not just the timeline rectangle");
        CreativeStaging.AssignSlot(scene, a, 7);
        var changed = scene.Compile()["commands"]!.AsArray().OfType<JsonObject>().First(r => r["cmd"]?.ToString() == "DoF" && r["TargetName"]?.ToString() == a);
        check(CreativeStaging.SlotAt(scene, b, 0) == 3 && changed["PositionType"]!.GetValue<int>() == 7, "Swapping stage slots updates actor-targeted focus without stale numeric references");
        var roundtrip = CreativeScene.Parse(scene.ToJson());
        check(roundtrip.Compile().ToJsonString() == scene.Compile().ToJsonString(), "All stage and focus controls roundtrip through the same JSON document");
        string nativeFocus = CreativeStaging.AddFocus(scene, a, 3, .6, false, 4);
        check(scene.Clip(nativeFocus)["data"]!["cmd"]!.ToString() == "Focus" && scene.Clip(nativeFocus)["rackFocus"] is null, "Native camera focus remains distinct from fixed-framing rack focus");
        int count = scene.Clips.Count;
        try { CreativeStaging.AddFocus(scene, b, 4, .5, otherBlur: 2); check(false, "Invalid focus rejected"); }
        catch (ArgumentException) { check(scene.Clips.Count == count, "Invalid focus edit is atomic"); }
        string nativeLayer = scene.AddClip("", "focus", 1, .5, new JsonObject
        { ["cmd"] = "DoF", ["PositionType"] = 7, ["Duration"] = .5, ["Parameter1"] = .7, ["CanvasLayers"] = new JsonArray(0, 2) }, "原分层景深");
        var layer = scene.Clip(nativeLayer); string unchanged = scene.ToJson();
        check(CreativeFocusEditing.Mode(layer) == "layer" && CreativeFocusEditing.LayerScope(layer) == (true, true)
            && CreativeFocusEditing.LayerActor(scene, layer) == a && scene.ToJson() == unchanged,
            "Inspecting original DoF retains both blur layers and resolves the actual slot without converting it to camera Focus");
        CreativeFocusEditing.SetLayerScope(scene, nativeLayer, b, true, true);
        check(scene.Compile()["commands"]!.AsArray().OfType<JsonObject>().Any(r => r["cmd"]?.ToString() == "DoF"
            && r["TargetName"]?.ToString() == b && r["PositionType"]!.GetValue<int>() == 3
            && r["CanvasLayers"]!.AsArray().Count == 2), "Layer edits retain combined background and character targets");
        CreativeFocusEditing.SetMode(scene, nativeLayer, "native", 4);
        check(CreativeFocusEditing.Mode(scene.Clip(nativeLayer)) == "native"
            && scene.Clip(nativeLayer)["data"]!["CameraDistance"]!.GetValue<int>() == 4, "Explicit camera-focus conversion records the chosen supported shot");
        CreativeFocusEditing.SetMode(scene, nativeLayer, "layer");
        CreativeFocusEditing.SetLayerScope(scene, nativeLayer, "", true, false);
        check(CreativeFocusEditing.LayerScope(scene.Clip(nativeLayer)) == (true, false)
            && scene.Clip(nativeLayer)["actorSlot"]!.GetValue<bool>() == false, "Background-only blur does not invent an actor target");
        string beforeInvalid = scene.ToJson();
        try { CreativeFocusEditing.SetLayerScope(scene, nativeLayer, "missing", false, true); check(false, "Missing actor rejected"); }
        catch (InvalidOperationException) { check(scene.ToJson() == beforeInvalid, "Invalid layer target leaves the complete clip unchanged"); }
        int beforeFocus = scene.Clips.Count;
        string sameFocus = CreativeFocusEditing.FocusHere(scene, b, .5);
        check(sameFocus == focus && scene.Clips.Count == beforeFocus && scene.Clip(focus)["actor"]!.ToString() == b,
            "Repeated focus at the same frame updates the existing authored rack instead of adding conflicting tweens");
        check(CreativeFocusEditing.StageAt(scene, 0) == "a-real-stage-key", "Active focus presets are keyed by actual stage");
        string order = CreativeFocusEditing.OrderHere(scene, a, 1.5, true);
        int beforeOrder = scene.Clips.Count;
        check(CreativeFocusEditing.OrderHere(scene, a, 1.5, false) == order && scene.Clips.Count == beforeOrder
            && scene.Clip(order)["data"]!["cmd"]!.ToString() == "Back", "Reordering a subject twice at the same frame updates one native order event");
        var orderRows = scene.Compile()["commands"]!.AsArray().OfType<JsonObject>();
        check(orderRows.Any(r => r["cmd"]?.ToString() == "Back" && r["PositionType"]!.GetValue<int>() == 7)
            && scene.Actor(a)["depth"]!.GetValue<double>() == -1.25, "Draw order keeps real depth unchanged and follows the actor's current native slot");
        string beforeBadOrder = scene.ToJson();
        try { CreativeFocusEditing.OrderHere(scene, "missing", 0, true); check(false, "Invalid order target rejected"); }
        catch (InvalidOperationException) { check(scene.ToJson() == beforeBadOrder, "Missing draw-order target is rejected without changing the scene"); }
        scene.RemoveClip(motion);
        check(scene.Compile()["commands"]!.AsArray().OfType<JsonObject>().Count(r => r["cmd"]?.ToString() == "In") == 2, "Removing an animation cannot remove staged characters");
    }
}
