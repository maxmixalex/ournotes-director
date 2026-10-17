using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

public sealed partial class AdvScene
{
    /// <summary>Immediate editing of the loaded native scene, using the export command handlers.</summary>
    public void ApplyPreviewEdits(IReadOnlyList<JsonObject> rows, AdvPlayback player)
    {
        loop.VerifyThread();
        var prepared = rows.Select((data,index) => AdvProgram.Row(data,"preview"+index)).ToArray();
        foreach(var row in prepared)
        {
            if(!AdvProgram.DirectorCommands.Contains(row.Command)||row.Number("Duration")!=0)
                throw new ArgumentException("Live edits require immediate Director.Actor, Camera, Scene or Parameter commands.");
            DirectorValidation.Check(row);
        }
        foreach(var row in prepared)
        {
            // Enumerating invokes the exact scalar/colour handlers. Duration is zero, so
            // they apply synchronously without advancing, restarting or reloading motion.
            foreach(var _ in Director(row,player)) { }
            if(row.Command=="Director.Parameter")Character(row.Text("TargetName"))?.RefreshPreviewParameters();
        }
        loop.Drain();player.ThrowIfFailed();
    }

    /// <summary>Current native geometry projected to the scene's actual render viewport.</summary>
    public IReadOnlyList<PreviewTarget> PreviewTargets()
    {
        loop.VerifyThread();
        var result=new List<PreviewTarget>();
        var world=Camera.ViewProjection(gl.Width/(float)gl.Height);
        foreach(var actor in shown.OrderByDescending(p=>p.Value.Prefab.Root.WorldPosition.Z).ThenBy(p=>priority[(p.Key-1)/2]).Select(p=>p.Value).Distinct())
            if(actor.PreviewBounds(world) is {} bounds)result.Add(bounds with { StagePlacement = StageDragGeometry(actor.Spec.Name) });
        foreach(var (actor,state) in directorActors.OrderBy(pair=>pair.Value.Order))
            if(state.Visible&&actor.PreviewBounds(state.Space=="screen"?DirectorProjection():world) is {} bounds)result.Add(bounds);
        return result;
    }

    public IReadOnlyList<PreviewLipSync> PreviewLipSyncState()=>shown.Values.Concat(directorActors.Keys).Distinct()
        .Select(actor=>new PreviewLipSync(actor.Spec.Name,actor.LipSyncEngine,
            actor.VoiceLipSyncActive||actor.PseudoLipSyncActive,actor.MouthOpen,actor.LipSyncMissing)).ToArray();

    public IReadOnlyList<PreviewActorAnimation> PreviewAnimationState()=>VisibleActors.Select(actor=>actor.PreviewAnimation()).ToArray();
}
