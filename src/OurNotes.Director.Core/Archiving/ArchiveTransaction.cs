using System.Text;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core.Archiving;

/// <summary>
/// Protects the mutable archive metadata across download, indexing and Director generation.
/// The caller must hold the archive's process lock. Verified immutable assets and HTTP caches
/// are intentionally retained after a failure, so the next invocation can reuse them.
/// </summary>
public sealed class ArchiveTransaction : IDisposable
{
    private const string Format = "ournotes.archive-transaction/1";
    private const string StateDirectoryName = ".archive-transaction";
    private static readonly string[] IndexFiles =
    [
        "catalog.json", "models.json", "stats.json", "commands.jsonl", "dialogue.jsonl", "texts.jsonl",
        "voices.jsonl", "sounds.jsonl", "files.jsonl",
    ];
    private readonly string _root;
    private readonly string _stateDirectory;
    private readonly string _id = Guid.NewGuid().ToString("N");
    private bool _committed;
    private bool _disposed;

    public ArchiveTransaction(string archiveDirectory)
    {
        _root = Path.GetFullPath(archiveDirectory);
        Directory.CreateDirectory(_root);
        _stateDirectory = Path.Combine(_root, StateDirectoryName);
        RecoverPrevious(_root, _stateDirectory);

        // The canonical journal is never visible without a valid marker. A crash while
        // preparing its sibling has not touched live metadata and cannot require rollback.
        var preparingDirectory = _stateDirectory + ".preparing-" + _id;
        Directory.CreateDirectory(preparingDirectory);
        var journal = new JsonObject { ["format"] = Format, ["id"] = _id, ["state"] = "preparing", ["files"] = new JsonArray() };
        var ownsStateDirectory = false;
        try
        {
            WriteJournal(preparingDirectory, journal);
            Directory.Move(preparingDirectory, _stateDirectory);
            ownsStateDirectory = true;
            var files = journal["files"]!.AsArray();
            foreach (var relative in MutableFiles(_root).Order(StringComparer.Ordinal))
            {
                var source = ArchiveIndexStorage.Within(_root, relative);
                var target = ArchiveIndexStorage.Within(Path.Combine(_stateDirectory, "files"), relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target);
                files.Add((JsonNode)new JsonObject { ["path"] = relative, ["size"] = new FileInfo(target).Length });
            }
            journal["state"] = "active";
            WriteJournal(_stateDirectory, journal);
        }
        catch
        {
            // No operation has received this transaction yet, so live metadata is unchanged.
            if (Directory.Exists(preparingDirectory)) Directory.Delete(preparingDirectory, recursive: true);
            if (ownsStateDirectory && Directory.Exists(_stateDirectory)) RetireJournalDirectory(_stateDirectory);
            throw;
        }
    }

