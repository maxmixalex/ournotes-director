using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

public sealed unsafe partial class Actor
{
    NativeMotionSync? mouthAnalysis;
    JsonNode? mouthSetting;
    string mouthDll="";
    AdvAudio? mouthAudio;
    AdvSound? mouthVoice;
    long mouthSample;
    int[] mouthTargets=[];
    float[] mouthSmooth=[],mouthDamper=[],mouthNeutral=[],mouthSmoothed=[],mouthDamped=[];
    float mouthFiltered,mouthPresented,mouthHold,mouthOpenScale=1;
    CriVoiceLipSync? criVoice;
    bool envelopeActive,releaseDisplayedMouth;
    string lastLipSyncEngine="";
    readonly VoiceEnvelopeLipSync envelope=new();
    public bool HasMotionSync=>mouthSetting is not null&&mouthDll.Length>0&&OperatingSystem.IsWindows();
    public bool VoiceLipSyncActive=>mouthVoice is not null;
    public string LipSyncEngine=>envelopeActive?"语音音量（备用）":PseudoLipSyncActive?"无语音文字口型":lastLipSyncEngine.Length>0?lastLipSyncEngine:"待语音播放";
    public string? LipSyncMissing{get;private set;}
    // Report the actual pose submitted to Cubism, not the newer simulation array.
    public float MouthOpen=>ids.TryGetValue("ParamMouthOpenY",out int index)?((float*)Core.Pointer("ParameterValues"))[index]:0;
    void InitializeLipSync(string coreDll)
    {
        var components=gamePrefab["nodes"]![0]!["components"]!.AsArray();
        var controller=components.FirstOrDefault(c=>Archive.S(c!,"class")=="CubismMotionSyncController");
        if(controller is null||!components.Any(c=>Archive.S(c!,"class")=="Live2DMotionSyncCriAudioInput"))return;
        var settings=controller["_motionSyncData"]!["Settings"]!.AsArray();if(settings.Count!=1)throw new NotSupportedException("Expected one MotionSync setting");
        mouthSetting=settings[0]!;mouthDll=NativeDependencies.MotionSyncLibrary(coreDll)??"";var parameters=mouthSetting["CubismParameters"]!.AsArray();
        mouthTargets=parameters.Select(p=>{if(p?["Parameter"] is not JsonNode parameter)return -1;string path=Archive.S(parameter,"gameObject");return ids.GetValueOrDefault(path[(path.LastIndexOf('/')+1)..],-1);}).ToArray();
        mouthSmooth=parameters.Select(p=>Math.Clamp(N(p!["Smooth"]),0,100)).ToArray();mouthDamper=parameters.Select(p=>Math.Max(0,N(p!["Damper"]))).ToArray();
        mouthNeutral=new float[parameters.Count];mouthSmoothed=new float[parameters.Count];mouthDamped=new float[parameters.Count];
        var silence=mouthSetting["Mappings"]!.AsArray().FirstOrDefault(m=>Archive.S(m!,"AudioParameterId")=="Silence");
        if(silence is not null)for(int k=0;k<parameters.Count;k++)mouthNeutral[k]=N(silence["Targets"]![k]!["Value"]);
    }
    public void StartVoiceLipSync(AdvAudio audio,AdvSound voice,CriVoiceLipSync? fallback=null,bool forceCri=false)
    {
        StopVoiceLipSync();pseudoActive=false;mouthAudio=audio;mouthVoice=voice;mouthSample=0;releaseDisplayedMouth=false;
        if(HasMotionSync&&!forceCri)
        {
            mouthAnalysis??=new(mouthDll,mouthSetting!);mouthAnalysis.ResetInput();Array.Copy(mouthNeutral,mouthSmoothed,mouthNeutral.Length);Array.Copy(mouthNeutral,mouthDamped,mouthNeutral.Length);
            LipSyncMissing=null;lastLipSyncEngine="MotionSync";
        }
        else if(fallback is not null){criVoice=fallback;LipSyncMissing=null;lastLipSyncEngine="CRI Lips";}
        else{envelopeActive=true;envelope.Reset();lastLipSyncEngine="语音音量（备用）";LipSyncMissing="此模型缺少可用的音素映射，当前使用实际语音音量。";}
        voice.OnFinished.Add(()=>{if(ReferenceEquals(mouthVoice,voice))StopVoiceLipSync();});
    }
    public void StopVoiceLipSync()
    {
        releaseDisplayedMouth|=mouthVoice is not null;mouthVoice=null;mouthAudio=null;criVoice=null;envelopeActive=false;envelope.Reset();
    }
    void SynchronizeDisplayedMouth()
    {
        if(!gameAnimation)return;
        // The source game buffers the general animation pose. Mouth shapes follow audible
        // samples instead; modifying just these cells avoids a second csmUpdateModel call.
        int opening=ids.GetValueOrDefault("ParamMouthOpenY",-1);
        foreach(var pose in gameDisplay)
        {
            if(opening>=0)pose[opening]=Values[opening];
            foreach(int index in mouthTargets)if(index>=0)pose[index]=Values[index];
        }
    }
    void ApplyVoiceLipSync(float dt)
    {
        if(mouthVoice is null)
        {if(releaseDisplayedMouth){SynchronizeDisplayedMouth();releaseDisplayedMouth=false;}return;}
        if(envelopeActive&&mouthAudio is not null)
        {
            var pcm=mouthAudio.PcmSince(mouthVoice,ref mouthSample,AdvAudio.SamplesPerFrame);float opening=envelope.Feed(pcm,dt);
            if(ids.TryGetValue("ParamMouthOpenY",out int mouth))Values[mouth]=Core.Min[mouth]+opening*(Core.Max[mouth]-Core.Min[mouth]);SynchronizeDisplayedMouth();return;
        }
        if(criVoice is not null)
        {
            float opening=Math.Clamp(criVoice.Read(),0,1);if(ids.TryGetValue("ParamMouthOpenY",out int mouth))Values[mouth]=Core.Min[mouth]+opening*(Core.Max[mouth]-Core.Min[mouth]);SynchronizeDisplayedMouth();return;
        }
        if(mouthAudio is null||mouthAnalysis is null)return;
        var input=mouthAudio.PcmSince(mouthVoice,ref mouthSample,AdvAudio.SamplesPerFrame);
        mouthAnalysis.Feed(input,raw=>
        {
            for(int k=0;k<Math.Min(raw.Length,mouthTargets.Length);k++)
            {
                int index=mouthTargets[k];if(index<0||!float.IsFinite(raw[k]))continue;float value=Math.Clamp(raw[k],Core.Min[index],Core.Max[index]);
                mouthSmoothed[k]=(value*(100-mouthSmooth[k])+mouthSmooth[k]*mouthSmoothed[k])/100;
                if(Math.Abs(mouthSmoothed[k]-mouthDamped[k])>=mouthDamper[k])mouthDamped[k]=mouthSmoothed[k];
            }
        });
        int openingIndex=ids.GetValueOrDefault("ParamMouthOpenY",-1);
        for(int k=0;k<mouthTargets.Length;k++)
        {
            int index=mouthTargets[k];if(index<0)continue;float basis=index==openingIndex?Math.Clamp(0,Core.Min[index],Core.Max[index]):Values[index];
            Values[index]=Math.Clamp(basis+mouthDamped[k]-mouthNeutral[k],Core.Min[index],Core.Max[index]);
        }
        SynchronizeDisplayedMouth();
    }
}
