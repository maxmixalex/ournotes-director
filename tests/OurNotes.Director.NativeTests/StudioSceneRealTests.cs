using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

/// <summary>
/// Opt-in real ADV regression, with assets downloaded by fetch-studio-fixture.py.
/// Performance is evidence, not a machine-dependent FPS pass/fail threshold.
/// </summary>
static class StudioSceneRealTests
{
    const string Adv="10462",Site="intl",Language="zh-Hans";

    public static async Task<int> RunAsync(string archivePath,string outputDirectory)
    {
        archivePath=Path.GetFullPath(archivePath);outputDirectory=Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var checks=new JsonArray();var report=new JsonObject
        {
            ["schema"]="ournotes-director/studio-scene-regression/1",["ok"]=false,["adv"]=Adv,
            ["site"]=Site,["language"]=Language,["checks"]=checks,["runtime"]=NativeDependencies.RuntimeIdentifier
        };
        void Check(bool result,string name)
        {
            checks.Add((JsonNode)new JsonObject{["name"]=name,["ok"]=result});
            if(!result)throw new InvalidOperationException(name);
        }
        var wall=Stopwatch.StartNew();
        try
        {
            NativeTestGraphics.Configure();
            string package=NativeTestGraphics.PackageDirectory;
            string core=NativeDependencies.CoreLibrary(baseDirectory:package),ffmpeg=NativeDependencies.MediaTool("ffmpeg",baseDirectory:package);
            var archive=new Archive(archivePath);var manifest=archive.Manifest(Site,"stories",Adv);
            var definition=JsonNode.Parse(archive.Logical(Site,manifest,"story.json"))!;
            var episode=JsonNode.Parse(archive.Logical(Site,manifest,Archive.S(definition,"episode")))!;
            var sourceCommands=episode["commands"]!.AsArray();
            Check(definition["models"] is JsonObject {Count:>=2},"real source contains two original Cubism models");
            report["manifestSha256"]=Archive.Hash(File.ReadAllBytes(archive.SitePath(Site,"stories/"+Adv+".json")));
            report["coreVersion"]=NativeDependencies.ReadCoreVersion(core);
            string project=Path.Combine(outputDirectory,"original.project.json");
            var document=new JsonObject
            {
                ["format"]=DirectorProject.Format,["version"]=1,
                ["source"]=new JsonObject{["adv"]=Adv,["site"]=Site,["language"]=Language},["output"]=new JsonObject()
            };
            File.WriteAllText(project,document.ToJsonString());
            string hash=Archive.Hash(Encoding.UTF8.GetBytes(document.ToJsonString()));

            NativePlaybackTiming timing;
            var source=new ScenePreviewSource(archivePath,project,core,ffmpeg,960,540,60,PreviewDurationMode.Automatic);
            await using(var session=new RealtimePreviewSession())
            {
                int progress=0;session.FrameReady+=(_,frame)=>{if(frame.Timing is {IsComplete:false,AnalyzedFrame:>0})Interlocked.Increment(ref progress);};
                var timer=Stopwatch.StartNew();await session.LoadSceneAsync(source);
                double firstFrameMs=timer.Elapsed.TotalMilliseconds;var first=session.LastFrame!;
                Check(first.Frame==0&&first.Pixels.Rgba.Length==960*540*4,"automatic load presents actual frame zero before seeking");
                using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(3));
                timing=await session.ResolveSceneTimingAsync(timeout.Token);
                double timingMs=timer.Elapsed.TotalMilliseconds;await session.SeekAsync(0);
                Check(timing is {IsComplete:true,EndFrame:>300,Duration:>10,Error:null}&&timing.ProjectHash==hash,"resource-backed interpreter resolves original duration and source hash");
                Check(timing.Duration==(timing.EndFrame+1)/30d,"automatic duration includes the final native frame");
                Check(session.LastFrame!.Frame==0&&session.LastFrame.DurationResolved&&session.LastFrame.Duration==timing.Duration,"duration analysis updates metadata without moving the playhead");
                Check(timing.Commands.Any(row=>row.Command=="Talk"&&row.EndFrame>row.StartFrame)&&timing.Commands.All(row=>row.StartFrame>=0),"timing contains observed blocking dialogue rows");
                report["timing"]=Timing(timing);report["load"]=new JsonObject
                {
                    ["firstFrameMilliseconds"]=firstFrameMs,["durationResolvedMilliseconds"]=timingMs,["progressEvents"]=progress
                };
                Console.WriteLine($"Real ADV {Adv}: native final frame {timing.EndFrame}, duration {timing.Duration:F6}s; first frame {firstFrameMs:F1}ms, analysis {timingMs:F1}ms.");

                var spoken=FirstSpoken(timing,sourceCommands);var row=sourceCommands[spoken.Index]!;
                long voiceId=row["VoiceIDs"]![0]!.GetValue<long>();
                var cue=AdvAudio.Decode(archive,Site,manifest,episode,voiceId,ffmpeg);
                Check(spoken.EndFrame-spoken.StartFrame>=Math.Ceiling(cue.Samples/(double)AdvAudio.SamplesPerFrame),"observed Talk span includes the decoded voice duration");
                report["voice"]=new JsonObject{["id"]=voiceId,["sampleFrames"]=cue.Samples,["channels"]=cue.Channels,["seconds"]=cue.Samples/(double)AdvAudio.SampleRate};
                await session.SeekAsync(spoken.StartFrame/30d);
                var before=session.LastFrame!;var samples=new JsonArray();object sampleGate=new();bool collecting=true;
                session.FrameReady+=(_,frame)=>
                {
                    if(!Volatile.Read(ref collecting)||frame.RenderMilliseconds<=0)return;
                    lock(sampleGate)samples.Add((JsonNode)Frame(frame));
                };
                var playback=Stopwatch.StartNew();session.Play();await Task.Delay(3500,timeout.Token);session.Pause();
                // A queue barrier waits for the final in-flight presentation and pause.
                await session.ResolveSceneTimingAsync(timeout.Token);Volatile.Write(ref collecting,false);
                var after=session.LastFrame!;
                Check(after.Frame>before.Frame&&after.SessionId==before.SessionId&&!session.IsPlaying,"continuous playback advances a persistent native session");
                Check(!ReferenceEquals(before.Pixels,after.Pixels)&&samples.Count>=2,"playback presents multiple native pixel frames");
                report["playback"]=new JsonObject
                {
                    ["wallMilliseconds"]=playback.Elapsed.TotalMilliseconds,["fromFrame"]=before.Frame,["toFrame"]=after.Frame,
                    ["presentations"]=samples.Count,["samples"]=samples
                };
                // A cached reload must retain exact timing while showing the new first frame.
                await session.LoadSceneAsync(source);var cached=session.LastFrame!;
                Check(cached.DurationResolved&&cached.Frame==0&&cached.Timing!.EndFrame==timing.EndFrame,"same-project reload reuses measured timing");
            }

            var cueCache=new AdvCueCache();
            using(var renderer=new AdvStoryRenderer(archive,Site,core,Adv,Language,960,540,project,ffmpeg,1,cueCache:cueCache))
            {
                report["device"]=renderer.Device;var samples=new JsonArray();long allocated=GC.GetAllocatedBytesForCurrentThread();
                int activeFrames=0;float peak=0;bool cleanChecked=false;var engines=new HashSet<string>(StringComparer.Ordinal);
                int visibleVoiceStart=FirstSpoken(timing,sourceCommands).StartFrame+10;
                var timer=Stopwatch.StartNew();
                for(int frame=0;frame<300;frame++)
                {
                    long stamp=Stopwatch.GetTimestamp();var pixels=renderer.Frame(frame);
                    double milliseconds=Stopwatch.GetElapsedTime(stamp).TotalMilliseconds;
                    var lips=renderer.Scene.PreviewLipSyncState();
                    bool active=lips.Any(lip=>lip.IsActive);if(active)activeFrames++;
                    foreach(var lip in lips.Where(lip=>lip.IsActive)){peak=Math.Max(peak,lip.MouthOpen);engines.Add(lip.Engine);}
                    samples.Add((JsonNode)new JsonObject{["frame"]=frame,["milliseconds"]=milliseconds,["lipSyncActive"]=active,
                        ["mouthOpen"]=lips.Count==0?0:lips.Max(lip=>lip.MouthOpen)});
                    if(!cleanChecked&&frame>=visibleVoiceStart&&CaptureVisibleVoice(renderer,frame,pixels,outputDirectory,Check) is {} visible)
                    {
                        report["visibleVoice"]=visible;cleanChecked=true;
                    }
                }
                Check(activeFrames>0&&peak>.05f&&cleanChecked,"actual original Talk commands animate native mouth parameters");
                var warm=samples.Skip(100).Select(sample=>sample!["milliseconds"]!.GetValue<double>()).Order().ToArray();
                report["rendering"]=new JsonObject
                {
                    ["width"]=960,["height"]=540,["supersample"]=1,["frames"]=300,["warmupFrames"]=100,
                    ["wallMilliseconds"]=timer.Elapsed.TotalMilliseconds,["allocatedBytes"]=GC.GetAllocatedBytesForCurrentThread()-allocated,
                    ["warmMeanMilliseconds"]=warm.Average(),["warmMedianMilliseconds"]=warm[warm.Length/2],
                    ["warmP95Milliseconds"]=warm[(int)Math.Ceiling(warm.Length*.95)-1],
                    ["warmThroughputFps"]=1000/warm.Average(),["samples"]=samples,
                    ["stageMilliseconds"]=new JsonObject(renderer.Timings.Select(pair=>new KeyValuePair<string,JsonNode?>(pair.Key,JsonValue.Create(pair.Value))))
                };
                report["lipSync"]=new JsonObject{["activeFrames"]=activeFrames,["peakMouthOpen"]=peak,
                    ["engines"]=new JsonArray(engines.Select(engine=>(JsonNode)JsonValue.Create(engine)!).ToArray())};
                Console.WriteLine($"Real ADV warm rendering: {warm.Average():F2}ms mean ({1000/warm.Average():F2} fps); voiced frames {activeFrames}, peak mouth {peak:F4}.");
                // The visible interpreter evaluates all remaining native animation steps,
                // while only the endpoint is drawn. It must agree with timing-only mode.
                _=renderer.Frame(timing.EndFrame!.Value+30,true,stopAtEnd:true);
                Check(renderer.Player.Finished&&renderer.Player.CompletionFrame==timing.EndFrame,"normal rendering and timing-only execution reach the identical native end frame");
            }
            report["isolatedActor"]=IsolatedActorRealTests.Run(archive,core,ffmpeg,project,(JsonObject)report["visibleVoice"]!,outputDirectory,Check);
            report["envelopeFallback"]=CheckEnvelope(archive,core,ffmpeg,manifest,definition,episode,timing,Check);
            report["fullCanvas"]=CheckFullCanvas(archive,core,ffmpeg,definition,episode,outputDirectory,Check);
            report["ok"]=true;Console.WriteLine($"Passed {checks.Count} real scene timing/lip-sync/preview/full-canvas checks.");return 0;
        }
        catch(Exception error){report["exception"]=error.ToString();throw;}
        finally
        {
            report["wallMilliseconds"]=wall.Elapsed.TotalMilliseconds;
            File.WriteAllText(Path.Combine(outputDirectory,"studio-scene.json"),report.ToJsonString(new(){WriteIndented=true}));
        }
    }

    static NativeCommandTiming FirstSpoken(NativePlaybackTiming timing,JsonArray commands)=>timing.Commands.First(row=>
        row.Section=="commands"&&row.Command=="Talk"&&row.Index<commands.Count
        &&commands[row.Index]?["VoiceIDs"] is JsonArray {Count:>0}&&!Actor.B(commands[row.Index]?["IgnoreLipSync"]));

    // Also callable by the focused fixture harness: no benchmark rerun is needed to
    // verify that a particular speaking actor really contributes visible pixels.
    internal static JsonObject? CaptureVisibleVoice(AdvStoryRenderer renderer,int frame,Pixels normal,string output,Action<bool,string> check)
    {
        var lips=renderer.Scene.PreviewLipSyncState();var targets=renderer.PreviewTargets();
        var speaking=lips.FirstOrDefault(lip=>lip.IsActive&&lip.MouthOpen>.05f&&targets.Any(target=>target.Id==lip.TargetId
            &&target.X<1&&target.Y<1&&target.X+target.Width>0&&target.Y+target.Height>0));
        if(speaking is null)return null;
        var actor=renderer.Scene.Character(speaking.TargetId)!;var bounds=targets.First(target=>target.Id==speaking.TargetId);
        int parameter=Array.IndexOf(actor.Core.ParameterIds,"ParamMouthOpenY");
        float nativeOpening=parameter<0?0:BitConverter.Int32BitsToSingle(Marshal.ReadInt32(actor.Core.Pointer("ParameterValues"),parameter*sizeof(float)));
        if(nativeOpening<=.01f)return null; // The game intentionally presents two buffered animation frames.
        check(nativeOpening>.01f,"speaking actor intersects the picture and the native Cubism mouth parameter is open");
        if(OperatingSystem.IsWindows()&&actor.HasMotionSync)
            check(speaking.Engine=="MotionSync","real supported original model prefers packaged MotionSync for normal Talk");
        var clean=renderer.Frame(frame,true);var cleanLips=renderer.Scene.PreviewLipSyncState();
        check(lips.SequenceEqual(cleanLips)&&renderer.Audio.Playing.Any(sound=>sound.Cue.Category==2),"clean presentation retains real voice playback and identical mouth parameters");

        // Bounds alone also exist during the initial In fade. Compare the same native
        // frame with only the speaking actor hidden to prove real pixel contribution.
        float opacity=actor.Spec.Opacity;Pixels without;
        try{actor.Spec.Opacity=0;without=renderer.Frame(frame,true);}
        finally{actor.Spec.Opacity=opacity;}
        int left=Math.Clamp((int)Math.Floor(bounds.X*clean.Width),0,clean.Width),right=Math.Clamp((int)Math.Ceiling((bounds.X+bounds.Width)*clean.Width),0,clean.Width);
        int top=Math.Clamp((int)Math.Floor(bounds.Y*clean.Height),0,clean.Height),bottom=Math.Clamp((int)Math.Ceiling((bounds.Y+bounds.Height)*clean.Height),0,clean.Height);
        int contributing=0;
        for(int y=top;y<bottom;y++)for(int x=left;x<right;x++)
        {
            int i=(y*clean.Width+x)*4;
            if(Math.Abs(clean.Rgba[i]-without.Rgba[i])+Math.Abs(clean.Rgba[i+1]-without.Rgba[i+1])+Math.Abs(clean.Rgba[i+2]-without.Rgba[i+2])>6)contributing++;
        }
        check(contributing>500,"speaking actor contributes visible pixels inside its projected bounds");
        File.WriteAllBytes(Path.Combine(output,"original-talk.png"),Png.Encode(Thumbnail(normal,480)));
        File.WriteAllBytes(Path.Combine(output,"original-talk-clean.png"),Png.Encode(Thumbnail(clean,480)));
        return new(){["frame"]=frame,["time"]=frame/30d,["target"]=speaking.TargetId,["engine"]=speaking.Engine,
            ["supportsMotionSync"]=actor.HasMotionSync,["mouthOpen"]=speaking.MouthOpen,["nativeMouthOpen"]=nativeOpening,["contributingPixels"]=contributing,
            ["bounds"]=new JsonObject{["x"]=bounds.X,["y"]=bounds.Y,["width"]=bounds.Width,["height"]=bounds.Height}};
    }

    static JsonObject CheckEnvelope(Archive archive,string core,string ffmpeg,JsonNode manifest,JsonNode definition,JsonNode episode,
        NativePlaybackTiming timing,Action<bool,string> check)
    {
        var rows=episode["commands"]!.AsArray();var spoken=FirstSpoken(timing,rows);var row=rows[spoken.Index]!;
        string name=Archive.S(row,"TargetName");
        var character=rows.First(command=>Archive.S(command!,"cmd")=="Character"&&Archive.S(command!,"TargetName")==name)!;
        string address=Archive.S(character,"TargetAssetName");
        var model=definition["models"]!.AsObject().First(pair=>pair.Key.EndsWith(address,StringComparison.Ordinal)).Value!.ToString();
        var cue=AdvAudio.Decode(archive,Site,manifest,episode,row["VoiceIDs"]![0]!.GetValue<long>(),ffmpeg);
        var loop=new AdvLoop();var audio=new AdvAudio(loop,[cue]);var voice=audio.Play(cue.SoundId);
        using var actor=new Actor(archive,Site,core,new ActorSpec{Name=name,Model=model,Physics=false},null,722);
        actor.StartVoiceLipSync(audio,voice,forceCri:true);
        check(actor.VoiceLipSyncActive&&actor.LipSyncEngine=="语音音量驱动（无分析数据）"&&actor.LipSyncMissing is not null,
            "missing-analysis fallback is explicit and still follows the actual voice");
        var shot=new Shot();float peak=0;int active=0;
        for(int frame=0;frame<=Math.Ceiling(cue.Samples/(double)AdvAudio.SamplesPerFrame);frame++)
        {
            loop.Step();actor.Step(shot,loop.Frame/30d,1/30f);
            if(actor.VoiceLipSyncActive){active++;peak=Math.Max(peak,actor.MouthOpen);}
        }
        check(active>0&&peak>.05f&&!actor.VoiceLipSyncActive,"real decoded PCM drives the fallback mouth and stops with its voice");
        return new(){["model"]=model,["voiceId"]=cue.SoundId,["activeFrames"]=active,["peakMouthOpen"]=peak,["engine"]="语音音量驱动（无分析数据）"};
    }

    static JsonObject CheckFullCanvas(Archive archive,string core,string ffmpeg,JsonNode definition,JsonNode episode,string output,Action<bool,string> check)
    {
        // Finish the PNG renderer and its large managed pixel buffers before the
        // separate video renderer creates another original-resolution GL surface.
        var report=CheckFullCanvasPng(archive,core,ffmpeg,definition,episode,output,check);
        string ffprobe=NativeDependencies.MediaTool("ffprobe",baseDirectory:NativeTestGraphics.PackageDirectory);
        var cineform=FullCanvasCineformTests.Run(archive,Path.Combine(output,"full-canvas.project.json"),output,core,ffmpeg,ffprobe);
        report["cineform"]=cineform;
        check(cineform["ok"]?.GetValue<bool>()==true,"formal full-canvas CineForm MOV retains original dimensions and transparent alpha");
        return report;
    }

    static JsonObject CheckFullCanvasPng(Archive archive,string core,string ffmpeg,JsonNode definition,JsonNode episode,string output,Action<bool,string> check)
    {
        var character=episode["commands"]!.AsArray().First(row=>Archive.S(row!,"cmd")=="Character")!.DeepClone();
        string name=Archive.S(character,"TargetName"),address=Archive.S(character,"TargetAssetName");
        string model=definition["models"]!.AsObject().First(pair=>pair.Key.EndsWith(address,StringComparison.Ordinal)).Value!.ToString();
        var modelManifest=archive.Manifest(Site,"models",model);var modelInfo=JsonNode.Parse(archive.Logical(Site,modelManifest,"model.json"))!;
        int width=(int)Actor.N(modelInfo["canvas"]!["width"]),height=(int)Actor.N(modelInfo["canvas"]!["height"]);
        check(width==6000&&height==9000,"real model declares its original 6000x9000 pixel canvas");
        RenderDimensions.Validate(width,height,1);bool rejectsOversampling=false;
        try{RenderDimensions.Validate(width,height,2);}catch(ArgumentException){rejectsOversampling=true;}
        check(rejectsOversampling,"oversampled original canvas fails the explicit pixel budget instead of shrinking");
        var document=new JsonObject
        {
            ["format"]=DirectorProject.Format,["version"]=1,["source"]=new JsonObject{["adv"]=Adv,["site"]=Site,["language"]=Language},
            ["output"]=new JsonObject{["width"]=width,["height"]=height,["supersample"]=1,["clean"]=true,["letterbox"]="none"},
            ["initialize"]=new JsonArray(character,
                new JsonObject{["cmd"]="Director.Scene",["Values"]=new JsonObject{["background"]=new JsonArray(0,0,0,0),["stageVisible"]=false}},
                new JsonObject{["cmd"]="Director.Actor",["TargetName"]=name,["Space"]="screen",["Values"]=new JsonObject
                    {["position"]=new JsonArray(.5,.5),["height"]=1,["physics"]=false,["opacity"]=1}}),
            ["commands"]=new JsonArray(new JsonObject{["cmd"]="Wait",["Duration"]=.1}),["finalize"]=new JsonArray()
        };
        string project=Path.Combine(output,"full-canvas.project.json");File.WriteAllText(project,document.ToJsonString());
        _=DirectorProject.Read(project);var timer=Stopwatch.StartNew();Pixels image;
        using(var renderer=new AdvStoryRenderer(archive,Site,core,Adv,Language,width,height,project,ffmpeg,1))image=renderer.Frame(2,true);
        check(image.Width==width&&image.Height==height&&image.Rgba.Length==width*height*4,"formal native renderer produces the exact original canvas without downsampling");
        long transparent=0,opaque=0,partial=0;
        for(int i=3;i<image.Rgba.Length;i+=4){if(image.Rgba[i]==0)transparent++;else if(image.Rgba[i]==255)opaque++;else partial++;}
        check(transparent>0&&opaque>0&&partial>0,"full-canvas render contains transparent, opaque and partial-alpha pixels");
        var png=Png.Encode(image);string path=Path.Combine(output,"full-canvas.png");File.WriteAllBytes(path,png);
        var decoded=Png.Decode(File.ReadAllBytes(path));
        check(decoded.Width==width&&decoded.Height==height&&decoded.Rgba.AsSpan().SequenceEqual(image.Rgba),"full-resolution RGBA PNG round-trips every source pixel");
        File.WriteAllBytes(Path.Combine(output,"full-canvas-preview.png"),Png.Encode(Thumbnail(image,480)));
        string checksum=Archive.Hash(png);
        // CI evidence keeps the thumbnail and exact metadata; no large source assets
        // or giant test image are included in the release/evidence artifact.
        if(Environment.GetEnvironmentVariable("OURNOTES_KEEP_FULL_CANVAS")!="1")File.Delete(path);
        return new(){["model"]=model,["width"]=width,["height"]=height,["supersample"]=1,["pngBytes"]=png.Length,
            ["pngSha256"]=checksum,["transparentPixels"]=transparent,["opaquePixels"]=opaque,["partialAlphaPixels"]=partial,["wallMilliseconds"]=timer.Elapsed.TotalMilliseconds};
    }

    static JsonObject Timing(NativePlaybackTiming timing)=>new()
    {
        ["projectHash"]=timing.ProjectHash,["isComplete"]=timing.IsComplete,["endFrame"]=timing.EndFrame,["duration"]=timing.Duration,
        ["commands"]=new JsonArray(timing.Commands.Select(row=>(JsonNode)new JsonObject
        {
            ["section"]=row.Section,["index"]=row.Index,["command"]=row.Command,["startFrame"]=row.StartFrame,
            ["endFrame"]=row.EndFrame,["ordinal"]=row.Ordinal,["key"]=row.Key
        }).ToArray())
    };
    static JsonObject Frame(PreviewFrame frame)=>new()
    {
        ["frame"]=frame.Frame,["session"]=frame.SessionId,["renderMilliseconds"]=frame.RenderMilliseconds,
        ["measuredFps"]=frame.FramesPerSecond,["simulationFrames"]=frame.Performance?.SimulationFrames,
        ["simulationMilliseconds"]=frame.Performance?.SimulationMilliseconds,["sceneMilliseconds"]=frame.Performance?.SceneMilliseconds,
        ["readbackMilliseconds"]=frame.Performance?.ReadbackMilliseconds,["targetMilliseconds"]=frame.Performance?.TargetMilliseconds,
        ["allocatedBytes"]=frame.Performance?.AllocatedBytes
    };
    static Pixels Thumbnail(Pixels image,int width)
    {
        int height=Math.Max(1,(int)Math.Round(image.Height*width/(double)image.Width));var pixels=new byte[width*height*4];
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)
        {
            int source=((int)(y*(long)image.Height/height)*image.Width+(int)(x*(long)image.Width/width))*4;
            image.Rgba.AsSpan(source,4).CopyTo(pixels.AsSpan((y*width+x)*4,4));
        }
        return new(width,height,pixels);
    }
}
