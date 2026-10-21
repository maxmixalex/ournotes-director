using System.Security.Cryptography;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

/// <summary>Real native slot state and pixels across completed/in-flight focus,
/// departure, replacement, saved replay and backward reconstruction.</summary>
static class FocusStateNativeTests
{
    public static async Task<int> RunAsync(string fixture, string output)
    {
        fixture = Path.GetFullPath(fixture); output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var checks = new JsonArray(); var cases = new JsonArray();
        var report = new JsonObject { ["ok"] = false, ["checks"] = checks, ["cases"] = cases };
        void Check(bool value, string name)
        {
            checks.Add((JsonNode)new JsonObject { ["name"] = name, ["ok"] = value });
            if (!value) throw new InvalidOperationException(name);
            Console.WriteLine(name);
        }
        try
        {
            NativeTestGraphics.Configure(); var archive = new Archive(fixture);
            string core = NativeDependencies.CoreLibrary(baseDirectory: NativeTestGraphics.PackageDirectory);
            string ffmpeg = NativeDependencies.MediaTool("ffmpeg", baseDirectory: NativeTestGraphics.PackageDirectory);
            var manifest = archive.Manifest("intl", "stories", "10462");
            var definition = JsonNode.Parse(archive.Logical("intl", manifest, "story.json"))!;
            string model = definition["models"]!.AsObject().First().Value!.ToString();
            string stage = NativeStageCatalog.Read(archive, "intl").First().Key;
            foreach (string name in new[] { "native-out", "director-detach" })
            {
                var project = Project(model, stage, name == "director-detach");
                string path = Path.Combine(output, $"focus-{name}.project.json"), saved = project.ToJsonString();
                File.WriteAllText(path, saved);
                var reference = project.DeepClone().AsObject();
                reference["commands"] = new JsonArray(reference["commands"]!.AsArray()
                    .Where(row => !(row?["TargetName"]?.ToString() == "departing"
                        && row?["cmd"]?.ToString() is "DoF" or "Brightness"))
                    .Select(row => row!.DeepClone()).ToArray());
                string referencePath = Path.Combine(output, $"focus-{name}-reference.project.json");
                File.WriteAllText(referencePath, reference.ToJsonString());
                var observed = new Dictionary<int, Pixels>();
                try
                {
                    await Task.Run(() =>
                    {
                        using (var renderer = new AdvStoryRenderer(archive, "intl", core, DirectorProject.LibrarySource,
                            "zh-Hans", 320, 180, path, ffmpeg, 1))
                        {
                            _ = renderer.Frame(0, true);
                            Check(renderer.Scene.StageActors.Single().Blur == 0, $"{name}: a fresh scene starts with a sharp character");
                            observed[5] = renderer.Frame(5, true);
                            Check(renderer.Scene.StageActors.Single().Blur > .8f, $"{name}: completed DoF remains active after its transition duration");
                            foreach (int frame in new[] { 30, 60, 80, 90 })
                            {
                                var image = observed[frame] = renderer.Frame(frame, true);
                                File.WriteAllBytes(Path.Combine(output, $"focus-{name}-{frame}.png"), Png.Encode(image));
                                var states = renderer.Scene.StageActors;
                                Check(states.Count == 1 && states[0].Id == "replacement" && states[0].Slot == 3,
                                    $"{name}: frame {frame} shows the replacement in the original native slot");
                                if (frame == 80) Check(Math.Abs(states[0].Blur - .65f) < .0001f,
                                    $"{name}: a later explicit DoF still blurs the replacement");
                                else Check(states[0].Blur == 0, $"{name}: frame {frame} does not inherit the departed character's blur");
                            }
                        }
                        using (var renderer = new AdvStoryRenderer(archive, "intl", core, DirectorProject.LibrarySource,
                            "zh-Hans", 320, 180, referencePath, ffmpeg, 1))
                        {
                            Check(!renderer.Frame(5, true).Rgba.SequenceEqual(observed[5].Rgba),
                                $"{name}: the original blur/dimming changes real pixels before departure");
                            foreach (int frame in new[] { 30, 60, 80, 90 })
                                Check(renderer.Frame(frame, true).Rgba.SequenceEqual(observed[frame].Rgba),
                                    $"{name}: frame {frame} exactly matches a replay with no departed-actor effects");
                        }
                    });
                    Check(!observed[60].Rgba.SequenceEqual(observed[80].Rgba)
                        && observed[60].Rgba.SequenceEqual(observed[90].Rgba),
                        $"{name}: explicit blur-on then blur-zero changes and restores the complete native image");
                    await using var session = new RealtimePreviewSession();
                    await session.LoadSceneAsync(new(fixture, path, core, ffmpeg, 320, 180, 10));
                    await session.SeekAsync(90 / 30d);
                    Check(session.LastFrame!.Pixels.Rgba.SequenceEqual(observed[90].Rgba),
                        $"{name}: reopening the saved scene restores the same sharp pixels");
                    await session.SeekAsync(5 / 30d);
                    Check(session.LastFrame!.Pixels.Rgba.SequenceEqual(observed[5].Rgba),
                        $"{name}: backward reconstruction restores the earlier intentional blur");
                    await session.SeekAsync(60 / 30d);
                    Check(!session.IsPlaying && session.LastFrame!.Pixels.Rgba.SequenceEqual(observed[60].Rgba),
                        $"{name}: forward seeking clears departed state again and remains paused");
                    Check(File.ReadAllText(path) == saved, $"{name}: preview and replay preserve the original saved commands");
                    cases.Add((JsonNode)new JsonObject { ["case"] = name, ["model"] = model, ["stage"] = stage,
                        ["sharpRgbaSha256"] = Hash(observed[60]), ["blurredRgbaSha256"] = Hash(observed[80]),
                        ["restoredRgbaSha256"] = Hash(observed[90]) });
                }
                finally { File.Delete(referencePath); }
            }
            await Task.Run(() => CheckNamedFocus(archive, core, manifest, definition, model, output, Check, report));
            report["ok"] = true;
            Console.WriteLine($"Passed {checks.Count} real native focus state, pixels, departure and replay checks."); return 0;
        }
        catch (Exception error) { report["exception"] = error.ToString(); throw; }
        finally { File.WriteAllText(Path.Combine(output, "focus-state-report.json"), report.ToJsonString(new() { WriteIndented = true })); }
    }

