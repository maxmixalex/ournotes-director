using System.Text.Json.Nodes;
using OurNotes.Director.Core;

namespace OurNotes.Director.Desktop.Core;

public sealed partial class SceneDocument
{
    /// <summary>True means this section still comes from the selected archive story; an explicit empty array is not inherited.</summary>
    public bool IsSectionInherited(string section)
    {
        EnsureUnified(); CheckSection(section);
        return root[section] is null;
    }

    public JsonArray? GetSection(string section)
    {
        EnsureUnified(); CheckSection(section);
        if (root[section] is null) return null;
        return root[section] is JsonArray rows ? (JsonArray)rows.DeepClone()
            : throw new InvalidDataException(section + " 必须为指令数组；请先修正高级 JSON。");
    }

    public SceneRowLocation InsertCommand(string section, int index, JsonObject row)
    {
        ArgumentNullException.ThrowIfNull(row); EnsureUnified(); CheckSection(section);
        Mutate(copy =>
        {
            var rows = EditableSection(copy, section);
            if (index < 0 || index > rows.Count) throw new ArgumentOutOfRangeException(nameof(index));
            rows.Insert(index, row.DeepClone());
        });
        return new(section, index);
    }

    /// <summary>Replaces exactly one native row. Unknown commands and fields are retained for explicit native validation.</summary>
    public void UpdateCommand(SceneRowLocation location, JsonObject row)
    {
        ArgumentNullException.ThrowIfNull(row); EnsureUnified(); CheckSection(location.Section);
        Mutate(copy =>
        {
            var rows = EditableSection(copy, location.Section);
            RequireRowIndex(rows, location.Index);
            rows[location.Index] = row.DeepClone();
        });
    }

    public void PatchCommand(SceneRowLocation location, JsonObject patch)
    {
        ArgumentNullException.ThrowIfNull(patch); EnsureUnified(); CheckSection(location.Section);
        Mutate(copy => Merge(ArrayRow(copy, location), patch));
    }

    public void RemoveCommand(SceneRowLocation location)
    {
        EnsureUnified(); CheckSection(location.Section);
        Mutate(copy =>
        {
            var rows = EditableSection(copy, location.Section);
            RequireRowIndex(rows, location.Index); rows.RemoveAt(location.Index);
        });
    }

    /// <summary>Index is the final index after removal, including for a move within the same section.</summary>
    public SceneRowLocation MoveCommand(SceneRowLocation location, string section, int index)
    {
        EnsureUnified(); CheckSection(location.Section); CheckSection(section);
        Mutate(copy =>
        {
            var source = EditableSection(copy, location.Section);
            var destination = EditableSection(copy, section);
            RequireRowIndex(source, location.Index);
            int countAfterRemoval = destination.Count - (ReferenceEquals(source, destination) ? 1 : 0);
            if (index < 0 || index > countAfterRemoval) throw new ArgumentOutOfRangeException(nameof(index));
            var row = source[location.Index]?.DeepClone();
            source.RemoveAt(location.Index); destination.Insert(index, row);
        });
        return new(section, index);
    }

    public SceneRowLocation DuplicateCommand(SceneRowLocation location)
    {
        EnsureUnified(); CheckSection(location.Section);
        var rows = EditableSection(root, location.Section);
        RequireRowIndex(rows, location.Index);
        if (rows[location.Index] is not JsonObject row)
            throw new InvalidDataException("该指令不是 JSON 对象；可先修正或删除此行。");
        return InsertCommand(location.Section, location.Index + 1, row);
    }

    public void SetCommandEnabled(SceneRowLocation location, bool enabled)
    {
        EnsureUnified(); CheckSection(location.Section);
        var original = ArrayRow(root, location);
        if ((Scalar(original["IgnoreData"]) is not ("1" or "true")) == enabled) return;
        Mutate(copy =>
        {
            var row = ArrayRow(copy, location);
            row["IgnoreData"] = row["IgnoreData"] is JsonValue value && value.TryGetValue<bool>(out _)
                ? JsonValue.Create(!enabled) : JsonValue.Create(enabled ? 0 : 1);
        });
    }

