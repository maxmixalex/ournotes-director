using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

internal sealed record HomeSlotMap(IReadOnlyDictionary<string, IReadOnlySet<string>> SlotsByCharacter,
    IReadOnlyList<string> SharedSceneSlots, string? UnsupportedReason);

/// <summary>
/// Resolves artwork identity in a shared SkeletonAnimation without changing its bone graph.
/// Slot names identify the art; bones are secondary evidence because the original scenes
/// deliberately attach props and hands to another person's animated bones.
/// </summary>
internal static class HomeSlotOwnership
{
    public static HomeSlotMap Resolve(JsonObject skeleton, IReadOnlyList<string> characters, string partPath)
    {
        var slots = (skeleton["slots"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        var boneList = (skeleton["bones"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        var bones = boneList.ToDictionary(b => HomeCatalog.Text(b["name"]), StringComparer.Ordinal);
        var included = characters.ToDictionary(name => name, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.OrdinalIgnoreCase);
        var aliases = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var shared = new List<string>();
        var unresolved = new List<string>();

        HashSet<string> Names(string value)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var character in characters)
            {
                var offset = 0;
                while (offset <= value.Length - character.Length)
                {
                    var index = value.IndexOf(character, offset, StringComparison.OrdinalIgnoreCase);
                    if (index < 0) break;
                    var end = index + character.Length;
                    if ((index == 0 || !char.IsLetterOrDigit(value[index - 1])) && (end == value.Length || !char.IsLetterOrDigit(value[end])))
                        result.Add(character);
                    offset = end;
                }
            }
            return result;
        }

        string? BoneOwner(string name)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (bones.TryGetValue(name, out var bone) && visited.Add(name))
            {
                var names = Names(name);
                if (names.Count == 1) return names.Single();
                // A copied bone can name both people. Its nearest unambiguous ancestor,
                // rather than the first token of that copied name, supplies the evidence.
                name = HomeCatalog.Text(bone["parent"]);
            }
            return null;
        }

        // Some exports use Japanese artwork labels and romanized rig names. Learn a label
        // only from this skeleton's named bones, and only when the association is unique.
        foreach (var slot in slots)
        {
            var prefix = AliasPrefix(HomeCatalog.Text(slot["name"]));
            var owner = BoneOwner(HomeCatalog.Text(slot["bone"]));
            if (owner is null || !prefix.Any(c => c > 127)) continue;
            if (!aliases.TryGetValue(prefix, out var owners)) aliases.Add(prefix, owners = new(StringComparer.OrdinalIgnoreCase));
            owners.Add(owner);
        }

        foreach (var slot in slots)
        {
            var name = HomeCatalog.Text(slot["name"]);
            var attachments = Attachments(skeleton, name).Where(a => IsDrawable(a.Value)).ToArray();
            if (attachments.Length == 0) continue; // Paths, masks and reference slots emit no artwork.
            var names = Names(name);
            var boneName = HomeCatalog.Text(slot["bone"]);
            var boneOwner = BoneOwner(boneName);
            string? owner = null;
            if (names.Count == 1) owner = names.Single();
            else if (names.Count > 1)
            {
                if (boneOwner is not null && names.Contains(boneOwner)) owner = boneOwner;
            }
            else
            {
                var attachmentOwners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var attachment in attachments)
                {
                    attachmentOwners.UnionWith(Names(attachment.Key));
                    attachmentOwners.UnionWith(Names(HomeCatalog.Text(attachment.Value["path"])));
                }
                if (attachmentOwners.Count == 1) owner = attachmentOwners.Single();
                else if (attachmentOwners.Count == 0)
                {
                    owner = boneOwner;
                    if (owner is null && aliases.TryGetValue(AliasPrefix(name), out var aliasOwners) && aliasOwners.Count == 1)
                        owner = aliasOwners.Single();
                    if (owner is null)
                    {
                        var weightedOwners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var attachment in attachments)
                            foreach (var boneIndex in WeightedBones(attachment.Value))
                                if (boneIndex >= 0 && boneIndex < boneList.Length && BoneOwner(HomeCatalog.Text(boneList[boneIndex]["name"])) is { } weightedOwner)
                                    weightedOwners.Add(weightedOwner);
                        if (weightedOwners.Count == 1) owner = weightedOwners.Single();
                    }
                }
            }

            if (owner is not null) included[owner].Add(name);
            else if (names.Count == 0 && IsRootScenery(slot, bones, attachments)) shared.Add(name);
            else unresolved.Add(name);
        }

