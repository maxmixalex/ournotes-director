namespace OurNotes.Director.Core;

public sealed partial class AdvScene
{
    enum StageEnvType{All,Light,Effect,PostEffect,FocusPosition}
    IEnumerable<AdvWait> StageEnvironment(AdvRow row,AdvPlayback player)
    {
        if(stage is null)yield break;var type=Enum.TryParse<StageEnvType>(row.Text("Parameter1"),out var value)?value:StageEnvType.All;int index=(int)Param(row,2);
        if(type is StageEnvType.All or StageEnvType.Light)stageLightGroups[StageName]=index;
        if(type is StageEnvType.All or StageEnvType.Effect)stageEffects.GetValueOrDefault(StageName)?.Change(index);
        if(type is StageEnvType.All or StageEnvType.PostEffect)SetStageVolume(index);
        if(type is StageEnvType.All or StageEnvType.FocusPosition){var entries=stage["_focusPointsCollection"]!["_entries"]!.AsArray();var points=entries[index>=0&&index<entries.Count?index:0]!["_focusPointPositions"]!.AsArray();
            for(int i=0;i<anchors.Length;i++)anchors[i].Position=AdvPrefab.Vector(points[i]);for(int i=0;i<slots.Length;i++)slots[i].Position=AdvPrefab.Vector(points[i*2]);}
        yield break;
    }
}
