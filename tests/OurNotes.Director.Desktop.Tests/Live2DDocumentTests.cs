using System.Text.Json.Nodes;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.Desktop.Tests;

public static class Live2DDocumentTests
{
    // Managed fixtures only: real Cubism metadata and pixels are covered by native tests.
    static JsonObject Metadata() => JsonNode.Parse("""
        {"id":"test-model","canvas":{"width":640,"height":960},"defaultMotion":"idle","defaultExpression":"normal",
        "motions":["idle","wave"],"expressions":["normal","smile"],"parameters":[
        {"id":"ParamAngleX","name":"头部角度 X","min":-30,"max":30,"default":0},
        {"id":"ParamEyeLOpen","name":"左眼","min":0,"max":1,"default":1}]}
        """)!.AsObject();

    public static void Run(Action<bool, string> check)
    {
        void Reject(Action action, string label)
        {
            bool failed = false;
            try { action(); } catch (Exception error) when (error is ArgumentException or InvalidOperationException or FormatException) { failed = true; }
            check(failed, label);
        }
        string directory = Path.Combine(Path.GetTempPath(), "director-live2d-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var doc = Live2DDocument.Create("test-model", metadata: Metadata());
            check(doc.Width == 640 && doc.Height == 960 && doc.Motion == "idle" && doc.Expression == "normal", "Live2D opens the actual model canvas and default requests");
            check(doc.Parameters.Count == 2 && doc.Parameters[0].Minimum == -30 && doc.Parameters[1].Default == 1, "Live2D parameter ranges/defaults come from supplied native definitions");
            var compiled = doc.ToSceneDocument();
            check(!compiled.Validate().HasErrors && compiled.Source.IsLibrary && compiled.Root["initialize"] is JsonArray && compiled.Root["finalize"] is JsonArray,
                "A new single-model project needs only the material library and compiles to the existing native format");
            check(compiled.Root["output"]?["isolateActor"]?.ToString() == doc.ActorId && compiled.Root["output"]?["letterbox"]?.ToString() == "none", "Standalone preview/export uses native isolation");
            string saved = Path.Combine(directory, "model.live2d.json");
            doc.Save(saved); doc.Time = 1.2;
            check(!doc.HasUnsavedChanges && Math.Abs(doc.Time - 1.2) < 1e-9, "Moving a Live2D playhead does not dirty the saved document");
            doc.UpdateParameterValues([new("ParamAngleX", "native", -30, 30, 0, 7.25, false)]);
            check(doc.Parameters[0].Value == 7.25 && !doc.HasUnsavedChanges, "Observed Cubism values do not become saved parameter pins");
            doc.Time = 1.201;
            check(doc.Parameters[0].Value == 7.25 && doc.Time == 1.2 && !doc.HasUnsavedChanges,
                "An unchanged displayed frame retains observed parameters during inspector refresh");
            doc.SetParameterMetadata(Metadata());
            check(!doc.HasUnsavedChanges, "Refreshing true model metadata is not an authored change");
            doc.SetParameter("ParamAngleX", 15);
            check(doc.HasUnsavedChanges && doc.Parameters[0].Value == 15 && doc.Parameters[0].IsOverridden, "Parameter edits create real native pins");
            var immediate = doc.CurrentEditRows().Single();
            check(immediate["cmd"]?.ToString() == "Director.Parameter" && immediate["AtFrame"]!.GetValue<int>() == 36,
                "Live edit and exported pin use the same current-frame command");
            immediate["Value"] = -25;
            check(doc.CurrentEditRows().Single()["Value"]!.GetValue<double>() == 15, "Live-edit callers receive independent rows");
            doc.Undo(); check(!doc.HasUnsavedChanges && doc.Time == 1.2, "Undo restores the saved model while retaining the cursor");
            doc.Redo(); check(doc.Parameters[0].Value == 15, "Redo restores a native parameter edit");
            doc.Time = 2; doc.ResetParameter("ParamAngleX");
            check(doc.CurrentEditRows().Single()["Operation"]?.ToString() == "release", "Restoring automatic parameters is a real native release command");
            var parameters = doc.ToSceneDocument().Root["commands"]!.AsArray().OfType<JsonObject>().Where(row => row["cmd"]?.ToString() == "Director.Parameter").ToArray();
            check(parameters.Length == 2 && parameters[0]["AtFrame"]!.GetValue<int>() == 36 && parameters[1]["AtFrame"]!.GetValue<int>() == 60,
                "Parameter reset retains the earlier pinned performance");
            doc.Time = 1.2; check(doc.Parameters[0].IsOverridden && doc.Parameters[0].Value == 15, "Seeking back before release restores the earlier authored pin");
            doc.SetExpression("smile"); doc.SetMotion("wave");
            var motion = doc.ToSceneDocument().Root["commands"]!.AsArray().OfType<JsonObject>().Single(row => row["cmd"]?.ToString() == "Motion");
            check(motion["ExpressionName"]?.ToString() == "smile", "Changing motion preserves the selected expression in the native command");
            doc.SetControllers(breath: false); doc.SetControllers(physics: false, blink: false, loop: false);
            var controls = doc.CurrentEditRows().Single(row => row["cmd"]?.ToString() == "Director.Actor")["Values"]!["controls"]!;
            check(controls.AsObject().Count == 4 && !doc.Breath && !doc.Blink && !doc.Physics && !doc.Loop,
                "Controller edits merge at the cursor and use the native controls branch");
            check(!doc.CurrentEditRows().Any(row => row["cmd"]?.ToString() is "Motion" or "Expression"), "Immediate edit rows do not accidentally restart motion/expression history");
            doc.SetTransform(new(.4, .6, .75, 12, .5));
            var transform = doc.ToSceneDocument().Root["output"]!["isolateTransform"]!;
            check(transform["scale"]!.GetValue<double>() == .75 && transform["opacity"]!.GetValue<double>() == .5,
                "Position, scale, rotation and opacity enter the native output presentation");
            doc.Range = new(15, 100, 30); doc.Save(); doc.Time = 1.5; doc.Save();
            var reopened = Live2DDocument.Open(saved);
            check(!reopened.HasUnsavedChanges && reopened.FilePath == saved && reopened.Time == 1.5 && reopened.Range == new TimelineRange(15, 100, 30)
                && reopened.Transform == doc.Transform && reopened.Parameters.Count == 2, "Save/reopen retains model, cursor, range, transform and true definitions");
            reopened.SetParameter("ParamEyeLOpen", .25);
            var draft = Live2DDocument.FromDraft(reopened.ToDraft());
            check(draft.HasUnsavedChanges && draft.FilePath == saved && draft.ToJson() == reopened.ToJson(), "Draft recovery retains both dirty status and original file identity");
            using (var snapshot = reopened.CreateSnapshot(Path.Combine(directory, "snapshots")))
            {
                var output = DirectorProject.Read(snapshot.FilePath);
                check(output.Adv == DirectorProject.LibrarySource && output.Output["isolateActor"]?.ToString() == reopened.ActorId,
                    "Live2D queue snapshots pass the shared native project reader");
            }

            var source = SceneDocument.Parse("""
                {"format":"ournotes-director/project","version":1,"source":{"adv":"story","site":"intl","language":"zh-Hans"},
                "output":{"width":1920,"height":1080,"letterbox":"game"},"initialize":[],"finalize":[],
                "commands":[{"cmd":"Character","TargetName":"actor","Model":"test-model","AtFrame":0},
                {"cmd":"In","TargetName":"actor","PositionType":3,"AtFrame":0},
                {"cmd":"Motion","TargetName":"actor","MotionName":"wave","ExpressionName":"smile","MotionFadeIn":1.2300e-4,"AtFrame":30},
                {"cmd":"Delay","Frames":0,"AtFrame":150}],"text":{"source":{"simplifiedChinese":"原始演出"}}}
                """, baseDirectory: directory);
            var captured = Live2DDocument.FromSceneActor(source, "actor", "test-model", Metadata(), 2.5, 5, new(60, 90, 30));
            var isolated = captured.ToSceneDocument().Root;
            var sourceRows = source.Root["commands"]!.AsArray().OfType<JsonObject>().ToArray();
            var isolatedRows = isolated["commands"]!.AsArray().OfType<JsonObject>().ToArray();
            check(sourceRows.All(original => isolatedRows.Any(candidate => JsonNode.DeepEquals(candidate, original)))
                && JsonNode.DeepEquals(isolated["text"], source.Root["text"])
                && isolated.ToJsonString().Contains("1.2300e-4", StringComparison.Ordinal), "Scene handoff retains every original command, text and raw numeric token");
            check(captured.Time == 0 && captured.Range == new TimelineRange(60, 90, 30) && captured.Motion == "wave" && captured.Expression == "smile",
                "Scene handoff captures the source playhead's single motion/expression and starts independent preview at frame zero");
            var selectedCommands = captured.ToSceneDocument(selectedMotionOnly: true).Root["commands"]!.AsArray().OfType<JsonObject>().ToArray();
            check(selectedCommands.Count(row => row["TargetName"]?.ToString() == "actor" && row["cmd"]?.ToString() == "Motion") == 1
                && selectedCommands.Count(row => row["TargetName"]?.ToString() == "actor" && row["cmd"]?.ToString() == "Expression") == 1
                && selectedCommands.Where(row => row["TargetName"]?.ToString() == "actor" && row["cmd"]?.ToString() is "Motion" or "Expression")
                    .All(row => row["AtFrame"]!.GetValue<int>() == 0),
                "Independent Live2D preview/export compiles one selected motion and expression at frame zero");
            check(isolated["output"]?["playbackViewport"]?["letterbox"]?.ToString() == "game", "Isolation preserves the source opening-wait viewport");
            captured.SetControllers(physics: false); captured.SetMotion("idle");
            var changed = captured.ToSceneDocument().Root["commands"]!.AsArray().OfType<JsonObject>().ToArray();
            check(changed.Count(row => row["cmd"]?.ToString() == "Motion") == 2
                && changed.Count(row => row["cmd"]?.ToString() == "Expression") == 1
                && captured.Motion == "idle" && captured.Expression == "smile" && captured.Time == 0,
                "Replacing a Live2D motion keeps the original source untouched but changes the independent selection globally");
            check(source.Root["commands"]!.AsArray().Count == 4 && source.Root["output"]?["isolateActor"] is null,
                "Working on a captured model never mutates its source scene");
            var raw = source.Root;
            raw["commands"]!.AsArray().Insert(3, new JsonObject { ["cmd"] = "Voice", ["TargetName"] = "actor", ["AudioPath"] = "voice.wav", ["AtFrame"] = 40 });
            raw["commands"]!.AsArray().Insert(0, new JsonObject { ["cmd"] = "Director.Scene", ["Values"] = new JsonObject
                { ["backgroundImage"] = new JsonObject { ["path"] = "background.png" } }, ["AtFrame"] = 0 });
            var dependencies = Live2DDocument.FromSceneActor(SceneDocument.Parse(raw.ToJsonString(), baseDirectory: directory), "actor", "test-model", Metadata(), 1, 5);
            dependencies.Save(Path.Combine(directory, "moved", "model.json"));
            var dependencyRows = dependencies.ToSceneDocument().Root["commands"]!.AsArray().OfType<JsonObject>();
            check(dependencyRows.Any(row => row["AudioPath"]?.ToString() == Path.Combine(directory, "voice.wav"))
                && dependencyRows.Any(row => row["Values"]?["backgroundImage"]?["path"]?.ToString() == Path.Combine(directory, "background.png")),
                "Embedded source audio and images remain resolved after saving the model elsewhere");
            captured.SetDuration(2); check(captured.Time == 2 && captured.Range!.EndFrameExclusive == 61, "Duration edits clamp the playhead/range without deleting source commands");
            Reject(() => captured.SetParameter("missing", 0), "A workbench cannot author invented model parameters");
            Reject(() => captured.SetParameter("ParamAngleX", 31), "A parameter cannot exceed its real Cubism range");
            Reject(() => captured.SetTransform(new(Scale: 0)), "Zero-scale isolated output is rejected");
            Reject(() => captured.SetMotion("missing"), "Unknown model motion is rejected before it reaches the renderer");
            Reject(() => ActorIsolation.ReadTransform(new JsonObject { ["x"] = .5 }, null), "Isolation transforms require a selected actor");
            Reject(() => DirectorValidation.Check(AdvProgram.Row(JsonNode.Parse("""{"cmd":"Director.Actor","TargetName":"actor","Values":{"controls":{"physics":false},"position":[0,0]}}""")!, "test")),
                "Controller-only edits cannot silently steal source actor placement");
            Reject(() => DirectorValidation.Check(AdvProgram.Row(JsonNode.Parse("""{"cmd":"Director.Actor","TargetName":"actor","Duration":1,"Values":{"controls":{"physics":false}}}""")!, "test")),
                "Controller switches reject misleading tween durations");
            Reject(() => Live2DDocument.Parse(doc.ToJson().Replace("\"version\": 1", "\"version\": 1, \"version\": 1", StringComparison.Ordinal)),
                "Live2D projects reject duplicate JSON keys");
            var sequential = source.Root;
            foreach (var row in sequential["commands"]!.AsArray().OfType<JsonObject>()) row.Remove("AtFrame");
            var original = Live2DDocument.FromSceneActor(SceneDocument.Parse(sequential.ToJsonString()), "actor", "test-model", Metadata(), 1, 5);
            check(JsonNode.DeepEquals(original.ToSceneDocument().Root["commands"], sequential["commands"]), "A sequential original remains intact when only its presentation is isolated");
            Reject(() => original.SetMotion("wave"), "Unmeasured sequential history cannot silently be converted to a guessed frame schedule");
            var single = Live2DDocument.Create("test-model", metadata: Metadata()); single.SetDuration(0);
            check(TimelineRange.Full(single.Duration, 30).FrameCount == 1 && !single.ToSceneDocument().Validate().HasErrors, "A one-frame model document is valid");
        }
        finally { Directory.Delete(directory, true); }
    }
}