        var reason = unresolved.Count == 0 ? null : $"Cannot safely isolate characters in shared home part '{partPath}': "
            + $"unresolved artwork slots {string.Join(", ", unresolved.Order(StringComparer.Ordinal))}. Export the full scene instead.";
        return new HomeSlotMap(included.ToDictionary(pair => pair.Key, pair => (IReadOnlySet<string>)pair.Value, StringComparer.OrdinalIgnoreCase),
            shared.Order(StringComparer.Ordinal).ToArray(), reason);
    }

    private static string AliasPrefix(string name)
    {
        var boundary = 0;
        while (boundary < name.Length && char.IsLetterOrDigit(name[boundary])) boundary++;
        return name[..boundary];
    }

    private static bool IsDrawable(JsonObject attachment) => HomeCatalog.Text(attachment["type"], "region")
        is not ("clipping" or "path" or "point" or "boundingbox");

    private static IEnumerable<KeyValuePair<string, JsonObject>> Attachments(JsonObject skeleton, string slot)
    {
        var skins = skeleton["skins"];
        if (skins is JsonArray array)
        {
            foreach (var skin in array.OfType<JsonObject>())
                if (skin["attachments"]?[slot] is JsonObject attachments)
                    foreach (var item in attachments)
                        if (item.Value is JsonObject attachment) yield return new(item.Key, attachment);
        }
        else if (skins is JsonObject legacy)
        {
            foreach (var skin in legacy.Select(pair => pair.Value).OfType<JsonObject>())
                if (skin[slot] is JsonObject attachments)
                    foreach (var item in attachments)
                        if (item.Value is JsonObject attachment) yield return new(item.Key, attachment);
        }
    }

    private static IEnumerable<int> WeightedBones(JsonObject attachment)
    {
        if (attachment["vertices"] is not JsonArray vertices || attachment["uvs"] is not JsonArray uvs || vertices.Count == uvs.Count) yield break;
        // Spine JSON weighted vertices: influence count, then [bone, x, y, weight] per influence.
        var offset = 0;
        while (offset < vertices.Count)
        {
            var count = HomeCatalog.Number(vertices[offset++]);
            if (count is null or < 0 || count > (vertices.Count - offset) / 4) yield break;
            for (var influence = 0; influence < count.Value; influence++)
            {
                if (HomeCatalog.Number(vertices[offset]) is { } index && index >= 0 && index <= int.MaxValue) yield return (int)index;
                offset += 4;
            }
        }
    }

    private static bool IsRootScenery(JsonObject slot, Dictionary<string, JsonObject> bones,
        KeyValuePair<string, JsonObject>[] attachments)
    {
        // These published shared skeletons also carry scene furniture. Require both an
        // explicit scenery label and a root attachment; an unnamed body part is unsupported.
        if (!bones.TryGetValue(HomeCatalog.Text(slot["bone"]), out var bone) || bone["parent"] is not null) return false;
        var tokens = HomeCatalog.Text(slot["name"]).Split(['_', '-', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (!tokens.Any(token => token.Equals("bg", StringComparison.OrdinalIgnoreCase)
            || token.Equals("background", StringComparison.OrdinalIgnoreCase)
            || token.Equals("table", StringComparison.OrdinalIgnoreCase)
            || token.Equals("bottle", StringComparison.OrdinalIgnoreCase))) return false;
        return attachments.All(attachment => !WeightedBones(attachment.Value).Any());
    }
}
