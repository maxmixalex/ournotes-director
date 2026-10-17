using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

public sealed record AdvCue(long SoundId,int Category,float[] Pcm,int Channels,int? LoopStart=null,int? LoopEnd=null)
{
    public int Samples=>Pcm.Length/Channels;
}

// PCM stays at 48 kHz; one 30 Hz simulation frame corresponds to exactly 1600 sample frames.
// The host writes MixFrame() for frame zero, then advances the loop for successive output frames.
public sealed class AdvAudio
{
    public const int SampleRate=48000,SamplesPerFrame=1600;
    private readonly AdvLoop loop;
    private readonly Dictionary<long,AdvCue> cues;
    private readonly Dictionary<int,AdvSound> playing=[];
    private readonly LinkedList<long> decodedOrder=[];
    private readonly Dictionary<long,LinkedListNode<long>> decodedNodes=[];
    // Only lazily decoded cues are evictable; caller-supplied/video PCM has no reload provider.
    public long DecodedCacheLimitBytes{get;init;}=128L*1024*1024;
    public long DecodedCacheBytes{get;private set;}
    private readonly float[] categoryVolumes=[.7f,1,1,1],options=[1,1,1,1];
    private int nextId=1,volumeToken;
    private readonly List<int> seIds=[];
    public int LastBgmOrSeFrame{get;private set;}=-1;
    public IReadOnlyCollection<AdvSound> Playing=>playing.Values;
    public Func<long,AdvCue>? ResolveCue{get;set;}
    public void AddCue(AdvCue cue){if(cue.Channels is <1 or >2||cue.Pcm.Length%cue.Channels!=0)throw new InvalidDataException("Invalid PCM cue");if(decodedNodes.Remove(cue.SoundId,out var node)){decodedOrder.Remove(node);DecodedCacheBytes-=(long)cues[cue.SoundId].Pcm.Length*sizeof(float);}cues[cue.SoundId]=cue;}
    void TrimDecodedCache()
    {
        for(var node=decodedOrder.First;node is not null&&DecodedCacheBytes>DecodedCacheLimitBytes;){
            var next=node.Next;long id=node.Value;
            if(!playing.Values.Any(s=>s.Cue.SoundId==id)){
                DecodedCacheBytes-=(long)cues[id].Pcm.Length*sizeof(float);cues.Remove(id);decodedNodes.Remove(id);decodedOrder.Remove(node);
            }
            node=next;
        }
    }
    public AdvAudio(AdvLoop loop,IEnumerable<AdvCue> cues)
    {
        this.loop=loop;this.cues=cues.ToDictionary(c=>c.SoundId);
        foreach(var cue in this.cues.Values)
        {
            if(cue.Category is <0 or >3||cue.Channels is <1 or >2||cue.Pcm.Length%cue.Channels!=0||cue.Samples==0||cue.Pcm.Any(v=>!float.IsFinite(v)))throw new InvalidDataException("Invalid PCM cue");
            if(cue.LoopStart.HasValue!=cue.LoopEnd.HasValue||cue.LoopStart<0||cue.LoopEnd>cue.Samples||cue.LoopStart>=cue.LoopEnd)throw new InvalidDataException("Invalid cue loop range");
        }
        loop.On(AdvPhase.Update,Update);
    }
    public AdvCue GetCue(long soundId)
    {
        if(!cues.TryGetValue(soundId,out var cue)){
            cue=ResolveCue?.Invoke(soundId)??throw new InvalidDataException($"Sound {soundId} not preloaded");
            if(cue.SoundId!=soundId||cue.Category is <0 or >3||cue.Channels is <1 or >2||cue.Samples==0)throw new InvalidDataException("Invalid resolved audio cue");
            cues.Add(soundId,cue);
            decodedNodes.Add(soundId,decodedOrder.AddLast(soundId));DecodedCacheBytes+=(long)cue.Pcm.Length*sizeof(float);
        }
        else if(decodedNodes.TryGetValue(soundId,out var cached)){decodedOrder.Remove(cached);decodedOrder.AddLast(cached);}
        return cue;
    }
    public AdvSound Play(long soundId,bool repeat=false,float crossFade=0,bool automaticCrossFade=false,double startSeconds=0,bool fadeIn=false,float volume=1)
    {
        var cue=GetCue(soundId);
        if(!double.IsFinite(startSeconds)||startSeconds<0||!float.IsFinite(crossFade)||crossFade<0||!float.IsFinite(volume))throw new ArgumentException("Invalid audio playback options");
        if(automaticCrossFade&&cue.Category==0){foreach(var old in playing.Values.Where(i=>i.Cue.Category==0).ToArray())Stop(old.Id,crossFade);fadeIn=true;}
        int offset=checked((int)Math.Round(Math.Truncate(startSeconds*1000)/1000*SampleRate,MidpointRounding.AwayFromZero));
        var sound=new AdvSound(nextId++,cue,loop.Frame,offset,repeat){Gain=volume};
        if(fadeIn&&crossFade>0){sound.Fade=1;sound.FadeStart=loop.Time;sound.FadeDuration=crossFade;}
        playing.Add(sound.Id,sound);TrimDecodedCache();return sound;
    }
    public bool IsPlaying(int id)=>playing.TryGetValue(id,out var s)&&!s.Stopped&&(s.Repeat||Position(s)<s.Cue.Samples);
    public void PlayChatSe(long id){if(id<=0)return;var sound=Play(id,crossFade:.3f);seIds.Add(sound.Id);LastBgmOrSeFrame=loop.Frame;}
    public void Stop(int id,float fade=0)
    {
        if(!playing.TryGetValue(id,out var sound))return;
        if(fade>0){sound.Fade=2;sound.FadeStart=loop.Time;sound.FadeDuration=fade;return;}
        Kill(sound);
    }
    public void StopAll(int category=-1,float fade=0){foreach(var s in playing.Values.Where(s=>category<0||s.Cue.Category==category).ToArray())Stop(s.Id,fade);}
    private void Kill(AdvSound sound){if(sound.Stopped)return;sound.Stopped=true;playing.Remove(sound.Id);foreach(var callback in sound.OnFinished.ToArray())callback();TrimDecodedCache();}
    private long Position(AdvSound sound)=>(long)(loop.Frame-sound.StartFrame)*SamplesPerFrame+sound.StartSample;
    private void Update()
    {
        foreach(var s in playing.Values.ToArray())
        {
            if(!s.Repeat&&Position(s)>=s.Cue.Samples){Kill(s);continue;}
            if(s.Fade==2){s.Gain=1-(loop.Time-s.FadeStart)/s.FadeDuration;if(s.Gain<=0)Kill(s);}
            else if(s.Fade==1){s.Gain=(loop.Time-s.FadeStart)/s.FadeDuration;if(s.Gain>=1){s.Gain=1;s.Fade=0;}}
        }
    }
    public float[] MixFrame()
    {
        var mix=new float[SamplesPerFrame*2];MixFrame(mix);return mix;
    }
    public void MixFrame(Span<float> mix)
    {
        if(mix.Length!=SamplesPerFrame*2)throw new ArgumentException("Audio output must hold one stereo frame",nameof(mix));
        mix.Clear();
        foreach(var sound in playing.Values)
        {
            var cue=sound.Cue;var position=Position(sound);float gain=sound.Gain*categoryVolumes[cue.Category]*options[cue.Category];
            for(int i=0;i<SamplesPerFrame;i++)
            {
                long at=Wrap(sound,position+i);if(at<0||at>=cue.Samples)continue;
                int n=checked((int)at)*cue.Channels;
                mix[i*2]+=cue.Pcm[n]*gain;mix[i*2+1]+=cue.Pcm[n+(cue.Channels==1?0:1)]*gain;
            }
        }
    }
    private static long Wrap(AdvSound sound,long position)
    {
        if(!sound.Repeat)return position;
        int start=sound.Cue.LoopStart??0,end=sound.Cue.LoopEnd??sound.Cue.Samples;
        return position>=end?start+(position-start)%(end-start):position;
    }
    // Source channel zero, before gain: suitable for the later native MotionSync/CRI Lips integration.
    public float[] PcmSince(AdvSound sound,ref long previousSample,int lookAheadSamples=0)
    {
        long now=Math.Max(0,Position(sound)-sound.StartSample+lookAheadSamples);int count=checked((int)Math.Max(0,now-previousSample));
        var result=new float[count];
        for(int i=0;i<count;i++){long at=Wrap(sound,sound.StartSample+previousSample+i);if(at>=0&&at<sound.Cue.Samples)result[i]=sound.Cue.Pcm[checked((int)at)*sound.Cue.Channels];}
        previousSample=now;return result;
    }
    public int ReadRecentMono(AdvSound sound,Span<float> destination)
    {
        long elapsed=Math.Max(0,Position(sound)-sound.StartSample);int count=(int)Math.Min(destination.Length,elapsed);
        for(int i=0;i<count;i++)
        {
            long at=Wrap(sound,sound.StartSample+elapsed-count+i);
            destination[i]=at>=0&&at<sound.Cue.Samples?sound.Cue.Pcm[checked((int)at)*sound.Cue.Channels]:0;
        }
        return count;
    }
    private static int Category(string name)=>name switch{"Bgm"=>0,"Se"=>1,"Voice"=>2,_=>-1};
    public float GetVolume(string name){int c=Category(name);return c>=0?categoryVolumes[c]:float.NaN;}
    public void ChangeVolume(string name,float volume)
    {
        if(name=="All"){ChangeVolume("Bgm",volume);ChangeVolume("Se",volume);ChangeVolume("Voice",volume);return;}
        int c=Category(name);if(c>=0)categoryVolumes[c]=(c==0?.7f:1)*volume;
    }
    public void Bind(AdvPlayback player)
    {
        player.Register("Bgm",(r,p)=>Bgm(r,p));player.Register("Se",(r,p)=>Se(r,p));
        player.Register("SoundVolume",(r,p)=>Volume(r,p));
        player.OnStop+=()=>{StopAll(0);StopAll(2);foreach(int id in seIds)Stop(id);};
    }
    private static float Parameter(AdvRow row,string key)=>float.TryParse(row.Text(key),NumberStyles.Float,CultureInfo.InvariantCulture,out float value)&&float.IsFinite(value)?value:0;
    private static long Id(AdvRow row,string key)=>long.TryParse(row.Text(key),out var value)?value:0;
    private void AutoStop(AdvPlayback p,int id,float duration,float fade)
    {if(duration>0)loop.Delay(duration).OnCompleted(()=>{if(IsPlaying(id))Stop(id,fade);});}
    private IEnumerable<AdvWait> Bgm(AdvRow row,AdvPlayback player)
    {
        long id=Id(row,"BgmID");float fade=Parameter(row,"Parameter1");
        if(id<1)StopAll(0,fade);
        else{var sound=Play(id,true,Math.Max(0,fade),true,Math.Max(0,Parameter(row,"Parameter2")));AutoStop(player,sound.Id,row.Number("Duration"),Parameter(row,"Parameter3"));}
        LastBgmOrSeFrame=loop.Frame;yield break;
    }
    private IEnumerable<AdvWait> Se(AdvRow row,AdvPlayback player)
    {
        // SE always forks, irrespective of IsNoWait.
        var fork=row.Data.DeepClone();fork["IsNoWait"]=1;
        yield return player.NoWait(row with{Data=fork},SeInner(row,player));
    }
    private IEnumerable<AdvWait> SeInner(AdvRow row,AdvPlayback player)
    {
        var wait=player.CommonDelay(player.CalcDuration(row.Number("DelaySeconds")));yield return wait;if(!wait.GetResult())yield break;
        long id=Id(row,"SeID");
        if(row.Text("Parameter1").Equals("stop",StringComparison.OrdinalIgnoreCase))
        {foreach(int pid in seIds){if(id>0&&(!playing.TryGetValue(pid,out var s)||s.Cue.SoundId!=id))continue;Stop(pid,Parameter(row,"Parameter2"));}}
        else if(id>0)
        {float fade=Parameter(row,"Parameter3");var sound=Play(id,crossFade:Math.Max(0,fade),fadeIn:fade>0);seIds.Add(sound.Id);AutoStop(player,sound.Id,player.CalcDuration(row.Number("Duration")),player.CalcDuration(Parameter(row,"Parameter2")));}
        if(row.Text("Parameter1").Equals("stop",StringComparison.OrdinalIgnoreCase)||id>0)LastBgmOrSeFrame=loop.Frame;
    }
    private IEnumerable<AdvWait> Volume(AdvRow row,AdvPlayback player){yield return player.NoWait(row,VolumeInner(row,player));}
    private IEnumerable<AdvWait> VolumeInner(AdvRow row,AdvPlayback player)
    {
        int token=++volumeToken;string category=row.Text("Parameter1"),lower=category.ToLowerInvariant();if(lower=="se")yield break;
        string[] names=lower=="all"||category==""?["Bgm","Voice"]:[category];
        float volume=Parameter(row,"Parameter2"),duration=player.CalcDuration(row.Number("Duration"));
        if(duration<=0){foreach(var name in names)ChangeVolume(name,volume);yield break;}
        var start=names.Select(GetVolume).ToArray();float elapsed=0;
        while(elapsed<duration)
        {
            if(player.Cancelled)yield break;
            elapsed+=(float)loop.DeltaTime;float t=Math.Clamp(elapsed/duration,0,1);
            for(int i=0;i<names.Length;i++)ChangeVolume(names[i],start[i]+(volume-start[i])*t);
            yield return loop.YieldUpdate();if(token!=volumeToken||player.Cancelled)yield break;
        }
        foreach(var name in names)ChangeVolume(name,volume);
    }
    public static AdvCue Decode(Archive archive,string site,JsonNode manifest,JsonNode episode,long soundId,string ffmpeg="ffmpeg")
    {
        var sound=episode["sounds"]?[soundId.ToString(CultureInfo.InvariantCulture)]??throw new InvalidDataException("Sound absent from episode");
        if(sound["_isRandomPitch"]?.GetValue<bool>()==true)throw new NotSupportedException("Random pitch is not implemented by reference engine");
        var sheet=episode["cuesheets"]![Archive.S(sound,"_soundCueSheetID")]!;
        string prefix="audio/"+Archive.S(sheet,"_cueSheetName")+"/";
        var meta=JsonNode.Parse(archive.Logical(site,manifest,prefix+"cues.json"))![Archive.S(sound,"_cueName")]!;
        byte[] encoded=archive.Logical(site,manifest,prefix+Archive.S(meta,"file"));
        int channels=meta["channels"]!.GetValue<int>(),rate=meta["sampleRate"]!.GetValue<int>();
        if(channels is <1 or >2||rate!=SampleRate)throw new NotSupportedException("Native audio import currently requires mono/stereo 48kHz cues");
        // Some archive M4A files have trailing moov tables and require seekable input.
        var temporary=NativeTemporary.Create("audio",Path.GetExtension(Archive.S(meta,"file")));
        try
        {
            File.WriteAllBytes(temporary,encoded);
            var start=new ProcessStartInfo(ffmpeg){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(var a in new[]{"-v","error","-nostdin","-i",temporary,"-f","f32le","-ar","48000","-ac",channels.ToString(CultureInfo.InvariantCulture),"pipe:1"})start.ArgumentList.Add(a);
            using var process=Process.Start(start)??throw new IOException("FFmpeg did not start");
            using var output=new MemoryStream();var read=process.StandardOutput.BaseStream.CopyToAsync(output);var error=process.StandardError.ReadToEndAsync();
            try{read.GetAwaiter().GetResult();process.WaitForExit();if(process.ExitCode!=0)throw new IOException(error.GetAwaiter().GetResult());}
            finally{if(!process.HasExited)process.Kill(true);}
            var bytes=output.ToArray();if(bytes.Length==0||bytes.Length%(4*channels)!=0)throw new InvalidDataException("Empty or invalid decoded float PCM: "+error.GetAwaiter().GetResult());
            var pcm=new float[bytes.Length/4];Buffer.BlockCopy(bytes,0,pcm,0,bytes.Length);
            return new(soundId,sound["_category"]!.GetValue<int>(),pcm,channels,meta["loopStart"]?.GetValue<int>(),meta["loopEnd"]?.GetValue<int>());
        }
        finally{NativeTemporary.Delete(temporary);}
    }
}
public sealed class AdvSound(int id,AdvCue cue,int frame,int startSample,bool repeat)
{
    public int Id{get;}=id;
    public AdvCue Cue{get;}=cue;
    public int StartFrame{get;}=frame;
    public int StartSample{get;}=startSample;
    public bool Repeat{get;}=repeat;
    public bool Stopped{get;internal set;}
    public float Gain{get;internal set;}=1;
    internal int Fade;
    internal float FadeStart,FadeDuration;
    public List<Action> OnFinished{get;}=[];
}
