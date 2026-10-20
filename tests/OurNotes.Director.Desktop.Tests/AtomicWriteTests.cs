using System.Text;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.Desktop.Tests;

internal static class AtomicWriteTests
{
    internal static void Run(Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ournotes-atomic-write-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "nested", "report.json");
        try
        {
            const string before = "{\"phase\":\"开始\"}";
            const string after = "{\"phase\":\"完成\"}";
            DesktopSettings.AtomicWrite(path, before);
            check(File.ReadAllText(path) == before && !File.ReadAllBytes(path).AsSpan().StartsWith(Encoding.UTF8.Preamble),
                "Atomic writes create a complete UTF-8 document without a BOM in a new directory");

            using (var original = OpenSnapshot(path))
            {
                DesktopSettings.AtomicWrite(path, after);
                using var current = OpenSnapshot(path);
                DesktopSettings.AtomicWrite(path, "{\"phase\":\"next\"}");
                check(original.ReadToEnd() == before && current.ReadToEnd() == after
                    && File.ReadAllText(path) == "{\"phase\":\"next\"}",
                    "Two retained report readers keep their complete snapshots while later atomic writes remain readable");
            }

            if (OperatingSystem.IsWindows())
            {
                using var exclusive = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                bool rejected = false;
                try { DesktopSettings.AtomicWrite(path, after); }
                catch (IOException) { rejected = true; }
                catch (UnauthorizedAccessException) { rejected = true; }
                using var reader = new StreamReader(exclusive, Encoding.UTF8, leaveOpen: true);
                check(rejected && reader.ReadToEnd() == "{\"phase\":\"next\"}",
                    "A destination that forbids replacement reports failure and retains its previous complete document");
            }
            check(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp-*").Length == 0,
                "Completed and rejected atomic writes remove only their private temporary files");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static StreamReader OpenSnapshot(string path) => new(File.Open(path, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete), Encoding.UTF8);
}
