namespace OurNotes.Director.Core;

public enum AdvEase { Unset,Linear,InSine,OutSine,InOutSine,InQuad,OutQuad,InOutQuad,InCubic,OutCubic,InOutCubic,InQuart,OutQuart,InOutQuart,InQuint,OutQuint,InOutQuint,InExpo,OutExpo,InOutExpo,InCirc,OutCirc,InOutCirc,InElastic,OutElastic,InOutElastic,InBack,OutBack,InOutBack,InBounce,OutBounce,InOutBounce,Flash,InFlash,OutFlash,InOutFlash,INTERNAL_Zero,INTERNAL_Custom,DirectorSmoothStep }

// DOTween-style scalar tween. Vector/colour handlers compose scalars with AdvLoop.All.
public sealed class AdvTweens
{
    private readonly AdvLoop loop;
    private readonly List<AdvTween> active=[];
    public AdvTweens(AdvLoop loop){this.loop=loop;loop.On(AdvPhase.Tweens,()=>{foreach(var tween in active.ToArray())tween.Step((float)loop.DeltaTime);});}
    public AdvTween To(Func<float> from,float to,float duration,AdvEase ease,Action<float> apply,bool floatPlugin=false)
    {
        loop.VerifyThread();
        if(!float.IsFinite(to)||!float.IsFinite(duration))throw new ArgumentException("Nonfinite tween target/duration");
        if(ease is >=AdvEase.Flash and <=AdvEase.InOutFlash or AdvEase.INTERNAL_Custom)throw new NotSupportedException("Ease is not supported by the reference engine: "+ease);
        var t=new AdvTween(this,loop,from,to,duration,ease,apply);
        if(duration<=0&&!floatPlugin){apply(to);t.Finish(true);}else active.Add(t);
        return t;
    }
    internal void Remove(AdvTween tween)=>active.Remove(tween);
    internal AdvTween Detached(Func<float> from,float to,float duration,AdvEase ease,Action<float> apply)=>new(this,loop,from,to,duration,ease,apply);
    public static float Evaluate(AdvEase ease,float time,float duration,float amplitude=1.70158f,float period=0)
    {
        float u=time/duration,h=time/(duration*.5f),v,s;
        const float pi=3.1415927f,halfPi=1.5707964f,twoPi=6.2831855f;
        float Pow(float x)=>(float)Math.Pow(2,x);
        float Sin(float x)=>(float)Math.Sin(x);
        float Cos(float x)=>(float)Math.Cos(x);
        float Sqrt(float x)=>(float)Math.Sqrt(x);
        static float Bounce(float x)
        {
            if(x<.36363637f)return (x*x)*7.5625f;
            float c,k;
            if(x>=.72727275f){if(x>=.90909094f){c=.95454544f;k=.984375f;}else{c=.8181818f;k=.9375f;}}
            else{c=.54545456f;k=.75f;}
            float y=x-c;return ((y*y)*7.5625f)+k;
        }
        switch(ease)
        {
            case AdvEase.DirectorSmoothStep:return u*u*(3-2*u);
            case AdvEase.Linear:return u;
            case AdvEase.InSine:return 1-Cos(u*halfPi);
            case AdvEase.OutSine:return Sin(u*halfPi);
            case AdvEase.InOutSine:return (Cos((time*pi)/duration)-1)*-.5f;
            case AdvEase.InQuad:return u*u;
            case AdvEase.InOutQuad:if(h<1)return h*(h*.5f);v=h-1;return ((v*(v-2))-1)*-.5f;
            case AdvEase.InCubic:return u*(u*u);
            case AdvEase.OutCubic:v=u-1;return v*(v*v)+1;
            case AdvEase.InOutCubic:if(h<1)return h*((h*h)*.5f);v=h-2;return (v*(v*v)+2)*.5f;
            case AdvEase.InQuart:return u*(u*(u*u));
            case AdvEase.OutQuart:v=u-1;return -((((v*v)*v)*v)-1);
            case AdvEase.InOutQuart:if(h<1)return h*(h*(h*(h*.5f)));v=h-2;return (v*((v*v)*v)-2)*-.5f;
            case AdvEase.InQuint:return u*(u*(u*(u*u)));
            case AdvEase.OutQuint:v=u-1;return v*(((v*v)*v)*v)+1;
            case AdvEase.InOutQuint:if(h<1)return h*((h*h)*((h*h)*.5f));v=h-2;return (v*(((v*v)*v)*v)+2)*.5f;
            case AdvEase.InExpo:return time==0?0:Pow((u-1)*10);
            case AdvEase.OutExpo:return time==duration?1:1-Pow((time*-10)/duration);
            case AdvEase.InOutExpo:if(time==0)return 0;if(time==duration)return 1;return (h>=1?2-Pow((h-1)*-10):Pow((h-1)*10))*.5f;
            case AdvEase.InCirc:return -(Sqrt(1-u*u)-1);
            case AdvEase.OutCirc:v=u-1;return Sqrt(1-v*v);
            case AdvEase.InOutCirc:if(h<1)return (Sqrt(1-h*h)-1)*-.5f;v=h-2;return (Sqrt(1-v*v)+1)*.5f;
            case AdvEase.InElastic:case AdvEase.OutElastic:case AdvEase.InOutElastic:
                if(time==0)return 0;if(u==1)return 1;
                float p=period!=0?period:duration*(ease==AdvEase.InOutElastic?.45000002f:.3f);
                float a=amplitude>=1?amplitude:1;
                s=amplitude>=1?(p/twoPi)*(float)Math.Asin(1/amplitude):p*.25f;
                if(ease==AdvEase.OutElastic)return ((a*Pow(u*-10))*Sin(((u*duration-s)*twoPi)/p))+1;
                if(ease==AdvEase.InElastic){v=u-1;return -((a*Pow(v*10))*Sin(((v*duration-s)*twoPi)/p));}
                v=h-1;float w=(a*Pow(v*(h>=1?-10:10)))*Sin(((v*duration-s)*twoPi)/p);return h>=1?w*.5f+1:w*-.5f;
            case AdvEase.InBack:return (u*u)*(u*(amplitude+1)-amplitude);
            case AdvEase.OutBack:v=u-1;return (v*v)*((amplitude+1)*v+amplitude)+1;
            case AdvEase.InOutBack:s=amplitude*1.525f;if(h<1)return ((h*h)*(h*(s+1)-s))*.5f;v=h-2;return ((v*v)*(s+(s+1)*v)+2)*.5f;
            case AdvEase.InBounce:return 1-Bounce((duration-time)/duration);
            case AdvEase.OutBounce:return Bounce(u);
            case AdvEase.InOutBounce:return duration*.5f<=time?Bounce(((time+time)-duration)/duration)*.5f+.5f:(1-Bounce((duration-(time+time))/duration))*.5f;
            case >=AdvEase.Flash and <=AdvEase.InOutFlash:case AdvEase.INTERNAL_Custom:throw new NotSupportedException("Unsupported reference ease: "+ease);
            case AdvEase.INTERNAL_Zero:return 1;
            default:return -(u*(u-2));
        }
    }
}
public sealed class AdvTween
{
    private readonly AdvTweens runner;
    private readonly Func<float> from;
    private readonly float target,duration;
    private readonly AdvEase ease;
    private readonly Action<float> apply;
    private float? start;
    private float change;
    public float Position{get;private set;}
    internal float Duration=>duration;
    public bool Done{get;private set;}
    public AdvWait Completed{get;}
    internal AdvTween(AdvTweens runner,AdvLoop loop,Func<float> from,float to,float duration,AdvEase ease,Action<float> apply)
    {this.runner=runner;this.from=from;target=to;this.duration=duration;this.ease=ease;this.apply=apply;Completed=new(loop);}
    internal void Step(float delta)
    {
        Sample(Position+delta);
        if(Position>=duration)Finish(true);
    }
    internal void Sample(float position)
    {
        Initialize();
        Position=Math.Clamp(position,0,Math.Max(0,duration));
        float k=duration<=0?1:AdvTweens.Evaluate(ease,Position,duration);
        apply(start!.Value+change*k);
    }
    internal void Initialize(){if(start is null){start=from();change=target-start.Value;}}
    internal void Finish(bool completed){if(Done)return;Done=true;runner.Remove(this);Completed.Resolve(completed);}
    public void Kill()=>Finish(false);
}
