using System.Numerics;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

// Bind exported DOTweenAnimation primitives to their original canvas properties.
public static class AdvStillProperties
{
    public static IReadOnlyList<AdvTween> Start(IReadOnlyDictionary<string,AdvCanvasNode> nodes,AdvCanvasNode owner,JsonNode animation,AdvTweens tweens,float duration,AdvEase ease,bool detached=false,AdvRandom? random=null)
    {
        int kind=(int)Actor.N(animation["animationType"]),type=(int)Actor.N(animation["targetType"]);
        string path=kind==5
            ?(Actor.B(animation["targetIsSelf"])?owner.Path:Archive.S(animation["targetGO"]!,"gameObject"))
            :Archive.S(animation["target"]!,"transform",Archive.S(animation["target"]!,"gameObject"));
        if(!nodes.TryGetValue(path,out var target))throw new InvalidDataException("Still tween target missing: "+path);
        var result=new List<AdvTween>();
        void Add(Func<float> get,Action<float> set,float end,bool scalar=false)=>result.Add(detached?tweens.Detached(get,end,duration,ease,set):tweens.To(get,end,duration,ease,set,scalar));
        var v=AdvPrefab.Vector(animation["endValueV3"]);
        float value=Actor.N(animation["endValueFloat"]);
        bool option=Actor.B(animation["optionalBool0"]);
        switch(kind,type){
            case (9,5):case (12,5):
                if(option)throw new NotSupportedException("Still anchored-position snapping");
                if(kind==12&&duration<=0)break;
                int mode=(int)Actor.N(animation["optionalShakeRandomnessMode"]);
                if(kind==12&&mode is not (0 or 1))throw new NotSupportedException("Still shake randomness mode");
                int vibrato=(int)Actor.N(animation["optionalInt0"]);float amount=Actor.N(animation["optionalFloat0"]);
                v.Z=0;
                double sourceDuration=animation["duration"]?.GetValue<double>()??duration;
                var (ends,times)=kind==9?AdvPunch.Waypoints(sourceDuration,v,vibrato,amount)
                    :AdvShake.Waypoints(sourceDuration,v,vibrato,amount,Actor.B(animation["optionalBool1"]),random??throw new ArgumentNullException(nameof(random)),true,mode==1);
                float total=0;foreach(float t in times)total+=t;
                Vector3[] starts=new Vector3[ends.Length],changes=new Vector3[ends.Length];
                float Initialize(){
                    var origin=new Vector3(target.AnchoredPosition,0);
                    for(int i=0;i<ends.Length;i++){ends[i]+=origin;starts[i]=i==0?origin:ends[i-1];changes[i]=ends[i]-starts[i];}
                    return 0;
                }
                void Apply(float elapsed){
                    float at=0;int index=times.Length-1;
                    for(int i=0;i<times.Length;i++){if(elapsed<=at+times[i]){index=i;break;}at+=times[i];}
                    float k=times[index]<=0?1:AdvTweens.Evaluate(kind==9?AdvEase.OutQuad:AdvEase.Linear,elapsed-at,times[index]);
                    var pos=starts[index]+changes[index]*k;target.AnchoredPosition=new(pos.X,pos.Y);
                }
                result.Add(detached?tweens.Detached(Initialize,total,total,AdvEase.Linear,Apply):tweens.To(Initialize,total,total,AdvEase.Linear,Apply,true));
                break;
            case (3,11):
                if(Actor.N(animation["optionalRotationMode"])!=0)throw new NotSupportedException("Still rotation mode is not supported by the reference engine");
                Vector3 start=default,change=default;
                float Begin(){start=EulerAngles(WorldRotation(target));change=new(Delta(start.X,v.X),Delta(start.Y,v.Y),Delta(start.Z,v.Z));return 0;}
                Add(Begin,t=>{
                    var angles=start+change*t;
                    var world=Quaternion.CreateFromYawPitchRoll(angles.Y*MathF.PI/180,angles.X*MathF.PI/180,angles.Z*MathF.PI/180);
                    target.Rotation=target.Parent is null?world:Quaternion.Inverse(WorldRotation(target.Parent))*world;
                },1,true);break;
            case (5,11):
                if(option)v=new Vector3(value);
                Add(()=>target.Scale.X,x=>target.Scale=target.Scale with{X=x},v.X);
                Add(()=>target.Scale.Y,y=>target.Scale=target.Scale with{Y=y},v.Y);
                Add(()=>target.Scale.Z,z=>target.Scale=target.Scale with{Z=z},v.Z);break;
            case (1,5):
                if(option)throw new NotSupportedException("Still anchored-position snapping");
                Add(()=>target.AnchoredPosition.X,x=>target.AnchoredPosition=target.AnchoredPosition with{X=x},v.X);
                Add(()=>target.AnchoredPosition.Y,y=>target.AnchoredPosition=target.AnchoredPosition with{Y=y},v.Y);
                Add(()=>target.LocalZ,z=>target.LocalZ=z,v.Z);break;
            case (21,5):
                float x=option?value:Actor.N(animation["endValueV2"]?["x"]),y=option?value:Actor.N(animation["endValueV2"]?["y"]);
                Add(()=>target.SizeDelta.X,z=>target.SizeDelta=target.SizeDelta with{X=z},x);
                Add(()=>target.SizeDelta.Y,z=>target.SizeDelta=target.SizeDelta with{Y=z},y);break;
            case (6,3):case (7,3):
                var color=target.Image?["m_Color"]??throw new InvalidDataException("Still graphic tween requires an Image: "+path);
                foreach(string channel in kind==6?new[]{"r","g","b","a"}:new[]{"a"}){
                    string c=channel;
                    Add(()=>Actor.N(color[c]),z=>color[c]=z,kind==6?Actor.N(animation["endValueColor"]?[c]):value);
                }break;
            case (7,2):
                if(target.Component("CanvasGroup") is null)throw new InvalidDataException("Still fade requires CanvasGroup: "+path);
                Add(()=>target.Alpha,z=>target.Alpha=z,value,true);break;
            default:throw new NotSupportedException($"Still animation property {kind}/{type}: {path}");
        }
        return result;
    }
    static Quaternion WorldRotation(AdvCanvasNode node)
    {
        var q=Quaternion.Identity;
        for(AdvCanvasNode? n=node;n is not null;n=n.Parent)q=n.Rotation*q;
        return q;
    }
    static float Delta(float start,float end)
    {
        if(end>360)end%=360;
        float d=end-start,a=Math.Abs(d);
        return a>180?(d>0?-(360-a):360-a):d;
    }
    // Unity Quaternion.eulerAngles uses Z-X-Y order, including its singularity threshold.
    static Vector3 EulerAngles(Quaternion q)
    {
        double m01=2*((double)q.X*q.Y-(double)q.W*q.Z),m02=2*((double)q.X*q.Z+(double)q.W*q.Y),m00=1-2*((double)q.Y*q.Y+(double)q.Z*q.Z);
        double m10=2*((double)q.X*q.Y+(double)q.W*q.Z),m11=1-2*((double)q.X*q.X+(double)q.Z*q.Z),m12=2*((double)q.Y*q.Z-(double)q.W*q.X),m22=1-2*((double)q.X*q.X+(double)q.Y*q.Y);
        double x,y,z;
        if(m12<.999){if(m12>-.999){x=Math.Asin(-m12);y=Math.Atan2(m02,m22);z=Math.Atan2(m10,m11);}else{x=Math.PI/2;y=Math.Atan2(m01,m00);z=0;}}
        else{x=-Math.PI/2;y=Math.Atan2(-m01,m00);z=0;}
        static float Degrees(double a)=>(float)((a<-.0001?a+2*Math.PI:a>2*Math.PI-.0001?a-2*Math.PI:a)*180/Math.PI);
        return new(Degrees(x),Degrees(y),Degrees(z));
    }
}
