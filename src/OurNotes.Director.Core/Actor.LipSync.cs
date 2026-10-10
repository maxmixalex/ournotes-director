using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

public sealed unsafe partial class Actor
{
    NativeMotionSync? mouthAnalysis;JsonNode? mouthSetting;string mouthDll="";
    AdvAudio? mouthAudio;AdvSound? mouthVoice;long mouthSample;
    int[] mouthTargets=[];float[] mouthSmooth=[],mouthDamper=[],mouthNeutral=[],mouthSmoothed=[],mouthDamped=[];
    float mouthFiltered,mouthPresented,mouthHold,mouthSpeechHint,mouthOpenScale=1,mouthFormScale=1,mouthAccumulator;
    bool mouthCanBlend;
    CriVoiceLipSync? criVoice;float criOpening;
    public bool HasMotionSync=>mouthSetting is not null&&!OperatingSystem.IsLinux();
    public bool VoiceLipSyncActive=>mouthVoice is not null;
    public string? LipSyncMissing{get;private set;}
    public float MouthOpen=>ids.TryGetValue("ParamMouthOpenY",out int i)?Values[i]:0;
    void InitializeLipSync(string coreDll)
    {
        int count=gamePrefab["nodes"]!.AsArray().Sum(n=>n!["components"]!.AsArray().Count(c=>Archive.S(c!,"class")=="CubismMouthParameter"));int extra=Math.Max(0,count-1);mouthOpenScale=1+.08f*extra;mouthFormScale=1/(1+.18f*extra);
        var components=gamePrefab["nodes"]![0]!["components"]!.AsArray();var ctrl=components.FirstOrDefault(c=>Archive.S(c!,"class")=="CubismMotionSyncController");
        if(ctrl is null||!components.Any(c=>Archive.S(c!,"class")=="Live2DMotionSyncCriAudioInput"))return;
        var settings=ctrl["_motionSyncData"]!["Settings"]!.AsArray();if(settings.Count!=1)throw new NotSupportedException("Expected one MotionSync setting");mouthSetting=settings[0]!;
        mouthDll=NativeDependencies.MotionSyncLibrary(coreDll)??"";
        var parameters=mouthSetting["CubismParameters"]!.AsArray();mouthTargets=parameters.Select(p=>{if(p?["Parameter"] is not JsonNode parameter)return -1;string path=Archive.S(parameter,"gameObject");return ids.GetValueOrDefault(path[(path.LastIndexOf('/')+1)..],-1);}).ToArray();
        mouthSmooth=parameters.Select(p=>N(p!["Smooth"])).ToArray();mouthDamper=parameters.Select(p=>N(p!["Damper"])).ToArray();mouthNeutral=new float[parameters.Count];mouthSmoothed=new float[parameters.Count];mouthDamped=new float[parameters.Count];
        var silence=mouthSetting["Mappings"]!.AsArray().FirstOrDefault(m=>Archive.S(m!,"AudioParameterId")=="Silence");if(silence is not null)for(int k=0;k<parameters.Count;k++)mouthNeutral[k]=N(silence["Targets"]![k]!["Value"]);
        int form=Array.FindIndex(mouthTargets,i=>i>=0&&Core.ParameterIds[i]=="ParamMouthForm");
        if(form>=0&&silence is not null)mouthCanBlend=mouthSetting["Mappings"]!.AsArray().Select((m,k)=>(m:m!,k)).Any(p=>Archive.S(p.m,"AudioParameterId") is "A" or "I" or "U" or "E" or "O"&&B(mouthSetting["AudioParameters"]![p.k]!["Enabled"])&&Math.Abs(N(mouthSetting["AudioParameters"]![p.k]!["Scale"]))>1e-6&&Math.Abs(N(p.m["Targets"]![form]!["Value"])-mouthNeutral[form])>1e-6);
    }
    public void StartVoiceLipSync(AdvAudio audio,AdvSound voice,CriVoiceLipSync? fallback=null,bool forceCri=false)
    {
        StopVoiceLipSync();pseudoActive=false;
        if(!HasMotionSync||forceCri){
            if(fallback is null){LipSyncMissing="Episode has no CRI Lips analysis data";return;}
            criVoice=fallback;criOpening=mouthFiltered=mouthPresented=mouthHold=0;mouthVoice=voice;LipSyncMissing=null;
            voice.OnFinished.Add(()=>{if(ReferenceEquals(mouthVoice,voice))StopVoiceLipSync();});return;
        }
        if(mouthDll=="")throw new FileNotFoundException("MotionSync 5.0.4 is missing from the application package. Extract the complete release package, or set CUBISM_MOTIONSYNC_DLL.");
        mouthAnalysis??=new(mouthDll,mouthSetting!);mouthAnalysis.ResetInput();mouthAudio=audio;mouthVoice=voice;mouthSample=0;mouthAccumulator=0;
        mouthFiltered=mouthPresented=mouthHold=mouthSpeechHint=0;Array.Copy(mouthNeutral,mouthSmoothed,mouthNeutral.Length);Array.Copy(mouthNeutral,mouthDamped,mouthNeutral.Length);LipSyncMissing=null;
        voice.OnFinished.Add(()=>{if(ReferenceEquals(mouthVoice,voice))StopVoiceLipSync();});
    }
    public void StopVoiceLipSync(){mouthVoice=null;mouthAudio=null;criVoice=null;mouthAnalysis?.ResetInput();}
    float MouthSigned(int i,float value){float lim=Math.Abs(value<0?Core.Min[i]:Core.Max[i]);return lim==0?0:Math.Clamp(value/lim,-1,1);}
    float MouthNormal(int i,float value){float span=Core.Max[i]-Core.Min[i];return span==0?0:Math.Clamp((value-Core.Min[i])/span,0,1);}
    void ApplyVoiceLipSync(float dt)
    {
        if(criVoice is not null){if(ids.TryGetValue("ParamMouthOpenY",out int mouth))Values[mouth]=Core.Min[mouth]+criOpening*(Core.Max[mouth]-Core.Min[mouth]);criOpening=FallbackMouthPolicy(criVoice.Read(),dt);return;}
        if(mouthVoice is null||mouthAudio is null||mouthAnalysis is null)return;
        float[] pcm=mouthAudio.PcmSince(mouthVoice,ref mouthSample);mouthAccumulator+=dt;
        float step=1/Math.Clamp(N(mouthSetting!["PostProcessing"]!["SampleRate"]),1,120);bool analyze=mouthAccumulator>=step;if(analyze)mouthAccumulator%=step;
        mouthAnalysis.Feed(pcm,raw=>{for(int k=0;k<raw.Length;k++){float value=Math.Clamp(raw[k],-1,1);mouthSmoothed[k]=(value*(100-mouthSmooth[k])+mouthSmooth[k]*mouthSmoothed[k])/100;if(Math.Abs(mouthSmoothed[k]-mouthDamped[k])>=mouthDamper[k])mouthDamped[k]=mouthSmoothed[k];}},analyze);
        int oy=ids.GetValueOrDefault("ParamMouthOpenY",-1),fm=ids.GetValueOrDefault("ParamMouthForm",-1);float baseForm=fm>=0?MouthSigned(fm,Values[fm]):0,delta=0,supp=0;bool hasBase=false;
        for(int k=0;k<mouthTargets.Length;k++){int i=mouthTargets[k];if(i<0)continue;float basis=i==oy?0:Values[i];if(i==fm){delta=MouthSigned(i,mouthDamped[k])-MouthSigned(i,mouthNeutral[k]);hasBase=true;}Values[i]=Math.Clamp(basis+mouthDamped[k]-mouthNeutral[k],Core.Min[i],Core.Max[i]);if(i==fm)supp=Math.Max(supp,Math.Abs(MouthNormal(i,Values[i])-MouthNormal(i,basis))*.35f);}
        if(oy<0)return;float open=MouthNormal(oy,Values[oy]);bool speaking;if(open>=.03f){mouthSpeechHint=.08f;speaking=true;}else{mouthSpeechHint=Math.Max(0,mouthSpeechHint-dt);speaking=!(Math.Abs(delta)<.16f&&supp<.12f)&&mouthSpeechHint>0;}
        float target=Math.Clamp(open*.94f*mouthOpenScale,0,1);target=Math.Min(target/(.06f*target+1),.82f);bool wasOpen=mouthHold>0||mouthPresented>=Math.Max(.0125f,.145f*.5f);
        if(speaking||open>=.022f){mouthHold=.05f;if(target>0&&target<=.10f)target=.10f;if(target<=.145f)target=.145f;}
        else{mouthHold=Math.Max(0,mouthHold-dt);if(mouthHold>0){wasOpen=true;if(target>0&&target<=.10f)target=.10f;}else{if(target<.0125f)target=0;wasOpen=true;}}
        float tau=target<=mouthFiltered?.018f:.018f*.65f;target=mouthFiltered+(target-mouthFiltered)*Math.Clamp((float)(1-Math.Exp(-dt/tau)),0,1);mouthFiltered=target;
        tau=target<=mouthPresented?.085f:.028f;target=mouthPresented+(target-mouthPresented)*Math.Clamp((float)(1-Math.Exp(-dt/tau)),0,1);mouthPresented=target;
        float output=Math.Clamp(target,0,1);if(!wasOpen){output=Math.Max(output,.12f);mouthFiltered=Math.Max(mouthFiltered,output);mouthPresented=Math.Max(mouthPresented,output);}
        Values[oy]=Core.Min[oy]+output*(Core.Max[oy]-Core.Min[oy]);
        if(fm>=0&&hasBase){float weight=Math.Clamp((output-.07f)/.03f,0,1),basis=baseForm;if(mouthCanBlend)basis+=(MouthSigned(fm,Core.Defaults[fm])-basis)*weight;float form=Math.Clamp(basis+weight*(1-output*.16f)*delta*.82f*mouthFormScale,-1,1);Values[fm]=form*Math.Abs(form>=0?Core.Max[fm]:Core.Min[fm]);}
    }
}