    /// <summary>Call only after every requested archive stage has succeeded.</summary>
    public void Commit()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_committed) return;
        var journal = ReadJournal(_stateDirectory);
        CheckIdentity(journal);
        journal["state"] = "committed";
        WriteJournal(_stateDirectory, journal);
        _committed = true;
        // A committed marker is enough for safe recovery even when Windows readers
        // temporarily keep a backup open and defer the housekeeping to the next run.
        try { RetireJournalDirectory(_stateDirectory); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_committed || !Directory.Exists(_stateDirectory)) return;
        var journal = ReadJournal(_stateDirectory);
        CheckIdentity(journal);
        Restore(_root, _stateDirectory, journal);
    }

    private void CheckIdentity(JsonObject journal)
    {
        if (journal["id"]?.ToString() != _id)
            throw new InvalidOperationException("The archive transaction changed. Hold the archive process lock for the complete update.");
    }

    private static void RecoverPrevious(string root, string directory)
    {
        if (!Directory.Exists(directory)) return;
        var journal = ReadJournal(directory);
        var state = journal["state"]?.ToString();
        if (state == "active") Restore(root, directory, journal);
        else if (state is "preparing" or "committed") RetireJournalDirectory(directory);
        else throw new InvalidDataException($"Unknown archive transaction state: {state}");
    }

    private static JsonObject ReadJournal(string directory)
    {
        var path = Path.Combine(directory, "journal.json");
        var journal = JsonNode.Parse(File.ReadAllBytes(path)) as JsonObject;
        if (journal is null || journal["format"]?.ToString() != Format || journal["files"] is not JsonArray || string.IsNullOrEmpty(journal["id"]?.ToString()))
            throw new InvalidDataException($"Unrecognized archive transaction journal: {path}");
        return journal;
    }

    private static void Restore(string root, string directory, JsonObject journal)
    {
        var original = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in journal["files"]!.AsArray())
        {
            var relative = entry?["path"]?.ToString() ?? "";
            if (!IsMutableFile(relative) || !original.TryAdd(relative, ArchiveIndexStorage.Within(Path.Combine(directory, "files"), relative)))
                throw new InvalidDataException($"Invalid archive transaction snapshot path: {relative}");
            var backup = original[relative];
            if (!File.Exists(backup) || ArchiveIndexStorage.Size(entry?["size"]) != new FileInfo(backup).Length)
                throw new InvalidDataException($"Incomplete archive transaction snapshot: {relative}");
        }
        // Check every backup before deleting/replacing anything. An interrupted rollback
        // leaves the active journal and its snapshots in place and can safely run again.
        foreach (var relative in MutableFiles(root).Where(relative => !original.ContainsKey(relative)).ToArray())
            File.Delete(ArchiveIndexStorage.Within(root, relative));
        foreach (var (relative, backup) in original)
        {
            var target = ArchiveIndexStorage.Within(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temporary = target + ".rollback-" + Guid.NewGuid().ToString("N");
            try
            {
                File.Copy(backup, temporary);
                File.Move(temporary, target, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        RetireJournalDirectory(directory);
    }

    private static void RetireJournalDirectory(string directory)
    {
        // Never recursively delete the canonical journal in place: process death after
        // deleting journal.json but before deleting its directory would leave an
        // unrecognizable recovery state. Once retired, no recovery is still required.
        var retired = directory + ".retired-" + Guid.NewGuid().ToString("N");
        Directory.Move(directory, retired);
        try { Directory.Delete(retired, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void WriteJournal(string directory, JsonObject journal)
    {
        var target = Path.Combine(directory, "journal.json");
        var temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporary, Encoding.UTF8.GetBytes(journal.ToJsonString() + "\n"));
            File.Move(temporary, target, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static IEnumerable<string> MutableFiles(string root)
    {
        if (File.Exists(Path.Combine(root, "discovery.json"))) yield return "discovery.json";
        foreach (var name in IndexFiles)
            if (File.Exists(Path.Combine(root, "index", name))) yield return "index/" + name;
        var sites = Path.Combine(root, "sites");
        if (!Directory.Exists(sites)) yield break;
        foreach (var directory in Directory.EnumerateDirectories(sites))
        {
            var site = Path.GetFileName(directory);
            if (!IsIdentifier(site)) continue;
            if (File.Exists(Path.Combine(directory, "stories.json"))) yield return $"sites/{site}/stories.json";
            foreach (var kind in new[] { "stories", "models" })
            {
                var manifests = Path.Combine(directory, kind);
                if (!Directory.Exists(manifests)) continue;
                foreach (var file in Directory.EnumerateFiles(manifests, "*.json", SearchOption.TopDirectoryOnly))
                    if (IsIdentifier(Path.GetFileNameWithoutExtension(file))) yield return $"sites/{site}/{kind}/{Path.GetFileName(file)}";
            }
            foreach (var name in new[] { "manifest.json", "resources.json" })
                if (File.Exists(Path.Combine(directory, "director", name))) yield return $"sites/{site}/director/{name}";
        }
    }

    private static bool IsMutableFile(string relative)
    {
        if (relative == "discovery.json") return true;
        var parts = relative.Split('/');
        if (parts.Length == 2 && parts[0] == "index") return IndexFiles.Contains(parts[1], StringComparer.Ordinal);
        if (parts.Length < 3 || parts[0] != "sites" || !IsIdentifier(parts[1])) return false;
        if (parts.Length == 3) return parts[2] == "stories.json";
        if (parts.Length != 4) return false;
        if (parts[2] == "director") return parts[3] is "manifest.json" or "resources.json";
        return (parts[2] is "stories" or "models") && parts[3].EndsWith(".json", StringComparison.Ordinal) && IsIdentifier(parts[3][..^5]);
    }

    private static bool IsIdentifier(string value) => value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
}
