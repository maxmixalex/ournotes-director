using System.Text.Json.Nodes;
using OurNotes.Director.Core;

namespace OurNotes.Director.Desktop.Core;

public sealed partial class StudioSceneDocument
{
    string? sourceModelsAdv, sourceModelsSite;
    readonly Dictionary<string, string> sourceModels = new(StringComparer.Ordinal);

    /// <summary>Loads the small story/model association JSON, including for projects whose command arrays were already expanded and saved.</summary>
    public int ResolveSourceModels(Archive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        var source = document.Source;
        var manifest = archive.Manifest(source.Site, "stories", source.Adv);
        var definition = JsonNode.Parse(archive.Logical(source.Site, manifest, "story.json")) as JsonObject
            ?? throw new InvalidDataException("归档 story.json 不是对象。");
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        if (definition["models"] is JsonObject models)
            foreach (var (address, value) in models)
                if (value is JsonValue && Text(value) is { Length: > 0 } id) resolved[address] = id;
        sourceModels.Clear(); foreach (var pair in resolved) sourceModels[pair.Key] = pair.Value;
        sourceModelsAdv = source.Adv; sourceModelsSite = source.Site; InvalidateSamples();
        return sourceModels.Count;
    }

    /// <summary>Copies source associations from a background-loaded clone without reading files or editing the scene JSON.</summary>
    public void ImportSourceMetadata(StudioSceneDocument source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (ReferenceEquals(this, source)) return;
        var own = document.Source;
        if (source.sourceModelsAdv != own.Adv || source.sourceModelsSite != own.Site)
            throw new ArgumentException("来源模型映射与当前剧情或站点不匹配。", nameof(source));
        sourceModels.Clear(); foreach (var pair in source.sourceModels) sourceModels[pair.Key] = pair.Value;
        sourceModelsAdv = source.sourceModelsAdv; sourceModelsSite = source.sourceModelsSite; InvalidateSamples();
    }

    public string? ResolveCommandModel(string id)
    {
        var row = RequireCommand(id).Data as JsonObject;
        if (row is null) return null;
        if (NativeCommandName(row) == "Character") return ResolveActorModel(row);
        string target = Text(row["TargetName"]);
        return originals.Where(value => value.Command == "Character" && !Ignored(value.Data) && Text(value.Data["TargetName"]) == target)
            .Select(value => ResolveActorModel(value.Data)).FirstOrDefault(value => value.Length > 0);
    }

    string ResolveActorModel(JsonObject row)
    {
        string model = Text(row["Model"]);
        if (model.Length > 0) return model;
        var source = document.Source;
        return sourceModelsAdv == source.Adv && sourceModelsSite == source.Site
            ? sourceModels.GetValueOrDefault("Character/Live2D/" + Text(row["TargetAssetName"]), "") : "";
    }
}
