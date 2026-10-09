using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OurNotes.Director.Core.Archiving;

// Independent of Archive's index-presence guard: these operations also run on a first download.
// JsonNode retains the source number tokens (including 1e999) without a double round trip.
internal static class ArchiveIndexStorage
{
    private static readonly Regex AssetPattern = new(@"^assets/([0-9a-f]{64})\.[a-z0-9]+(?:\.(gz|br))?$");
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    internal static JsonNode? Clone(JsonNode? node) => node?.DeepClone();
    internal static string Text(JsonNode? node) => node?.ToString() ?? "";
    internal static IEnumerable<KeyValuePair<string, JsonNode?>> Properties(JsonNode? node) =>
        node is JsonObject obj ? obj : Enumerable.Empty<KeyValuePair<string, JsonNode?>>();
    internal static IEnumerable<JsonNode?> Items(JsonNode? node) =>
        node is JsonArray array ? array : Enumerable.Empty<JsonNode?>();
    internal static JsonArray Strings(IEnumerable<string> values) => new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());

    internal static bool Truthy(JsonNode? node)
    {
        if (node is null) return false;
        return node.GetValueKind() switch
        {
            JsonValueKind.Null or JsonValueKind.False => false,
            JsonValueKind.String => node.GetValue<string>().Length > 0,
            JsonValueKind.Number => !double.TryParse(node.ToJsonString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var number) || number != 0,
            _ => true,
        };
    }

    internal static bool PositiveNumber(JsonNode? node) => node?.GetValueKind() == JsonValueKind.Number &&
        double.TryParse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
        double.IsFinite(value) && value > 0;

    internal static long? Size(JsonNode? node)
    {
        if (node is null) return null;
        if (long.TryParse(Text(node), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) && size >= 0) return size;
        throw new InvalidDataException($"Invalid asset size: {node}");
    }

    internal static string SiteDirectory(string archiveDirectory, string site)
    {
        if (string.IsNullOrEmpty(site) || site.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-'))
            throw new ArgumentException("Invalid archive site name.", nameof(site));
        return Path.Combine(Path.GetFullPath(archiveDirectory), "sites", site);
    }

    internal static string Within(string root, string relative)
    {
        if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) || relative.Contains('\\'))
            throw new InvalidDataException($"Invalid archive-relative path: {relative}");
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(prefix, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException($"Archive path escapes its directory: {relative}");
        return full;
    }

    internal static async Task<JsonObject> ReadObjectAsync(string path, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return JsonNode.Parse(bytes) as JsonObject ?? throw new InvalidDataException($"Expected JSON object: {path}");
    }

    internal static async Task<JsonNode?> DecodeJsonAsync(string siteDirectory, string asset, long? size, long? stored,
        CancellationToken cancellationToken)
    {
        var match = AssetPattern.Match(asset);
        if (!match.Success) throw new InvalidDataException($"Invalid archive asset: {asset}");
        var path = Within(siteDirectory, asset);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (stored.HasValue && bytes.LongLength != stored.Value)
            throw new InvalidDataException($"Stored size mismatch: {asset}");
        if (match.Groups[2].Success)
        {
            using var source = new MemoryStream(bytes, writable: false);
            using Stream decoder = match.Groups[2].Value == "gz"
                ? new GZipStream(source, CompressionMode.Decompress)
                : new BrotliStream(source, CompressionMode.Decompress);
            using var output = new MemoryStream();
            await decoder.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            bytes = output.ToArray();
        }
        if (size.HasValue && bytes.LongLength != size.Value)
            throw new InvalidDataException($"Decoded size mismatch: {asset}");
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), match.Groups[1].Value, StringComparison.Ordinal))
            throw new InvalidDataException($"SHA-256 mismatch: {asset}");
        return JsonNode.Parse(bytes);
    }

    internal static async Task<JsonNode?> MaterializeAsync(string siteDirectory, JsonNode entry, CancellationToken cancellationToken)
    {
        if (entry["asset"] is not null)
            return await DecodeJsonAsync(siteDirectory, Text(entry["asset"]), Size(entry["size"]), Size(entry["stored"]), cancellationToken).ConfigureAwait(false);
        if (entry["parts"] is not JsonArray parts) throw new InvalidDataException("Unknown archive manifest file entry.");
        var result = new JsonObject();
        foreach (var partNode in parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (partNode is not JsonArray part || part.Count < 3 || part[0]?.GetValueKind() != JsonValueKind.String || part[1]?.GetValueKind() != JsonValueKind.String)
                throw new InvalidDataException("Invalid split archive asset part.");
            var key = Text(part[0]);
            if (result.ContainsKey(key)) throw new InvalidDataException($"Duplicate split archive key: {key}");
            result.Add(key, await DecodeJsonAsync(siteDirectory, Text(part[1]), Size(part[2]),
                part.Count > 3 ? Size(part[3]) : null, cancellationToken).ConfigureAwait(false));
        }
        return result;
    }

    internal static JsonNode? LogicalEntry(JsonObject manifest, string path, bool includeLanguages = false, IReadOnlySet<string>? languages = null)
    {
        var entry = manifest["files"]?[path];
        if (entry is not null || !includeLanguages) return entry;
        return Properties(manifest["languages"]).Where(group => languages is null || languages.Contains(group.Key))
            .Select(group => group.Value?["files"]?[path]).FirstOrDefault(value => value is not null);
    }

    internal static Task<JsonNode?> LogicalAsync(string siteDirectory, JsonObject manifest, string path, CancellationToken cancellationToken)
    {
        var entry = LogicalEntry(manifest, path);
        return entry is null ? Task.FromResult<JsonNode?>(null) : MaterializeAsync(siteDirectory, entry, cancellationToken);
    }

    internal static async Task WriteBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            var info = new FileInfo(path);
            if (info.Length == bytes.LongLength && (await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false)).AsSpan().SequenceEqual(bytes)) return;
        }
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static Task WriteJsonAsync(string path, JsonNode value, CancellationToken cancellationToken) =>
        WriteBytesAsync(path, Encoding.UTF8.GetBytes(value.ToJsonString(IndentedJson) + "\n"), cancellationToken);

    internal static async Task<JsonObject> PutJsonAssetAsync(string siteDirectory, JsonNode value, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(value.ToJsonString() + "\n");
        var relative = "assets/" + Convert.ToHexStringLower(SHA256.HashData(bytes)) + ".json";
        await WriteBytesAsync(Within(siteDirectory, relative), bytes, cancellationToken).ConfigureAwait(false);
        return new JsonObject { ["asset"] = relative, ["size"] = bytes.LongLength };
    }

    // Build a complete replacement before making it visible. A failed rebuild leaves the previous index/pack usable.
    internal static void PublishDirectory(string temporaryDirectory, string targetDirectory)
    {
        var previous = targetDirectory + ".previous-" + Guid.NewGuid().ToString("N");
        var hadPrevious = Directory.Exists(targetDirectory);
        if (hadPrevious) Directory.Move(targetDirectory, previous);
        try { Directory.Move(temporaryDirectory, targetDirectory); }
        catch
        {
            if (hadPrevious) Directory.Move(previous, targetDirectory);
            throw;
        }
        if (hadPrevious)
        {
            try { Directory.Delete(previous, recursive: true); }
            catch (IOException) { /* A reader may still have an old index open on Windows. */ }
            catch (UnauthorizedAccessException) { /* The new complete index is already published. */ }
        }
    }
}
