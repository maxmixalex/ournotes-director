namespace OurNotes.Director.Core;

public sealed record NativeStageActorState(string Id, int Slot, float WorldX, float WorldY,
    float WorldZ, float Blur, int Priority, string Motion, string Expression, float MouthOpen);
public sealed partial class AdvScene
{
    /// <summary>Actual native stage state, not the editable document's requested values.</summary>
    public IReadOnlyList<NativeStageActorState> StageActors => shown.Select(pair =>
    {
        var actor = pair.Value; var world = actor.Prefab.Root.WorldPosition; var animation = actor.PreviewAnimation();
        int index = (pair.Key - 1) / 2;
        return new NativeStageActorState(actor.Spec.Name, pair.Key, world.X, world.Y, world.Z,
            slotBlur[index], priority[index], animation.Motion, animation.Expression, actor.MouthOpen);
    }).ToArray();
}
