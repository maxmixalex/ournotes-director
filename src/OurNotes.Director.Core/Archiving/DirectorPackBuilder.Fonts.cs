using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using static OurNotes.Director.Core.Archiving.ArchiveIndexStorage;

namespace OurNotes.Director.Core.Archiving;

public static partial class DirectorPackBuilder
{
    private static JsonNode? UiEntry(JsonObject manifest, string path, string? language) =>
        (language is null ? null : manifest["languages"]?[language]?["files"]?[path]) ?? manifest["files"]?[path];

    private static async Task MergeUiAsync(string siteDirectory, JsonObject baseManifest, List<JsonObject> manifests,
        JsonObject files, JsonObject languages, CancellationToken cancellationToken)
    {
        var ordered = new[] { baseManifest }.Concat(manifests.Where(manifest => !ReferenceEquals(manifest, baseManifest))).ToArray();
        var uiShaders = new List<JsonNode>();
        foreach (var manifest in ordered)
        {
            foreach (var (path, entry) in Properties(manifest["files"]))
            {
                if (!path.StartsWith("ui/shaders/", StringComparison.Ordinal)) continue;
                if (path == "ui/shaders/shaders.json")
                {
                    if (entry is not null) uiShaders.Add((await MaterializeAsync(siteDirectory, entry, cancellationToken).ConfigureAwait(false))!);
                }
                else if (!files.ContainsKey(path)) files[path] = Clone(entry);
                else if (!JsonNode.DeepEquals(files[path], entry))
                    throw new InvalidDataException($"Director UI shader-path collision: {path} (ADV {manifest["advId"]}).");
            }
        }
        if (uiShaders.Count > 0)
            files["ui/shaders/shaders.json"] = await PutJsonAssetAsync(siteDirectory, MergeShaderIndexes(uiShaders), cancellationToken).ConfigureAwait(false);

        if (ordered.Any(manifest => manifest["files"]?["ui/fonts.json"] is not null || manifest["files"]?["ui/ui.json"] is not null))
            await MergeUiLanguageAsync(siteDirectory, ordered, files, null, cancellationToken).ConfigureAwait(false);
        foreach (var (language, group) in languages)
        {
            group!["files"] ??= new JsonObject();
            await MergeUiLanguageAsync(siteDirectory, ordered, group["files"]!.AsObject(), language, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task MergeUiLanguageAsync(string siteDirectory, JsonObject[] manifests, JsonObject files,
        string? language, CancellationToken cancellationToken)
    {
        JsonObject? ui = null;
        var fonts = new JsonObject();
        foreach (var manifest in manifests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (UiEntry(manifest, "ui/ui.json", language) is JsonNode uiEntry)
            {
                var source = await MaterializeAsync(siteDirectory, uiEntry, cancellationToken).ConfigureAwait(false) as JsonObject
                    ?? throw new InvalidDataException("Director UI must be a JSON object.");
                if (ui is null) ui = source;
                else
                {
                    // Keep the chosen scene's main UI layout, while retaining the labels
                    // and bindings belonging to every exported chat window.
                    foreach (var field in new[] { "chatTexts", "chatStatusTexts" })
                    {
                        if (source[field] is null) continue;
                        ui[field] ??= new JsonObject();
                        MergeFirst(ui[field]!.AsObject(), source[field]);
                    }
                    if (ui["chatWidget"] is null) ui["chatWidget"] = Clone(source["chatWidget"]);
                }
            }
            if (UiEntry(manifest, "ui/fonts.json", language) is not JsonNode fontEntry) continue;
            var sourceFonts = await MaterializeAsync(siteDirectory, fontEntry, cancellationToken).ConfigureAwait(false) as JsonObject
                ?? throw new InvalidDataException("Director UI fonts must be a JSON object.");
            ImportFontTextures(fonts, sourceFonts, manifest, files, language);
            MergeFontDocuments(fonts, sourceFonts);
        }
        if (ui is not null) files["ui/ui.json"] = await PutJsonAssetAsync(siteDirectory, ui, cancellationToken).ConfigureAwait(false);
        if (fonts.Count > 0) files["ui/fonts.json"] = await PutJsonAssetAsync(siteDirectory, fonts, cancellationToken).ConfigureAwait(false);
    }

    private static void ImportFontTextures(JsonObject target, JsonObject source, JsonObject manifest,
        JsonObject files, string? language)
    {
        target["textures"] ??= new JsonObject();
        var textures = target["textures"]!.AsObject();
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, node) in Properties(source["textures"]))
        {
            var texture = node as JsonObject ?? throw new InvalidDataException("Invalid font texture descriptor.");
            var path = "ui/" + Text(texture["texture"]);
            var entry = UiEntry(manifest, path, language) ?? throw new InvalidDataException($"Font atlas is absent from the manifest: {path}");
            if (textures[name] is JsonObject existing && JsonNode.DeepEquals(existing, texture) &&
                JsonNode.DeepEquals(files["ui/" + Text(existing["texture"])], entry)) continue;

            var key = name;
            var copy = (JsonObject)texture.DeepClone();
            if (textures.ContainsKey(name) || files[path] is JsonNode previous && !JsonNode.DeepEquals(previous, entry))
            {
                // Episode subsets reuse atlas names but contain different glyph packing.
                // Keep each physical atlas and point individual glyphs to its unique key.
                key = "director_" + Text(manifest["advId"]) + "/" + name;
                var relative = "director/" + Text(manifest["advId"]) + "/" + Text(texture["texture"]);
                copy["name"] = key;
                copy["texture"] = relative;
                path = "ui/" + relative;
            }
            replacements[name] = key;
            if (textures[key] is not null && !JsonNode.DeepEquals(textures[key], copy))
                throw new InvalidDataException($"Director font atlas-key collision: {key}");
            textures[key] = copy;
            files[path] = Clone(entry);
        }
        RewriteTextureReferences(source, replacements);
    }

    private static void RewriteTextureReferences(JsonNode? node, IReadOnlyDictionary<string, string> replacements)
    {
        if (node is JsonObject obj)
        {
            foreach (var (key, value) in obj.ToArray())
            {
                if (value?.GetValueKind() == JsonValueKind.String && replacements.TryGetValue(value.GetValue<string>(), out var name)) obj[key] = name;
                else RewriteTextureReferences(value, replacements);
            }
        }
        else if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                var value = array[index];
                if (value?.GetValueKind() == JsonValueKind.String && replacements.TryGetValue(value.GetValue<string>(), out var name)) array[index] = name;
                else RewriteTextureReferences(value, replacements);
            }
        }
    }

    private static void MergeFontDocuments(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source)
        {
            if (key == "textures") continue; // Descriptors and their files were imported together.
            if (!target.TryGetPropertyValue(key, out var previous)) { target[key] = Clone(value); continue; }
            if (previous is not JsonObject objects || value is not JsonObject additions) continue;
            if (key is "fonts" or "spriteAssets")
            {
                foreach (var (name, face) in additions)
                {
                    if (objects[name] is not JsonObject existing) objects[name] = Clone(face);
                    else MergeFontFace(existing, face!.AsObject(), name, key == "spriteAssets");
                }
            }
            else if (key is "chatTexts" or "frameTexts" or "stillTexts" or "dialogTexts")
            {
                foreach (var (name, bindings) in additions)
                {
                    if (objects[name] is JsonObject existing && bindings is JsonObject) MergeFirst(existing, bindings);
                    else if (!objects.ContainsKey(name)) objects[name] = Clone(bindings);
                }
            }
            else MergeFirst(objects, additions);
        }
    }

    private static void MergeFontFace(JsonObject target, JsonObject source, string name, bool sprite)
    {
        foreach (var key in new[] { "faceInfo", "atlasPadding", "atlasRenderMode", "boldSpacing", "boldStyle", "normalSpacingOffset", "normalStyle" })
            RequireSameIfPresent(target[key], source[key], $"font {name}: {key}");

        if (!sprite)
        {
            MergeDistinctArray(target, source, "atlases");
            var atlases = Items(target["atlases"]).Select(Text).ToList();
            var sourceAtlases = Items(source["atlases"]).Select(Text).ToArray();
            foreach (var (_, glyph) in Properties(source["glyphs"]))
                if (glyph is JsonObject obj && int.TryParse(Text(obj["atlasIndex"]), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < sourceAtlases.Length)
                    obj["atlasIndex"] = atlases.IndexOf(sourceAtlases[index]);
        }
        foreach (var (id, glyph) in Properties(source["glyphs"]))
        {
            if (target["glyphs"]?[id] is not JsonNode previous) continue;
            RequireSameIfPresent(previous["metrics"], glyph?["metrics"], $"font {name}: glyph {id} metrics");
            RequireSameIfPresent(previous["scale"], glyph?["scale"], $"font {name}: glyph {id} scale");
        }
        foreach (var key in new[] { "glyphs", "glyphPairAdjustments" })
        {
            target[key] ??= new JsonObject();
            MergeFirst(target[key]!.AsObject(), source[key]);
        }
        if (sprite) MergeDistinctArray(target, source, "characters", row => Text(row?["name"]));
        else
        {
            target["characters"] ??= new JsonObject();
            foreach (var (id, character) in Properties(source["characters"]))
                RequireSameIfPresent(target["characters"]?[id], character, $"font {name}: character {id}");
            MergeFirst(target["characters"]!.AsObject(), source["characters"]);
        }
        foreach (var key in new[] { "fallbacks", "runtimeCharacters", "sequences" }) MergeDistinctArray(target, source, key);
        if (source["missingGlyph"] is JsonObject missing)
        {
            if (target["missingGlyph"] is not JsonObject existing) target["missingGlyph"] = Clone(missing);
            else
            {
                RequireSameIfPresent(existing["unicode"], missing["unicode"], $"font {name}: missing-glyph substitute");
                MergeDistinctArray(existing, missing, "characters");
            }
        }
        MergeFirst(target, source);
    }

    private static void RequireSameIfPresent(JsonNode? left, JsonNode? right, string description)
    {
        if (left is not null && right is not null && !JsonNode.DeepEquals(left, right))
            throw new InvalidDataException("Incompatible director " + description + ".");
    }

    private static void MergeDistinctArray(JsonObject target, JsonObject source, string key, Func<JsonNode?, string>? identity = null)
    {
        if (source[key] is not JsonArray additions) return;
        if (target[key] is not JsonArray existing) { target[key] = Clone(additions); return; }
        identity ??= row => row?.ToJsonString() ?? "null";
        var seen = new HashSet<string>(existing.Select(identity), StringComparer.Ordinal);
        foreach (var row in additions)
            if (seen.Add(identity(row))) existing.Add(Clone(row));
    }
}
