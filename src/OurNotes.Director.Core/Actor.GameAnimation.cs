using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

public sealed unsafe partial class Actor
{
    sealed class PlayingMotion(JsonNode data,float start,float fade,float speed)
    {
        public JsonNode Data=data;public float Start=start,Fade=fade>=0?fade:N(data["FadeInTime"]),Speed=speed;
        public float End=N(data["MotionLength"])>0?start+N(data["MotionLength"])/speed:-1;
    }
    sealed class PlayingExpression(JsonNode data)
    {
        public JsonNode Data=data;public float Time,End,Weight;
        public float FadeIn=N(data["FadeInTime"])<0?1:N(data["FadeInTime"]),FadeOut=N(data["FadeOutTime"])<0?1:N(data["FadeOutTime"]);
    }
    bool gameAnimation;
    float[] gameStore=[];
    readonly Dictionary<int,JsonNode> gameFades=[];
    readonly List<PlayingMotion> gameMotions=[];
    readonly List<PlayingExpression> gameExpressions=[];
    readonly HashSet<int> gameExpressionParameters=[];
    readonly Queue<float[]> gameDisplay=[];
    string gameExpression="";float gameExpressionFade;
    (string Name,float Fade)? gamePendingMotion,gamePendingExpression;
    void ResumeGameRequests(){var motion=gamePendingMotion;var expression=gamePendingExpression;gamePendingMotion=gamePendingExpression=null;foreach(int i in gameExpressionParameters)gameStore[i]=Core.Defaults[i];if(motion.HasValue)Motion(motion.Value.Name,motion.Value.Fade);if(expression.HasValue)Expression(expression.Value.Name,expression.Value.Fade);}
    float[] harmonicTimes=[],harmonicSpeeds=[];
    static float SineFade(float value)=>value<=0?0:value>=1?1:.5f-.5f*MathF.Cos(MathF.PI*value);
    void InitializeGameAnimation()
    {
        if(gameAnimation)return;gameAnimation=true;gameStore=(float[])Core.Defaults.Clone();
        var list=Prefab.Find("CubismFadeController").Component["CubismFadeMotionList"]!;
        for(int i=0;i<list["MotionInstanceIds"]!.AsArray().Count;i++)gameFades[list["MotionInstanceIds"]![i]!.GetValue<int>()]=list["CubismFadeMotionObjects"]![i]!;
        foreach(var clip in clips.Values)if(!gameFades.ContainsKey(clip.InstanceId))throw new InvalidDataException("Missing fade data: "+clip.Name);
        foreach(var expr in expressions.Values)foreach(var p in expr["Parameters"]!.AsArray())if(ids.TryGetValue(Archive.S(p!,"Id"),out int i))gameExpressionParameters.Add(i);
        harmonicTimes=new float[harmonics.Count];harmonicSpeeds=Prefab.Find("CubismHarmonicMotionController").Component["ChannelTimescales"]!.AsArray().Select(v=>N(v)).ToArray();
    }
    public void BeginGameWarmup(){Motion(DefaultMotion,-1);Expression(DefaultExpression,-1);physics?.Stabilize(Values);Core.Update(Values);}
    void GameHarmonics(float dt)
    {
        for(int j=0;j<harmonics.Count;j++){var (i,c)=harmonics[j];int channel=(int)N(c["Channel"]);float speed=channel==0?Spec.Breath?1:0:harmonicSpeeds[channel];harmonicTimes[j]+=dt*speed;float duration=N(c["Duration"],3);while(harmonicTimes[j]>duration)harmonicTimes[j]-=duration;
            float range=Core.Max[i]-Core.Min[i],origin=Math.Clamp(Core.Min[i]+N(c["NormalizedOrigin"])*range,Core.Min[i],Core.Max[i]);Values[i]=Math.Clamp(Values[i]+origin+range*N(c["NormalizedRange"])*MathF.Sin(harmonicTimes[j]*MathF.Tau/duration),Core.Min[i],Core.Max[i]);}
    }
    void GameStartMotion(Clip clip,float fade)
    {
        var data=gameFades[clip.InstanceId];var next=new PlayingMotion(data,elapsed,fade,Spec.Speed);
        if(gameMotions.Count>0){var previous=gameMotions[^1];float end=elapsed+N(previous.Data["FadeOutTime"]);if(Math.Max(previous.End,0)>end)previous.End=end;
            if(next.Fade>0)previous.End=Math.Max(previous.End,elapsed+Math.Max(0,N(previous.Data["FadeOutTime"])));}
        gameMotions.Add(next);current=clip;motionTime=0;
    }
    void GameStartExpression(string name,float fade)
    {
        if(gameExpression.Equals(name,StringComparison.OrdinalIgnoreCase))return;
        foreach(int i in gameExpressionParameters){Values[i]=Core.Defaults[i];gameStore[i]=Core.Defaults[i];}
        if(gameExpressions.Count>0){var last=gameExpressions[^1];float end=last.Time+last.FadeOut;if(last.End==0||end<last.End)last.End=end;}
        gameExpressions.Add(new(expressions[name]));gameExpression=name;gameExpressionFade=fade;
    }
    void GameFadeMotion()
    {
        if(gameMotions.Count<2)return;var last=gameMotions[^1];float newest=(elapsed-last.Start)*last.Speed;
        bool fading=last.Data["ParameterFadeInTimes"]!.AsArray().Any(f=>newest<=last.Fade||N(f)>=0&&newest<=N(f));
        if(!fading){gameMotions.RemoveAll(m=>m!=last&&m.End<elapsed);return;}
        Array.Copy(gameStore,Values,Values.Length);
        foreach(var m in gameMotions){float t=(elapsed-m.Start)*m.Speed,fi=m.Fade>0?SineFade(t/m.Fade):1,outTime=N(m.Data["FadeOutTime"]),fo=outTime>0&&m.End>=0?SineFade((m.End-elapsed)/outTime):1;
            var parameters=m.Data["ParameterIds"]!.AsArray();for(int j=0;j<parameters.Count;j++)if(ids.TryGetValue(parameters[j]!.ToString(),out int i)){
                float target=Math.Clamp(AdvParameterLoop.Curve(m.Data["ParameterCurves"]![j]!["m_Curve"]!.AsArray(),t),Core.Min[i],Core.Max[i]);
                float pin=N(m.Data["ParameterFadeInTimes"]![j]),pout=N(m.Data["ParameterFadeOutTimes"]![j]),weight=fi*fo;
                if(!(pin<0&&pout<0)){float wi=pin>=0?pin>0?SineFade(t/pin):1:fi,wo=pout>=0?m.End-t>=0&&pout>0?SineFade((m.End-t)/pout):1:fo;weight=wi*wo;}
                Values[i]=Math.Clamp(Values[i]+(target-Values[i])*weight,Core.Min[i],Core.Max[i]);
            }
        }
    }
    void GameApplyExpressions(float dt)
    {
        float weight=0;var entries=new Dictionary<int,(float Add,float Multiply,float Override)>();
        for(int index=0;index<gameExpressions.Count;index++){
            var expression=gameExpressions[index];expression.Time+=dt;float fade=gameExpressionFade>=0?gameExpressionFade:expression.FadeIn;
            float fi=fade!=0?SineFade(expression.Time/fade):1,fo=expression.End!=0&&expression.End>=0?SineFade((expression.End-expression.Time)/expression.FadeOut):1;expression.Weight=fi*fo;
            var parameters=expression.Data["Parameters"]!.AsArray().Where(p=>ids.ContainsKey(Archive.S(p!,"Id"))).ToDictionary(p=>ids[Archive.S(p!,"Id")],p=>p!);
            foreach(int i in parameters.Keys)entries.TryAdd(i,(0,1,Values[i]));
            foreach(int i in entries.Keys.ToArray()){
                float add=0,multiply=1,over=Values[i];if(parameters.TryGetValue(i,out var p)){float v=N(p["Value"]);switch((int)N(p["Blend"])){case 1:add=v;break;case 2:multiply=v;break;default:over=v;break;}}
                if(index==0)entries[i]=(add,multiply,over);else{var previous=entries[i];float w=expression.Weight,iw=1-w;entries[i]=(add*w+previous.Add*iw,multiply*w+previous.Multiply*iw,over*w+Values[i]*iw);}
            }
            weight+=fi;
        }
        if(gameExpressions.Count>1&&gameExpressions[^1].Weight>=1)gameExpressions.RemoveRange(0,gameExpressions.Count-1);
        weight=Math.Min(weight,1);foreach(var (i,v) in entries)Values[i]=Math.Clamp(Values[i]+((v.Add+v.Override)*v.Multiply-Values[i])*weight,Core.Min[i],Core.Max[i]);
    }
    public void ResetGameAnimation()
    {
        if(!gameAnimation)return;gameMotions.Clear();gameDisplay.Clear();gameExpression="";gameExpressions.Clear();gamePendingMotion=gamePendingExpression=null;animationPaused=false;ParameterLoop.Stop(0);
        foreach(int i in gameExpressionParameters)gameStore[i]=Core.Defaults[i];
    }
}
