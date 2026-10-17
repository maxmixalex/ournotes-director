using System.Text.Json.Nodes;
using OurNotes.Director.Core;
using OurNotes.Director.Core.Archiving;

static class CreativeStagingNativeTests
{
    public static async Task<int> RunAsync(string fixture, string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var checks = new JsonArray(); var report = new JsonObject { ["ok"] = false, ["checks"] = checks };
        void Check(bool pass, string name)
        { checks.Add((JsonNode)new JsonObject { ["name"] = name, ["ok"] = pass }); if (!pass) throw new InvalidOperationException(name); }
        try
        {
            await PrepareFixtureLibraryAsync(fixture);
            NativeTestGraphics.Configure(); var archive = new Archive(fixture);
            var manifest = archive.Manifest("intl", "stories", "10462");
            var definition = JsonNode.Parse(archive.Logical("intl", manifest, "story.json"))!;
            var episode = JsonNode.Parse(archive.Logical("intl", manifest, "episode.json"))!;
            var models = definition["models"]!.AsObject().Select(p => p.Value!.ToString()).Distinct().Take(2).ToArray();
            string core = NativeDependencies.CoreLibrary(baseDirectory: NativeTestGraphics.PackageDirectory), ffmpeg = NativeDependencies.MediaTool("ffmpeg", baseDirectory: NativeTestGraphics.PackageDirectory);
            var stage = NativeStageCatalog.Read(archive, "intl").First();
            await using (var previews = new StagePreviewSession(archive, "intl", "10462", "zh-Hans"))
            {
                var first = await previews.GetAsync(stage.Key);
                var cached = await previews.GetAsync(stage.Key);
                var thumbnail = Png.Decode(first);
                Check(ReferenceEquals(first, cached) && thumbnail.Width == 320 && thumbnail.Height == 180,
                    "Stage browser caches actual small native images without loading the source performance");
                File.WriteAllBytes(Path.Combine(output, "stage-thumbnail.png"), first);
            }
            var scene = CreativeScene.Blank("intl", "zh-Hans"); scene.Canvas["width"] = 640; scene.Canvas["height"] = 360;
            CreativeStaging.SetStage(scene, stage.Key, stage.Label);
            string a = scene.AddActor(models[0], "前景人物"), b = scene.AddActor(models[1], "后景人物");
            scene.Actor(a)["depth"] = -.6; scene.Actor(b)["depth"] = .8;
            var model = archive.Model("intl", models[0]);
            string motion = model["motions"]!.AsArray().Select(m => m!.ToString()).First(m => m != model["defaultMotion"]?.ToString());
            string expression = model["expressions"]!.AsArray().Select(m => m!.ToString()).First(m => m != model["defaultExpression"]?.ToString());
            var speech = episode["commands"]!.AsArray().First(r => r?["VoiceIDs"] is JsonArray { Count: > 0 })!;
            var cue = AdvAudio.Decode(archive, "intl", manifest, episode, speech["VoiceIDs"]![0]!.GetValue<long>(), ffmpeg);
            string wav = Path.Combine(output, "original-voice.wav"); WriteWave(wav, cue);
            string authoredMotion = scene.AddClip(a, "motion", 1, 2, new JsonObject { ["cmd"] = "Motion", ["MotionName"] = motion, ["MotionFadeIn"] = -1 }, "原动作");
            CreativeMaterialEditing.SetKeepsExpression(scene, authoredMotion, true);
            scene.AddClip(a, "expression", .2, 2.8, new JsonObject { ["cmd"] = "Expression", ["ExpressionName"] = expression, ["MotionFadeIn"] = -1 }, "表情");
            var voice = new JsonObject { ["cmd"] = "Voice", ["TargetName"] = a, ["AudioPath"] = wav, ["AudioIn"] = 0, ["AudioDuration"] = Math.Min(3, cue.Samples / (double)AdvAudio.SampleRate), ["IgnoreLipSync"] = false };
            scene.AddClip(a, "voice", 0, Math.Min(3, cue.Samples / (double)AdvAudio.SampleRate), voice, "原语音");
            CreativeStaging.AddFocus(scene, a, 0, .3); CreativeStaging.AddFocus(scene, b, 2, .3);
            var compiled = scene.Compile(); var rows = new List<JsonNode>();
            void Row(int frame, JsonObject row) { row["AtFrame"] = frame; rows.Add(row); }
            Row(0, new() { ["cmd"] = "Stage", ["TargetAssetName"] = stage.Key, ["Duration"] = 0 });
            foreach (var (id, asset, slot, depth) in new[] { (a, models[0], 3, -.6), (b, models[1], 7, .8) })
            {
                Row(0, new() { ["cmd"] = "Character", ["TargetName"] = id, ["Model"] = asset });
                Row(0, new() { ["cmd"] = "In", ["TargetName"] = id, ["PositionType"] = slot, ["Duration"] = 0 });
                Row(0, new() { ["cmd"] = "MoveToDirection", ["TargetName"] = id, ["PositionType"] = slot, ["Parameter1"] = 0, ["Parameter2"] = 0, ["Parameter3"] = depth, ["Duration"] = 0 });
            }
            Row(0, (JsonObject)voice.DeepClone());
            foreach (int frame in new[] { 0, 60 })
                foreach (var (id, slot, blur, layer) in new[] { (a, 3, frame == 0 ? 0d : 1, 2), (b, 7, frame == 0 ? 1d : 0, 2), ("", 0, .5, 0) })
                    Row(frame, new() { ["cmd"] = "DoF", ["TargetName"] = id, ["PositionType"] = slot, ["Parameter1"] = blur, ["CanvasLayers"] = new JsonArray(layer), ["Duration"] = .3, ["Parameter2"] = "InOutQuad" });
            Row(6, new() { ["cmd"] = "Expression", ["TargetName"] = a, ["ExpressionName"] = expression, ["MotionFadeIn"] = -1 });
            Row(30, new() { ["cmd"] = "Motion", ["TargetName"] = a, ["MotionName"] = motion, ["ExpressionName"] = expression, ["MotionFadeIn"] = -1 });
            Row(90, new() { ["cmd"] = "Delay", ["Frames"] = 0 });
            var reference = (JsonObject)compiled.DeepClone(); reference["commands"] = new JsonArray(rows.OrderBy(r => (int)Actor.N(r["AtFrame"])).ToArray());
            string project = Path.Combine(output, "authored.scene.json"), nativeProject = Path.Combine(output, "reference.project.json");
            File.WriteAllText(project, scene.ToJson()); File.WriteAllText(nativeProject, reference.ToJsonString());
            var frames = new[] { 15, 35, 75 }; var observed = new Dictionary<int, Pixels>(); var mouth = new List<float>();
            using (var renderer = new AdvStoryRenderer(archive, "intl", core, DirectorProject.LibrarySource, "zh-Hans", 640, 360, project, ffmpeg, 1))
            {
                for (int frame = 0; frame <= 90; frame++)
                {
                    var pixels = renderer.Frame(frame, true); mouth.Add(renderer.Scene.Character(a)!.MouthOpen);
                    if (!frames.Contains(frame)) continue;
                    observed[frame] = pixels; File.WriteAllBytes(Path.Combine(output, $"stage-{frame}.png"), Png.Encode(pixels));
                    var states = renderer.Scene.StageActors;
                    Check(states.Count == 2 && states.Single(s => s.Id == a).Slot == 3 && states.Single(s => s.Id == b).Slot == 7, $"Frame {frame}: both characters retain native slots, including after a motion change");
                    Check(states.Single(s => s.Id == a).WorldZ < states.Single(s => s.Id == b).WorldZ, $"Frame {frame}: foreground/background are actual spatial depth");
                    if (frame == 15) Check(states.Single(s => s.Id == a).Blur == 0 && states.Single(s => s.Id == b).Blur == 1, "First focus makes the rear actor defocused");
                    if (frame >= 35) Check(states.Single(s => s.Id == a).Expression == expression && states.Single(s => s.Id == a).Motion == motion,
                        $"Frame {frame}: inserted Motion preserves the earlier expression through existing native command parameters");
                    if (frame == 75) Check(states.Single(s => s.Id == a).Blur == 1 && states.Single(s => s.Id == b).Blur == 0, "Focus transfer swaps the native blur layers without moving either actor");
                }
            }
            using (var original = new AdvStoryRenderer(archive, "intl", core, DirectorProject.LibrarySource, "zh-Hans", 640, 360, nativeProject, ffmpeg, 1))
                foreach (int frame in frames) Check(original.Frame(frame, true).Rgba.SequenceEqual(observed[frame].Rgba), $"Frame {frame}: GUI-authored model compiles to the same pixels as independently specified native staging rows");
            Check(mouth.Max() - mouth.Min() > .01, "Actual displayed mouth parameters vary while the real voice plays");
            Check(scene.Source["adv"] is null, "The authored scene does not require importing an original story");
            report["mouthRange"] = mouth.Max() - mouth.Min(); report["modelA"] = models[0]; report["modelB"] = models[1]; report["stage"] = stage.Key;
            report["comparisonScope"] = "native interpreter equivalence; not a pixel comparison against a recording of the original game";
            StagePlacementNativeTests.Run(archive, core, ffmpeg, project, output, a, Check);
            CreativeStagingExports.Run(archive, core, ffmpeg, project, output, a, Check);
            report["ok"] = true; Console.WriteLine($"Passed {checks.Count} real native staging checks."); return 0;
        }
        finally { File.WriteAllText(Path.Combine(output, "report.json"), report.ToJsonString()); }
    }
    static async Task PrepareFixtureLibraryAsync(string fixture)
    {
        // The downloaded fixture has the complete site's index but only selected story
        // manifests. Restrict only this test copy while building the creative library.
        string indexPath = Path.Combine(fixture, "sites", "intl", "stories.json");
        byte[] original = File.ReadAllBytes(indexPath);
        string manifestPath = Path.Combine(fixture, "sites", "intl", "stories", "10462.json");
        byte[] originalManifest = File.ReadAllBytes(manifestPath);
        if (!File.Exists(Path.Combine(fixture, "studio-fixture.json")))
            throw new InvalidOperationException("This operation requires a dedicated test fixture, not a user archive.");
        try
        {
            var index = JsonNode.Parse(original)!.AsObject();
            index["stories"] = new JsonArray(index["stories"]!.AsArray()
                .Where(item => File.Exists(Path.Combine(fixture, "sites", "intl", item!["manifest"]!.ToString())))
                .Select(item => item!.DeepClone()).ToArray());
            File.WriteAllText(indexPath, index.ToJsonString());
            var manifest = JsonNode.Parse(originalManifest)!.AsObject();
            // Only zh-Hans language variants are downloaded by fetch-studio-fixture.py.
            manifest["languages"] = new JsonObject { ["zh-Hans"] = manifest["languages"]!["zh-Hans"]!.DeepClone() };
            File.WriteAllText(manifestPath, manifest.ToJsonString());
            await DirectorPackBuilder.BuildAsync(fixture, "intl", 10462);
        }
        finally { File.WriteAllBytes(indexPath, original); File.WriteAllBytes(manifestPath, originalManifest); }
    }
    static void WriteWave(string path, AdvCue cue)
    {
        using var w = new BinaryWriter(File.Create(path)); int bytes = cue.Pcm.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)cue.Channels);
        w.Write(48000); w.Write(48000 * cue.Channels * 2); w.Write((short)(cue.Channels * 2)); w.Write((short)16); w.Write("data"u8); w.Write(bytes);
        foreach (float sample in cue.Pcm) w.Write((short)Math.Clamp((int)MathF.Round(sample * 32767), -32768, 32767));
    }
}
