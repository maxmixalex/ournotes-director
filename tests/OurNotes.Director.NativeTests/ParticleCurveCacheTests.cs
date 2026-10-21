using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

static class ParticleCurveCacheTests
{
    public static void Run(Action<bool,string> check)
    {
        static JsonNode Number(float value)=>float.IsFinite(value)?JsonValue.Create(value)!:JsonValue.Create(value.ToString(CultureInfo.InvariantCulture))!;
        static JsonObject Key(float time,float value,float slope=0,int weighted=0,float weight=1f/3)=>new()
        {
            ["time"]=Number(time),["value"]=Number(value),["outSlope"]=Number(slope),["inSlope"]=Number(slope),
            ["weightedMode"]=weighted,["outWeight"]=Number(weight),["inWeight"]=Number(weight)
        };
        static JsonObject Curve(int pre,int post,params JsonObject[] keys)=>new()
        { ["m_Curve"]=new JsonArray(keys.Select(key=>(JsonNode)key).ToArray()),["m_PreInfinity"]=pre,["m_PostInfinity"]=post };
        static JsonObject Value(int mode,float scalar=1,float minimum=0,JsonNode? low=null,JsonNode? high=null)=>new()
        { ["minMaxState"]=mode,["scalar"]=Number(scalar),["minScalar"]=Number(minimum),["minCurve"]=low?.DeepClone(),["maxCurve"]=high?.DeepClone() };
        static (int Bits,Type? Error) Evaluate(Func<float> evaluate)
        {
            try{float result=evaluate();return(float.IsNaN(result)?int.MaxValue:BitConverter.SingleToInt32Bits(result),null);}
            catch(Exception error){return(0,error.GetType());}
        }
        void Compare(JsonNode? value,string name,float[]? times=null,float[]? randoms=null)
        {
            var cache=new AdvParticleCurveCache();bool same=true;
            foreach(float time in times??[-float.PositiveInfinity,-3,-.125f,-0f,0,.001f,.25f,.5f,1,MathF.BitIncrement(1),2,5,12,float.PositiveInfinity,float.NaN])
                foreach(float random in randoms??[-1,0,.125f,.5f,1,2,float.NaN])
                    same&=Evaluate(()=>cache.Value(value,time,random))==Evaluate(()=>AdvParticleCurve.Value(value,time,random));
            check(same,"Per-step particle curve is bitwise/exception equivalent: "+name);
        }

        Compare(null,"null");Compare(Value(0,3),"constant");Compare(Value(3,7,-2),"two constants");
        Compare(Value(1,high:Curve(2,2)),"empty curve");Compare(Value(1,high:Curve(2,2,Key(0,-0f))),"one signed-zero key");
        foreach(int mode in new[]{0,1,2,17})Compare(Value(1,high:Curve(mode,mode,Key(2,10,2),Key(7,20,2))),"infinity mode "+mode);
        Compare(Value(1,high:Curve(1,0,Key(2,10,2),Key(7,20,2))),"different pre/post wrapping");
        Compare(Value(1,high:Curve(2,2,Key(0,0,float.PositiveInfinity),Key(1,10),Key(2,20))),"exact interior key uses left step segment");
        Compare(Value(1,high:Curve(2,2,Key(0,4,float.NaN),Key(1,10))),"NaN slope and time");
        Compare(Value(2,low:Curve(2,2,Key(0,-16777216)),high:Curve(2,2,Key(0,1))),"float min/max cancellation");
        Compare(Value(3,1,-16777216),"float constant interpolation cancellation");
        Compare(Value(2,low:Curve(2,2,Key(0,-float.MaxValue)),high:Curve(2,2,Key(0,float.MaxValue))),"overflow and random endpoints");
        Compare(Value(3,float.MaxValue,-float.MaxValue),"constant interpolation overflow");
        foreach(var (left,right) in new[]{(.04f,.91f),(0f,1f),(1.25f,-.25f)})
            Compare(Value(1,high:Curve(1,0,Key(0,2,3,2,left),Key(1,-4,-2,1,right))),"weighted Bezier "+left+"/"+right,[0,.000001f,.001f,.25f,.5f,.9f,1,2]);
        Compare(Value(1,high:Curve(2,2,Key(0,2,3,2,.04f),Key(1,-4,-2))),"left weighted with double implicit right weight",[.4f]);
        Compare(Value(1,high:Curve(2,2,Key(0,2,3),Key(1,-4,-2,1,.91f))),"right weighted with double implicit left weight",[.25f]);
        Compare(Value(1,high:Curve(0,1,Key(1,3),Key(1,5))),"zero wrap length");
        Compare(Value(1,high:Curve(0,1,Key(2,3),Key(1,5))),"negative wrap length");
        Compare(new JsonObject(),"empty min/max object defaults");Compare(Value(1,high:new JsonObject()),"missing key array");
        var irrelevant=Curve(2,2,Key(0,0,1,1),Key(1,1,1,2));irrelevant["m_Curve"]![0]!["outWeight"]="bad";irrelevant["m_Curve"]![1]!["inWeight"]="bad";
        Compare(Value(1,high:irrelevant),"irrelevant weight bits and malformed unused weights");
        var precision=Value(1,high:Curve(1,1,Key(-16777216,0),Key(1,1)));var preciseCache=new AdvParticleCurveCache();
        check(preciseCache.Value(precision,-8388608,1)==.5f&&preciseCache.Value(precision,33554432,1)==1,
            "Compiled curves keep float segment subtraction and double wrap length distinct");

        var single=Curve(2,2,Key(0,7));single["m_Curve"]![0]!["time"]="bad";single["m_Curve"]![0]!["outSlope"]="bad";
        Compare(Value(1,high:single),"single key ignores all unused fields");
        var malformed=Curve(2,2,Key(0,7),Key(1,8),Key(2,9));malformed["m_Curve"]![0]!["outSlope"]="bad";
        Compare(Value(1,high:malformed),"malformed slope fails only in its selected segment");
        malformed=Curve(2,2,Key(0,7),Key(1,8));malformed["m_PreInfinity"]="bad";
        Compare(Value(1,high:malformed),"unused malformed infinity field");
        Compare(Value(0,4,high:new JsonObject{["m_Curve"]="bad"}),"constant ignores malformed curve");
        Compare(Value(1,high:new JsonObject{["m_Curve"]=new JsonArray((JsonNode?)null,Key(1,2))}),"null key");
        Compare(Value(1,high:new JsonObject{["m_Curve"]="bad"}),"non-array keys");Compare(Value(8),"unknown mode");

        var entries=new AdvParticleCurveCache();bool entryValues=true;
        for(int i=0;i<=AdvParticleCurveCache.EntryLimit;i++){var value=Value(0,i);entryValues&=entries.Value(value,0,1)==i;}
        check(entryValues&&entries.Count==AdvParticleCurveCache.EntryLimit,"Per-step entry 129 falls back without exceeding the 128-entry bound");
        var oversized=Value(1,high:Curve(2,2,Enumerable.Range(0,AdvParticleCurveCache.CurveKeyLimit+1).Select(i=>Key(i,i)).ToArray()));
        var large=new AdvParticleCurveCache();float largeValue=large.Value(oversized,12.5f,1);
        check(largeValue==AdvParticleCurve.Value(oversized,12.5f)&&large.Count==1&&large.KeyCount==0,"A 129-key curve retains only a fallback marker");
        var budget=new AdvParticleCurveCache();bool budgetValues=true;
        for(int i=0;i<17;i++)
        {
            var curve=Curve(2,2,Enumerable.Range(0,AdvParticleCurveCache.CurveKeyLimit).Select(k=>Key(k,k+i)).ToArray());
            var value=Value(2,low:curve,high:curve);budgetValues&=budget.Value(value,30.25f,.3f)==AdvParticleCurve.Value(value,30.25f,.3f);
        }
        check(budgetValues&&budget.KeyCount==AdvParticleCurveCache.TotalKeyLimit,"Per-step total numeric key storage is bounded at 4096 with exact fallback");
        budget.Clear();check(budget.Count==0&&budget.KeyCount==0,"Clearing a step releases every numeric snapshot and fallback marker");
        var mutable=Value(1,high:Curve(2,2,Key(0,2)));var changed=new AdvParticleCurveCache();_=changed.Value(mutable,0,1);
        mutable["maxCurve"]!["m_Curve"]![0]!["value"]=3;changed.Clear();
        check(changed.Value(mutable,0,1)==3,"The next scope observes mutable key values immediately");

        VerifySimulationScopes(check);
    }

