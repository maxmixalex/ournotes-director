using System.Globalization;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

namespace OurNotes.Director.Desktop.Core;

public sealed partial class StudioSceneDocument
{
    OriginalRow[]? sampleRows;
    TimelineKey[][]? sampleTracks;
    EvaluatedState? sampleBaseline;

    sealed class ActorState(string id, string model, int order)
    {
        public string Id = id, Model = model, Space = "game";
        public string? Motion, Expression;
        public int MotionFrame;
        public JsonObject Values = new()
        {
            ["position"] = new JsonArray(.5, .5, 0), ["rotation"] = new JsonArray(0, 0, 0),
            ["scale"] = new JsonArray(1, 1, 1), ["height"] = .95, ["opacity"] = 1,
            ["brightness"] = 1, ["tint"] = new JsonArray(1, 1, 1, 1), ["speed"] = 1,
            ["visible"] = false, ["order"] = order
        };
    }
    sealed class EvaluatedState
    {
        public readonly Dictionary<string, ActorState> Actors = new(StringComparer.Ordinal);
        public readonly JsonObject Camera = new() { ["framing"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["zoom"] = 1, ["roll"] = 0 } };
        public readonly JsonObject Scene = new() { ["background"] = new JsonArray(0, 0, 0, 0),
            ["grade"] = new JsonObject { ["exposure"] = 0, ["contrast"] = 1, ["saturation"] = 1, ["vignette"] = 0 } };
        public readonly Dictionary<(string Target, string Property), SampleCurve> Curves = new();
    }
    sealed record SampleCurve(float From, float To, int FirstFrame, float Duration, AdvEase Ease);

    /// <summary>
    /// Samples authored coordinates, key values and event names. It does not evaluate model assets,
    /// bones, physics or source-game placement; the native preview supplies those pixels and bounds.
    /// Parsing and track grouping are cached until the document changes, and sampling is reversible.
    /// </summary>
    public StudioSceneSample Sample(double time)
    {
        int at = Math.Clamp(ToFrame(time), 0, durationFrames);
        if (cachedSample is { } cached && cached.Time == at / (double)FramesPerSecond) return CopySample(cached);
        var state = EvaluateOriginal(at);
        sampleBaseline ??= EvaluateOriginal(0, includeEvents: false);
        sampleTracks ??= keys.GroupBy(key => (key.Target, key.Track)).Select(group => group.OrderBy(key => key.Frame).ToArray()).ToArray();
        foreach (var track in sampleTracks)
        {
            var first = track[0];
            if (first.Numeric)
            {
                var baseline = PropertyValue(sampleBaseline, first.Target, first.Property) ?? first.Value!;
                SetProperty(state, first.Target, first.Property, EvaluateTrack(track, baseline, at));
            }
            else
            {
                var key = track.LastOrDefault(key => key.Frame <= at);
                if (key is null) continue;
                if (key.IsEvent && state.Actors.TryGetValue(key.Target, out var actor))
                {
                    if (key.Track == "motion") { actor.Motion = Text(key.Value); actor.MotionFrame = key.Frame; }
                    else actor.Expression = Text(key.Value);
                }
                else SetProperty(state, key.Target, key.Property, key.Value!.DeepClone());
            }
        }
        var actors = state.Actors.Values.Select(actor => new StudioActor(actor.Id, actor.Id, actor.Model, actor.Space,
            new StudioTransform(Component(actor.Values["position"], 0, .5), Component(actor.Values["position"], 1, .5),
                Number(actor.Values["height"], .95), Component(actor.Values["rotation"], 2),
                Component(actor.Values["scale"], 0, 1), Component(actor.Values["scale"], 1, 1), Number(actor.Values["opacity"], 1),
                Component(actor.Values["position"], 2), Component(actor.Values["rotation"], 0), Component(actor.Values["rotation"], 1),
                Component(actor.Values["scale"], 2, 1)), Flag(actor.Values["visible"]), actor.Motion, actor.Expression,
            Math.Max(0, at - actor.MotionFrame) / (double)FramesPerSecond, (int)Number(actor.Values["order"]))).ToArray();
        var framing = state.Camera["framing"]!.AsObject(); var world = (JsonObject)state.Camera.DeepClone(); world.Remove("framing");
        var grade = state.Scene["grade"]!.AsObject();
        cachedSample = new(at / (double)FramesPerSecond, Array.AsReadOnly(actors),
            new(Number(framing["x"]), Number(framing["y"]), Number(framing["zoom"], 1), Number(framing["roll"])) { WorldValues = world },
            new(Number(grade["exposure"]), Number(grade["contrast"], 1), Number(grade["saturation"], 1), Number(grade["vignette"])),
            new JsonArray(state.Scene["background"]!.AsArray().Select(value => (JsonNode?)JsonValue.Create(Number(value))).ToArray()), timingKnown);
        return CopySample(cachedSample);
    }
    static StudioSceneSample CopySample(StudioSceneSample sample) => sample with
    {
        Camera = sample.Camera with { WorldValues = (JsonObject)sample.Camera.WorldValues.DeepClone() },
        Background = (JsonArray)sample.Background.DeepClone()
    };
    void InvalidateSamples() { cachedSample = null; sampleRows = null; sampleTracks = null; sampleBaseline = null; }
    IReadOnlyList<StudioActor> ActorDefinitions() => originals.Where(row => row.Command == "Character")
        .Select(row => new StudioActor(Text(row.Data["TargetName"]), Text(row.Data["TargetName"]), Text(row.Data["Model"]), "game", new(), false))
        .DistinctBy(actor => actor.Id, StringComparer.Ordinal).ToArray();

