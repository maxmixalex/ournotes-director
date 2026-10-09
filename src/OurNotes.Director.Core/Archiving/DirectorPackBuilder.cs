using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static OurNotes.Director.Core.Archiving.ArchiveIndexStorage;

namespace OurNotes.Director.Core.Archiving;

/// <summary>Merges archived normal ADV resources into the resource pack consumed by the native Director.</summary>
public static partial class DirectorPackBuilder
{
    private const long DirectorAdvId = 9990001;
    private const string DirectorAsset = "adv_director_millsage";
    private static readonly HashSet<string> SpecialFiles = new(StringComparer.Ordinal)
    {
        "story.json", "episode.json", "scene.json", "frames.json", "effects.json", "posteffects.json", "stills.json",
        "talkwindows.json", "chat.json", "videos/videos.json", "shaders/shaders.json",
    };
    private static readonly (string Path, string Key)[] MediaDocuments =
    [
        ("frames.json", "frames"), ("effects.json", "effects"), ("posteffects.json", "postEffects"),
        ("stills.json", "stills"), ("talkwindows.json", "talkWindows"), ("chat.json", "chat"), ("videos/videos.json", "videos"),
    ];
    private static readonly HashSet<string> CameraCommands = new(StringComparer.Ordinal)
    {
        "Focus", "Track", "Pedestal", "Zoom", "PanV2", "Pan", "Tilt", "Role", "DoF", "CameraShake", "Shake",
    };
    private static readonly HashSet<string> VisualCommands = new(StringComparer.Ordinal)
    {
        "Brightness", "RimLight", "StageEnv", "Flash", "Frame", "Effect", "PostEffect", "Still", "Alpha",
    };
    private static readonly HashSet<string> PresetExcludedFields = new(StringComparer.Ordinal)
    {
        "i", "cmd", "raw", "lines", "voiceCues", "AdvTextID", "VoiceIDs", "TargetTextIDs", "TargetTextColors",
    };