    static JsonObject Constant(float value)=>new(){["minMaxState"]=0,["scalar"]=value};
    static JsonObject ParticleData()=>new()
    {
        ["lengthInSec"]=1,["simulationSpeed"]=1,["looping"]=true,["moveWithTransform"]=0,["scalingMode"]=0,["autoRandomSeed"]=true,
        ["startDelay"]=Constant(0),["InitialModule"]=new JsonObject{["maxNumParticles"]=64,["startLifetime"]=Constant(2),["startSpeed"]=Constant(1),["startSize"]=Constant(1),["startRotation"]=Constant(0),["gravityModifier"]=Constant(0),
            ["startColor"]=new JsonObject{["minMaxState"]=0,["maxColor"]=new JsonObject{["r"]=1,["g"]=1,["b"]=1,["a"]=1}}},
        ["EmissionModule"]=new JsonObject{["enabled"]=true,["m_Bursts"]=new JsonArray(),["m_BurstCount"]=0,["rateOverTime"]=Constant(12),["rateOverDistance"]=Constant(0)},
        ["VelocityModule"]=new JsonObject{["enabled"]=true,["x"]=Constant(1),["y"]=Constant(2),["z"]=Constant(3),["speedModifier"]=Constant(1)}
    };

    static void VerifySimulationScopes(Action<bool,string> check)
    {
        (AdvParticles Parent,AdvParticles Child) Graph(bool cached)
        {
            var random=new AdvRandom(173);var parent=new AdvParticles(ParticleData(),random,cached);var child=new AdvParticles(ParticleData(),random,cached);
            parent.Data["SubModule"]=new JsonObject{["enabled"]=true,["subEmitters"]=new JsonArray(new JsonObject
                {["emitter"]=new JsonObject{["gameObject"]="child"},["type"]=0,["properties"]=0,["emitProbability"]=.75f})};
            parent.LinkSubEmitters(_=>child);parent.SetTransform(new AdvTransform());child.SetTransform(new AdvTransform{Position=new(1,2,3)});
            parent.Start();child.Start();parent.Simulate(1);return(parent,child);
        }
        var baseline=Graph(false);var cached=Graph(true);bool exact=State(baseline)==State(cached);bool childActive=true;
        baseline.Child.ActiveInHierarchy=cached.Child.ActiveInHierarchy=()=>childActive;
        for(int frame=0;frame<180;frame++)
        {
            foreach(var pair in new[]{baseline,cached})
            {
                if(frame%9==0)pair.Parent.Bind("VelocityModule.x.scalar")!.Set(frame/17f);
                if(frame==40)pair.Parent.Data["VelocityModule"]!["y"]=Constant(-4);
                if(frame==60)pair.Child.Data["EmissionModule"]!["rateOverTime"]=Constant(7);
                if(frame==120){pair.Parent.Clear();pair.Child.Clear();pair.Parent.Start();pair.Child.Start();}
            }
            if(frame==80)childActive=false;if(frame==100)childActive=true;
            float dt=frame%17==0?.2f:1f/30;baseline.Parent.Simulate(dt);cached.Parent.Simulate(dt);exact&=State(baseline)==State(cached);
        }
        check(exact&&baseline.Child.Particles.Count>0,"Per-step snapshots preserve prewarm, shared random/sub-emitter state, active transitions, Bind edits and frame-to-frame Data replacement");

        var plain=new AdvParticles(ParticleData(),new(81));var scoped=new AdvParticles(ParticleData(),new(81),true);
        foreach(var system in new[]{plain,scoped}){system.Start();system.Simulate(.2f);system.Data["VelocityModule"]!["orbitalX"]=Constant(1);}
        bool rejected=true;
        foreach(var system in new[]{plain,scoped})
        {
            try{system.Simulate(1f/30);rejected=false;}catch(NotSupportedException){}
            system.Data["VelocityModule"]!["orbitalX"]=Constant(0);system.Data["InitialModule"]!["gravityModifier"]=Constant(3);system.Simulate(1f/30);
        }
        check(rejected&&State((plain,plain))==State((scoped,scoped)),"An interrupted Step clears its scope before repaired Data is simulated again");

        // Public standalone callers may modify Data from a custom conversion
        // callback. They keep dynamic evaluation within the very same step.
        var dynamic=new AdvParticles(ParticleData(),new(52));dynamic.Start();dynamic.Simulate(.5f);int calls=0;
        dynamic.WorldToLocal=value=>{calls++;dynamic.Data["VelocityModule"]!["x"]!["scalar"]=calls;return value;};
        dynamic.Simulate(1f/60);
        check(calls>1&&dynamic.Particles.Select(p=>p.Animated.X).Distinct().Count()>1,"Public particle systems still observe callback Data changes between particles within one Step");
    }

    static string State((AdvParticles Parent,AdvParticles Child) pair)
    {
        using var stream=new MemoryStream();using(var writer=new BinaryWriter(stream,System.Text.Encoding.UTF8,true))
        foreach(var system in new[]{pair.Parent,pair.Child})
        {
            writer.Write(system.Time);writer.Write(system.Playing);writer.Write(system.Emitting);writer.Write(system.Particles.Count);
            foreach(var p in system.Particles)
            {
                foreach(var v in new[]{p.Position,p.Velocity,p.Animated,p.Size,p.Rotation,p.Noise,p.Flip}){writer.Write(v.X);writer.Write(v.Y);writer.Write(v.Z);}
                foreach(float value in p.Stable)writer.Write(value);foreach(float value in p.Color)writer.Write(value);foreach(float value in p.Random)writer.Write(value);
                writer.Write(p.Age);writer.Write(p.Life);writer.Write(p.Spin);writer.Write(p.UvRandom);writer.Write(p.UvStart);
                foreach(var birth in p.SubBirth){writer.Write(birth is not null);if(birth is not null){writer.Write(birth.Accumulator);foreach(int burst in birth.Bursts)writer.Write(burst);}}
            }
        }
        return Archive.Hash(stream.ToArray());
    }
}
