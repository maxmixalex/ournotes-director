// CRI Lips openY path, ported from ournotes-player (AGPL-3.0-only).
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

public sealed class CriLipsData
{
    public static CriLipsData? FromArchive(Archive archive,string site,JsonNode manifest,JsonNode definition)
    {
        CriLipsData? Read(JsonNode source,JsonNode document)
        {
            if(document["crilips"] is not JsonNode data)return null;
            try{return new(JsonNode.Parse(archive.Logical(site,source,Archive.S(data,"descriptor")))!,archive.Logical(site,source,Archive.S(data,"data")));}
            catch(FileNotFoundException){return null;}
            catch(DirectoryNotFoundException){return null;}
        }
        var direct=Read(manifest,definition);if(direct is not null)return direct;
        // MotionSync-only episodes omit the shared CRI neural weights. Linux
        // uses the same archived CRI analyser, sourced from another episode. The same
        // lookup also serves Windows commands that explicitly request CRI for a group.
        foreach(string file in Directory.EnumerateFiles(archive.SitePath(site,"stories"),"*.json").Order()){
            var source=Archive.ReadJson(file);
            if(source["files"] is not JsonObject files||!files.Any(p=>p.Key.Contains("crilips",StringComparison.OrdinalIgnoreCase)))continue;
            JsonNode document;
            try{document=JsonNode.Parse(archive.Logical(site,source,"story.json"))!;}
            catch(FileNotFoundException){continue;}
            catch(DirectoryNotFoundException){continue;}
            if(Read(source,document) is {} found)return found;
        }
        return null;
    }
    public JsonNode Frontend{get;}public JsonNode Constants{get;}internal Dictionary<string,float[]> Weights{get;}=[];
    public CriLipsData(JsonNode descriptor,byte[] blob)
    {
        if(Archive.S(descriptor,"dtype")!="float32"||Archive.S(descriptor,"order")!="LE"||!BitConverter.IsLittleEndian)throw new InvalidDataException("CRI Lips requires float32 LE data");
        if(blob.Length!=(int)Actor.N(descriptor["total_floats"])*4)throw new InvalidDataException("Truncated CRI Lips weights");
        Frontend=descriptor["frontend"]!;Constants=descriptor["constants"]!;
        foreach(var block in descriptor["blocks"]!.AsArray()){int offset=(int)Actor.N(block!["offset"]),count=(int)Actor.N(block["count"]);Weights.Add(Archive.S(block,"name"),MemoryMarshal.Cast<byte,float>(blob.AsSpan(checked(offset*4),checked(count*4))).ToArray());}
    }
    internal static float[] Array(JsonNode node)=>node.AsArray().Select(n=>Actor.N(n)).ToArray();
}
sealed class CriNet
{
    readonly Dictionary<string,float[]> w;readonly float[] conv=new float[220],h1=new float[128],h2=new float[128],h24=new float[24],h40=new float[40],h20=new float[20],h5=new float[5];
    public CriNet(CriLipsData data)=>w=data.Weights;
    void Dense(float[] input,int count,string key,float[] output,bool relu=false)
    {
        var weights=w[key+".w"];var bias=w[key+".b"];int n=output.Length;System.Array.Clear(output);
        for(int i=0;i<count;i++){float x=input[i];int start=i*n;for(int o=0;o<n;o++)output[o]+=x*weights[start+o];}
        for(int o=0;o<n;o++){output[o]+=bias[o];if(relu&&output[o]<0)output[o]=0;}
    }
    static void Softmax(float[] values){var exp=new double[values.Length];double sum=0;for(int i=0;i<values.Length;i++){exp[i]=Math.Exp(values[i]);sum+=exp[i];}sum=Math.Max(sum,2.2250738585072014e-308);for(int i=0;i<values.Length;i++)values[i]=(float)(exp[i]/sum);}
    void BatchNorm(float[] values,string key){var beta=w[key+".beta"];var gamma=w[key+".gamma"];var mean=w[key+".mean"];var variance=w[key+".var"];for(int i=0;i<values.Length;i++)values[i]=Math.Max(0,beta[i]+gamma[i]*(values[i]-mean[i])/MathF.Sqrt(variance[i]+.0001f));}
    public float[] InternalClass(float[] input)
    {
        var weights=w["net1.conv.w"];var bias=w["net1.conv.b"];
        for(int t=0;t<22;t++)for(int ch=0;ch<10;ch++){float acc=0;for(int r=0;r<3;r++)for(int c=0;c<3;c++)acc+=input[r*24+t+c]*weights[ch*9+r*3+c];conv[ch*22+t]=acc+bias[ch];}
        Dense(conv,220,"net1.d1",h1,true);Dense(h1,128,"net1.d2",h2,true);Dense(h2,128,"net1.d3",h1,true);Dense(h1,128,"net1.d4",h24);Softmax(h24);return h24;
    }
    public float[] Vowels(float[] input){Dense(input,72,"net3.d1",h40);BatchNorm(h40,"net3.bn1");Dense(h40,40,"net3.d2",h20);BatchNorm(h20,"net3.bn2");Dense(h20,20,"net3.d3",h5);Softmax(h5);return h5;}
}
sealed class CriDiscretizer
{
    readonly float rate,suppression,smoothing,release;readonly int holdFrames;readonly float[] wildcard;
    readonly float[][] buffers;readonly int[] positions=new int[6];readonly float[] last=[0,0,0,0,0,1],sm=new float[6];
    int holdCount;bool released=true;
    public float[] Last=>last;
    public CriDiscretizer(JsonNode cfg,float rate)
    {
        this.rate=rate;suppression=Actor.N(cfg["suppression"]);smoothing=Actor.N(cfg["smoothing_sec"]);release=Actor.N(cfg["release_sec"]);holdFrames=(int)(rate*Actor.N(cfg["hold_sec"]));wildcard=CriLipsData.Array(cfg["wildcard"]!);
        int count=(int)MathF.Ceiling(rate*Actor.N(cfg["max_window_sec"]));buffers=Enumerable.Range(0,6).Select(_=>new float[count]).ToArray();System.Array.Fill(buffers[5],1);
    }
    float Smooth(int j,float window){float count=rate*Math.Max(1/rate,window),sum=0,remaining=count;int i=positions[j],n=buffers[j].Length;do{i=(n-1+i)%n;float v=buffers[j][i];if(remaining<=1)v*=remaining;sum+=v;remaining-=1;}while(remaining>0);return sum/count;}
    public float[] Process(float[] post,int type)
    {
        bool hold=false;float window,sup=1;
        if(type==4){if(holdCount<holdFrames){hold=true;holdCount++;window=released?release:smoothing;}else{released=true;window=release;}}
        else{released=false;window=smoothing;holdCount=0;if(type==0)sup=suppression;else if(type<0||type>4)return last;}
        for(int j=0;j<6;j++){float v=hold?last[j]:type==0?(j<post.Length?post[j]:0):type==3?wildcard[j]:j==5?1:0;buffers[j][positions[j]]=v;positions[j]=(positions[j]+1)%buffers[j].Length;sm[j]=Smooth(j,window);}
        int top=0,second=0;float best=-float.MaxValue;for(int j=0;j<5;j++)if(sm[j]>best){best=sm[j];top=j;}best=-float.MaxValue;for(int j=0;j<5;j++)if(j!=top&&sm[j]>best){best=sm[j];second=j;}
        for(int j=0;j<6;j++){float v;if(j==top||j==5)v=sm[j];else if(j==second)v=sup*sm[j];else{v=0;buffers[j][(positions[j]+buffers[j].Length-1)%buffers[j].Length]=0;}if(!hold||released)last[j]=v;}return last;
    }
}
sealed class CriMouth
{
    readonly float[] coef,ring,af;readonly float silence;float previous,opening;
    public CriMouth(CriLipsData data){var p=data.Frontend["mouth_open"]!;coef=CriLipsData.Array(p["vowel_coef"]!);ring=new float[(int)(Actor.N(p["anti_shake_sec"])*Actor.N(data.Frontend["update_rate_hz"]))];af=CriLipsData.Array(data.Constants["antiflap_defaults"]!);silence=Actor.N(data.Constants["silence_coef"]);}
    public float Process(float[] vowels)
    {
        float amount=0,best=0;for(int i=0;i<5;i++)if(vowels[i]>best){amount=vowels[i]*coef[i];best=vowels[i];}float cur=Math.Clamp(amount,0,1);
        if(ring.Length>0){System.Array.Copy(ring,0,ring,1,ring.Length-1);ring[0]=cur;cur=(ring.Max()+ring.Min())*.5f;}
        float c=Math.Min(cur,1),delta=Math.Abs(c-previous);cur=delta<=af[2]?(delta<af[3]?(c*af[1]+af[0]*previous)/(af[0]+af[1]):(c+previous)*.5f):(c*af[0]+af[1]*previous)/(af[0]+af[1]);previous=cur;
        if(Math.Abs(opening*silence)<=1&&ring.All(x=>Math.Abs(x*silence)<=1)&&Math.Abs(previous*silence)<=1){System.Array.Clear(ring);previous=0;}opening=cur;return cur;
    }
}
sealed class CriCore
{
    readonly CriMel general,vowel;readonly CriNet net;readonly CriDiscretizer discretizer;
    readonly float[] ringG,ringV,coefficients;readonly int[] motions,motionTable,typeTable;readonly int bands,labialHold;readonly float threshold;
    int position,holdCount;bool hold;
    public int Hop{get;}public float[] Blend=>discretizer.Last;
    public CriCore(CriLipsData data)
    {
        var fe=data.Frontend;int rate=(int)Actor.N(fe["resample_hz"]);Hop=(int)(rate*Actor.N(fe["hop_ms"])/1000);int length=(int)(rate*Actor.N(fe["frame_ms"])/1000);bands=(int)Actor.N(fe["mel_bands"]);if(bands!=24)throw new NotSupportedException("CRI Lips neural network requires 24 bands");
        general=new(length,Hop,bands,rate,Actor.N(fe["mel_high_hz"]),Archive.S(fe,"window_internal_class"));vowel=new(length,Hop,bands,rate,Actor.N(fe["mel_high_hz"]),Archive.S(fe,"window_vowel"));
        net=new(data);discretizer=new(fe["discretizer"]!,1000/Actor.N(fe["hop_ms"]));threshold=Actor.N(fe["silence_threshold_db"]);labialHold=(int)(Actor.N(fe["labial_hold_sec"])*1000/Actor.N(fe["hop_ms"]));
        motions=Enumerable.Repeat(4,(int)Actor.N(fe["motion_class_buffer"])).ToArray();motionTable=CriLipsData.Array(data.Constants["motion_class_table"]!).Select(x=>(int)x).ToArray();typeTable=CriLipsData.Array(data.Constants["discretizer_type_table"]!).Select(x=>(int)x).ToArray();
        ringG=new float[3*bands];ringV=new float[3*bands];coefficients=new float[bands];
        void Init(CriMel mel,float[] ring){mel.Initialize();mel.Coefficients(coefficients);System.Array.Copy(coefficients,0,ring,bands,bands);System.Array.Copy(coefficients,0,ring,2*bands,bands);mel.Shift();}Init(general,ringG);Init(vowel,ringV);
    }
    void Slide(CriMel mel,float[] ring){mel.Coefficients(coefficients);System.Array.Copy(ring,bands,ring,0,bands*2);System.Array.Copy(coefficients,0,ring,bands*2,bands);}
    int Principal(){var counts=new int[5];foreach(int c in motions)counts[c]++;if(counts[1]>0)return 1;if(counts[2]>0)return 2;int best=0,cls=5;for(int k=0;k<5;k++)if(counts[k]>best){best=counts[k];cls=k;}return cls;}
    public bool Process(ReadOnlySpan<float> pcm)
    {
        if(!general.Put(pcm))return false;Slide(general,ringG);float db=general.Power()*10;
        if(hold){if(labialHold<=holdCount||db<threshold){hold=false;holdCount=0;}else holdCount++;}
        int cls=24;if(threshold<=db){var p=net.InternalClass(ringG);if(p.Any(x=>!float.IsFinite(x)))return false;float best=p[0];cls=0;for(int k=1;k<25;k++){float v=k<24?p[k]:0;if(v>best){best=v;cls=k;}}}
        motions[position]=cls<24?motionTable[cls]:4;position=(position+1)%motions.Length;if(Principal()==1&&holdCount==0)hold=true;general.Shift();
        if(!vowel.Put(pcm))return false;Slide(vowel,ringV);int type=hold?2:cls<25?typeTable[cls]:3;discretizer.Process(net.Vowels(ringV),type);vowel.Shift();return true;
    }
}
public sealed class CriLipsAnalyzer
{
    readonly CriBiquad filter;readonly CriResampler resampler;readonly CriCore core;readonly CriMouth mouth;
    readonly float[] block,filtered,work;readonly double tick;readonly Queue<(long End,float Opening)> recent=[];
    int filled,have;long consumed;float opening;
    public CriLipsAnalyzer(CriLipsData data,int inputRate=48000)
    {
        filter=new(inputRate,Actor.N(data.Frontend["biquad_lpf_cutoff_hz"]));resampler=new(inputRate,(int)Actor.N(data.Frontend["resample_hz"]));core=new(data);mouth=new(data);block=new float[8*core.Hop+64];filtered=new float[block.Length];work=new float[4*core.Hop+64];tick=inputRate/(double)Actor.N(data.Frontend["server_hz"]);
    }
    public float Feed(ReadOnlySpan<float> pcm)
    {
        int used=0;for(;;){int length=(resampler.Need(core.Hop-have)+7)&~7,take=Math.Min(length-filled,pcm.Length-used);pcm.Slice(used,take).CopyTo(block.AsSpan(filled));filled+=take;used+=take;if(filled!=length)break;
            filter.Process(block,filtered,length);have+=resampler.Process(filtered,length,work,have);if(core.Process(work.AsSpan(0,core.Hop)))opening=mouth.Process(core.Blend);System.Array.Copy(work,core.Hop,work,0,have-core.Hop);have-=core.Hop;filled=0;recent.Enqueue((consumed+used,opening));if(recent.Count>64)recent.Dequeue();}
        consumed+=pcm.Length;double limit=Math.Floor(consumed/tick)*tick;float result=0;foreach(var item in recent)if(item.End<=limit)result=item.Opening;return result;
    }
}
public sealed class CriVoiceLipSync
{
    readonly AdvAudio audio;readonly AdvSound voice;readonly CriLipsAnalyzer analyzer;long sample;
    public CriVoiceLipSync(CriLipsData data,AdvAudio audio,AdvSound voice){this.audio=audio;this.voice=voice;analyzer=new(data);}
    public float Read()=>analyzer.Feed(audio.PcmSince(voice,ref sample));
}
