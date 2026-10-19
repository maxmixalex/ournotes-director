using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.Desktop.Tests;

static class CreativeSourceImporterTests
{
    public static void Run(Action<bool, string> check)
    {
        string folder = Path.Combine(Path.GetTempPath(), "ournotes-source-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            var archive = Fixture(folder, 1600);
            const string source = """
                {"format":"ournotes-director/project","version":1,
                 "source":{"adv":"42","site":"intl","language":"zh-Hans"},
                 "output":{"width":960,"height":540,"supersample":1},
                 "initialize":null,"finalize":[],"text":{"line":{"simplifiedChinese":"项目覆盖"}}}
                """;
            var baseline = PreviousPreparation(source, folder, archive);
            var prepared = CreativeSourceImporter.Prepare(source, folder, archive);
            string expected = baseline.Document.Root.ToJsonString();
            check(prepared.Document.Root.ToJsonString() == expected
                && prepared.ActorModels.OrderBy(pair => pair.Key).SequenceEqual(baseline.ActorModels.OrderBy(pair => pair.Key)),
                "Direct story preparation equals the existing Studio path's expanded JSON and actor/model associations");
            var root = prepared.Document.Root;
            check(root["finalize"]!.AsArray().Count == 0
                && root["text"]!["line"]!["simplifiedChinese"]!.ToString() == "项目覆盖"
                && root["text"]!["line"]!["english"] is null
                && root["text"]!["source-only"]!["english"]!.ToString() == "kept",
                "Source preparation preserves explicit empty sections and whole-entry dialogue overrides");
            check(root["initialize"]![1]!["large"]!.ToJsonString() == "123456789012345678901234567890"
                && root["commands"]![1]!["originalNumber"]!.ToJsonString() == "1.2300e-7"
                && root["commands"]![1]!["opaque"]!.ToJsonString() == "1e999"
                && root["initialize"]![1]!["Model"] is null,
                "Source preparation retains native enum rows, numeric lexemes and original model addresses without injecting Model fields");
            check(prepared.ActorModels["source-target"] == "verified-model-id"
                && prepared.ActorModels["explicit-target"] == "explicit-model"
                && !prepared.ActorModels.ContainsKey("ignored-target")
                && prepared.ActorModels["unresolved-target"] == "",
                "Actor discovery preserves first-enabled definitions, explicit models, ignored rows and unresolved addresses");
            var timing = Timing(root);
            var expectedScene = baseline.CreateScene(timing);
            var actualScene = prepared.CreateScene(timing);
            check(WithoutIds(actualScene) == WithoutIds(expectedScene)
                && actualScene.Compile().ToJsonString() == expectedScene.Compile().ToJsonString(),
                "Identical native timing produces identical editable clips and compiled command order after direct preparation");
            check(prepared.Document.Root.ToJsonString() == expected,
                "Editable conversion leaves the prepared source document and exact source tokens intact");

            string expandedJson = prepared.Document.Text;
            var expanded = CreativeSourceImporter.Prepare(expandedJson, folder, archive);
            check(expanded.Document.Text == expandedJson
                && expanded.ActorModels.OrderBy(pair => pair.Key).SequenceEqual(prepared.ActorModels.OrderBy(pair => pair.Key)),
                "An already-expanded story resolves its actor metadata without reformatting or editing saved JSON");
            using (var snapshot = prepared.Document.CreateSnapshot(Path.Combine(folder, "snapshots")))
            {
                var render = DirectorProject.Document(snapshot.FilePath);
                check(render["commands"]![0]!["Values"]!["backgroundImage"]!["path"]!.ToString() == Path.Combine(folder, "background.png")
                    && prepared.Document.Root["commands"]![0]!["Values"]!["backgroundImage"]!["path"]!.ToString() == "background.png",
                    "Source snapshots anchor local assets while the editable source retains relative references");
            }
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                check(Throws<OperationCanceledException>(() => CreativeSourceImporter.Prepare("invalid JSON", folder, archive, cancellation.Token))
                    && Throws<OperationCanceledException>(() => prepared.CreateScene(timing, cancellation.Token))
                    && Throws<OperationCanceledException>(() => CreativeScene.FromPerformance(new JsonObject(), timing, cancellation.Token)),
                    "Cancelled source preparation and conversion stop before parsing or producing any editable result");
            }

            // Both paths are warmed above and read the exact same verified archive.
            // This baseline reproduces the current branch's import preparation,
            // including its extra model read and final SceneDocument parse.
            var previous = Enumerable.Range(0, 3).Select(_ => Measure(() => PreviousPreparation(source, folder, archive))).ToArray();
            var direct = Enumerable.Range(0, 3).Select(_ => Measure(() => CreativeSourceImporter.Prepare(source, folder, archive))).ToArray();
            long beforeBytes = previous.Select(sample => sample.Bytes).Order().ElementAt(1);
            long afterBytes = direct.Select(sample => sample.Bytes).Order().ElementAt(1);
            check(afterBytes < beforeBytes,
                "Direct source preparation allocates less than the former Studio construction, static timing and sampled actor pipeline");
            Console.WriteLine($"STORY_PREPARATION_BENCHMARK rows=1600 previousMedianMs={previous.Select(sample => sample.Milliseconds).Order().ElementAt(1):F3} directMedianMs={direct.Select(sample => sample.Milliseconds).Order().ElementAt(1):F3} previousAllocatedBytes={beforeBytes} directAllocatedBytes={afterBytes}");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    static CreativeSourceImport PreviousPreparation(string json, string folder, Archive archive)
    {
        var document = SceneDocument.Parse(json, baseDirectory: folder);
        if (document.Validate().HasErrors) throw new InvalidDataException("Invalid benchmark project.");
        var previous = StudioSceneDocument.FromSceneDocument(document);
        previous.ExpandInheritedSections(archive); previous.ResolveSourceModels(archive);
        var result = previous.ToSceneDocument();
        return new(result, previous.Actors.ToDictionary(actor => actor.Id, actor => actor.Model, StringComparer.Ordinal));
    }

    static (double Milliseconds, long Bytes) Measure(Func<CreativeSourceImport> action)
    {
        long before = GC.GetAllocatedBytesForCurrentThread(), started = Stopwatch.GetTimestamp();
        var result = action();
        var sample = (Stopwatch.GetElapsedTime(started).TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - before);
        GC.KeepAlive(result); return sample;
    }

    static NativePlaybackTiming Timing(JsonObject root)
    {
        var timing = new List<NativeCommandTiming>(); int frame = 0;
        foreach (string section in new[] { "initialize", "commands", "finalize" })
        {
            var rows = root[section]!.AsArray();
            for (int i = 0; i < rows.Count; i++)
            {
                var row = AdvProgram.Row(rows[i]!, section + i);
                if (row.Ignored) continue;
                timing.Add(new(section, i, row.Command, frame, frame + 3, timing.Count, "")); frame += 3;
            }
        }
        return new("fixture-timing", true, frame, (frame + 1) / 30d, timing);
    }

    static string WithoutIds(CreativeScene scene)
    {
        var root = scene.Root.DeepClone();
        foreach (var clip in root["clips"]!.AsArray().OfType<JsonObject>()) clip.Remove("id");
        return root.ToJsonString();
    }

    static bool Throws<T>(Action action) where T : Exception
    { try { action(); return false; } catch (T) { return true; } }

    static Archive Fixture(string folder, int count)
    {
        var rows = new JsonArray { new JsonObject { ["cmd"] = "Director.Scene", ["Values"] = new JsonObject
            { ["backgroundImage"] = new JsonObject { ["path"] = "background.png" } } } };
        for (int i = 0; i < count; i++)
            rows.Add(new JsonObject { ["cmd"] = i % 3 == 0 ? "Motion" : i % 3 == 1 ? "Expression" : "Delay",
                ["TargetName"] = "source-target", ["MotionName"] = "motion", ["ExpressionName"] = "expression",
                ["Duration"] = 1.25, ["originalNumber"] = JsonNode.Parse("1.2300e-7"), ["opaque"] = JsonNode.Parse("1e999") });
        var episode = new JsonObject { ["commands"] = rows, ["text"] = JsonNode.Parse("""
            {"line":{"simplifiedChinese":"源对白","english":"source"},"source-only":{"english":"kept"}}
            """) };
        var settings = JsonNode.Parse("""
            {"_initializeEpisodes":[
              {"Command":23,"TargetName":"ignored-target","Model":"ignored-model","IgnoreData":1},
              {"Command":23,"TargetName":"source-target","TargetAssetName":"source-address","large":123456789012345678901234567890},
              {"raw":23,"TargetName":"explicit-target","Model":"explicit-model","TargetAssetName":"source-address"},
              {"cmd":"Character","TargetName":"source-target","TargetAssetName":"different-address"},
              {"cmd":"Character","TargetName":"unresolved-target","TargetAssetName":"missing"}],
             "_finalizeEpisodes":[{"cmd":"Character","TargetName":"finalized-target","Model":"finalized-model"}],
             "_waitCommandLingeringTimeOnAutoPlay":0.5}
            """)!.AsObject();
        var manifest = new JsonObject { ["files"] = new JsonObject() };
        Directory.CreateDirectory(Path.Combine(folder, "index"));
        File.WriteAllText(Path.Combine(folder, "index", "stats.json"), "{}");
        Add("story.json", new JsonObject { ["episode"] = "episode.json", ["scene"] = "scene.json", ["models"] = new JsonObject
            { ["Character/Live2D/source-address"] = "verified-model-id", ["Character/Live2D/different-address"] = "second-model" } });
        Add("episode.json", episode);
        Add("scene.json", new JsonObject { ["settings"] = new JsonObject { ["playerSettings"] = settings } });
        string stories = Path.Combine(folder, "sites", "intl", "stories"); Directory.CreateDirectory(stories);
        File.WriteAllText(Path.Combine(stories, "42.json"), manifest.ToJsonString());
        return new(folder);

        void Add(string logical, JsonObject value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value.ToJsonString()); string asset = "assets/" + Archive.Hash(bytes) + ".json";
            string path = Path.Combine(folder, "sites", "intl", asset); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            manifest["files"]![logical] = new JsonObject { ["asset"] = asset, ["size"] = bytes.Length, ["stored"] = bytes.Length };
        }
    }
}
