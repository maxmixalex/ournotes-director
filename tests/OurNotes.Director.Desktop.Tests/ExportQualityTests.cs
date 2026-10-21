using System.Text.Json.Nodes;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.Desktop.Tests;

static class ExportQualityTests
{
    internal static void Run(Action<bool, string> check)
    {
        check(ExportQuality.InitialCineFormQuality(null, true) == "film3+"
            && ExportQuality.InitialCineFormQuality(new JsonObject(), false) == VideoEncoding.DefaultCineFormQuality,
            "Transparent export prefers faint-edge quality without changing the general CineForm default");
        check(ExportQuality.InitialCineFormQuality(new JsonObject { ["cineformQuality"] = "film1" }, true) == "film1",
            "An explicit existing balanced-quality choice takes precedence over the transparent default");
        string[] supported = ["film3+", "film3", "film2+", "film2", "film1.5", "film1+", "film1", "high+", "high", "medium+", "medium", "low+", "low"];
        check(ExportQuality.CineFormChoices.Select(choice => choice.Value).SequenceEqual(supported)
            && CommandCatalog.Get("render.clip").Fields.Single(field => field.Key == "cineform-quality")
                .Choices.Select(choice => choice.Value).SequenceEqual(supported),
            "Both editor quality selectors and the command form expose all thirteen supported CineForm choices");

        string directory = Path.Combine(Path.GetTempPath(), "director-export-quality-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "saved-project.json");
            foreach (string quality in supported)
            {
                var source = SceneDocument.Parse("""
                    {"format":"ournotes-director/project","version":1,
                    "source":{"adv":"fixture","site":"intl","language":"zh-Hans"},
                    "output":{"width":640,"height":960,"supersample":1,"codec":"cineform","clean":true,"letterbox":"none"},
                    "initialize":[],"commands":[],"finalize":[]}
                    """);
                source.SetOutput(cineformQuality: quality); source.Save(path);
                var reopened = SceneDocument.Open(path);
                // The UI uses this same initial value and list; a missing list
                // entry used to select its first item (film1) without an error.
                string initial = ExportQuality.InitialCineFormQuality(reopened.Root["output"] as JsonObject, true);
                string selected = ExportQuality.CineFormChoices.Single(choice => choice.Value == initial).Value;
                reopened.SetOutput(cineformQuality: selected); reopened.Save();
                var values = new Dictionary<string, string?> { ["project"] = path, ["out"] = Path.Combine(directory, quality + ".mov"),
                    ["duration"] = "1", ["supersample"] = "4", ["cineform-quality"] = selected };
                var command = CommandLineBuilder.Build("render.clip", values, new() { ArchivePath = directory, WorkingDirectory = directory });
                int argument = command.Arguments.ToList().IndexOf("--cineform-quality");
                int sampling = command.Arguments.ToList().IndexOf("--supersample");
                check(command.IsValid && argument >= 0 && command.Arguments[argument + 1] == quality
                    && sampling >= 0 && command.Arguments[sampling + 1] == "4"
                    && SceneDocument.Open(path).Output.CineformQuality == quality,
                    $"Saved {quality} and an explicit 4x export override survive editor choice, project save and the actual command without a silent downgrade");
            }
        }
        finally { Directory.Delete(directory, true); }
    }
}
