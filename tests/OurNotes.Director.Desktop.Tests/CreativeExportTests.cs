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

        var voices = CreativeScene.Blank("intl", "zh-Hans");
        string voice = voices.AddClip("", "voice", 2, 4,
            new JsonObject { ["cmd"] = "Voice", ["AudioPath"] = "fixture.wav", ["AudioIn"] = 1, ["AudioDuration"] = 4 }, "Voice");
        voices.Clip(voice)["sourceDuration"] = 10;
        string voiceSource = voices.ToJson();
        var trimmed = CreativeClipEditing.TrimStart(voices, voice, 3).Scene;
        check(CreativeScene.Number(trimmed.Clip(voice), "start") == 3 && CreativeScene.Number(trimmed.Clip(voice), "duration") == 3
            && CreativeScene.Number(trimmed.Clip(voice)["data"]!.AsObject(), "AudioIn") == 2,
            "Trimming a voice start changes the actual PCM in point while preserving the output end");
        check(voices.ToJson() == voiceSource, "Trimming returns a separate document for atomic undo");
        var split = CreativeClipEditing.Split(voices, voice, 4);
        var right = split.Scene.Clip(split.Selection); var left = split.Scene.Clip(voice);
        check(CreativeScene.Number(left, "duration") == 2 && CreativeScene.Number(right, "start") == 4
            && CreativeScene.Number(right["data"]!.AsObject(), "AudioIn") == 3
            && CreativeScene.Number(left["data"]!.AsObject(), "AudioDuration") == 2
            && CreativeScene.Number(right["data"]!.AsObject(), "AudioDuration") == 2,
            "Splitting voices produces contiguous, non-repeated source audio ranges");
        check(split.Scene.Duration == voices.Duration && voices.ToJson() == voiceSource,
            "Voice splitting changes neither the source scene nor total performance length");
        bool rejected = false;
        try { CreativeClipEditing.TrimStart(voices, voice, 0); } catch (ArgumentException) { rejected = true; }
        check(rejected && voices.ToJson() == voiceSource, "Trimming beyond the source audio start is rejected without modifying the scene");
        check(!CreativeClipEditing.CanSplit(voices.Clip(voice), 2) && !CreativeClipEditing.CanSplit(voices.Clip(voice), 6),
            "Splitting at either clip boundary cannot create zero-length audio");
        string hold = voices.AddClip("", "hold", 6, 2, new JsonObject { ["cmd"] = "Delay", ["Frames"] = 0 }, "Hold");
        var splitHold = CreativeClipEditing.Split(voices, hold, 7);
        check(splitHold.Scene.Duration == 8 && splitHold.Scene.Clips.Count == voices.Clips.Count + 1,
            "Visible holds can be split like audio without an invisible duration constraint");

        var dialogue=CreativeScene.Blank("intl","zh-Hans");
        string line=dialogue.AddClip("speaker","voice",0,1,new JsonObject{["cmd"]="Talk",["AdvTextID"]="shared"},"Original");
        string copy=dialogue.DuplicateClip(line);
        dialogue.Root["text"]=new JsonObject{["shared"]=new JsonObject{["simplifiedChinese"]="原台词",["japanese"]="元の台詞"}};
        CreativeDialogue.Write(dialogue,line,"新台词");
        check(CreativeDialogue.Read(dialogue,line)=="新台词"&&CreativeDialogue.Read(dialogue,copy)=="原台词",
            "Editing an imported or copied dialogue line forks only that clip's text");
        string owned=dialogue.Clip(line)["data"]!["AdvTextID"]!.ToString();
        CreativeDialogue.Write(dialogue,line,"再次修改");
        check(dialogue.Clip(line)["data"]!["AdvTextID"]!.ToString()==owned&&dialogue.Root["text"]![owned]!["japanese"]!.ToString()=="元の台詞",
            "Repeated dialogue edits reuse the private text row and preserve other languages");
        string repeated=dialogue.DuplicateClip(line);CreativeDialogue.Write(dialogue,repeated,"另一份");
        check(CreativeDialogue.Read(dialogue,line)=="再次修改"&&CreativeDialogue.Read(dialogue,repeated)=="另一份",
            "Duplicating an already edited line does not create shared mutable dialogue");
        var all=scene.Compile();scene.Canvas["content"]="characters";var characterOutput=scene.Compile();
        check(JsonNode.DeepEquals(all["commands"],characterOutput["commands"])&&characterOutput["output"]!["content"]!.ToString()=="characters",
            "Only-character transparent output never rewrites source timing or actor placement instructions");
        check(!SceneDocument.Parse(characterOutput.ToJsonString()).Validate().HasErrors,
            "Character-only presentation survives the same JSON validation used by export");

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
