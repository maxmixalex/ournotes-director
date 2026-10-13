using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

public sealed partial class AdvScene
{
    AdvPostFx post=null!;
    AdvPostFx? mediaPost;AdvCamera? mediaCamera;
    readonly List<AdvVolume> volumes=[];
    readonly List<(JsonNode Profile,float Weight)> evaluatedVolumeInputs=[];
    JsonNode? evaluatedVolumeStack;
    JsonNode VolumeStack()
    {
        bool unchanged=evaluatedVolumeStack is not null&&evaluatedVolumeInputs.Count==volumes.Count;
        for(int i=0;unchanged&&i<volumes.Count;i++)unchanged=ReferenceEquals(evaluatedVolumeInputs[i].Profile,volumes[i].Profile)&&evaluatedVolumeInputs[i].Weight==volumes[i].Weight;
        if(unchanged)return evaluatedVolumeStack!;
        evaluatedVolumeInputs.Clear();foreach(var volume in volumes)evaluatedVolumeInputs.Add((volume.Profile,volume.Weight));
        return evaluatedVolumeStack=AdvVolume.Evaluate(volumes);
    }
    readonly Dictionary<string,JsonNode> effectProfiles=[];
    readonly Dictionary<string,ChildVolume> childVolumes=[];
    AdvVolume? stageVolume;
    sealed class ChildVolume(AdvVolume volume){public AdvVolume Volume=volume;public AdvTween? Tween;public bool FadeOut;}
    void InitializePost(JsonNode scene)
    {
        post=new(gl,shaders){GrainTextures=scene["postTextures"]?["filmGrainTex"]?.AsArray(),LoadTexture=desc=>{
            string path=Archive.S(desc,"texture");if(!textures.TryGetValue(path,out uint texture))textures[path]=texture=gl.Upload(Png.Decode(archive.Logical(site,manifest,path)));return texture;}};
        var nodes=scene["globalVolume"]!["nodes"]!.AsArray().OfType<JsonNode>().ToArray();var warmup=nodes.FirstOrDefault(n=>n["components"]!.AsArray().Any(c=>c?["sharedProfile"]?["name"]?.ToString()=="AdvWarmupVolumeProfileAll"));
        if(warmup is not null){var v=warmup["components"]!.AsArray().First(c=>c?["sharedProfile"] is not null)!;volumes.Add(new(v["sharedProfile"]!,Actor.N(v["weight"],1)));}
        string file=Archive.S(definition,"postEffects");if(file!="")foreach(var entry in JsonNode.Parse(archive.Logical(site,manifest,file))!["postEffects"]!.AsObject())effectProfiles[entry.Key]=entry.Value!;
    }
    void SetStageVolume(int index=0)
    {
        var profiles=stage?["_allVolumeProfileCollection"]?["_profiles"]?.AsArray();var profile=index>=0&&index<(profiles?.Count??0)?profiles![index]:null;
        if(profile is null){if(stageVolume is not null)volumes.Remove(stageVolume);stageVolume=null;return;}
        if(profile["components"] is null){string name=Archive.S(profile,"name");profile=stages.Values.SelectMany(s=>s["_allVolumeProfileCollection"]?["_profiles"]?.AsArray()??[]).OfType<JsonNode>().FirstOrDefault(p=>Archive.S(p,"name")==name&&p["components"] is not null)??throw new InvalidDataException("Shared stage volume not exported: "+name);}
        if(stageVolume is null){stageVolume=new(profile);volumes.Add(stageVolume);}else stageVolume.Profile=profile;
    }
    void KillVolumeTween(ChildVolume child)
    {
        child.Tween?.Kill();child.Tween=null;if(child.FadeOut)volumes.Remove(child.Volume);child.FadeOut=false;
    }
    void FadeVolume(ChildVolume child,bool show,float duration)
    {
        if(show&&!volumes.Contains(child.Volume))volumes.Add(child.Volume);KillVolumeTween(child);
        if(duration<=0){child.Volume.Weight=show?1:0;if(!show)volumes.Remove(child.Volume);return;}
        child.FadeOut=!show;var tween=child.Tween=tweens.To(()=>child.Volume.Weight,show?1:0,duration,AdvEase.OutQuad,v=>child.Volume.Weight=v);
        tween.Completed.OnCompleted(()=>{if(child.Tween!=tween)return;child.Tween=null;if(child.FadeOut)volumes.Remove(child.Volume);child.FadeOut=false;});
    }
    IEnumerable<AdvWait> PostEffect(AdvRow row,AdvPlayback player)
    {
        float fade=player.CalcDuration(Param(row,1));if(fade<=0)fade=player.CalcDuration(row.Number("Duration"));
        if(effectProfiles.TryGetValue(row.Text("TargetAssetName"),out var profile)){
            string name=Archive.S(profile,"name");if(!childVolumes.TryGetValue(name,out var child))childVolumes[name]=child=new(new(profile,0));
            FadeVolume(child,!volumes.Contains(child.Volume),fade);
        }else foreach(var child in childVolumes.Values)FadeVolume(child,false,fade);
        yield break;
    }
    public void FinalPost(bool fxaa=true)=>post.Final(fxaa);
    public void DrawMedia(Action draw,bool suppressVolumes)
    {
        mediaPost??=new(gl,shaders){GrainTextures=post.GrainTextures,LoadTexture=post.LoadTexture};
        if(mediaCamera is null){var ui=JsonNode.Parse(archive.Logical(site,manifest,Archive.S(definition,"ui"),language))!;var c=ui["videoAndStillCamera"]!["camera"]!;mediaCamera=new(loop,tweens,Actor.N(c["field of view"]),Actor.N(c["near clip plane"]),Actor.N(c["far clip plane"]));}
        mediaPost.GrainIntensity=post.GrainIntensity;mediaPost.GrainScale=post.GrainScale;
        gl.BeginLayer();draw();mediaPost.Render(suppressVolumes?AdvVolume.Evaluate([]):VolumeStack(),mediaCamera,loop.Frame);gl.EndLayer();
    }
}
