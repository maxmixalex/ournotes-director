using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using static OurNotes.Director.Core.Archiving.ArchiveIndexStorage;

namespace OurNotes.Director.Core.Archiving;

/// <summary>Builds the searchable C# archive indexes directly from downloaded manifests and immutable assets.</summary>
public static class ArchiveIndexer
{
    private static readonly (string Language, string Field)[] TextFields =
    [
        ("ja", "japanese"), ("en", "english"), ("zh-Hant", "traditionalChinese"),
        ("zh-Hans", "simplifiedChinese"), ("ko", "korean"),
    ];
    private static readonly string[] RowIndexes = ["commands", "dialogue", "texts", "voices", "sounds", "files"];

    public static async Task<JsonObject> BuildAsync(string archiveDirectory, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(archiveDirectory);
        var discovery = await ReadObjectAsync(Path.Combine(root, "discovery.json"), cancellationToken).ConfigureAwait(false);
        var temporaryDirectory = Path.Combine(root, ".index-build-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var writers = new Dictionary<string, StreamWriter>(StringComparer.Ordinal);
        try
        {
            foreach (var name in RowIndexes)
                writers.Add(name, new StreamWriter(Path.Combine(temporaryDirectory, name + ".jsonl"), false, new UTF8Encoding(false), 65536));
            var stats = await BuildRowsAsync(root, discovery, temporaryDirectory, writers, cancellationToken).ConfigureAwait(false);
            foreach (var writer in writers.Values) await writer.DisposeAsync().ConfigureAwait(false);
            writers.Clear();
            cancellationToken.ThrowIfCancellationRequested();
            PublishDirectory(temporaryDirectory, Path.Combine(root, "index"));
            return stats;
        }
        finally
        {
            foreach (var writer in writers.Values) await writer.DisposeAsync().ConfigureAwait(false);
            if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static async Task<JsonObject> BuildRowsAsync(string root, JsonObject discovery, string indexDirectory,
        Dictionary<string, StreamWriter> writers, CancellationToken cancellationToken)
    {
        var catalog = new JsonArray();
        var selected = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var entry in Items(discovery["stories"]))
            if (entry?["advId"] is not null) selected[Text(entry["advId"])] = entry;
        var models = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var modelManifests = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var counts = RowIndexes.ToDictionary(key => key, _ => 0L, StringComparer.Ordinal);
        var siteCounts = new JsonObject();
        var languages = discovery["archive"]?["languages"] is JsonArray languageArray
            ? new HashSet<string>(languageArray.Select(Text), StringComparer.Ordinal) : null;

        var sites = discovery["indexes"] is JsonObject indexes
            ? indexes.Select(pair => pair.Key).ToArray()
            : new[] { "intl", "jp" };
        foreach (var site in sites)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var siteDirectory = SiteDirectory(root, site);
            var storiesPath = Path.Combine(siteDirectory, "stories.json");
            if (!File.Exists(storiesPath)) continue;
            var storyIndex = await ReadObjectAsync(storiesPath, cancellationToken).ConfigureAwait(false);
            var stories = Items(storyIndex["stories"]).ToArray();
            siteCounts[site] = stories.Length;
            foreach (var entryNode in stories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entryNode is not JsonObject entry) throw new InvalidDataException($"Invalid story index entry in {storiesPath}");
                var manifestPath = Text(entry["manifest"]);
                var manifest = await ReadObjectAsync(Within(siteDirectory, manifestPath), cancellationToken).ConfigureAwait(false);
                var storyEntry = LogicalEntry(manifest, "story.json");
                var episodeEntry = LogicalEntry(manifest, "episode.json");
                if (storyEntry is null || episodeEntry is null) continue;
                var story = await MaterializeAsync(siteDirectory, storyEntry, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException($"Null story document: {manifestPath}");
                var episode = await MaterializeAsync(siteDirectory, episodeEntry, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException($"Null episode document: {manifestPath}");
                var commands = Items(episode["commands"]).ToArray();
                var advId = entry["advId"] ?? manifest["advId"];
                var sounds = new Dictionary<string, JsonObject?>(StringComparer.Ordinal);
                var cueDocuments = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);

                async Task<JsonObject?> SoundAsync(JsonNode? id)
                {
                    var key = Text(id);
                    if (!sounds.TryGetValue(key, out var sound))
                    {
                        sound = await ResolveSoundAsync(siteDirectory, manifest, story, episode, id, cueDocuments, languages, cancellationToken).ConfigureAwait(false);
                        sounds.Add(key, sound);
                    }
                    return sound;
                }

                foreach (var (textId, text) in Properties(episode["text"]))
                {
                    await WriteRowAsync(writers["texts"], new JsonObject
                    {
                        ["site"] = site, ["advId"] = Clone(advId), ["textId"] = textId, ["text"] = TextMap(text),
                    }, cancellationToken).ConfigureAwait(false);
                    counts["texts"]++;
                }

                foreach (var commandNode in commands)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (commandNode is not JsonObject command) throw new InvalidDataException($"Invalid command in {manifestPath}");
                    var originalCommand = new JsonObject { ["site"] = site, ["advId"] = Clone(advId) };
                    foreach (var (key, value) in command) originalCommand[key] = Clone(value);
                    await WriteRowAsync(writers["commands"], originalCommand, cancellationToken).ConfigureAwait(false);
                    counts["commands"]++;

                    var voiceIds = command["VoiceIDs"] as JsonArray ?? new JsonArray();
                    var voiceRows = new JsonArray();
                    foreach (var voiceId in voiceIds)
                    {
                        var resolved = await SoundAsync(voiceId).ConfigureAwait(false);
                        if (resolved is not null) voiceRows.Add(Clone(resolved));
                    }
                    var textId = command["AdvTextID"];
                    var text = command["lines"] ?? episode["text"]?[Text(textId)];
                    if (voiceIds.Count > 0)
                    {
                        await WriteRowAsync(writers["voices"], new JsonObject
                        {
                            ["site"] = site, ["advId"] = Clone(advId), ["commandIndex"] = Clone(command["i"]),
                            ["command"] = Clone(command["cmd"]), ["speakerTarget"] = Clone(command["TargetName"]),
                            ["textId"] = Clone(textId), ["text"] = Truthy(textId) ? TextMap(text) : null,
                            ["voiceIds"] = Clone(voiceIds), ["voiceCues"] = Clone(command["voiceCues"]) ?? new JsonArray(),
                            ["voices"] = Clone(voiceRows),
                        }, cancellationToken).ConfigureAwait(false);
                        counts["voices"] += voiceIds.Count;
                    }

                    foreach (var kind in new[] { "Bgm", "Se", "Voice" })
                    {
                        var ids = kind == "Voice" ? Items(voiceIds) : new[] { command[kind + "ID"] };
                        foreach (var id in ids)
                        {
                            if (!PositiveNumber(id)) continue;
                            var row = new JsonObject
                            {
                                ["site"] = site, ["advId"] = Clone(advId), ["commandIndex"] = Clone(command["i"]), ["kind"] = kind,
                            };
                            foreach (var (key, value) in Properties(await SoundAsync(id).ConfigureAwait(false))) row[key] = Clone(value);
                            await WriteRowAsync(writers["sounds"], row, cancellationToken).ConfigureAwait(false);
                            counts["sounds"]++;
                        }
                    }

                    if (Text(command["cmd"]) != "Talk" || Truthy(command["IgnoreData"]) || !Truthy(textId)) continue;
                    await WriteRowAsync(writers["dialogue"], new JsonObject
                    {
                        ["site"] = site, ["advId"] = Clone(advId), ["commandIndex"] = Clone(command["i"]),
                        ["speakerTarget"] = Clone(command["TargetName"]), ["textId"] = Clone(textId), ["text"] = TextMap(text),
                        ["voiceIds"] = Clone(voiceIds), ["voiceCues"] = Clone(command["voiceCues"]) ?? new JsonArray(),
                        ["voices"] = Clone(voiceRows), ["motion"] = Clone(command["MotionName"]), ["expression"] = Clone(command["ExpressionName"]),
                    }, cancellationToken).ConfigureAwait(false);
                    counts["dialogue"]++;
                }

                counts["files"] += await AddFileRowsAsync(writers["files"], site, advId, "story-common", manifest["files"], null, null, cancellationToken).ConfigureAwait(false);
                foreach (var (language, group) in Properties(manifest["languages"]))
                {
                    if (languages is not null && !languages.Contains(language)) continue;
                    counts["files"] += await AddFileRowsAsync(writers["files"], site, advId, "story-language", group?["files"], language, null, cancellationToken).ConfigureAwait(false);
                }
                foreach (var (modelId, pathNode) in Properties(manifest["models"]))
                {
                    var modelPath = Text(pathNode);
                    var cacheKey = site + ":" + modelPath;
                    if (!modelManifests.TryGetValue(cacheKey, out var modelManifest))
                    {
                        modelManifest = await ReadObjectAsync(Within(siteDirectory, modelPath), cancellationToken).ConfigureAwait(false);
                        modelManifests.Add(cacheKey, modelManifest);
                    }
                    var modelKey = site + ":" + modelId;
                    if (!models.ContainsKey(modelKey))
                    {
                        var modelRow = new JsonObject
                        {
                            ["site"] = site, ["id"] = modelId, ["manifest"] = modelPath, ["key"] = Clone(modelManifest["key"]),
                        };
                        foreach (var (key, value) in Properties(modelManifest["model"])) modelRow[key] = Clone(value);
                        modelRow["logicalFiles"] = Strings(Properties(modelManifest["files"]).Select(pair => pair.Key));
                        models.Add(modelKey, modelRow);
                    }
                    counts["files"] += await AddFileRowsAsync(writers["files"], site, advId, "model", modelManifest["files"], null, modelId, cancellationToken).ConfigureAwait(false);
                }

                var selectedStory = selected.GetValueOrDefault(Text(advId));
                catalog.Add((JsonNode)new JsonObject
                {
                    ["site"] = site, ["advId"] = Clone(advId), ["manifest"] = manifestPath,
                    ["titles"] = Clone(entry["titles"] ?? manifest["story"]?["titles"]) ?? new JsonObject(),
                    ["groups"] = Clone(entry["groups"] ?? manifest["story"]?["groups"]) ?? new JsonArray(),
                    ["languages"] = Strings((entry["languages"] is JsonArray availableLanguages
                        ? availableLanguages.Select(Text) : Properties(manifest["languages"]).Select(pair => pair.Key))
                        .Where(language => languages is null || languages.Contains(language))),
                    ["audio"] = Clone(manifest["audio"]), ["audioFormat"] = Clone(manifest["audioFormat"]),
                    ["commands"] = Clone(manifest["story"]?["commands"]) ?? new JsonArray(),
                    ["commandCount"] = Clone(manifest["story"]?["commandCount"]) ?? JsonValue.Create(commands.Length),
                    ["models"] = Strings(Properties(manifest["models"]).Select(pair => pair.Key)),
                    ["selection"] = selectedStory is null ? null : new JsonObject
                    {
                        ["categories"] = Clone(selectedStory["categories"]) ?? new JsonArray(),
                        ["reasons"] = Clone(selectedStory["reasons"]) ?? new JsonArray(),
                        ["characterIds"] = Clone(selectedStory["characterIds"]) ?? new JsonArray(),
                    },
                });
            }
        }

        var stats = new JsonObject { ["stories"] = catalog.Count };
        foreach (var (key, count) in counts) stats[key] = count;
        stats["models"] = models.Count;
        stats["sites"] = siteCounts;
        await WriteJsonAsync(Path.Combine(indexDirectory, "catalog.json"), new JsonObject
        {
            ["format"] = "millsage.catalog/1", ["generatedAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["band"] = Clone(discovery["band"]), ["stories"] = catalog,
        }, cancellationToken).ConfigureAwait(false);
        await WriteJsonAsync(Path.Combine(indexDirectory, "models.json"), new JsonObject
        {
            ["format"] = "millsage.models/1", ["models"] = new JsonArray(models.Values.Select(model => (JsonNode?)model).ToArray()),
        }, cancellationToken).ConfigureAwait(false);
        await WriteJsonAsync(Path.Combine(indexDirectory, "stats.json"), stats, cancellationToken).ConfigureAwait(false);
        return stats;
    }

    private static JsonObject TextMap(JsonNode? row)
    {
        var result = new JsonObject();
        foreach (var (language, field) in TextFields) result[language] = Clone(row?[field]);
        return result;
    }

    private static async Task<JsonObject?> ResolveSoundAsync(string siteDirectory, JsonObject manifest, JsonNode story,
        JsonNode episode, JsonNode? soundId, Dictionary<string, JsonNode?> cueDocuments, IReadOnlySet<string>? languages, CancellationToken cancellationToken)
    {
        var sound = episode["sounds"]?[Text(soundId)];
        if (sound is null) return null;
        var result = new JsonObject { ["soundId"] = Clone(soundId), ["cue"] = Clone(sound["_cueName"]) };
        var sheet = episode["cuesheets"]?[Text(sound["_soundCueSheetID"])]?["_cueSheetName"];
        if (!Truthy(sheet)) return result;
        result["sheet"] = Clone(sheet);
        var directory = story["audio"]?[Text(sheet)];
        if (!Truthy(directory)) return result;
        var cuesPath = Text(directory) + "/cues.json";
        var cuesEntry = LogicalEntry(manifest, cuesPath, includeLanguages: true, languages);
        if (cuesEntry is null)
        {
            result["dir"] = Clone(directory);
            return result;
        }
        if (!cueDocuments.TryGetValue(cuesPath, out var cues))
        {
            cues = await MaterializeAsync(siteDirectory, cuesEntry, cancellationToken).ConfigureAwait(false);
            cueDocuments.Add(cuesPath, cues);
        }
        var cue = cues?[Text(sound["_cueName"])];
        var logicalPath = Truthy(cue?["file"]) ? Text(directory) + "/" + Text(cue?["file"]) : null;
        var file = logicalPath is null ? null : LogicalEntry(manifest, logicalPath, includeLanguages: true, languages);
        result["logicalPath"] = logicalPath;
        result["asset"] = Clone(file?["asset"]);
        foreach (var field in new[] { "sampleRate", "channels", "samples" })
            if (cue?[field] is not null) result[field] = Clone(cue[field]);
        return result;
    }

    private static async Task<long> AddFileRowsAsync(StreamWriter writer, string site, JsonNode? advId, string scope,
        JsonNode? files, string? language, string? modelId, CancellationToken cancellationToken)
    {
        long count = 0;
        foreach (var (logicalPath, entry) in Properties(files))
        {
            if (entry is null) continue;
            var parts = entry["parts"] as JsonArray;
            if (entry["asset"] is null && parts is null) continue;
            var entries = entry["asset"] is not null ? new JsonNode?[] { entry } : Items(parts);
            foreach (var assetEntry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var part = assetEntry as JsonArray;
                if (part is not null && part.Count < 3) throw new InvalidDataException($"Invalid split asset part for {logicalPath}");
                await WriteRowAsync(writer, new JsonObject
                {
                    ["site"] = site, ["advId"] = Clone(advId), ["scope"] = scope, ["modelId"] = modelId, ["language"] = language,
                    ["logicalPath"] = logicalPath, ["kind"] = Classify(logicalPath),
                    ["asset"] = Clone(part is not null ? part[1] : assetEntry?["asset"]),
                    ["size"] = Clone(part is not null ? part[2] : assetEntry?["size"]),
                    ["stored"] = Clone(part is not null ? (part.Count > 3 ? part[3] : null) : assetEntry?["stored"]),
                }, cancellationToken).ConfigureAwait(false);
                count++;
            }
        }
        return count;
    }

    private static string Classify(string path) => Path.GetExtension(path) switch
    {
        ".m4a" or ".flac" => "audio", ".png" => path.Contains("fonts/", StringComparison.Ordinal) ? "font-atlas" : "image",
        ".webm" => "video", ".moc3" => "moc3", ".json" => "json", ".glsl" => "shader", _ => "other",
    };

    private static ValueTask WriteRowAsync(StreamWriter writer, JsonNode row, CancellationToken cancellationToken) =>
        new(writer.WriteLineAsync(row.ToJsonString().AsMemory(), cancellationToken));
}
