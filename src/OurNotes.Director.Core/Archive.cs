using System.IO.Compression;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OurNotes.Director.Core;

public sealed class Archive
{
    public string Root { get; }
    internal PreviewTextureCache? PreviewTextures{get;init;}
    internal PreviewModelCache? PreviewModels{get;init;}
    readonly Dictionary<string,double> modelInitializationMilliseconds=[];
    internal IReadOnlyDictionary<string,double> ModelInitializationTimings=>modelInitializationMilliseconds;
    internal void RecordModelInitialization(string name,long started)
        =>modelInitializationMilliseconds[name]=modelInitializationMilliseconds.GetValueOrDefault(name)+Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    // A foreground construction token applies only to model resource loading. Shared
    // audio decoders can outlive that request and must never retain its cancellation.
    internal CancellationToken LoadCancellation{get;set;}
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
        =>Decode(site,asset,size,stored,null,default);
    byte[] Decode(string site,string asset,long? size,long? stored,PreviewModelCache? cache,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var match = Regex.Match(asset, @"^assets/([0-9a-f]{64})\.[a-z0-9]+(?:\.(gz|br))?$");
        if (!match.Success) throw new InvalidDataException($"Invalid asset: {asset}");
        byte[] current=File.ReadAllBytes(SitePath(site,asset));
        cancellationToken.ThrowIfCancellationRequested();
        if(stored.HasValue&&stored!=current.LongLength)throw new InvalidDataException($"Stored size mismatch: {asset}");
        byte[] Verified()
        {
            byte[] bytes=current;
            if(match.Groups[2].Success)
            {
                using var input=new MemoryStream(current,false);
                using Stream decoder=match.Groups[2].Value=="gz"?new GZipStream(input,CompressionMode.Decompress):new BrotliStream(input,CompressionMode.Decompress);
                using var output=new MemoryStream();
                if(!cancellationToken.CanBeCanceled)decoder.CopyTo(output);
                else
                {
                    byte[] buffer=new byte[81920];int read;
                    while((read=decoder.Read(buffer))>0){cancellationToken.ThrowIfCancellationRequested();output.Write(buffer,0,read);}
                }
                bytes=output.ToArray();
            }
            cancellationToken.ThrowIfCancellationRequested();
            if(size.HasValue&&size!=bytes.LongLength)throw new InvalidDataException($"Decoded size mismatch: {asset}");
            if(Hash(bytes)!=match.Groups[1].Value)throw new InvalidDataException($"SHA-256 mismatch: {asset}");
            cancellationToken.ThrowIfCancellationRequested();return bytes;
        }
        return cache?.ReadAsset(asset,current,size,Verified,cancellationToken)??Verified();
    }
    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public byte[] Materialize(string site, JsonNode entry)
        =>Materialize(site,entry,null,default);
    byte[] Materialize(string site,JsonNode entry,PreviewModelCache? cache,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if(entry["asset"] is not null)return Decode(site,S(entry,"asset"),Size(entry["size"]),Size(entry["stored"]),cache,cancellationToken);
        if (entry["parts"] is not JsonArray parts) throw new InvalidDataException("Unknown file entry");
        IEnumerable<(string Key,byte[] Bytes)> ReadParts()
        {
            var keys=new HashSet<string>();
            foreach(var part in parts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string key=part![0]!.GetValue<string>();
                if(!keys.Add(key))throw new InvalidDataException($"Duplicate split key: {key}");
                yield return(key,Decode(site,part[1]!.GetValue<string>(),Size(part[2]),part.AsArray().Count>3?Size(part[3]):null,cache,cancellationToken));
            }
        }
        byte[] Build(IEnumerable<(string Key,byte[] Bytes)> values)
        {
            using var output=new MemoryStream();
            using(var writer=new Utf8JsonWriter(output))
            {
                writer.WriteStartObject();
                foreach(var part in values)
                {
                    cancellationToken.ThrowIfCancellationRequested();writer.WritePropertyName(part.Key);
                    // Raw JSON preserves numeric lexemes including 1e999; never deserialize to double.
                    writer.WriteRawValue(part.Bytes);
                }
                writer.WriteEndObject();
            }
            cancellationToken.ThrowIfCancellationRequested();return output.ToArray();
        }
        if(cache is null)return Build(ReadParts());
        // Every part, current stored file and manifest constraint must be checked
        // before an already assembled object can be reused.
        var verified=ReadParts().ToArray();
        return cache.Materialize(entry.ToJsonString(),()=>Build(verified),cancellationToken);
    }
    public byte[] Logical(string site, JsonNode manifest, string path, string? language = null)
    {
        var entry = language is null ? null : manifest["languages"]?[language]?["files"]?[path];
        entry ??= manifest["files"]?[path];
        return Materialize(site, entry ?? throw new FileNotFoundException($"Logical file not found: {path}"));
    }
    internal Pixels ModelTexture(string site,JsonNode manifest,string path)
    {
        LoadCancellation.ThrowIfCancellationRequested();
        byte[] bytes=Logical(site,manifest,path);
        LoadCancellation.ThrowIfCancellationRequested();
        return PreviewTextures?.Read(bytes,LoadCancellation)??Png.Decode(bytes,LoadCancellation);
    }
    internal byte[] ModelResource(string site,JsonNode manifest,string path)
    {
        LoadCancellation.ThrowIfCancellationRequested();
        var entry=manifest["files"]?[path]??throw new FileNotFoundException($"Logical file not found: {path}");
        return Materialize(site,entry,PreviewModels,LoadCancellation);
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
