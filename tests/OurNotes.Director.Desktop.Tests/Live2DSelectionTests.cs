using System.Text.Json.Nodes;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.Desktop.Tests;

static class Live2DSelectionTests
{
    public static void Run(Action<bool, string> check)
    {
        var metadata = JsonNode.Parse("""
            {"canvas":{"width":640,"height":960},"defaultMotion":"idle","defaultExpression":"normal",
             "motions":["idle","wave"],"expressions":["normal","smile"],
             "parameters":[{"id":"Angle","min":-30,"max":30,"default":0}]}
            """)!;
        static JsonObject[] Rows(SceneDocument project) => project.Root["commands"]!.AsArray().OfType<JsonObject>().ToArray();
        static bool Single(SceneDocument project, string motion, string expression)
        {
            var rows = Rows(project);
            var motions = rows.Where(r => r["cmd"]?.ToString() == "Motion").ToArray();
            var expressions = rows.Where(r => r["cmd"]?.ToString() == "Expression").ToArray();
            return motions.Length == 1 && expressions.Length == 1
                && motions[0]["MotionName"]?.ToString() == motion
                && motions[0]["ExpressionName"]?.ToString() == expression
                && expressions[0]["ExpressionName"]?.ToString() == expression
                && motions[0]["AtFrame"]!.GetValue<int>() == 0
                && expressions[0]["AtFrame"]!.GetValue<int>() == 0;
        }
        var doc = Live2DDocument.Create("test-model", metadata: metadata);
        doc.Time = 2; doc.SetMotion("wave"); doc.SetExpression("smile");
        check(doc.Time == 2 && Single(doc.ToWorkbenchSceneDocument(), "wave", "smile"),
            "Global motion/expression selections retain the cursor and compile exactly one pair at frame zero");
        doc.SetParameter("Angle", 12);
        check(doc.CurrentEditRows().Single()["AtFrame"]!.GetValue<int>() == 60,
            "Changing an action does not silently move the next parameter edit to the first frame");
        doc.SetMotion("idle"); doc.SetExpression(null);
        check(doc.Expression == "normal" && doc.Motion == "idle"
            && Single(doc.ToWorkbenchSceneDocument(), "idle", "normal"),
            "Resetting expression replaces the embedded motion expression as well as the explicit selection");
        doc.Time = 0;
        check(Single(doc.ToWorkbenchSceneDocument(), "idle", "normal"),
            "Seeking backwards never restores a discarded motion or expression");
        doc.Undo();
        check(Single(doc.ToWorkbenchSceneDocument(), "idle", "smile"),
            "One undo restores the selected expression and its native motion payload together");
        doc.Redo();
        check(Single(doc.ToWorkbenchSceneDocument(), "idle", "normal"), "Redo restores the default expression consistently");

        var legacy = JsonNode.Parse(doc.ToJson())!.AsObject();
        legacy["edits"]!.AsArray().Add((JsonNode)new JsonObject { ["cmd"] = "Motion", ["TargetName"] = "live2d",
            ["MotionName"] = "wave", ["ExpressionName"] = "normal", ["AtFrame"] = 75 });
        legacy["edits"]!.AsArray().Add((JsonNode)new JsonObject { ["cmd"] = "Expression", ["TargetName"] = "live2d",
            ["ExpressionName"] = "smile", ["AtFrame"] = 90 });
        var recovered = Live2DDocument.Parse(legacy.ToJsonString());
        check(recovered.Time == 0 && recovered.Motion == "wave" && recovered.Expression == "smile"
            && Single(recovered.ToWorkbenchSceneDocument(), "wave", "smile"),
            "Legacy time-stamped choices are projected as one latest selection, including before their old frame");

        var source = SceneDocument.Parse("""
            {"format":"ournotes-director/project","version":1,
             "source":{"adv":"story","site":"intl","language":"zh-Hans"},
             "output":{"width":1920,"height":1080},"initialize":[],"finalize":[],"text":{},
             "commands":[{"cmd":"Character","TargetName":"actor","Model":"test-model","AtFrame":0},
               {"cmd":"In","TargetName":"actor","PositionType":3,"AtFrame":0},
               {"cmd":"Motion","TargetName":"actor","MotionName":"wave","ExpressionName":"smile","MotionFadeIn":1.2300e-4,"AtFrame":30},
               {"cmd":"Out","TargetName":"actor","PositionType":3,"AtFrame":120},
               {"cmd":"Delay","Frames":0,"AtFrame":150}]}
            """);
        string original = source.Root.ToJsonString();
        var captured = Live2DDocument.FromSceneActor(source, "actor", "test-model", metadata, 2.5, 5);
        var selected = captured.ToWorkbenchSceneDocument();
        var selectedRows = Rows(selected);
        check(captured.Breath && captured.Blink && captured.Physics && captured.Loop,
            "Captured single-model controls reflect the director baseline rather than the source animation mode");
        check(!selected.Validate().HasErrors && Single(selected, "wave", "smile")
            && selectedRows.Count(r => r["cmd"]?.ToString() == "Character") == 1
            && selectedRows.Any(r => r["cmd"]?.ToString() == "Director.Actor" && r["Values"]?["visible"]?.GetValue<bool>() == true),
            "Captured single-selection preview has an explicit visible actor instead of removing its only In command");
        check(!selectedRows.Any(r => r["cmd"]?.ToString() is "In" or "Out" or "Costume")
            && selected.Source.Adv == "story"
            && selected.Root["initialize"]!.AsArray().Count == 0 && selected.Root["finalize"]!.AsArray().Count == 0,
            "Source assets remain usable without replaying original visibility, costume or initialization tracks");
        check(source.Root.ToJsonString() == original
            && JsonNode.DeepEquals(JsonNode.Parse(captured.ToJson())!["performance"], source.Root),
            "The complete original source, including numeric tokens, remains unchanged for recovery");
        captured.Time = 4.5;
        check(Single(captured.ToWorkbenchSceneDocument(), "wave", "smile"),
            "The original scene's later Out cannot hide the independent selected model");
        string directory = Path.Combine(Path.GetTempPath(), "director-selection-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var snapshot = captured.CreateWorkbenchSnapshot(directory);
            check(JsonNode.DeepEquals(SceneDocument.Open(snapshot.FilePath).Root, captured.ToWorkbenchSceneDocument().Root),
                "Preview/live-edit/canvas snapshots and export compile the same canonical workbench project");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