    public static async Task<JsonObject> BuildAsync(string archiveDirectory, string site = "intl", long baseAdvId = 10060,
        CancellationToken cancellationToken = default)
    {
        var siteDirectory = SiteDirectory(archiveDirectory, site);
        var storiesDirectory = Path.Combine(siteDirectory, "stories");
        if (!Directory.Exists(storiesDirectory))
            return await PublishEmptyAsync(siteDirectory, site, "No downloaded stories for this site.", new JsonArray(), cancellationToken).ConfigureAwait(false);

        // Follow the published selection, not leftover files from an earlier archive invocation.
        var selectedIndex = Path.Combine(siteDirectory, "stories.json");
        IEnumerable<string> manifestPaths;
        if (File.Exists(selectedIndex))
        {
            var index = await ReadObjectAsync(selectedIndex, cancellationToken).ConfigureAwait(false);
            manifestPaths = Items(index["stories"]).Select(story => Text(story?["manifest"])).Distinct(StringComparer.Ordinal).ToArray();
        }
        else
        {
            manifestPaths = Directory.EnumerateFiles(storiesDirectory, "*.json", SearchOption.TopDirectoryOnly)
                .Where(path => long.TryParse(Path.GetFileNameWithoutExtension(path), NumberStyles.None, CultureInfo.InvariantCulture, out _))
                .Select(path => "stories/" + Path.GetFileName(path)).ToArray();
        }

        var manifests = new List<JsonObject>();
        var skippedStories = new JsonArray();
        foreach (var relative in manifestPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifest = await ReadObjectAsync(Within(siteDirectory, relative), cancellationToken).ConfigureAwait(false);
            if (Truthy(manifest["story"]?["playbackMode"])) continue;
            if (new[] { "story.json", "episode.json", "scene.json" }.Any(path => LogicalEntry(manifest, path) is null))
            {
                skippedStories.Add(Clone(manifest["advId"]));
                continue;
            }
            manifests.Add(manifest);
        }
        if (manifests.Count == 0)
        {
            return await PublishEmptyAsync(siteDirectory, site,
                "No normal ADV story with scene, story and episode resources is available.", skippedStories, cancellationToken).ConfigureAwait(false);
        }
        manifests.Sort((left, right) => CompareIds(left["advId"], right["advId"]));
        var baseManifest = manifests.FirstOrDefault(manifest => Text(manifest["advId"]) == baseAdvId.ToString(CultureInfo.InvariantCulture)) ?? manifests[0];

        var selectedLanguages = await SelectedLanguagesAsync(archiveDirectory, cancellationToken).ConfigureAwait(false);
        var languages = new JsonObject();
        foreach (var (language, group) in Properties(baseManifest["languages"]))
            if (selectedLanguages is null || selectedLanguages.Contains(language)) languages[language] = Clone(group);
        var baseLanguage = Clone(baseManifest["language"]);
        if (selectedLanguages is not null && !selectedLanguages.Contains(Text(baseLanguage)))
            baseLanguage = languages.Count > 0 ? JsonValue.Create(languages.First().Key) : null;

        var files = MergeFiles(baseManifest, manifests);
        var scene = await LogicalAsync(siteDirectory, baseManifest, "scene.json", cancellationToken).ConfigureAwait(false) as JsonObject
            ?? throw new InvalidDataException("Director base scene must be a JSON object.");
        var story = await LogicalAsync(siteDirectory, baseManifest, "story.json", cancellationToken).ConfigureAwait(false) as JsonObject
            ?? throw new InvalidDataException("Director base story must be a JSON object.");
        var episode = await LogicalAsync(siteDirectory, baseManifest, "episode.json", cancellationToken).ConfigureAwait(false) as JsonObject
            ?? throw new InvalidDataException("Director base episode must be a JSON object.");

        scene["stages"] = new JsonObject();
        var stages = scene["stages"]!.AsObject();
        var shaderNames = new SortedSet<string>(Items(scene["shaders"]?["names"]).Select(Text), StringComparer.Ordinal);
        var shaderLists = new List<JsonNode>();
        var media = MediaDocuments.ToDictionary(item => item.Key, _ => new JsonObject(), StringComparer.Ordinal);
        var effectInstances = new JsonObject();
        var episodeText = new JsonObject();
        var chatSounds = new JsonObject();
        var chatCueSheets = new JsonObject();
        var chatAudio = new JsonObject();
        var storyModels = new JsonObject();
        var manifestModels = new JsonObject();
        var videoMaster = new JsonObject();
        var sources = new JsonArray();
        var cameraPresets = new List<JsonObject>();
        var visualPresets = new List<JsonObject>();

        foreach (var manifest in manifests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sources.Add(Clone(manifest["advId"]));
            var sourceScene = await LogicalAsync(siteDirectory, manifest, "scene.json", cancellationToken).ConfigureAwait(false);
            MergeFirst(stages, sourceScene?["stages"]);
            foreach (var name in Items(sourceScene?["shaders"]?["names"])) shaderNames.Add(Text(name));
            var shaderIndex = await LogicalAsync(siteDirectory, manifest, "shaders/shaders.json", cancellationToken).ConfigureAwait(false);
            if (shaderIndex is not null) shaderLists.Add(shaderIndex);
            var sourceStory = await LogicalAsync(siteDirectory, manifest, "story.json", cancellationToken).ConfigureAwait(false);
            MergeFirst(storyModels, sourceStory?["models"]);
            MergeFirst(manifestModels, manifest["models"]);
            var sourceEpisode = await LogicalAsync(siteDirectory, manifest, "episode.json", cancellationToken).ConfigureAwait(false);
            MergeFirst(videoMaster, sourceEpisode?["videos"]);
            MergeFirst(episodeText, sourceEpisode?["text"]);
            foreach (var commandNode in Items(sourceEpisode?["commands"]))
            {
                if (commandNode is not JsonObject command || Truthy(command["IgnoreData"])) continue;
                var name = Text(command["cmd"]);
                if (CameraCommands.Contains(name)) cameraPresets.Add(CleanCommand(command, manifest["advId"]));
                if (VisualCommands.Contains(name)) visualPresets.Add(CleanCommand(command, manifest["advId"]));
            }
            foreach (var (path, key) in MediaDocuments)
            {
                var document = await LogicalAsync(siteDirectory, manifest, path, cancellationToken).ConfigureAwait(false);
                if (key == "chat")
                {
                    MergeChatDocument(media[key], document);
                    MergeChatAudio(document, sourceEpisode, sourceStory, chatSounds, chatCueSheets, chatAudio);
                }
                else MergeFirst(media[key], document?[key]);
                if (key == "effects") MergeFirst(effectInstances, document?["instances"]);
            }
        }
        if (shaderNames.Count > 0 || scene["shaders"] is JsonObject)
        {
            scene["shaders"] ??= new JsonObject();
            scene["shaders"]!["names"] = Strings(shaderNames);
        }
        await MergeUiAsync(siteDirectory, baseManifest, manifests, files, languages, cancellationToken).ConfigureAwait(false);

        var actors = new JsonArray();
        foreach (var (address, idNode) in storyModels.OrderBy(pair => Text(pair.Value), StringComparer.Ordinal).ThenBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var id = Text(idNode);
            var modelManifestPath = manifestModels[id];
            if (modelManifestPath is null) continue;
            actors.Add((JsonNode)await InspectModelAsync(siteDirectory, id, Text(modelManifestPath), address, cancellationToken).ConfigureAwait(false));
        }

        files["shaders/shaders.json"] = await PutJsonAssetAsync(siteDirectory, MergeShaderIndexes(shaderLists), cancellationToken).ConfigureAwait(false);
        files["scene.json"] = await PutJsonAssetAsync(siteDirectory, scene, cancellationToken).ConfigureAwait(false);
        foreach (var (path, key) in MediaDocuments)
        {
            if (media[key].Count == 0) continue;
            var document = key == "chat" ? (JsonObject)media[key].DeepClone() : new JsonObject { [key] = Clone(media[key]) };
            if (key == "effects") document["instances"] = Clone(effectInstances);
            if (key == "videos") document["format"] = 1;
            files[path] = await PutJsonAssetAsync(siteDirectory, document, cancellationToken).ConfigureAwait(false);
        }

        story["advId"] = DirectorAdvId;
        story["models"] = Clone(storyModels);
        story["audio"] = chatAudio;
        foreach (var (path, key) in MediaDocuments) story[key] = media[key].Count > 0 ? JsonValue.Create(path) : null;
        story["episode"] = "episode.json";
        story["scene"] = "scene.json";
        files["story.json"] = await PutJsonAssetAsync(siteDirectory, story, cancellationToken).ConfigureAwait(false);

        var commands = SyntheticCommands(media, effectInstances, actors);
        episode["advId"] = DirectorAdvId;
        episode["asset"] = DirectorAsset;
        episode["commandCount"] = commands.Count;
        episode["commands"] = Clone(commands);
        episode["text"] = episodeText;
        episode["sounds"] = chatSounds;
        episode["cuesheets"] = chatCueSheets;
        episode["videos"] = videoMaster;
        episode["resources"] = new JsonArray();
        episode["master"] ??= new JsonObject();
        var master = episode["master"]!.AsObject();
        master["_id"] = DirectorAdvId;
        master["_advEpisodeAsset"] = DirectorAsset;
        master["_sheetName"] = DirectorAsset;
        master["_titleTextId"] = "";
        master["_playbackMode"] = 0;
        files["episode.json"] = await PutJsonAssetAsync(siteDirectory, episode, cancellationToken).ConfigureAwait(false);

        var commandNames = Strings(commands.Select(command => Text(command?["cmd"])).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        var outputManifest = new JsonObject
        {
            ["format"] = "ournotes.story-manifest/2", ["advId"] = DirectorAdvId, ["root"] = "../", ["language"] = Clone(baseLanguage),
            ["audio"] = chatSounds.Count > 0, ["audioFormat"] = chatSounds.Count > 0 ? Clone(baseManifest["audioFormat"]) : null,
            ["fonts"] = Clone(baseManifest["fonts"]) ?? JsonValue.Create("open"),
            ["requires"] = new JsonObject { ["commands"] = Clone(commandNames), ["cubismCore"] = true, ["motionSync"] = false },
            ["story"] = new JsonObject
            {
                ["advId"] = DirectorAdvId, ["asset"] = DirectorAsset, ["sheetName"] = DirectorAsset, ["playbackMode"] = 0,
                ["titles"] = new JsonObject { ["zh-Hans"] = "Millsage ADV Director Pack", ["ja"] = "Millsage ADV Director Pack" },
                ["groups"] = new JsonArray(), ["commands"] = Clone(commandNames), ["commandCount"] = commands.Count,
                ["language"] = Clone(baseLanguage), ["languages"] = Strings(languages.Select(pair => pair.Key)),
            },
            ["files"] = files, ["models"] = manifestModels, ["languages"] = languages,
        };

        var videos = new JsonArray();
        foreach (var (id, value) in media["videos"])
        {
            var row = new JsonObject { ["id"] = VideoId(id) };
            foreach (var field in new[] { "file", "key", "master" })
                if (value?[field] is not null) row[field] = Clone(value[field]);
            videos.Add((JsonNode)row);
        }
        var resources = new JsonObject
        {
            ["format"] = "millsage.adv-director-resources/2", ["site"] = site, ["baseAdvId"] = Clone(baseManifest["advId"]),
            ["advId"] = DirectorAdvId, ["manifest"] = "director/manifest.json", ["sources"] = sources,
            ["stages"] = Strings(stages.Select(pair => pair.Key).Order(StringComparer.Ordinal)), ["stageDetails"] = StageInfo(stages),
            ["videos"] = videos, ["models"] = Clone(actors), ["actors"] = actors,
            ["cameraPresets"] = UniqueCommands(cameraPresets), ["visualPresets"] = UniqueCommands(visualPresets),
        };
        foreach (var key in new[] { "frames", "effects", "postEffects", "stills" })
            resources[key] = Strings(media[key].Select(pair => pair.Key).Order(StringComparer.Ordinal));

        var temporaryDirectory = Path.Combine(siteDirectory, ".director-build-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            await WriteJsonAsync(Path.Combine(temporaryDirectory, "manifest.json"), outputManifest, cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(Path.Combine(temporaryDirectory, "resources.json"), resources, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            PublishDirectory(temporaryDirectory, Path.Combine(siteDirectory, "director"));
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, recursive: true);
        }

        var result = new JsonObject
        {
            ["site"] = site, ["baseAdvId"] = Clone(baseManifest["advId"]), ["stories"] = manifests.Count,
            ["files"] = files.Count, ["models"] = actors.Count, ["videos"] = videos.Count,
            ["cameraPresets"] = resources["cameraPresets"]!.AsArray().Count,
            ["visualPresets"] = resources["visualPresets"]!.AsArray().Count,
            ["manifest"] = Path.Combine(siteDirectory, "director", "manifest.json"), ["incompleteStories"] = skippedStories,
        };
        foreach (var key in new[] { "stages", "frames", "effects", "postEffects", "stills" }) result[key] = resources[key]!.AsArray().Count;
        return result;
    }