    EvaluatedState EvaluateOriginal(int at, bool includeEvents = true)
    {
        var state = new EvaluatedState();
        foreach (var definition in ActorDefinitions()) state.Actors.TryAdd(definition.Id, new(definition.Id, definition.Model, state.Actors.Count));
        sampleRows ??= originals.OrderBy(row => row.Section == "finalize" && timingKnown ? durationFrames : row.Frame).ThenBy(row => row.Order).ToArray();
        foreach (var row in sampleRows)
        {
            int start = row.Section == "finalize" && timingKnown ? durationFrames : row.Frame;
            if (start > at || !row.Known || row.Section == "commands" && Ignored(row.Data)) continue;
            Advance(state, Math.Max(0, start - (row.StartsAfterTweens ? 0 : 1)));
            ApplySampleRow(state, row.Command, row.Data, start, includeEvents, row.StartsAfterTweens);
        }
        Advance(state, at); return state;
    }
    static void ApplySampleRow(EvaluatedState state, string command, JsonObject row, int start, bool includeEvents, bool afterTweens)
    {
        string target = command switch { "Director.Camera" => CameraId, "Director.Scene" => SceneId, _ => Text(row["TargetName"]) };
        if (command == "Character") return; // Definitions are discovered without claiming that a model is loaded or visible.
        if (includeEvents && state.Actors.TryGetValue(target, out var eventActor))
        {
            if (command == "Motion") { eventActor.Motion = Text(row["MotionName"]); eventActor.MotionFrame = start; }
            else if (command == "Expression") eventActor.Expression = Text(row["ExpressionName"]);
        }
        if (command is not ("Director.Actor" or "Director.Camera" or "Director.Scene") || row["Values"] is not JsonObject values) return;
        if (command == "Director.Actor")
        {
            if (!state.Actors.TryGetValue(target, out var actor)) return;
            if (actor.Space == "game") { actor.Space = "screen"; actor.Values["visible"] = true; }
            string space = Text(row["Space"]);
            if (space is "world" or "screen") actor.Space = space;
        }
        float duration = (float)Number(row["Duration"]); string easeName = Text(row["Ease"], "Linear");
        if (!Enum.TryParse<AdvEase>(easeName, out var ease)) ease = AdvEase.Linear;
        foreach (var (path, value) in Leaves(values))
        {
            if (NumericNode(value) && IsTweenProperty(command, path))
            {
                foreach (var (property, scalar) in ScalarLeaves(path, value))
                {
                    string canonical = CanonicalProperty(property);
                    var address = (target, canonical);
                    float from = (float)Number(PropertyValue(state, target, canonical), Number(scalar));
                    state.Curves.Remove(address);
                    if (duration > 0) state.Curves[address] = new(from, (float)Number(scalar), start == 0 || afterTweens ? start + 1 : start, duration, ease);
                    else SetProperty(state, target, canonical, scalar.DeepClone());
                }
            }
            else SetProperty(state, target, path, value.DeepClone());
        }
    }
    static IEnumerable<(string Property, JsonNode Value)> ScalarLeaves(string property, JsonNode value)
    {
        if (value is JsonArray array)
        {
            for (int index = 0; index < array.Count; index++)
                if (array[index] is { } component) yield return (property + "." + index.ToString(CultureInfo.InvariantCulture), component);
        }
        else yield return (property, value);
    }
    static void Advance(EvaluatedState state, int at)
    {
        foreach (var ((target, property), curve) in state.Curves)
        {
            int steps = Math.Max(0, at - curve.FirstFrame + 1);
            float elapsed = Math.Min(FrameElapsed(steps), curve.Duration);
            float fraction = elapsed <= 0 ? 0 : AdvTweens.Evaluate(curve.Ease, elapsed, curve.Duration);
            SetProperty(state, target, property, JsonValue.Create(curve.From + (curve.To - curve.From) * fraction));
        }
    }
    // Native tweens accumulate single-precision frame deltas. Reuse that same clock for raw-row handles.
    static readonly List<float> elapsedFrames = [0];
    static float FrameElapsed(int frame)
    {
        lock (elapsedFrames)
        {
            while (elapsedFrames.Count <= frame) elapsedFrames.Add(elapsedFrames[^1] + 1f / FramesPerSecond);
            return elapsedFrames[frame];
        }
    }
    static JsonObject? ValuesFor(EvaluatedState state, string target) => target == CameraId ? state.Camera
        : target == SceneId ? state.Scene : state.Actors.TryGetValue(target, out var actor) ? actor.Values : null;
    static JsonNode? PropertyValue(EvaluatedState state, string target, string property)
    {
        JsonNode? current = ValuesFor(state, target);
        foreach (string field in CanonicalProperty(property).Split('.'))
        {
            if (current is JsonObject obj) current = obj[field];
            else if (current is JsonArray array && int.TryParse(field, out int index) && index >= 0 && index < array.Count) current = array[index];
            else return null;
        }
        // Screen positions have two exported components, even though the native state is a Vector3.
        if (property == "position" && target is not (CameraId or SceneId) && state.Actors.TryGetValue(target, out var actor)
            && actor.Space == "screen" && current is JsonArray position && position.Count == 3)
            return new JsonArray(position[0]?.DeepClone(), position[1]?.DeepClone());
        return current;
    }
    static void SetProperty(EvaluatedState state, string target, string property, JsonNode? value)
    {
        var root = ValuesFor(state, target); if (root is null) return;
        string canonical = CanonicalProperty(property); string[] fields = canonical.Split('.');
        if (fields.Length == 2 && int.TryParse(fields[1], out int index))
        {
            if (root[fields[0]] is not JsonArray array) root[fields[0]] = array = new JsonArray();
            while (array.Count <= index) array.Add(0);
            array[index] = value; return;
        }
        SetPath(root, canonical, value);
    }
    static double Component(JsonNode? node, int index, double fallback = 0)
        => node is JsonArray array && index < array.Count ? Number(array[index], fallback) : fallback;
    static bool IsTweenProperty(string command, string path) => command switch
    {
        "Director.Actor" => path is "position" or "rotation" or "scale" or "height" or "positionX" or "positionY" or "rotationZ" or "opacity" or "brightness" or "tint" or "speed",
        "Director.Camera" => path is "position" or "rotation" or "fieldOfView" || path.StartsWith("framing.", StringComparison.Ordinal),
        "Director.Scene" => path == "background" || path.StartsWith("grade.", StringComparison.Ordinal),
        _ => false
    };
}
