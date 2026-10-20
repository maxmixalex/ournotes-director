using System.Text;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

static class StoryTimingRenderStateTests
{
    public static async Task RunAsync(Archive archive,string core,string ffmpeg,string output,
        Action<bool,string> check,JsonObject report)
    {
        var manifest=archive.Manifest("intl","stories","10462");
        var definition=JsonNode.Parse(archive.Logical("intl",manifest,"story.json"))!;
        var scene=JsonNode.Parse(archive.Logical("intl",manifest,Archive.S(definition,"scene")))!;
        string stage=scene["stages"]!.AsObject().First().Key;
        string modelId=definition["models"]!.AsObject().First().Value!.ToString();
        var model=archive.Model("intl",modelId);
        string motion=model["motions"]!.AsArray().Select(value=>value!.ToString()).First(value=>value!=model["defaultMotion"]!.ToString());
        string expression=model["expressions"]!.AsArray().Select(value=>value!.ToString()).First(value=>value!=model["defaultExpression"]!.ToString());
        const string actor="capture-actor";
        var rows=new JsonArray(
            new JsonObject{["cmd"]="Character",["TargetName"]=actor,["Model"]=modelId},
            new JsonObject{["cmd"]="Stage",["TargetAssetName"]=stage,["Duration"]=.1},
            new JsonObject{["cmd"]="In",["TargetName"]=actor,["PositionType"]=3,["Duration"]=.1},
            new JsonObject{["cmd"]="Motion",["TargetName"]=actor,["MotionName"]=motion,["ExpressionName"]=expression,["MotionWait"]=.075,["MotionFadeIn"]=.1},
            new JsonObject{["cmd"]="Delay",["Frames"]=6},
            new JsonObject{["cmd"]="Pause",["TargetName"]=actor},
            new JsonObject{["cmd"]="Motion",["TargetName"]=actor,["MotionName"]=model["defaultMotion"]!.ToString(),["MotionFadeIn"]=.1},
            new JsonObject{["cmd"]="Expression",["TargetName"]=actor,["ExpressionName"]=expression,["MotionFadeIn"]=.1},
            new JsonObject{["cmd"]="RimLight",["TargetName"]=actor,["Parameter1"]="#ffcc88",["Parameter2"]=.4},
            new JsonObject{["cmd"]="Stage",["TargetAssetName"]=stage,["Duration"]=.2,["IsNoWait"]=1},
            new JsonObject{["cmd"]="Delay",["Frames"]=1},
            new JsonObject{["cmd"]="Resume",["TargetName"]=actor},
            new JsonObject{["cmd"]="Delay",["Frames"]=16},
            new JsonObject{["cmd"]="Stage",["TargetAssetName"]="",["Duration"]=.05},
            new JsonObject{["cmd"]="Delay",["Frames"]=6}
        );
        var project=new JsonObject{["format"]=DirectorProject.Format,["version"]=1,
            ["source"]=new JsonObject{["adv"]="10462",["site"]="intl",["language"]="zh-Hans"},
            ["output"]=new JsonObject{["width"]=320,["height"]=180,["supersample"]=1},
            ["initialize"]=new JsonArray(),["finalize"]=new JsonArray(),["commands"]=rows};
        string path=Path.Combine(output,"capture-timing.project.json"),json=project.ToJsonString();
        File.WriteAllText(path,json);string hash=Archive.Hash(Encoding.UTF8.GetBytes(json));
        AdvStoryRenderer Renderer(Archive input,bool timingOnly)=>new(input,"intl",core,"10462","zh-Hans",320,180,path,ffmpeg,1,project,timingOnly,new AdvCueCache());
        bool Same(NativePlaybackTiming a,NativePlaybackTiming b)=>a.IsComplete&&b.IsComplete&&a.ProjectHash==b.ProjectHash
            &&a.EndFrame==b.EndFrame&&a.Duration==b.Duration&&a.Commands.SequenceEqual(b.Commands);

        NativePlaybackTiming reference;
        using(var rendered=Renderer(archive,false))
        {
            reference=rendered.AnalyzeTiming(hash);
            check(rendered.Scene.CaptureWaitCount==3&&rendered.Scene.CaptureDrawCount==3
                &&rendered.Scene.PreloadedModelTextureCount>0,
                "Timing reference executes three actual stage captures with full native models, textures and queued/paused motion requests");
        }
        using(var timing=Renderer(archive,true))
        {
            var actual=timing.AnalyzeTiming(hash);
            check(Same(actual,reference)&&timing.Scene.CaptureWaitCount==3&&timing.Scene.CaptureDrawCount==0
                &&timing.Scene.PreloadedModelTextureCount==0&&timing.Scene.Character(actor)!.Core.ParameterIds.Length>0,
                "Drawing-free capture timing matches every full-renderer command boundary, including overlapping stage fades and MotionWait");
        }
        using(var timing=Renderer(archive,true))
        using(var cancellation=new CancellationTokenSource())
        {
            timing.Player.OnCommand=(row,_)=>{if(row.Command=="Stage")cancellation.Cancel();};
            bool cancelled=false;
            try{timing.AnalyzeTiming(hash,cancellationToken:cancellation.Token);}
            catch(OperationCanceledException)when(cancellation.IsCancellationRequested){cancelled=true;}
            check(cancelled&&timing.Scene.CaptureWaitCount==0&&timing.Scene.CaptureDrawCount==0,
                "Cancellation stops with the stage capture still pending, before its next-Update completion");
            timing.Player.OnCommand=null;
            check(Same(timing.AnalyzeTiming(hash),reference)&&timing.Scene.CaptureWaitCount==3,
                "Resuming the pending capture preserves the exact original completion frame and command timing");
        }

        // Use a private archive copy with only this model's PNG assets omitted.
        // Analysis can finish without them; the actual editable preview must still
        // surface its loading error instead of falsely declaring the import ready.
        string missingRoot=Path.Combine(output,"missing-model-textures");
        Directory.CreateDirectory(Path.Combine(missingRoot,"index"));File.WriteAllText(Path.Combine(missingRoot,"index","stats.json"),"{}");
        var modelManifest=archive.Manifest("intl","models",modelId);
        var modelDocument=JsonNode.Parse(archive.Logical("intl",modelManifest,"model.json"))!;
        var omitted=modelDocument["textures"]!.AsArray().Select(texture=>modelManifest["files"]![texture!.ToString()]!["asset"]!.ToString()).ToHashSet(StringComparer.Ordinal);
        CopyManifest("stories","10462",manifest);CopyManifest("models",modelId,modelManifest);
        var missing=new Archive(missingRoot);
        using(var timing=Renderer(missing,true))
            check(Same(timing.AnalyzeTiming(hash),reference),"Missing model PNGs do not force unused graphics decoding during exact native timing analysis");
        await using(var preview=new RealtimePreviewSession())
        {
            bool reported=false,failed=false;preview.Failed+=(_,_)=>reported=true;
            try{await preview.LoadSceneAsync(new(missingRoot,path,core,ffmpeg,320,180,reference.Duration!.Value));}
            catch(FileNotFoundException){failed=true;}
            check(failed&&reported&&preview.LastFrame is null,"The final editable preview still propagates and reports a missing model texture instead of publishing a false success");
        }
        report["captureTiming"]=new JsonObject{["endFrame"]=reference.EndFrame,["commands"]=reference.Commands.Count,
            ["captures"]=3,["motionWaitSeconds"]=.075,["stage"]=stage,["model"]=modelId};

        void CopyManifest(string type,string id,JsonNode document)
        {
            string destination=Path.Combine(missingRoot,"sites","intl",type,id+".json");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.WriteAllText(destination,document.ToJsonString());
            CopyAssets(document);
        }
        void CopyAssets(JsonNode? node)
        {
            if(node is JsonObject obj)
            {
                if(obj["asset"] is JsonValue asset)CopyAsset(asset.ToString());
                foreach(var value in obj)CopyAssets(value.Value);
            }
            else if(node is JsonArray array)foreach(var value in array)
            {
                if(value is JsonValue item&&item.ToString().StartsWith("assets/",StringComparison.Ordinal))CopyAsset(item.ToString());
                else CopyAssets(value);
            }
        }
        void CopyAsset(string asset)
        {
            if(omitted.Contains(asset))return;
            string source=archive.SitePath("intl",asset),destination=Path.Combine(missingRoot,"sites","intl",asset);
            if(!File.Exists(source)||File.Exists(destination))return;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.Copy(source,destination);
        }
    }
}
