using System.Numerics;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

// story/features/effect.js EffectMeshRenderer: submesh i uses material i.
public sealed class AdvEffectMesh
{
    public AdvTransform Transform{get;}
    public bool Enabled{get;}
    public bool Visible=>Enabled&&Transform.ActiveInHierarchy;
    public int SortingLayer{get;}
    public int SortingOrder{get;set;}
    readonly JsonNode[] materials;
    readonly uint[][] indices;
    readonly Dictionary<string,float[]> attributes=[];
    public AdvEffectMesh(JsonNode filter,JsonNode renderer,AdvTransform transform)
    {
        Transform=transform;Enabled=Actor.B(renderer["m_Enabled"]);SortingLayer=(int)Actor.N(renderer["m_SortingLayer"]);SortingOrder=(int)Actor.N(renderer["m_SortingOrder"]);
        var mesh=filter["m_Mesh"]??throw new InvalidDataException("Effect MeshFilter has no mesh");
        var vertices=mesh["vertices"]!.AsArray();int count=vertices.Count;
        var mats=renderer["m_Materials"]?.AsArray();if(mats is null||mats.Count==0||mats.Any(m=>m is null))throw new InvalidDataException("Effect mesh materials missing");
        var parts=mesh["submeshes"]!.AsArray();if(parts.Count>mats.Count)throw new InvalidDataException("More effect submeshes than materials");
        materials=mats.Take(parts.Count).Select(m=>m!).ToArray();indices=parts.Select(p=>p!.AsArray().Select(i=>checked((uint)Actor.N(i))).ToArray()).ToArray();
        if(indices.SelectMany(p=>p).Any(i=>i>=count))throw new InvalidDataException("Effect mesh index outside vertex range");
        void Add(string field,string attribute,int width,string channels){
            if(mesh[field] is not JsonArray data||data.Count!=count)return;
            var values=new float[count*width];for(int i=0;i<count;i++)for(int k=0;k<width;k++)values[i*width+k]=Actor.N(data[i] is JsonArray a?a[k]:data[i]?[channels[k].ToString()]);attributes[attribute]=values;
        }
        Add("vertices","in_POSITION0",3,"xyz");Add("normals","in_NORMAL0",3,"xyz");Add("colors","in_COLOR0",4,"rgba");Add("uv0","in_TEXCOORD0",2,"xy");
        if(!attributes.ContainsKey("in_COLOR0"))attributes["in_COLOR0"]=Enumerable.Repeat(1f,count*4).ToArray();
    }
    public Vector3 WorldCenter()
    {
        var matrix=Transform.WorldMatrix();var vertices=attributes["in_POSITION0"];var lo=new Vector3(float.PositiveInfinity);var hi=new Vector3(float.NegativeInfinity);
        for(int i=0;i<vertices.Length;i+=3){var p=AdvMatrix.Transform(matrix,new(vertices[i],vertices[i+1],vertices[i+2],1));var v=new Vector3(p.X,p.Y,p.Z);lo=Vector3.Min(lo,v);hi=Vector3.Max(hi,v);}
        return vertices.Length==0?Transform.WorldPosition:(lo+hi)*.5f;
    }
    public void Draw(AdvEffectMaterials renderer,IReadOnlyDictionary<string,float[]> globals)
    {
        if(!Visible)return;var matrix=Transform.WorldMatrix();
        for(int i=0;i<indices.Length;i++)renderer.DrawMesh(materials[i],globals,new(attributes,indices[i],matrix,UnityUvs:true));
    }
}
