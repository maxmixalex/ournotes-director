using System.Numerics;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

namespace OurNotes.Director.Desktop.Tests;

static class StagePlacementTests
{
    public static void Run(Action<bool, string> check)
    {
        var random = new Random(722);
        double maximum = 0;
        for (int i = 0; i < 200; i++)
        {
            // Exercise perspective, parent rotation/scale, camera roll and vertical letterboxing.
            var parent = AdvMatrix.Trs(new(.2f, -.5f, -8), new(5, 12, (float)random.NextDouble() * 30), new(1.1f, .8f, 1));
            var projection = AdvMatrix.Multiply(AdvMatrix.Perspective(37, 16f / 9, .1f, 100), parent);
            var anchor = new Vector3((float)random.NextDouble(), (float)random.NextDouble(), .4f);
            var plane = new StageDragPlane(3, new(.1f, .2f, .3f), anchor, projection, .75);
            double dx = random.NextDouble() * .2 - .1, dy = random.NextDouble() * .2 - .1;
            var shift = plane.Displacement(dx, dy);
            var before = AdvMatrix.Transform(projection, new Vector4(anchor, 1));
            var after = AdvMatrix.Transform(projection, new Vector4(anchor + shift, 1));
            maximum = Math.Max(maximum, Math.Abs((after.X / after.W - before.X / before.W) / 2 - dx));
            maximum = Math.Max(maximum, Math.Abs(-(after.Y / after.W - before.Y / before.W) / 2 * .75 - dy));
            if (shift.Z != 0) throw new InvalidOperationException("Canvas drag changed native Z.");
        }
        check(maximum < 1e-6, "Projected stage dragging follows the pointer under rotated/scaled parents and letterboxing, preserving Z");
        bool invalid = false;
        try { new StageDragPlane(3, Vector3.Zero, Vector3.Zero, new float[16]).Displacement(.1, .1); }
        catch (InvalidOperationException) { invalid = true; }
        check(invalid, "Degenerate or behind-camera drag geometry is rejected instead of fabricating movement");
        var scene = CreativeScene.Blank("intl", "zh-Hans"); CreativeStaging.SetStage(scene, "stage");
        string actor = scene.AddActor("model", "actor");
        string first = CreativeStagePlacement.MoveHere(scene, actor, 0, new(.2f, .1f, 0));
        string second = CreativeStagePlacement.MoveHere(scene, actor, 0, new(.3f, -.1f, .2f));
        var row = scene.Clip(first)["data"]!.AsObject();
        check(first == second && scene.Clips.Count == 1 && Math.Abs(CreativeScene.Number(row, "Parameter1") - .5) < 1e-6,
            "Repeated gestures at the same frame merge only their native movement event");
        check(CreativeScene.Number(row, "Parameter2") == 0 && Math.Abs(CreativeScene.Number(row, "Parameter3") - .2) < 1e-6 && row["cmd"]!.ToString() == "MoveToDirection",
            "The saved gesture is native XYZ displacement, not overlay, scale or a new renderer command");
        CreativeStaging.AssignSlot(scene, actor, 7);
        var compiled = scene.Compile()["commands"]!.AsArray().OfType<JsonObject>();
        check(compiled.Any(r => r["cmd"]?.ToString() == "MoveToDirection" && r["PositionType"]?.ToString() == "7")
            && !compiled.Any(r => r["cmd"]?.ToString() == "Director.Actor"), "Authored placement follows reassigned native slots without enabling Director animation");
        check(CreativeScene.Parse(scene.ToJson()).Compile().ToJsonString() == scene.Compile().ToJsonString(), "Native spatial gestures roundtrip through scene JSON");
        var beforeInvalid = scene.ToJson();
        try { CreativeStagePlacement.MoveHere(scene, actor, 0, new(float.NaN, 0, 0)); }
        catch (ArgumentException) { }
        check(beforeInvalid == scene.ToJson(), "Non-finite drag input cannot corrupt the saved scene");
    }
}
