using System.Text.Json.Nodes;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.Desktop.Tests;

static class CreativeExportTests
{
    internal static void Run(Action<bool, string> check)
    {
        var scene = CreativeScene.Blank("intl", "zh-Hans");
        // These are document-only identifiers; the test does not pretend to render a real asset.
        string actor = scene.AddActor("document-fixture-model", "Fixture");
        string motion = scene.AddClip(actor, "motion", 2, 3,
            new JsonObject { ["cmd"] = "Motion", ["TargetName"] = actor, ["MotionName"] = "document-fixture-motion" }, "Motion");
        string transform = scene.AddClip(actor, "transform", 3, 1,
            new JsonObject { ["cmd"] = "Director.Actor", ["TargetName"] = actor, ["Space"] = "screen", ["Duration"] = 1,
                ["Values"] = new JsonObject { ["position"] = new JsonArray(.25, .5) } }, "Placement");
        var compiled = scene.Compile();
        var native = SceneDocument.Parse(compiled.ToJsonString());
        string before = native.Text;
        var export = StudioSceneDocument.CreateExportView(native, scene.Duration + 1d / 30);
        check(JsonNode.DeepEquals(compiled, export.ToSceneDocument().Root),
            "Creative export retains every absolute clip row without recompiling sparse keys");
        check(native.Text == before, "Creating an export view never mutates the source scene");
        check(Math.Abs(export.Duration - 151d / 30) < 1e-8 && export.DurationMode == StudioDurationMode.Fixed,
            "Creative export includes the terminal pose and uses the authored range");
        check(export.ToSceneDocument().Root["commands"]!.AsArray().OfType<JsonObject>()
            .Single(row => row["cmd"]?.ToString() == "Motion")["AtFrame"]!.GetValue<int>() == 60,
            "Opening the output dialog preserves the motion's two-second start");
        scene.MoveClip(motion, 4);
        check(scene.Duration == 7 && scene.Compile()["commands"]!.AsArray().OfType<JsonObject>()
            .Single(row => row["cmd"]?.ToString() == "Motion")["AtFrame"]!.GetValue<int>() == 120,
            "Moving a creative clip changes native scheduling and content duration");
        check(JsonNode.DeepEquals(compiled, export.ToSceneDocument().Root),
            "Later creative edits cannot change an existing export snapshot");

        string folder = Path.Combine(Path.GetTempPath(), "creative-audio-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string input = Path.Combine(folder, "scene.json");
            File.WriteAllText(input, scene.Compile().ToJsonString());
            var source = new ScenePreviewSource(folder, input, "unused-core", "unused-ffmpeg", Duration: 8);
            check(!PreviewAudioIdentity.HasAudio(source), "Unvoiced authored scenes need no audio device or second interpreter");
            string audioKey = PreviewAudioIdentity.Key(source);
            scene.Actor(actor)["x"] = .7;
            scene.Clip(transform)["data"]!["Values"]!["position"]![0] = .8;
            File.WriteAllText(input, scene.Compile().ToJsonString());
            check(PreviewAudioIdentity.Key(source) == audioKey,
                "A placement edit does not invalidate a soundtrack when timing is unchanged");
            var voiced = scene.Compile();
            voiced["commands"]!.AsArray().Insert(0, new JsonObject { ["cmd"] = "Voice", ["AtFrame"] = 0,
                ["TargetName"] = actor, ["VoiceIDs"] = new JsonArray(1L) });
            File.WriteAllText(input, voiced.ToJsonString());
            check(PreviewAudioIdentity.HasAudio(source) && PreviewAudioIdentity.Key(source) != audioKey,
                "Adding a real audio command invalidates the silent-preview identity");
        }
        finally { Directory.Delete(folder, true); }
    }
}
