using System.Text.Json.Nodes;
using OurNotes.Director.Core;

namespace OurNotes.Director.Desktop.Core;

/// <summary>Atomic operations on actual source-audio ranges, not just timeline rectangles.</summary>
public static class CreativeClipEditing
{
    public sealed record Result(CreativeScene Scene, string Selection);
    static string S(JsonObject value, string key) => CreativeScene.Text(value, key);
    static double N(JsonObject value, string key, double fallback = 0) => CreativeScene.Number(value, key, fallback);
    static double Snap(double time)
    {
        if (!double.IsFinite(time) || time < 0 || time > 36000) throw new ArgumentOutOfRangeException(nameof(time));
        return Math.Round(time * 30) / 30;
    }
    public static bool CanTrim(JsonObject clip) => S(clip, "kind") == "hold" || clip["data"]?["AudioPath"] is not null;
    public static bool CanSplit(JsonObject clip, double time) => CanTrim(clip) && double.IsFinite(time)
        && time > N(clip, "start") + .5 / 30 && time < N(clip, "start") + N(clip, "duration") - .5 / 30;
    public static Result TrimStart(CreativeScene source, string id, double time)
    {
        var next = new CreativeScene(source.Root); var clip = next.Clip(id);
        if (!CanTrim(clip)) throw new InvalidOperationException("这个片段的开头由原演出决定；声音文件和停留片段可以直接裁剪。");
        double at = Snap(time), oldStart = N(clip, "start"), end = oldStart + N(clip, "duration");
        if (at >= end - .5 / 30) throw new ArgumentException("裁剪后至少保留一帧。");
        var data = clip["data"]!.AsObject();
        if (data["AudioPath"] is not null)
        {
            double input = N(data, "AudioIn") + at - oldStart;
            if (input < -1e-8) throw new ArgumentException("已经到达声音文件开头，不能再向左延长。");
            data["AudioIn"] = Math.Max(0, input); data["AudioDuration"] = end - at;
        }
        clip["start"] = at; clip["duration"] = end - at; next.Validate(); return new(next, id);
    }
    public static Result Split(CreativeScene source, string id, double time)
    {
        double at = Snap(time); var next = new CreativeScene(source.Root); var left = next.Clip(id);
        if (!CanSplit(left, at)) throw new ArgumentException("把播放头放在声音或停留片段内部，再执行分割。");
        double start = N(left, "start"), end = start + N(left, "duration");
        var right = (JsonObject)left.DeepClone(); string newId = CreativeScene.Id();
        left["duration"] = at - start; right["id"] = newId; right["start"] = at; right["duration"] = end - at;
        if (left["data"]?["AudioPath"] is not null)
        {
            left["data"]!["AudioDuration"] = at - start;
            right["data"]!["AudioIn"] = N(right["data"]!.AsObject(), "AudioIn") + at - start;
            right["data"]!["AudioDuration"] = end - at;
        }
        next.Clips.Insert(next.Clips.IndexOf(left) + 1, right); next.Validate(); return new(next, newId);
    }
    public static double DisplayEnd(CreativeScene source, JsonObject clip)
    {
        double end = N(clip, "start") + N(clip, "duration");
        string kind = S(clip, "kind");
        if (kind == "expression") return CreativeMaterialEditing.ExpressionEnd(source, clip);
        if (kind != "expression" && !(kind == "motion" && CreativeStaging.IsNativeActor(source,S(clip,"actor")))) return end;
        double start = N(clip, "start"); string actor = S(clip, "actor");
        return source.Clips.OfType<JsonObject>().Where(other => !ReferenceEquals(other, clip)
                && S(other, "kind") == kind && S(other, "actor") == actor
                && other["enabled"]?.GetValue<bool>() != false && N(other, "start") > start)
            .Select(other => N(other, "start")).DefaultIfEmpty(source.Duration).Min();
    }
}
