using System.Numerics;
using System.Text.Json.Nodes;
namespace OurNotes.Director.Core;

internal static class AdvParticleVertexStreams
{
    static float[] V(Vector3 v)=>[v.X,v.Y,v.Z];
    static int Width(int s)=>s switch{0 or 1 or 10 or 14 or 16 or 19 or 25 or 33 or 37=>3,2 or 3 or 26 or 34 or 38=>4,4 or 13 or 24 or 32 or 36=>2,11 or 12 or 15 or 20 or 21 or 22 or 23 or 31 or 35=>1,_=>throw new NotSupportedException("Particle vertex stream: "+s)};
    public static Dictionary<string,float[]> Pack(JsonNode renderer,AdvParticles system,AdvParticles.Particle[] particles,int verticesPerParticle,Dictionary<string,float[]> basic)
    {
        int[] streams=renderer["m_VertexStreams"]!.AsArray().Select(n=>(int)Actor.N(n)).ToArray();if(!streams.Contains(0))throw new InvalidDataException("Particle streams need Position");
        int vertices=particles.Length*verticesPerParticle;var output=new Dictionary<string,float[]>();
        string[] standard=["in_POSITION0","in_NORMAL0","in_TANGENT0","in_COLOR0"];
        foreach(int s in streams.Where(s=>s<=3).Distinct()){
            _=Width(s);if(s==2){var tangent=new float[vertices*4];for(int i=0;i<vertices;i++){tangent[i*4]=1;tangent[i*4+3]=1;}output[standard[s]]=tangent;}else output[standard[s]]=basic[standard[s]];
        }
        var texStreams=streams.Where(s=>s>3).ToArray();int stride=texStreams.Sum(Width);for(int i=0;i*4<stride;i++)output["in_TEXCOORD"+i]=new float[vertices*Math.Min(4,stride-i*4)];
        // Resolve destinations once per mesh and particle values once per particle, not per vertex.
        var destinations=Enumerable.Range(0,stride).Select(o=>(Data:output["in_TEXCOORD"+(o/4)],Width:Math.Min(4,stride-o/4*4),Offset:o%4)).ToArray();
        var values=new float[stride];var uvOffsets=new List<int>();var vertexOffsets=new List<int>();int streamOffset=0;
        foreach(int s in texStreams){if(s==4)uvOffsets.Add(streamOffset);if(s==11)vertexOffsets.Add(streamOffset);streamOffset+=Width(s);}
        for(int pi=0;pi<particles.Length;pi++){
            var p=particles[pi];var size=V(system.Size(p));var velocity=p.Velocity+p.Animated;var custom1=system.CustomData(p,0);var custom2=system.CustomData(p,1);
            int offset=0;
            foreach(int s in texStreams){
                if(s==4){offset+=2;continue;}
                float[] data=s switch{10=>V(p.Position),11=>[0],12 or 13 or 14=>size,15=>[p.Rotation.Z],16=>V(p.Rotation),19=>V(velocity),20=>[velocity.Length()],21=>[Math.Clamp(p.Age/p.Life,0,1)],22=>[1/p.Life],>=23 and <=26=>p.Stable,>=31 and <=34=>custom1,>=35 and <=38=>custom2,_=>throw new NotSupportedException("Particle vertex stream: "+s)};
                for(int k=0;k<Width(s);k++)values[offset++]=data[k];
            }
            for(int vi=0;vi<verticesPerParticle;vi++){
                int vertex=pi*verticesPerParticle+vi;
                foreach(int uvOffset in uvOffsets){values[uvOffset]=basic["in_TEXCOORD0"][vertex*2];values[uvOffset+1]=1-basic["in_TEXCOORD0"][vertex*2+1];}
                foreach(int vertexOffset in vertexOffsets)values[vertexOffset]=vi;
                for(int k=0;k<stride;k++){var d=destinations[k];d.Data[vertex*d.Width+d.Offset]=values[k];}
            }
        }
        return output;
    }
}
