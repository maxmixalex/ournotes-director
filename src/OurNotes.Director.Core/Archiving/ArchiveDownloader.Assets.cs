using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OurNotes.Director.Core.Archiving;

public sealed partial class ArchiveDownloader
{
    private sealed partial class Operation
    {
        private static readonly Regex AssetPattern = new("^assets/([0-9a-f]{64})\\.[a-z0-9]+(?:\\.(gz|br))?$", RegexOptions.CultureInvariant);
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _assetLocks = new(StringComparer.Ordinal);
        private readonly Dictionary<string, VerifiedAsset> _verifiedStore = new(StringComparer.Ordinal);

        public async Task<JsonObject> SyncAsync(JsonObject discovery)
        {
            if (discovery["stories"] is not JsonArray stories) throw new InvalidDataException("Discovery has no stories array.");
            var indexes = Object(discovery["indexes"], "discovery.indexes");
            var manifests = new Dictionary<string, ManifestCopy>(StringComparer.Ordinal);
            var jobs = new Dictionary<string, AssetJob>(StringComparer.Ordinal);
            var selectedIndexes = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var (site, siteNode) in indexes)
            {
                ValidateSite(site);
                var siteInfo = Object(siteNode, $"discovery.indexes.{site}");
                var root = Text(siteInfo["root"]);
                ValidateRoot(root);
                var selectedEntries = new JsonArray();
                var siteStories = stories.OfType<JsonObject>().Where(story => story["sites"]?[site] is JsonObject).OrderBy(x => Number(x["advId"])).ToArray();
                var collected = 0;
                Report("manifests", site, processed: 0, total: siteStories.Length);
                foreach (var story in siteStories)
                {
                    _cancellation.ThrowIfCancellationRequested();
                    if (story["sites"]?[site] is not JsonObject selection) continue;
                    var id = Number(story["advId"]) ?? throw new InvalidDataException("Discovery story has no numeric advId.");
                    var path = ValidateManifest(Text(selection["manifest"], "stories/" + id.ToString(CultureInfo.InvariantCulture) + ".json"));
                    if (!path.StartsWith("stories/", StringComparison.Ordinal)) throw new InvalidDataException($"Expected a story manifest: {path}");
                    await CollectManifestAsync(site, root, path, manifests, jobs);
                    var entry = (JsonObject)Object(selection["entry"], $"story {id} site entry").DeepClone();
                    entry["manifest"] = path;
                    selectedEntries.Add((JsonNode)entry);
                    Interlocked.Increment(ref _stories);
                    Report("manifests", site, path, ++collected, siteStories.Length);
                }
                var source = siteInfo["index"] as JsonObject;
                var selectedIndex = source is null ? new JsonObject { ["format"] = "ournotes.stories/1" } : (JsonObject)source.DeepClone();
                selectedIndex["stories"] = selectedEntries;
                selectedIndexes.Add(site, selectedIndex);
            }

            // Finish all asset work before replacing any live manifest or selection.
            // A failed/cancelled run leaves its verified assets reusable and the previous
            // selection intact. No old material is deleted as part of an update.
            Report("assets", processed: 0, total: jobs.Count);
            await Parallel.ForEachAsync(jobs.Values, new ParallelOptions
            {
                MaxDegreeOfParallelism = _options.Concurrency,
                CancellationToken = _cancellation,
            }, async (job, token) =>
            {
                await FetchAssetAsync(job);
                token.ThrowIfCancellationRequested();
                var completed = Interlocked.Increment(ref _assets);
                Report("assets", job.Site, job.Asset, completed, jobs.Count);
            });

            Report("publishing");
            foreach (var copy in manifests.Values.OrderBy(x => x.Path.StartsWith("models/", StringComparison.Ordinal) ? 0 : 1))
            {
                var bytes = await File.ReadAllBytesAsync(copy.CacheFile, _cancellation);
                await WriteBytesAsync(SitePath(copy.Site, copy.Path), bytes, _cancellation);
            }
            foreach (var (site, selectedIndex) in selectedIndexes)
                await WriteJsonAsync(SitePath(site, "stories.json"), selectedIndex, _cancellation);
            discovery["archive"] = SelectionOptions();
            await WriteJsonAsync(Path.Combine(_root, "discovery.json"), discovery, _cancellation);
            var result = new JsonObject
            {
                ["output"] = _root,
                ["stories"] = _stories,
                ["models"] = _models,
                ["assets"] = _assets,
                ["downloaded"] = _downloaded,
                ["reused"] = _reused,
                ["repaired"] = _repaired,
                ["downloadedBytes"] = _downloadedBytes,
                ["reusedBytes"] = _reusedBytes,
                ["metadataDownloaded"] = _jsonDownloaded,
                ["metadataNotModified"] = _jsonNotModified,
                ["discoveredStories"] = stories.Count,
                ["languages"] = _languages is null ? null : StringArray(_languages.Order(StringComparer.Ordinal)),
                ["sites"] = StringArray(selectedIndexes.Keys),
            };
            Report("complete");
            return result;
        }

