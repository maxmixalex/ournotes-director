using System.Numerics;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

public sealed partial class AdvScene
{
    readonly AdvRandom shakeRandom=new(722);
    readonly Dictionary<string,AdvTween> shakeTweens=[];
    int shakeGeneration,cameraGeneration,cameraState;
    float cameraEnableStart,cameraDisableStart,cameraFade,cameraShakeDuration,cameraRandomness;
    int cameraVibrato;Vector3 cameraStrength,cameraShakePosition;
    AdvTween? cameraShakeTween;
    public Func<Vector3>? TalkShakeGet{get;set;}
    public Action<Vector3>? TalkShakeSet{get;set;}
    public Func<Vector3>? StillShakeGet{get;set;}
    public Action<Vector3>? StillShakeSet{get;set;}
    void BindShakes(AdvPlayback p)
    {
        p.Register("Shake",(r,player)=>Shake(r,player));p.Register("CameraShake",(r,player)=>CameraShake(r,player));
        p.OnStop+=()=>{cameraGeneration++;cameraShakeTween?.Kill();Camera.ShakeOffset=Vector3.Zero;foreach(var tween in shakeTweens.Values)tween.Kill();fieldPrefab.Root.Position=backgroundPrefab.Root.Position=Vector3.Zero;StillShakeSet?.Invoke(Vector3.Zero);TalkShakeSet?.Invoke(Vector3.Zero);};
    }
    void ShakeTarget(string name,Func<Vector3> get,Action<Vector3> set,float duration,Vector3 strength,bool vectorBased=true)
    {
        if(shakeTweens.Remove(name,out var old))old.Kill();set(Vector3.Zero);
        var tween=AdvShake.Start(tweens,shakeRandom,get,set,duration,strength,vectorBased:vectorBased);if(tween is not null)shakeTweens[name]=tween;
    }
    IEnumerable<AdvWait> Shake(AdvRow r,AdvPlayback p){yield return p.NoWait(r,ShakeInner(r,p));}
    IEnumerable<AdvWait> ShakeInner(AdvRow r,AdvPlayback p)
    {
        int generation=++shakeGeneration;float duration=p.CalcDuration(r.Number("Duration")),power=Param(r,1),fieldPower=power*Actor.N(settings["_shakeFieldStrength"]),uiPower=power*Actor.N(settings["_shakeUIStrength"]);
        var layers=r.Data["CanvasLayers"]?.AsArray();bool Includes(int n)=>layers is null||layers.Count==0||layers.Any(v=>Actor.N(v)==n);
        if(Includes(0))ShakeTarget("background",()=>backgroundPrefab.Root.Position,v=>backgroundPrefab.Root.Position=v,duration,new(fieldPower,fieldPower,0));
        if(Includes(1)||Includes(2))ShakeTarget("field",()=>fieldPrefab.Root.Position,v=>fieldPrefab.Root.Position=v,duration,new(fieldPower,fieldPower,0));
        if(Includes(7)&&StillShakeGet is not null&&StillShakeSet is not null)ShakeTarget("still",StillShakeGet,StillShakeSet,duration,new(uiPower),false);
        if(Includes(9)&&TalkShakeGet is not null&&TalkShakeSet is not null)ShakeTarget("talk",TalkShakeGet,TalkShakeSet,duration,new(uiPower),false);
        float remaining=duration;while(remaining>0&&!p.Cancelled){yield return loop.YieldUpdate();if(layers is {Count:>0}&&generation!=shakeGeneration)yield break;remaining-=(float)loop.DeltaTime;}
    }
    IEnumerable<AdvWait> CameraShake(AdvRow r,AdvPlayback p){yield return p.NoWait(r,CameraShakeInner(r,p));}
    IEnumerable<AdvWait> CameraShakeInner(AdvRow r,AdvPlayback p)
    {
        float fade=p.CalcDuration(Param(r,1));if(fade<=0)fade=p.CalcDuration(r.Number("Duration"));
        if(cameraState==1){foreach(var w in CameraDisable(fade,p))yield return w;yield break;}
        float strength=Param(r,2);if(strength<=0)strength=Actor.N(settings["_cameraShakeStrength"]);
        cameraEnableStart=loop.Time;cameraState=1;cameraFade=fade;cameraStrength=new(strength,strength,0);cameraShakeDuration=Actor.N(settings["_cameraShakeDuration"]);cameraVibrato=(int)Actor.N(settings["_cameraShakeVibrato"]);cameraRandomness=Actor.N(settings["_cameraShakeRandomness"]);
        int run=++cameraGeneration;cameraShakeTween?.Kill();var detached=r.Data.DeepClone();detached["IsNoWait"]=1;
        p.NoWait(new(r.Index,r.Command,detached),CameraEnableCycles(run,p));yield return loop.Delay(fade);
    }
    AdvTween? CameraCycle(Func<float> weight)
    {
        return cameraShakeTween=AdvShake.Start(tweens,shakeRandom,()=>cameraShakePosition,v=>cameraShakePosition=v,cameraShakeDuration,cameraStrength,cameraVibrato,cameraRandomness,false,()=>{cameraShakePosition*=weight();Camera.ShakeOffset=cameraShakePosition;});
    }
    IEnumerable<AdvWait> CameraEnableCycles(int run,AdvPlayback p)
    {
        while(run==cameraGeneration&&!p.Cancelled){var tween=CameraCycle(()=>cameraFade<=0?1:Math.Clamp((loop.Time-cameraEnableStart)/cameraFade,0,1));if(tween is null)yield break;yield return tween.Completed;if(!tween.Completed.GetResult()||run!=cameraGeneration)yield break;yield return loop.YieldUpdate();}
    }
    IEnumerable<AdvWait> CameraDisable(float fade,AdvPlayback p)
    {
        if(cameraState!=2){cameraDisableStart=loop.Time;cameraState=2;cameraFade=fade;}
        int run=++cameraGeneration;cameraShakeTween?.Kill();
        do{var tween=CameraCycle(()=>cameraFade<=0?0:Math.Clamp(1-(loop.Time-cameraDisableStart)/cameraFade,0,1));if(tween is not null){yield return tween.Completed;if(!tween.Completed.GetResult())yield break;}if(run!=cameraGeneration||p.Cancelled)yield break;}while(loop.Time-cameraDisableStart<cameraFade);
        cameraShakePosition=Camera.ShakeOffset=Vector3.Zero;cameraState=0;
    }
}
