using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;
using OurNotes.Director.Core.Archiving;

internal static class ArchiveIndexTests
{
    public static async Task RunAsync(Action<bool, string> assert)
    {
        var root = Path.Combine(Path.GetTempPath(), "ournotes-index-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var intl = new FixtureSite(root, "intl");
            var jp = new FixtureSite(root, "jp");
            var modelFiles = new JsonObject
            {
                ["model.json"] = intl.Put("""{"prefab":"prefab.json"}""", "br"),
                ["prefab.json"] = intl.Put("""
                    {"nodes":[{"components":[
                      {"class":"Live2DCharacter","DefaultMotionName":"","_motionList":[{"clip":"idle"},{"name":"wave"},{"MotionName":"idle"}]},
                      {"class":"CubismExpressionController","ExpressionsList":{"CubismExpressionObjects":[{"name":"smile.exp3"},{"name":"surprise.EXP3"}]}}
                    ]}]}
                    """, "gz"),
            };
            intl.Write("models/adv_live2d_test.json", new JsonObject
            {
                ["key"] = "test-model", ["model"] = JsonNode.Parse("""{"character":3,"names":{"en":"Test"},"label":"Costume"}"""), ["files"] = modelFiles,
            });

            var modelMap = JsonNode.Parse("""{"Character/Live2D/Actor":"adv_live2d_test"}""")!;
            var modelManifests = JsonNode.Parse("""{"adv_live2d_test":"models/adv_live2d_test.json"}""")!;
            var stagePart = intl.Put("""
                {"stage_a":{"nodes":[{"components":[{"class":"AdvStage","_fov":43,"_initialCameraPosition":{"x":0,"y":1,"z":2},"_initialCameraRotation":{"x":0,"y":0,"z":0},"_usingFocusDataSettingsKey":"focus_a"}]}]}}
                """, "gz");
            var sceneEntry = new JsonObject { ["parts"] = new JsonArray(
                Part("stages", stagePart), Part("shaders", intl.Put("""{"names":["Shader/A"]}""", "br")), Part("infinity", intl.Put("1e999", "gz"))) };
            var voice = intl.Put("audio-en", "", "m4a");
            var unusedVoice = intl.Put("audio-zh", "", "m4a");
            var cue = intl.Put("""{"file":"voice.m4a","sampleRate":48000,"channels":1,"samples":96001}""", "br");
            var languageGroups = new JsonObject
            {
                ["zh-Hans"] = new JsonObject { ["files"] = new JsonObject
                {
                    ["audio/main/cues.json"] = intl.Put("""{"voice":{"file":"voice.m4a","samples":1}}""", "gz"),
                    ["audio/main/voice.m4a"] = unusedVoice,
                } },
                ["en"] = new JsonObject { ["files"] = new JsonObject
                {
                    ["audio/main/cues.json"] = new JsonObject { ["parts"] = new JsonArray(Part("voice", cue)) },
                    ["audio/main/voice.m4a"] = voice,
                } },
            };
            var episodeEntry = intl.Put("""
                {"advId":42,"master":{},"text":{"t1":{"japanese":"原文","english":"Hello\nworld","simplifiedChinese":"你好"}},
                 "commands":[
                   {"i":7,"cmd":"Talk","raw":1,"TargetName":"hotaru","AdvTextID":"t1","VoiceIDs":[301],"voiceCues":["voice"],"BgmID":301},
                   {"i":8,"cmd":"Talk","AdvTextID":"t1","IgnoreData":1},
                   {"i":9,"cmd":"Focus","raw":7,"Parameter1":1e999,"TargetName":"hotaru","Duration":0},
                   {"i":10,"cmd":"Frame","raw":44,"TargetAssetName":"frame_a"}],
                 "sounds":{"301":{"_soundCueSheetID":5,"_cueName":"voice"}},"cuesheets":{"5":{"_cueSheetName":"sheet"}},"videos":{}}
                """, "gz");
            var files = new JsonObject
            {
                ["story.json"] = intl.Put(new JsonObject { ["advId"] = 42, ["models"] = modelMap.DeepClone(), ["audio"] = new JsonObject { ["sheet"] = "audio/main" } }.ToJsonString(), "br"),
                ["episode.json"] = episodeEntry,
                ["scene.json"] = sceneEntry,
                ["shaders/shaders.json"] = intl.Put("""[{"name":"Shader/A","variants":[{"pass":0,"file":"shaders/a.glsl"}]}]""", "gz"),
                ["frames.json"] = intl.Put("""{"frames":{"frame_a":{"value":1e999}}}""", "br"),
                ["shaders/a.glsl"] = intl.Put("shader", "", "glsl"),
                ["ui/shared.png"] = intl.Put("base-ui", "", "png"),
            };
            intl.Write("stories/42.json", Manifest(42, files, modelManifests.DeepClone(), languageGroups.DeepClone()));

            var files2 = new JsonObject
            {
                ["story.json"] = intl.Put(new JsonObject { ["advId"] = 43, ["models"] = modelMap.DeepClone(), ["audio"] = new JsonObject() }.ToJsonString(), ""),
                ["episode.json"] = intl.Put("""
                    {"advId":43,"commands":[{"i":1,"cmd":"Focus","raw":7,"Parameter1":1e999,"TargetName":"hotaru"},{"i":2,"cmd":"Still","raw":30,"TargetAssetName":"still_b"}],"videos":{"18":{"_id":18}}}
                    """, ""),
                ["scene.json"] = intl.Put("""{"stages":{"stage_b":{"nodes":[]}},"shaders":{"names":["Shader/A","Shader/B"]}}""", ""),
                ["shaders/shaders.json"] = intl.Put("""[{"name":"Shader/A","variants":[{"pass":0,"file":"shaders/a.glsl"},{"pass":1,"file":"shaders/a.glsl"}]}]""", ""),
                ["effects.json"] = intl.Put("""{"effects":{"effect_b":{}}}""", ""),
                ["stills.json"] = intl.Put("""{"stills":{"still_b":{}}}""", ""),
                ["posteffects.json"] = intl.Put("""{"postEffects":{"post_b":{}}}""", ""),
                ["videos/videos.json"] = intl.Put("""{"format":1,"videos":{"18":{"file":"videos/video.webm","key":"video","master":{"_id":18}}}}""", ""),
                ["videos/video.webm"] = intl.Put("video", "", "webm"),
                ["shaders/a.glsl"] = files["shaders/a.glsl"]!.DeepClone(),
                ["ui/shared.png"] = intl.Put("other-ui", "", "png"),
            };
            intl.Write("stories/43.json", Manifest(43, files2, modelManifests.DeepClone(), languageGroups.DeepClone()));
            intl.Write("stories.json", JsonNode.Parse("""{"stories":[{"advId":43,"manifest":"stories/43.json","languages":["en","zh-Hans"]},{"advId":42,"manifest":"stories/42.json","languages":["en","zh-Hans"]}]}""")!);
            // An old manifest outside the current selected index must not enter the generated resource pack.
            intl.Write("stories/999.json", JsonNode.Parse("""{"advId":999,"story":{"playbackMode":0},"files":{}}""")!);
            jp.Write("stories/80.json", JsonNode.Parse(new JsonObject
            {
                ["advId"] = 80, ["story"] = new JsonObject { ["playbackMode"] = 1 }, ["files"] = new JsonObject
                {
                    ["story.json"] = jp.Put("{}", ""), ["episode.json"] = jp.Put("""{"commands":[]}""", ""),
                },
            }.ToJsonString())!);
            jp.Write("stories.json", JsonNode.Parse("""{"stories":[{"advId":80,"manifest":"stories/80.json"}]}""")!);
            File.WriteAllText(Path.Combine(root, "discovery.json"), """
                {"band":{"textId":"Band_Name_millsage"},"indexes":{"intl":{},"jp":{}},"archive":{"languages":["en"]},
                 "stories":[{"advId":42,"categories":["main"],"reasons":["character"],"characterIds":[3]},{"advId":43}]}
                """);

            var stats = await ArchiveIndexer.BuildAsync(root);
            var archive = new Archive(root);
            assert(stats["stories"]!.GetValue<int>() == 3 && stats["commands"]!.GetValue<long>() == 6, "archive index builds from scratch across normal and simple ADV sites");
            assert(stats["dialogue"]!.GetValue<long>() == 1 && stats["voices"]!.GetValue<long>() == 1 && stats["sounds"]!.GetValue<long>() == 2, "archive index preserves command/dialogue/voice/sound counting");
            assert(File.ReadAllText(Path.Combine(root, "index", "commands.jsonl")).Contains("1e999", StringComparison.Ordinal), "index preserves nonfinite JSON number tokens");
            var line = archive.Rows("dialogue").Single();
            assert(line["text"]!["en"]!.GetValue<string>() == "Hello\nworld" && line["voices"]![0]!["samples"]!.GetValue<int>() == 96001, "split multilingual cue assets map original dialogue and duration");
            assert(line["voices"]![0]!["asset"]!.GetValue<string>() == voice["asset"]!.GetValue<string>(), "cue resolver chooses downloaded language groups");
            var fileRows = archive.Rows("files").ToArray();
            assert(fileRows.All(row => row["language"]?.ToString() != "zh-Hans") && fileRows.Any(row => row["language"]?.ToString() == "en"), "file index excludes undownloaded language assets");
            assert(fileRows.LongLength == stats["files"]!.GetValue<long>() && fileRows.Count(row => row["logicalPath"]?.ToString() == "scene.json" && row["advId"]?.ToString() == "42") == 3, "split asset parts have individual file rows and accurate totals");
            var catalog = archive.Index("catalog")["stories"]!.AsArray();
            var selected = catalog.Single(row => row?["advId"]?.ToString() == "42")!;
            assert(selected["languages"]!.AsArray().Count == 1 && selected["selection"]!["categories"]![0]!.GetValue<string>() == "main", "catalog retains discovery provenance and downloaded language list");
            assert(stats["models"]!.GetValue<int>() == 1 && archive.Index("models")["models"]![0]!["logicalFiles"]!.AsArray().Count == 2, "model descriptors deduplicate across stories");

            var pack = await DirectorPackBuilder.BuildAsync(root);
            assert(pack["baseAdvId"]!.GetValue<int>() == 42 && pack["stories"]!.GetValue<int>() == 2, "director pack selects available normal ADV base and current selection");
            var resources = Archive.ReadJson(intl.PathFor("director/resources.json"));
            assert(resources["stages"]!.AsArray().Count == 2 && resources["effects"]!.AsArray().Count == 1 && resources["stills"]!.AsArray().Count == 1 && resources["videos"]!.AsArray().Count == 1, "director resource registry merges all media families");
            assert(resources["stageDetails"]![0]!["fov"]!.GetValue<int>() == 43 && resources["cameraPresets"]!.AsArray().Count == 1 && resources["cameraPresets"]![0]!["Parameter1"]!.ToJsonString() == "1e999", "director camera presets deduplicate and preserve raw numeric fields");
            var actor = resources["actors"]![0]!;
            assert(actor["targetName"]!.GetValue<string>() == "director_test" && actor["targetAssetName"]!.GetValue<string>() == "Actor" && actor["defaultMotion"]!.GetValue<string>() == "idle", "director model preload names and fallback motion match native runtime");
            assert(actor["motions"]!.AsArray().Count == 2 && actor["expressions"]![1]!.GetValue<string>() == "surprise", "director model registry includes deduplicated motions and expression names");
            var manifest = Archive.ReadJson(intl.PathFor("director/manifest.json"));
            assert(manifest["language"]!.GetValue<string>() == "en" && manifest["languages"]!.AsObject().Count == 1, "director manifest uses a downloaded base language");
            var mergedScene = Encoding.UTF8.GetString(archive.Logical("intl", manifest, "scene.json"));
            assert(mergedScene.Contains("1e999", StringComparison.Ordinal), "director split scene merge preserves raw numeric values");
            var shaders = JsonNode.Parse(archive.Logical("intl", manifest, "shaders/shaders.json"))!;
            assert(shaders[0]!["variants"]!.AsArray().Count == 2, "director shader registry merges distinct shader variants");
            var synthetic = JsonNode.Parse(archive.Logical("intl", manifest, "episode.json"))!;
            assert(synthetic["commands"]!.AsArray().Count == 6 && synthetic["commands"]![0]!["cmd"]!.GetValue<string>() == "Character" && synthetic["sounds"]!.AsObject().Count == 0, "director episode synthesizes preload commands without retaining source audio");
            assert(manifest["files"]!["ui/shared.png"]!["asset"]!.GetValue<string>() == files["ui/shared.png"]!["asset"]!.GetValue<string>(), "director retains chosen base UI while merging shared resources");
            var firstManifest = File.ReadAllBytes(intl.PathFor("director/manifest.json"));
            var assetCount = Directory.EnumerateFiles(intl.PathFor("assets")).Count();
            await DirectorPackBuilder.BuildAsync(root);
            assert(firstManifest.SequenceEqual(File.ReadAllBytes(intl.PathFor("director/manifest.json"))) && Directory.EnumerateFiles(intl.PathFor("assets")).Count() == assetCount, "director rebuild is deterministic and reuses existing generated assets");
            var skipped = await DirectorPackBuilder.BuildAsync(root, "jp");
            assert(skipped["skipped"]!.GetValue<bool>() && !File.Exists(jp.PathFor("director/manifest.json")), "simple-only archive finishes with explicit skipped director pack");

            var previousStats = File.ReadAllBytes(Path.Combine(root, "index", "stats.json"));
            var sourcePath = intl.PathFor(episodeEntry["asset"]!.GetValue<string>());
            var original = File.ReadAllBytes(sourcePath);
            File.WriteAllText(sourcePath, "corrupt");
            var rejected = false;
            try { await ArchiveIndexer.BuildAsync(root); }
            catch (InvalidDataException) { rejected = true; }
            finally { File.WriteAllBytes(sourcePath, original); }
            assert(rejected && previousStats.SequenceEqual(File.ReadAllBytes(Path.Combine(root, "index", "stats.json"))), "failed index rebuild preserves the previous complete index");
            assert(!Directory.EnumerateDirectories(root, ".index-build-*").Any(), "failed index rebuild removes its incomplete temporary index");

            foreach (var id in new[] { 42, 43 })
            {
                var changed = Archive.ReadJson(intl.PathFor($"stories/{id}.json"));
                changed["story"]!["playbackMode"] = 1;
                intl.Write($"stories/{id}.json", changed);
            }
            var noNormal = await DirectorPackBuilder.BuildAsync(root);
            var emptyResources = Archive.ReadJson(intl.PathFor("director/resources.json"));
            assert(noNormal["skipped"]!.GetValue<bool>() && emptyResources["stages"]!.AsArray().Count == 0 && emptyResources["actors"]!.AsArray().Count == 0 && !File.Exists(intl.PathFor("director/manifest.json")), "successful simple-only selection replaces stale director resources with an empty registry");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static JsonObject Manifest(int id, JsonObject files, JsonNode models, JsonNode languages) => new()
    {
        ["format"] = "ournotes.story-manifest/2", ["advId"] = id, ["language"] = "zh-Hans", ["fonts"] = "open", ["audio"] = true, ["audioFormat"] = "m4a",
        ["story"] = new JsonObject { ["playbackMode"] = 0, ["titles"] = new JsonObject { ["en"] = "Story " + id } },
        ["files"] = files.DeepClone(), ["models"] = models, ["languages"] = languages,
    };

    private static JsonArray Part(string key, JsonObject asset) => new(key, asset["asset"]!.DeepClone(), asset["size"]!.DeepClone(), asset["stored"]!.DeepClone());

    private sealed class FixtureSite(string root, string site)
    {
        public string PathFor(string relative) => Path.Combine(root, "sites", site, relative.Replace('/', Path.DirectorySeparatorChar));

        public void Write(string relative, JsonNode value)
        {
            var path = PathFor(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, value.ToJsonString());
        }

        public JsonObject Put(string content, string encoding, string extension = "json")
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            var relative = "assets/" + Archive.Hash(bytes) + "." + extension + (encoding.Length == 0 ? "" : "." + encoding);
            using var output = new MemoryStream();
            if (encoding.Length == 0) output.Write(bytes);
            else
            {
                using Stream compressor = encoding == "gz" ? new GZipStream(output, CompressionLevel.Optimal, true) : new BrotliStream(output, CompressionLevel.Optimal, true);
                compressor.Write(bytes);
            }
            var path = PathFor(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, output.ToArray());
            return new JsonObject { ["asset"] = relative, ["size"] = bytes.Length, ["stored"] = output.Length };
        }
    }
}
