using System.Text.Json.Nodes;
using OurNotes.Director.Core.Archiving;

internal static class ArchiveTransactionTests
{
    public static void Run(Action<bool, string> assert)
    {
        var root = Path.Combine(Path.GetTempPath(), "ournotes-transaction-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string PathFor(string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        void Put(string relative, string text)
        {
            var path = PathFor(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        try
        {
            string[] existing =
            [
                "discovery.json", "index/stats.json", "index/catalog.json", "index/commands.jsonl",
                "sites/intl/stories.json", "sites/intl/stories/7.json", "sites/intl/models/actor.json",
                "sites/intl/director/manifest.json", "sites/intl/director/resources.json",
            ];
            foreach (var relative in existing) Put(relative, "previous:" + relative);
            Put("notes.json", "user file");
            Put("sites/intl/stories/notes.txt", "user notes");
            using (new ArchiveTransaction(root))
            {
                foreach (var relative in existing) Put(relative, "new:" + relative);
                File.Delete(PathFor("sites/intl/models/actor.json"));
                Put("sites/jp/stories/8.json", "new story");
                Put("sites/jp/stories.json", "new selection");
                Put("index/voices.jsonl", "new index");
                Put("store/verified.bin", "verified download");
                Put("sites/intl/assets/verified.bin", "verified site copy");
                Put(".cache/http/cache.body", "revalidated metadata");
            }
            assert(existing.All(relative => File.ReadAllText(PathFor(relative)) == "previous:" + relative), "archive transaction restores manifests, selection, indexes and pack together");
            assert(!File.Exists(PathFor("sites/jp/stories/8.json")) && !File.Exists(PathFor("sites/jp/stories.json")) && !File.Exists(PathFor("index/voices.jsonl")), "archive rollback removes only newly published mutable metadata");
            assert(File.ReadAllText(PathFor("store/verified.bin")) == "verified download" && File.Exists(PathFor("sites/intl/assets/verified.bin")) && File.Exists(PathFor(".cache/http/cache.body")), "archive rollback retains verified assets and HTTP cache for resuming");
            assert(File.ReadAllText(PathFor("notes.json")) == "user file" && File.ReadAllText(PathFor("sites/intl/stories/notes.txt")) == "user notes", "archive transaction does not alter unrelated user files");

            using (var transaction = new ArchiveTransaction(root))
            {
                Put("discovery.json", "committed discovery");
                transaction.Commit();
            }
            assert(File.ReadAllText(PathFor("discovery.json")) == "committed discovery" && !Directory.Exists(PathFor(".archive-transaction")), "successful archive commit retains new metadata and removes its journal");

            // Abandon an active transaction to model process death; the next constructor
            // must recover before it snapshots anything from the interrupted generation.
            _ = new ArchiveTransaction(root);
            Put("discovery.json", "interrupted discovery");
            Put("sites/intl/stories/999.json", "interrupted new story");
            using (var recovered = new ArchiveTransaction(root))
            {
                assert(File.ReadAllText(PathFor("discovery.json")) == "committed discovery" && !File.Exists(PathFor("sites/intl/stories/999.json")), "next archive invocation recovers an interrupted transaction before starting");
                recovered.Commit();
            }

            // A crash after writing the committed marker must never revert that commit.
            _ = new ArchiveTransaction(root);
            Put("discovery.json", "committed before process exit");
            var journal = JsonNode.Parse(File.ReadAllBytes(PathFor(".archive-transaction/journal.json")))!;
            journal["state"] = "committed";
            Put(".archive-transaction/journal.json", journal.ToJsonString());
            using (var recovered = new ArchiveTransaction(root))
            {
                assert(File.ReadAllText(PathFor("discovery.json")) == "committed before process exit", "journal recovery honors a completed commit whose cleanup was interrupted");
                recovered.Commit();
            }

            var firstDownload = PathFor("first-download");
            using (new ArchiveTransaction(firstDownload))
            {
                Put("first-download/discovery.json", "incomplete first download");
                Put("first-download/index/stats.json", "incomplete index");
                Put("first-download/sites/intl/assets/verified.bin", "download to reuse");
            }
            assert(!File.Exists(PathFor("first-download/discovery.json")) && !File.Exists(PathFor("first-download/index/stats.json")) && File.Exists(PathFor("first-download/sites/intl/assets/verified.bin")), "failed first archive leaves reusable assets without publishing a partial archive");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
