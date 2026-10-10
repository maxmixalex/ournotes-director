using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core.Archiving;

public sealed partial class ArchiveDownloader
{
    private sealed partial class Operation
    {
        private static readonly string[] MasterFiles =
        [
            "MasterBand.json", "MasterCharacter.json", "MasterStoryChapter.json", "MasterStoryEpisode.json",
            "MasterStoryFriendshipEpisode.json", "MasterCharacterFriendship.json", "MasterStoryLiveResultEpisode.json",
            "MasterStoryHomeSpotTapTalkEpisode.json", "MasterHomeSpot.json", "MasterAdv.json",
        ];

        public async Task<JsonObject> DiscoverAsync()
        {
            Report("discovery");
            var version = Object((await FetchJsonAsync(JoinUrl(_options.MetadataBase, "current_version.json"))).Value, "current_version.json");
            var regions = new JsonObject();
            var indexes = new JsonObject();
            var selected = new Dictionary<long, JsonObject>();
            var bands = new Dictionary<string, BandSelection>(StringComparer.Ordinal);
            BandSelection? canonicalBand = null;
            foreach (var site in _options.Sites)
            {
                _cancellation.ThrowIfCancellationRequested();
                var tablesRead = 0;
                Report("masterdata", site, processed: 0, total: MasterFiles.Length);
                var region = _options.MasterRegions[site];
                var versionNumber = Text(version["regions"]?[region.Region]?["version"]);
                var suffix = versionNumber.Length == 0 ? "" : "?v=" + Uri.EscapeDataString(versionNumber);
                var tables = await Task.WhenAll(MasterFiles.Select(async file =>
                {
                    var document = await FetchJsonAsync(JoinUrl(_options.MetadataBase, region.Path.Trim('/') + "/" + file) + suffix);
                    var rows = document.Value as JsonArray ?? document.Value["_allData"] as JsonArray;
                    if (rows is null) throw new InvalidDataException($"Expected an array or _allData array in {site}/{file}.");
                    Report("masterdata", site, file, Interlocked.Increment(ref tablesRead), MasterFiles.Length);
                    return (File: file, Rows: rows);
                }));
                var data = tables.ToDictionary(x => x.File, x => x.Rows, StringComparer.Ordinal);
                var band = SelectBand(data, site, canonicalBand);
                bands.Add(site, band);
                canonicalBand ??= band;
                var regionStories = SelectMasterStories(data, band);
                regions[site] = new JsonObject
                {
                    ["band"] = BandMetadata(band),
                    ["stories"] = new JsonArray(regionStories.OrderBy(x => x.Key).Select(x => (JsonNode?)x.Value.DeepClone()).ToArray()),
                };
                foreach (var (id, story) in regionStories)
                {
                    if (!selected.TryGetValue(id, out var merged))
                    {
                        merged = (JsonObject)story.DeepClone();
                        merged["regions"] = new JsonArray();
                        merged["sites"] = new JsonObject();
                        selected.Add(id, merged);
                    }
                    MergeArray(merged, "regions", StringArray([site]));
                    foreach (var field in new[] { "categories", "reasons", "characterIds" }) MergeArray(merged, field, story[field] as JsonArray);
                }
                var root = _options.SiteRoots[site].TrimEnd('/');
                Report("story-index", site, processed: 0, total: 1);
                var index = Object((await FetchJsonAsync(JoinUrl(root, "stories.json"))).Value, $"{site}/stories.json");
                if (index["stories"] is not JsonArray) throw new InvalidDataException($"No stories array in {site}/stories.json.");
                indexes[site] = new JsonObject { ["root"] = root, ["index"] = index.DeepClone() };
                Report("story-index", site, processed: 1, total: 1);
                foreach (var file in MasterFiles)
                    _documents.TryRemove(JoinUrl(_options.MetadataBase, region.Path.Trim('/') + "/" + file) + suffix, out _);
            }

            // Index grouping also captures stories that are absent from MasterData.
            foreach (var site in _options.Sites)
            {
                var band = bands[site];
                foreach (var entry in IndexEntries(indexes, site))
                {
                    var id = Number(entry["advId"]);
                    if (id is null or <= 0) continue;
                    var grouped = (entry["groups"] as JsonArray)?.Any(group =>
                        Number(group?["chapter"]?["bandId"]) == band.Id ||
                        (group?["characters"] as JsonArray)?.Any(character => band.CharacterIds.Contains(Number(character?["id"]) ?? -1)) == true) == true;
                    if (!grouped && !selected.ContainsKey(id.Value)) continue;
                    if (!selected.TryGetValue(id.Value, out var story))
                    {
                        story = NewStory(id.Value);
                        selected.Add(id.Value, story);
                    }
                    if (grouped) MergeArray(story, "reasons", StringArray(["story-index:group"]));
                    AttachSite(story, site, Text(indexes[site]?["root"]), entry);
                }
            }

            if (_options.DeepScan)
            {
                foreach (var site in _options.Sites)
                {
                    var root = Text(indexes[site]?["root"]);
                    var hits = new ConcurrentBag<JsonObject>();
                    var candidates = IndexEntries(indexes, site).Where(entry => Number(entry["advId"]) is long id && id > 0 && !selected.ContainsKey(id)).ToArray();
                    var scanned = 0;
                    Report("deep-scan", site, processed: 0, total: candidates.Length);
                    await Parallel.ForEachAsync(candidates, new ParallelOptions { MaxDegreeOfParallelism = _options.Concurrency, CancellationToken = _cancellation }, async (entry, token) =>
                    {
                        var id = Number(entry["advId"]);
                        if (id is null or <= 0) return;
                        var path = StoryPath(entry, id.Value);
                        var manifest = Object((await FetchJsonAsync(JoinUrl(root, path))).Value, path);
                        if (await HasBandModelAsync(root, manifest, bands[site].CharacterIds, new HashSet<string>(StringComparer.Ordinal))) hits.Add(entry);
                        _documents.TryRemove(JoinUrl(root, path), out _);
                        token.ThrowIfCancellationRequested();
                        Report("deep-scan", site, path, Interlocked.Increment(ref scanned), candidates.Length);
                    });
                    foreach (var entry in hits.OrderBy(x => Number(x["advId"])))
                    {
                        var id = Number(entry["advId"])!.Value;
                        if (!selected.TryGetValue(id, out var story))
                        {
                            story = NewStory(id);
                            story["categories"] = StringArray(["model-cameo"]);
                            story["reasons"] = StringArray(["manifest:model-character"]);
                            selected.Add(id, story);
                        }
                        AttachSite(story, site, root, entry);
                    }
                }
            }

            // A cameo found only in the last-scanned region still needs every other
            // available region. Attaching in a final pass avoids scan-order omissions.
            foreach (var site in _options.Sites)
                foreach (var entry in IndexEntries(indexes, site))
                    if (selected.TryGetValue(Number(entry["advId"]) ?? -1, out var story)) AttachSite(story, site, Text(indexes[site]?["root"]), entry);

            var actualBand = canonicalBand ?? throw new InvalidDataException("No master region was selected.");
            _documents.Clear();
            return new JsonObject
            {
                ["format"] = "millsage.discovery/1",
                ["generatedAt"] = DateTimeOffset.UtcNow.ToString("O"),
                ["source"] = new JsonObject { ["metadataBase"] = _options.MetadataBase, ["version"] = version["regions"]?.DeepClone() },
                ["band"] = BandMetadata(actualBand),
                ["regions"] = regions,
                ["stories"] = new JsonArray(selected.OrderBy(x => x.Key).Select(x => (JsonNode?)x.Value).ToArray()),
                ["indexes"] = indexes,
                ["archive"] = SelectionOptions(),
            };
        }

