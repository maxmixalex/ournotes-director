using System.Text.Json.Nodes;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.Desktop.Tests;

internal static class LibraryProjectWorkflowTests
{
    internal static void Run(Action<bool, string> check)
    {
        string folder = Path.Combine(Path.GetTempPath(), "library-project-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var context = new CommandContext { ArchivePath = folder, WorkingDirectory = folder, OutputDirectory = folder };
            var creative = CreativeScene.Blank("jp", "ja");
            string actor = creative.AddActor("document-fixture-model", "Fixture");
            creative.AddClip(actor, "motion", 2, 3, new JsonObject { ["cmd"] = "Motion", ["MotionName"] = "fixture-motion" }, "Motion");
            string creativePath = Path.Combine(folder, "creative.json");
            File.WriteAllText(creativePath, creative.ToJson());
            var reopened = CreativeScene.Parse(File.ReadAllText(creativePath));
            var compiled = reopened.Compile();
            var pasted = SceneDocument.Parse("```json\n" + compiled.ToJsonString() + "\n```", baseDirectory: folder);
            string projectPath = Path.Combine(folder, "project.json");
            pasted.Save(projectPath);
            var saved = SceneDocument.Open(projectPath);
            var export = StudioSceneDocument.CreateExportView(saved, reopened.Duration + 1d / 30).ToSceneDocument();
            string before = export.Text;
            using var snapshot = export.CreateSnapshot(Path.Combine(folder, "snapshots"));
            foreach (string command in new[] { "render.validate", "render.state", "render.frame", "render.contact", "render.clip", "render.sequence" })
            {
                var built = CommandLineBuilder.Build(command, new Dictionary<string, string?> { ["project"] = snapshot.FilePath }, context);
                check(built.IsValid, "library scene passes " + command + " after save, reopen, JSON paste and export snapshot: "
                    + string.Join("; ", built.Errors.Select(error => error.Message)));
            }
            var range = CommandLineBuilder.Build("render.clip", new Dictionary<string, string?>
            {
                ["project"] = snapshot.FilePath, ["time"] = "2", ["duration"] = "1", ["codec"] = "cineform"
            }, context);
            check(range.IsValid && range.OutputPath!.EndsWith(".mov", StringComparison.Ordinal),
                "library scene range export keeps its CineForm output without a fabricated story ID");
            var native = DirectorProject.Read(snapshot.FilePath);
            check(native.Adv == DirectorProject.LibrarySource && native.Site == "jp" && native.Language == "ja"
                && JsonNode.DeepEquals(DirectorProject.Document(snapshot.FilePath)["commands"], compiled["commands"])
                && export.Text == before,
                "preview and export use the same library, language and exact native scheduling without editing the open document");

            var isolated = SceneAssetExport.Isolate(export, actor, 640, 1080, 1);
            using var actorSnapshot = isolated.CreateSnapshot(Path.Combine(folder, "snapshots"));
            foreach (string command in new[] { "render.frame", "render.clip" })
                check(CommandLineBuilder.Build(command, new Dictionary<string, string?> { ["project"] = actorSnapshot.FilePath }, context).IsValid,
                    "single-character library asset passes " + command + " through the export form");

            CommandBuildResult Build(JsonObject value, string command = "render.frame", string? story = null)
            {
                string path = Path.Combine(folder, "source-contract.json");
                File.WriteAllText(path, value.ToJsonString());
                var values = new Dictionary<string, string?> { [story is null ? "project" : "script"] = path };
                if (story is not null) values["advId"] = story;
                return CommandLineBuilder.Build(command, values, context);
            }
            foreach (string? sourceId in new string?[] { null, "", " ", DirectorProject.LibrarySource })
            {
                var value = (JsonObject)compiled.DeepClone();
                if (sourceId is null) value["source"]!.AsObject().Remove("adv");
                else value["source"]!["adv"] = sourceId;
                check(Build(value).IsValid, "explicit library commands accept the existing source form: " + (sourceId ?? "omitted"));
            }
            foreach (string section in new[] { "initialize", "commands", "finalize" })
            {
                var missing = (JsonObject)compiled.DeepClone(); missing.Remove(section);
                check(!Build(missing).IsValid, "library export rejects inherited " + section + " instead of silently adopting a story");
                missing[section] = null;
                check(!Build(missing).IsValid, "library export rejects null inherited " + section);
                missing[section] = new JsonArray();
                check(Build(missing).IsValid, "explicitly empty library " + section + " remains valid");
            }
            foreach (JsonNode? invalidSource in new JsonNode?[] { null, new JsonArray(), new JsonObject { ["adv"] = 42 } })
            {
                var invalid = (JsonObject)compiled.DeepClone(); invalid["source"] = invalidSource;
                check(!Build(invalid).IsValid, "missing or malformed source metadata is still blocked by the shared editor validation");
            }
            var invalidRow = (JsonObject)compiled.DeepClone();
            invalidRow["commands"] = new JsonArray(new JsonObject { ["cmd"] = "Delay", ["Frames"] = -1 });
            check(!Build(invalidRow).IsValid, "removing the source-story requirement does not bypass invalid native timing checks");

            var original = SceneTemplates.Create("story-patch", new SceneSource("42", "jp", "ja"));
            var inherited = original.Root;
            check(Build(inherited).IsValid && new[] { "initialize", "commands", "finalize" }.All(section => inherited[section] is null),
                "a real source story can still inherit all three original performance sections unchanged");
            check(Build(inherited, "story.frame", "42").IsValid && !Build(inherited, "story.frame", "43").IsValid,
                "original-story imports retain their source ID matching requirement");
            check(!Build(compiled, "story.frame", "42").IsValid,
                "library projects cannot silently become replacements for an unrelated original story");
        }
        finally { Directory.Delete(folder, true); }
    }
}
