using System.Numerics;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

// Coffee UIParticle camera-canvas host. Simulation advances once per ADV frame, never per readback.
public sealed class AdvCanvasParticles
{
    sealed class Group(AdvCanvasNode node,JsonNode data){public AdvCanvasNode Node=node;public JsonNode Data=data;public bool Active;public Vector3 SavedScale;public List<AdvCanvasNode> Systems=[];public HashSet<AdvCanvasNode> Prewarm=[];}
    readonly IReadOnlyDictionary<string,AdvCanvasNode> nodes;readonly List<Group> groups=[];
    readonly OffscreenGl gl;readonly UnityShaderLibrary shaders;readonly Func<JsonNode,uint> texture;
    readonly float width,height,canvasScale;int lastFrame=-1;
    readonly Dictionary<AdvCanvasNode,AdvTransform> transforms=[];
    public AdvCanvasParticles(IReadOnlyDictionary<string,AdvCanvasNode> nodes,AdvRandom random,float width,float height,float cameraSize,OffscreenGl gl,UnityShaderLibrary shaders,Func<JsonNode,uint> texture,float planeDistance=100)
    {
        this.nodes=nodes;this.width=width;this.height=height;canvasScale=2*cameraSize/height;this.gl=gl;this.shaders=shaders;this.texture=texture;
        var canvasTransform=new AdvTransform{Position=new(-width*canvasScale/2,-height*canvasScale/2,planeDistance),Scale=new(canvasScale)};
        foreach(var node in nodes.Values)transforms[node]=new(){Parent=node.Parent is null?canvasTransform:transforms[node.Parent]};
        foreach(var node in nodes.Values)if(node.Component("ParticleSystem") is JsonNode component){if(Actor.N(component["moveWithTransform"]) is not (0 or 1))throw new NotSupportedException("UI particle custom simulation space");node.Particles=new(component,random);}
        foreach(var node in nodes.Values)if(node.Particles is not null){node.Particles.ActiveInHierarchy=()=>node.ActiveInHierarchy;node.Particles.LinkSubEmitters(reference=>nodes.GetValueOrDefault(Archive.S(reference,"gameObject"))?.Particles);}
        foreach(var node in nodes.Values)if(node.Component("UIParticle") is JsonNode ui){
            if(Actor.N(ui["m_MeshSharing"])!=0||Actor.B(ui["m_UseCustomView"]))throw new NotSupportedException("Unsupported UIParticle sharing/view configuration");var group=new Group(node,ui);
            var refs=ui["m_Particles"]!.AsArray();group.Systems=refs.Count>0?refs.OfType<JsonNode>().Select(r=>nodes[Archive.S(r,"gameObject")]).ToList():nodes.Values.Where(n=>n.Particles is not null&&n.Path.StartsWith(node.Path,StringComparison.Ordinal)).ToList();groups.Add(group);
        }
    }
    static Vector3 ScaleOf(float[] m)=>new(new Vector3(m[0],m[1],m[2]).Length(),new Vector3(m[4],m[5],m[6]).Length(),new Vector3(m[8],m[9],m[10]).Length());
    void SyncTransforms(){foreach(var node in nodes.Values){var t=transforms[node];t.Position=node.LayoutPosition;t.Scale=node.Scale;t.Rotation=node.Rotation;}}
    Vector3 WorldScale(Group group,AdvCanvasNode node){var ui=group.Data;int auto=(int)Actor.N(ui["m_AutoScalingMode"]);var parent=group.Node.Parent is null?new Vector3(canvasScale):ScaleOf(group.Node.Parent.Matrix)*canvasScale;var s=AdvPrefab.Vector(ui["m_Scale3D"],Vector3.One);var scale=(auto==2?s:s/canvasScale*group.Node.Scale)*parent;return auto==1&&Actor.N(node.Particles!.Data["scalingMode"])==1?scale*canvasScale:scale;}
    bool CanRender(Group group,AdvCanvasNode node){var scale=ScaleOf(node.Matrix)*WorldScale(group,node);float volume=scale.X*scale.Y*scale.Z;return node.ActiveInHierarchy&&node.InheritedAlpha>=.01f&&volume!=0&&!float.IsNaN(volume);}
    public object Snapshot()=>JsonOutput.Object(("canvasScale", canvasScale), ("systems", groups.SelectMany(g=>g.Systems).Select(n=>JsonOutput.Object(("Path", n.Path), ("count", n.Particles!.Particles.Count), ("time", n.Particles.Time), ("scale", new[]{n.Scale.X,n.Scale.Y,n.Scale.Z}), ("first", n.Particles.Particles.Take(1).Select(p=>JsonOutput.Object(("position", new[]{p.Position.X,p.Position.Y,p.Position.Z}), ("velocity", new[]{p.Velocity.X,p.Velocity.Y,p.Velocity.Z}), ("size", new[]{n.Particles.Size(p).X,n.Particles.Size(p).Y}), ("tile", new[]{n.Particles.Tile(p).X,n.Particles.Tile(p).Y,n.Particles.Tile(p).Z,n.Particles.Tile(p).W}))))))));
    public void Update(bool active,int frame,float dt)
    {
        if(frame==lastFrame)return;lastFrame=frame;
        SyncTransforms();
        foreach(var group in groups){bool enabled=active&&group.Node.ActiveInHierarchy&&Actor.B(group.Data["m_Enabled"]);if(enabled!=group.Active){group.Active=enabled;
                if(enabled){group.SavedScale=group.Node.Scale;group.Prewarm.Clear();foreach(var node in group.Systems){node.Particles!.SetTransform(transforms[node]);node.Particles.Start();if(Actor.B(node.Particles.Data["prewarm"]))group.Prewarm.Add(node);}}
                else{group.Node.Scale=group.SavedScale;foreach(var node in group.Systems)node.Particles!.Clear();}}
            if(!enabled)continue;
            if((int)Actor.N(group.Data["m_AutoScalingMode"])==2){var parent=group.Node.Parent is null?new Vector3(canvasScale):ScaleOf(group.Node.Parent.Matrix)*canvasScale;group.Node.Scale=new(parent.X==0?1:1/parent.X,parent.Y==0?1:1/parent.Y,parent.Z==0?1:1/parent.Z);foreach(var node in nodes.Values)node.Layout(width,height);}
            SyncTransforms();
            foreach(var node in group.Systems){if(!CanRender(group,node))continue;var system=node.Particles!;
                system.SetTransform(transforms[node]);
                if(Actor.N(system.Data["moveWithTransform"])==1){
                    var effective=WorldScale(group,node);static float Divide(float v,float scale)=>scale==0?0:v/scale;
                    var pos=system.EmitterPosition;system.EmitterPosition=new(Divide(pos.X,effective.X),Divide(pos.Y,effective.Y),Divide(pos.Z,effective.Z));
                }
            }
            foreach(var node in group.Systems){if(!CanRender(group,node))continue;var system=node.Particles!;float step=dt*Actor.N(group.Data["m_TimeScaleMultiplier"],1);if(step>0&&group.Prewarm.Remove(node))step+=Actor.N(system.Data["lengthInSec"]);system.Simulate(step);}
        }
    }
    public void Draw(AdvCanvasNode uiNode)
    {
        var group=groups.FirstOrDefault(g=>g.Node==uiNode&&g.Active);if(group is null)return;SyncTransforms();
        foreach(var node in group.Systems){
            if(node.Particles is null||!CanRender(group,node)||node.Particles.Particles.Count==0)continue;
            var renderer=node.Component("ParticleSystemRenderer");if(renderer is null||Actor.N(renderer["m_RenderMode"])==5)continue;
            var material=renderer["m_Materials"]?[0]??throw new InvalidDataException("Particle material absent");string shader=material["shader"]?["shader"]?.ToString()??"";
            if(shader is not ("Mobile/Particles/Additive" or "Mobile/Particles/Alpha Blended"))throw new NotSupportedException("Canvas particle material not connected: "+shader);
            if(AdvPrefab.Vector(renderer["m_Flip"])!=Vector3.Zero)throw new NotSupportedException("UIParticle flip on a disabled renderer is unsupported by the reference");
            var mesh=AdvCanvasParticleMesh.Build(node.Particles,transforms[node],renderer,width,height,canvasScale,WorldScale(group,node),transforms[uiNode].WorldPosition,(int)Actor.N(group.Data["m_PositionMode"])==1);
            var colors=mesh.Attributes["in_COLOR0"];for(int i=3;i<colors.Length;i+=4)colors[i]*=node.InheritedAlpha;
            var values=shaders.Defaults(shader);values["unity_ObjectToWorld"]=AdvMatrix.Identity();values["unity_MatrixVP"]=AdvMatrix.Identity();values["_MainTex_ST"]=[1,1,0,0];values["_GlobalMipBias"]=[0,0];
            foreach(string section in new[]{"floats","ints"})foreach(var pair in material[section]?.AsObject()??new JsonObject())values[pair.Key]=[Actor.N(pair.Value)];
            foreach(var pair in material["colors"]?.AsObject()??new JsonObject())values[pair.Key]=new[]{"r","g","b","a"}.Select(k=>Actor.N(pair.Value?[k])).ToArray();
            var mainTexture=material["textures"]!["_MainTex"]!;float scaleY=Actor.N(mainTexture["scale"]?["y"],1);
            // Canvas textures and vertices use top-down UVs; conjugate Unity's UV transform by that flip.
            values["_MainTex_ST"]=[Actor.N(mainTexture["scale"]?["x"],1),scaleY,Actor.N(mainTexture["offset"]?["x"]),1-scaleY-Actor.N(mainTexture["offset"]?["y"])];
            int destination=shader=="Mobile/Particles/Additive"?1:10;
            gl.GameMaterial32(shaders.Program(shader),values,new Dictionary<string,uint>{{"_MainTex",texture(material["textures"]!["_MainTex"]!["texture"]!)}},mesh.Attributes,mesh.Indices,5,destination,5,destination);
        }
    }
}
