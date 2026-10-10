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
            var englishFiles = languageGroups["en"]!["files"]!.AsObject();
            englishFiles["ui/ui.json"] = intl.Put("""{"layout":"base","chatWidget":{"nodes":[]},"nodes":[]}""", "gz");
            englishFiles["ui/fonts.json"] = intl.Put(FontSubset('A', false).ToJsonString(), "gz");
            englishFiles["ui/fonts/shared.png"] = intl.Put(Png.Encode(new Pixels(64, 32, Enumerable.Repeat((byte)255, 64 * 32 * 4).ToArray())), "", "png");
            var otherLanguages = (JsonObject)languageGroups.DeepClone();
            var otherEnglishFiles = otherLanguages["en"]!["files"]!.AsObject();
            otherEnglishFiles["ui/ui.json"] = intl.Put("""{"layout":"other","chatWidget":{"nodes":[]},"nodes":[],"chatStatusTexts":{"incoming":"Incoming call"}}""", "gz");
            otherEnglishFiles["ui/fonts.json"] = intl.Put(FontSubset('B', true).ToJsonString(), "gz");
            otherEnglishFiles["ui/fonts/shared.png"] = intl.Put(Png.Encode(new Pixels(64, 32, Enumerable.Repeat((byte)200, 64 * 32 * 4).ToArray())), "", "png");
            otherEnglishFiles["ui/fonts/fallback.png"] = intl.Put(Png.Encode(new Pixels(32, 32, Enumerable.Repeat((byte)100, 32 * 32 * 4).ToArray())), "", "png");
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
                ["effects.json"] = intl.Put("""{"effects":{"effect_b":{"nodes":[]}},"instances":{"spark_left":"effect_b"}}""", "gz"),
                ["chat.json"] = intl.Put("""
                    {"chats":{"1001":{"_id":1001,"_chatSoundId":0,"_inOutSoundId":0,"_chatWindowAssetName":"phone_a","_chatIconAssetName":"icon_a"}},
                     "windows":{"phone_a":{"nodes":[]}},"icons":{"icon_a":{"texture":{"texture":"textures/icon_a.png"}}},"stamps":{},
                     "text":[{"_id":"60001","_english":"Read"}],"sounds":[{"_id":40005,"_cueName":"legacy_chat"}],"cueSheets":[{"_id":2000,"_cueSheetName":"AdvSe_Common"}]}
                    """, "gz"),
                ["textures/icon_a.png"] = intl.Put("icon-a", "", "png"),
                ["shaders/a.glsl"] = intl.Put("shader", "", "glsl"),
                ["ui/shared.png"] = intl.Put("base-ui", "", "png"),
            };
            intl.Write("stories/42.json", Manifest(42, files, modelManifests.DeepClone(), languageGroups.DeepClone()));

            var files2 = new JsonObject
            {
                ["story.json"] = intl.Put(new JsonObject { ["advId"] = 43, ["models"] = modelMap.DeepClone(), ["audio"] = new JsonObject { ["chat_pick"] = "audio/chat_pick" } }.ToJsonString(), ""),
                ["episode.json"] = intl.Put("""
                    {"advId":43,"commands":[{"i":1,"cmd":"Focus","raw":7,"Parameter1":1e999,"TargetName":"hotaru"},{"i":2,"cmd":"Still","raw":30,"TargetAssetName":"still_b"}],"videos":{"18":{"_id":18}},
                     "text":{"60001":{"english":"Read"},"chat_line":{"english":"Message from the second story"}},
                     "sounds":{"2100000000053":{"_id":2100000000053,"_category":1,"_soundCueSheetID":2100000000053,"_cueName":"chat_pick"}},
                     "cuesheets":{"2100000000053":{"_id":2100000000053,"_cueSheetName":"chat_pick"}}}
                    """, ""),
                ["scene.json"] = intl.Put("""{"stages":{"stage_b":{"nodes":[]}},"shaders":{"names":["Shader/A","Shader/B"]}}""", ""),
                ["shaders/shaders.json"] = intl.Put("""[{"name":"Shader/A","variants":[{"pass":0,"file":"shaders/a.glsl"},{"pass":1,"file":"shaders/a.glsl"}]}]""", ""),
                ["effects.json"] = intl.Put("""{"effects":{"effect_b":{"nodes":[]}},"instances":{"spark_right":"effect_b"}}""", ""),
                ["chat.json"] = intl.Put("""
                    {"chats":{"1001":{"_id":1001},"1002":{"_id":1002,"_chatSoundId":2100000000053,"_inOutSoundId":2100000000053,"_chatWindowAssetName":"phone_b","_chatIconAssetName":"icon_b"}},
                     "windows":{"phone_b":{"nodes":[]}},"icons":{"icon_b":{"texture":{"texture":"textures/icon_b.png"}}},"stamps":{"stamp_b":{"texture":{"texture":"textures/stamp_b.png"}}},
                     "text":[{"_id":"60001","_english":"Read"},{"_id":"chat_line","_english":"Message from the second story","rawValue":1e999}],
                     "sounds":[{"_id":40005,"_cueName":"legacy_chat"},{"_id":40006,"_cueName":"legacy_chat_other"}],"cueSheets":[{"_id":2000,"_cueSheetName":"AdvSe_Common"}]}
                    """, "br"),
                ["textures/icon_b.png"] = intl.Put("icon-b", "", "png"),
                ["textures/stamp_b.png"] = intl.Put("stamp-b", "", "png"),
                ["audio/chat_pick/cues.json"] = intl.Put("""{"chat_pick":{"file":"chat_pick.m4a","sampleRate":48000,"channels":1,"samples":4800}}""", "gz"),
                ["audio/chat_pick/chat_pick.m4a"] = intl.Put("chat-audio", "", "m4a"),
                ["stills.json"] = intl.Put("""{"stills":{"still_b":{}}}""", ""),
                ["posteffects.json"] = intl.Put("""{"postEffects":{"post_b":{}}}""", ""),
                ["videos/videos.json"] = intl.Put("""{"format":1,"videos":{"18":{"file":"videos/video.webm","key":"video","master":{"_id":18}}}}""", ""),
                ["videos/video.webm"] = intl.Put("video", "", "webm"),
                ["shaders/a.glsl"] = files["shaders/a.glsl"]!.DeepClone(),
                ["ui/shared.png"] = intl.Put("other-ui", "", "png"),
            };
            intl.Write("stories/43.json", Manifest(43, files2, modelManifests.DeepClone(), otherLanguages.DeepClone()));
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
            assert(stats["band"]?["textId"]?.ToString() == "Band_Name_millsage", "index statistics retain legacy discovery band metadata");
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
            assert(manifest["story"]!["asset"]!.GetValue<string>() == "adv_director_millsage" && manifest["story"]!["titles"]!["en"]!.GetValue<string>() == "Millsage ADV Director Pack",
                "Millsage resource packs retain the legacy asset identity and expose titles in downloaded languages");
            assert(manifest["language"]!.GetValue<string>() == "en" && manifest["languages"]!.AsObject().Count == 1, "director manifest uses a downloaded base language");
            var mergedScene = Encoding.UTF8.GetString(archive.Logical("intl", manifest, "scene.json"));
            assert(mergedScene.Contains("1e999", StringComparison.Ordinal), "director split scene merge preserves raw numeric values");
            var shaders = JsonNode.Parse(archive.Logical("intl", manifest, "shaders/shaders.json"))!;
            assert(shaders[0]!["variants"]!.AsArray().Count == 2, "director shader registry merges distinct shader variants");
            var synthetic = JsonNode.Parse(archive.Logical("intl", manifest, "episode.json"))!;
            assert(synthetic["commands"]!.AsArray().Count == 7 && synthetic["commands"]![0]!["cmd"]!.GetValue<string>() == "Character" && synthetic["sounds"]!.AsObject().Count == 1 && synthetic["sounds"]!["301"] is null, "director episode synthesizes resource preloads and retains only referenced chat audio");
            var nativeProgram = AdvProgram.From(synthetic, new JsonObject
            {
                ["_initializeEpisodes"] = new JsonArray(), ["_finalizeEpisodes"] = new JsonArray(),
                ["_waitCommandLingeringTimeOnAutoPlay"] = 0.5f,
            });
            assert(nativeProgram.Rows.Select(row => row.Command).SequenceEqual(new[] { "Character", "Frame", "Effect", "Effect", "PostEffect", "Still", "Movie" }),
                "every synthesized director command passes the native program parser with consistent command IDs and names");
            var effects = JsonNode.Parse(archive.Logical("intl", manifest, "effects.json"))!;
            assert(nativeProgram.Rows.Where(row => row.Command == "Effect").Select(row => row.Text("TargetName")).SequenceEqual(new[] { "spark_left", "spark_right" }) &&
                nativeProgram.Rows.Where(row => row.Command == "Effect").All(row => effects["effects"]?[effects["instances"]![row.Text("TargetName")]!.GetValue<string>()] is JsonObject) &&
                resources["effects"]![0]!.GetValue<string>() == "effect_b",
                "effect preloads resolve native instance names to merged prefabs while effect show retains prefab IDs");
            var mergedStory = JsonNode.Parse(archive.Logical("intl", manifest, "story.json"))!;
            var chat = JsonNode.Parse(archive.Logical("intl", manifest, mergedStory["chat"]!.GetValue<string>()))!;
            assert(chat["chats"]!.AsObject().Count == 2 && chat["windows"]!.AsObject().Count == 2 && chat["icons"]!.AsObject().Count == 2 && chat["stamps"]!.AsObject().Count == 1 &&
                chat["text"]!.AsArray().Count == 2 && chat["sounds"]!.AsArray().Count == 2 && chat["cueSheets"]!.AsArray().Count == 1 && chat["text"]![1]!["rawValue"]!.ToJsonString() == "1e999",
                "chat merge preserves each native document domain and deduplicates complete master-data rows by ID");
            assert(chat["chats"]!.AsObject().All(pair => chat["windows"]?[pair.Value!["_chatWindowAssetName"]!.GetValue<string>()] is not null && chat["icons"]?[pair.Value["_chatIconAssetName"]!.GetValue<string>()] is not null) &&
                chat["icons"]!.AsObject().All(pair => archive.Logical("intl", manifest, pair.Value!["texture"]!["texture"]!.GetValue<string>()).Length > 0) &&
                synthetic["text"]!["t1"]!["english"]!.GetValue<string>() == "Hello\nworld" && synthetic["text"]!["chat_line"]!["english"]!.GetValue<string>() == "Message from the second story",
                "merged chat masters resolve their windows, icon assets and localized text across source episodes");
            var chatSoundId = chat["chats"]!["1002"]!["_chatSoundId"]!.GetValue<long>().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var chatSound = synthetic["sounds"]![chatSoundId]!;
            var chatSheet = synthetic["cuesheets"]![chatSound["_soundCueSheetID"]!.ToString()]!;
            var chatPrefix = "audio/" + chatSheet["_cueSheetName"]!.GetValue<string>() + "/";
            var chatCue = JsonNode.Parse(archive.Logical("intl", manifest, chatPrefix + "cues.json"))![chatSound["_cueName"]!.GetValue<string>()]!;
            assert(chatSoundId == "2100000000053" && chatSound["_category"]!.GetValue<int>() == 1 && manifest["audio"]!.GetValue<bool>() &&
                mergedStory["audio"]!["chat_pick"]!.GetValue<string>() == "audio/chat_pick" && archive.Logical("intl", manifest, chatPrefix + chatCue["file"]!.GetValue<string>()).Length > 0,
                "chat audio follows the native 64-bit sound, cue-sheet, cue-metadata and encoded-asset lookup chain");
            var mergedUi = JsonNode.Parse(archive.Logical("intl", manifest, "ui/ui.json", "en"))!;
            var mergedFonts = JsonNode.Parse(archive.Logical("intl", manifest, "ui/fonts.json", "en"))!;
            var sharedBinding = mergedFonts["chatTexts"]!["phone_a"]!["phone_a/text"]!;
            var mergedText = new AdvTmpText(mergedFonts, sharedBinding) { Text = "ABC" };
            var glyphs = mergedText.Generate(100, 40, new(0, 1));
            assert(mergedUi["layout"]!.GetValue<string>() == "base" && mergedUi["chatStatusTexts"]!["incoming"]!.GetValue<string>() == "Incoming call" &&
                glyphs.Select(glyph => glyph.Character).SequenceEqual(new[] { 65, 66, 67 }) && mergedFonts["chatTexts"]!["phone_b"] is not null,
                "a shared chat binding generates glyphs from both source subsets and an added fallback without replacing the base UI");
            assert(glyphs[0].Texture != glyphs[1].Texture && Math.Abs(glyphs[0].Uv.X - 5f / 64) < 0.0001f && Math.Abs(glyphs[1].Uv.X - 14f / 64) < 0.0001f &&
                glyphs.Select(glyph => Png.Decode(archive.Logical("intl", manifest, "ui/" + mergedFonts["textures"]![glyph.Texture]!["texture"]!.GetValue<string>(), "en")).Rgba[0]).SequenceEqual(new byte[] { 255, 200, 100 }),
                "merged native glyph output preserves per-source atlas pixels and packed UVs despite duplicate atlas file names");
            assert(manifest["files"]!["ui/shared.png"]!["asset"]!.GetValue<string>() == files["ui/shared.png"]!["asset"]!.GetValue<string>(), "director retains chosen base UI while merging shared resources");
            var firstManifest = File.ReadAllBytes(intl.PathFor("director/manifest.json"));
            var assetCount = Directory.EnumerateFiles(intl.PathFor("assets")).Count();
            await DirectorPackBuilder.BuildAsync(root);
            assert(firstManifest.SequenceEqual(File.ReadAllBytes(intl.PathFor("director/manifest.json"))) && Directory.EnumerateFiles(intl.PathFor("assets")).Count() == assetCount, "director rebuild is deterministic and reuses existing generated assets");
            var discoveryPath = Path.Combine(root, "discovery.json");
            var bandDiscovery = Archive.ReadJson(discoveryPath);
            bandDiscovery["band"] = new JsonObject { ["bandId"] = 2, ["bandTextId"] = "Band_Name_fixture", ["token"] = "fixture", ["name"] = "Fixture Band" };
            File.WriteAllText(discoveryPath, bandDiscovery.ToJsonString());
            await DirectorPackBuilder.BuildAsync(root);
            var otherBandManifest = Archive.ReadJson(intl.PathFor("director/manifest.json"));
            var otherBandEpisode = JsonNode.Parse(archive.Logical("intl", otherBandManifest, "episode.json"))!;
            assert(otherBandManifest["story"]!["asset"]!.GetValue<string>() == "adv_director_fixture" && otherBandManifest["story"]!["titles"]!["en"]!.GetValue<string>() == "Fixture Band ADV Director Pack" &&
                otherBandEpisode["asset"]!.GetValue<string>() == "adv_director_fixture" && otherBandEpisode["master"]!["_advEpisodeAsset"]!.GetValue<string>() == "adv_director_fixture" &&
                Archive.ReadJson(intl.PathFor("director/resources.json"))["band"]!["bandId"]!.GetValue<int>() == 2,
                "non-Millsage packs consistently use the selected band identity while keeping registry paths and native schemas compatible");
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

    private static JsonObject FontSubset(char character, bool fallback)
    {
        var document = JsonNode.Parse("""
            {"format":"fixture-fonts","source":"fixture","fonts":{},"materials":{
              "Chat Material":{"floats":{"_GradientScale":1,"_ScaleRatioA":1},"keywords":[],"colors":{}},
              "Chat Material + Fallback":{"floats":{"_GradientScale":1,"_ScaleRatioA":1},"keywords":[],"colors":{}}},
             "textures":{"atlas":{"name":"atlas","texture":"fonts/shared.png","width":64,"height":32}},
             "chatTexts":{"phone_a":{"phone_a/text":{
               "enabled":1,"fontAsset":"Test Font","material":"Chat Material","m_fontSize":10,"m_fontSizeBase":10,"m_fontStyle":0,
               "m_HorizontalAlignment":1,"m_VerticalAlignment":256,"m_TextWrappingMode":0,"m_fontColor":{"r":1,"g":1,"b":1,"a":1}}}}}
            """)!.AsObject();
        JsonObject Face(char value, string texture, int rectX, int dx) => new()
        {
            ["faceInfo"] = JsonNode.Parse("""{"m_PointSize":10,"m_Scale":1,"m_LineHeight":12,"m_AscentLine":8,"m_DescentLine":-2,"m_Baseline":0}"""),
            ["atlases"] = new JsonArray(texture), ["characters"] = new JsonObject { [((int)value).ToString()] = new JsonObject { ["glyph"] = (int)value, ["scale"] = 1 } },
            ["glyphs"] = new JsonObject { [((int)value).ToString()] = new JsonObject
            {
                ["atlasIndex"] = 0, ["scale"] = 1,
                ["metrics"] = JsonNode.Parse("""{"m_Width":6,"m_Height":8,"m_HorizontalAdvance":7,"m_HorizontalBearingX":0,"m_HorizontalBearingY":8}"""),
                ["rect"] = new JsonObject { ["m_X"] = rectX, ["m_Y"] = 4, ["m_Width"] = 6, ["m_Height"] = 8 },
                ["packed"] = new JsonObject { ["texture"] = texture, ["dx"] = dx, ["dy"] = 0 },
            } },
            ["fallbacks"] = new JsonArray(), ["normalStyle"] = 0, ["normalSpacingOffset"] = 0, ["boldSpacing"] = 0,
        };
        document["fonts"]!["Test Font"] = Face(character, "atlas", character == 'A' ? 4 : 8, character == 'A' ? 2 : 7);
        if (fallback)
        {
            document["fonts"]!["Test Font"]!["fallbacks"] = new JsonArray("Fallback");
            document["fonts"]!["Fallback"] = Face('C', "fallback", 12, 0);
            document["textures"]!["fallback"] = new JsonObject { ["name"] = "fallback", ["texture"] = "fonts/fallback.png", ["width"] = 32, ["height"] = 32 };
            document["chatTexts"]!["phone_b"] = new JsonObject { ["phone_b/text"] = document["chatTexts"]!["phone_a"]!["phone_a/text"]!.DeepClone() };
        }
        return document;
    }

    private sealed class FixtureSite(string root, string site)
    {
        public string PathFor(string relative) => Path.Combine(root, "sites", site, relative.Replace('/', Path.DirectorySeparatorChar));

        public void Write(string relative, JsonNode value)
        {
            var path = PathFor(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, value.ToJsonString());
        }

        public JsonObject Put(string content, string encoding, string extension = "json") => Put(Encoding.UTF8.GetBytes(content), encoding, extension);

        public JsonObject Put(byte[] bytes, string encoding, string extension = "json")
        {
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
