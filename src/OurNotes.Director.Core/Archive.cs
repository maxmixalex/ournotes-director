using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OurNotes.Director.Core;

public sealed class Archive
{
    public string Root { get; }
    public Archive(string root)
    {
        Root = Path.GetFullPath(root);
        if (!File.Exists(Path.Combine(Root, "index", "stats.json"))) throw new DirectoryNotFoundException($"Not an archive: {Root}");
    }
    public static JsonNode ReadJson(string path) => JsonNode.Parse(File.ReadAllBytes(path))!;
    public JsonNode Index(string name) => ReadJson(Path.Combine(Root, "index", name + ".json"));
    public IEnumerable<JsonNode> Rows(string name)
    {
        foreach (var line in File.ReadLines(Path.Combine(Root, "index", name + ".jsonl")))
            if (!string.IsNullOrWhiteSpace(line)) yield return JsonNode.Parse(line)!;
    }
    public string SitePath(string site, string relative)
    {
        if (!Regex.IsMatch(site, "^[a-zA-Z0-9_-]+$")) throw new ArgumentException("Invalid site");
        var basePath = Path.GetFullPath(Path.Combine(Root, "sites", site)) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(basePath, relative));
        if (!path.StartsWith(basePath, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Path escapes site");
        return path;
    }
    public JsonNode Manifest(string site, string type, string id)
    {
        if (type == "stories" && (string.IsNullOrWhiteSpace(id) || id == DirectorProject.LibrarySource))
        {
            var path = SitePath(site, "director/manifest.json");
            if (!File.Exists(path)) throw new FileNotFoundException("素材库缺少创作资源索引。请在素材库中完成归档更新；空白场景不需要绑定原剧情。", path);
            return ReadJson(path);
        }
        if (!Regex.IsMatch(id, "^[a-zA-Z0-9_-]+$")) throw new ArgumentException("Invalid manifest ID");
        return ReadJson(SitePath(site, $"{type}/{id}.json"));
    }
    public byte[] Decode(string site, string asset, long? size = null, long? stored = null)
    {
        var match = Regex.Match(asset, @"^assets/([0-9a-f]{64})\.[a-z0-9]+(?:\.(gz|br))?$");
        if (!match.Success) throw new InvalidDataException($"Invalid asset: {asset}");
        var bytes = File.ReadAllBytes(SitePath(site, asset));
        if (stored.HasValue && stored != bytes.LongLength) throw new InvalidDataException($"Stored size mismatch: {asset}");
        if (match.Groups[2].Success)
        {
            using var input = new MemoryStream(bytes);
            using Stream decoder = match.Groups[2].Value == "gz" ? new GZipStream(input, CompressionMode.Decompress) : new BrotliStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream(); decoder.CopyTo(output); bytes = output.ToArray();
        }
        if (size.HasValue && size != bytes.LongLength) throw new InvalidDataException($"Decoded size mismatch: {asset}");
        if (Hash(bytes) != match.Groups[1].Value) throw new InvalidDataException($"SHA-256 mismatch: {asset}");
        return bytes;
    }
    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public byte[] Materialize(string site, JsonNode entry)
    {
        if (entry["asset"] is not null) return Decode(site, S(entry, "asset"), Size(entry["size"]), Size(entry["stored"]));
        if (entry["parts"] is not JsonArray parts) throw new InvalidDataException("Unknown file entry");
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            var keys = new HashSet<string>();
            foreach (var part in parts)
            {
                var key = part![0]!.GetValue<string>();
                if (!keys.Add(key)) throw new InvalidDataException($"Duplicate split key: {key}");
                writer.WritePropertyName(key);
                // Raw JSON preserves numeric lexemes including 1e999; never deserialize to double.
                writer.WriteRawValue(Decode(site, part[1]!.GetValue<string>(), Size(part[2]), part.AsArray().Count > 3 ? Size(part[3]) : null));
            }
            writer.WriteEndObject();
        }
        return output.ToArray();
    }
    public byte[] Logical(string site, JsonNode manifest, string path, string? language = null)
    {
        var entry = language is null ? null : manifest["languages"]?[language]?["files"]?[path];
        entry ??= manifest["files"]?[path];
        return Materialize(site, entry ?? throw new FileNotFoundException($"Logical file not found: {path}"));
    }
    public JsonNode Model(string site, string id)
    {
        var manifest = Manifest(site, "models", id);
        var model = JsonNode.Parse(Logical(site, manifest, "model.json"))!;
        var prefab = JsonNode.Parse(Logical(site, manifest, S(model, "prefab")))!;
        var components = prefab["nodes"]![0]!["components"]!.AsArray();
        var ch = components.FirstOrDefault(c => S(c!, "class") == "Live2DCharacter");
        var ex = components.FirstOrDefault(c => S(c!, "class") == "CubismExpressionController");
        var motions = ch?["_motionList"]?.AsArray().Select(m => S(m!, "clip", S(m!, "name", S(m!, "MotionName")))).Where(s => s != "").Distinct().ToArray() ?? [];
        var expressions = ex?["ExpressionsList"]?["CubismExpressionObjects"]?.AsArray().Select(e => Regex.Replace(S(e!, "name"), @"\.exp3$", "")).Distinct().ToArray() ?? [];
        return JsonOutput.SerializeToNode(JsonOutput.Object(("id", id), ("site", site), ("manifest", $"models/{id}.json"), ("metadata", manifest["model"]), ("canvas", prefab["canvas"]), ("defaultMotion", ch?["DefaultMotionName"]), ("defaultExpression", ch?["DefaultExpressionName"]), ("motions", motions), ("expressions", expressions), ("files", manifest["files"])))!;
    }
    public static string S(JsonNode node, string key, string fallback = "") => node[key]?.ToString() ?? fallback;
    private static long? Size(JsonNode? node) => node is null ? null : long.Parse(node.ToString(), System.Globalization.CultureInfo.InvariantCulture);
    public object Export(string site, byte[] bytes, string output, object source, bool overwrite)
    {
        var path = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temp, bytes); File.Move(temp, path, overwrite); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return JsonOutput.Object(("output", path), ("size", bytes.Length), ("sha256", Hash(bytes)), ("provenance", JsonOutput.Object(("archive", Root), ("site", site), ("source", source))));
    }
}
