using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

/// <summary>
/// A snapshot of the home spots already present in an archive. The published story manifests
/// are the source of truth, so archives created before the home command need no migration.
/// Requested metadata is cached per catalog; textures and skeletons are read only when requested.
/// </summary>
public sealed class HomeCatalog(Archive archive)
{
    private readonly Archive _archive = archive;
    private readonly Dictionary<string, JsonObject> _manifests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonObject> _documents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HomeScene> _scenes = new(StringComparer.Ordinal);
    private Dictionary<string, List<HomeSource>>? _sources;

    public JsonArray List(string? site = null)
    {
        if (site is not null) _archive.SitePath(site, "stories.json");
        return new JsonArray(Sources.Values
            .Where(sources => site is null || sources[0].Site == site)
            .OrderBy(sources => sources[0].Site, StringComparer.Ordinal)
            .ThenBy(sources => sources[0].SpotId)
            .Select(sources => (JsonNode?)Open(sources[0].SpotId, sources[0].Site).ToJson())
            .ToArray());
    }

    /// <summary>Uses the international source first when the caller does not specify a site.</summary>
    public HomeScene Open(long spotId, string? site = null, long? advId = null)
    {
        if (spotId <= 0) throw new ArgumentOutOfRangeException(nameof(spotId), "A home spot ID must be positive.");
        if (site is not null) _archive.SitePath(site, "stories.json");
        var choices = Sources.Values.Where(s => s[0].SpotId == spotId && (site is null || s[0].Site == site))
            .OrderBy(s => s[0].Site == "intl" ? 0 : 1).ThenBy(s => s[0].Site, StringComparer.Ordinal).ToArray();
        if (choices.Length == 0) throw new FileNotFoundException($"Home spot {spotId} is not in the archive{(site is null ? "" : " for site " + site)}.");
        var sources = choices.FirstOrDefault(s => advId is null || s.Any(source => source.AdvId == advId))
            ?? throw new ArgumentException($"Story {advId} is not a source of home spot {spotId}.");
        var source = advId.HasValue ? sources.First(s => s.AdvId == advId) : sources[0];
        var cacheKey = source.Site + ":" + source.ManifestPath;
        if (_scenes.TryGetValue(cacheKey, out var cached)) return cached;
        var manifest = Manifest(source.Site, source.ManifestPath);
        var hostPath = manifest["host"] is JsonObject declaration ? Text(declaration["doc"], "host/host.json") : "host/host.json";
        var host = Document(source.Site, manifest, hostPath);
        if (Text(host["kind"]) != "home" || host["home"] is not JsonObject home)
            throw new InvalidDataException($"{source.Site}/{source.ManifestPath} does not describe a home host.");
        var spotPath = Text(home["spot"], "host/spot/spot.json");
        var spot = Document(source.Site, manifest, spotPath);
        if (Number(spot["spotId"] ?? spot["master"]?["_id"]) != spotId || Number(home["spotId"]) != spotId)
            throw new InvalidDataException($"Home spot ID differs between the index, host and spot in {source.ManifestPath}.");
        var scene = new HomeScene(_archive, source, manifest, host, spot, spotPath, sources.AsReadOnly());
        _scenes.Add(cacheKey, scene);
        return scene;
    }

    private Dictionary<string, List<HomeSource>> Sources => _sources ??= Discover();

