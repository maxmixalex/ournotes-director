using System.Globalization;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OurNotes.Director.Core.Archiving;

internal static class ArchiveCommand
{
    public static async Task<int> RunAsync(
        IReadOnlyDictionary<string, string> arguments,
        IReadOnlyList<string> positional,
        JsonSerializerOptions json)
    {
        if (positional.Count != 1)
            throw new ArgumentException("archive has no subcommands. Run advkit archive [--out DIRECTORY].");

        string[] accepted = ["out", "archive", "sites", "site", "languages", "concurrency", "retries", "no-deep", "json", "debug"];
        foreach (var key in arguments.Keys)
            if (!accepted.Contains(key, StringComparer.Ordinal))
                throw new ArgumentException($"--{key} is not supported by archive; updates and resuming are automatic.");
        if (arguments.ContainsKey("out") && arguments.ContainsKey("archive"))
            throw new ArgumentException("Choose one destination option: --out or --archive.");
        if (arguments.ContainsKey("sites") && arguments.ContainsKey("site"))
            throw new ArgumentException("Choose one site option: --sites or --site.");

        string Option(string key, string fallback = "") => arguments.GetValueOrDefault(key, fallback);
        string[] Csv(string value) => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal).ToArray();
        int Integer(string key, int fallback, int maximum)
        {
            if (!int.TryParse(Option(key, fallback.ToString(CultureInfo.InvariantCulture)), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                || value < 1 || value > maximum)
                throw new ArgumentException($"--{key} must be between 1 and {maximum}.");
            return value;
        }

        var defaults = new ArchiveOptions();
        var sites = Csv(Option("sites", Option("site", "intl,jp")));
        if (sites.Length == 0 || sites.Any(site => !defaults.SiteRoots.ContainsKey(site)))
            throw new ArgumentException("--sites accepts intl, jp, or intl,jp.");
        var languages = arguments.ContainsKey("languages") ? Csv(Option("languages")) : null;
        if (languages is { Length: 0 }) throw new ArgumentException("--languages must contain at least one language.");

        var roots = defaults.SiteRoots.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var site in sites)
        {
            var value = Environment.GetEnvironmentVariable("OURNOTES_STORY_SITE_" + site.ToUpperInvariant());
            if (!string.IsNullOrWhiteSpace(value)) roots[site] = value;
        }
        var destination = Option("out", Option("archive", Environment.GetEnvironmentVariable("OURNOTES_ARCHIVE") ?? ""));
        if (string.IsNullOrWhiteSpace(destination)) destination = FindExistingArchive() ?? "archive";
        destination = Path.GetFullPath(destination);

        var settings = new ArchiveOptions
        {
            OutputDirectory = destination,
            MetadataBase = Environment.GetEnvironmentVariable("OURNOTES_METADATA_BASE") ?? defaults.MetadataBase,
            Sites = sites,
            SiteRoots = roots,
            Languages = languages,
            DeepScan = !arguments.ContainsKey("no-deep"),
            Concurrency = Integer("concurrency", defaults.Concurrency, 64),
            Retries = Integer("retries", defaults.Retries, 10),
        };

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            Directory.CreateDirectory(destination);
            // Keep the OS lock through all publishing stages. The file can remain after a crash;
            // the operating system releases the lock, so the next invocation resumes normally.
            using var archiveLock = new FileStream(Path.Combine(destination, ".archive.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using var transaction = new ArchiveTransaction(destination);
            RemoveInterruptedDownloads(destination);
            Console.Error.WriteLine($"Archive: {destination}");
            JsonObject downloaded;
            using (var progress = new ArchiveProgressReporter())
            {
                using var downloader = new ArchiveDownloader(progress: progress.Report);
                downloaded = await downloader.RunAsync(settings, cancellation.Token);
            }
            Console.Error.WriteLine("[index] Building searchable story, dialogue, voice and asset indexes.");
            var indexed = await ArchiveIndexer.BuildAsync(destination, cancellation.Token);
            var packs = new JsonArray();
            foreach (var site in sites)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                Console.Error.WriteLine($"[director] Updating {site} resources.");
                packs.Add((JsonNode)await DirectorPackBuilder.BuildAsync(destination, site, cancellationToken: cancellation.Token));
            }
            transaction.Commit();
            Console.WriteLine(new JsonObject
            {
                ["schema"] = "ournotes-director/1",
                ["ok"] = true,
                ["provenance"] = new JsonObject { ["archive"] = destination },
                ["result"] = new JsonObject
                {
                    ["archive"] = destination,
                    ["incremental"] = true,
                    ["download"] = downloaded,
                    ["index"] = indexed,
                    ["director"] = packs,
                },
            }.ToJsonString(json));
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }

