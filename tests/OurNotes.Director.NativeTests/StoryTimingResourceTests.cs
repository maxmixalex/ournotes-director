using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

// The production timing shortcut must preserve the capture/fade waits, even
// though it no longer creates model drawing resources or captures scene pixels.
static class StoryTimingResourceTests
{
    public static int Run(string fixture, string output)
    {
        string reportPath = Path.Combine(output, "report.json");
        var report = JsonNode.Parse(File.ReadAllText(reportPath))!.AsObject();
        var checks = report["checks"]!.AsArray();
        var samples = new JsonArray(); report["modelResources"] = samples; report["ok"] = false;
        void Check(bool value, string name)
        {
            checks.Add((JsonNode)new JsonObject { ["name"] = name, ["ok"] = value });
            if (!value) throw new InvalidOperationException(name);
        }
        try
        {
            NativeTestGraphics.Configure();
            string core = NativeDependencies.CoreLibrary(baseDirectory: NativeTestGraphics.PackageDirectory);
            string ffmpeg = NativeDependencies.MediaTool("ffmpeg", baseDirectory: NativeTestGraphics.PackageDirectory);
            var archive = new Archive(fixture);
            var manifest = archive.Manifest("intl", "stories", "10462");
            var definition = JsonNode.Parse(archive.Logical("intl", manifest, "story.json"))!;
            var episode = JsonNode.Parse(archive.Logical("intl", manifest, Archive.S(definition, "episode")))!;
            var scene = JsonNode.Parse(archive.Logical("intl", manifest, Archive.S(definition, "scene")))!;
            var program = AdvProgram.From(episode, scene["settings"]!["playerSettings"]!);
            var sourceCharacter = program.Initialize.Concat(program.Rows).Concat(program.Finalize)
                .First(row => !row.Ignored && row.Command == "Character" && row.Number("TargetAssetIndex") == 0);
            string model = sourceCharacter.Text("Model");
            if (model.Length == 0) model = definition["models"]!["Character/Live2D/" + sourceCharacter.Text("TargetAssetName")]!.GetValue<string>();
            var project = JsonNode.Parse("""
                {"format":"ournotes-director/project","version":1,
                 "source":{"adv":"10462","site":"intl","language":"zh-Hans"},
                 "output":{"width":960,"height":540,"letterbox":"none"},
                 "initialize":[],"finalize":[],"text":{},"commands":[
                   {"cmd":"Character","TargetName":"timing-actor","Model":""},
                   {"cmd":"In","TargetName":"timing-actor","PositionType":3},
                   {"cmd":"RimLight","TargetName":"timing-actor","Parameter1":"#ffaaaa","Parameter2":"0.5"},
                   {"cmd":"Stage","TargetAssetName":"","Duration":0.1},
                   {"cmd":"Delay","Frames":3},
                   {"cmd":"Stage","TargetAssetName":"","Duration":0.2},
                   {"cmd":"Out","TargetName":"timing-actor","PositionType":3,"Duration":0.1},
                   {"cmd":"Delay","Frames":1}]}
                """)!.AsObject();
            project["commands"]![0]!["Model"] = model;
            string json = project.ToJsonString(), hash = Archive.Hash(Encoding.UTF8.GetBytes(json));
            string path = Path.Combine(output, "capture-wait.project.json"); File.WriteAllText(path, json);
            NativePlaybackTiming? reference = null;
            foreach (bool timingOnly in new[] { false, true })
            {
                var watch = Stopwatch.StartNew();
                using var native = new AdvStoryRenderer(archive, "intl", core, "10462", "zh-Hans",
                    960, 540, path, ffmpeg, 1, project, timingOnly);
                double initialized = watch.Elapsed.TotalMilliseconds;
                var actor = native.Scene.Character("timing-actor");
                Check(actor is not null && actor.HasGameDrawingResources == !timingOnly,
                    timingOnly ? "Timing-only actors retain native animation but allocate no model textures or drawing meshes"
                        : "The full-renderer reference allocates the real model drawing resources");
                var timing = native.AnalyzeTiming(hash);
                samples.Add((JsonNode)new JsonObject { ["timingOnly"] = timingOnly, ["initializationMs"] = initialized,
                    ["totalMs"] = watch.Elapsed.TotalMilliseconds, ["endFrame"] = timing.EndFrame,
                    ["commands"] = timing.Commands.Count, ["modelDrawingAllocated"] = actor!.HasGameDrawingResources });
                Check(timing.IsComplete && timing.Commands.Count(row => row.Command == "Stage") == 2,
                    "Both native capture-dependent Stage fades finish " + (timingOnly ? "without GPU drawing" : "with actual GPU captures"));
                if (reference is not null)
                    Check(timing.ProjectHash == reference.ProjectHash && timing.EndFrame == reference.EndFrame
                        && timing.Duration == reference.Duration && timing.Commands.SequenceEqual(reference.Commands),
                        "Skipping capture pixels preserves every native command wait, ordinal and endpoint, including consecutive Stage fades");
                reference = timing;
            }
            Check(File.ReadAllText(path) == json, "Timing resource optimization does not rewrite its source project");
            report["ok"] = true;
            Console.WriteLine("Passed 6 native model-resource and capture-wait parity checks.");
            return 0;
        }
        finally { File.WriteAllText(reportPath, report.ToJsonString(new() { WriteIndented = true })); }
    }
}