    static JsonObject Project(string model, string stage, bool directorDetach)
    {
        var project = CreativeScene.Blank("intl", "zh-Hans").Compile();
        project["output"]!["width"] = 320; project["output"]!["height"] = 180;
        project["output"]!["content"] = "characters"; project["output"]!["supersample"] = 1;
        var rows = new JsonArray();
        void Row(int frame, JsonObject row) { row["AtFrame"] = frame; rows.Add((JsonNode)row); }
        Row(0, new() { ["cmd"] = "Stage", ["TargetAssetName"] = stage, ["Duration"] = 0 });
        foreach (string actor in new[] { "departing", "replacement" })
        {
            Row(0, new() { ["cmd"] = "Character", ["TargetName"] = actor, ["Model"] = model, ["Seed"] = 722 });
            Row(0, new() { ["cmd"] = "Director.Actor", ["TargetName"] = actor,
                ["Values"] = new JsonObject { ["controls"] = new JsonObject { ["physics"] = false, ["breath"] = false, ["blink"] = false } } });
        }
        Row(0, new() { ["cmd"] = "In", ["TargetName"] = "departing", ["PositionType"] = 3, ["Duration"] = 0 });
        Row(0, new() { ["cmd"] = "Pause", ["TargetName"] = "departing" });
        void Blur(int frame, string actor, double value, double duration) => Row(frame,
            new() { ["cmd"] = "DoF", ["TargetName"] = actor, ["PositionType"] = 3, ["CanvasLayers"] = new JsonArray(2),
                ["Parameter1"] = value, ["Parameter2"] = "Linear", ["Duration"] = duration });
        Blur(3, "departing", .9, 0);
        Row(3, new() { ["cmd"] = "Brightness", ["TargetName"] = "departing", ["PositionType"] = 3, ["Parameter1"] = .3, ["Duration"] = 0 });
        // Departure happens before these two-second writers finish. Merely
        // zeroing the slot at Out would allow them to contaminate its replacement.
        Blur(6, "departing", .4, 2);
        Row(6, new() { ["cmd"] = "Brightness", ["TargetName"] = "departing", ["PositionType"] = 3, ["Parameter1"] = .7, ["Duration"] = 2 });
        Row(12, directorDetach
            ? new() { ["cmd"] = "Director.Actor", ["TargetName"] = "departing", ["Values"] = new JsonObject { ["visible"] = false } }
            : new() { ["cmd"] = "Out", ["TargetName"] = "departing", ["PositionType"] = 3, ["Duration"] = 0 });
        Row(24, new() { ["cmd"] = "In", ["TargetName"] = "replacement", ["PositionType"] = 3, ["Duration"] = 0 });
        Row(24, new() { ["cmd"] = "Pause", ["TargetName"] = "replacement" });
        Blur(75, "replacement", .65, 0); Blur(81, "replacement", 0, 0);
        Row(96, new() { ["cmd"] = "Delay", ["Frames"] = 0 });
        project["commands"] = rows; return project;
    }

