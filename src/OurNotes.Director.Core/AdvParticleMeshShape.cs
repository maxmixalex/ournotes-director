using System.Numerics;
using System.Text.Json.Nodes;
namespace OurNotes.Director.Core;

// Shuriken Mesh shape: uniform vertices, length-weighted edges, area-weighted triangles.
internal sealed class AdvParticleMeshShape
{
    readonly Vector3[] points,normals;
    readonly List<(int A,int B,int C)> triangles=[];
    readonly List<(int A,int B)> edges=[];
    readonly List<double> areas=[],lengths=[];
    readonly int placement;readonly float offset;
    static Vector3 Unit(Vector3 v)=>v.LengthSquared()>1e-24f?Vector3.Normalize(v):Vector3.Zero;
    public AdvParticleMeshShape(JsonNode shape)
    {
        var mesh=shape["m_Mesh"]??throw new InvalidDataException("Particle shape mesh missing");
        if(Actor.N(shape["m_MeshSpawn"]?["mode"])!=0||Actor.B(shape["m_UseMeshColors"])&&mesh["colors"] is not null)throw new NotSupportedException("Particle mesh spawn mode or mesh colors unsupported by reference");
        placement=(int)Actor.N(shape["placementMode"]);offset=Actor.N(shape["m_MeshNormalOffset"]);
        if(placement is <0 or >2)throw new NotSupportedException("Particle mesh placement: "+placement);
        static Vector3 Read(JsonNode? n)=>new(Actor.N(n?[0]),Actor.N(n?[1]),n?.AsArray().Count>2?Actor.N(n[2]):0);
        points=mesh["vertices"]?.AsArray().Select(Read).ToArray()??[];if(points.Length==0)throw new InvalidDataException("Particle shape mesh has no vertices");
        normals=mesh["normals"]?.AsArray().Select(Read).ToArray()??Enumerable.Repeat(Vector3.UnitZ,points.Length).ToArray();
        if(normals.Length!=points.Length)throw new InvalidDataException("Particle shape normals do not match vertices");
        var subs=mesh["submeshes"]?.AsArray()??new JsonArray();IEnumerable<JsonNode?> selected=subs;
        if(Actor.B(shape["m_UseMeshMaterialIndex"])){int index=(int)Actor.N(shape["m_MeshMaterialIndex"]);selected=index>=0&&index<subs.Count?[subs[index]]:[];}
        double area=0,length=0;
        foreach(var sub in selected.OfType<JsonArray>())for(int i=0;i+2<sub.Count;i+=3){
            int a=(int)Actor.N(sub[i]),b=(int)Actor.N(sub[i+1]),c=(int)Actor.N(sub[i+2]);
            if(new[]{a,b,c}.Any(k=>k<0||k>=points.Length))throw new InvalidDataException("Particle shape mesh index out of range");
            triangles.Add((a,b,c));area+=.5*Vector3.Cross(points[b]-points[a],points[c]-points[a]).Length();areas.Add(area);
            foreach(var(x,y)in new[]{(a,b),(b,c),(c,a)}){edges.Add((x,y));length+=Vector3.Distance(points[x],points[y]);lengths.Add(length);}
        }
        if(placement!=0&&triangles.Count==0)throw new InvalidDataException("Particle shape mesh has no triangles");
    }
    public (Vector3 Position,Vector3 Direction) Sample(AdvRandom random)
    {
        int Pick(List<double> cumulative){double value=random.Value()*cumulative[^1];int lo=0,hi=cumulative.Count-1;while(lo<hi){int mid=(lo+hi)/2;if(cumulative[mid]>value)hi=mid;else lo=mid+1;}return lo;}
        Vector3 p,d;
        if(placement==0){int i=Math.Min(points.Length-1,(int)(random.Value()*points.Length));p=points[i];d=Unit(normals[i]);}
        else if(placement==1){var(a,b)=edges[Pick(lengths)];float u=random.Value();p=Vector3.Lerp(points[a],points[b],u);d=Unit(Vector3.Lerp(normals[a],normals[b],u));}
        else{var(a,b,c)=triangles[Pick(areas)];float u=random.Value(),v=random.Value();if(u+v>1){u=1-u;v=1-v;}
            Vector3 Bary(Vector3[] values)=>values[a]+((values[b]-values[a])*u+(values[c]-values[a])*v);
            p=Bary(points);d=Unit(Bary(normals));}
        return(p+d*offset,d);
    }
}
