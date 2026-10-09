using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

// One-layer Unity Mecanim data, ported from ournotes-player engine/anim.js.
public sealed class AdvAnimRecords
{
    readonly Dictionary<string,JsonNode?> controllers=[],clips=[];
    public AdvAnimRecords(JsonNode document)
    {
        void Add(Dictionary<string,JsonNode?> map,string name,JsonNode node){if(!map.TryGetValue(name,out var old))map[name]=node;else if(!JsonNode.DeepEquals(old,node))map[name]=null;}
        void Walk(JsonNode? node){if(node is JsonObject obj){if(obj["controller"] is JsonValue&&obj["layers"] is JsonArray)Add(controllers,obj["controller"]!.ToString(),obj);
            else if(obj["clip"] is JsonValue&&obj.Count>1)Add(clips,obj["clip"]!.ToString(),obj);foreach(var item in obj)Walk(item.Value);}else if(node is JsonArray array)foreach(var item in array)Walk(item);}
        Walk(document);
    }
    public JsonNode Controller(JsonNode node)=>node["layers"] is JsonArray?node:controllers.GetValueOrDefault(node["controller"]!.ToString())??throw new InvalidDataException("Missing or ambiguous controller: "+node);
    public JsonNode Clip(JsonNode node)=>node.AsObject().Count>1?node:clips.GetValueOrDefault(node["clip"]!.ToString())??throw new InvalidDataException("Missing or ambiguous clip: "+node);
}
public sealed class AdvAnimClip
{
    public readonly record struct Binding(string Path,string Class,string Attribute,bool PPtr=false);
    public Binding[] Bindings{get;}
    public float Length{get;}public bool Loop{get;}
    readonly JsonNode[] curves;
    readonly JsonArray objects;
    public AdvAnimClip(JsonNode clip)
    {
        Length=Actor.N(clip["stopTime"]);Loop=Actor.B(clip["loopTime"]);objects=clip["pptrCurveMapping"] as JsonArray??new();
        if(Actor.N(clip["discreteCurveCount"])!=0)throw new NotSupportedException("Discrete curves outside streamed clip");
        if(Actor.N(clip["startTime"])!=0||Actor.N(clip["cycleOffset"])!=0||Actor.N(clip["dense"]?["curveCount"])!=0)throw new NotSupportedException("Unsupported native animation clip layout: "+Archive.S(clip,"clip"));
        var bindings=new List<Binding>();foreach(var b in clip["bindings"]!.AsArray().OfType<JsonNode>()){
            if(Actor.B(b["pptr"])){
                if(Actor.N(b["typeID"])!=212||b["attribute"] is not null||Actor.N(b["attributeCrc"])!=0)throw new NotSupportedException("Unsupported PPtr animation binding");
                bindings.Add(new(Archive.S(b,"path"),Archive.S(b,"class"),"m_Sprite",true));continue;
            }
            int n=(int)Actor.N(b["curves"],1);if(n is not (1 or 3 or 4))throw new InvalidDataException("Invalid vector curve width");
            for(int i=0;i<n;i++)bindings.Add(new(Archive.S(b,"path"),Archive.S(b,"class"),Archive.S(b,"attribute")+(n==1?"":"."+"xyzw"[i])));
        }
        Bindings=bindings.ToArray();int discrete=(int)Actor.N(clip["streamed"]?["discreteCurveCount"]),streamed=(int)Actor.N(clip["streamed"]!["curveCount"])+discrete;
        if(Bindings.Length!=streamed+clip["constant"]!.AsArray().Count)throw new InvalidDataException("Clip binding count mismatch");
        if(Bindings.Take(streamed).Count(b=>b.PPtr)!=discrete||Bindings.Skip(streamed).Any(b=>b.PPtr))throw new NotSupportedException("Discrete curves other than streamed PPtr curves");
        curves=Enumerable.Range(0,streamed).Select(_=>(JsonNode)new JsonObject{["keys"]=new JsonArray()}).Concat(clip["constant"]!.AsArray().Select(v=>(JsonNode)new JsonObject{["constant"]=v!.DeepClone()})).ToArray();
        foreach(var frame in clip["streamed"]!["frames"]!.AsArray())foreach(var key in frame![1]!.AsArray()){int i=(int)Actor.N(key![0]);curves[i]["keys"]!.AsArray().Add((JsonNode)new JsonArray(frame[0]!.DeepClone(),key[1]!.DeepClone(),key[2]!.DeepClone(),key[3]!.DeepClone(),key[4]!.DeepClone()));}
    }
    public float[] Sample(float time)
    {
        float local=LocalTime(time);return curves.Select((c,i)=>Bindings[i].PPtr?0:AdvAnimation.Evaluate(c,local)).ToArray();
    }
    float LocalTime(float time){if(Loop&&Length>0){float r=time%Length;return r<0?r+Length:r;}return Math.Clamp(time,0,Length);}
    public JsonNode? SampleObject(int index,float time)
    {
        if(!Bindings[index].PPtr)throw new ArgumentException("Binding is not a PPtr curve",nameof(index));
        var keys=curves[index]["keys"]!.AsArray();if(keys.Count==0)throw new InvalidDataException("PPtr curve has no keys");
        float local=LocalTime(time);int lo=0,hi=keys.Count-1;
        while(lo<hi){int mid=(lo+hi+1)/2;if(Actor.N(keys[mid]![0])<=local)lo=mid;else hi=mid-1;}
        int value=(int)Actor.N(keys[lo]![4]);if(value<0)return null;
        if(value>=objects.Count)throw new InvalidDataException($"PPtr index {value} outside mapping ({objects.Count})");return objects[value];
    }
}
public sealed class AdvAnimator
{
    public sealed record Access(Func<float> Get,Action<float> Set);
    public sealed record ObjectAccess(Func<JsonNode?> Get,Action<JsonNode?> Set);
    sealed record Property(Access Access,float Default);
    sealed record ObjectProperty(ObjectAccess Access,JsonNode? Default);
    sealed record State(string Name,float Speed,bool WriteDefaults,AdvAnimClip? Clip,JsonArray Transitions);
    readonly State[] states;readonly JsonArray any;
    readonly Dictionary<string,(int Type,float Value)> parameters=[];
    readonly Dictionary<AdvAnimClip,Property?[]> bound=[];
    readonly Dictionary<AdvAnimClip.Binding,Property?> properties=[];
    readonly Dictionary<AdvAnimClip,ObjectProperty?[]> objectBound=[];
    readonly Dictionary<AdvAnimClip.Binding,ObjectProperty?> objectProperties=[];
    readonly int defaultState;
    int current;float time;int? fading;float fadeTime,fadeDuration,fadeElapsed;
    public float Speed{get;set;}=1;
    public string StateName=>states[current].Name;
    public float NormalizedTime=>states[current].Clip is {Length:>0} c?time/c.Length:time;
    public bool HasState(string name)=>states.Any(s=>s.Name==name);
    public AdvAnimator(JsonNode record,AdvAnimRecords records,Func<AdvAnimClip.Binding,Access?> bind,Func<AdvAnimClip.Binding,ObjectAccess?>? bindObject=null)
    {
        var controller=records.Controller(record);if(controller["layers"]!.AsArray().Count!=1)throw new NotSupportedException("Multilayer animator unsupported by reference");
        var machine=controller["stateMachines"]![(int)Actor.N(controller["layers"]![0]!["stateMachine"])]!;defaultState=(int)Actor.N(machine["defaultState"]);any=machine["anyStateTransitions"]!.AsArray();
        var clips=controller["clips"]!.AsArray().Select(c=>new AdvAnimClip(records.Clip(c!))).ToArray();
        states=machine["states"]!.AsArray().OfType<JsonNode>().Select(s=>{
            if(Actor.N(s["cycleOffset"])!=0||Actor.B(s["mirror"])||Archive.S(s,"speedParam")!=""||Archive.S(s,"timeParam")!="")throw new NotSupportedException("Unsupported animator state settings");
            var trees=s["blendTrees"]!.AsArray();if(trees.Count>1||trees.Count==1&&trees[0]!.AsArray().Count!=1)throw new NotSupportedException("Animator blend tree unsupported by reference");var leaf=trees.Count==0?null:trees[0]![0];
            if(leaf is not null&&(leaf["children"]!.AsArray().Count>0||Actor.N(leaf["clip"])<0))throw new NotSupportedException("Animator blend tree unsupported by reference");
            return new State(Archive.S(s,"name"),Actor.N(s["speed"],1),Actor.B(s["writeDefaultValues"]),leaf is null?null:clips[(int)Actor.N(leaf["clip"])],s["transitions"]!.AsArray());
        }).ToArray();
        foreach(var clip in states.Select(s=>s.Clip).OfType<AdvAnimClip>().Distinct())bound[clip]=clip.Bindings.Select(b=>{
            if(b.PPtr)return null;
            if(properties.TryGetValue(b,out var p))return p;var access=bind(b);return properties[b]=access is null?null:new(access,access.Get());}).ToArray();
        foreach(var clip in bound.Keys)objectBound[clip]=clip.Bindings.Select(b=>{
            if(!b.PPtr)return null;
            if(objectProperties.TryGetValue(b,out var p))return p;var access=bindObject?.Invoke(b);return objectProperties[b]=access is null?null:new(access,access.Get());}).ToArray();
        foreach(var p in controller["parameters"]!.AsArray().OfType<JsonNode>())parameters[Archive.S(p,"name")]=((int)Actor.N(p["type"]),0);Reset();
    }
    public void Reset(){current=defaultState;time=0;fading=null;foreach(string name in parameters.Keys.ToArray())parameters[name]=(parameters[name].Type,0);}
    public void Play(string name){int index=Array.FindIndex(states,s=>s.Name==name);if(index<0)throw new ArgumentException("Unknown animation state: "+name);current=index;time=0;fading=null;}
    bool Conditions(JsonNode transition)
    {
        foreach(var c in transition["conditions"]!.AsArray().OfType<JsonNode>()){
            if(!parameters.TryGetValue(Archive.S(c,"event"),out var p))throw new InvalidDataException("Unknown animation parameter");float threshold=Actor.N(c["threshold"]);
            bool ok=(int)Actor.N(c["mode"]) switch{1=>p.Value!=0,2=>p.Value==0,3=>p.Value>threshold,4=>p.Value<threshold,6=>p.Value==threshold,7=>p.Value!=threshold,_=>throw new NotSupportedException("Unknown animation condition")};if(!ok)return false;
        }return true;
    }
    void Take(JsonNode transition,float carry)
    {
        foreach(var c in transition["conditions"]!.AsArray().OfType<JsonNode>()){string name=Archive.S(c,"event");if(parameters[name].Type==9)parameters[name]=(9,0);}
        int next=(int)Actor.N(transition["destination"]);float duration=Actor.N(transition["duration"])*(Actor.B(transition["fixedDuration"])?1:states[current].Clip?.Length??0);
        fading=duration>0?current:null;fadeTime=time;fadeDuration=duration;fadeElapsed=0;current=next;time=Actor.N(transition["offset"])*(states[next].Clip?.Length??0)+carry;
    }
    public void Step(float dt)
    {
        if(fading is null)foreach(var tr in any.Concat(states[current].Transitions.Where(t=>t!["conditions"]!.AsArray().Count>0)).OfType<JsonNode>().Where(t=>!Actor.B(t["hasExitTime"]))){if(!Actor.B(tr["canTransitionToSelf"])&&(int)Actor.N(tr["destination"])==current)continue;if(Conditions(tr)){Take(tr,0);break;}}
        var state=states[current];float prev=time;time+=dt*state.Speed*Speed;
        if(fading is int old){fadeTime+=dt*states[old].Speed*Speed;fadeElapsed+=dt*Speed;if(fadeElapsed>=fadeDuration)fading=null;}
        if(fading is null&&state.Clip is {Length:>0} clip)foreach(var tr in state.Transitions.OfType<JsonNode>()){
            if(!Actor.B(tr["hasExitTime"])||!Conditions(tr))continue;float exit=Actor.N(tr["exitTime"]),n0=prev/clip.Length,n1=time/clip.Length;
            bool looping=exit<1&&clip.Loop,hit=looping?MathF.Floor(n1-exit)>MathF.Floor(n0-exit):n0<exit&&n1>=exit;
            if(hit){Take(tr,(n1-(looping?MathF.Floor(n1-exit)+exit:exit))*clip.Length);break;}
        }
        Write();
    }
    void Write()
    {
        var state=states[current];
        if(fading is int previous&&(state.Clip?.Bindings.Any(b=>b.PPtr)==true||states[previous].Clip?.Bindings.Any(b=>b.PPtr)==true))
            throw new NotSupportedException("Cross-fade over a PPtr curve is unsupported by reference");
        if(fading is null&&objectProperties.Count>0){
            var written=new HashSet<ObjectProperty>();
            if(state.Clip is { } objectClip){var ps=objectBound[objectClip];for(int i=0;i<ps.Length;i++)if(ps[i] is { } p){p.Access.Set(objectClip.SampleObject(i,time));written.Add(p);}}
            if(state.WriteDefaults)foreach(var p in objectProperties.Values.OfType<ObjectProperty>())if(!written.Contains(p))p.Access.Set(p.Default);
        }
        Dictionary<Property,float> Sample(State s,float at){var values=new Dictionary<Property,float>();if(s.Clip is not null){var v=s.Clip.Sample(at);var ps=bound[s.Clip];for(int i=0;i<v.Length;i++)if(ps[i] is Property p)values[p]=v[i];}return values;}
        var to=Sample(state,time);if(fading is not int old){foreach(var p in properties.Values.OfType<Property>())if(to.TryGetValue(p,out float v))p.Access.Set(v);else if(state.WriteDefaults)p.Access.Set(p.Default);return;}
        var from=Sample(states[old],fadeTime);float weight=Math.Clamp(fadeElapsed/fadeDuration,0,1);
        foreach(var p in properties.Values.OfType<Property>()){float a=from.GetValueOrDefault(p,states[old].WriteDefaults?p.Default:p.Access.Get()),b=to.GetValueOrDefault(p,state.WriteDefaults?p.Default:p.Access.Get());p.Access.Set(a+(b-a)*weight);}
    }
}
