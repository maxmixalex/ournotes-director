using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

namespace OurNotes.Director.Desktop.Core;

/// <summary>A private, expanded source document and its character/model associations.
/// Timing is supplied separately by the native interpreter; preparation never evaluates
/// an editor timeline or rewrites an existing performance's scheduling fields.</summary>
public sealed record CreativeSourceImport(SceneDocument Document, IReadOnlyDictionary<string, string> ActorModels)
{
    public CreativeScene CreateScene(NativePlaybackTiming timing, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scene = CreativeScene.FromPerformance(Document.Root, timing, cancellationToken);
        var ids = scene.Actors.OfType<JsonObject>().Select(actor => CreativeScene.Text(actor, "id"))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var (id, model) in ActorModels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(model) && ids.Add(id))
                scene.Actors.Add((JsonNode)new JsonObject
                    { ["id"] = id, ["name"] = id, ["model"] = model, ["fromSource"] = true });
        }
        return scene;
    }
}

/// <summary>Prepares original stories without constructing the unrelated keyframe editor,
/// its undo snapshots, static timing scans or sampled actor state.</summary>
public static class CreativeSourceImporter
{
    public static CreativeSourceImport Prepare(string json, string baseDirectory, Archive archive,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(archive);
        var document = SceneDocument.Parse(json, baseDirectory: baseDirectory);
        var validation = document.Validate();
        if (validation.HasErrors)
            throw new ArgumentException(string.Join(Environment.NewLine,
                validation.Issues.Where(issue => issue.Severity == SceneIssueSeverity.Error)
                    .Select(issue => issue.Path + "：" + issue.Message)));
        cancellationToken.ThrowIfCancellationRequested();
        var source = document.Source;
        var manifest = archive.Manifest(source.Site, "stories", source.Adv);
        JsonObject Read(string logical)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return JsonNode.Parse(archive.Logical(source.Site, manifest, logical)) as JsonObject
                ?? throw new InvalidDataException("归档 " + logical + " 不是对象。");
        }
        var definition = Read("story.json");
        var episode = Read(Archive.S(definition, "episode"));
        var scene = Read(Archive.S(definition, "scene"));
        var settings = scene["settings"]?["playerSettings"] as JsonObject
            ?? throw new InvalidDataException("归档场景缺少 settings.playerSettings。");
        cancellationToken.ThrowIfCancellationRequested();
        // Use the same lossless expansion as the native-project editor. Explicit
        // empty arrays and complete text-entry overrides retain their precedence.
        document.ExpandInheritedSections(episode, settings);
        cancellationToken.ThrowIfCancellationRequested();
        var actors = new Dictionary<string, string>(StringComparer.Ordinal);
        var root = document.Root;
        foreach (string section in new[] { "initialize", "commands", "finalize" })
        {
            if (root[section] is not JsonArray rows) continue;
            for (int index = 0; index < rows.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (rows[index] is not JsonObject row || row["IgnoreData"]?.ToString() is "1" or "true") continue;
                // Inspect only definitions: parsing and cloning every animation row
                // just to discover the few actors recreates the old import bottleneck.
                if (row["cmd"]?.ToString() != "Character" && (row["Command"] ?? row["raw"])?.ToString() != "23") continue;
                var command = AdvProgram.Row(row, section + "[" + index + "]");
                if (command.Command != "Character") continue;
                string id = command.Text("TargetName"), model = command.Text("Model");
                if (model.Length == 0 && definition["models"] is JsonObject models
                    && models["Character/Live2D/" + command.Text("TargetAssetName")] is JsonValue value)
                    model = value.ToString();
                // The first enabled definition of an ID is the source association,
                // including when its model is unresolved. Executed explicit Model
                // commands retain precedence when the editable scene is created.
                actors.TryAdd(id, model);
            }
        }
        return new(document, new ReadOnlyDictionary<string, string>(actors));
    }
}
