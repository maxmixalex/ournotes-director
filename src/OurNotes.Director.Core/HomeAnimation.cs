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
public sealed partial class HomeAnimation
{
    public sealed class Part(string path, SpineActor actor, float[] world, int sortingOrder, int queue, bool depthWrite, bool? straightAlphaInput)
    {
        public string Path { get; } = path;
        public SpineActor Actor { get; internal set; } = actor;
        public float[] World { get; } = world;
        public int SortingOrder { get; } = sortingOrder;
        public int Queue { get; } = queue;
        public bool DepthWrite { get; } = depthWrite;
        public bool? StraightAlphaInput { get; } = straightAlphaInput;
        public string? CharacterKey { get; internal init; }
        public int SourceOrder { get; internal init; }
        internal JsonNode SourceAnimation { get; init; } = new JsonObject();
        internal IReadOnlySet<string>? IncludedSlots { get; init; }
        internal HomeAnimationCue? CurrentCue { get; set; }
    }
    readonly IReadOnlyList<Part> sourceParts;
    IReadOnlyList<Part>? editableParts;
    readonly string? animationOverride;
    readonly bool? loopOverride;
    HomeEditState? editState;
    readonly Dictionary<float, IReadOnlyDictionary<string, Vector2>> pivots = [];
    public HomeResources Resources { get; }
    public HomeScene Scene => Resources.Scene;
    public HomeCharacter? Character { get; }
    public HomeCamera Camera { get; }
    public IReadOnlyList<Part> Parts => editState is null ? sourceParts : editableParts!;
    public IReadOnlyList<Part> SourceParts => sourceParts;
    public double Duration => sourceParts.Max(p => p.Actor.Duration);
    public bool AllLooping => sourceParts.All(p => p.Actor.Loop);

