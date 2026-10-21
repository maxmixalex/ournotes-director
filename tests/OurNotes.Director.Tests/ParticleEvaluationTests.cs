using System.Numerics;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

static class ParticleEvaluationTests
{
    public static void Run(Action<bool,string> assert)
    {
        static bool OriginalBoolean(JsonNode? node)=>node is not null&&node.ToString() is "true" or "1";
        string[] tokens=["null","true","false","0","1","1.0","1e0","-0","-1","\"true\"","\"false\"","\"True\"","\"1\"","\"1.0\"","[]","{}"];
        foreach(string token in tokens){var value=JsonNode.Parse(token);assert(Actor.B(value)==OriginalBoolean(value),"particle boolean preserves raw token "+token);}
        JsonNode?[] typed=[JsonValue.Create(true),JsonValue.Create(false),JsonValue.Create(1),JsonValue.Create(1f),JsonValue.Create(1d),JsonValue.Create(1m),JsonValue.Create("true"),JsonValue.Create("True"),JsonValue.Create("1")];
        foreach(var value in typed)assert(Actor.B(value)==OriginalBoolean(value),"particle boolean preserves typed value "+value);

        // The oracle deliberately retains the old per-sample trigonometry.
        // A float gradient table or a differently rounded angle changes this
        // deterministic field and eventually changes particle trajectories.
        var random=new AdvRandom(0);var permutation=Enumerable.Range(0,256).ToArray();
        for(int i=255;i>0;i--){int j=(int)(random.Next()%(i+1));(permutation[i],permutation[j])=(permutation[j],permutation[i]);}
        var p=Enumerable.Range(0,512).Select(i=>permutation[i&255]).ToArray();
        static double Fade(double t)=>t*t*t*(t*(t*6-15)+10);
        static double Lerp(double a,double b,double t)=>a+(b-a)*t;
        double Original2(double x,double y)
        {
            int ix=(int)Math.Floor(x)&255,iy=(int)Math.Floor(y)&255;double fx=x-Math.Floor(x),fy=y-Math.Floor(y);
            static double G(int h,double dx,double dy){double a=(h&7)*Math.PI/4;return Math.Cos(a)*dx+Math.Sin(a)*dy;}
            return Math.Clamp(Math.Sqrt(2)*Lerp(Lerp(G(p[p[ix]+iy],fx,fy),G(p[p[ix+1]+iy],fx-1,fy),Fade(fx)),Lerp(G(p[p[ix]+iy+1],fx,fy-1),G(p[p[ix+1]+iy+1],fx-1,fy-1),Fade(fx)),Fade(fy)),-1,1);
        }
        Vector3 OriginalNoise(Vector3 q)=>new((float)Original2(q.Y,q.Z),(float)Original2(q.Z+31.416,q.X+17.32),(float)Original2(q.X+57.1,q.Y+91.7));
        static bool Same(float a,float b)=>BitConverter.SingleToInt32Bits(a)==BitConverter.SingleToInt32Bits(b);
        static bool SameVector(Vector3 a,Vector3 b)=>Same(a.X,b.X)&&Same(a.Y,b.Y)&&Same(a.Z,b.Z);
        float[] boundaries=[-16777216,-256.5f,-256,-1,-float.Epsilon,-0f,0,float.Epsilon,.125f,.5f,1,255.5f,256,16777216];
        bool exact=true;
        foreach(float x in boundaries)foreach(float y in boundaries)foreach(float z in boundaries){var q=new Vector3(x,y,z);exact&=SameVector(AdvParticleNoise.Sample(q,1),OriginalNoise(q));}
        random=new(17431);
        for(int i=0;i<20000;i++){var q=new Vector3(random.Value()*4000-2000,random.Value()*4000-2000,random.Value()*4000-2000);exact&=SameVector(AdvParticleNoise.Sample(q,1),OriginalNoise(q));}
        assert(exact,"particle two-dimensional noise retains every float bit across boundaries and 20,000 deterministic samples");

        foreach(int mode in new[]{0,1,2})
        {
            var root=new AdvTransform{Position=new(2,-3,4),Scale=new(2,.5f,-1),Rotation=Quaternion.CreateFromYawPitchRoll(.3f,-.7f,1.2f)};
            var parent=new AdvTransform{Parent=root,Position=new(-4,3,2),Scale=new(1,0,2),Rotation=Quaternion.CreateFromYawPitchRoll(.4f,.1f,-.3f)};
            var transform=new AdvTransform{Parent=parent,Position=new(8,-2,9),Scale=new(-2,3,.125f),Rotation=Quaternion.CreateFromYawPitchRoll(-.2f,1.1f,.8f)};
            var system=new AdvParticles(new JsonObject{["scalingMode"]=mode},new AdvRandom(19));
            bool positions=true;
            for(int i=0;i<30;i++)
            {
                root.Position+=new Vector3(.125f,-.25f,.5f);parent.Rotation=Quaternion.CreateFromYawPitchRoll(i*.025f,-.3f,.2f);
                var expected=transform.WorldPosition;system.SetTransform(transform);positions&=SameVector(system.EmitterPosition,expected);
                // UIParticle rescales this output after SetTransform. The next
                // call must restore the world translation even for a static pose.
                system.EmitterPosition/=4;system.SetTransform(transform);positions&=SameVector(system.EmitterPosition,expected);
            }
            assert(positions,"particle transform translation remains exact and resets canvas scaling, mode "+mode);
        }
    }
}