        private BandSelection SelectBand(Dictionary<string, JsonArray> data, string site, BandSelection? canonicalBand)
        {
            // Resolve a display name once, then use the stable text ID in subsequent
            // regions. A translated name need not appear in every region's table.
            var selector = canonicalBand is null ? _bandSelector : canonicalBand.TextId.Length > 0
                ? canonicalBand.TextId : canonicalBand.Id.ToString(CultureInfo.InvariantCulture);
            var available = data["MasterBand.json"].OfType<JsonObject>().Where(row => Id(row) is > 0)
                .OrderBy(row => Id(row)).ToArray();
            JsonObject[] matches;
            if (long.TryParse(selector, NumberStyles.Integer, CultureInfo.InvariantCulture, out var requestedId))
                matches = available.Where(row => Id(row) == requestedId).ToArray();
            else
            {
                matches = available.Where(row => BandNames(row).Any(name => name.Equals(selector, StringComparison.OrdinalIgnoreCase))).ToArray();
                if (matches.Length == 0)
                {
                    var normalized = NormalizeBandName(selector);
                    matches = normalized.Length == 0 ? [] : available.Where(row =>
                        BandNames(row).Any(name => NormalizeBandName(name) == normalized)).ToArray();
                }
            }
            if (matches.Length != 1)
            {
                var choices = string.Join(", ", available.Select(row =>
                    $"{Id(row)} ({BandToken(BandTextId(row), Id(row)!.Value)})"));
                var problem = matches.Length == 0 ? "was not found" : "matches more than one band";
                throw new ArgumentException($"Band '{selector}' {problem} in {site} MasterBand. Choose one band by ID or name. Available bands: {choices}.");
            }
            var selectedRow = matches[0];
            var bandId = Id(selectedRow)!.Value;
            var textId = BandTextId(selectedRow);
            var token = BandToken(textId, bandId);
            var displayName = DisplayNames(selectedRow).FirstOrDefault() ?? token;
            var characters = new JsonArray();
            var characterIds = new HashSet<long>();
            foreach (var character in data["MasterCharacter.json"].Where(x => Number(Pick(x, "_bandID", "_bandId", "bandID", "bandId")) == bandId))
            {
                var id = Id(character);
                if (id is null) continue;
                characterIds.Add(id.Value);
                var name = Text(Pick(character, "_nameTextID", "_nameTextId", "nameTextID", "nameTextId"));
                const string prefix = "Character_Name_";
                characters.Add((JsonNode)new JsonObject
                {
                    ["id"] = id.Value,
                    ["nameTextId"] = name,
                    ["shortNameTextId"] = Text(Pick(character, "_shortNameTextID", "_shortNameTextId", "shortNameTextID", "shortNameTextId")),
                    ["assetToken"] = (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? name[prefix.Length..] : name).ToLowerInvariant(),
                });
            }
            return new(bandId, textId, token, displayName, characters, characterIds);
        }