    public HomeAnimation(HomeResources resources, HomeCharacter? character = null, string? animation = null, bool? loop = null)
    {
        Resources = resources; Character = character; Camera = new(Scene.Home, Scene.Spot);
        animationOverride = animation; loopOverride = loop;
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
                string? named = (extra?["pageMaterials"]?[texture.File] ?? part["pageMaterials"]?[texture.File])?.ToString();
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
                (int)Actor.N(extra?["sortingOrder"] ?? part["sortingOrder"]), mode.Queue < 0 || modes.Length == 0 ? 3000 : mode.Queue, mode.Depth, mode.Straight)
            { SourceOrder = result.Count, CharacterKey = character?.Key, IncludedSlots = includedSlots, SourceAnimation = part["animation"]!.DeepClone() });
        }
        if (result.Count == 0) throw new InvalidDataException("The selected home animation contains no renderable skeletons.");
        sourceParts = result.AsReadOnly();
    }

    public void SetEditState(HomeEditState? state)
    {
        if (state is not null)
        {
            HomeProject.ValidateEdits(state);
            foreach (var entry in state.Characters)
            {
                if (!Scene.Characters.Any(c => c.Key == entry.Key)) throw new ArgumentException("主页项目包含不存在的角色：" + entry.Key);
                var character = Scene.Characters.Single(c => c.Key == entry.Key);
                foreach (var cue in entry.Value.Animations.Where(c => c.Name is not null))
                    foreach (var part in character.Parts)
                    {
                        if (Scene.IncludedSlots(character, part) is { Count: 0 }) continue;
                        var asset = Resources.Skeleton(Archive.S(part, "skeletonData"));
                        if (!asset.Animations.Any(a => a.Name == cue.Name))
                            throw new ArgumentException($"角色 {entry.Key} 的 {asset.Name} 没有动作 {cue.Name}。");
                    }
            }
            EnsureEditableParts();
        }
        editState = state;
    }

    void EnsureEditableParts()
    {
        if (editableParts is not null) return;
        var result = new List<Part>();
        foreach (var original in sourceParts)
        {
            var source = Scene.SpineParts.Single(p => Archive.S(p, "path") == original.Path);
            var owners = Character is not null ? new[] { Character } : Scene.Characters.Where(c => c.Parts.Contains(source)).ToArray();
            var covered = new HashSet<string>(StringComparer.Ordinal);
            foreach (var owner in owners)
            {
                var slots = Scene.IncludedSlots(owner, source);
                if (slots is { Count: 0 }) continue;
                var names = slots ?? original.Actor.Asset.Slots.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
                if (covered.Overlaps(names)) throw new InvalidDataException("角色的 Spine 槽位重叠：" + original.Path);
                covered.UnionWith(names);
                result.Add(CopyPart(original, owner.Key, slots));
            }
            if (Character is null)
            {
                var remaining = original.Actor.Asset.Slots.Select(s => s.Name).Where(name => !covered.Contains(name)).ToHashSet(StringComparer.Ordinal);
                if (remaining.Count > 0) result.Add(CopyPart(original, null, covered.Count == 0 ? null : remaining));
            }
        }
        editableParts = result.AsReadOnly();
    }
    Part CopyPart(Part source, string? character, IReadOnlySet<string>? includedSlots)
        => new(source.Path, new SpineActor(source.Actor.Asset, source.SourceAnimation, animationOverride, loopOverride, includedSlots), source.World,
            source.SortingOrder, source.Queue, source.DepthWrite, source.StraightAlphaInput)
        { CharacterKey = character, SourceOrder = source.SourceOrder, SourceAnimation = source.SourceAnimation, IncludedSlots = includedSlots };

    public void Advance(int frame, int fps)
    {
        foreach (var part in sourceParts) part.Actor.Advance(frame, fps);
        if (editState is null) return;
        double time = frame / (double)fps;
        foreach (var part in editableParts!)
        {
            var cue = part.CharacterKey is not null && editState.Characters.TryGetValue(part.CharacterKey, out var track) ? track.AnimationAt(time) : null;
            if (part.CurrentCue != cue)
            {
                part.Actor = new(part.Actor.Asset, part.SourceAnimation, cue?.Name ?? animationOverride, cue?.Loop ?? loopOverride, part.IncludedSlots);
                part.CurrentCue = cue;
            }
            double local = cue is null ? time : cue.SourceTime + Math.Max(0, time - cue.Time) * cue.Speed;
            part.Actor.Advance(checked((int)Math.Floor(local * fps + 1e-8)), fps);
        }
    }
    public void Reset()
    {
        foreach (var part in sourceParts) part.Actor.Reset();
        foreach (var part in editableParts ?? []) part.Actor.Reset();
    }
    public HomeTransform TransformFor(Part part, double time) => part.CharacterKey is not null && editState?.Characters.TryGetValue(part.CharacterKey, out var track) == true ? track.At(time) : new();

    /// <summary>The pivot is the original end-pose center and remains fixed while scrubbing or editing.</summary>
    public IReadOnlyDictionary<string, Vector2> CharacterPivots(float aspect)
    {
        if (pivots.TryGetValue(aspect, out var cached)) return cached;
        EnsureEditableParts();
        var projection = Camera.ViewProjection(aspect);
        var bounds = new Dictionary<string, (Vector2 Min, Vector2 Max)>(StringComparer.Ordinal);
        foreach (var part in editableParts!)
        {
            if (part.CharacterKey is null) continue;
            // Use an independent source actor so inspecting a pivot never changes a running clip.
            var actor = new SpineActor(part.Actor.Asset, part.SourceAnimation, animationOverride, loopOverride, part.IncludedSlots);
            actor.ApplyEndState();
            var matrix = AdvMatrix.Multiply(projection, part.World);
            foreach (var batch in actor.Mesh.Batches)
                for (int i = 0; i < batch.Positions.Length; i += 3)
                {
                    var p = AdvMatrix.Transform(matrix, new(batch.Positions[i], batch.Positions[i + 1], batch.Positions[i + 2], 1));
                    if (p.W <= 0) continue;
                    var point = new Vector2(p.X / p.W, p.Y / p.W);
                    var b = bounds.GetValueOrDefault(part.CharacterKey, (new(float.PositiveInfinity), new(float.NegativeInfinity)));
                    bounds[part.CharacterKey] = (Vector2.Min(b.Item1, point), Vector2.Max(b.Item2, point));
                }
        }
        var result = bounds.ToDictionary(p => p.Key, p => (p.Value.Min + p.Value.Max) / 2, StringComparer.Ordinal);
        pivots[aspect] = result; return result;
    }

    public float[] EditedMatrix(Part part, float[] projection, float aspect, double time)
    {
        var matrix = AdvMatrix.Multiply(projection, part.World);
        var transform = TransformFor(part, time);
        if (part.CharacterKey is null || transform.X == 0 && transform.Y == 0 && transform.Scale == 1) return matrix;
        var pivot = CharacterPivots(aspect).GetValueOrDefault(part.CharacterKey);
        // Projection includes a possible fixed fit crop. Convert the source pivot through that crop.
        var sourceProjection = Camera.ViewProjection(aspect);
        var crop = AdvMatrix.Multiply(projection, HomeCamera.Inverse(sourceProjection));
        var p = AdvMatrix.Transform(crop, new(pivot.X, pivot.Y, 0, 1));
        var screen = AdvMatrix.Identity(); screen[0] = screen[5] = (float)transform.Scale;
        screen[12] = (float)(2 * transform.X + (1 - transform.Scale) * p.X);
        screen[13] = (float)(-2 * transform.Y + (1 - transform.Scale) * p.Y);
        return AdvMatrix.Multiply(screen, matrix);
    }
    public JsonObject Describe() => new()
    {
        ["site"] = Scene.Site, ["spotId"] = Scene.SpotId, ["advId"] = Scene.AdvId, ["character"] = Character?.Key,
        ["sourceDuration"] = Duration, ["runtime"] = "Spine " + SpineAsset.RuntimeVersion,
        ["spineRecoveries"] = new JsonArray(Scene.SpineRecoveries.Where(r => Parts.Any(p => p.Path == r.Path)).Select(r => (JsonNode?)r.ToJson()).ToArray()),
        ["unresolvedSpineParts"] = new JsonArray(Scene.UnresolvedSpineParts.Select(p => (JsonNode?)p.DeepClone()).ToArray()),
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
        return MeasureBounds(aspect, firstFrame, frameCount, fps).Crop(margin);
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
