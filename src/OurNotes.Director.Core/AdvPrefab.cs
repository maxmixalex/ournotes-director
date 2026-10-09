// Adapted from ournotes-player engine/prefab.js and engine/math.js (AGPL-3.0-only).
using System.Numerics;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

public sealed class AdvTransform
{
    public AdvTransform? Parent{get;set;}
    public Vector3 Position{get;set;}
    public Quaternion Rotation{get;set;}=Quaternion.Identity;
    public Vector3 Scale{get;set;}=Vector3.One;
    public bool Active{get;set;}=true;
    public float[] LocalMatrix()
    {
        double x=Rotation.X,y=Rotation.Y,z=Rotation.Z,w=Rotation.W;
        var m=AdvMatrix.Identity();
        m[0]=(float)((1-2*(y*y+z*z))*Scale.X);m[1]=(float)(2*(x*y+z*w)*Scale.X);m[2]=(float)(2*(x*z-y*w)*Scale.X);
        m[4]=(float)(2*(x*y-z*w)*Scale.Y);m[5]=(float)((1-2*(x*x+z*z))*Scale.Y);m[6]=(float)(2*(y*z+x*w)*Scale.Y);
        m[8]=(float)(2*(x*z+y*w)*Scale.Z);m[9]=(float)(2*(y*z-x*w)*Scale.Z);m[10]=(float)((1-2*(x*x+y*y))*Scale.Z);
        m[12]=Position.X;m[13]=Position.Y;m[14]=Position.Z;return m;
    }
    public float[] WorldMatrix()=>Parent is null?LocalMatrix():AdvMatrix.Multiply(Parent.WorldMatrix(),LocalMatrix());
    public Vector3 WorldPosition{get{var m=WorldMatrix();return new(m[12],m[13],m[14]);}}
    public bool ActiveInHierarchy=>Active&&(Parent?.ActiveInHierarchy??true);
    public void SetEuler(Vector3 degrees){const float radians=MathF.PI/180;Rotation=Quaternion.CreateFromYawPitchRoll(degrees.Y*radians,degrees.X*radians,degrees.Z*radians);}
}

public sealed class AdvPrefab
{
    readonly Dictionary<string,(JsonNode node,AdvTransform transform)> nodes=new(StringComparer.Ordinal);
    readonly List<AdvTransform> roots=[];
    public AdvTransform Root=>roots.Count==1?roots[0]:throw new InvalidDataException("Prefab must have one root");
    public static Vector3 Vector(JsonNode? node,Vector3 fallback=default)=>node is null?fallback:new(Actor.N(node["x"]),Actor.N(node["y"]),Actor.N(node["z"]));
    public AdvPrefab(JsonNode exported)
    {
        foreach(var node in exported["nodes"]!.AsArray().OfType<JsonNode>()){
            var path=Archive.S(node,"path");int cut=path.LastIndexOf('/');
            var rotation=node["localRotation"];
            var t=new AdvTransform{Parent=cut<0?null:Transform(path[..cut]),Position=Vector(node["localPosition"]),Scale=Vector(node["localScale"],Vector3.One),Rotation=rotation is null?Quaternion.Identity:new(Actor.N(rotation["x"]),Actor.N(rotation["y"]),Actor.N(rotation["z"]),Actor.N(rotation["w"],1)),Active=node["active"] is null||Actor.B(node["active"])};
            nodes.Add(path,(node,t));if(cut<0)roots.Add(t);
        }
    }
    public AdvTransform Transform(string path)=>nodes.TryGetValue(path,out var entry)?entry.transform:throw new InvalidDataException("Missing prefab transform: "+path);
    public AdvTransform? TryTransform(string path)=>nodes.TryGetValue(path,out var entry)?entry.transform:null;
    public JsonNode Component(string path,string name)=>nodes[path].node["components"]!.AsArray().OfType<JsonNode>().Single(c=>Archive.S(c,"class",Archive.S(c,"type"))==name);
    public (string Path,JsonNode Component) Find(string name)=>nodes.SelectMany(pair=>pair.Value.node["components"]!.AsArray().OfType<JsonNode>().Where(c=>Archive.S(c,"class",Archive.S(c,"type"))==name).Select(c=>(pair.Key,c))).Single();
}
