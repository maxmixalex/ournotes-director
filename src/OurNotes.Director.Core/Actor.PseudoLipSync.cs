namespace OurNotes.Director.Core;

public sealed unsafe partial class Actor
{
    bool pseudoActive,pseudoHoldOpen,pseudoOpen;
    float pseudoTime,pseudoStopTime,pseudoTimer,pseudoSpeed=1,pseudoMultiplier,pseudoScale=.8f,pseudoVelocity,pseudoBefore,pseudoMouth;
    public bool PseudoLipSyncActive=>pseudoActive;
    public void StartTimedPseudoLipSync(int textLength,float speed,float multiplier=1,bool holdOpen=false)
    {
        StopVoiceLipSync();pseudoActive=true;pseudoHoldOpen=holdOpen;pseudoOpen=false;
        pseudoTime=.14f*textLength;pseudoStopTime=pseudoTimer=pseudoVelocity=pseudoBefore=pseudoMouth=0;
        pseudoSpeed=speed;pseudoMultiplier=multiplier;pseudoScale=.8f;
        mouthFiltered=mouthPresented=mouthHold=0;LipSyncMissing=null;
    }
    static float MouthDamp(float current,float target,ref float velocity,float smoothTime,float dt)
    {
        float omega=2/Math.Max(.0001f,smoothTime),x=omega*dt,decay=1/(((x+1)+x*x*.48f)+x*x*x*.235f);
        float change=current-target,temp=(omega*change+velocity)*dt,output=(current-change)+decay*(change+temp);
        float oldVelocity=velocity;velocity=decay*(oldVelocity-omega*temp);
        if((target-current>0)!=(output<=target)){velocity=dt!=0?0:oldVelocity;output=target;}return output;
    }
    float FallbackMouthPolicy(float raw,float dt)
    {
        raw=Math.Clamp(raw,0,1);float target=Math.Clamp(raw*1.03f*mouthOpenScale,0,1);
        target=Math.Min(target/(.07f*target+1),.88f);bool wasOpen=mouthHold>0||mouthPresented>=.085f;
        if(raw>=.026f){mouthHold=.06f;if(target>0&&target<=.11f)target=.11f;if(target<=.17f)target=.17f;}
        else{mouthHold=Math.Max(0,mouthHold-dt);if(mouthHold>0){wasOpen=true;if(target>0&&target<=.11f)target=.11f;}else{if(target<.015f)target=0;wasOpen=true;}}
        float tau=target<=mouthFiltered?.022f:.022f*.65f;target=mouthFiltered+(target-mouthFiltered)*Math.Clamp((float)(1-Math.Exp(-dt/tau)),0,1);mouthFiltered=target;
        tau=target<=mouthPresented?.10f:.032f;target=mouthPresented+(target-mouthPresented)*Math.Clamp((float)(1-Math.Exp(-dt/tau)),0,1);mouthPresented=target;
        float output=Math.Clamp(target,0,1);if(!wasOpen){output=Math.Max(output,.13f);mouthFiltered=Math.Max(mouthFiltered,output);mouthPresented=Math.Max(mouthPresented,output);}return output;
    }
    void ApplyPseudoLipSync(float dt)
    {
        if(!pseudoActive)return;
        // CubismMouthController consumes the previous late update's opening.
        if(ids.TryGetValue("ParamMouthOpenY",out int i))Values[i]=Core.Min[i]+pseudoMouth*(Core.Max[i]-Core.Min[i]);
        bool stop=pseudoTime<0;float step=dt*pseudoSpeed,target;
        if(pseudoHoldOpen){pseudoScale=MouthDamp(pseudoScale,stop?0:Math.Clamp(pseudoMultiplier,0,1),ref pseudoVelocity,(stop?.04f:.025f)/pseudoSpeed,dt);
            if(stop)pseudoOpen=true;else{pseudoTimer-=step;if(pseudoTimer<=0){pseudoOpen=true;pseudoTimer=random.Range(.05f,.15f);}}
            target=pseudoOpen?pseudoScale:0;
        }else{
            if(stop)pseudoOpen=false;else{pseudoTimer-=step;if(pseudoTimer<=0){if(pseudoOpen&&random.Value()>.75f){pseudoOpen=false;pseudoTimer=random.Range(.05f,.15f);}else{pseudoOpen=true;pseudoTimer=random.Range(.05f,.15f);pseudoScale=random.Range(.6f,1);}}}
            target=pseudoOpen?pseudoScale:0;
        }
        pseudoBefore=MouthDamp(pseudoBefore,target,ref pseudoVelocity,(pseudoMouth<target?.025f:.04f)/pseudoSpeed,dt);
        pseudoMouth=FallbackMouthPolicy(pseudoBefore*(pseudoHoldOpen?1:pseudoMultiplier),dt);
        if(stop){pseudoStopTime-=step;if(pseudoStopTime<=0)pseudoActive=false;}
        else{pseudoTime-=step;if(pseudoTime<=0)pseudoStopTime=.3f;}
    }
}
