using System.Numerics;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

/// <summary>Loads each archived skeleton once, including atlas bytes shared by separate characters.</summary>
public sealed class HomeResources(HomeScene scene)
{
    readonly Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
    readonly Dictionary<string, SpineAsset> skeletons = new(StringComparer.Ordinal);
    public HomeScene Scene { get; } = scene;
    public byte[] Read(string path)
    {
        if (!files.TryGetValue(path, out var bytes)) files.Add(path, bytes = Scene.Read(path));
        return bytes;
    }
    public SpineAsset Skeleton(string name)
    {
        if (skeletons.TryGetValue(name, out var asset)) return asset;
        var record = Scene.Spot["skeletons"]?.AsArray().FirstOrDefault(s => Archive.S(s!, "name") == name)
            ?? throw new InvalidDataException($"Home skeleton definition is missing: {name}");
        asset = SpineAsset.Load(record, path => Read(Scene.SpineDirectory + "/" + path));
        skeletons.Add(name, asset);
        return asset;
    }
}

/// <summary>Mutable animation state, independent of the graphics context and encoder.</summary>
public sealed class HomeAnimation
{
    public sealed record Part(string Path, SpineActor Actor, float[] World, int SortingOrder, int Queue, bool DepthWrite, bool? StraightAlphaInput);
    public HomeResources Resources { get; }
    public HomeScene Scene => Resources.Scene;
    public HomeCharacter? Character { get; }
    public HomeCamera Camera { get; }
    public IReadOnlyList<Part> Parts { get; }
    public double Duration => Parts.Max(p => p.Actor.Duration);
    public bool AllLooping => Parts.All(p => p.Actor.Loop);

    public HomeAnimation(HomeResources resources, HomeCharacter? character = null, string? animation = null, bool? loop = null)
    {
        Resources = resources; Character = character; Camera = new(Scene.Home, Scene.Spot);
        var extras = (Scene.Home["spineCharacters"] as JsonArray ?? []).OfType<JsonObject>()
            .ToDictionary(p => Archive.S(p, "path"), StringComparer.Ordinal);
        var materials = Scene.Home["spineMaterials"] as JsonObject ?? new JsonObject();
        var result = new List<Part>();
        foreach (var part in character?.Parts ?? Scene.SpineParts)
        {
            string path = Archive.S(part, "path");
            var includedSlots = character is null ? null : Scene.IncludedSlots(character, part);
            if (includedSlots is { Count: 0 }) continue;
            var asset = resources.Skeleton(Archive.S(part, "skeletonData"));
            var actor = new SpineActor(asset, part["animation"]!, animation, loop, includedSlots);
            extras.TryGetValue(path, out var extra);
            var pageMaterials = new List<JsonNode>();
            foreach (var texture in asset.Textures)
            {
                string? named = extra?["pageMaterials"]?[texture.File]?.ToString();
                var material = named is not null ? materials[named] : materials.Count == 1 ? materials.First().Value
                    : materials[Path.GetFileNameWithoutExtension(texture.File) + "_Material"];
                if (material is not null) pageMaterials.Add(material);
            }
            var modes = pageMaterials.Select(m => (Straight: m["floats"]?["_StraightAlphaInput"] is { } straight ? (bool?)(Actor.N(straight) != 0) : null,
                Depth: Actor.N(m["floats"]?["_ZWrite"]) != 0, Queue: (int)Actor.N(m["renderQueue"], -1))).Distinct().ToArray();
            if (modes.Length > 1) throw new NotSupportedException($"Spine pages in {path} use different render states.");
            foreach (var material in pageMaterials)
            {
                string shader = material["shader"] is JsonObject obj ? Archive.S(obj, "shader") : Archive.S(material, "shader");
                if (shader.Length > 0 && shader != "Universal Render Pipeline/Spine/Skeleton")
                    throw new NotSupportedException($"Home Spine material shader: {shader}");
            }
            var mode = modes.FirstOrDefault();
            result.Add(new(path, actor, AdvMatrix.Multiply(Camera.Root, HomeCamera.Matrix(part["world"])),
                (int)Actor.N(extra?["sortingOrder"] ?? part["sortingOrder"]), mode.Queue < 0 || modes.Length == 0 ? 3000 : mode.Queue, mode.Depth, mode.Straight));
        }
        if (result.Count == 0) throw new InvalidDataException("The selected home animation contains no renderable skeletons.");
        Parts = result.AsReadOnly();
    }