        private static string BandTextId(JsonNode row) => Text(Pick(row, "_nameTextID", "_nameTextId", "nameTextID", "nameTextId"));

        private static string BandToken(string textId, long id)
        {
            const string prefix = "Band_Name_";
            return textId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? textId[prefix.Length..]
                : textId.Length > 0 ? textId : id.ToString(CultureInfo.InvariantCulture);
        }

        private static IEnumerable<string> DisplayNames(JsonNode row)
        {
            static IEnumerable<string> Values(JsonNode? node)
            {
                if (node is JsonValue value && value.TryGetValue<string>(out var name) && !string.IsNullOrWhiteSpace(name)) yield return name;
                else if (node is JsonObject names)
                    foreach (var (_, child) in names)
                        foreach (var text in Values(child)) yield return text;
                else if (node is JsonArray array)
                    foreach (var child in array)
                        foreach (var text in Values(child)) yield return text;
            }
            foreach (var field in new[] { "_name", "name", "_displayName", "displayName", "_names", "names", "displayNames" })
                foreach (var name in Values(row[field])) yield return name;
        }

        private static IEnumerable<string> BandNames(JsonNode row)
        {
            var textId = BandTextId(row);
            if (textId.Length > 0)
            {
                yield return textId;
                yield return BandToken(textId, Id(row)!.Value);
            }
            foreach (var name in DisplayNames(row)) yield return name;
        }

