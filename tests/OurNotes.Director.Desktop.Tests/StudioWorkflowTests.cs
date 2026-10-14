using System.Text.Json.Nodes;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.Desktop.Tests;

internal static class StudioWorkflowTests
{
    internal static void Run(Action<bool, string> check)
    {
        var scene = StudioSceneDocument.CreateBlank("jp", "ja", 2);
        check(scene.Document.Source.IsLibrary && scene.Document.Root["source"]?["adv"] is null,
            "new scene contains no fabricated or required source story");
        check(!scene.Document.Validate().HasErrors && scene.Document.Root["initialize"] is JsonArray && scene.Document.Root["finalize"] is JsonArray,
            "blank project is explicit and valid without a material archive");
        var actor = scene.AddActor("fixture-model"); scene.SetMotion(actor, "actual-motion", .5);
        string before = scene.ToJson();
        var metadata = JsonNode.Parse("""{"canvas":{"width":1001,"height":2003}}""")!;
        var png = SceneAssetExport.Canvas(metadata); var mov = SceneAssetExport.Canvas(metadata, "cineform");
        check(png == (1001, 2003) && mov == (1008, 2008), "image keeps exact canvas, only CineForm applies required alignment");
        var isolated = SceneAssetExport.Isolate(scene.ToSceneDocument(), actor, png.Width, png.Height);
        check(scene.ToJson() == before && isolated.Root["output"]?["isolateActor"]?.ToString() == actor,
            "asset export overrides a clone without modifying the open scene");
        check(JsonNode.DeepEquals(isolated.Root["commands"], scene.Document.Root["commands"]) && isolated.Output.Letterbox == "none",
            "isolated export preserves authored motion timing and removes scene letterbox");
        check(!isolated.Validate().HasErrors, "image canvas is not rejected because the default video codec has alignment limits");
        check(isolated.Root["output"]?["playbackViewport"]?["letterbox"]?.ToString() == scene.Document.Output.Letterbox,
            "isolation preserves the source viewport's opening chronology separately from exported letterbox pixels");
        var twice = SceneAssetExport.Isolate(isolated, actor, mov.Width, mov.Height);
        check(JsonNode.DeepEquals(twice.Root["output"]?["playbackViewport"], isolated.Root["output"]?["playbackViewport"]),
            "changing isolated output resolution never overwrites the source playback viewport");
        using var snapshot = isolated.CreateSnapshot(Path.Combine(Path.GetTempPath(), "ournotes-workflow-" + Guid.NewGuid().ToString("N")));
        var native = DirectorProject.Read(snapshot.FilePath);
        check(native.Adv == DirectorProject.LibrarySource && native.Site == "jp" && native.Output["isolateActor"]?.ToString() == actor,
            "native project uses shared library without inheriting a demonstration episode");
        var inherited = scene.Document.Root; inherited.Remove("initialize");
        check(SceneDocument.Parse(inherited.ToJsonString()).Validate().HasErrors, "library project cannot accidentally inherit unknown opening commands");
        scene.Undo(); scene.Redo();
        check(scene.ToJson() == before, "blank project commands still round-trip through undo and redo");
    }
}
