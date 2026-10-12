using System.Text.Json.Nodes;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.Desktop.Tests;

public static class StudioSceneDocumentTests
{
    public static void Run(Action<bool, string> check)
    {
        Basics(check);
        Timeline(check);
        OriginalCommands(check);
        HistoryAndFiles(check);
        NativeFrameClock(check);
    }
    static void Basics(Action<bool, string> check)
    {
        var scene = StudioSceneDocument.Create(new("42", "intl", "zh-Hans"), 1);
        check(scene.Duration == 1 && scene.CanEditTimeline && scene.Sample(0).Actors.Count == 0,
            "studio creates a real empty native project and preserves a one-second duration");
        check(!scene.Document.Validate().HasErrors && scene.Document.Validate().RequiresNativeValidation,
            "studio structure success still requires archive/native asset validation");
        int changes = 0, playheads = 0, selections = 0;
        scene.Changed += () => changes++; scene.PlayheadChanged += () => playheads++; scene.SelectionChanged += () => selections++;
        string original = scene.Document.Text;
        scene.Time = .52; scene.SelectedId = StudioSceneDocument.CameraId;
        check(changes == 0 && playheads == 1 && selections == 1 && scene.Frame == 16 && scene.Document.Text == original && !scene.CanUndo,
            "studio playhead and selection do not mutate JSON or create undo revisions");
        string actor = scene.AddActor("fixture-model");
        var initial = scene.Sample(0).Actors.Single();
        check(initial.Id == actor && initial.Model == "fixture-model" && initial.Space == "screen" && initial.Visible && Near(initial.Transform.Height, .95),
            "studio actor uses the native screen coordinate defaults and an actual Character command");
        check(Throws<ArgumentException>(() => scene.AddActor("fixture-model", actor)) && scene.Actors.Count == 1,
            "studio refuses duplicate object names without losing the original actor");
        scene.SetActorTransform(actor, .2, .4, .8, 12, 0, false);
        initial = scene.Sample(0).Actors.Single();
        check(Near(initial.Transform.X, .2) && Near(initial.Transform.Y, .4) && Near(initial.Transform.Height, .8) && Near(initial.Transform.Roll, 12),
            "studio screen placement and roll values follow the native Director.Actor units");
        scene.EditDocument(document =>
        {
            var root = document.Root;
            var row = root["commands"]!.AsArray().OfType<JsonObject>().First(row => row["cmd"]?.ToString() == "Director.Actor");
            row["Space"] = "world"; row["Values"]!["position"] = new JsonArray(2, 3, -4);
            document.ReplaceText(root.ToJsonString());
        });
        check(scene.Actors.Single().Space == "world" && Near(scene.Actors.Single().Transform.Z, -4)
            && Throws<InvalidOperationException>(() => scene.SetActorTransform(actor, .5, .5, 1, 0, 0, false)),
            "studio preserves world XYZ and prevents normalized-screen drags from reinterpreting it");
        scene.SetActorTransform(actor, new(X: 4, Y: 5, Z: 6, RotationX: 7, RotationY: 8, Roll: 9, ScaleZ: 2), 0, false);
        check(Near(scene.Actors.Single().Transform.Z, 6) && Near(scene.Actors.Single().Transform.RotationY, 8) && Near(scene.Actors.Single().Transform.ScaleZ, 2),
            "studio world transform editing retains XYZ rotation and scale components");
        var sample = scene.Sample(0); sample.Background[3] = 1; sample.Camera.WorldValues["fieldOfView"] = 99;
        check(Near(scene.Sample(0).Background[3]!.GetValue<double>(), 0) && scene.Sample(0).Camera.WorldValues["fieldOfView"] is null,
            "studio returned sample JSON cannot corrupt the cached scene state");
    }
    static void Timeline(Action<bool, string> check)
    {
        var scene = StudioSceneDocument.Create(new("42", "intl", "zh-Hans"), 1);
        string actor = scene.AddActor("fixture-model");
        scene.SetActorTransform(actor, .2, .4, .8, 0, 0, false);
        scene.SetActorTransform(actor, .8, .6, 1.2, 90, 1, true);
        scene.SetCamera(new(0, 0, 2, 30), 1, true);
        scene.SetGrade(new(2, 1.5, .5, .8), 1, true);
        scene.SetBackground(new JsonArray(1, 0, 0, 1), 1, true);
        scene.SetMotion(actor, "motion-from-archive", .5);
        scene.SetExpression(actor, "expression-from-archive", .75);
        scene.SetActorOpacity(actor, .2, 1, true);
        scene.SetActorVisibility(actor, false, .9, true);
        var middle = scene.Sample(.5);
        check(Near(middle.Actors[0].Transform.X, .5) && Near(middle.Actors[0].Transform.Y, .5)
            && Near(middle.Actors[0].Transform.Height, 1) && Near(middle.Actors[0].Transform.Roll, 45),
            "studio position, height and rotation interpolate from their base pose");
        check(Near(middle.Camera.Zoom, 1.5) && Near(middle.Camera.Roll, 15) && Near(middle.Grade.Exposure, 1)
            && Near(middle.Grade.Vignette, .4) && Near(middle.Background[3]!.GetValue<double>(), .5),
            "studio camera, grade and integer-created RGBA arrays are real numeric tracks");
        check(scene.Sample(14d / 30).Actors[0].Motion is null && middle.Actors[0].Motion == "motion-from-archive"
            && scene.Sample(22d / 30).Actors[0].Expression is null && scene.Sample(23d / 30).Actors[0].Expression == "expression-from-archive",
            "studio animation events snap to frame boundaries and hold their names only after the event");
        check(scene.Sample(26d / 30).Actors[0].Visible && !scene.Sample(27d / 30).Actors[0].Visible,
            "studio visibility is discrete rather than interpolated");
        var firstHalf = scene.Sample(.5);
        scene.Sample(1); var backward = scene.Sample(.5);
        check(firstHalf.Actors[0] == backward.Actors[0] && firstHalf.Grade == backward.Grade,
            "studio backwards scrubbing returns the same authored state");
        var positionKey = scene.Keyframes.Single(key => key.ObjectId == actor && key.Track == "actor.position");
        scene.SetKeyframeEase(positionKey.Id, "InQuad");
        check(Near(scene.Sample(.5).Actors[0].Transform.X, .35), "studio keys use native easing functions");
        scene.MoveKeyframe(positionKey.Id, 2);
        check(scene.Duration == 2 && Near(scene.Sample(1).Actors[0].Transform.X, .35),
            "studio moving a key changes native interval duration and extends the scene");
        check(Throws<ArgumentException>(() => scene.SetDuration(.5)), "studio refuses truncation across existing keys");
        scene.SetKeyframeEase(positionKey.Id, "Step");
        check(Near(scene.Sample(1).Actors[0].Transform.X, .2) && Near(scene.Sample(2).Actors[0].Transform.X, .8),
            "studio step keys retain the previous value until the target frame");
        scene.DeleteKeyframe(positionKey.Id);
        check(Near(scene.Sample(1).Actors[0].Transform.X, .2), "studio deleting the last position key restores the unchanged base pose");
        scene.Undo();
        var reopened = StudioSceneDocument.Parse(scene.Document.Text);
        check(reopened.CanEditTimeline && reopened.Keyframes.Count == scene.Keyframes.Count && reopened.Duration == scene.Duration,
            "studio native JSON round trip reconstructs sparse edit keys instead of exposing thousands of baked keys");
        foreach (int frame in new[] { 0, 1, 14, 15, 22, 23, 26, 27, 30, 59, 60 })
        {
            var left = scene.Sample(frame / 30d); var right = reopened.Sample(frame / 30d);
            check(left.Actors[0] == right.Actors[0] && left.Grade == right.Grade && JsonNode.DeepEquals(left.Background, right.Background),
                "studio round-trip value parity at frame " + frame);
        }
        check(!reopened.Document.Validate().HasErrors, "studio compiled timing metadata stays inside legal native command fields");
        var native = Playback(reopened.Document.Root);
        native.Player.Start();
        for (int frame = 0; frame <= 60; frame++)
        {
            if (frame > 0) native.Player.Step();
            var expected = reopened.Sample(frame / 30d);
            check(Near(native.X, expected.Actors[0].Transform.X) && Near(native.BackgroundAlpha, expected.Background[3]!.GetValue<double>()),
                "studio baked native playback matches the editor exactly at frame " + frame);
        }
        check(native.Events["motion-from-archive"] == 15 && native.Events["expression-from-archive"] == 23,
            "studio generated native commands run Motion and Expression on the selected output frames");
        var edited = reopened.Document.Root;
        var derived = edited["commands"]!.AsArray().OfType<JsonObject>().First(row => row["Key"]?.ToString().StartsWith("ournotes-studio/1/bake/", StringComparison.Ordinal) == true);
        derived["IsNoWait"] = 0;
        var advanced = StudioSceneDocument.Parse(edited.ToJsonString()); string advancedText = advanced.Document.Text;
        check(!advanced.CanEditTimeline && Throws<InvalidOperationException>(() => advanced.SetGrade(new(), .5, true)) && advanced.Document.Text == advancedText,
            "studio retains manually modified derived frames and prevents later visual edits from discarding them");
    }
    static void OriginalCommands(Action<bool, string> check)
    {
        const string json = """
            {"format":"ournotes-director/project","version":1,"source":{"adv":"42"},
             "initialize":[{"cmd":"Character","TargetName":"a","Model":"fixture-model","future":123456789012345678901234567890},
               {"cmd":"Director.Actor","TargetName":"a","Space":"screen","Key":"original-key","Values":{"position":[0.25,0.5],"height":0.9}}],
             "commands":[{"cmd":"Delay","Duration":0.10000000000000001,"i":"original-delay"},
               {"cmd":"Motion","TargetName":"a","MotionName":"original-motion","other":1.234567890123456789e-90},
               {"cmd":"Delay","Duration":1}],"finalize":[],"text":{"future":{"keep":true}}}
            """;
        var scene = StudioSceneDocument.Parse(json);
        check(scene.Document.Text == json && scene.CanEditTimeline, "studio import preserves literal source text before editing");
        var before = Playback(scene.Document.Root); before.Player.Start(); for (int i = 0; i < 40; i++) before.Player.Step();
        scene.SetActorTransform("a", .4, .5, .8, 0, 0, false);
        var initial = scene.Document.Root["initialize"]!.AsArray().OfType<JsonObject>().Single(row => row["cmd"]!.ToString() == "Director.Actor");
        check(Near(initial["Values"]!["position"]![0]!.GetValue<double>(), .4) && initial["Key"]!.ToString() == "original-key",
            "studio base editing writes back into initialize and retains the original diagnostic key");
        var reopened = StudioSceneDocument.Parse(scene.Document.Text);
        var delay = reopened.RawCommands.Single(row => row.Data["i"]?.ToString() == "original-delay");
        check(delay.Data["Duration"]!.ToJsonString() == "0.10000000000000001" && !delay.Data.ContainsKey("Frames")
            && reopened.Document.Root["initialize"]![0]!["future"]!.ToJsonString() == "123456789012345678901234567890",
            "studio compiled timing restores original delay presence, exact number tokens and unfamiliar source fields");
        var after = Playback(reopened.Document.Root); after.Player.Start(); for (int i = 0; i < 40; i++) after.Player.Step();
        check(before.Events["original-motion"] == after.Events["original-motion"],
            "studio normalizes old floating-point Delay timing without moving original animation events");
        string eventKey = reopened.Keyframes.Single(key => key.Track == "motion").Id;
        reopened.MoveKeyframe(eventKey, .5);
        var moved = Playback(reopened.Document.Root); moved.Player.Start(); for (int i = 0; i < 16; i++) moved.Player.Step();
        check(moved.Events["original-motion"] == 15, "studio raw animation event markers can be moved using their retained native row");
        reopened.DeleteKeyframe(eventKey);
        check(!reopened.RawCommands.Any(row => row.Command == "Motion"), "studio deleting a raw event removes that exact row");
        const string inherited = " {\"format\":\"ournotes-director/project\",\"version\":1,\"source\":{\"adv\":\"42\"}} ";
        var original = StudioSceneDocument.Parse(inherited); int changed = 0; original.Changed += () => changed++;
        original.SetPlaybackDuration(120);
        check(!original.CanEditTimeline && original.Duration == 120 && original.Document.Text == inherited && changed == 0
            && Throws<InvalidOperationException>(() => original.AddActor("fixture-model")),
            "studio inherited source segments keep JSON intact while allowing an explicit native preview/export range");
        var talk = StudioSceneDocument.Parse(json.Replace("\"cmd\":\"Motion\"", "\"cmd\":\"Talk\"", StringComparison.Ordinal));
        check(!talk.CanEditTimeline && talk.RawCommands.Any(row => row.Command == "Talk"),
            "studio leaves resource-dependent dialogue timing as complete raw instructions");
    }
    static void HistoryAndFiles(Action<bool, string> check)
    {
        string folder = Path.Combine(Path.GetTempPath(), "ournotes-studio-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string oldDirectory = Path.Combine(folder, "old"), newDirectory = Path.Combine(folder, "new");
            Directory.CreateDirectory(oldDirectory);
            var scene = StudioSceneDocument.Create(new("42", "intl", "zh-Hans"), 1);
            scene.Save(Path.Combine(oldDirectory, "scene.json"));
            scene.SetBackgroundImage("image.png");
            string actor = scene.AddActor("fixture-model"); string initial = scene.Document.Text;
            scene.BeginEdit("drag");
            scene.SetActorTransform(actor, .6, .5, .9, 0, 0, false);
            scene.SetActorTransform(actor, .7, .5, .9, 0, 0, false);
            scene.SetActorOpacity(actor, .5, 0, false);
            scene.CommitEdit(); string edited = scene.Document.Text;
            check(scene.UndoLabel == "drag" && scene.Undo() && scene.Document.Text == initial,
                "studio multi-field drag creates one reversible edit transaction");
            check(scene.Redo() && scene.Document.Text == edited, "studio redo restores the compiled native document exactly");
            scene.BeginEdit("cancel drag"); scene.SetActorOpacity(actor, .1); scene.CancelEdit();
            check(scene.Document.Text == edited, "studio cancelling a drag restores its complete pre-edit JSON");
            string beforeInvalid = scene.Document.Text;
            check(Throws<ArgumentException>(() => scene.SetActorOpacity(actor, double.NaN)) && scene.Document.Text == beforeInvalid,
                "studio failed numeric validation leaves the previous document intact");
            check(Throws<SceneDocumentParseException>(() => scene.ReplaceJson("{")) && scene.Document.Text == beforeInvalid,
                "studio failed advanced JSON paste leaves the prior model and history intact");
            scene.Save(Path.Combine(newDirectory, "scene.json"));
            check(ResolveBackground(scene.Document) == Path.Combine(oldDirectory, "image.png"),
                "studio Save As keeps relative background resources attached to their original file");
            check(scene.Undo() && ResolveBackground(scene.Document) == Path.Combine(oldDirectory, "image.png"),
                "studio undo after Save As rebases the prior revision rather than redirecting its image paths");
            scene.Redo();
            using var snapshot = scene.ToSceneDocument().CreateSnapshot(Path.Combine(folder, "snapshot"));
            check(ResolveBackground(SceneDocument.Open(snapshot.FilePath)) == Path.Combine(oldDirectory, "image.png"),
                "studio native export snapshots resolve resources independently of their temporary directory");
        }
        finally { Directory.Delete(folder, true); }
    }
    static void NativeFrameClock(Action<bool, string> check)
    {
        var root = StudioSceneDocument.Create(new("42", "intl", "zh-Hans"), 1).Document.Root;
        root["commands"] = new JsonArray((JsonNode)new JsonObject { ["cmd"] = "Delay", ["Frames"] = 0 },
            (JsonNode)new JsonObject { ["cmd"] = "Motion", ["TargetName"] = "a", ["MotionName"] = "zero" },
            (JsonNode)new JsonObject { ["cmd"] = "Delay", ["Frames"] = 1 },
            (JsonNode)new JsonObject { ["cmd"] = "Motion", ["TargetName"] = "a", ["MotionName"] = "one" },
            (JsonNode)new JsonObject { ["cmd"] = "Delay", ["Frames"] = 29 },
            (JsonNode)new JsonObject { ["cmd"] = "Motion", ["TargetName"] = "a", ["MotionName"] = "thirty" });
        var native = Playback(root); native.Player.Start();
        check(native.Events["zero"] == 0 && !native.Events.ContainsKey("one"), "native Delay.Frames zero completes without spending a frame");
        native.Player.Step();
        check(native.Events["one"] == 1, "native Delay.Frames one resumes exactly on output frame one");
        for (int i = 1; i < 29; i++) native.Player.Step();
        check(!native.Events.ContainsKey("thirty"), "native fixed frame waits do not fire their endpoint early");
        native.Player.Step();
        check(native.Events["thirty"] == 30, "native fixed frame waits do not accumulate float-duration drift");
        foreach (string value in new[] { "null", "-1", "1.5", "1080001", "\"1\"", "true" })
        {
            var row = AdvProgram.Row(JsonNode.Parse("{\"cmd\":\"Delay\",\"Frames\":" + value + "}")!, "invalid");
            check(Throws<ArgumentException>(() => AdvPlayback.FixedDelayFrames(row)), "native fixed frame validation rejects " + value);
        }
        check(Throws<ArgumentException>(() => AdvPlayback.FixedDelayFrames(AdvProgram.Row(JsonNode.Parse("{\"cmd\":\"Delay\",\"Frames\":1,\"Duration\":1}")!, "both"))),
            "native fixed frame waits reject ambiguous simultaneous duration units");
        var invalidRoot = (JsonObject)root.DeepClone(); invalidRoot["commands"]![0]!["Frames"] = -1;
        check(SceneDocument.Parse(invalidRoot.ToJsonString()).Validate().HasErrors, "studio local validation shares native fixed frame rules");

        var delayedTween = StudioSceneDocument.Create(new("42", "intl", "zh-Hans"), 1).Document.Root;
        delayedTween["commands"] = JsonNode.Parse("""
            [{"cmd":"Delay","Frames":30},{"cmd":"Director.Scene","Duration":0.1,"Values":{"background":[0,0,0,1]}},
             {"cmd":"Motion","TargetName":"a","MotionName":"after-tween"}]
            """);
        int endFrame = TweenEventFrame(delayedTween);
        var model = StudioSceneDocument.Parse(delayedTween.ToJsonString());
        check(model.CanEditTimeline && Near(model.RawCommands.Single(row => row.Command == "Motion").Time!.Value, endFrame / 30d),
            "studio accounts for a native tween starting in the Update phase of an existing frame");
        model.SetCamera(new(Zoom: 2));
        check(TweenEventFrame(model.Document.Root) == endFrame,
            "studio editing a parallel camera preserves the delayed raw tween completion frame");
        var consecutive = (JsonObject)delayedTween.DeepClone();
        consecutive["commands"] = JsonNode.Parse("""
            [{"cmd":"Director.Scene","Duration":0.1,"Values":{"background":[0,0,0,1]}},
             {"cmd":"Director.Scene","Duration":0.1,"Values":{"background":[1,1,1,0]}}]
            """);
        string untouched = consecutive.ToJsonString(); var raw = StudioSceneDocument.Parse(untouched);
        check(!raw.CanEditTimeline && raw.Document.Text == untouched && Throws<InvalidOperationException>(() => raw.SetGrade(new(), .1, true)),
            "studio preserves phase-dependent consecutive source tweens as read-only raw instructions instead of shifting a frame");
    }
    static int TweenEventFrame(JsonObject root)
    {
        var loop = new AdvLoop(); var tweens = new AdvTweens(loop); float alpha = 0; int eventFrame = -1;
        var program = new AdvProgram { Rows = root["commands"]!.AsArray().Select((row, index) => AdvProgram.Row(row!, index.ToString())).ToArray() };
        var player = new AdvPlayback(loop, program) { Prepare = _ => [] };
        player.Register("TalkWindow", (_, _) => []); player.Register("Director.Camera", (_, _) => []);
        IEnumerable<AdvWait> Scene(AdvRow row, AdvPlayback p)
        {
            if (row.Data["Values"]?["background"] is JsonArray rgba)
                yield return tweens.To(() => alpha, float.Parse(rgba[3]!.ToString(), System.Globalization.CultureInfo.InvariantCulture),
                    row.Number("Duration"), AdvEase.Linear, value => alpha = value).Completed;
        }
        player.Register("Director.Scene", (row, p) => [p.NoWait(row, Scene(row, p))]);
        player.Register("Motion", (_, p) => { eventFrame = p.Loop.Frame; return []; });
        player.Start(); for (int frame = 0; frame < 40; frame++) player.Step();
        return eventFrame;
    }
    sealed class NativeProbe
    {
        public required AdvPlayback Player;
        public double X = .5, BackgroundAlpha;
        public Dictionary<string, int> Events = new(StringComparer.Ordinal);
    }
    static NativeProbe Playback(JsonObject root)
    {
        AdvRow[] Rows(string section) => root[section]!.AsArray().Select((row, index) => AdvProgram.Row(row!, index.ToString())).ToArray();
        var loop = new AdvLoop(); var program = new AdvProgram { Initialize = Rows("initialize"), Rows = Rows("commands"), Finalize = Rows("finalize") };
        var player = new AdvPlayback(loop, program) { Prepare = _ => [] };
        var probe = new NativeProbe { Player = player };
        player.Register("TalkWindow", (_, _) => []); player.Register("Character", (_, _) => []);
        IEnumerable<AdvWait> Values(AdvRow row, AdvPlayback playback)
        {
            // The timing under test is the production interpreter; compiled values must all be immediate.
            if (row.Number("Duration") != 0) throw new InvalidOperationException("Probe expects baked scalar rows.");
            DirectorValidation.Check(row);
            if (row.Command == "Director.Actor")
            {
                if (row.Data["Values"]?["position"] is JsonArray position) probe.X = position[0]!.GetValue<double>();
                if (row.Data["Values"]?["positionX"] is { } x) probe.X = x.GetValue<double>();
            }
            if (row.Command == "Director.Scene" && row.Data["Values"]?["background"] is JsonArray rgba) probe.BackgroundAlpha = rgba[3]!.GetValue<double>();
            yield break;
        }
        foreach (string command in new[] { "Director.Actor", "Director.Camera", "Director.Scene", "Director.Parameter" }) player.Register(command, Values);
        foreach (string command in new[] { "Motion", "Expression" }) player.Register(command, (row, playback) =>
        {
            probe.Events[row.Text(row.Command == "Motion" ? "MotionName" : "ExpressionName")] = playback.Loop.Frame; return [];
        });
        return probe;
    }
    static string ResolveBackground(SceneDocument document)
    {
        var row = document.Root["commands"]!.AsArray().OfType<JsonObject>()
            .First(value => value["cmd"]?.ToString() == "Director.Scene" && value["Values"]?["backgroundImage"] is not null);
        return document.ResolveAssetPath(row["Values"]!["backgroundImage"]!["path"]!.ToString());
    }
    static bool Near(double actual, double expected) => Math.Abs(actual - expected) < .00001;
    static bool Throws<T>(Action action) where T : Exception { try { action(); return false; } catch (T) { return true; } }
}
