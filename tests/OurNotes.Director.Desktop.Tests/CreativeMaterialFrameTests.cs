using System.Text.Json.Nodes;
using OurNotes.Director.Core;

namespace OurNotes.Director.Desktop.Tests;

static class CreativeMaterialFrameTests
{
    public static void Run(Action<bool, string> check)
    {
        var scene = CreativeScene.Blank("intl", "zh-Hans");
        string actor = scene.AddActor("model", "Actor");
        string motion = CreativeMaterialEditing.Insert(scene, actor, "motion", "motion", "Motion", 0, 1);
        string expression = CreativeMaterialEditing.Insert(scene, actor, "expression", "smile", "Face", 0, 1);
        // Agent-authored JSON need not start at an exact multiple of 1/30. Both entries
        // round to frame one, so their insertion order, not raw decimal order, applies.
        scene.Clip(motion)["start"] = .049;
        scene.Clip(expression)["start"] = .02;
        var rows = scene.Compile()["commands"]!.AsArray().OfType<JsonObject>().ToArray();
        check(rows.Single(r => r["cmd"]?.ToString() == "Motion")["ExpressionName"]?.ToString() == "",
            "Fractional JSON timestamps use exactly the compiler's frame rounding when inheriting expressions");
        CreativeMaterialEditing.SetKeepsExpression(scene, motion, false);
        check(CreativeMaterialEditing.ExpressionEnd(scene, scene.Clip(expression)) == scene.Duration,
            "An earlier same-frame Motion cannot truncate the expression that executes after it");
        scene.Clip(motion)["start"] = .051;
        check(CreativeMaterialEditing.ExpressionEnd(scene, scene.Clip(expression)) == .051,
            "A Motion rounding to the next frame ends the preceding expression track");
        var roundtrip = CreativeScene.Parse(scene.ToJson());
        check(roundtrip.Compile().ToJsonString() == scene.Compile().ToJsonString(),
            "Fractional timestamp ordering survives JSON roundtrip without rewriting source times");
    }
}
