using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

// Owned by one canvas particle system, and cleared after every synchronous
// simulation step. The host's transform/activity callbacks do not edit Data in
// that scope. Mutable animator values are therefore read again on the next step.
// Standalone AdvParticles and the public curve evaluator keep their dynamic API.
internal sealed class AdvParticleCurveCache
{
    internal const int EntryLimit=128,CurveKeyLimit=128,TotalKeyLimit=4096;
    readonly Dictionary<JsonNode,ValueCurve> values=new(ReferenceEqualityComparer.Instance);
    internal int Count=>values.Count;
    internal int KeyCount{get;private set;}

    internal void Clear(){values.Clear();KeyCount=0;}

    internal float Value(JsonNode? raw,float time,float random)
    {
        if(raw is null)return 0;
        if(values.TryGetValue(raw,out var cached))return cached.Mode<0?AdvParticleCurve.Value(raw,time,random):cached.Sample(time,random);
        if(values.Count>=EntryLimit)return AdvParticleCurve.Value(raw,time,random);
        if(TryCompile(raw,out var compiled)&&KeyCount+compiled.KeyCount<=TotalKeyLimit)
        {
            values.Add(raw,compiled);KeyCount+=compiled.KeyCount;
            return compiled.Sample(time,random);
        }
        // Remember the fallback for this step too, so an oversized or malformed
        // curve does not repeatedly allocate speculative snapshots per particle.
        values.Add(raw,new(-1,0,0,null,null));
        return AdvParticleCurve.Value(raw,time,random);
    }

    static bool TryCompile(JsonNode raw,out ValueCurve result)
    {
        result=default;
        if(raw is not JsonObject)return false;
        try
        {
            float scalar=Actor.N(raw["scalar"]);int mode=(int)Actor.N(raw["minMaxState"]);
            float minimumScalar=0;Curve? minimum=null,maximum=null;
            switch(mode)
            {
                case 0:break;
                case 1:if(!Curve.TryRead(raw["maxCurve"],out maximum))return false;break;
                case 2:if(!Curve.TryRead(raw["minCurve"],out minimum)||!Curve.TryRead(raw["maxCurve"],out maximum))return false;break;
                case 3:minimumScalar=Actor.N(raw["minScalar"]);break;
                default:return false;
            }
            result=new(mode,scalar,minimumScalar,minimum,maximum);return true;
        }
        // A malformed unused key/field must not fail earlier just because a
        // snapshot reads it. Preserve the original evaluator's lazy validation.
        catch(Exception error) when(error is FormatException or InvalidOperationException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    readonly record struct ValueCurve(int Mode,float Scalar,float MinimumScalar,Curve? Minimum,Curve? Maximum)
    {
        internal int KeyCount=>(Minimum?.Count??0)+(Maximum?.Count??0);
        internal float Sample(float time,float random)
        {
            float r=Math.Clamp(random,0,1);
            return Mode switch
            {
                0=>Scalar,
                1=>Maximum!.Sample(time)*Scalar,
                2=>(Minimum!.Sample(time)+(Maximum!.Sample(time)-Minimum.Sample(time))*r)*Scalar,
                3=>MinimumScalar+(Scalar-MinimumScalar)*r,
                _=>throw new InvalidOperationException("Invalid compiled particle curve")
            };
        }
    }

    sealed class Curve(Key[] keys,int preInfinity,int postInfinity)
    {
        static readonly Curve empty=new([],2,2);
        internal int Count=>keys.Length;

        internal static bool TryRead(JsonNode? raw,out Curve? curve)
        {
            curve=null;
            if(raw is null){curve=empty;return true;}
            if(raw is not JsonObject)return false;
            var source=raw["m_Curve"];
            if(source is null){curve=empty;return true;}
            if(source is not JsonArray items||items.Count>CurveKeyLimit)return false;
            if(items.Count==0){curve=empty;return true;}
            if(items.Count==1)
            {
                if(items[0] is not JsonObject single)return false;
                curve=new([new(0,Actor.N(single["value"]),0,0,0,0,0)],2,2);return true;
            }
            var keys=new Key[items.Count];
            for(int i=0;i<keys.Length;i++)
            {
                if(items[i] is not JsonObject key)return false;
                keys[i]=new(Actor.N(key["time"]),Actor.N(key["value"]),Actor.N(key["outSlope"]),Actor.N(key["inSlope"]),
                    (int)Actor.N(key["weightedMode"]),Actor.N(key["outWeight"]),Actor.N(key["inWeight"]));
            }
            curve=new(keys,(int)Actor.N(raw["m_PreInfinity"],2),(int)Actor.N(raw["m_PostInfinity"],2));return true;
        }

        internal float Sample(float time)
        {
            if(keys.Length==0)return 0;if(keys.Length==1)return keys[0].Value;
            double start=keys[0].Time,end=keys[^1].Time,t=time,length=end-start;
            if(t<start||t>end)
            {
                int mode=t<start?preInfinity:postInfinity;
                if(length<=0)t=start;
                else if(mode==1)t=start+((t-start)%length+length)%length;
                else if(mode==0){double r=Math.Abs(t-start)%(length*2);t=start+(r>length?length*2-r:r);}
                else t=Math.Clamp(t,start,end);
            }
            if(t<=start)return keys[0].Value;if(t>=end)return keys[^1].Value;
            int index=1;while(keys[index].Time<t)index++;var a=keys[index-1];var b=keys[index];
            // Time subtraction is float arithmetic in the source evaluator,
            // then widened to double. Reassociating it changes particle paths.
            double av=a.Value,bv=b.Value,dx=b.Time-a.Time,outSlope=a.OutSlope,inSlope=b.InSlope;
            if(!double.IsFinite(outSlope)||!double.IsFinite(inSlope))return(float)av;
            double s=(t-a.Time)/dx;bool weighted=(a.WeightedMode&2)!=0||(b.WeightedMode&1)!=0;
            if(!weighted)
            {
                double s2=s*s,s3=s2*s;
                return(float)((2*s3-3*s2+1)*av+(s3-2*s2+s)*outSlope*dx+(-2*s3+3*s2)*bv+(s3-s2)*inSlope*dx);
            }
            double ow=(a.WeightedMode&2)!=0?a.OutWeight:1d/3,iw=(b.WeightedMode&1)!=0?b.InWeight:1d/3;
            double x1=ow,x2=1-iw,lo=0,hi=1,u=s;
            for(int i=0;i<40;i++)
            {
                double m=1-u,x=3*m*m*u*x1+3*m*u*u*x2+u*u*u-s;
                if(Math.Abs(x)<1e-7)break;
                if(x>0)hi=u;else lo=u;
                double d=3*m*m*x1+6*m*u*(x2-x1)+3*u*u*(1-x2),next=d>1e-9?u-x/d:(lo+hi)/2;
                u=next>lo&&next<hi?next:(lo+hi)/2;
            }
            double v=1-u,y1=av+ow*dx*outSlope,y2=bv-iw*dx*inSlope;
            return(float)(v*v*v*av+3*v*v*u*y1+3*v*u*u*y2+u*u*u*bv);
        }
    }

    readonly record struct Key(float Time,float Value,float OutSlope,float InSlope,int WeightedMode,float OutWeight,float InWeight);
}
