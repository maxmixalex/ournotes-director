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
        scene.RemoveClip(motion);
        check(scene.Compile()["commands"]!.AsArray().OfType<JsonObject>().Count(r => r["cmd"]?.ToString() == "In") == 2, "Removing an animation cannot remove staged characters");
    }
}
