using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

// Serialized Fwk DOTweenSequence timing. These are game prefab animations, not script instructions.
public sealed class AdvStillSequence
{
    sealed class Clip
    {
        public required IReadOnlyList<AdvTween> Tweens;
        public required JsonNode Definition;public Action<JsonNode>? Invoke;public required Func<bool> Alive;
        public float At,Duration,End;public int Loops,LoopType;public bool Started;
        bool played,complete;int completedLoops;float rawPosition;
        public bool Killed;public void Kill(){Killed=true;foreach(var tween in Tweens)tween.Kill();}
        void Event(string name){if(Actor.B(Definition["has"+char.ToUpperInvariant(name[0])+name[1..]])&&Definition[name] is JsonNode ev){if(Invoke is not null)Invoke(ev);else if(ev["m_PersistentCalls"]?["m_Calls"] is JsonArray calls&&calls.Count>0)throw new NotSupportedException("Still animation event host missing");}}
        public void Sample(float elapsed,bool rewind=false){
            if(Killed)return;
            Started=true;
            foreach(var tween in Tweens)tween.Initialize();
            if(!played&&!rewind){
                played=true;Event("onStart");if(!Alive())return;
                Event("onPlay");if(!Alive())return;
            }
            bool wasRewinded=rawPosition<=0&&completedLoops<=0,wasComplete=complete;
            int previousLoops=completedLoops;
            elapsed=Math.Clamp(elapsed,0,Duration*Loops);
            completedLoops=Duration<=0?1:(int)Math.Min(Math.Floor(elapsed/Duration),Loops);
            float position=Duration<=0?0:completedLoops>=Loops?Duration:elapsed%Duration;
            if(position<=0&&completedLoops>0)position=Duration;
            rawPosition=position;complete=completedLoops==Loops;
            if(Loops>1&&LoopType==1&&(position<Duration?completedLoops%2!=0:completedLoops%2==0))position=Duration-position;
            foreach(var tween in Tweens)tween.Sample(position);
            Event("onUpdate");
            if(rawPosition<=0&&completedLoops<=0&&!wasRewinded)Event("onRewind");
            if(!rewind)for(int i=previousLoops;i<completedLoops;i++)Event("onStepComplete");
            if(complete&&!wasComplete)Event("onComplete");
        }
    }
    readonly List<Clip> clips=[];readonly bool repeat;readonly float restartAt,duration,speed;
    readonly List<(float At,Clip? Clip,JsonNode? Event)> entries=[];
    readonly Action<JsonNode>? invoke;
    readonly bool standalone;
    float time;public bool Finished{get;private set;}
    public bool CreatedPlaying{get;private set;}
    public AdvStillSequence(IReadOnlyDictionary<string,AdvCanvasNode> nodes,AdvCanvasNode owner,JsonNode sequence,AdvTweens tweens,float speed,AdvRandom? random=null,Action<JsonNode>? invoke=null,bool standalone=false,Action<string,JsonNode,Action,Action>? created=null,Func<string,bool>? available=null,Action<string,Func<bool>>? inserted=null)
    {
        this.speed=speed>0?speed:1;repeat=Actor.B(sequence["_isLoop"]);random??=new(722);this.invoke=invoke;this.standalone=standalone;
        float at=0;var selected=new HashSet<string>();
        foreach(var command in sequence["_list"]!.AsArray().OfType<JsonNode>()){
            int type=(int)Actor.N(command["_commandType"]);
            if(type==0){
                string path=Archive.S(command["_tweenAnimation"]!,"gameObject");
                var node=nodes[path];if(ReferenceEquals(node,owner)&&node.Component("DOTweenAnimation") is null)continue;
                var animation=node.Component("DOTweenAnimation")??throw new NotSupportedException("Nested still DOTweenSequence");
                if(selected.Add(path)&&(available?.Invoke(path)??true)&&(animation["isValid"] is null||Actor.B(animation["isValid"]))&&(animation["isActive"] is null||Actor.B(animation["isActive"]))){
                    foreach(string flag in new[]{"isFrom","isRelative","isSpeedBased","useTargetAsV3","isIndependentUpdate","autoPlay"})
                        if(Actor.B(animation[flag]))throw new NotSupportedException("Still tween option: "+flag);
                    if(Actor.N(animation["updateType"])!=0||Actor.N(animation["forcedTargetType"])!=0||(animation["autoKill"] is not null&&!Actor.B(animation["autoKill"])))
                        throw new NotSupportedException("Still tween update/target/lifetime option");
                    int loops=(int)Actor.N(animation["loops"],1),loopType=(int)Actor.N(animation["loopType"]);
                    if(loopType is not (0 or 1))throw new NotSupportedException("Still incremental tween loops");
                    if(loops<0)loops=int.MaxValue;else loops=Math.Max(1,loops);
                    float length=Math.Max(0,Actor.N(animation["duration"])),start=at+Actor.N(animation["delay"]);
                    var clip=new Clip{At=start,Duration=length,End=start+length*loops,Loops=loops,LoopType=loopType,Definition=animation,Invoke=invoke,Alive=()=>!Finished,
                        Tweens=AdvStillProperties.Start(nodes,node,animation,tweens,length,(AdvEase)(int)Actor.N(animation["easeType"]),true,random)};
                    if(clip.Tweens.Count>0){
                        clip.Alive=()=>!Finished&&!clip.Killed;
                        if(Actor.B(animation["hasOnTweenCreated"])&&animation["onTweenCreated"] is JsonNode ev){
                            if(created is not null)created(path,ev,clip.Kill,()=>CreatedPlaying=true);
                            else if(invoke is not null)invoke(ev);
                            else if(ev["m_PersistentCalls"]?["m_Calls"] is JsonArray calls&&calls.Count>0)throw new NotSupportedException("Still animation event host missing");
                        }
                        if(!clip.Killed){clip.Duration=clip.Tweens[0].Duration;clip.End=start+clip.Duration*loops;clips.Add(clip);entries.Add((start,clip,null));duration=Math.Max(duration,clip.End);inserted?.Invoke(path,()=>!clip.Killed);}
                    }
                }
            }else if(type==1){if(command["_tweenEvent"] is JsonNode ev)entries.Add((at,null,ev));}
            else if(type!=2)throw new NotSupportedException("Still sequence command: "+type);
            at+=Math.Max(0,Actor.N(command["_duration"]));
        }
        restartAt=at;duration=Math.Max(duration,at);
        var sorted=clips.OrderBy(c=>c.At).ToArray();clips.Clear();clips.AddRange(sorted);
        var ordered=entries.OrderBy(e=>e.At).ToArray();entries.Clear();entries.AddRange(ordered);
    }
    public void Step(float delta)
    {
        if(Finished||Math.Abs(delta*speed)<1e-6f)return;
        float previous=time;time=Math.Min(time+delta*speed,duration);
        // The sequence's completion callback can precede the end of an inserted tween.
        float to=repeat&&time>=restartAt?restartAt:time;
        foreach(var entry in entries){
            float end=entry.Clip?.End??entry.At;
            if(entry.At>to||(entry.At>0?end<=previous:end<previous))continue;
            if(standalone&&entry.At>0&&entry.At==to&&entry.Clip is {Started:false})continue;
            if(entry.Clip is not null)entry.Clip.Sample(to-entry.At);
            else if(invoke is not null)invoke(entry.Event!);
            else if(entry.Event?["m_PersistentCalls"]?["m_Calls"] is JsonArray calls&&calls.Count>0)throw new NotSupportedException("Still event host missing");
            if(Finished)return;
        }
        if(repeat&&time>=restartAt){
            for(int i=clips.Count-1;i>=0;i--)if(clips[i].Started){clips[i].Sample(0,true);if(Finished)return;}
            time=0;
        }else if(time>=duration)Finished=true;
    }
    public void Kill(){Finished=true;foreach(var clip in clips)clip.Kill();}
}
