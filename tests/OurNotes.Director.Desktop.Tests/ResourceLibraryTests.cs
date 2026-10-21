using System.Text;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.Desktop.Tests;

static class ResourceLibraryTests
{
    internal static void Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "ournotes-resource-library-" + Guid.NewGuid().ToString("N"));
        try
        {
            JsonObject Put(string text)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text); string asset = "assets/" + Archive.Hash(bytes) + ".json";
                Write("sites/intl/" + asset, text);
                return new() { ["asset"] = asset, ["size"] = bytes.Length, ["stored"] = bytes.Length };
            }
            void Write(string relative, string text)
            {
                string file = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, text);
            }
            Write("index/stats.json", """{"stories":1,"models":1}""");
            Write("discovery.json", """{"band":{"token":"millsage","bandTextId":"Band_Name_millsage"},"archive":{"languages":["zh-Hans"]}}""");
            Write("sites/intl/stories.json", """{"stories":[{"advId":10060,"manifest":"stories/10060.json"}]}""");
            Write("index/catalog.json", """{"stories":[{"advId":10060,"site":"intl","manifest":"stories/10060.json","titles":{"zh-Hans":"彩虹色的光芒","en":"Rainbow-colored light"},"languages":["zh-Hans"]}]}""");
            Write("index/models.json", """[{"id":"kanata","site":"intl","manifest":"models/kanata.json"}]""");
            var shared = Put("{\"originalNumber\":1.2300e-7}");
            var model = Put("{\"character\":\"kanata\"}");
            var localized = Put("{\"speaker\":\"???\"}");
            var absentLanguage = new JsonObject { ["asset"] = "assets/" + new string('a', 64) + ".json", ["size"] = 2, ["stored"] = 2 };
            Write("sites/intl/stories/10060.json", new JsonObject
            {
                ["files"] = new JsonObject { ["story.json"] = shared.DeepClone(), ["same-content.json"] = shared.DeepClone() },
                ["models"] = new JsonObject { ["kanata"] = "models/kanata.json" },
                ["languages"] = new JsonObject { ["zh-Hans"] = new JsonObject { ["files"] = new JsonObject { ["text.json"] = localized } },
                    ["ja"] = new JsonObject { ["files"] = new JsonObject { ["text.json"] = absentLanguage } } }
            }.ToJsonString());
            Write("sites/intl/models/kanata.json", new JsonObject { ["files"] = new JsonObject { ["model.json"] = model } }.ToJsonString());
            var summary = ResourceLibrary.ReadSummary(root);
            check(summary.Stories == 1 && summary.Models == 1 && summary.Band == "millsage" && summary.Sites.SequenceEqual(["intl"])
                && summary.Languages!.SequenceEqual(["zh-Hans"]), "Resource library reads counts, canonical band and selected languages from the original archive");
            var story = ResourceLibrary.ReadItems(root, "stories", "zh-Hans").Single();
            check(story.Title == "彩虹色的光芒" && story.Selection("adv").Value == "10060" && story.Context["site"] == "intl"
                && story.SearchText.Contains("Rainbow-colored light", StringComparison.Ordinal), "Resource search retains translated names and exact story/site selection");
            var modelRow = ResourceLibrary.ReadItems(root, "models", "zh-Hans").Single();
            check(modelRow.Kind == "model" && modelRow.Id == "kanata", "Resource model browsing retains the original model identity");
            var result = ResourceLibrary.CheckFiles(root, story);
            check(result.Success && result.Files == 3 && result.StoredBytes > 0,
                "Local verification follows model dependencies, deduplicates shared files and ignores languages that were not downloaded");
            string assetPath = Path.Combine(root, "sites/intl", shared["asset"]!.ToString());
            byte[] original = File.ReadAllBytes(assetPath); byte[] corrupted = (byte[])original.Clone(); corrupted[0] ^= 1; File.WriteAllBytes(assetPath, corrupted);
            check(ResourceLibrary.CheckFiles(root, story).Problems.Any(problem => problem.Contains("SHA-256", StringComparison.Ordinal)),
                "Verification reports corrupted content even when its stored size is unchanged");
            File.WriteAllBytes(assetPath, original);
            string modelPath = Path.Combine(root, "sites/intl/models/kanata.json"); File.Move(modelPath, modelPath + ".saved");
            check(ResourceLibrary.CheckFiles(root, story).Problems.Any(problem => problem.Contains("models/kanata.json", StringComparison.Ordinal)),
                "Verification reports a missing referenced model manifest instead of treating the story as complete");
            File.Move(modelPath + ".saved", modelPath);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            check(Throws<OperationCanceledException>(() => ResourceLibrary.ReadItems(root, "stories", "zh-Hans", cancellation.Token))
                && Throws<OperationCanceledException>(() => ResourceLibrary.CheckFiles(root, story, cancellationToken: cancellation.Token)),
                "Cancelled library reads and verification stop without rewriting the archive");
            var traversal = story with { Data = new JsonObject { ["manifest"] = "../../outside.json" } };
            check(Throws<InvalidDataException>(() => ResourceLibrary.CheckFiles(root, traversal)), "Resource verification rejects a manifest path outside the library");
            var request = ResourceLibrary.DownloadRequest("millsage", Path.Combine(root, "download"), "intl", "zh-Hans", 4, true, root);
            check(request.CommandId == "archive" && request.Arguments[0] == "archive" && request.Arguments.Contains("zh-Hans")
                && !request.Arguments.Contains("--no-deep") && request.OutputKind == CommandOutputKind.Directory,
                "The resource manager submits the real archive command with selected language and deep discovery");
            check(Throws<ArgumentException>(() => ResourceLibrary.DownloadRequest("millsage", "", "intl", null, 4, true, root))
                && Throws<ArgumentException>(() => ResourceLibrary.DownloadRequest("", root, "intl", null, 4, true, root)),
                "An empty download destination or band cannot silently select the current working directory or another band");
            var settings = new DesktopSettings { ArchivePath = root };
            settings.RememberResourceLibrary(root); settings.RememberResourceLibrary(root); settings.RememberResourceLibrary(Path.Combine(root, "second"));
            string settingsPath = Path.Combine(root, "settings.json"); settings.Save(settingsPath);
            var loaded = DesktopSettings.Load(settingsPath);
            check(loaded.ResourceLibraries.Count == 2 && loaded.ResourceLibraries[1] == root && loaded.ArchivePath == root,
                "Multiple local libraries persist without duplicate registrations or changing the active source");
            loaded.ResourceLibraries.Remove(root); loaded.Save(settingsPath);
            check(File.Exists(assetPath) && File.Exists(modelPath), "Removing a library registration leaves the user's resources untouched");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    static bool Throws<T>(Action action) where T : Exception { try { action(); return false; } catch (T) { return true; } }
}