        private async Task CollectManifestAsync(string site, string root, string path, Dictionary<string, ManifestCopy> manifests, Dictionary<string, AssetJob> jobs)
        {
            var key = site + ":" + path;
            if (manifests.ContainsKey(key)) return;
            var document = await FetchJsonAsync(JoinUrl(root, path));
            var manifest = Object(document.Value, path);
            // Record before recursing, so shared and cyclic model references are bounded.
            manifests.Add(key, new(site, path, document.CacheFile));
            if (path.StartsWith("models/", StringComparison.Ordinal)) Interlocked.Increment(ref _models);
            AddFiles(manifest["files"], site, root, jobs);
            if (manifest["languages"] is JsonObject languages)
                foreach (var (language, group) in languages)
                    if (_languages is null || _languages.Contains(language)) AddFiles(group?["files"], site, root, jobs);
            foreach (var modelPath in ModelPaths(manifest)) await CollectManifestAsync(site, root, modelPath, manifests, jobs);
            _documents.TryRemove(JoinUrl(root, path), out _);
        }

        private static long? ExpectedSize(JsonNode? value, string label)
        {
            if (value is null) return null;
            var size = Number(value);
            if (size is null or < 0) throw new InvalidDataException($"Invalid {label}: {value}");
            return size;
        }

