using System.Text.Json.Nodes;
using OurNotes.Director.Core;

namespace OurNotes.Director.Desktop.Core;

public sealed partial class Live2DDocument
{
    // One canonical project for preview, live-edit commits, canvas analysis and
    // export. ToSceneDocument(false) remains the lossless source-recovery path.
    public SceneDocument ToWorkbenchSceneDocument() => ToSceneDocument(selectedMotionOnly: true);
    public SceneSnapshot CreateWorkbenchSnapshot(string directory)
        => ToWorkbenchSceneDocument().CreateSnapshot(directory);

    bool HasExactSelectionSchedule => root["performance"] is not JsonObject performance
        || performance["commands"] is JsonArray rows
            && rows.OfType<JsonObject>().All(row => row["AtFrame"] is not null);

    JsonObject SelectedPerformance()
    {
        var project = Standalone();
        // Use the original archive as a material source where available. No
        // inherited episode is executed: initialize/finalize are explicitly empty.
        // This also works with small archives that have no separate library pack.
        if (root["performance"] is JsonObject performance)
            project["source"] = performance["source"]!.DeepClone();
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
