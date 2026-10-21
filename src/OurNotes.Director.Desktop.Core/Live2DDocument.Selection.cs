using System.Text.Json.Nodes;
using OurNotes.Director.Core;

namespace OurNotes.Director.Desktop.Core;

public sealed partial class Live2DDocument
{
    (string? Motion, string? Expression) SourceAnimationSelection()
    {
        string? defaultMotion = root["metadata"]?["defaultMotion"]?.GetValue<string>();
        string? defaultExpression = root["metadata"]?["defaultExpression"]?.GetValue<string>();
        string? motion = defaultMotion, expression = defaultExpression;
        foreach (var row in PerformanceRows().Where(row => Frame(row) <= FrameAt(Time)
            && Text(row, "TargetName") == ActorId).OrderBy(Frame))
        {
            var native = AdvProgram.Row(row, "Live2D source selection");
            if (native.Ignored) continue;
            if (native.Command is "In" or "Motion")
            {
                if (native.Text("MotionName") is { Length: > 0 } selected) motion = selected;
                else if (native.Command == "In") motion = defaultMotion;
                // The native handlers reset an omitted expression on both In and
                // Motion; an omitted MotionName only resets when entering an actor.
                expression = native.Text("ExpressionName") is { Length: > 0 } face ? face : defaultExpression;
            }
            else if (native.Command == "Expression")
                expression = native.Text("ExpressionName") is { Length: > 0 } face ? face : defaultExpression;
        }
        return (motion, expression);
    }

    void CaptureMissingSelections()
    {
        if (root["performance"] is not JsonObject || !HasExactSelectionSchedule) return;
        // Older saved workbench files may contain only the original performance.
        // Freeze their selection at the saved cursor once, before transport can
        // move. Store the projection as ordinary edits while retaining every
        // original source row and existing parameter/controller edit unchanged.
        string? motion = Motion, expression = Expression;
        var edits = root["edits"]!.AsArray();
        if (!OwnRows().Any(row => Text(row, "cmd") == "Motion"))
            edits.Add((JsonNode)new JsonObject { ["cmd"] = "Motion", ["TargetName"] = ActorId,
                ["MotionName"] = motion ?? "", ["ExpressionName"] = expression ?? "",
                ["MotionFadeIn"] = 0, ["AtFrame"] = 0 });
        if (!OwnRows().Any(row => Text(row, "cmd") == "Expression"))
            edits.Add((JsonNode)new JsonObject { ["cmd"] = "Expression", ["TargetName"] = ActorId,
                ["ExpressionName"] = expression ?? "", ["MotionFadeIn"] = 0, ["AtFrame"] = 0 });
    }

    // One canonical project for preview, live-edit commits, canvas analysis and
    // export. ToSceneDocument(false) remains the lossless source-recovery path.
    public SceneDocument ToWorkbenchSceneDocument() => ToSceneDocument(selectedMotionOnly: true);
    public SceneSnapshot CreateWorkbenchSnapshot(string directory)
        => ToWorkbenchSceneDocument().CreateSnapshot(directory);

    bool HasExactSelectionSchedule => root["performance"] is not JsonObject performance
        || performance["initialize"] is JsonArray && performance["finalize"] is JsonArray
            && performance["commands"] is JsonArray rows
            && rows.OfType<JsonObject>().All(row => row["AtFrame"] is not null);

    JsonObject SelectedPerformance()
    {
        var project = Standalone();
        // Use the original archive as a material source where available. No
        // inherited episode is executed: initialize/finalize are explicitly empty.
        // This also works with small archives that have no separate library pack.
        if (root["performance"] is JsonObject performance)
        {
            project["source"] = performance["source"]!.DeepClone();
            if (performance["output"]?["cineformQuality"] is { } quality)
                project["output"]!["cineformQuality"] = quality.DeepClone();
        }
        return project;
    }

    IEnumerable<JsonObject> SelectedEditRows()
    {
        foreach (var row in OwnRows())
            if (Text(row, "cmd") is not ("Motion" or "Expression")) yield return row;
        // The baseline Director.Actor makes the model visible without retaining
        // source In/Out/costume/camera tracks. Removing In alone hides the actor.
        yield return new JsonObject { ["cmd"] = "Motion", ["TargetName"] = ActorId,
            ["MotionName"] = Motion ?? "", ["ExpressionName"] = Expression ?? "",
            ["MotionFadeIn"] = 0, ["AtFrame"] = 0 };
        yield return new JsonObject { ["cmd"] = "Expression", ["TargetName"] = ActorId,
            ["ExpressionName"] = Expression ?? "", ["MotionFadeIn"] = 0, ["AtFrame"] = 0 };
    }
}
