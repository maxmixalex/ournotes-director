using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

// AdvParticleEffectGroupCollection: shared references are one instance, including when switching groups.
public sealed class AdvEffectGroups
{
    readonly AdvEffectInstance[][] groups;
    readonly AdvLoop loop;
    public IReadOnlyList<AdvEffectInstance> Instances{get;}
    public int Current{get;private set;}
    public AdvEffectGroups(JsonNode exported,AdvPrefab prefab,JsonNode collection,AdvAnimRecords records,AdvRandom random,AdvLoop loop)
    {
        this.loop=loop;var byPath=new Dictionary<string,AdvEffectInstance>();
        groups=collection["_groups"]!.AsArray().Select(g=>g!["_particleEffects"]!.AsArray().Select(reference=>{
            if(reference is null||Archive.S(reference,"class")!="AdvParticleEffect"||reference["gameObject"] is null)throw new InvalidDataException("Invalid stage particle effect reference");
            string path=Archive.S(reference,"gameObject");if(!byPath.TryGetValue(path,out var effect)){
                if(byPath.Keys.Any(p=>path.StartsWith(p+"/",StringComparison.Ordinal)||p.StartsWith(path+"/",StringComparison.Ordinal)))throw new NotSupportedException("Nested stage particle effects");
                byPath[path]=effect=new(exported,random,records,prefab,path);
            }return effect;
        }).ToArray()).ToArray();
        Instances=byPath.Values.ToArray();foreach(var effect in Instances)effect.Initialize();
    }
    IEnumerable<AdvEffectInstance> ActiveGroup=>Current>=0&&Current<groups.Length?groups[Current]:[];
    public void Play(){foreach(var effect in ActiveGroup)effect.Play();}
    public void Stop(){foreach(var effect in ActiveGroup)effect.Stop(false,loop);}
    public void Change(int index){Stop();Current=index;Play();}
    public void SetSpeed(float speed){foreach(var effect in Instances)effect.SetSpeed(speed);}
}
