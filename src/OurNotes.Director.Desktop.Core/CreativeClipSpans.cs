using System.Text.Json.Nodes;
using OurNotes.Director.Core;

namespace OurNotes.Director.Desktop.Core;

/// <summary>Batch equivalent of CreativeClipEditing.DisplayEnd. Building a long
/// timeline must not scan the whole document for every motion/expression.</summary>
public static class CreativeClipSpans
{
    public static IReadOnlyDictionary<string, double> Calculate(CreativeScene scene)
    {
        var clips = scene.Clips.OfType<JsonObject>().Select((clip, index) => new Entry(clip, index)).ToArray();
        double duration = scene.Duration;
        var ends = clips.ToDictionary(e => e.Id, e => e.Start + e.Duration, StringComparer.Ordinal);
        var expressions = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var entry in clips.OrderByDescending(e => e.Frame).ThenByDescending(e => e.Index))
        {
            if (entry.Kind == "expression") ends[entry.Id] = Math.Max(entry.Start, expressions.GetValueOrDefault(entry.Actor, duration));
            if (!entry.Enabled || entry.Clip["data"] is not JsonObject row) continue;
            string command = CreativeScene.Text(row, "cmd");
            bool changes = command is "Expression" or "In" or "Out" or "Costume"
                || command == "Motion" && !CreativeMaterialEditing.KeepsExpression(entry.Clip)
                    && CreativeScene.Number(row, "MotionWait") <= 0;
            if (changes && !AdvProgram.Row(row, "expression span").Ignored) expressions[entry.Actor] = entry.Start;
        }
        var native = scene.Actors.OfType<JsonObject>().Where(a => a["stageSlot"] is not null || a["fromSource"]?.GetValue<bool>() == true)
            .Select(a => CreativeScene.Text(a, "id")).ToHashSet(StringComparer.Ordinal);
        foreach (var actor in clips.Where(e => e.Kind == "motion" && native.Contains(e.Actor)).GroupBy(e => e.Actor))
        {
            double next = duration;
            foreach (var group in actor.OrderByDescending(e => e.Start).GroupBy(e => e.Start))
            {
                foreach (var entry in group) ends[entry.Id] = next;
                if (group.Any(e => e.Enabled)) next = group.Key;
            }
        }
        return ends;
    }
    sealed class Entry(JsonObject clip, int index)
    {
        public JsonObject Clip { get; } = clip;
        public int Index { get; } = index;
        public string Id { get; } = CreativeScene.Text(clip, "id");
        public string Actor { get; } = CreativeScene.Text(clip, "actor");
        public string Kind { get; } = CreativeScene.Text(clip, "kind");
        public double Start { get; } = CreativeScene.Number(clip, "start");
        public double Duration { get; } = CreativeScene.Number(clip, "duration");
        public int Frame { get; } = checked((int)Math.Round(CreativeScene.Number(clip, "start") * 30));
        public bool Enabled { get; } = clip["enabled"]?.GetValue<bool>() != false;
    }
}
