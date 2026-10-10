using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OurNotes.Director.Core.Archiving;

/// <summary>
/// Discovers and mirrors the current upstream data without a JavaScript runtime.
/// Content-addressed assets are verified and reused; mutable JSON is revalidated on every run.
/// The caller owns the archive lock for the complete download/index/pack transaction.
/// </summary>
public sealed partial class ArchiveDownloader : IDisposable
{
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly Action<ArchiveProgress>? _progress;

    public ArchiveDownloader(HttpClient? client = null, Action<ArchiveProgress>? progress = null)
    {
        _ownsClient = client is null;
        // .gz/.br asset files are already compressed at rest. Automatic HTTP decoding
        // would change their bytes and invalidate the manifest's stored-size contract.
        _client = client ?? new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        }) { Timeout = Timeout.InfiniteTimeSpan };
        _progress = progress;
    }

    public async Task<JsonObject> RunAsync(ArchiveOptions options, CancellationToken cancellationToken = default)
    {
        using var operation = new Operation(_client, options, _progress, cancellationToken);
        var discovery = await operation.DiscoverAsync();
        return await operation.SyncAsync(discovery);
    }

    /// <summary>Separately usable for deterministic discovery tests and embedding.</summary>
    public async Task<JsonObject> DiscoverAsync(ArchiveOptions options, CancellationToken cancellationToken = default)
    {
        using var operation = new Operation(_client, options, _progress, cancellationToken);
        return await operation.DiscoverAsync();
    }

    /// <summary>Mirrors an already discovered selection using the same integrity and update rules.</summary>
    public async Task<JsonObject> SyncAsync(ArchiveOptions options, JsonObject discovery, CancellationToken cancellationToken = default)
    {
        using var operation = new Operation(_client, options, _progress, cancellationToken);
        return await operation.SyncAsync((JsonObject)discovery.DeepClone());
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }

    private sealed partial class Operation : IDisposable
    {
        private static readonly Regex SitePattern = new("^[a-zA-Z0-9_-]+$", RegexOptions.CultureInvariant);
        private static readonly Regex ManifestPattern = new("^(stories|models)/[a-zA-Z0-9_-]+\\.json$", RegexOptions.CultureInvariant);
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
        private readonly HttpClient _client;
        private readonly ArchiveOptions _options;
        private readonly Action<ArchiveProgress>? _progress;
        private readonly CancellationToken _cancellation;
        private readonly SemaphoreSlim _httpSlots;
        private readonly string _root;
        private readonly string _cache;
        private readonly string _bandSelector;
        private readonly ConcurrentDictionary<string, Lazy<Task<RemoteDocument>>> _documents = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _validatedDocumentHashes = new(StringComparer.Ordinal);
        private readonly HashSet<string>? _languages;
        private int _stories, _models, _assets, _downloaded, _reused, _repaired, _jsonDownloaded, _jsonNotModified;
        private long _downloadedBytes, _reusedBytes;

        public Operation(HttpClient client, ArchiveOptions options, Action<ArchiveProgress>? progress, CancellationToken cancellation)
        {
            ArgumentNullException.ThrowIfNull(options);
            if (string.IsNullOrWhiteSpace(options.OutputDirectory)) throw new ArgumentException("An archive output directory is required.");
            if (options.Concurrency is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(options), "Concurrency must be between 1 and 64.");
            if (options.Retries is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(options), "Retries must be between 1 and 20.");
            if (options.RequestTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options), "Request timeout must be positive.");
            ValidateRoot(options.MetadataBase);
            var bandSelector = options.Band ?? options.BandTextId;
            if (string.IsNullOrWhiteSpace(bandSelector)) throw new ArgumentException("A band ID or name is required.");
            _bandSelector = bandSelector.Trim();
            if (options.Sites.Count == 0) throw new ArgumentException("At least one site is required.");
            if (options.Sites.Distinct(StringComparer.Ordinal).Count() != options.Sites.Count) throw new ArgumentException("Duplicate sites are not allowed.");
            foreach (var site in options.Sites)
            {
                ValidateSite(site);
                if (!options.SiteRoots.TryGetValue(site, out var siteRoot)) throw new ArgumentException($"No story root is configured for site '{site}'.");
                ValidateRoot(siteRoot);
                if (!options.MasterRegions.TryGetValue(site, out var region)) throw new ArgumentException($"No MasterData region is configured for site '{site}'.");
                if (string.IsNullOrWhiteSpace(region.Region) || string.IsNullOrWhiteSpace(region.Path) || region.Path.Contains("..", StringComparison.Ordinal))
                    throw new ArgumentException($"Invalid MasterData region for '{site}'.");
            }
            if (options.Languages?.Any(string.IsNullOrWhiteSpace) == true || options.Languages?.Count == 0)
                throw new ArgumentException("Languages must contain at least one nonempty language code, or be null for all languages.");
            _client = client;
            _options = options;
            _progress = progress;
            _cancellation = cancellation;
            _httpSlots = new(options.Concurrency);
            _root = Path.GetFullPath(options.OutputDirectory);
            _cache = Path.Combine(_root, ".cache", "http");
            _languages = options.Languages is null ? null : new(options.Languages, StringComparer.Ordinal);
            Directory.CreateDirectory(_cache);
        }

        private void Report(string stage, string? site = null, string? path = null, int? processed = null, int? total = null) => _progress?.Invoke(new(
            stage, Volatile.Read(ref _stories), Volatile.Read(ref _models), Volatile.Read(ref _assets),
            Interlocked.Read(ref _downloadedBytes), Interlocked.Read(ref _reusedBytes), site, path, processed, total));

        private static void ValidateSite(string site)
        {
            if (!SitePattern.IsMatch(site)) throw new InvalidDataException($"Invalid site key: {site}");
        }

        private static void ValidateRoot(string root)
        {
            if (!Uri.TryCreate(root, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http") ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                throw new ArgumentException($"Expected an HTTP(S) base URL without credentials, query, or fragment: {root}");
        }

        private static string JoinUrl(string root, string relative) => root.TrimEnd('/') + "/" + relative.TrimStart('/');

        private static string ValidateManifest(string path)
        {
            if (!ManifestPattern.IsMatch(path)) throw new InvalidDataException($"Unsupported manifest path: {path}");
            return path;
        }

        private string SitePath(string site, string relative)
        {
            ValidateSite(site);
            return Path.Combine(_root, "sites", site, relative.Replace('/', Path.DirectorySeparatorChar));
        }

        private static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
        private static string Text(JsonNode? node, string fallback = "") => node?.ToString() ?? fallback;
        private static JsonNode? Pick(JsonNode? node, params string[] names)
        {
            if (node is not JsonObject obj) return null;
            foreach (var name in names) if (obj.TryGetPropertyValue(name, out var value)) return value;
            return null;
        }
        private static long? Number(JsonNode? node) => long.TryParse(Text(node), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
        private static long? Id(JsonNode? row) => Number(Pick(row, "_id", "id"));
        private static long? AdvId(JsonNode? row) => Number(Pick(row, "_advId", "advId"));
        private static long[] Numbers(JsonNode? node) => node is JsonArray array ? array.Select(Number).Where(x => x.HasValue).Select(x => x!.Value).ToArray() : [];
        private static JsonArray NumberArray(IEnumerable<long> numbers) => new(numbers.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
        private static JsonArray StringArray(IEnumerable<string> strings) => new(strings.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
        private static JsonObject Object(JsonNode? node, string source) => node as JsonObject ?? throw new InvalidDataException($"Expected a JSON object from {source}.");

        private static async Task WriteBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken)
        {
            if (File.Exists(path) && new FileInfo(path).Length == bytes.LongLength &&
                (await File.ReadAllBytesAsync(path, cancellationToken)).AsSpan().SequenceEqual(bytes)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static Task WriteJsonAsync(string path, JsonNode value, CancellationToken cancellationToken) =>
            WriteBytesAsync(path, Encoding.UTF8.GetBytes(value.ToJsonString(JsonOptions) + "\n"), cancellationToken);

        public void Dispose()
        {
            _httpSlots.Dispose();
            foreach (var gate in _assetLocks.Values) gate.Dispose();
        }
        private sealed record RemoteDocument(JsonNode Value, byte[] Bytes, string CacheFile);
    }
}