    private Dictionary<string, List<HomeSource>> Discover()
    {
        var result = new Dictionary<string, List<HomeSource>>(StringComparer.Ordinal);
        var sitesDirectory = Path.Combine(_archive.Root, "sites");
        if (!Directory.Exists(sitesDirectory)) return result;
        foreach (var directory in Directory.EnumerateDirectories(sitesDirectory).Order(StringComparer.Ordinal))
        {
            var site = Path.GetFileName(directory);
            var indexPath = _archive.SitePath(site, "stories.json");
            if (!File.Exists(indexPath)) continue;
            var index = Archive.ReadJson(indexPath);
            if (index["stories"] is not JsonArray entries) throw new InvalidDataException($"No stories array in {indexPath}.");
            foreach (var entry in entries.OfType<JsonObject>())
            {
                var advId = Number(entry["advId"]);
                if (advId is null or <= 0) continue;
                var manifestPath = Text(entry["manifest"], $"stories/{advId.Value.ToString(CultureInfo.InvariantCulture)}.json");
                // Discovery scans old archives without an extra on-disk home index. Do not
                // retain every unrelated story manifest and host in this catalog's caches.
                var manifest = Archive.ReadJson(_archive.SitePath(site, manifestPath)) as JsonObject
                    ?? throw new InvalidDataException($"Invalid manifest {site}/{manifestPath}.");
                var declaredHost = manifest["host"];
                var hostKind = declaredHost is JsonObject ? Text(declaredHost["kind"]) : "";
                if (hostKind.Length > 0 && hostKind != "home") continue;
                var hostPath = declaredHost is JsonObject ? Text(declaredHost["doc"], "host/host.json") : "host/host.json";
                if (manifest["files"]?[hostPath] is null) continue;
                // The host is authoritative even for old indexes without spot groups, or a talk
                // assigned to a character who has no tap target in the scene.
                var host = Document(site, manifest, hostPath, cache: false);
                if (Text(host["kind"]) != "home" || host["home"] is not JsonObject home)
                {
                    if (hostKind == "home") throw new InvalidDataException($"The home declaration and host kind differ in {site}/{manifestPath}.");
                    continue;
                }
                var spotId = Number(home["spotId"]);
                if (spotId is null or <= 0) throw new InvalidDataException($"No home spot ID in {site}/{manifestPath}.");
                var key = site + ":" + spotId.Value.ToString(CultureInfo.InvariantCulture);
                if (!result.TryGetValue(key, out var sources)) result.Add(key, sources = []);
                if (sources.Any(s => s.ManifestPath == manifestPath)) continue;
                sources.Add(new HomeSource(site, spotId.Value, advId.Value, manifestPath, Text(home["talk"])));
            }
        }
        foreach (var sources in result.Values)
            sources.Sort((a, b) =>
            {
                // Area talks carry the same complete scene without selecting an individual tap target.
                var order = (a.Talk == "area" ? 0 : 1).CompareTo(b.Talk == "area" ? 0 : 1);
                return order != 0 ? order : a.AdvId.CompareTo(b.AdvId);
            });
        return result;
    }

    private JsonObject Manifest(string site, string relative)
    {
        var key = site + ":" + relative;
        if (!_manifests.TryGetValue(key, out var document))
        {
            document = Archive.ReadJson(_archive.SitePath(site, relative)) as JsonObject
                ?? throw new InvalidDataException($"Invalid manifest {site}/{relative}.");
            _manifests.Add(key, document);
        }
        return document;
    }

    private JsonObject Document(string site, JsonObject manifest, string logicalPath, bool cache = true)
    {
        var entry = manifest["files"]?[logicalPath]
            ?? throw new FileNotFoundException($"Home metadata not found: {logicalPath}.");
        var key = site + ":" + entry.ToJsonString();
        if (!_documents.TryGetValue(key, out var document))
        {
            document = JsonNode.Parse(_archive.Materialize(site, entry)) as JsonObject
                ?? throw new InvalidDataException($"Invalid home document {logicalPath}.");
            if (cache) _documents.Add(key, document);
        }
        return document;
    }

    internal static string Text(JsonNode? value, string fallback = "") => value?.ToString() is { Length: > 0 } text ? text : fallback;
    internal static long? Number(JsonNode? value) => long.TryParse(value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;
}

public sealed record HomeSource(string Site, long SpotId, long AdvId, string ManifestPath, string Talk)
{
    public JsonObject ToJson() => new()
    {
        ["site"] = Site, ["spotId"] = SpotId, ["advId"] = AdvId, ["manifest"] = ManifestPath, ["talk"] = Talk,
    };
}

public sealed class HomeCharacter
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _sharedMembers;
    public string Key { get; }
    public string Name { get; }
    public string Association { get; }
    /// <summary>The spot prefab's original ID; it is not rewritten using a playable-character table.</summary>
    public long? SourceCharacterId { get; }
    public IReadOnlyList<JsonObject> Targets { get; }
    public IReadOnlyList<JsonObject> Parts { get; }
    public IReadOnlyList<string> PartPaths => Parts.Select(p => HomeCatalog.Text(p["path"])).ToArray();
    /// <summary>These instances contain more than one tap character and require a slot filter.</summary>
    public IReadOnlyList<string> SharedPartPaths => _sharedMembers.Keys.ToArray();