    private static JsonObject Skipped(string site, string reason) => new() { ["site"] = site, ["skipped"] = true, ["reason"] = reason };

    private static async Task<JsonObject> PublishEmptyAsync(string siteDirectory, string site, string reason,
        JsonArray incompleteStories, CancellationToken cancellationToken)
    {
        // A successful selection change must not leave an older pack queryable. The empty
        // registry keeps list commands useful for a SimpleADV-only archive; there is no
        // playable synthetic manifest when the required normal ADV scene is unavailable.
        var resources = new JsonObject
        {
            ["format"] = "millsage.adv-director-resources/2", ["site"] = site, ["baseAdvId"] = null,
            ["advId"] = DirectorAdvId, ["manifest"] = null, ["skipped"] = true, ["reason"] = reason,
        };
        foreach (var key in new[] { "sources", "stages", "stageDetails", "frames", "effects", "postEffects", "stills", "videos", "models", "actors", "cameraPresets", "visualPresets" })
            resources[key] = new JsonArray();
        var temporaryDirectory = Path.Combine(siteDirectory, ".director-build-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            await WriteJsonAsync(Path.Combine(temporaryDirectory, "resources.json"), resources, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            PublishDirectory(temporaryDirectory, Path.Combine(siteDirectory, "director"));
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, recursive: true);
        }
        var summary = Skipped(site, reason);
        summary["incompleteStories"] = incompleteStories;
        return summary;
    }

    private static int CompareIds(JsonNode? left, JsonNode? right)
    {
        if (long.TryParse(Text(left), out var a) && long.TryParse(Text(right), out var b)) return a.CompareTo(b);
        return string.CompareOrdinal(Text(left), Text(right));
    }

    private static async Task<HashSet<string>?> SelectedLanguagesAsync(string archiveDirectory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(archiveDirectory, "discovery.json");
        if (!File.Exists(path)) return null;
        var discovery = await ReadObjectAsync(path, cancellationToken).ConfigureAwait(false);
        return discovery["archive"]?["languages"] is JsonArray languages
            ? new HashSet<string>(languages.Select(Text), StringComparer.Ordinal) : null;
    }

    private static JsonObject MergeFiles(JsonObject baseManifest, List<JsonObject> manifests)
    {
        var files = new JsonObject();
        foreach (var (path, entry) in Properties(baseManifest["files"]))
            if (!SpecialFiles.Contains(path)) files[path] = Clone(entry);
        foreach (var manifest in manifests)
        {
            foreach (var (path, entry) in Properties(manifest["files"]))
            {
                if (SpecialFiles.Contains(path) || path.StartsWith("ui/", StringComparison.Ordinal)) continue;
                if (!files.TryGetPropertyValue(path, out var previous)) files[path] = Clone(entry);
                else if (!JsonNode.DeepEquals(previous, entry))
                    throw new InvalidDataException($"Director pack logical-path collision: {path} (ADV {manifest["advId"]}).");
            }
        }
        return files;
    }

    private static void MergeFirst(JsonObject target, JsonNode? source)
    {
        foreach (var (key, value) in Properties(source))
            if (!target.ContainsKey(key)) target[key] = Clone(value);
    }

    private static void MergeChatDocument(JsonObject target, JsonNode? source)
    {
        // Chat exports have several sibling registries, not a single `chat` registry.
        // Master-data arrays use stable IDs; preserve complete rows and their raw values.
        foreach (var (key, value) in Properties(source))
        {
            if (!target.TryGetPropertyValue(key, out var previous)) target[key] = Clone(value);
            else if (previous is JsonObject objects && value is JsonObject) MergeFirst(objects, value);
            else if (previous is JsonArray rows && value is JsonArray additions)
            {
                static string Identity(JsonNode? row) => row is JsonObject obj && obj["_id"] is not null
                    ? "id:" + Text(obj["_id"]) : "value:" + (row?.ToJsonString() ?? "null");
                var seen = new HashSet<string>(rows.Select(Identity), StringComparer.Ordinal);
                foreach (var row in additions)
                    if (seen.Add(Identity(row))) rows.Add(Clone(row));
            }
        }
    }

    private static void MergeChatAudio(JsonNode? chat, JsonNode? episode, JsonNode? story,
        JsonObject sounds, JsonObject cueSheets, JsonObject audio)
    {
        // The exported chat masters can refer to episode-specific 64-bit sound IDs,
        // which differ from the legacy common sound rows also included in chat.json.
        foreach (var (_, master) in Properties(chat?["chats"]))
        {
            foreach (var field in new[] { "_chatSoundId", "_inOutSoundId" })
            {
                var id = Text(master?[field]);
                if (id is "" or "0" || sounds.ContainsKey(id)) continue;
                var sound = episode?["sounds"]?[id] ?? Items(chat?["sounds"]).FirstOrDefault(row => Text(row?["_id"]) == id)
                    ?? throw new InvalidDataException($"Chat sound absent from source episode: {id}");
                var sheetId = Text(sound["_soundCueSheetID"]);
                var sheet = episode?["cuesheets"]?[sheetId] ?? Items(chat?["cueSheets"]).FirstOrDefault(row => Text(row?["_id"]) == sheetId)
                    ?? throw new InvalidDataException($"Chat cue sheet absent from source episode: {sheetId}");
                var sheetName = Text(sheet["_cueSheetName"]);
                sounds[id] = Clone(sound);
                if (!cueSheets.ContainsKey(sheetId)) cueSheets[sheetId] = Clone(sheet);
                if (!audio.ContainsKey(sheetName)) audio[sheetName] = Clone(story?["audio"]?[sheetName]) ?? JsonValue.Create("audio/" + sheetName);
            }
        }
    }

    private static JsonArray MergeShaderIndexes(IEnumerable<JsonNode> indexes)
    {
        var shaders = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var index in indexes)
        {
            foreach (var shaderNode in Items(index))
            {
                if (shaderNode is not JsonObject shader) throw new InvalidDataException("Invalid shader registry entry.");
                var name = Text(shader["name"]);
                if (!shaders.TryGetValue(name, out var previous))
                {
                    shaders.Add(name, (JsonObject)shader.DeepClone());
                    continue;
                }
                var seen = new HashSet<string>(Items(previous["variants"]).Select(variant => variant?.ToJsonString() ?? "null"), StringComparer.Ordinal);
                foreach (var variant in Items(shader["variants"]))
                {
                    if (!seen.Add(variant?.ToJsonString() ?? "null")) continue;
                    previous["variants"] ??= new JsonArray();
                    previous["variants"]!.AsArray().Add(Clone(variant));
                }
            }
        }
        return new JsonArray(shaders.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => (JsonNode?)pair.Value).ToArray());
    }

    private static JsonObject CleanCommand(JsonObject command, JsonNode? sourceAdvId)
    {
        var result = new JsonObject
        {
            ["sourceAdvId"] = Clone(sourceAdvId), ["sourceRow"] = Clone(command["i"]), ["cmd"] = Clone(command["cmd"]),
            ["raw"] = Clone(command["raw"]) ?? JsonValue.Create(0),
        };
        foreach (var (key, value) in command)
        {
            if (PresetExcludedFields.Contains(key) || !Truthy(value) || value is JsonArray { Count: 0 }) continue;
            result[key] = Clone(value);
        }
        return result;
    }

    private static JsonArray UniqueCommands(IEnumerable<JsonObject> commands)
    {
        var result = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var command in commands)
        {
            var identity = (JsonObject)command.DeepClone();
            identity.Remove("sourceAdvId");
            identity.Remove("sourceRow");
            if (seen.Add(identity.ToJsonString())) result.Add((JsonNode)command);
        }
        return result;
    }

    private static JsonArray StageInfo(JsonObject stages)
    {
        var result = new JsonArray();
        foreach (var (name, prefab) in stages.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var root = Items(prefab?["nodes"]).FirstOrDefault();
            var adv = Items(root?["components"]).FirstOrDefault(component => Text(component?["class"]) == "AdvStage");
            result.Add((JsonNode)new JsonObject
            {
                ["name"] = name, ["fov"] = Clone(adv?["_fov"]), ["initialCameraPosition"] = Clone(adv?["_initialCameraPosition"]),
                ["initialCameraRotation"] = Clone(adv?["_initialCameraRotation"]), ["focusDataSettings"] = Clone(adv?["_usingFocusDataSettingsKey"]),
            });
        }
        return result;
    }

    private static async Task<JsonObject> InspectModelAsync(string siteDirectory, string id, string manifestPath, string address,
        CancellationToken cancellationToken)
    {
        var manifest = await ReadObjectAsync(Within(siteDirectory, manifestPath), cancellationToken).ConfigureAwait(false);
        var model = await LogicalAsync(siteDirectory, manifest, "model.json", cancellationToken).ConfigureAwait(false);
        var prefab = Truthy(model?["prefab"])
            ? await LogicalAsync(siteDirectory, manifest, Text(model?["prefab"]), cancellationToken).ConfigureAwait(false) : null;
        var root = Items(prefab?["nodes"]).FirstOrDefault();
        var components = Items(root?["components"]).ToArray();
        var character = components.FirstOrDefault(component => Text(component?["class"]) == "Live2DCharacter");
        var expression = components.FirstOrDefault(component => Text(component?["class"]) == "CubismExpressionController");
        var motions = Items(character?["_motionList"])
            .Select(motion => FirstText(motion?["clip"], motion?["name"], motion?["MotionName"]))
            .Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        var expressions = Items(expression?["ExpressionsList"]?["CubismExpressionObjects"])
            .Select(value => Regex.Replace(Text(value?["name"]), @"\.exp3$", "", RegexOptions.IgnoreCase))
            .Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        return new JsonObject
        {
            ["id"] = id, ["address"] = address,
            ["targetAssetName"] = address.StartsWith("Character/Live2D/", StringComparison.Ordinal) ? address[17..] : address,
            ["targetName"] = "director_" + Regex.Replace(id.StartsWith("adv_live2d_", StringComparison.Ordinal) ? id[11..] : id, "[^a-zA-Z0-9_]", "_"),
            ["character"] = Clone(manifest["model"]?["character"]), ["names"] = Clone(manifest["model"]?["names"]), ["label"] = Clone(manifest["model"]?["label"]),
            ["defaultMotion"] = FirstText(character?["DefaultMotionName"], motions.Length > 0 ? JsonValue.Create(motions[0]) : null),
            ["defaultExpression"] = FirstText(character?["DefaultExpressionName"], expressions.Length > 0 ? JsonValue.Create(expressions[0]) : null),
            ["motions"] = Strings(motions), ["expressions"] = Strings(expressions),
        };
    }

    private static string FirstText(params JsonNode?[] values) => values.Select(Text).FirstOrDefault(value => value.Length > 0) ?? "";

    private static JsonArray SyntheticCommands(Dictionary<string, JsonObject> media, JsonObject effectInstances, JsonArray actors)
    {
        var commands = new JsonArray();
        void Push(string name, int raw, JsonObject values)
        {
            var command = new JsonObject { ["i"] = commands.Count + 1, ["cmd"] = name, ["raw"] = raw, ["IsNoWait"] = 1 };
            foreach (var (key, value) in values) command[key] = Clone(value);
            commands.Add((JsonNode)command);
        }
        foreach (var actor in actors)
            Push("Character", 23, new JsonObject { ["TargetName"] = Clone(actor?["targetName"]), ["TargetAssetName"] = Clone(actor?["targetAssetName"]), ["TargetAssetIndex"] = 0 });
        foreach (var (name, _) in media["frames"]) Push("Frame", 44, new JsonObject { ["TargetAssetName"] = name });
        foreach (var (name, asset) in effectInstances)
        {
            if (!media["effects"].ContainsKey(Text(asset))) throw new InvalidDataException($"Effect instance {name} references an absent prefab: {asset}");
            Push("Effect", 54, new JsonObject { ["TargetName"] = name });
        }
        foreach (var (name, _) in media["postEffects"]) Push("PostEffect", 43, new JsonObject { ["TargetAssetName"] = name });
        foreach (var (name, _) in media["stills"]) Push("Still", 30, new JsonObject { ["TargetAssetName"] = name });
        foreach (var (id, _) in media["videos"]) Push("Movie", 26, new JsonObject { ["VideoID"] = VideoId(id) });
        return commands;
    }

    private static long VideoId(string id) => long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        ? value : throw new InvalidDataException($"Non-numeric video ID in archive: {id}");
}
