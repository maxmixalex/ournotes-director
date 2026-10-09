// Unity Random and DOTween shake waypoints, ported from ournotes-player (AGPL-3.0-only).
using System.Numerics;

namespace OurNotes.Director.Core;

public sealed class AdvRandom
{
    uint x,y,z,w;
    public AdvRandom(uint seed){x=seed;y=unchecked(1812433253*x+1);z=unchecked(1812433253*y+1);w=unchecked(1812433253*z+1);}
    public uint Next(){uint t=x^(x<<11);x=y;y=z;z=w;return w=w^(w>>19)^t^(t>>8);}
    public float Value()=>(Next()&0x7FFFFF)/8388607f;
    public float Range(float min,float max){float t=Value();return min*t+(1-t)*max;}
}
public static class AdvShake
{
    static float Length(Vector3 v)=>MathF.Sqrt((v.X*v.X+v.Y*v.Y)+v.Z*v.Z);
    static Vector3 Clamp(Vector3 v,float max)=>v.LengthSquared()<=max*max?v:v/Length(v)*max;
    public static (Vector3[] Ends,float[] Durations) Waypoints(double duration,Vector3 strength,int vibrato,float randomness,bool fadeOut,AdvRandom random,bool vectorBased=true,bool harmonic=false)
    {
        int n=Math.Max(2,(int)(float)(vibrato*duration));if(n>100000)throw new ArgumentException("Shake has too many waypoints");
        float magnitude=vectorBased?Length(strength):strength.X,decay=fadeOut?magnitude/n:0,sum=0;var times=new float[n];var ends=new Vector3[n];
        for(int i=0;i<n;i++){times[i]=fadeOut?(float)(((i+1)/(float)n)*duration):(float)(duration/n);sum+=times[i];}for(int i=0;i<n;i++)times[i]*=(float)(duration/sum);
        float angle=random.Range(0,360);
        for(int i=0;i<n-1;i++){
            float low=harmonic?0:-randomness;
            if(i>0)angle=(angle-180)+random.Range(low,randomness);
            float rotation=random.Range(low,randomness)*.017453292f*.5f,qy=MathF.Sin(rotation),qw=MathF.Cos(rotation),a=angle*.017453292f;
            float vx=magnitude*MathF.Cos(a),vy=magnitude*MathF.Sin(a);var v=new Vector3((1-2*qy*qy)*vx,vy,-2*qw*qy*vx);
            if(vectorBased){v=v with{X=Clamp(v,strength.X).X};v=v with{Y=Clamp(v,strength.Y).Y};v=v with{Z=Clamp(v,strength.Z).Z};float m=Length(v);v=m>1e-5?v/m*magnitude:Vector3.Zero;magnitude-=decay;strength=Clamp(strength,magnitude);}else magnitude-=decay;
            ends[i]=v;
        }
        return(ends,times);
    }
    public static AdvTween? Start(AdvTweens tweens,AdvRandom random,Func<Vector3> get,Action<Vector3> set,float duration,Vector3 strength,int vibrato=10,float randomness=90,bool fadeOut=true,Action? update=null,bool vectorBased=true)
    {
        if(duration<=0)return null;var(ends,times)=Waypoints(duration,strength,vibrato,randomness,fadeOut,random,vectorBased);Vector3 start=default;float total=0;foreach(float t in times)total+=t;
        return tweens.To(()=>{start=get();return 0;},total,total,AdvEase.Linear,elapsed=>{
            float at=0;Vector3 previous=Vector3.Zero;for(int i=0;i<times.Length;i++){if(elapsed<=at+times[i]||i==times.Length-1){float k=times[i]>0?(elapsed-at)/times[i]:1;set(start+previous+(ends[i]-previous)*k);break;}at+=times[i];previous=ends[i];}update?.Invoke();
        });
    }
}
