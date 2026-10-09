using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

// Particle AnimationCurve / MinMaxCurve / MinMaxGradient, matching engine/particles.js.
public static class AdvParticleCurve
{
    public static float Curve(JsonNode? raw,float time)
    {
        var keys=raw?["m_Curve"]?.AsArray();if(keys is null||keys.Count==0)return 0;if(keys.Count==1)return Actor.N(keys[0]!["value"]);
        double start=Actor.N(keys[0]!["time"]),end=Actor.N(keys[^1]!["time"]),t=time,length=end-start;
        if(t<start||t>end){int mode=(int)Actor.N(raw![t<start?"m_PreInfinity":"m_PostInfinity"],2);if(length<=0)t=start;else if(mode==1)t=start+((t-start)%length+length)%length;else if(mode==0){double r=Math.Abs(t-start)%(length*2);t=start+(r>length?length*2-r:r);}else t=Math.Clamp(t,start,end);}
        if(t<=start)return Actor.N(keys[0]!["value"]);if(t>=end)return Actor.N(keys[^1]!["value"]);
        int index=1;while(Actor.N(keys[index]!["time"])<t)index++;var a=keys[index-1]!;var b=keys[index]!;
        double av=Actor.N(a["value"]),bv=Actor.N(b["value"]),dx=Actor.N(b["time"])-Actor.N(a["time"]),outSlope=Actor.N(a["outSlope"]),inSlope=Actor.N(b["inSlope"]);if(!double.IsFinite(outSlope)||!double.IsFinite(inSlope))return (float)av;
        double s=(t-Actor.N(a["time"]))/dx;bool weighted=((int)Actor.N(a["weightedMode"])&2)!=0||((int)Actor.N(b["weightedMode"])&1)!=0;
        if(!weighted){double s2=s*s,s3=s2*s;return (float)((2*s3-3*s2+1)*av+(s3-2*s2+s)*outSlope*dx+(-2*s3+3*s2)*bv+(s3-s2)*inSlope*dx);}
        double ow=((int)Actor.N(a["weightedMode"])&2)!=0?Actor.N(a["outWeight"]):1d/3,iw=((int)Actor.N(b["weightedMode"])&1)!=0?Actor.N(b["inWeight"]):1d/3;
        double x1=ow,x2=1-iw,lo=0,hi=1,u=s;
        for(int i=0;i<40;i++){double m=1-u,x=3*m*m*u*x1+3*m*u*u*x2+u*u*u-s;if(Math.Abs(x)<1e-7)break;if(x>0)hi=u;else lo=u;double d=3*m*m*x1+6*m*u*(x2-x1)+3*u*u*(1-x2),next=d>1e-9?u-x/d:(lo+hi)/2;u=next>lo&&next<hi?next:(lo+hi)/2;}
        double v=1-u,y1=av+ow*dx*outSlope,y2=bv-iw*dx*inSlope;return (float)(v*v*v*av+3*v*v*u*y1+3*v*u*u*y2+u*u*u*bv);
    }
    public static float Value(JsonNode? raw,float time,float random=1)
    {
        if(raw is null)return 0;float max=Actor.N(raw["scalar"]),r=Math.Clamp(random,0,1);return (int)Actor.N(raw["minMaxState"]) switch{
            0=>max,1=>Curve(raw["maxCurve"],time)*max,2=>(Curve(raw["minCurve"],time)+(Curve(raw["maxCurve"],time)-Curve(raw["minCurve"],time))*r)*max,
            3=>Actor.N(raw["minScalar"])+(max-Actor.N(raw["minScalar"]))*r,_=>throw new NotSupportedException("Unknown particle curve mode")};
    }
    public static float[] Color(JsonNode raw,float time,float random=1)
    {
        static float[] Read(JsonNode? c)=>new[]{"r","g","b","a"}.Select(k=>Actor.N(c?[k],1)).ToArray();
        float r=Math.Clamp(random,0,1);float[] Mix(float[] a,float[] b)=>Enumerable.Range(0,4).Select(i=>a[i]+(b[i]-a[i])*r).ToArray();
        return (int)Actor.N(raw["minMaxState"]) switch{0=>Read(raw["maxColor"]),1=>AdvAnimation.Gradient(raw["maxGradient"]!,time),2=>Mix(Read(raw["minColor"]),Read(raw["maxColor"])),3=>Mix(AdvAnimation.Gradient(raw["minGradient"]!,time),AdvAnimation.Gradient(raw["maxGradient"]!,time)),4=>AdvAnimation.Gradient(raw["maxGradient"]!,r),_=>throw new NotSupportedException("Unknown particle gradient mode")};
    }
}
