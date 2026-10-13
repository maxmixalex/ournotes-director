using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

// Sequential native FFmpeg decode. Seeking the story still executes every intervening game frame.
public sealed class AdvVideo : IDisposable
{
    readonly Archive archive;readonly string site,ffmpeg;readonly JsonNode manifest,document,settings;
    readonly AdvLoop loop;readonly AdvTweens tweens;readonly AdvAudio audio;readonly AdvUi ui;readonly OffscreenGl gl;
    Process? decoder;Task<string>? errors;string? temporary;Pixels? pixels;uint texture;
    int decoded=-1,total,started,sound;double fps;float alpha;AdvTween? fade;bool showing,timeline;float target;
    public bool Playing=>showing&&(loop.Frame-started)/30d<total/fps;
    public bool Visible=>showing;
    public float Alpha=>alpha;
    internal bool TimingOnly{get;init;}
    public AdvWait SetAlpha(float value,float duration)
    {
        if(!showing)return loop.Ready();
        if(duration<=0){fade?.Kill();alpha=value;return loop.Ready();}
        Fade(value,duration);return fade!.Completed;
    }
    double Elapsed=>Math.Min(total/fps,Math.Floor((loop.Frame-started)/30d*fps)/fps);
    public AdvVideo(Archive archive,string site,JsonNode manifest,JsonNode definition,JsonNode settings,AdvLoop loop,AdvTweens tweens,AdvAudio audio,AdvUi ui,OffscreenGl gl,string ffmpeg)
    {
        this.archive=archive;this.site=site;this.manifest=manifest;this.settings=settings;this.loop=loop;this.tweens=tweens;this.audio=audio;this.ui=ui;this.gl=gl;this.ffmpeg=ffmpeg;
        string file=Archive.S(definition,"videos");document=file==""?new JsonObject():JsonNode.Parse(archive.Logical(site,manifest,file))!;
    }
    public void Bind(AdvPlayback p){p.Register("Clip",Clip);p.Register("Movie",Movie);p.VideoDelay=Delay;ui.VideoPlaying=()=>Playing;}
    IEnumerable<AdvWait> Delay(AdvRow r,AdvPlayback p)
    {
        if(timeline){target+=r.Number("Duration");do{yield return loop.YieldUpdate();}while(Playing&&Elapsed+1e-5<target&&!p.Cancelled);if(Elapsed+1e-5>=target)yield break;timeline=false;
            foreach(var w in p.DelayAdjusted(p.CalcDuration(Math.Max(0,target-(float)Elapsed))))yield return w;
        }else foreach(var w in p.DelayAdjusted(p.CalcDuration(r.Number("Duration"))))yield return w;
    }
    IEnumerable<AdvWait> Clip(AdvRow r,AdvPlayback p)
    {
        float duration=p.CalcDuration(r.Number("Parameter1"));long.TryParse(r.Text("VideoID"),out long id);
        if(showing||id<=0){foreach(var w in p.DelayAdjusted(p.CalcDuration(Actor.N(settings["_waitVideoLingeringTimeOnAutoPlay"]))))yield return w;foreach(var w in Hide(duration))yield return w;}
        if(id<=0)yield break;Start(id,p);float a=r.Number("Parameter2");Fade(a<=0?1:Math.Min(a,1),duration);timeline=true;target=0;
    }
    IEnumerable<AdvWait> Movie(AdvRow r,AdvPlayback p)
    {
        long.TryParse(r.Text("VideoID"),out long id);if(id<=0)yield break;
        if(showing){foreach(var w in p.DelayAdjusted(p.CalcDuration(Actor.N(settings["_waitVideoLingeringTimeOnAutoPlay"]))))yield return w;foreach(var w in Hide(0))yield return w;}
        Start(id,p);float a=r.Number("Parameter3");Fade(a<=0?1:Math.Min(a,1),p.CalcDuration(r.Number("Parameter1")));
        do{yield return loop.YieldUpdate();}while(Playing&&!p.Cancelled);foreach(var w in Hide(p.CalcDuration(r.Number("Parameter2"))))yield return w;
    }
    void Fade(float value,float duration){fade?.Kill();fade=tweens.To(()=>alpha,value,duration,AdvEase.OutQuad,v=>alpha=v);}
    IEnumerable<AdvWait> Hide(float duration){ui.ClearVideoText();timeline=false;if(!showing)yield break;Fade(0,duration);yield return fade!.Completed;showing=false;CloseDecoder();if(sound!=0)audio.Stop(sound);sound=0;}
    void Start(long id,AdvPlayback p)
    {
        if(p.Speed!=1)throw new NotSupportedException("Video audio resampling for playback speed is not connected");
        CloseDecoder();var data=document["videos"]?[id.ToString(CultureInfo.InvariantCulture)]??throw new InvalidDataException("Video not loaded: "+id);
        int w=(int)Actor.N(data["width"]),h=(int)Actor.N(data["height"]);total=(int)Actor.N(data["frames"]);fps=Actor.N(data["frameRate"]![0])/Actor.N(data["frameRate"]![1]);
        if(w<1||h<1||total<1||!double.IsFinite(fps)||fps<=0)throw new InvalidDataException("Invalid archived video timing");
        if(TimingOnly){started=loop.Frame;showing=true;alpha=0;ui.ClearVideoText();return;}
        temporary=NativeTemporary.Create("video",".webm");
        try{
        File.WriteAllBytes(temporary,archive.Logical(site,manifest,Archive.S(data,"file")));
        if(data["audio"] is not null){var a=ProcessInfo(["-v","error","-nostdin","-i",temporary,"-vn","-f","f32le","-ac","2","-ar","48000","pipe:1"]);using var proc=Process.Start(a)!;var err=proc.StandardError.ReadToEndAsync();using var output=new MemoryStream();proc.StandardOutput.BaseStream.CopyTo(output);proc.WaitForExit();if(proc.ExitCode!=0)throw new IOException(err.GetAwaiter().GetResult());var pcm=MemoryMarshal.Cast<byte,float>(output.ToArray()).ToArray();audio.AddCue(new(-id,3,pcm,2));sound=audio.Play(-id).Id;}
        decoder=Process.Start(ProcessInfo(["-v","error","-nostdin","-i",temporary,"-an","-f","rawvideo","-pix_fmt","rgba","pipe:1"]))??throw new IOException("Cannot start native video decoder");errors=decoder.StandardError.ReadToEndAsync();pixels=new(w,h,new byte[checked(w*h*4)]);decoded=-1;started=loop.Frame;showing=true;alpha=0;ui.ClearVideoText();
        }catch{CloseDecoder();if(sound!=0)audio.Stop(sound);sound=0;showing=false;throw;}
    }
    ProcessStartInfo ProcessInfo(string[] args){var p=new ProcessStartInfo(ffmpeg){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};foreach(string a in args)p.ArgumentList.Add(a);return p;}
    public void Draw()
    {
        if(!showing||pixels is null||decoder is null)return;int wanted=Math.Min(total-1,(int)Math.Floor((loop.Frame-started)/30d*fps));
        bool changed=false;while(decoded<wanted){try{decoder.StandardOutput.BaseStream.ReadExactly(pixels.Rgba);}catch(EndOfStreamException){decoder.WaitForExit();throw new InvalidDataException("Video decoder ended before its declared frame count: "+errors!.GetAwaiter().GetResult());}decoded++;changed=true;}
        if(texture==0)texture=gl.Upload(pixels);else if(changed)gl.UpdateTexture(texture,pixels);
        float x=1,y=1,aspect=pixels.Width/(float)pixels.Height,view=gl.Width/(float)gl.Height;if(view>aspect)x=aspect/view;else y=view/aspect;
        gl.Draw(texture,[-x,-y,x,-y,x,y,-x,y],[0,1,1,1,1,0,0,0],[0,1,2,0,2,3],alpha,[1,1,1,1],[0,0,0,0],[1,1,1,1],0,false,false);
    }
    void CloseDecoder(){if(decoder is not null){if(!decoder.HasExited)decoder.Kill(true);decoder.WaitForExit();decoder.Dispose();decoder=null;}if(temporary is not null)NativeTemporary.Delete(temporary);temporary=null;}
    public void Dispose(){fade?.Kill();CloseDecoder();}
}