        private static string NormalizeBandName(string name) =>
            new(name.Normalize(NormalizationForm.FormKC).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        private static JsonObject BandMetadata(BandSelection band) => new()
        {
            ["bandId"] = band.Id, ["bandTextId"] = band.TextId, ["token"] = band.Token,
            ["name"] = band.Name, ["characters"] = band.Characters.DeepClone(),
        };

        private static Dictionary<long, JsonObject> SelectMasterStories(Dictionary<string, JsonArray> data, BandSelection band)
        {
            var selected = new Dictionary<long, JsonObject>();
            var chapters = ById(data["MasterStoryChapter.json"]);
            var friendships = ById(data["MasterCharacterFriendship.json"]);
            var spots = ById(data["MasterHomeSpot.json"]);
            var advs = ById(data["MasterAdv.json"]);
            bool Owns(IEnumerable<long> ids) => ids.Any(band.CharacterIds.Contains);
            void Add(string category, long? id, IEnumerable<long> characterIds, JsonObject? extra = null)
            {
                if (id is null or <= 0) return;
                if (!selected.TryGetValue(id.Value, out var story)) { story = NewStory(id.Value); selected.Add(id.Value, story); }
                MergeArray(story, "categories", StringArray([category]));
                MergeArray(story, "reasons", StringArray(["masterdata:" + category]));
                MergeArray(story, "characterIds", NumberArray(characterIds));
                if (extra is not null) foreach (var (key, value) in extra) story[key] = value?.DeepClone();
            }

            foreach (var row in data["MasterStoryEpisode.json"])
            {
                var chapterId = Number(Pick(row, "_chapterId", "chapterId"));
                chapters.TryGetValue(chapterId ?? -1, out var chapter);
                var ids = Numbers(Pick(chapter, "_mainCharacterIds", "mainCharacterIds"));
                var single = Number(Pick(row, "_characterId", "characterId"));
                if (Number(Pick(chapter, "_bandId", "bandId")) != band.Id && !Owns(ids) && !band.CharacterIds.Contains(single ?? -1)) continue;
                var another = IsTrue(Pick(row, "_isAnotherEpisode", "isAnotherEpisode"));
                var extra = IsTrue(Pick(row, "_isExtraEpisode", "isExtraEpisode"));
                Add(IsTrue(Pick(chapter, "_isSpecialStory", "isSpecialStory")) ? "birthday" : "chapter", AdvId(row), another && single.HasValue ? [single.Value] : ids, new JsonObject
                {
                    ["chapterId"] = chapterId,
                    ["episodeNumber"] = Pick(row, "_episodeNumber", "episodeNumber")?.DeepClone(),
                    ["episodeKind"] = another ? "another" : extra ? "extra" : "main",
                });
            }
            foreach (var row in data["MasterStoryFriendshipEpisode.json"])
            {
                friendships.TryGetValue(Number(Pick(row, "_characterFriendshipId", "characterFriendshipId")) ?? -1, out var friendship);
                var ids = new[] { Number(Pick(friendship, "_masterCharacterIdA", "masterCharacterIdA")), Number(Pick(friendship, "_masterCharacterIdB", "masterCharacterIdB")) }
                    .Where(x => x.HasValue).Select(x => x!.Value).ToArray();
                if (Owns(ids)) Add("friendship", AdvId(row), ids, new JsonObject { ["episodeNumber"] = Pick(row, "_episodeNumber", "episodeNumber")?.DeepClone() });
            }
            foreach (var row in data["MasterStoryLiveResultEpisode.json"])
            {
                var ids = Numbers(Pick(row, "_characterIds", "characterIds"));
                if (Owns(ids)) Add("live-result", AdvId(row), ids);
            }
            foreach (var row in data["MasterStoryHomeSpotTapTalkEpisode.json"])
            {
                var spotId = Number(Pick(row, "_spotId", "spotId"));
                spots.TryGetValue(spotId ?? -1, out var spot);
                var character = Number(Pick(row, "_characterId", "characterId"));
                if (!band.CharacterIds.Contains(character ?? -1) && Number(Pick(spot, "_bandId", "bandId")) != band.Id && !Owns(Numbers(Pick(spot, "_characterIds", "characterIds")))) continue;
                Add("home-tap", AdvId(row), character.HasValue ? [character.Value] : [], new JsonObject { ["spotId"] = spotId });
            }
            foreach (var spot in data["MasterHomeSpot.json"])
            {
                var ids = Numbers(Pick(spot, "_characterIds", "characterIds"));
                if (Number(Pick(spot, "_bandId", "bandId")) == band.Id || Owns(ids)) Add("home-intro", AdvId(spot), ids, new JsonObject { ["spotId"] = Id(spot) });
            }
            foreach (var (id, story) in selected)
            {
                advs.TryGetValue(id, out var adv);
                story["asset"] = Text(Pick(adv, "_advEpisodeAsset", "advEpisodeAsset"));
                story["sheetName"] = Text(Pick(adv, "_sheetName", "sheetName"));
                story["titleTextId"] = Text(Pick(adv, "_titleTextId", "_titleTextID", "titleTextId", "titleTextID"));
                story["playbackMode"] = Pick(adv, "_playbackMode", "playbackMode")?.DeepClone();
            }
            return selected;
        }

        private static Dictionary<long, JsonNode?> ById(JsonArray rows) => rows.Where(x => Id(x).HasValue).GroupBy(x => Id(x)!.Value).ToDictionary(x => x.Key, x => x.Last());
        private static bool IsTrue(JsonNode? value) => string.Equals(Text(value), "true", StringComparison.OrdinalIgnoreCase) || Text(value) == "1";
        private static JsonObject NewStory(long id) => new()
        {
            ["advId"] = id, ["categories"] = new JsonArray(), ["reasons"] = new JsonArray(), ["characterIds"] = new JsonArray(),
            ["regions"] = new JsonArray(), ["sites"] = new JsonObject(),
        };

        private static void MergeArray(JsonObject target, string field, JsonArray? additions)
        {
            if (additions is null) return;
            if (target[field] is not JsonArray array) { array = new JsonArray(); target[field] = array; }
            var existing = array.Select(x => x?.ToJsonString()).ToHashSet(StringComparer.Ordinal);
            foreach (var value in additions) if (existing.Add(value?.ToJsonString())) array.Add(value?.DeepClone());
        }

        private static IEnumerable<JsonObject> IndexEntries(JsonObject indexes, string site) =>
            (indexes[site]?["index"]?["stories"] as JsonArray ?? []).OfType<JsonObject>();

        private static string StoryPath(JsonObject entry, long id) => ValidateManifest(Text(entry["manifest"], "stories/" + id.ToString(CultureInfo.InvariantCulture) + ".json"));

        private static void AttachSite(JsonObject story, string site, string root, JsonObject entry)
        {
            if (story["sites"] is not JsonObject sites) { sites = new JsonObject(); story["sites"] = sites; }
            var id = Number(entry["advId"]) ?? throw new InvalidDataException("Story index entry has no numeric advId.");
            var normalizedEntry = (JsonObject)entry.DeepClone();
            var path = StoryPath(entry, id);
            normalizedEntry["manifest"] = path;
            sites[site] = new JsonObject { ["root"] = root, ["manifest"] = path, ["entry"] = normalizedEntry };
            MergeArray(story, "regions", StringArray([site]));
        }

        private async Task<bool> HasBandModelAsync(string root, JsonObject manifest, HashSet<long> characters, HashSet<string> visited)
        {
            foreach (var path in ModelPaths(manifest))
            {
                if (!visited.Add(path)) continue;
                var model = Object((await FetchJsonAsync(JoinUrl(root, path))).Value, path);
                if (characters.Contains(Number(model["model"]?["character"]) ?? -1) || await HasBandModelAsync(root, model, characters, visited)) return true;
            }
            return false;
        }

        private static IEnumerable<string> ModelPaths(JsonObject manifest)
        {
            if (manifest["models"] is not JsonObject models) yield break;
            foreach (var (_, value) in models)
            {
                var path = ValidateManifest(Text(value));
                if (!path.StartsWith("models/", StringComparison.Ordinal)) throw new InvalidDataException($"Expected a model manifest: {path}");
                yield return path;
            }
        }

        private JsonObject SelectionOptions() => new()
        {
            ["languages"] = _languages is null ? null : StringArray(_languages.Order(StringComparer.Ordinal)),
            ["deepScan"] = _options.DeepScan,
        };

        private sealed record BandSelection(long Id, string TextId, string Token, string Name, JsonArray Characters, HashSet<long> CharacterIds);
    }
}