    private static readonly Regex IncompleteAssetName = new(
        @"\A[0-9a-f]{64}\.[a-z0-9]+(?:\.(?:gz|br))?\.tmp-[0-9a-f]{32}\z", RegexOptions.CultureInvariant);

    private static void RemoveInterruptedDownloads(string archiveDirectory)
    {
        // Called only while holding the archive lock, after transaction recovery.
        // Never follow a redirected store or descend into arbitrary subdirectories.
        var store = Path.Combine(archiveDirectory, "store");
        if (!Directory.Exists(store)) return;
        var removed = 0;
        long bytes = 0;
        try
        {
            if ((File.GetAttributes(store) & FileAttributes.ReparsePoint) != 0) return;
            foreach (var file in Directory.EnumerateFiles(store, "*.tmp-*", SearchOption.TopDirectoryOnly))
            {
                if (!IncompleteAssetName.IsMatch(Path.GetFileName(file))) continue;
                try
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
                    var size = new FileInfo(file).Length;
                    File.Delete(file);
                    removed++;
                    bytes += size;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"[cleanup] Could not remove incomplete download '{file}': {error.Message}");
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[cleanup] Could not inspect incomplete downloads in '{store}': {error.Message}");
        }
        if (removed > 0)
            Console.Error.WriteLine(FormattableString.Invariant($"[cleanup] Removed {removed} incomplete download(s), {bytes} bytes."));
    }

    private sealed class ArchiveProgressReporter : IDisposable
    {
        private readonly object _gate = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Timer _heartbeat;
        private ArchiveProgress? _latest;
        private ArchiveProgress? _printed;
        private TimeSpan _lastPrint;
        private bool _disposed;

        public ArchiveProgressReporter()
        {
            // A large request or network retry may not finish an item for a while.
            // A sparse heartbeat retains the current counts and shows elapsed time.
            _heartbeat = new Timer(_ => Heartbeat(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        }

        public void Report(ArchiveProgress progress)
        {
            lock (_gate)
            {
                if (_disposed) return;
                var phaseChanged = _latest is null || Phase(_latest) != Phase(progress);
                if (!phaseChanged && progress.Processed < _latest!.Processed) return;
                _latest = progress;
                var finished = progress.Processed is { } done && progress.Total == done &&
                    (_printed is null || Phase(_printed) != Phase(progress) || _printed.Processed != done);
                if (phaseChanged || finished || _clock.Elapsed - _lastPrint >= TimeSpan.FromSeconds(2)) WriteProgress(progress);
            }
        }

        private static string Phase(ArchiveProgress progress) =>
            progress.Stage is "assets" or "complete" ? progress.Stage : progress.Stage + ":" + progress.Site;

        private void Heartbeat()
        {
            lock (_gate)
            {
                if (!_disposed && _latest is { } progress && _clock.Elapsed - _lastPrint >= TimeSpan.FromSeconds(10))
                    WriteProgress(progress);
            }
        }

        private void WriteProgress(ArchiveProgress progress)
        {
            var site = progress.Site is not null && progress.Stage is not ("assets" or "complete") ? " " + progress.Site : "";
            var count = progress.Processed is { } processed && progress.Total is { } total ? FormattableString.Invariant($" {processed}/{total}") : "";
            var details = progress.Stage is "assets" or "complete"
                ? FormattableString.Invariant($"; downloaded={progress.DownloadedBytes / 1048576d:F1} MiB, reused={progress.ReusedBytes / 1048576d:F1} MiB")
                : progress.Stage == "manifests" ? FormattableString.Invariant($"; models={progress.Models}") : "";
            if (progress.Stage == "complete")
                details = FormattableString.Invariant($" stories={progress.Stories} models={progress.Models} assets={progress.Assets}") + details;
            Console.Error.WriteLine(FormattableString.Invariant($"[{progress.Stage}{site}]{count}{details}; elapsed={_clock.Elapsed.TotalSeconds:F1}s"));
            _printed = progress;
            _lastPrint = _clock.Elapsed;
        }

        public void Dispose()
        {
            lock (_gate) _disposed = true;
            _heartbeat.Dispose();
        }
    }

    private static string? FindExistingArchive()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
            {
                foreach (var relative in new[] { "archive", Path.Combine("legacy", "millsage-story-archive", "archive") })
                {
                    var candidate = Path.Combine(directory.FullName, relative);
                    if (File.Exists(Path.Combine(candidate, "discovery.json")) || File.Exists(Path.Combine(candidate, "index", "stats.json")))
                        return candidate;
                }
            }
        }
        return null;
    }
}
