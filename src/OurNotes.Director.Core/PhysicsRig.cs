// Adapted from ournotes-player/src/live2d/physics.js, AGPL-3.0-only.
using System.Numerics;
using System.Text.Json.Nodes;
using static OurNotes.Director.Core.Actor;

namespace OurNotes.Director.Core;

public sealed class PhysicsRig
{
    sealed class Particle {public Vector2 Position,Last,Velocity,Gravity;public float Mobility,Delay,Acceleration,Radius;}
    sealed class Sub {public JsonNode Data=null!;public Particle[] Parts=[];public float[] Current=[],Previous=[];}
    readonly Sub[] subs;readonly NativeModel core;readonly Dictionary<string,int> ids;readonly Vector2 gravity,wind;readonly float fps;float remain;readonly float[] cache,input;
    public PhysicsRig(JsonNode rig,NativeModel core,Dictionary<string,int> ids)
    {
        this.core=core;this.ids=ids;gravity=Vector(rig["Gravity"]!);wind=Vector(rig["Wind"]!);fps=N(rig["Fps"]);cache=new float[core.ParameterIds.Length];input=new float[cache.Length];
        subs=rig["SubRigs"]!.AsArray().OfType<JsonNode>().Select(s=>{var p=s["Particles"]!.AsArray().Select(p=>new Particle{Mobility=N(p!["Mobility"]),Delay=N(p["Delay"]),Acceleration=N(p["Acceleration"]),Radius=N(p["Radius"]),Gravity=new(gravity.X,-gravity.Y)}).ToArray();for(int i=1;i<p.Length;i++)p[i].Last=p[i].Position=new(0,p[i-1].Position.Y+p[i].Radius);return new Sub{Data=s,Parts=p,Current=new float[s["Output"]!.AsArray().Count],Previous=new float[s["Output"]!.AsArray().Count]};}).ToArray();
    }
    static Vector2 Vector(JsonNode n)=>new(N(n["x"]),N(n["y"]));
    static float Wrap(float f){while(f< -MathF.PI)f+=MathF.Tau;while(f>MathF.PI)f-=MathF.Tau;return f;}
    float Normalize(int i,float[] values,JsonNode norm,bool inverse)
    {
        var v=values[i]=Math.Clamp(values[i],core.Min[i],core.Max[i]);var mid=(core.Max[i]+core.Min[i])*.5f;var delta=v>=mid?core.Max[i]-mid:core.Min[i]-mid;float r=delta==0?0:(N(norm[v>=mid?"Maximum":"Minimum"])-N(norm["Default"]))/delta*(v-mid)+N(norm["Default"]);return inverse?r:-r;
    }
    (Vector2,float) Inputs(Sub s,float[] values)
    {
        Vector2 t=new();float a=0;foreach(var i in s.Data["Input"]!.AsArray().OfType<JsonNode>())if(ids.TryGetValue(Archive.S(i,"SourceId"),out int src)){int comp=(int)N(i["SourceComponent"]);float v=Normalize(src,values,s.Data["Normalization"]![comp==2?"Angle":"Position"]!,B(i["IsInverted"]))*N(i["Weight"])/100;if(comp==2)a+=v;else if(comp==1)t.Y+=v;else t.X+=v;}float angle=a/-180*MathF.PI,c=MathF.Cos(angle),sn=MathF.Sin(angle);t.X=t.X*c-t.Y*sn;t.Y=t.Y*c+sn*t.X;return(t,a);
    }
    void Particles(Sub s,Vector2 translation,float angle,float dt)
    {
        var p=s.Parts;p[0].Position=translation;var g=new Vector2(MathF.Sin(angle/180*MathF.PI),MathF.Cos(angle/180*MathF.PI));var ga=MathF.Atan2(g.Y,g.X);float threshold=N(s.Data["Normalization"]!["Position"]!["Maximum"])*.001f;
        for(int i=1;i<p.Length;i++){var v=p[i];var parent=p[i-1];var force=wind+g*v.Acceleration;v.Last=v.Position;float rad=Wrap(ga-MathF.Atan2(v.Gravity.Y,v.Gravity.X))/5,delay=v.Delay*dt*30;var diff=v.Position-parent.Position;float c=MathF.Cos(rad),sn=MathF.Sin(rad);float nx=diff.X*c-diff.Y*sn,ny=diff.Y*c+sn*nx;var pos=parent.Position+new Vector2(nx,ny)+v.Velocity*delay+force*delay*delay;var direction=pos-parent.Position;float length=direction.Length();pos=parent.Position+(length>1e-5?direction/length:Vector2.Zero)*v.Radius;if(MathF.Abs(pos.X)<threshold)pos.X=0;v.Position=pos;if(delay!=0)v.Velocity=(pos-v.Last)/delay*v.Mobility;v.Gravity=g;}
    }
    float Output(Sub s,JsonNode o)
    {
        int i=(int)N(o["ParticleIndex"]),comp=(int)N(o["SourceComponent"]);if(i<=0||i>=s.Parts.Length)return 0;var t=s.Parts[i].Position-s.Parts[i-1].Position;float v;if(comp==0)v=t.X;else if(comp==1)v=t.Y;else {var parent=i>=2?s.Parts[i-1].Position-s.Parts[i-2].Position:new Vector2(gravity.X,-gravity.Y);v=Wrap(MathF.Atan2(t.Y,t.X)-MathF.Atan2(parent.Y,parent.X));}return B(o["IsInverted"])?-v:v;
    }
    void Write(JsonNode o,float[] values,float value)
    {
        if(!ids.TryGetValue(Archive.S(o,"DestinationId"),out var i))return;int comp=(int)N(o["SourceComponent"]);float scale=comp==2?N(o["AngleScale"]):N(o["TranslationScale"]![comp==0?"x":"y"]);float v=Math.Clamp(value*scale,core.Min[i],core.Max[i]);float w=N(o["Weight"])/100;values[i]=w<1?v*w+(1-w)*values[i]:v;
    }
    public void Evaluate(float[] values,float dt)
    {
        if(dt<=0)return;remain+=dt;if(remain>5)remain=0;float step=fps>0?1/fps:dt;
        while(step<=remain){float w=step/remain;for(int i=0;i<values.Length;i++)cache[i]=input[i]=(1-w)*input[i]+w*values[i];foreach(var s in subs){var(t,a)=Inputs(s,cache);Particles(s,t,a,step);var outputs=s.Data["Output"]!.AsArray();for(int i=0;i<outputs.Count;i++){s.Previous[i]=s.Current[i];s.Current[i]=Output(s,outputs[i]!);Write(outputs[i]!,cache,s.Current[i]);}}remain-=step;}
        float alpha=remain/step;foreach(var s in subs){var outputs=s.Data["Output"]!.AsArray();for(int i=0;i<outputs.Count;i++)Write(outputs[i]!,values,(1-alpha)*s.Previous[i]+alpha*s.Current[i]);}
    }
    public void Stabilize(float[] values)
    {
        Array.Copy(values,cache,values.Length);Array.Copy(values,input,values.Length);
        foreach(var s in subs){var (translation,angle)=Inputs(s,values);
            foreach(var source in s.Data["Input"]!.AsArray())if(ids.TryGetValue(Archive.S(source!,"SourceId"),out int i))cache[i]=values[i];
            var parts=s.Parts;parts[0].Position=translation;var direction=new Vector2(MathF.Sin(angle/180*MathF.PI),MathF.Cos(angle/180*MathF.PI));float threshold=N(s.Data["Normalization"]!["Position"]!["Maximum"])*.001f;
            for(int i=1;i<parts.Length;i++){var p=parts[i];p.Last=p.Position;p.Velocity=Vector2.Zero;var force=wind+direction*p.Acceleration;float length=force.Length();p.Position=parts[i-1].Position+(length>1e-5?force/length:Vector2.Zero)*p.Radius;if(Math.Abs(p.Position.X)<threshold)p.Position.X=0;p.Gravity=direction;}
            var outputs=s.Data["Output"]!.AsArray();for(int i=0;i<outputs.Count;i++){s.Current[i]=s.Previous[i]=Output(s,outputs[i]!);Write(outputs[i]!,values,s.Current[i]);if(ids.TryGetValue(Archive.S(outputs[i]!,"DestinationId"),out int destination))cache[destination]=values[destination];}
        }
    }
}
