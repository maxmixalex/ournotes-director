using System.Globalization;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

public static class DirectorValidation
{
    public static void Check(AdvRow row)
    {
        if(!row.Command.StartsWith("Director.",StringComparison.Ordinal)||row.Ignored)return;
        void Error(string message)=>throw new ArgumentException($"{row.Index} {row.Command}: {message}");
        float Number(JsonNode? node,string name,float min=float.NegativeInfinity,float max=float.PositiveInfinity)
        {if(node is null||!float.TryParse(node.ToString(),NumberStyles.Float,CultureInfo.InvariantCulture,out float n)||!float.IsFinite(n)||n<min||n>max){Error("Invalid "+name);return 0;}return n;}
        var allowed=new[]{"cmd","i","Command","raw","TargetName","Values","Duration","Ease","IsNoWait","IgnoreData","Space","Parameter","Operation","Value","Key","AtFrame"};
        foreach(var pair in row.Data.AsObject())if(!allowed.Contains(pair.Key))Error("Unknown field "+pair.Key);
        if(row.Data["Duration"] is {} duration)Number(duration,"Duration",0,36000);
        if(row.Text("Ease") is {Length:>0} ease&&(!Enum.TryParse<AdvEase>(ease,out var parsed)||!Enum.IsDefined(parsed)||parsed is >=AdvEase.Flash and <=AdvEase.INTERNAL_Custom))Error("Unsupported Ease");
        if(row.Command is "Director.Actor" or "Director.Parameter"&&string.IsNullOrWhiteSpace(row.Text("TargetName")))Error("TargetName is required");
        if(row.Command=="Director.Parameter")
        {
            if(row.Text("Parameter")=="")Error("Parameter is required");if(row.Text("Operation") is not ("" or "set" or "release"))Error("Operation must be set or release");
            if(row.Text("Operation")!="release")Number(row.Data["Value"],"Value");return;
        }
        if(row.Text("Space") is not ("" or "world" or "screen"))Error("Space must be world or screen");
        if(row.Data["Values"] is not JsonObject values){Error("Values must be an object");return;}
        if(row.Command=="Director.Actor"&&values.ContainsKey("overlay"))
        {
            if(values.Count!=1||row.Text("Space").Length>0||values["overlay"] is not JsonObject overlay){Error("overlay cannot be mixed with actor placement");return;}
            foreach(var(key,value)in overlay)
            {if(key is not ("x" or "y" or "scale"))Error("Unsupported overlay value "+key);Number(value,"overlay."+key,key=="scale"?.0001f:float.NegativeInfinity);}
            return;
        }
        string[] keys=row.Command switch
        {
            "Director.Actor"=>["position","rotation","scale","positionX","positionY","rotationZ","height","opacity","brightness","tint","physics","breath","blink","speed","loop","animation","warmupFrames","visible","order"],
            "Director.Camera"=>["position","rotation","fieldOfView","framing"],"Director.Scene"=>["background","stageVisible","backgroundImage","grade"],_=>[]
        };
        foreach(var(key,value)in values)
        {
            if(!keys.Contains(key))Error("Unsupported value "+key);
            if(key is "position" or "rotation" or "scale" or "tint" or "background")
            {
                if(value is not JsonArray array){Error(key+" must be an array");continue;}
                int size=key is "tint" or "background"?4:3;
                if(array.Count!=size&&!(key=="position"&&row.Command=="Director.Actor"&&array.Count==2))Error("Wrong component count for "+key);
                foreach(var element in array)Number(element,key,key is "tint" or "background"?0:float.NegativeInfinity,key=="background"?1:float.PositiveInfinity);
            }
            else if(key is "physics" or "breath" or "blink" or "loop" or "visible" or "stageVisible")
            {
                if(value is not JsonValue boolean||!boolean.TryGetValue<bool>(out bool b))Error(key+" must be boolean");else if(key=="stageVisible"&&b)Error("Use Stage to show a stage");
            }
            else if(key=="animation"){if(value?.ToString() is not ("game" or "director"))Error("Unknown animation mode");}
            else if(key is "framing" or "grade")
            {
                if(value is not JsonObject obj){Error(key+" must be an object");continue;}
                foreach(var pair in obj){if(!(key=="framing"?new[]{"x","y","zoom","roll"}:new[]{"exposure","contrast","saturation","vignette"}).Contains(pair.Key))Error("Unknown "+key+" field "+pair.Key);Number(pair.Value,pair.Key,pair.Key=="zoom"?.0001f:float.NegativeInfinity);}
            }
            else if(key=="backgroundImage")
            {if(value is not JsonObject image||image["path"] is null&&(image["adv"] is null||image["logicalPath"] is null))Error("backgroundImage requires path or adv/logicalPath");}
            else
            {
                Number(value,key,key is "height" or "fieldOfView"?.0001f:key is "opacity" or "brightness" or "speed" or "warmupFrames"?0:float.NegativeInfinity,key=="opacity"?1:key=="fieldOfView"?179.999f:key=="warmupFrames"?300:float.PositiveInfinity);
                if(key is "warmupFrames" or "order"&&!int.TryParse(value!.ToString(),out _))Error(key+" must be an integer");
            }
        }
    }
}