    /// <summary>Loads actual source JSON only. This does not initialize a native renderer or assert renderability.</summary>
    public StudioExpansionResult ExpandInheritedSections(Archive archive)
    {
        ArgumentNullException.ThrowIfNull(archive); EnsureUnified();
        var source = Source;
        var manifest = archive.Manifest(source.Site, "stories", source.Adv);
        var definition = JsonNode.Parse(archive.Logical(source.Site, manifest, "story.json")) as JsonObject
            ?? throw new InvalidDataException("归档 story.json 不是对象。");
        var episode = JsonNode.Parse(archive.Logical(source.Site, manifest, Scalar(definition["episode"]))) as JsonObject
            ?? throw new InvalidDataException("归档原剧情不是对象。");
        var scene = JsonNode.Parse(archive.Logical(source.Site, manifest, Scalar(definition["scene"]))) as JsonObject
            ?? throw new InvalidDataException("归档场景不是对象。");
        var settings = scene["settings"]?["playerSettings"] as JsonObject
            ?? throw new InvalidDataException("归档场景缺少 settings.playerSettings。");
        return ExpandInheritedSections(episode, settings);
    }

    /// <summary>
    /// episode uses commands/text; playerSettings uses _initializeEpisodes/_finalizeEpisodes.
    /// Only omitted/null sections are expanded. Explicit arrays and project text overrides win unchanged.
    /// </summary>
    public StudioExpansionResult ExpandInheritedSections(JsonObject episode, JsonObject playerSettings)
    {
        ArgumentNullException.ThrowIfNull(episode); ArgumentNullException.ThrowIfNull(playerSettings); EnsureUnified();
        int initialize = 0, commands = 0, finalize = 0, textEntries = 0;
        var copy = (JsonObject)root.DeepClone();
        foreach (string section in Sections)
        {
            if (copy[section] is not null)
            {
                if (copy[section] is not JsonArray) throw new InvalidDataException(section + " 必须为指令数组。");
                continue;
            }
            var data = section switch
            {
                "initialize" => playerSettings["_initializeEpisodes"],
                "finalize" => playerSettings["_finalizeEpisodes"],
                _ => episode["commands"]
            };
            if (data is not JsonArray rows) throw new InvalidDataException("原剧情缺少 " + section + " 指令数组。");
            copy[section] = rows.DeepClone();
            if (section == "initialize") initialize = rows.Count;
            else if (section == "finalize") finalize = rows.Count;
            else commands = rows.Count;
        }
        if (episode["text"] is not null && episode["text"] is not JsonObject)
            throw new InvalidDataException("原剧情 text 必须为对象。");
        if (copy["text"] is not null && copy["text"] is not JsonObject)
            throw new InvalidDataException("项目 text 必须为对象；原文保持不变。");
        if (episode["text"] is JsonObject sourceText)
        {
            var merged = (JsonObject)sourceText.DeepClone();
            var overrides = copy["text"] as JsonObject;
            textEntries = sourceText.Count(pair => overrides?.ContainsKey(pair.Key) != true);
            if (overrides is not null)
                foreach (var pair in overrides) merged[pair.Key] = pair.Value?.DeepClone();
            copy["text"] = merged;
        }
        if (!JsonNode.DeepEquals(root, copy)) ReplaceText(Serialize(copy));
        return new(initialize, commands, finalize, textEntries);
    }

    /// <summary>Canonical render input with local image paths anchored to this document's directory.</summary>
    public JsonObject RenderRoot()
    {
        var copy = (JsonObject)root.DeepClone();
        RewriteImagePaths(copy, ResolveAssetPath);
        return copy;
    }

    static JsonArray EditableSection(JsonObject value, string section)
    {
        if (value[section] is null)
            throw new InvalidOperationException(section + " 当前继承原剧情，请先从归档展开该段；不会用空数组覆盖它。");
        return value[section] as JsonArray
            ?? throw new InvalidDataException(section + " 必须为指令数组。");
    }
    static void RequireRowIndex(JsonArray rows, int index)
    {
        if (index < 0 || index >= rows.Count) throw new ArgumentOutOfRangeException(nameof(index), "指令位置已不存在，请刷新指令列表。");
    }
}
