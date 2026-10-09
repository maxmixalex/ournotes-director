// Port of ournotes-player live2d/paramloop.js (AGPL-3.0-only).
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

public sealed class AdvParameterLoop
{
    readonly JsonNode[] motions;readonly NativeModel model;readonly Dictionary<string,int> indices;
    JsonNode? current;int[] targets=[];float phase,weight,start,target,elapsed,duration;
    public bool Playing=>current is not null;
    public AdvParameterLoop(JsonNode? fadeList,NativeModel model)
    {motions=fadeList?.AsArray().OfType<JsonNode>().ToArray()??[];this.model=model;indices=model.ParameterIds.Select((s,i)=>(s,i)).ToDictionary(x=>x.s,x=>x.i);}
    public void Play(string name,float fade)
    {
        if(!name.StartsWith("misc_",StringComparison.Ordinal))return;
        var m=motions.FirstOrDefault(m=>Archive.S(m,"m_Name",Archive.S(m,"name")).Replace(".fade","",StringComparison.Ordinal)==name);
        if(m is null||Actor.N(m["MotionLength"])<=0||m["ParameterIds"] is not JsonArray ids||ids.Count==0||m["ParameterCurves"] is not JsonArray curves||curves.Count!=ids.Count||curves.Any(c=>c is null))return;
        current=m;targets=ids.Select(v=>indices.GetValueOrDefault(v!.ToString(),-1)).ToArray();phase=elapsed=0;start=weight;target=1;duration=fade;if(fade<=0)weight=1;
    }
    public void Stop(float fade){if(current is null)return;if(fade<=0){current=null;targets=[];phase=weight=0;return;}start=weight;target=0;elapsed=0;duration=fade;}
    public void Step(float dt,float[] values)
    {
        if(current is null)return;phase=(phase+dt)%Actor.N(current["MotionLength"]);
        if(duration<=elapsed)weight=target;else{elapsed+=dt;float k=Math.Clamp(elapsed/duration,0,1);weight=start+(target-start)*(.5f-.5f*MathF.Cos(k*MathF.PI));}
        if(target==0&&weight<=0){Stop(0);return;}
        for(int n=0;n<targets.Length;n++){int i=targets[n];if(i<0)continue;float v=Curve(current["ParameterCurves"]![n]!["m_Curve"]!.AsArray(),phase);values[i]=model.Defaults[i]+(v-model.Defaults[i])*Math.Clamp(weight,0,1);}
    }
    public static float Curve(JsonArray keys,float time)
    {
        if(keys.Count==0)return 0;if(time<=Actor.N(keys[0]!["time"]))return Actor.N(keys[0]!["value"]);
        for(int i=1;i<keys.Count;i++){
            var a=keys[i-1]!;var b=keys[i]!;if(time>Actor.N(b["time"]))continue;float d=Actor.N(b["time"])-Actor.N(a["time"]),t=(time-Actor.N(a["time"]))/d;
            float av=Actor.N(a["value"]),bv=Actor.N(b["value"]),aSlope=Actor.N(a["outSlope"]),bSlope=Actor.N(b["inSlope"]);if(!float.IsFinite(aSlope)||!float.IsFinite(bSlope))return av;
            float t2=t*t,t3=t2*t;return (2*t3-3*t2+1)*av+(t3-2*t2+t)*d*aSlope+(-2*t3+3*t2)*bv+(t3-t2)*d*bSlope;
        }
        return Actor.N(keys[^1]!["value"]);
    }
}