    static void CheckNamedFocus(Archive archive, string core, JsonNode manifest, JsonNode definition, string model,
        string output, Action<bool, string> check, JsonObject report)
    {
        var sourceScene = JsonNode.Parse(archive.Logical("intl", manifest, Archive.S(definition, "scene")))!;
        var settings = sourceScene["settings"]!["playerSettings"]!;
        var map = settings["_focusDataSettingsMap"]!["_list"]!.AsArray();
        // The downloaded story's profiles disable automatic character blur. Only
        // this in-memory test scene enables the native profile switch; archive
        // files and the production defaults remain untouched.
        foreach (var entry in map) entry!["Value"]!["_characterBlurEnabled"] = 1;
        var selectedStage = sourceScene["stages"]!.AsObject().First();
        var stage = new AdvPrefab(selectedStage.Value!).Find("AdvStage").Component;
        string key = Archive.S(stage, "_usingFocusDataSettingsKey");
        var profile = (map.FirstOrDefault(entry => Archive.S(entry!, "Key") == key)
            ?? map.First(entry => Archive.S(entry!, "Key") == Archive.S(settings, "_defaultFocusDataSettingsKey")))!["Value"]!;
        var distance = profile["_focusData"]!.AsArray().First(value => Actor.N(value!["_characterBlurIntensity"]) > 0)!;
        int cameraDistance = (int)Actor.N(distance["_cameraDistance"]); float intensity = Actor.N(distance["_characterBlurIntensity"]);
        var rows = new JsonArray();
        void Row(int frame, JsonObject row) { row["AtFrame"] = frame; rows.Add((JsonNode)row); }
        Row(0, new() { ["cmd"] = "Stage", ["TargetAssetName"] = selectedStage.Key, ["Duration"] = 0 });
        foreach (var (actor, position) in new[] { ("left", 3), ("right", 7) })
        {
            Row(0, new() { ["cmd"] = "Character", ["TargetName"] = actor, ["Model"] = model, ["Seed"] = 722 });
            Row(0, new() { ["cmd"] = "In", ["TargetName"] = actor, ["PositionType"] = position, ["Duration"] = 0 });
            Row(0, new() { ["cmd"] = "Pause", ["TargetName"] = actor });
        }
        void Focus(int frame, bool sharp) => Row(frame, new() { ["cmd"] = "Focus", ["TargetName"] = "right",
            ["PositionType"] = 0, ["CameraDistance"] = cameraDistance, ["Parameter1"] = sharp ? "sharp" : "", ["Duration"] = 0 });
        void Blur(int frame, string actor, float value) => Row(frame, new() { ["cmd"] = "DoF", ["TargetName"] = actor,
            ["CanvasLayers"] = new JsonArray(2), ["Parameter1"] = value, ["Duration"] = 0 });
        Focus(3, false); Blur(4, "left", intensity); Blur(4, "right", 0);
        Focus(5, true); Blur(6, "left", 0); Blur(6, "right", 0);
        Row(7, new() { ["cmd"] = "Delay", ["Frames"] = 0 });
        var program = new AdvProgram { Rows = rows.Select((row, index) => AdvProgram.Row(row!, index.ToString())).ToArray() };
        var loop = new AdvLoop(); var tweens = new AdvTweens(loop); using var gl = new OffscreenGl(320, 180);
        using var scene = new AdvScene(archive, "intl", core, manifest, definition, sourceScene, "zh-Hans", gl, loop, tweens);
        var player = new AdvPlayback(loop, program) { Prepare = _ => [] };
        scene.Preload(program.Rows); scene.Bind(player); player.Register("TalkWindow", (_, _) => []); player.Start();
        Pixels Frame(int frame)
        {
            while (loop.Frame < frame) player.Step();
            scene.DrawCharacters(); return gl.Present(320, 180, 1, 0, static (_, _, _) => { }, true, true);
        }
        var focused = Frame(3); var states = scene.StageActors;
        check(states.Single(actor => actor.Id == "right").Blur == 0
            && states.Single(actor => actor.Id == "left").Blur == intensity,
            "Focus TargetName selects the actual occupied slot even when PositionType is zero");
        check(Frame(4).Rgba.SequenceEqual(focused.Rgba),
            "Named Focus exactly matches explicit native blur commands for the intended actors");
        var sharp = Frame(5);
        check(scene.StageActors.All(actor => actor.Blur == 0) && !sharp.Rgba.SequenceEqual(focused.Rgba),
            "Focus sharp clears every character blur and changes the real rendered image");
        check(Frame(6).Rgba.SequenceEqual(sharp.Rgba),
            "Focus sharp exactly matches an explicit zero-blur reference at the same camera");
        File.WriteAllBytes(Path.Combine(output, "focus-named-target.png"), Png.Encode(focused));
        File.WriteAllBytes(Path.Combine(output, "focus-named-sharp.png"), Png.Encode(sharp));
        report["namedFocus"] = new JsonObject { ["nativeCharacterBlurEnabledForTest"] = true,
            ["cameraDistance"] = cameraDistance, ["intensity"] = intensity,
            ["focusedRgbaSha256"] = Hash(focused), ["sharpRgbaSha256"] = Hash(sharp) };
    }

    static string Hash(Pixels image) => Convert.ToHexString(SHA256.HashData(image.Rgba));
}
