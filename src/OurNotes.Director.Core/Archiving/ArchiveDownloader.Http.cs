using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core.Archiving;

public sealed partial class ArchiveDownloader
{
    private sealed partial class Operation
    {
        private Task<RemoteDocument> FetchJsonAsync(string url) => _documents.GetOrAdd(url,
            address => new Lazy<Task<RemoteDocument>>(() => FetchJsonCoreAsync(address))).Value;

        private async Task<RemoteDocument> FetchJsonCoreAsync(string url)
        {
            var key = Sha256(Encoding.UTF8.GetBytes(url));
            var bodyPath = Path.Combine(_cache, key + ".body");
            var metaPath = Path.Combine(_cache, key + ".json");
            // Deep scans can touch thousands of manifests. Their JSON objects need not
            // remain in memory: an already revalidated document may be reopened locally
            // later in this same operation, with no duplicate HTTP request.
            if (_validatedDocumentHashes.TryGetValue(url, out var currentHash))
            {
                try
                {
                    var currentBytes = await File.ReadAllBytesAsync(bodyPath, _cancellation);
                    var currentValue = Sha256(currentBytes) == currentHash ? JsonNode.Parse(currentBytes) : null;
                    if (currentValue is not null) return new(currentValue, currentBytes, bodyPath);
                }
                catch (Exception error) when (error is IOException or JsonException) { }
                _validatedDocumentHashes.TryRemove(url, out _);
            }
            JsonObject? cacheInfo = null;
            byte[]? cachedBytes = null;
            JsonNode? cachedValue = null;
            // A cache entry is usable only when its bytes still match its checksum.
            // Corrupt local JSON must not be perpetuated by a server's 304 response.
            try
            {
                if (File.Exists(metaPath) && File.Exists(bodyPath))
                {
                    cacheInfo = JsonNode.Parse(await File.ReadAllBytesAsync(metaPath, _cancellation)) as JsonObject;
                    cachedBytes = await File.ReadAllBytesAsync(bodyPath, _cancellation);
                    if (Text(cacheInfo?["url"]) == url && Text(cacheInfo?["sha256"]) == Sha256(cachedBytes))
                        cachedValue = JsonNode.Parse(cachedBytes);
                }
            }
            catch (Exception error) when (error is IOException or JsonException or InvalidOperationException)
            {
                // Reacquire a damaged cache; never silently use stale metadata.
            }
            if (cachedValue is null) { cacheInfo = null; cachedBytes = null; }

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await _httpSlots.WaitAsync(_cancellation);
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cancellation);
                        timeout.CancelAfter(_options.RequestTimeout);
                        using var request = NewRequest(url, "application/json");
                        if (cacheInfo is not null)
                        {
                            var etag = Text(cacheInfo["etag"]);
                            var modified = Text(cacheInfo["lastModified"]);
                            if (etag.Length > 0) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
                            if (modified.Length > 0) request.Headers.TryAddWithoutValidation("If-Modified-Since", modified);
                        }
                        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                        if (response.StatusCode == HttpStatusCode.NotModified)
                        {
                            if (cachedValue is null || cachedBytes is null) throw new InvalidDataException($"Unexpected HTTP 304 without a valid local copy: {url}");
                            Interlocked.Increment(ref _jsonNotModified);
                            _validatedDocumentHashes[url] = Sha256(cachedBytes);
                            return new(cachedValue, cachedBytes, bodyPath);
                        }
                        ThrowForStatus(response, url);
                        var bytes = await ReadJsonBytesAsync(response, timeout.Token);
                        JsonNode value;
                        try { value = JsonNode.Parse(bytes) ?? throw new JsonException("JSON document is null."); }
                        catch (JsonException error) { throw new InvalidDataException($"Invalid JSON from {url}: {error.Message}", error); }
                        // Save exact JSON entity bytes. Re-serializing valid numbers such as
                        // 1e999 through a floating-point representation would alter source data.
                        await WriteBytesAsync(bodyPath, bytes, _cancellation);
                        await WriteJsonAsync(metaPath, new JsonObject
                        {
                            ["url"] = url,
                            ["sha256"] = Sha256(bytes),
                            ["etag"] = response.Headers.ETag?.ToString(),
                            ["lastModified"] = response.Content.Headers.LastModified?.ToString("R"),
                        }, _cancellation);
                        Interlocked.Increment(ref _jsonDownloaded);
                        _validatedDocumentHashes[url] = Sha256(bytes);
                        return new(value, bytes, bodyPath);
                    }
                    finally { _httpSlots.Release(); }
                }
                catch (Exception error) when (CanRetry(error, attempt))
                {
                    await RetryDelayAsync(error, attempt);
                }
            }
        }

        private static HttpRequestMessage NewRequest(string url, string accept)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("ournotes-director/1.0");
            request.Headers.Accept.ParseAdd(accept);
            request.Headers.AcceptEncoding.ParseAdd("identity");
            return request;
        }

        private static async Task<byte[]> ReadJsonBytesAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            // Identity is requested, but tolerate correctly labelled compressed JSON
            // responses. This is HTTP transport decoding, never asset-file decoding.
            foreach (var encoding in response.Content.Headers.ContentEncoding.Reverse())
            {
                if (encoding.Equals("identity", StringComparison.OrdinalIgnoreCase)) continue;
                using var input = new MemoryStream(bytes, false);
                using Stream decoder = encoding.ToLowerInvariant() switch
                {
                    "gzip" => new GZipStream(input, CompressionMode.Decompress),
                    "br" => new BrotliStream(input, CompressionMode.Decompress),
                    "deflate" => new ZLibStream(input, CompressionMode.Decompress),
                    _ => throw new InvalidDataException($"Unsupported HTTP JSON encoding: {encoding}"),
                };
                using var output = new MemoryStream();
                await decoder.CopyToAsync(output, cancellationToken);
                bytes = output.ToArray();
            }
            return bytes;
        }

        private static void ThrowForStatus(HttpResponseMessage response, string url)
        {
            if (response.IsSuccessStatusCode) return;
            var retryAfter = response.Headers.RetryAfter;
            var delay = retryAfter?.Delta ?? (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
            throw new ArchiveHttpException(url, response.StatusCode, delay);
        }

        private bool CanRetry(Exception error, int attempt)
        {
            if (_cancellation.IsCancellationRequested || attempt >= _options.Retries) return false;
            if (error is ArchiveHttpException http)
                return http.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int?)http.StatusCode >= 500;
            return error is HttpRequestException or IOException or InvalidDataException or TaskCanceledException;
        }

        private async Task RetryDelayAsync(Exception error, int attempt)
        {
            var delay = error is ArchiveHttpException { RetryAfter: { } retryAfter } ? retryAfter : TimeSpan.FromMilliseconds(350 * attempt * attempt);
            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
            if (delay > TimeSpan.FromSeconds(30)) delay = TimeSpan.FromSeconds(30);
            await Task.Delay(delay, _cancellation);
        }

        private sealed class ArchiveHttpException(string url, HttpStatusCode status, TimeSpan? retryAfter)
            : HttpRequestException($"HTTP {(int)status} ({status}) for {url}", null, status)
        {
            public TimeSpan? RetryAfter { get; } = retryAfter;
        }
    }
}