    internal HomeCharacter(string key, string name, long? characterId, IReadOnlyList<JsonObject> targets,
        IReadOnlyList<JsonObject> parts, IReadOnlyDictionary<string, IReadOnlyList<string>> sharedMembers, string association)
    {
        Key = key; Name = name; SourceCharacterId = characterId; Targets = targets; Parts = parts;
        _sharedMembers = sharedMembers; Association = association;
    }

    internal IReadOnlyList<string>? SharedMembers(JsonObject part) => _sharedMembers.GetValueOrDefault(HomeCatalog.Text(part["path"]));

    public JsonObject ToJson() => new()
    {
        ["key"] = Key, ["name"] = Name, ["sourceCharacterId"] = SourceCharacterId, ["association"] = Association,
        ["parts"] = new JsonArray(Parts.Select(p => (JsonNode?)new JsonObject
        {
            ["path"] = p["path"]?.DeepClone(), ["skeletonData"] = p["skeletonData"]?.DeepClone(),
            ["animation"] = p["animation"]?["_animationName"]?.DeepClone(),
            ["sharedSkeleton"] = SharedMembers(p) is not null,
        }).ToArray()),
        ["targetPaths"] = new JsonArray(Targets.Select(p => p["path"]?.DeepClone()).ToArray()),
    };
}

public sealed class HomeScene
{
    private readonly Archive _archive;
    private readonly Dictionary<string, HomeSlotMap> _slotMaps = new(StringComparer.Ordinal);
    public string Site { get; }
    public long SpotId { get; }
    public long AdvId { get; }
    public string ManifestPath { get; }
    public string SpotPath { get; }
    public string SpineDirectory { get; }
    public JsonObject Manifest { get; }
    public JsonObject Host { get; }
    public JsonObject Home => Host["home"]!.AsObject();
    public JsonObject Spot { get; }
    public IReadOnlyList<HomeSource> Sources { get; }
    public IReadOnlyList<HomeCharacter> Characters { get; }
    public IReadOnlyList<JsonObject> SpineParts { get; }
    /// <summary>Backgrounds and props without an explicit character association; included in a full scene.</summary>
    public IReadOnlyList<JsonObject> UnassignedParts { get; }

