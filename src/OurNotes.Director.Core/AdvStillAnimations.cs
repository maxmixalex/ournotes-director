using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

// Native bindings for the serialized UnityEvents carried by a still prefab.
public sealed class AdvStillAnimations : IDisposable
{
    sealed record Running(AdvStillSequence Sequence,bool Standalone=false){public bool Playing=!Standalone;public string Target="",Id="",Path="";}
    readonly IReadOnlyDictionary<string,AdvCanvasNode> nodes;readonly AdvTweens tweens;readonly AdvRandom random;
    readonly Dictionary<string,AdvStillSequence> active=[];float speed=1;
    readonly List<Running> running=[];
    readonly HashSet<string> awoken=[],enabledSequences=[];
    readonly Dictionary<string,Func<bool>> ownedAnimations=[];
    sealed record Creating(string Path,string Target,string Id,Action Kill,Action Play);
    readonly List<Creating> creating=[];
    void Created(string path,JsonNode ev,Action kill,Action play)
    {
        var node=nodes[path];var animation=node.Component("DOTweenAnimation")!;
        var entry=new Creating(path,Target(node,animation),Archive.S(animation,"id"),kill,play);creating.Add(entry);
        try{Invoke(ev);}finally{creating.Remove(entry);}
    }
    public AdvStillAnimations(IReadOnlyDictionary<string,AdvCanvasNode> nodes,AdvTweens tweens,AdvRandom random)
    {
        this.nodes=nodes;this.tweens=tweens;this.random=random;
        SyncActivity();
        Stop(); // Initial still deactivation kills the tweens referenced by its sequences.
    }
    public void SyncActivity()
    {
        foreach(var node in nodes.Values){
            var animation=node.Component("DOTweenAnimation");
            if(animation is null||!node.ActiveInHierarchy||!awoken.Add(node.Path))continue;
            if(!Actor.B(animation["isActive"])||!Actor.B(animation["autoGenerate"])||!Actor.B(animation["isValid"]))continue;
            var definition=new JsonObject{["_list"]=new JsonArray(new JsonObject{["_commandType"]=0,["_tweenAnimation"]=new JsonObject{["gameObject"]=node.Path}})};
            int order=running.Count;var sequence=new AdvStillSequence(nodes,node,definition,tweens,1,random,Invoke,true,Created);
            running.Insert(Math.Min(order,running.Count),new(sequence,true){Playing=sequence.CreatedPlaying,Target=Target(node,animation),Id=Archive.S(animation,"id"),Path=node.Path});
        }
        foreach(var node in nodes.Values){
            var sequence=node.Component("DOTweenSequence");if(sequence is null)continue;
            bool enabled=node.ActiveInHierarchy&&Actor.B(sequence["m_Enabled"]);
            if(enabled)enabledSequences.Add(node.Path);
            else if(enabledSequences.Remove(node.Path)){if(active.Remove(node.Path,out var playing))playing.Kill();KillReferenced(sequence);}
        }
    }
    static string Target(AdvCanvasNode node,JsonNode animation)=>!Actor.B(animation["targetIsSelf"])&&Actor.B(animation["tweenTargetIsTargetGO"])?Archive.S(animation["targetGO"]!,"gameObject"):node.Path;
    void KillReferenced(JsonNode definition)
    {
        var paths=definition["_list"]!.AsArray().OfType<JsonNode>().Select(c=>c["_tweenAnimation"]?["gameObject"]?.GetValue<string>()).OfType<string>().ToHashSet();
        foreach(var entry in creating.ToArray())if(paths.Contains(entry.Path))entry.Kill();
        foreach(var entry in running)if(entry.Standalone&&paths.Contains(entry.Path))entry.Sequence.Kill();
    }
    public void Play(string path,float playbackSpeed)
    {
        speed=playbackSpeed;var node=nodes[path];var definition=node.Component("DOTweenSequence")??throw new InvalidDataException("Still sequence missing: "+path);
        if(active.Remove(path,out var previous))previous.Kill();
        KillReferenced(definition);
        int order=running.Count;var sequence=new AdvStillSequence(nodes,node,definition,tweens,speed,random,Invoke,created:Created,available:p=>!ownedAnimations.TryGetValue(p,out var alive)||!alive(),inserted:(p,alive)=>ownedAnimations[p]=alive);active[path]=sequence;running.Insert(Math.Min(order,running.Count),new(sequence));
    }
    public void Step(float delta){foreach(var entry in running.ToArray())if(entry.Playing)entry.Sequence.Step(delta);running.RemoveAll(s=>s.Sequence.Finished);}
    public void Stop(){foreach(var sequence in active.Values)sequence.Kill();foreach(var entry in running)if(!entry.Standalone)entry.Sequence.Kill();foreach(var node in nodes.Values)if(node.Component("DOTweenSequence") is JsonNode sequence)KillReferenced(sequence);active.Clear();ownedAnimations.Clear();running.RemoveAll(s=>s.Sequence.Finished);}
    public void Dispose(){foreach(var sequence in active.Values)sequence.Kill();foreach(var entry in running)entry.Sequence.Kill();running.Clear();active.Clear();ownedAnimations.Clear();}
    void Invoke(JsonNode ev)
    {
        foreach(var call in ev["m_PersistentCalls"]?["m_Calls"]?.AsArray().OfType<JsonNode>()??[]){
            string method=Archive.S(call,"m_MethodName");
            if(Actor.N(call["m_CallState"])==0||method==""||call["m_Target"] is not JsonNode reference)continue;
            string component=Archive.S(reference,"class",Archive.S(reference,"component")),path=Archive.S(reference,"gameObject");
            if(Archive.S(reference,"component")=="")throw new NotSupportedException("Still UnityEvent on a GameObject: "+method);
            int mode=(int)Actor.N(call["m_Mode"]);if(mode is not (1 or 5))throw new NotSupportedException("Still UnityEvent argument mode: "+mode);
            if(!nodes.TryGetValue(path,out var node)||node.Component(component) is null)throw new InvalidDataException("Still UnityEvent target missing: "+path+"/"+component);
            if(component=="DOTweenSequence"&&method=="DOPlay")Play(path,speed);
            else if(component=="DOTweenAnimation"&&method is "DOPlay" or "DOKill" or "DOPlayAllById" or "DOKillAllById"){
                bool byId=method.EndsWith("AllById",StringComparison.Ordinal),play=method.StartsWith("DOPlay",StringComparison.Ordinal);
                string key=byId?mode==5?Archive.S(call["m_Arguments"]!,"m_StringArgument"):"":Target(node,node.Component(component)!);
                if(byId&&mode!=5)continue;
                foreach(var entry in creating.AsEnumerable().Reverse().Where(e=>byId?e.Id!=""&&e.Id==key:e.Target==key).ToArray()){
                    if(play)entry.Play();else entry.Kill();
                }
                foreach(var entry in running.AsEnumerable().Reverse().Where(e=>e.Standalone&&!e.Sequence.Finished&&(byId?e.Id!=""&&e.Id==key:e.Target==key)).ToArray()){
                    if(play)entry.Playing=true;else entry.Sequence.Kill();
                }
            }
            else throw new NotSupportedException("Still UnityEvent method: "+component+"."+method);
        }
    }
}
