using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OurNotes.Director.Core;

namespace OurNotes.Director.Desktop.Core;

public sealed record ResourceLibrarySummary(string Directory, int Stories, int Models, IReadOnlyList<string> Sites,
    string? Band, IReadOnlyList<string>? Languages);
public sealed record ResourceFileCheck(int Files, long StoredBytes, IReadOnlyList<string> Problems)
{
    public bool Success => Problems.Count == 0;
}

/// <summary>Local library browsing and verification use the original archive, without changing its manifests.</summary>
public static partial class ResourceLibrary
{
    public static ResourceLibrarySummary ReadSummary(string directory)
    {
        var archive = new Archive(directory);
        var stats = archive.Index("stats");
        var sites = Directory.Exists(Path.Combine(archive.Root, "sites"))
            ? Directory.EnumerateDirectories(Path.Combine(archive.Root, "sites"))
                .Where(path => File.Exists(Path.Combine(path, "stories.json"))).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToArray()
            : [];
        var discoveryPath = Path.Combine(archive.Root, "discovery.json");
        var discovery = File.Exists(discoveryPath) ? Archive.ReadJson(discoveryPath) : null;
        string? band = discovery?["band"]?["token"]?.ToString() ?? discovery?["band"]?["bandTextId"]?.ToString()
            ?? stats["band"]?["token"]?.ToString() ?? stats["band"]?["bandTextId"]?.ToString();
        var languages = discovery?["archive"]?["languages"] is JsonArray selected
            ? selected.OfType<JsonValue>().Select(value => value.ToString()).ToArray() : null;
        return new(archive.Root, Count(stats["stories"]), Count(stats["models"]), sites, band, languages);
    }

    public static IReadOnlyList<ResultRow> ReadItems(string directory, string category, string language,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var archive = new Archive(directory);
        var context = new Dictionary<string, string?> { ["archive"] = archive.Root, ["lang"] = language };
        JsonNode data = category switch
        {
            "stories" => archive.Index("catalog"),
            "models" => archive.Index("models"),
            "homes" => new HomeCatalog(archive).List(),
            _ => throw new ArgumentException("未知素材分类。", nameof(category))
        };
        cancellationToken.ThrowIfCancellationRequested();
        return ResultRow.Read(data, category switch { "stories" => "story.list", "models" => "model.list", _ => "home.list" }, context);
    }

    public static ResourceFileCheck CheckFiles(string directory, ResultRow row,
        Action<int, int>? progress = null, CancellationToken cancellationToken = default)
    {
        var archive = new Archive(directory);
        string site = row.Context.GetValueOrDefault("site") ?? row.Data["site"]?.ToString() ?? "";
        string manifest = row.Kind == "home.spot" ? row.Data["sources"]?[0]?["manifest"]?.ToString() ?? ""
            : row.Data["manifest"]?.ToString() ?? "";
        if (!ManifestPath().IsMatch(manifest)) throw new InvalidDataException("素材没有有效的本地清单。");
        var summary = ReadSummary(directory);
        var languageSource = row.Data;
        if (row.Kind == "home.spot" && archive.Index("catalog")["stories"] is JsonArray catalog)
            languageSource = catalog.FirstOrDefault(item => item?["site"]?.ToString() == site && item?["manifest"]?.ToString() == manifest) ?? row.Data;
        var selectedLanguages = languageSource["languages"] is JsonArray localLanguages
            ? localLanguages.Select(value => value!.ToString()).ToHashSet(StringComparer.Ordinal)
            : summary.Languages?.ToHashSet(StringComparer.Ordinal);
        var manifests = new HashSet<string>(StringComparer.Ordinal);
        var entries = new Dictionary<string, (string Label, JsonNode Entry)>(StringComparer.Ordinal);
        var problems = new List<string>();
        void Collect(string relative)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ManifestPath().IsMatch(relative)) throw new InvalidDataException("素材依赖清单路径无效：" + relative);
            if (!manifests.Add(relative)) return;
            var path = archive.SitePath(site, relative);
            if (!File.Exists(path)) { problems.Add("缺少清单：" + relative); return; }
            var source = Archive.ReadJson(path);
            void Add(JsonNode? files, string prefix)
            {
                if (files is not JsonObject values) return;
                foreach (var pair in values)
                    if (pair.Value is JsonObject entry && (entry["asset"] is not null || entry["parts"] is JsonArray))
                        entries.TryAdd(entry.ToJsonString(), (prefix + pair.Key, entry));
            }
            Add(source["files"], "");
            if (source["languages"] is JsonObject languages)
                foreach (var pair in languages)
                    if (selectedLanguages is null || selectedLanguages.Contains(pair.Key)) Add(pair.Value?["files"], pair.Key + "/");
            if (source["models"] is JsonObject models)
                foreach (var model in models) Collect(model.Value?.ToString() ?? "");
        }
        Collect(manifest);
        long bytes = 0; int completed = 0;
        var countedAssets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, value) in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Materialize verifies stored size, decompressed size and the original content hash,
                // including every part of split JSON. No model runtime or GPU is required.
                archive.Materialize(site, value.Entry);
                IEnumerable<string> assets = value.Entry["asset"] is JsonValue asset
                    ? [asset.ToString()] : value.Entry["parts"]!.AsArray().Select(part => part![1]!.ToString());
                foreach (var path in assets)
                    if (countedAssets.Add(path)) bytes += new FileInfo(archive.SitePath(site, path)).Length;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            { problems.Add(value.Label + "：" + error.Message); }
            progress?.Invoke(++completed, entries.Count);
        }
        return new(entries.Count, bytes, problems);
    }

    public static EngineRequest DownloadRequest(string band, string destination, string sites, string? languages,
        int concurrency, bool deepScan, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(band)) throw new ArgumentException("请选择要下载的乐队。", nameof(band));
        if (string.IsNullOrWhiteSpace(destination)) throw new ArgumentException("请选择素材保存文件夹。", nameof(destination));
        var values = new Dictionary<string, string?>
        {
            ["band"] = band.Trim(), ["out"] = Path.GetFullPath(destination), ["sites"] = sites,
            ["concurrency"] = concurrency.ToString(CultureInfo.InvariantCulture), ["retries"] = "4",
            ["no-deep"] = deepScan ? "false" : "true"
        };
        if (!string.IsNullOrWhiteSpace(languages)) values["languages"] = languages;
        var build = CommandLineBuilder.Build("archive", values, new() { WorkingDirectory = workingDirectory });
        if (!build.IsValid) throw new ArgumentException(string.Join(Environment.NewLine, build.Errors.Select(error => error.Message)));
        return new("archive", "下载素材 · " + band.Trim(), build.Arguments, workingDirectory, build.OutputPath, build.OutputKind);
    }

    public static string FormatBytes(long bytes) => bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024 * 1024):F2} GB"
        : bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):F1} MB" : $"{bytes / 1024d:F1} KB";
    static int Count(JsonNode? value) => int.TryParse(value?.ToString(), CultureInfo.InvariantCulture, out int count) ? count : 0;
    [GeneratedRegex("^(stories|models)/[a-zA-Z0-9_-]+\\.json$", RegexOptions.CultureInvariant)]
    private static partial Regex ManifestPath();
}
