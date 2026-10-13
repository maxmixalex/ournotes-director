using System.Numerics;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

/// <summary>A documented reconstruction of a missing serialized component reference, never an original prefab matrix.</summary>
public sealed record HomeSpineRecovery(string Path, string Skeleton, string ReferencePath, int SharedBones, int Samples, double MaximumBoneErrorPixels)
{
    public JsonObject ToJson() => new()
    {
        ["kind"] = "boneAlignedBackLayer", ["path"] = Path, ["skeleton"] = Skeleton, ["referencePath"] = ReferencePath,
        ["sourceMatrixAvailable"] = false, ["originalSceneProjectionExact"] = false,
        ["sharedBones"] = SharedBones, ["samples"] = Samples, ["maximumBoneErrorPixels"] = MaximumBoneErrorPixels,
        ["description"] = "原始主页记录缺少背层组件引用；已逐帧验证共有骨骼后在角色坐标中补全后层。该对齐结果不代表原 prefab 的独立变换。",
    };
}

internal static class HomeSpineReconstruction
{
    internal sealed record Result(IReadOnlyList<JsonObject> Parts, IReadOnlyList<HomeSpineRecovery> Recoveries, IReadOnlyList<JsonObject> Unresolved);

    internal static Result Resolve(JsonObject spot, JsonObject home, Func<string, JsonObject> readSkeleton, Func<JsonObject, SpineAsset> load)
    {
        var declarations = (spot["spineCharacters"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        var records = (spot["skeletons"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        var parts = declarations.Where(Renderable).ToList();
        var recovered = new List<HomeSpineRecovery>(); var unresolved = new List<JsonObject>();
        var groups = HomeScene.GroupCharacters(spot, declarations);
        var parsed = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var assets = new Dictionary<string, SpineAsset>(StringComparer.Ordinal);
        JsonObject Skeleton(JsonObject record)
        {
            string file = Archive.S(record, "skeleton");
            if (!parsed.TryGetValue(file, out var json)) parsed[file] = json = readSkeleton(file);
            return json;
        }
        SpineAsset Asset(JsonObject record)
        {
            string name = Archive.S(record, "name");
            if (!assets.TryGetValue(name, out var value)) assets[name] = value = load(record);
            return value;
        }
        foreach (var missing in declarations.Where(p => !Renderable(p)))
        {
            string path = Archive.S(missing, "path"), leaf = path[(path.LastIndexOf('/') + 1)..];
            // A missing controller reference is not proof that arbitrary dependency assets are visible.
            // Only the explicit same-character rear component naming contract may be reconstructed.
            if (!leaf.EndsWith("_back", StringComparison.OrdinalIgnoreCase)) continue;
            var candidates = records.Where(record => Archive.S(record, "skeleton").EndsWith("_" + leaf + ".json", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (candidates.Length == 0) continue;
            if (missing["skeletonData"] is not null || missing["animation"] is not null || missing["world"] is not null)
            {
                unresolved.Add(new JsonObject { ["path"] = path, ["reason"] = "源组件仍有部分独立设置，不能用另一组件的设置覆盖缺失字段。" });
                continue;
            }
            string failure = "背层没有唯一、可验证的同角色参考骨架。";
            var owners = groups.Where(g => g.Parts.Contains(missing)).ToArray();
            if (candidates.Length == 1 && owners.Length == 1)
            {
                var candidate = candidates[0]; var references = new List<(JsonObject Part, JsonObject Record, int Bones)>();
                foreach (var donor in owners[0].Parts.Where(Renderable))
                {
                    var reference = records.SingleOrDefault(record => Archive.S(record, "name") == Archive.S(donor, "skeletonData"));
                    if (reference is null || !Archive.S(reference, "skeleton").EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                    if (Compatible(candidate, reference, Skeleton(candidate), Skeleton(reference), donor["animation"]!, out int bones))
                        references.Add((donor, reference, bones));
                }
                if (references.Count == 1)
                {
                    var reference = references[0];
                    var verification = VerifyPoses(Asset(candidate), Asset(reference.Record), reference.Part["animation"]!);
                    if (verification is { } verified)
                    {
                        var replacement = (JsonObject)missing.DeepClone();
                        var extra = (home["spineCharacters"] as JsonArray ?? []).OfType<JsonObject>().SingleOrDefault(p => Archive.S(p, "path") == Archive.S(reference.Part, "path"));
                        int order = checked((int)Actor.N(extra?["sortingOrder"] ?? reference.Part["sortingOrder"]) - 1);
                        var report = new HomeSpineRecovery(path, Archive.S(candidate, "name"), Archive.S(reference.Part, "path"), reference.Bones, verified.Samples, verified.Error);
                        replacement["skeletonData"] = candidate["name"]!.DeepClone();
                        replacement["animation"] = reference.Part["animation"]!.DeepClone();
                        replacement["world"] = reference.Part["world"]!.DeepClone();
                        replacement["sortingOrder"] = order;
                        replacement["pageMaterials"] = extra?["pageMaterials"]?.DeepClone();
                        replacement["recovery"] = report.ToJson();
                        parts.Add(replacement); recovered.Add(report); continue;
                    }
                    failure = "背层与参考骨架的实际动画骨骼不一致，不能按共有骨骼自动补全。";
                }
            }
            unresolved.Add(new JsonObject { ["path"] = path, ["reason"] = failure,
                ["skeletonCandidates"] = new JsonArray(candidates.Select(p => p["name"]?.DeepClone()).ToArray()) });
        }
        // Retain the original component sequence; it is the final stable tie breaker after renderer ordering.
        var byPath = parts.ToDictionary(p => Archive.S(p, "path"), StringComparer.Ordinal);
        return new(declarations.Select(p => byPath.GetValueOrDefault(Archive.S(p, "path"))).OfType<JsonObject>().ToArray(), recovered, unresolved);
    }

    internal static bool Renderable(JsonObject part) => part["skeletonData"] is not null && part["animation"] is JsonObject && part["world"] is JsonArray;

    static bool Compatible(JsonObject candidateRecord, JsonObject referenceRecord, JsonObject candidate, JsonObject reference, JsonNode animation, out int bones)
    {
        bones = 0;
        if (!JsonNode.DeepEquals(candidateRecord["atlases"], referenceRecord["atlases"]) || Actor.N(candidateRecord["scale"], 1) != Actor.N(referenceRecord["scale"], 1)
            || candidate["skeleton"]?["spine"]?.ToString() != reference["skeleton"]?["spine"]?.ToString()) return false;
        var candidateBones = Named(candidate["bones"]); var referenceBones = Named(reference["bones"]);
        if (candidateBones.Count < 2 || !candidateBones.All(b => referenceBones.TryGetValue(b.Key, out var other) && JsonNode.DeepEquals(b.Value, other))) return false;
        bones = candidateBones.Count;
        var candidateSlots = Named(candidate["slots"]); var referenceSlots = Named(reference["slots"]);
        if (candidateSlots.Count == 0 || !candidateSlots.All(s => referenceSlots.TryGetValue(s.Key, out var other) && s.Value["bone"]?.ToString() == other["bone"]?.ToString())) return false;
        // The rear file must supply at least one attachment that the front file intentionally leaves empty.
        if (!candidateSlots.Any(s => s.Value["attachment"] is not null && referenceSlots[s.Key]["attachment"] is null)) return false;
        var candidateOrders = new Dictionary<string, int>(StringComparer.Ordinal); var referenceOrders = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string kind in new[] { "ik", "transform", "path", "physics" })
        {
            var a = Named(candidate[kind]); var b = Named(reference[kind]);
            foreach (var pair in a)
            {
                if (!b.TryGetValue(pair.Key, out var other)) return false;
                var left = (JsonObject)pair.Value.DeepClone(); var right = (JsonObject)other.DeepClone(); left.Remove("order"); right.Remove("order");
                if (!JsonNode.DeepEquals(left, right)) return false;
                string name = kind + ":" + pair.Key;
                candidateOrders[name] = (int)Actor.N(pair.Value["order"]); referenceOrders[name] = (int)Actor.N(other["order"]);
            }
        }
        if (!candidateOrders.OrderBy(p => p.Value).Select(p => p.Key).SequenceEqual(referenceOrders.OrderBy(p => p.Value).Select(p => p.Key))) return false;
        if (candidate["animations"] is not JsonObject animationSet || reference["animations"] is not JsonObject referenceSet
            || !animationSet.ContainsKey(Archive.S(animation, "_animationName"))) return false;
        foreach (var entry in animationSet)
        {
            if (entry.Value is not JsonObject value || referenceSet[entry.Key] is not JsonObject original) return false;
            foreach (string kind in new[] { "bones", "ik", "transform", "path", "physics" })
                foreach (var timeline in value[kind] as JsonObject ?? [])
                    if (!JsonNode.DeepEquals(timeline.Value, original[kind]?[timeline.Key])) return false;
        }
        return true;
    }

    static Dictionary<string, JsonObject> Named(JsonNode? node) => (node as JsonArray ?? []).OfType<JsonObject>().ToDictionary(n => Archive.S(n, "name"), StringComparer.Ordinal);

    static (int Samples, double Error)? VerifyPoses(SpineAsset candidate, SpineAsset reference, JsonNode settings)
    {
        int samples = 0; double maximumError = 0;
        // Validate the source animation itself even when a component happens to be paused
        // or accelerated. Editor cues must never reveal an unverified intermediate pose.
        var verificationSettings = settings.DeepClone(); verificationSettings["timeScale"] = 1;
        foreach (var animation in candidate.Animations)
        {
            var counterpart = reference.Animations.SingleOrDefault(a => a.Name == animation.Name);
            if (counterpart is null || Math.Abs(animation.Duration - counterpart.Duration) > 1e-5) return null;
            var back = new SpineActor(candidate, verificationSettings, animation.Name, false); var front = new SpineActor(reference, verificationSettings, animation.Name, false);
            var timing = HomeTiming.Plan(back.Duration, fps: 60);
            if (timing.FrameCount > 7201) return null; // Keep archive listing bounded for unusual external files.
            for (int frame = 0; frame < timing.FrameCount; frame++)
            {
                back.Advance(frame, 60); front.Advance(frame, 60); samples++;
                foreach (var bone in back.RuntimeSkeleton.Bones)
                {
                    var other = front.RuntimeSkeleton.FindBone(bone.Data.Name);
                    if (other is null) return null;
                    double error = Vector2.Distance(new(bone.WorldX, bone.WorldY), new(other.WorldX, other.WorldY)) / candidate.SourceScale;
                    maximumError = Math.Max(maximumError, error);
                    if (error > .05 || Math.Abs(bone.A - other.A) > .0001 || Math.Abs(bone.B - other.B) > .0001 || Math.Abs(bone.C - other.C) > .0001 || Math.Abs(bone.D - other.D) > .0001) return null;
                }
            }
        }
        return (samples, maximumError);
    }
}