    internal HomeScene(Archive archive, HomeSource source, JsonObject manifest, JsonObject host,
        JsonObject spot, string spotPath, IReadOnlyList<HomeSource> sources)
    {
        _archive = archive; Site = source.Site; SpotId = source.SpotId; AdvId = source.AdvId;
        ManifestPath = source.ManifestPath; Manifest = manifest; Host = host; Spot = spot; SpotPath = spotPath;
        Sources = sources;
        var slash = spotPath.LastIndexOf('/');
        SpineDirectory = (slash < 0 ? "" : spotPath[..(slash + 1)]) + "spine";
        SpineParts = (spot["spineCharacters"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(p => p["skeletonData"] is not null && p["animation"] is JsonObject && p["world"] is JsonArray).ToArray();
        Characters = GroupCharacters(spot, SpineParts);
        var assigned = Characters.SelectMany(c => c.Parts).ToHashSet();
        UnassignedParts = SpineParts.Where(p => !assigned.Contains(p)).ToArray();
    }

    public byte[] Read(string logicalPath) => _archive.Logical(Site, Manifest, logicalPath);
    public JsonObject ReadJson(string logicalPath) => JsonNode.Parse(Read(logicalPath)) as JsonObject
        ?? throw new InvalidDataException($"Expected a JSON object: {logicalPath}.");

    /// <summary>
    /// Null means an exclusive skeleton, an empty set means this shared instance has no artwork
    /// for the selected character. Keep the entire skeleton updating and filter only emitted slots;
    /// shared bones and constraints can drive another character's hand or held prop.
    /// </summary>
    public IReadOnlySet<string>? IncludedSlots(HomeCharacter character, JsonObject part)
    {
        if (!Characters.Contains(character) || !character.Parts.Contains(part))
            throw new ArgumentException("The character and Spine part must belong to this home scene.");
        var members = character.SharedMembers(part);
        if (members is null) return null;
        var map = SlotMap(part, members);
        if (map.UnsupportedReason is not null) throw new NotSupportedException(map.UnsupportedReason);
        return map.SlotsByCharacter[character.Name];
    }

    private HomeSlotMap SlotMap(JsonObject part, IReadOnlyList<string> members)
    {
        var path = HomeCatalog.Text(part["path"]);
        if (_slotMaps.TryGetValue(path, out var cached)) return cached;
        var name = HomeCatalog.Text(part["skeletonData"]);
        var skeleton = (Spot["skeletons"] as JsonArray ?? []).OfType<JsonObject>()
            .SingleOrDefault(s => HomeCatalog.Text(s["name"]) == name)
            ?? throw new InvalidDataException($"No skeleton definition for shared home part '{path}'.");
        var file = HomeCatalog.Text(skeleton["skeleton"]);
        if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Character isolation for '{path}' needs named slots and bones in a Spine JSON skeleton.");
        var result = HomeSlotOwnership.Resolve(ReadJson(SpineDirectory + "/" + file), members, path);
        _slotMaps.Add(path, result);
        return result;
    }

    public HomeCharacter SelectCharacter(string selector)
    {
        var matches = Characters.Where(c => string.Equals(c.Key, selector, StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.Name, selector, StringComparison.OrdinalIgnoreCase)
            || c.Targets.Any(t => string.Equals(HomeCatalog.Text(t["path"]), selector, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (matches.Length == 0 && long.TryParse(selector, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            matches = Characters.Where(c => c.SourceCharacterId == id).ToArray();
        if (matches.Length > 1) throw new ArgumentException($"Character '{selector}' is ambiguous; use a character key from home show.");
        return matches.SingleOrDefault() ?? throw new ArgumentException($"Character '{selector}' is not in home spot {SpotId}. Available: {string.Join(", ", Characters.Select(c => c.Key))}.");
    }

    public JsonObject ToJson(bool includeSlotFilters = false)
    {
        var characters = new JsonArray();
        foreach (var character in Characters)
        {
            var description = character.ToJson();
            if (includeSlotFilters)
            {
                try
                {
                    var filters = new JsonArray();
                    foreach (var part in character.Parts)
                    {
                        var slots = IncludedSlots(character, part);
                        if (slots is null) continue;
                        var map = SlotMap(part, character.SharedMembers(part)!);
                        filters.Add((JsonNode)new JsonObject
                        {
                            ["path"] = part["path"]?.DeepClone(),
                            ["includedSlots"] = new JsonArray(slots.Order(StringComparer.Ordinal).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
                            ["sharedSceneSlots"] = new JsonArray(map.SharedSceneSlots.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
                        });
                    }
                    description["exportSupported"] = true;
                    description["slotFilters"] = filters;
                }
                catch (NotSupportedException error)
                {
                    description["exportSupported"] = false;
                    description["unsupportedReason"] = error.Message;
                }
            }
            characters.Add((JsonNode)description);
        }
        return new JsonObject
        {
            ["site"] = Site, ["spotId"] = SpotId, ["advId"] = AdvId, ["manifest"] = ManifestPath,
            ["names"] = Spot["name"]?.DeepClone() ?? new JsonObject(), ["bandId"] = Spot["master"]?["_bandId"]?.DeepClone(),
            ["characters"] = characters, ["spinePartCount"] = SpineParts.Count,
            ["unassignedParts"] = new JsonArray(UnassignedParts.Select(p => p["path"]?.DeepClone()).ToArray()),
            ["sources"] = new JsonArray(Sources.Select(s => (JsonNode?)s.ToJson()).ToArray()),
        };
    }

    /// <summary>
    /// The game's character boundary is the SpotCharacter path. A character can have several
    /// sibling SkeletonAnimations (front/back, limbs, shadows and cables); filenames alone or
    /// MasterCharacter IDs are insufficient. Derive the prefix from the targets in this spot.
    /// </summary>
    internal static IReadOnlyList<HomeCharacter> GroupCharacters(JsonObject spot, IReadOnlyList<JsonObject> parts)
    {
        var targets = (spot["characters"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(t => HomeCatalog.Text(t["path"]).Length > 0).ToArray();
        var seeds = targets.Select(t => new CharacterSeed(t)).Concat(VerifiedNonInteractive(spot, parts)).ToArray();
        // Prefixes such as "character_1" and "character_2" must not collapse merely because
        // they share the first word. A shared short prefix is safe only for the same original ID.
        foreach (var ambiguous in seeds.GroupBy(s => s.Parent + "/" + s.Stem, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Select(s => s.Id).Distinct().Count() > 1))
            foreach (var seed in ambiguous) seed.Stem = seed.Leaf;

        var groups = seeds.GroupBy(s => s.Parent + "/" + s.Stem, StringComparer.OrdinalIgnoreCase)
            .Select(group => new CharacterGroup(group.ToArray())).ToArray();
        var sharedFamilies = new List<(string Prefix, CharacterGroup[] Members)>();
        var assignedParts = new HashSet<JsonObject>();
        foreach (var part in parts)
        {
            var path = HomeCatalog.Text(part["path"]);
            // In a shared skeleton, the prefab places each tap target below the instance's
            // Spine GameObject. This ancestry explicitly identifies the participating people.
            var descendants = groups.Where(g => g.Seeds.Any(s => s.Path.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase))).ToArray();
            if (descendants.Length > 0)
            {
                Associate(part, descendants);
                if (descendants.Length > 1)
                {
                    var slash = path.LastIndexOf('/');
                    var leaf = path[(slash + 1)..];
                    var boundary = leaf.TakeWhile(c => !IsBoundary(c)).Count();
                    if (boundary > 0) sharedFamilies.Add((path[..(slash + 1)] + leaf[..boundary], descendants));
                }
                continue;
            }
            var exact = groups.Where(g => g.Seeds.Any(s => PathMatch(path, s.Path))).ToArray();
            var matches = exact.Length > 0 ? exact : groups.Where(g => PathMatch(path, g.Parent.Length == 0 ? g.Stem : g.Parent + "/" + g.Stem)).ToArray();
            if (matches.Length > 1)
            {
                var longest = matches.Max(g => g.Parent.Length + g.Stem.Length);
                matches = matches.Where(g => g.Parent.Length + g.Stem.Length == longest).ToArray();
            }
            if (matches.Length == 1) Associate(part, matches);
        }
        foreach (var part in parts.Where(p => !assignedParts.Contains(p)))
        {
            // One tap group can have additional sibling layers such as ArareRitsu_back;
            // reuse a known family only across an actual component-name boundary.
            var path = HomeCatalog.Text(part["path"]);
            var family = sharedFamilies.Where(f => PathMatch(path, f.Prefix)).OrderByDescending(f => f.Prefix.Length).FirstOrDefault();
            if (family.Members is not null) Associate(part, family.Members);
        }
        void Associate(JsonObject part, CharacterGroup[] members)
        {
            var names = members.Select(g => g.Stem).ToArray();
            foreach (var member in members)
            {
                member.Parts.Add(part);
                if (members.Length > 1) member.SharedMembers.Add(HomeCatalog.Text(part["path"]), names);
            }
            assignedParts.Add(part);
        }
        var usedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<HomeCharacter>();
        foreach (var group in groups.Where(g => g.Parts.Count > 0))
        {
            var key = KeyOf(group.Stem);
            if (!usedKeys.Add(key))
            {
                var prefix = KeyOf(group.Parent + "/" + group.Stem);
                key = prefix;
                for (var n = 2; !usedKeys.Add(key); n++) key = prefix + "-" + n.ToString(CultureInfo.InvariantCulture);
            }
            result.Add(new HomeCharacter(key, group.Stem, group.Seeds[0].Id,
                group.Seeds.Where(s => s.IsTapTarget).Select(s => s.Target).ToArray(), group.Parts.AsReadOnly(), group.SharedMembers,
                group.Seeds.Any(s => s.IsTapTarget) ? "tapTarget" : "verifiedNonInteractive"));
        }
        return result.AsReadOnly();
    }

    private static IEnumerable<CharacterSeed> VerifiedNonInteractive(JsonObject spot, IReadOnlyList<JsonObject> parts)
    {
        // These two source prefabs contain named, independently animated NPCs without a
        // SpotCharacter tap target. Their body/head/limb skeletons were checked against the
        // published assets. Require the complete identity; arbitrary Bg/Effect components
        // and similarly named objects in another scene are not inferred to be characters.
        var rules = new (long SpotId, string Component, string Skeleton)[]
        {
            (30002, "Manager", "03_mugen_01_vrfloor_02_manager.json"),
            (30003, "Marukun", "03_yumemita_01_vrfloor_03_marukun.json"),
        };
        var spotId = HomeCatalog.Number(spot["spotId"] ?? spot["master"]?["_id"]);
        foreach (var rule in rules.Where(rule => rule.SpotId == spotId))
        {
            var names = (spot["skeletons"] as JsonArray ?? []).OfType<JsonObject>()
                .Where(skeleton => HomeCatalog.Text(skeleton["skeleton"]) == rule.Skeleton)
                .Select(skeleton => HomeCatalog.Text(skeleton["name"])).ToHashSet(StringComparer.Ordinal);
            foreach (var part in parts)
            {
                var path = HomeCatalog.Text(part["path"]);
                if (path[(path.LastIndexOf('/') + 1)..] == rule.Component && names.Contains(HomeCatalog.Text(part["skeletonData"])))
                    yield return new CharacterSeed(new JsonObject { ["path"] = path }, isTapTarget: false);
            }
        }
    }

    private static bool PathMatch(string candidate, string prefix)
    {
        if (string.Equals(candidate, prefix, StringComparison.OrdinalIgnoreCase)) return true;
        return candidate.Length > prefix.Length && candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && IsBoundary(candidate[prefix.Length]);
    }

    private static bool IsBoundary(char c) => c is '/' or '_' or '-' || char.IsWhiteSpace(c);
    private static string KeyOf(string text)
    {
        var builder = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c)) builder.Append(char.ToLowerInvariant(c));
            else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
        }
        var key = builder.ToString().TrimEnd('-');
        if (key.Length == 0) return "character";
        // Batch exports use these keys as filenames on both Windows and Linux.
        if (key is "con" or "prn" or "aux" or "nul"
            || key.Length == 4 && (key.StartsWith("com", StringComparison.Ordinal) || key.StartsWith("lpt", StringComparison.Ordinal)) && key[3] is >= '1' and <= '9')
            return "character-" + key;
        return key;
    }

    private sealed class CharacterSeed
    {
        public JsonObject Target { get; }
        public string Path { get; }
        public string Parent { get; }
        public string Leaf { get; }
        public string Stem { get; set; }
        public long? Id { get; }
        public bool IsTapTarget { get; }
        public CharacterSeed(JsonObject target, bool isTapTarget = true)
        {
            Target = target; IsTapTarget = isTapTarget; Path = HomeCatalog.Text(target["path"]);
            var slash = Path.LastIndexOf('/');
            Parent = slash < 0 ? "" : Path[..slash]; Leaf = Path[(slash + 1)..].Trim();
            var boundary = Leaf.TakeWhile(c => !IsBoundary(c)).Count();
            Stem = boundary == 0 ? Leaf : Leaf[..boundary];
            Id = HomeCatalog.Number(target["_characterId"] ?? target["characterId"]);
        }
    }

    private sealed class CharacterGroup(CharacterSeed[] seeds)
    {
        public CharacterSeed[] Seeds { get; } = seeds;
        public string Parent => Seeds[0].Parent;
        public string Stem => Seeds[0].Stem;
        public List<JsonObject> Parts { get; } = [];
        public Dictionary<string, IReadOnlyList<string>> SharedMembers { get; } = new(StringComparer.Ordinal);
    }
}
