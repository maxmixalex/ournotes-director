namespace OurNotes.Director.Core.Archiving;

/// <summary>Options for one incremental acquisition of the upstream archive.</summary>
public sealed class ArchiveOptions
{
    public string OutputDirectory { get; init; } = "archive";
    public string MetadataBase { get; init; } = "https://metadata.bdon.moe";
    public string BandTextId { get; init; } = "Band_Name_millsage";
    public IReadOnlyList<string> Sites { get; init; } = ["intl", "jp"];
    public IReadOnlyDictionary<string, string> SiteRoots { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["intl"] = "https://storage.bdon.moe/moenotes",
        ["jp"] = "https://storage.bdon.moe/moenotes/jp",
    };
    public IReadOnlyDictionary<string, ArchiveMasterRegion> MasterRegions { get; init; } = new Dictionary<string, ArchiveMasterRegion>(StringComparer.Ordinal)
    {
        ["intl"] = new("hk-tw-mo", "tw/master"),
        ["jp"] = new("jp", "jp/master"),
    };
    /// <summary>Null downloads every available language. Common files are always included.</summary>
    public IReadOnlyCollection<string>? Languages { get; init; }
    public bool DeepScan { get; init; } = true;
    public int Concurrency { get; init; } = 8;
    /// <summary>Total attempts for a transient network or integrity failure.</summary>
    public int Retries { get; init; } = 4;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(90);
}

public sealed record ArchiveMasterRegion(string Region, string Path);

/// <summary>Progress is reported to the caller, never directly to stdout.</summary>
public sealed record ArchiveProgress(
    string Stage,
    int Stories,
    int Models,
    int Assets,
    long DownloadedBytes,
    long ReusedBytes,
    string? Site = null,
    string? Path = null);
