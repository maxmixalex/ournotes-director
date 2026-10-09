using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
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
            Console.Error.WriteLine($"Archive: {destination}");
            var lastStage = "";
            var lastAssets = 0;
            var progressGate = new object();
            using var downloader = new ArchiveDownloader(progress: progress =>
            {
                lock (progressGate)
                {
                    if (progress.Stage == lastStage && progress.Assets < lastAssets + 50) return;
                    lastStage = progress.Stage;
                    lastAssets = progress.Assets;
                    Console.Error.WriteLine(FormattableString.Invariant(
                        $"[{progress.Stage}] stories={progress.Stories} models={progress.Models} assets={progress.Assets}; downloaded={progress.DownloadedBytes / 1048576d:F1} MiB, reused={progress.ReusedBytes / 1048576d:F1} MiB"));
                }
            });
            var downloaded = await downloader.RunAsync(settings, cancellation.Token);
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
