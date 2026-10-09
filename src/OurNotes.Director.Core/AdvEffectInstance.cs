using System.Numerics;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

// AdvParticleEffect lifecycle and renderer ordering, shared by episode effects and stage groups.
public sealed class AdvEffectInstance
{
    sealed record SystemEntry(string Path,AdvTransform Transform,AdvParticles System,float Speed){public bool? Active;}
    public sealed record Renderer(AdvTransform Transform,AdvEffectSprite? Sprite,AdvEffectParticles? Particles,int DefaultOrder,AdvEffectMesh? Mesh=null)
    {
        public int Layer{get;set;}
        public int Order{get=>Sprite?.SortingOrder??Mesh?.SortingOrder??Particles!.SortingOrder;set{if(Sprite is not null)Sprite.SortingOrder=value;else if(Mesh is not null)Mesh.SortingOrder=value;else Particles!.SortingOrder=value;}}
    }
    readonly List<SystemEntry> systems=[];
    readonly List<Renderer> renderers=[];
    readonly AdvParticles main;
    readonly string mainPath;
    readonly AdvEffectAnimation? animation;
    readonly int[] relativeOrders;
    int generation;
    public AdvTransform Root{get;}
    public IReadOnlyList<Renderer> Renderers=>renderers;
    public bool Playing{get;private set;}
    public bool Visible=>Root.ActiveInHierarchy;
    public int PositionType{get;set;}
    int layer;
    public int Layer{get=>layer;set{layer=value;foreach(var renderer in renderers)renderer.Layer=value;}}
    public AdvEffectInstance(JsonNode exported,AdvRandom random,AdvAnimRecords records,AdvPrefab? prefab=null,string? rootPath=null)
    {
        prefab??=new(exported);rootPath??=Archive.S(exported["nodes"]![0]!,"path");Root=prefab.Transform(rootPath);
        var sprites=new Dictionary<string,AdvEffectSprite>();var particleMap=new Dictionary<string,AdvParticles>();var meshPaths=new HashSet<string>();
        JsonNode? effect=null,animator=null;string animatorPath="";
        foreach(var node in exported["nodes"]!.AsArray()){
            string path=Archive.S(node!,"path");if(path!=rootPath&&!path.StartsWith(rootPath+"/",StringComparison.Ordinal))continue;
            if(path==rootPath)layer=(int)Actor.N(node!["layer"]);
            var transform=prefab.Transform(path);var components=node!["components"]!.AsArray();
            foreach(var c in components.OfType<JsonNode>())switch(Archive.S(c,"class",Archive.S(c,"type"))){
                case "AdvParticleEffect":effect=c;break;
                case "Animator":if(animator is null){animator=c;animatorPath=path;}break;
                case "SpriteRenderer":var sprite=new AdvEffectSprite(c,transform);sprites[path]=sprite;renderers.Add(new(transform,sprite,null,sprite.SortingOrder){Layer=(int)Actor.N(node!["layer"])});break;
                case "MeshFilter":
                    var meshRenderer=components.FirstOrDefault(n=>Archive.S(n!,"class",Archive.S(n!,"type"))=="MeshRenderer")??throw new InvalidDataException("Effect MeshFilter without MeshRenderer");
                    var mesh=new AdvEffectMesh(c,meshRenderer,transform);meshPaths.Add(path);renderers.Add(new(transform,null,null,mesh.SortingOrder,mesh){Layer=(int)Actor.N(node!["layer"])});break;
                case "MeshRenderer":
                    if(!components.Any(n=>Archive.S(n!,"class",Archive.S(n!,"type"))=="MeshFilter"))throw new InvalidDataException("Effect MeshRenderer without MeshFilter");break;
                case "ParticleSystem":
                    var system=new AdvParticles(c,random);particleMap[path]=system;systems.Add(new(path,transform,system,Actor.N(c["simulationSpeed"],1)));
                    var r=components.FirstOrDefault(n=>Archive.S(n!,"class",Archive.S(n!,"type"))=="ParticleSystemRenderer");
                    if(r is not null){var renderer=new AdvEffectParticles(system,transform,r);renderers.Add(new(transform,null,renderer,renderer.SortingOrder){Layer=(int)Actor.N(node!["layer"])});}break;
                case "ParticleSystemRenderer":break;
                default:throw new NotSupportedException("Effect component not connected: "+Archive.S(c,"class",Archive.S(c,"type")));
            }
        }
        foreach(var entry in systems){entry.System.ActiveInHierarchy=()=>entry.Transform.ActiveInHierarchy;entry.System.LinkSubEmitters(reference=>particleMap.GetValueOrDefault(Archive.S(reference,"gameObject")));}
        mainPath=Archive.S(effect?["_particleSystem"]??throw new InvalidDataException("Effect has no main ParticleSystem"),"gameObject");
        main=particleMap.GetValueOrDefault(mainPath)??throw new InvalidDataException("Effect main ParticleSystem absent: "+mainPath);
        if(animator is not null)animation=new(animator,animatorPath,prefab,records,sprites,particleMap,meshPaths);
        var positive=renderers.Select(r=>r.DefaultOrder).Where(o=>o>=1).Distinct().Order().ToArray();var negative=renderers.Select(r=>r.DefaultOrder).Where(o=>o<1).Distinct().Order().ToArray();
        relativeOrders=renderers.Select(r=>r.DefaultOrder>=1?Array.IndexOf(positive,r.DefaultOrder)+1:Array.IndexOf(negative,r.DefaultOrder)-negative.Length).ToArray();
        SyncActivity();
    }
    bool MainChild(SystemEntry entry)=>entry.Path==mainPath||entry.Path.StartsWith(mainPath+"/",StringComparison.Ordinal);
    static void Configure(SystemEntry entry)=>entry.System.SetTransform(entry.Transform);
    public void SyncActivity()
    {
        if(systems.Any(entry=>entry.Active!=entry.Transform.ActiveInHierarchy))foreach(var entry in systems)if(entry.Transform.ActiveInHierarchy)Configure(entry);
        foreach(var entry in systems){bool active=entry.Transform.ActiveInHierarchy;if(entry.Active==active)continue;entry.Active=active;
            if(!active)entry.System.Deactivate();else if(Actor.B(entry.System.Data["playOnAwake"])){Configure(entry);entry.System.Play();}}
        animation?.SyncActive();
    }
    void SetActive(bool active){Root.Active=active;SyncActivity();}
    public void Initialize(float speed=1){foreach(var entry in systems.Where(MainChild))entry.System.Stop(true);SetSpeed(speed);SetActive(false);Playing=false;generation++;}
    public void SetSpeed(float speed){foreach(var entry in systems)entry.System.Data["simulationSpeed"]=entry.Speed*speed;if(animation is not null)animation.Speed=speed;}
    public void Play(float? speed=null)
    {
        Playing=true;generation++;if(speed is float rate)SetSpeed(rate);SetActive(true);
        foreach(var entry in systems.Where(MainChild))if(entry.Transform.ActiveInHierarchy){Configure(entry);entry.System.Play();}
    }
    public void Stop(bool immediate,AdvLoop loop)
    {
        if(!Playing)return;Playing=false;int token=++generation;
        foreach(var entry in systems.Where(MainChild))entry.System.Stop(immediate);
        if(immediate){SetActive(false);return;}
        void Wait(){if(token!=generation)return;if(main.Playing)loop.YieldUpdate().OnCompleted(Wait);else SetActive(false);}Wait();
    }
    public bool TryPlayState(string state)=>animation?.TryPlay(state)??false;
    public void Animate(float dt){SyncActivity();animation?.Step(dt);SyncActivity();}
    public void Simulate(float dt){SyncActivity();foreach(var entry in systems)if(entry.Transform.ActiveInHierarchy)Configure(entry);foreach(var entry in systems)if(entry.Transform.ActiveInHierarchy)entry.System.Simulate(dt);}
    public void RestoreOrder(){foreach(var renderer in renderers)renderer.Order=renderer.DefaultOrder;}
    public void SetOrder(int order){RestoreOrder();foreach(var renderer in renderers)if(renderer.Particles is not null)renderer.Order=order;}
    public void SetRelativeOrder(int back,int front,int maximum){for(int i=0;i<renderers.Count;i++)renderers[i].Order=relativeOrders[i]>=1?Math.Min(front+relativeOrders[i],maximum):back+relativeOrders[i];}
    public object Snapshot()=>JsonOutput.Object(("playing", Playing), ("visible", Visible), ("systems", systems.Select(s=>JsonOutput.Object(("time", s.System.Time), ("count", s.System.Particles.Count), ("playing", s.System.Playing), ("emitting", s.System.Emitting)))), ("orders", renderers.Select(r=>r.Order)));
}
