using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

// Host for the native game-command player. A script is an episode command array, not a new scripting language.
public sealed class AdvStoryRenderer : IDisposable
{
    public AdvLoop Loop{get;}=new();
    public AdvPlayback Player{get;}
    public AdvScene Scene{get;}
    public AdvAudio Audio{get;}
    public AdvUi Ui{get;}
    public AdvFrames Frames{get;}
    public AdvStills Stills{get;}
    public AdvVideo Video{get;}
    public string Device=>gl.Device;
    readonly OffscreenGl gl;
    readonly int outputWidth,outputHeight,supersample,viewportY;
    readonly Dictionary<string,double> milliseconds=[];
    T Measure<T>(string name,Func<T> action){long start=Stopwatch.GetTimestamp();try{return action();}finally{milliseconds[name]=milliseconds.GetValueOrDefault(name)+Stopwatch.GetElapsedTime(start).TotalMilliseconds;}}
    void Measure(string name,Action action)=>Measure(name,()=>{action();return 0;});
    public IReadOnlyDictionary<string,double> Timings=>milliseconds;
    public AdvStoryRenderer(Archive archive,string site,string dll,string id,string language,int width,int height,string? script,string ffmpeg,int supersample=2)
    {
        if(width<16||height<16||width>7680||height>4320)throw new ArgumentException("Invalid story render dimensions");
        if(supersample is <1 or >4)throw new ArgumentException("Supersample must be 1..4");
        outputWidth=width;outputHeight=height;this.supersample=supersample;
        float target=width>height?2.1666667f:1.7777778f;int viewportHeight=height;
        bool noLetterbox=script is not null&&DirectorProject.Document(script) is JsonObject projectDoc&&Archive.S(projectDoc,"format")==DirectorProject.Format&&Archive.S(projectDoc["output"]??new JsonObject(),"letterbox")=="none";
        if(!noLetterbox&&width/(float)height+.0001f<target&&(height-width/target)/2>1)viewportHeight=(int)MathF.Floor(width/target+.5f);
        viewportY=(int)MathF.Floor((height-viewportHeight)/2f+.5f);
        var manifest=archive.Manifest(site,"stories",id);var definition=JsonNode.Parse(archive.Logical(site,manifest,"story.json"))!;
        var episode=JsonNode.Parse(archive.Logical(site,manifest,Archive.S(definition,"episode")))!;
        var scene=JsonNode.Parse(archive.Logical(site,manifest,Archive.S(definition,"scene")))!;var settings=scene["settings"]!["playerSettings"]!.DeepClone();
        if(script is not null){var custom=DirectorProject.Document(script);bool project=custom is JsonObject&&Archive.S(custom,"format")==DirectorProject.Format;
            if(project){var config=DirectorProject.Read(script);if(config.Adv!=id)throw new ArgumentException("Project source does not match story ID");}
            var commands=custom is JsonArray?custom:custom["commands"];
            if(commands is not null)episode["commands"]=commands.DeepClone();else if(!project)throw new ArgumentException("Script needs a commands array");
            if(custom is JsonObject){if(custom["text"] is JsonObject text)foreach(var pair in text)episode["text"]![pair.Key]=pair.Value?.DeepClone();if(custom["initialize"] is JsonArray init)settings["_initializeEpisodes"]=init.DeepClone();if(custom["finalize"] is JsonArray fin)settings["_finalizeEpisodes"]=fin.DeepClone();}
        }
        var program=AdvProgram.From(episode,settings);Player=new(Loop,program);var tweens=new AdvTweens(Loop);
        gl=new(checked(width*supersample),checked(viewportHeight*supersample));
        try{
            Scene=new(archive,site,dll,manifest,definition,scene,language,gl,Loop,tweens);Scene.Bind(Player);
            if(script is not null)Scene.ProjectDirectory=Path.GetDirectoryName(Path.GetFullPath(script))!;
            var rows=program.Initialize.Concat(program.Rows).Concat(program.Finalize).Where(r=>!r.Ignored).ToArray();
            foreach(var row in rows)DirectorValidation.Check(row);
            Audio=new(Loop,[]){ResolveCue=n=>AdvAudio.Decode(archive,site,manifest,episode,n,ffmpeg)};Audio.Bind(Player);
            Ui=new(archive,site,manifest,definition,episode,settings,language,gl,Loop,Audio,supersample){HasLetterbox=viewportHeight<height,MasterIds=scene["settings"]?["masterIdSettings"]};Ui.Bind(Player);Ui.CheckTexts(rows);
            Ui.ResolveCharacter=Scene.Character;
            Ui.ShowingCharacters=()=>Scene.VisibleActors;
            Scene.ForegroundItem=()=>Ui.Chat is {Visible:true} chat?new(chat.SortingOrder,chat.PlaneDistance*chat.PlaneDistance,()=>chat.Draw(Scene.Camera)):null;
            Frames=new(archive,site,manifest,definition,language,Loop,tweens,gl,supersample){Localize=Ui.Localize};Frames.Bind(Player);Frames.Preload(rows);
            Stills=new(archive,site,manifest,definition,language,Loop,tweens,gl,rows);Stills.Bind(Player);
            Video=new(archive,site,manifest,definition,settings,Loop,tweens,Audio,Ui,gl,ffmpeg);Video.Bind(Player);
            AdvAlphaCommands.Bind(Player,Loop,Stills.SetAlpha,Video.SetAlpha);
            Scene.TalkShakeGet=()=>Ui.ShakeOffset;Scene.TalkShakeSet=v=>Ui.ShakeOffset=v;
            Scene.StillShakeGet=()=>Stills.ShakeOffset;Scene.StillShakeSet=v=>Stills.ShakeOffset=v;
            if(Player.MissingCommands.Length>0)throw new NotSupportedException("Native story commands not connected: "+string.Join(", ",Player.MissingCommands));
            Scene.Preload(rows);
            foreach(var row in rows.Where(r=>r.Command=="Director.Parameter"))_=(Scene.Character(row.Text("TargetName"))??throw new ArgumentException("Unknown actor: "+row.Text("TargetName"))).ParameterValue(row.Text("Parameter"));
            Player.Prepare=p=>Ui.Prepare();Player.Start();
        }catch{Video?.Dispose();Stills?.Dispose();Frames?.Dispose();Ui?.Dispose();Scene?.Dispose();gl.Dispose();throw;}
    }
    public Pixels Frame(int frame,bool clean=false,IReadOnlyList<JsonObject>? previewEdits=null)
    {
        RenderFrame(frame,clean,previewEdits);
        var pixels=Measure("readbackAndResolve",()=>gl.Present(outputWidth,outputHeight,supersample,viewportY,Ui.DrawLetterbox,Scene.DirectorTransparent));Loop.Drain();Player.ThrowIfFailed();return pixels;
    }
    public IReadOnlyList<PreviewTarget> PreviewTargets()
    {
        double top=viewportY/(double)outputHeight,height=gl.Height/(double)supersample/outputHeight;
        return Scene.PreviewTargets().Select(target=>target with{Y=top+target.Y*height,Height=target.Height*height}).ToArray();
    }
    void RenderFrame(int frame,bool clean,IReadOnlyList<JsonObject>? previewEdits=null)
    {
        if(frame<Loop.Frame)throw new ArgumentException("Frame requests must be increasing");
        Measure("simulation",()=>{while(Loop.Frame<frame){Scene.AdvanceRenderState();Frames.AdvanceRenderState();Ui.Chat?.AdvanceRenderState();Loop.Drain();Player.Step();}});
        if(previewEdits is {Count:>0})Scene.ApplyPreviewEdits(previewEdits,Player);
        Draw(clean);
    }
    public int PendingFrames=>gl.PendingPixels;
    public void QueueFrame(int frame,bool clean=false){if(PendingFrames>=3)throw new InvalidOperationException("Read a queued frame before advancing the story");RenderFrame(frame,clean);Measure("readbackAndResolve",()=>gl.QueuePresent(outputWidth,outputHeight,supersample,viewportY,Ui.DrawLetterbox,Scene.DirectorTransparent));Loop.Drain();Player.ThrowIfFailed();}
    public Pixels ReadQueuedFrame()=>Measure("readbackAndResolve",gl.ReadQueued);
    void Draw(bool clean){Measure("scene",Scene.Draw);if(Video.Visible||Stills.Visible)Measure("media",()=>Scene.DrawMedia(()=>{Video.Draw();Stills.Draw();},Stills.PostEffectSuppressed));Measure("frames",Frames.Draw);Measure("ui",()=>Ui.Draw(clean));if(!Scene.DirectorTransparent)Measure("finalPost",()=>Scene.FinalPost());Scene.ApplyDirectorGrade();}
    public void Dispose(){Video.Dispose();Stills.Dispose();Frames.Dispose();Ui.Dispose();Scene.Dispose();gl.Dispose();}
    public static object Contact(Archive archive,string site,string dll,string id,string language,string? script,string output,int width,int height,double[] times,int columns,bool clean,bool overwrite,string ffmpeg,int supersample=2)
    {
        if(times.Length is <1 or >64||times.Any(t=>!double.IsFinite(t)||t<0||t>36000))throw new ArgumentException("Contact needs 1..64 times within ten hours");
        if(columns<1||columns>16)throw new ArgumentException("Contact columns must be 1..16");
        int cols=Math.Min(columns,times.Length),rows=(times.Length+cols-1)/cols;
        if(width<16||height<16||(long)width*height*cols*rows>64000000)throw new ArgumentException("Contact dimensions exceed 64 megapixels");
        int outWidth=checked(width*cols),outHeight=checked(height*rows);
        output=Path.GetFullPath(output);if(File.Exists(output)&&!overwrite)throw new IOException("Output exists; use --overwrite");
        var wall=Stopwatch.StartNew();var pixels=new byte[checked(outWidth*outHeight*4)];var cells=times.Select((t,i)=>new ContactCell(i,(int)Math.Floor(t*30+1e-8),Math.Floor(t*30+1e-8)/30,i%cols*width,i/cols*height)).ToArray();
        using var renderer=new AdvStoryRenderer(archive,site,dll,id,language,width,height,script,ffmpeg,supersample);
        foreach(var group in cells.GroupBy(c=>c.Frame).OrderBy(g=>g.Key)){var image=renderer.Frame(group.Key,clean);foreach(var cell in group)for(int y=0;y<height;y++)Buffer.BlockCopy(image.Rgba,y*width*4,pixels,((cell.Y+y)*outWidth+cell.X)*4,width*4);}
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);string temp=output+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllBytes(temp,Png.Encode(new(outWidth,outHeight,pixels)));File.Move(temp,output,overwrite);}finally{if(File.Exists(temp))File.Delete(temp);}
        return JsonOutput.Object(("output", output), ("width", outWidth), ("height", outHeight), ("cells", cells), ("performance", JsonOutput.Object(("wallSeconds", wall.Elapsed.TotalSeconds), ("cpuSubmissionMilliseconds", renderer.Timings))));
    }
    public static object Run(Archive archive,string site,string dll,string id,string language,string? script,string mode,string output,int width,int height,double time,double duration,string codec,bool clean,bool overwrite,string ffmpeg,int supersample=2,bool untilEnd=false,double speed=1,string cineformQuality=VideoEncoding.DefaultCineFormQuality)
    {
        if(mode is not ("frame" or "clip"))throw new ArgumentException("Story output must be frame or clip");
        if(untilEnd&&mode!="clip")throw new ArgumentException("--until-end requires story clip");
        if(!double.IsFinite(speed)||speed<.25||speed>4)throw new ArgumentException("Export speed must be between 0.25 and 4");
        if(mode!="clip"&&speed!=1)throw new ArgumentException("--speed requires story clip");
        if(!double.IsFinite(time)||time<0||!double.IsFinite(duration)||duration<=0||time+duration>36000)throw new ArgumentException("Invalid time/duration; maximum ten hours");
        codec=VideoEncoding.NormalizeCodec(codec);
        if(mode=="clip"){VideoEncoding.Validate(codec,width,height,cineformQuality);VideoEncoding.ValidateOutputPath(codec,output);}
        output=Path.GetFullPath(output);if(File.Exists(output)&&!overwrite)throw new IOException("Output exists; use --overwrite");Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        string prefix=Path.Combine(Path.GetDirectoryName(output)!,".adv-"+Guid.NewGuid().ToString("N")),video=prefix+".video",audio=prefix+".f32",mux=prefix+".mux";
        Process? encoder=null;Task<string>? errors=null;int start=(int)Math.Floor(time*30+1e-8),count=(int)Math.Ceiling(duration*30),written=0;bool finished=false;var wall=Stopwatch.StartNew();double encodeMs=0,audioMs=0;
        try{
            using var renderer=new AdvStoryRenderer(archive,site,dll,id,language,width,height,script,ffmpeg,supersample);
            double initializationMs=wall.Elapsed.TotalMilliseconds;
            if(mode=="frame"){File.WriteAllBytes(mux,Png.Encode(renderer.Frame(start,clean)));File.Move(mux,output,overwrite);return JsonOutput.Object(("output", output), ("frame", start));}
            var info=VideoEncoding.RawVideoStartInfo(ffmpeg,codec,width,height,30*speed,video,cineformQuality);
            string format=VideoEncoding.Format(codec);
            encoder=Process.Start(info)??throw new IOException("Cannot start video encoder");errors=encoder.StandardError.ReadToEndAsync();
            void EncodeQueued(){var pixels=renderer.ReadQueuedFrame();long stamp=Stopwatch.GetTimestamp();encoder.StandardInput.BaseStream.Write(pixels.Rgba);encodeMs+=Stopwatch.GetElapsedTime(stamp).TotalMilliseconds;}
            var mixedPcm=new float[AdvAudio.SamplesPerFrame*2];
            using(var pcm=File.Create(audio))for(int i=0;i<count;i++){
                renderer.QueueFrame(start+i,clean);if(renderer.PendingFrames>=3)EncodeQueued();
                if(codec=="h264"&&renderer.Scene.DirectorTransparent)throw new ArgumentException("Transparent scenes require --codec cineform, prores, or ffv1");
                long stamp=Stopwatch.GetTimestamp();renderer.Audio.MixFrame(mixedPcm);pcm.Write(MemoryMarshal.AsBytes(mixedPcm.AsSpan()));audioMs+=Stopwatch.GetElapsedTime(stamp).TotalMilliseconds;
                written++;finished=renderer.Player.Finished;
                if(i%30==0)Console.Error.WriteLine($"story render {i+1}/{count}");
                if(untilEnd&&finished)break;
            }
            while(renderer.PendingFrames>0)EncodeQueued();
            encoder.StandardInput.Close();encoder.WaitForExit();if(encoder.ExitCode!=0)throw new IOException(errors.GetAwaiter().GetResult());
            if(untilEnd&&!finished)throw new InvalidOperationException("Story did not finish within --duration; increase the cap or inspect its command timeline. No incomplete video was published.");
            var merge=new ProcessStartInfo(ffmpeg){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true};
            var mergeArgs=new List<string>{"-v","error","-y","-i",video,"-f","f32le","-ar","48000","-ac","2","-i",audio,"-map","0:v:0","-map","1:a:0","-c:v","copy"};
            double outputDuration=written/(30*speed);
            if(speed!=1)mergeArgs.AddRange(["-af",Media.TempoFilter(speed)+",apad,atrim=duration="+outputDuration.ToString("R",CultureInfo.InvariantCulture)]);
            mergeArgs.AddRange(["-c:a",codec=="h264"?"aac":"pcm_s24le","-t",outputDuration.ToString("R",CultureInfo.InvariantCulture),"-f",format,mux]);
            foreach(var a in mergeArgs)merge.ArgumentList.Add(a);
            using var joined=Process.Start(merge)??throw new IOException("Cannot start audio mux");var err=joined.StandardError.ReadToEndAsync();joined.WaitForExit();if(joined.ExitCode!=0)throw new IOException(err.GetAwaiter().GetResult());File.Move(mux,output,overwrite);
            return JsonOutput.Object(("output", output), ("frames", written), ("width", width), ("height", height), ("supersample", supersample), ("fps", 30*speed), ("speed", speed), ("sourceDuration", written/30d), ("duration", outputDuration), ("audioHz", 48000), ("codec", codec), ("cineformQuality", codec=="cineform"?VideoEncoding.NormalizeCineFormQuality(cineformQuality):null), ("alpha", codec=="h264"?"none":"straight"), ("finished", finished), ("performance", JsonOutput.Object(("Device", renderer.Device), ("initializationMilliseconds", initializationMs), ("wallSeconds", wall.Elapsed.TotalSeconds), ("framesPerSecond", written/wall.Elapsed.TotalSeconds), ("cpuSubmissionMilliseconds", renderer.Timings), ("encoderWriteMilliseconds", encodeMs), ("audioMixMilliseconds", audioMs))));
        }finally{if(encoder is not null){if(!encoder.HasExited)encoder.Kill(true);encoder.Dispose();}foreach(var path in new[]{video,audio,mux})if(File.Exists(path))File.Delete(path);}
    }
}