        private static void AddFiles(JsonNode? filesNode, string site, string root, Dictionary<string, AssetJob> jobs)
        {
            if (filesNode is null) return;
            var files = Object(filesNode, "manifest files");
            void Add(string asset, JsonNode? size, JsonNode? stored)
            {
                ParseAsset(asset);
                var decoded = ExpectedSize(size, "decoded asset size");
                var encoded = ExpectedSize(stored, "stored asset size");
                var key = site + ":" + asset;
                if (jobs.TryGetValue(key, out var previous))
                {
                    if (decoded.HasValue && previous.Size.HasValue && decoded != previous.Size) throw new InvalidDataException($"Conflicting decoded sizes for {site}/{asset}.");
                    if (encoded.HasValue && previous.Stored.HasValue && encoded != previous.Stored) throw new InvalidDataException($"Conflicting stored sizes for {site}/{asset}.");
                    previous.Size ??= decoded;
                    previous.Stored ??= encoded;
                }
                else jobs.Add(key, new(site, root, asset, decoded, encoded));
            }
            foreach (var (logicalPath, entryNode) in files)
            {
                var entry = Object(entryNode, $"file entry {logicalPath}");
                if (entry["asset"] is not null)
                {
                    Add(Text(entry["asset"]), entry["size"], entry["stored"]);
                }
                else if (entry["parts"] is JsonArray parts)
                {
                    var keys = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var node in parts)
                    {
                        if (node is not JsonArray part || part.Count is < 3 or > 4 || part[0] is not JsonValue keyValue || !keyValue.TryGetValue<string>(out var partKey))
                            throw new InvalidDataException($"Invalid split JSON part in {logicalPath}.");
                        if (!keys.Add(partKey)) throw new InvalidDataException($"Duplicate split JSON key in {logicalPath}: {partKey}");
                        Add(Text(part[1]), part[2], part.Count == 4 ? part[3] : null);
                    }
                }
                else throw new InvalidDataException($"Unknown manifest file entry: {logicalPath}");
            }
        }

        private async Task FetchAssetAsync(AssetJob job)
        {
            var parsed = ParseAsset(job.Asset);
            var store = Path.Combine(_root, "store", job.Asset["assets/".Length..]);
            var destination = SitePath(job.Site, job.Asset);
            var gate = _assetLocks.GetOrAdd(job.Asset, _ => new SemaphoreSlim(1));
            await gate.WaitAsync(_cancellation);
            try
            {
                var storeExisted = File.Exists(store);
                var targetExisted = File.Exists(destination);
                VerifiedAsset? validStore;
                lock (_verifiedStore) _verifiedStore.TryGetValue(job.Asset, out validStore);
                if (validStore is not null && (!File.Exists(store) || !Matches(validStore, job))) validStore = null;
                validStore ??= await TryVerifyAsync(store, parsed, job);
                var validTarget = targetExisted ? await TryVerifyAsync(destination, parsed, job) : null;
                var damaged = (storeExisted && validStore is null) || (targetExisted && validTarget is null);
                var downloaded = false;
                if (validStore is null)
                {
                    var recovery = validTarget is not null ? destination : null;
                    var recovered = validTarget;
                    // Legacy archives may contain only site copies; a good existing copy
                    // can populate the shared store without fetching the asset again.
                    if (recovery is null)
                    {
                        foreach (var site in _options.Sites.Where(x => x != job.Site))
                        {
                            var candidate = SitePath(site, job.Asset);
                            var verified = await TryVerifyAsync(candidate, parsed, job);
                            if (verified is null) continue;
                            recovery = candidate;
                            recovered = verified;
                            break;
                        }
                    }
                    if (recovery is not null)
                    {
                        await LinkOrCopyAsync(recovery, store, _cancellation);
                        validStore = recovered!;
                    }
                    else
                    {
                        validStore = await DownloadAssetAsync(job, parsed, store);
                        Interlocked.Increment(ref _downloaded);
                        Interlocked.Add(ref _downloadedBytes, validStore.Stored);
                        downloaded = true;
                    }
                    lock (_verifiedStore) _verifiedStore[job.Asset] = validStore;
                }
                else
                {
                    lock (_verifiedStore) _verifiedStore[job.Asset] = validStore;
                }
                if (validTarget is null) await LinkOrCopyAsync(store, destination, _cancellation);
                if (!downloaded)
                {
                    Interlocked.Increment(ref _reused);
                    Interlocked.Add(ref _reusedBytes, validStore.Stored);
                }
                if (damaged) Interlocked.Increment(ref _repaired);
            }
            finally { gate.Release(); }
        }

        private async Task<VerifiedAsset> DownloadAssetAsync(AssetJob job, AssetPath parsed, string destination)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var url = JoinUrl(job.Root, job.Asset);
            for (var attempt = 1; ; attempt++)
            {
                var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    await _httpSlots.WaitAsync(_cancellation);
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cancellation);
                        timeout.CancelAfter(_options.RequestTimeout);
                        using var request = NewRequest(url, "*/*");
                        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                        ThrowForStatus(response, url);
                        if (job.Stored.HasValue && response.Content.Headers.ContentLength is { } length && length != job.Stored.Value)
                            throw new InvalidDataException($"Stored size mismatch for {job.Asset}: HTTP length {length}, expected {job.Stored.Value}.");
                        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
                        await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
                            await input.CopyToAsync(output, timeout.Token);
                    }
                    finally { _httpSlots.Release(); }
                    var verified = await VerifyFileAsync(temporary, parsed, job);
                    _cancellation.ThrowIfCancellationRequested();
                    File.Move(temporary, destination, true);
                    return verified;
                }
                catch (Exception error) when (CanRetry(error, attempt)) { await RetryDelayAsync(error, attempt); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }

        private static AssetPath ParseAsset(string asset)
        {
            var match = AssetPattern.Match(asset);
            if (!match.Success) throw new InvalidDataException($"Unsupported asset path: {asset}");
            return new(match.Groups[1].Value, match.Groups[2].Value);
        }

        private static bool Matches(VerifiedAsset verified, AssetJob job) =>
            (!job.Size.HasValue || job.Size == verified.Decoded) && (!job.Stored.HasValue || job.Stored == verified.Stored);

        private async Task<VerifiedAsset?> TryVerifyAsync(string file, AssetPath parsed, AssetJob job)
        {
            if (!File.Exists(file)) return null;
            try { return await VerifyFileAsync(file, parsed, job); }
            catch (Exception error) when (error is IOException or InvalidDataException) { return null; }
        }

        private async Task<VerifiedAsset> VerifyFileAsync(string file, AssetPath parsed, AssetJob job)
        {
            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var stored = input.Length;
            if (job.Stored.HasValue && stored != job.Stored.Value) throw new InvalidDataException($"Stored size mismatch: {job.Asset}.");
            using var decoder = parsed.Encoding switch
            {
                "gz" => (Stream)new GZipStream(input, CompressionMode.Decompress, true),
                "br" => new BrotliStream(input, CompressionMode.Decompress, true),
                _ => null,
            };
            var source = decoder ?? input;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(131072);
            long decoded = 0;
            try
            {
                int count;
                while ((count = await source.ReadAsync(buffer.AsMemory(), _cancellation)) != 0)
                {
                    decoded = checked(decoded + count);
                    if (job.Size.HasValue && decoded > job.Size.Value) throw new InvalidDataException($"Decoded size mismatch: {job.Asset}.");
                    hash.AppendData(buffer.AsSpan(0, count));
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            if (job.Size.HasValue && decoded != job.Size.Value) throw new InvalidDataException($"Decoded size mismatch: {job.Asset}.");
            if (!string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), parsed.Hash, StringComparison.Ordinal)) throw new InvalidDataException($"SHA-256 mismatch: {job.Asset}.");
            return new(decoded, stored);
        }

        private static async Task LinkOrCopyAsync(string source, string destination, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                var linked = OperatingSystem.IsWindows() ? CreateHardLinkWindows(temporary, source, IntPtr.Zero) :
                    (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) && CreateHardLinkUnix(source, temporary) == 0;
                if (!linked)
                {
                    await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await input.CopyToAsync(output, cancellationToken);
                }
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, destination, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CreateHardLinkWindows(string fileName, string existingFileName, IntPtr securityAttributes);

        [LibraryImport("libc", EntryPoint = "link", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
        private static partial int CreateHardLinkUnix(string source, string destination);

        private sealed record AssetPath(string Hash, string Encoding);
        private sealed record VerifiedAsset(long Decoded, long Stored);
        private sealed record ManifestCopy(string Site, string Path, string CacheFile);
        private sealed class AssetJob(string site, string root, string asset, long? size, long? stored)
        {
            public string Site { get; } = site;
            public string Root { get; } = root;
            public string Asset { get; } = asset;
            public long? Size { get; set; } = size;
            public long? Stored { get; set; } = stored;
        }
    }
}
