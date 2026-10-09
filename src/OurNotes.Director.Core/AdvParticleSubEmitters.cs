using System.Numerics;
using System.Text.Json.Nodes;
namespace OurNotes.Director.Core;

public sealed partial class AdvParticles
{
    internal sealed class SubBirthState(int count){public int[] Bursts=new int[count];public double Accumulator;}
    sealed record SubEmitter(JsonNode Definition,AdvParticles? System,float Probability);
    sealed record PendingBirth(float Age,Vector3 Position,float NormalizedTime);
    readonly List<SubEmitter> subEmitters=[];
    readonly List<PendingBirth> pendingBirths=[];
    public AdvParticles? MainEmitter{get;private set;}
    public Func<bool> ActiveInHierarchy{get;set;}=()=>true;
    public void LinkSubEmitters(Func<JsonNode,AdvParticles?> resolve)
    {
        subEmitters.Clear();if(!Enabled(Data["SubModule"]))return;
        foreach(var definition in Data["SubModule"]!["subEmitters"]?.AsArray()??new JsonArray()){
            var e=definition!;var system=e["emitter"] is JsonNode reference?resolve(reference):null;
            if(system is null||system==this)throw new InvalidDataException("Sub-emitter system missing or self-referential");
            for(var ancestor=this;ancestor is not null;ancestor=ancestor.MainEmitter)if(ancestor==system)throw new InvalidDataException("Cyclic particle sub-emitters");
            system.MainEmitter=this;subEmitters.Add(new(e,system,Actor.N(e["emitProbability"],1)));
        }
        CheckSubEmitters();
    }
    void CheckSubEmitters()
    {
        if(!Enabled(Data["SubModule"]))return;
        if((Data["SubModule"]?["subEmitters"]?.AsArray().Count??0)!=subEmitters.Count)throw new InvalidDataException("Particle sub-emitters must be linked before playback");
        foreach(var e in subEmitters)if(N(e.Definition,"type")!=0||N(e.Definition,"properties")!=0)throw new NotSupportedException("Reference supports only Birth sub-emitters without inherited properties");
    }
    void EmitSubBirths(Particle p,float dt)
    {
        float t1=p.Age,t0=Math.Max(0,t1-dt);
        for(int i=0;i<subEmitters.Count;i++){
            var entry=subEmitters[i];var state=p.SubBirth[i];var sub=entry.System!;var emission=sub.Data["EmissionModule"];
            if(state is null||!Enabled(emission)||!sub.ActiveInHierarchy())continue;
            void Put(float tau){var at=WorldSpace?p.Position:LocalToWorld(p.Position)+EmitterPosition;sub.pendingBirths.Add(new(t1-tau,at,sub.Duration>0?Math.Min(tau/sub.Duration,1):0));}
            var list=emission!["m_Bursts"]!.AsArray();
            for(int k=0;k<Math.Min(list.Count,(int)N(emission,"m_BurstCount"));k++){
                var b=list[k]!;float interval=N(b,"repeatInterval");int cycles=(int)N(b,"cycleCount");if(cycles<=0)cycles=interval>0?int.MaxValue:1;
                for(int guard=0;guard<100000&&state.Bursts[k]<cycles;guard++){
                    float at=N(b,"time")+state.Bursts[k]*interval;if(at>t1)break;state.Bursts[k]++;
                    if(N(b,"probability")<1&&sub.random.Value()>=N(b,"probability"))continue;
                    int count=(int)MathF.Floor(C(b["countCurve"],sub.Duration>0?at/sub.Duration:0,sub.random.Value())+.5f);for(int n=0;n<count;n++)Put(at);
                }
            }
            if(t1>t0){float rate=C(emission["rateOverTime"],sub.Duration>0?(t0+t1)*.5f/sub.Duration:0,sub.random.Value());if(rate>0){double total=state.Accumulator+rate*(double)(t1-t0);int count=(int)Math.Floor(total);for(int n=1;n<=count;n++)Put((float)(t0+(n-state.Accumulator)/rate));state.Accumulator=total-count;}}
        }
    }
}