    public void Advance(int frame, int fps) { foreach (var part in Parts) part.Actor.Advance(frame, fps); }
    public void Reset() { foreach (var part in Parts) part.Actor.Reset(); }
    public JsonObject Describe() => new()
    {
        ["site"] = Scene.Site, ["spotId"] = Scene.SpotId, ["advId"] = Scene.AdvId, ["character"] = Character?.Key,
        ["sourceDuration"] = Duration, ["runtime"] = "Spine " + SpineAsset.RuntimeVersion,
        ["parts"] = new JsonArray(Parts.Select(p => (JsonNode?)new JsonObject
        {
            ["path"] = p.Path, ["skeleton"] = p.Actor.Asset.Name, ["version"] = p.Actor.Asset.Version,
            ["animation"] = p.Actor.AnimationName, ["loop"] = p.Actor.Loop, ["timeScale"] = p.Actor.TimeScale,
            ["duration"] = p.Actor.Duration, ["sourceFps"] = p.Actor.Asset.FramesPerSecond,
            ["animations"] = new JsonArray(p.Actor.Asset.Animations.Select(a => (JsonNode?)new JsonObject { ["name"] = a.Name, ["duration"] = a.Duration }).ToArray()),
        }).ToArray()),
    };

    /// <summary>One fixed crop for the entire requested interval; geometry is sampled at every output frame.</summary>
    public float[] Fit(float aspect, int firstFrame, int frameCount, int fps, float margin = .06f)
    {
        if (!float.IsFinite(margin) || margin < 0 || margin >= .5f) throw new ArgumentOutOfRangeException(nameof(margin));
        var projection = Camera.ViewProjection(aspect);
        var matrices = Parts.Select(p => AdvMatrix.Multiply(projection, p.World)).ToArray();
        var min = new Vector2(float.PositiveInfinity); var max = new Vector2(float.NegativeInfinity);
        bool visible = false;
        try
        {
            for (int f = firstFrame; f < checked(firstFrame + frameCount); f++)
            {
                Advance(f, fps);
                for (int i = 0; i < Parts.Count; i++)
                    foreach (var batch in Parts[i].Actor.Mesh.Batches)
                        for (int v = 0; v < batch.Positions.Length; v += 3)
                        {
                            var p = AdvMatrix.Transform(matrices[i], new(batch.Positions[v], batch.Positions[v + 1], batch.Positions[v + 2], 1));
                            if (p.W <= 0) throw new NotSupportedException("Cannot fit home geometry crossing behind the scene camera; use --framing scene.");
                            var point = new Vector2(p.X / p.W, p.Y / p.W);
                            min = Vector2.Min(min, point); max = Vector2.Max(max, point); visible = true;
                        }
            }
        }
        finally { Reset(); }
        if (!visible) throw new InvalidDataException("No visible Spine geometry in the requested animation interval.");
        var size = max - min; var center = (min + max) / 2;
        float zoom = Math.Min(2 * (1 - margin) / Math.Max(size.X, 1e-6f), 2 * (1 - margin) / Math.Max(size.Y, 1e-6f));
        var crop = AdvMatrix.Identity(); crop[0] = crop[5] = zoom; crop[12] = -center.X * zoom; crop[13] = -center.Y * zoom;
        return crop;
    }
}

public readonly record struct HomeTiming(int FirstFrame, int FrameCount, int Fps)
{
    public int LastFrame => checked(FirstFrame + FrameCount - 1);
    public double Duration => FrameCount / (double)Fps;
    public static HomeTiming Plan(double sourceDuration, double time = 0, double? duration = null, int fps = 30, bool loop = false)
    {
        if (fps is < 1 or > 120) throw new ArgumentException("Home fps must be 1..120.");
        if (!double.IsFinite(sourceDuration) || sourceDuration < 0 || sourceDuration > 36000 || !double.IsFinite(time) || time < 0 || time > 36000
            || duration is { } d && (!double.IsFinite(d) || d <= 0 || time + d > 36000)) throw new ArgumentException("Invalid home time/duration; maximum ten hours.");
        int first = (int)Math.Floor(time * fps + 1e-8);
        // An untrimmed non-looping clip includes the endpoint, even when the source has a half-frame key.
        int count = duration is { } seconds ? (int)Math.Ceiling(seconds * fps - 1e-8)
            : Math.Max(1, (int)Math.Ceiling(Math.Max(0, sourceDuration - first / (double)fps) * fps - 1e-7) + (loop ? 0 : 1));
        return new(first, Math.Max(1, count), fps);
    }
}
