using System.Numerics;

namespace OurNotes.Director.Core;

// DOTween Punch / Vector3ArrayPlugin waypoint construction.
public static class AdvPunch
{
    public static (Vector3[] Ends,float[] Durations) Waypoints(double duration,Vector3 direction,int vibrato,float elasticity)
    {
        int count=Math.Max(2,(int)(float)(vibrato*duration));
        if(count>100000)throw new ArgumentException("Punch has too many waypoints");
        float sq=(direction.X*direction.X+direction.Y*direction.Y)+direction.Z*direction.Z;
        float strength=MathF.Sqrt(sq),decay=strength/count,sum=0;
        var normal=direction/strength;var ends=new Vector3[count];var times=new float[count];
        Vector3 Clamp(float length)=>length*length<sq?normal*length:direction;
        for(int i=0;i<count;i++){times[i]=(float)(((i+1)/(float)count)*duration);sum+=times[i];}
        for(int i=0;i<count;i++)times[i]=(float)(duration/sum)*times[i];
        for(int i=0;i<count-1;i++){
            ends[i]=i==0?direction:i%2==1?-Clamp(strength*Math.Clamp(elasticity,0,1)):Clamp(strength);
            strength-=decay;
        }
        return(ends,times);
    }
}
