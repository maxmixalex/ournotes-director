using System.Text.Json;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

// Legacy input adapter only. Playback and drawing remain in AdvStoryRenderer.
public static class ShotProject
{
    public static JsonObject Convert(string path)
    {
        var shot=Shot.Read(path);shot.Validate();var commands=new JsonArray();
        var source=shot.Source??new ShotSource();
        JsonNode Node<T>(T value)=>JsonOutput.SerializeToNode(value,Shot.JsonOptions)!;
        void Add(string cmd,JsonObject values)=>commands.Add((JsonNode)new JsonObject{["cmd"]=cmd,["Values"]=values});
        Add("Director.Scene",new(){["background"]=Node(shot.Background),["stageVisible"]=false,["grade"]=Node(shot.Grade)});
        if(shot.Stage is not null)commands.Add((JsonNode)new JsonObject{["cmd"]="Stage",["TargetAssetName"]=shot.Stage});
        if(shot.BackgroundImage is not null)Add("Director.Scene",new(){["backgroundImage"]=Node(shot.BackgroundImage)});
        Add("Director.Camera",new(){["framing"]=Node(shot.Camera)});
        foreach(var a in shot.Actors){
            commands.Add((JsonNode)new JsonObject{["cmd"]="Character",["TargetName"]=a.Name,["Model"]=a.Model,["Seed"]=shot.Seed+(uint)shot.Actors.IndexOf(a)*7919});
            commands.Add((JsonNode)new JsonObject{["cmd"]="Director.Actor",["TargetName"]=a.Name,["Space"]="screen",["Values"]=new JsonObject{
                ["position"]=new JsonArray(a.X,a.Y),["height"]=a.Height,["rotation"]=new JsonArray(0,0,a.Rotation),["opacity"]=a.Opacity,["brightness"]=a.Brightness,["tint"]=Node(a.Tint),
                ["animation"]="director",["loop"]=a.Loop,["speed"]=a.Speed,["physics"]=a.Physics,["breath"]=a.Breath,["blink"]=a.Blink,["order"]=shot.Actors.IndexOf(a)}});
            if(a.Motion is not null)commands.Add((JsonNode)new JsonObject{["cmd"]="Motion",["TargetName"]=a.Name,["MotionName"]=a.Motion});
            if(a.Expression is not null)commands.Add((JsonNode)new JsonObject{["cmd"]="Expression",["TargetName"]=a.Name,["ExpressionName"]=a.Expression});
            foreach(var (parameter,value) in a.Parameters)commands.Add((JsonNode)new JsonObject{["cmd"]="Director.Parameter",["TargetName"]=a.Name,["Parameter"]=parameter,["Value"]=value});
            commands.Add((JsonNode)new JsonObject{["cmd"]="Director.Actor",["TargetName"]=a.Name,["Values"]=new JsonObject{["warmupFrames"]=shot.WarmupFrames}});
        }
        var scheduled=new List<(double Time,JsonObject Row)>();
        JsonObject Key(TrackSpec track,float value,float duration){
            var row=new JsonObject{["Duration"]=duration,["IsNoWait"]=1,["Ease"]=track.Interpolation=="smoothstep"?"DirectorSmoothStep":"Linear"};
            if(track.Target=="camera"){row["cmd"]="Director.Camera";row["Values"]=new JsonObject{["framing"]=new JsonObject{[track.Property]=value}};}
            else if(track.Property.StartsWith("param:",StringComparison.Ordinal)){row["cmd"]="Director.Parameter";row["TargetName"]=track.Target;row["Parameter"]=track.Property[6..];row["Value"]=value;}
            else{row["cmd"]="Director.Actor";row["TargetName"]=track.Target;row["Values"]=new JsonObject{[track.Property switch{"x"=>"positionX","y"=>"positionY","rotation"=>"rotationZ",_=>track.Property}]=value};}
            return row;
        }
        foreach(var track in shot.Tracks){
            scheduled.Add((0,Key(track,track.Keys[0].Value,0)));
            for(int i=1;i<track.Keys.Count;i++){
                var previous=track.Keys[i-1];var next=track.Keys[i];bool step=track.Interpolation=="step";
                scheduled.Add((step?next.T:previous.T,Key(track,next.Value,step?0:(float)(next.T-previous.T))));
            }
        }
        foreach(var ev in shot.Events){
            if(ev.Motion is not null)scheduled.Add((ev.T,new(){["cmd"]="Motion",["TargetName"]=ev.Actor,["MotionName"]=ev.Motion,["MotionFadeIn"]=ev.Fade}));
            if(ev.Expression is not null)scheduled.Add((ev.T,new(){["cmd"]="Expression",["TargetName"]=ev.Actor,["ExpressionName"]=ev.Expression,["MotionFadeIn"]=ev.Fade}));
        }
        double time=0;
        foreach(var item in scheduled.OrderBy(p=>p.Time)){
            if(item.Time>time)commands.Add((JsonNode)new JsonObject{["cmd"]="Delay",["Duration"]=item.Time-time});commands.Add((JsonNode)item.Row);time=item.Time;
        }
        if(time<shot.Duration)commands.Add((JsonNode)new JsonObject{["cmd"]="Delay",["Duration"]=shot.Duration-time});
        return new(){["format"]=DirectorProject.Format,["version"]=1,["source"]=new JsonObject{["adv"]=source.Adv,["site"]=source.Site,["language"]=source.Language},["initialize"]=new JsonArray(),["finalize"]=new JsonArray(),["commands"]=commands,
            ["output"]=new JsonObject{["width"]=shot.Output.Width,["height"]=shot.Output.Height,["supersample"]=shot.Output.RenderScale,["codec"]="prores",["clean"]=true,["letterbox"]="none"}};
    }
}
