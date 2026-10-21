using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

static class ModelResourceCacheNativeTests
{
    sealed record Sample(int Frame,string Pixels,string Parameters,string NativeParameters,string Animations,string LipSync,string Pcm,string Commands,double AudioEnergy)
    {
        public JsonObject ToJson()=>new(){["frame"]=Frame,["pixels"]=Pixels,["parameters"]=Parameters,
            ["nativeParameters"]=NativeParameters,["animations"]=Animations,["lipSync"]=LipSync,
            ["pcm"]=Pcm,["commands"]=Commands,["audioEnergy"]=AudioEnergy};
    }

    public static int Run(string fixture,string output)
    {
        Directory.CreateDirectory(output);
        var checks=new JsonArray();var runs=new JsonArray();
        var report=new JsonObject{["schema"]="ournotes-director/model-cache-regression/1",["ok"]=false,["checks"]=checks,["runs"]=runs};
        void Check(bool result,string name)
        {
            checks.Add((JsonNode)new JsonObject{["name"]=name,["ok"]=result});
            if(!result)throw new InvalidOperationException(name);
        }
        try
        {
            PreviewModelCacheTests.Run(Check);
            NativeTestGraphics.Configure();
            string core=NativeDependencies.CoreLibrary(baseDirectory:NativeTestGraphics.PackageDirectory);
            string ffmpeg=NativeDependencies.MediaTool("ffmpeg",baseDirectory:NativeTestGraphics.PackageDirectory);
            var document=new JsonObject{["format"]=DirectorProject.Format,["version"]=1,
                ["source"]=new JsonObject{["adv"]="10462",["site"]="intl",["language"]="zh-Hans"},["output"]=new JsonObject()};
            string path=Path.Combine(output,"original.project.json"),saved=document.ToJsonString();File.WriteAllText(path,saved);
            report["projectHash"]=Hash(saved);report["source"]="10462";report["width"]=960;report["height"]=540;
            var textures=new PreviewTextureCache();var models=new PreviewModelCache();var cues=new AdvCueCache();
            Sample[]? reference=null;var allocatedOff=new List<double>();var allocatedOn=new List<double>();
            var elapsedOff=new List<double>();var elapsedOn=new List<double>();
            // Cold shader/PNG/JIT and model-cache seeding runs are excluded. Each
            // following pair reverses or alternates order within the same binary,
            // process, source, driver, PCM cache and warm immutable PNG cache.
            bool[] enabled=[false,true,false,true,true,false,false,true];
            for(int run=0;run<enabled.Length;run++)
            {
                bool cached=enabled[run],measured=run>=2;
                var before=models.Capture();long textureDecodes=textures.Decodes,textureHits=textures.Hits;
                var archive=new Archive(fixture){PreviewTextures=textures,PreviewModels=cached?models:null};
                long allocation=GC.GetAllocatedBytesForCurrentThread(),started=Stopwatch.GetTimestamp();
                using var renderer=new AdvStoryRenderer(archive,"intl",core,"10462","zh-Hans",960,540,path,ffmpeg,1,cueCache:cues);
                double elapsed=Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                long allocated=GC.GetAllocatedBytesForCurrentThread()-allocation;
                var native=new JsonObject();foreach(var (name,value) in renderer.InitializationTimings)native[name]=value;
                JsonObject? resources=cached?models.DiagnosticsSince(before):null;
                var item=new JsonObject{["run"]=run,["cacheEnabled"]=cached,["measured"]=measured,
                    ["constructorMs"]=elapsed,["constructorAllocatedBytes"]=allocated,["native"]=native,
                    ["modelResources"]=resources,["textureDecodes"]=textures.Decodes-textureDecodes,["textureHits"]=textures.Hits-textureHits};
                runs.Add((JsonNode)item);
                Console.WriteLine($"Model-cache run {run}: enabled={cached}, constructor={elapsed:F2} ms, managed={allocated} bytes");
                var samples=new[]{6,75,240,600}.Select(frame=>Capture(renderer,frame)).ToArray();
                item["samples"]=new JsonArray(samples.Select(sample=>(JsonNode)sample.ToJson()).ToArray());
                reference??=samples;
                Check(samples.SequenceEqual(reference),$"Reconstruction {run} preserves exact rendered pixels, both parameter buffers, native animations, mouths, audible PCM and command observations at all four frames");
                if(measured)
                {
                    Check(textures.Decodes==textureDecodes,"Measured reconstruction reuses the same warm PNG cache: "+run);
                    (cached?allocatedOn:allocatedOff).Add(allocated);(cached?elapsedOn:elapsedOff).Add(elapsed);
                    if(cached)Check(resources!["assetDecodes"]!.GetValue<long>()==0&&resources["clipCompilations"]!.GetValue<long>()==0
                        &&resources["assetHits"]!.GetValue<long>()>0&&resources["clipHits"]!.GetValue<long>()>0
                        &&models.RetainedBytes<=models.LimitBytes,
                        "A fitting warm reconstruction validates current sources while avoiding every model-source decode and motion compilation: "+run);
                }
            }
            Console.WriteLine("Alternating model-cache reconstruction measurements complete.");
            var baseline=reference??throw new InvalidOperationException("No native reference was captured.");
            Check(baseline.Any(sample=>sample.AudioEnergy>0),"The PCM comparison covers actual audible story output rather than only silence");
            Check(baseline.Count(sample=>sample.NativeParameters!=Hash("{}"))>=2,
                "Multiple sampled frames contain real visible native actors and parameter buffers");
            double Median(List<double> values)=>values.Order().ElementAt(values.Count/2);
            report["summary"]=new JsonObject{["cacheOffMedianMs"]=Median(elapsedOff),["cacheOnMedianMs"]=Median(elapsedOn),
                ["cacheOffMedianAllocatedBytes"]=Median(allocatedOff),["cacheOnMedianAllocatedBytes"]=Median(allocatedOn),
                ["wallClockIsDiagnosticOnly"]=true,["retainedEstimatedBytes"]=models.RetainedBytes,["limitBytes"]=models.LimitBytes};
            Check(Median(allocatedOn)<Median(allocatedOff),"Warm source and immutable motion reuse lowers measured constructor managed allocations");
            VerifyCompleteTiming(fixture,path,core,ffmpeg,models,cues,Check,report);
            VerifySessionAsync(fixture,path,core,ffmpeg,baseline,Check,report).GetAwaiter().GetResult();
            Check(File.ReadAllText(path)==saved,"Cache construction and backward replay do not modify the saved original project");
            report["ok"]=true;Console.WriteLine($"Passed {checks.Count} model source/motion cache and native reconstruction checks.");return 0;
        }
        finally{File.WriteAllText(Path.Combine(output,"model-cache.json"),report.ToJsonString(new(){WriteIndented=true}));}
    }

    static Sample Capture(AdvStoryRenderer renderer,int frame)
    {
        var pixels=renderer.Frame(frame);
        var evaluated=new JsonObject();var displayed=new JsonObject();
        foreach(var actor in renderer.Scene.VisibleActors.OrderBy(actor=>actor.Spec.Name,StringComparer.Ordinal))
        {
            evaluated[actor.Spec.Name]=FloatHash(actor.Values);
            displayed[actor.Spec.Name]=FloatHash(actor.Core.Floats("ParameterValues",actor.Core.ParameterIds.Length));
        }
        var animations=AnimationJson(renderer.Scene.PreviewAnimationState());
        var lips=new JsonArray(renderer.Scene.PreviewLipSyncState().OrderBy(value=>value.TargetId,StringComparer.Ordinal).Select(value=>(JsonNode)new JsonObject
        { ["actor"]=value.TargetId,["engine"]=value.Engine,["active"]=value.IsActive,["mouthBits"]=BitConverter.SingleToInt32Bits(value.MouthOpen),["missing"]=value.Missing }).ToArray());
        float[] pcm=renderer.Audio.MixFrame();
        return new(frame,Archive.Hash(pixels.Rgba),Hash(evaluated.ToJsonString()),Hash(displayed.ToJsonString()),Hash(animations.ToJsonString()),
            Hash(lips.ToJsonString()),FloatHash(pcm),Hash(CommandJson(renderer.Player.CommandTimings()).ToJsonString()),pcm.Sum(value=>Math.Abs((double)value)));
    }

    static JsonArray AnimationJson(IReadOnlyList<PreviewActorAnimation> values)=>new(values.OrderBy(value=>value.TargetId,StringComparer.Ordinal)
        .Select(value=>(JsonNode)new JsonObject{["actor"]=value.TargetId,["model"]=value.Model,["motion"]=value.Motion,
            ["expression"]=value.Expression,["timeBits"]=BitConverter.DoubleToInt64Bits(value.MotionTime)}).ToArray());
    static JsonArray CommandJson(IReadOnlyList<NativeCommandTiming> values)=>new(values.Select(value=>(JsonNode)new JsonObject
        {["section"]=value.Section,["index"]=value.Index,["command"]=value.Command,["start"]=value.StartFrame,["end"]=value.EndFrame,["ordinal"]=value.Ordinal,["key"]=value.Key}).ToArray());
    static string FloatHash(float[] values)=>Archive.Hash(MemoryMarshal.AsBytes(values.AsSpan()).ToArray());
    static string Hash(string value)=>Archive.Hash(Encoding.UTF8.GetBytes(value));

    static void VerifyCompleteTiming(string fixture,string path,string core,string ffmpeg,PreviewModelCache models,AdvCueCache cues,Action<bool,string> check,JsonObject report)
    {
        NativePlaybackTiming? reference=null;var results=new JsonArray();report["completeTiming"]=results;
        foreach(bool cached in new[]{false,true})
        {
            var archive=new Archive(fixture){PreviewModels=cached?models:null};
            using var renderer=new AdvStoryRenderer(archive,"intl",core,"10462","zh-Hans",960,540,path,ffmpeg,1,timingOnly:true,cueCache:cues);
            var timing=renderer.AnalyzeTiming(Hash(File.ReadAllText(path)));reference??=timing;
            check(timing.IsComplete&&timing.EndFrame==reference.EndFrame&&timing.Duration==reference.Duration
                &&timing.Commands.SequenceEqual(reference.Commands),"Cache mode preserves the complete native command schedule and final frame: "+cached);
            results.Add((JsonNode)new JsonObject{["cacheEnabled"]=cached,["endFrame"]=timing.EndFrame,["duration"]=timing.Duration,
                ["commands"]=timing.Commands.Count,["commandHash"]=Hash(CommandJson(timing.Commands).ToJsonString())});
        }
    }

    static async Task VerifySessionAsync(string fixture,string path,string core,string ffmpeg,Sample[] reference,Action<bool,string> check,JsonObject report)
    {
        await using var session=new RealtimePreviewSession();
        await session.LoadSceneAsync(new(fixture,path,core,ffmpeg,960,540,30));
        var cold=session.LastFrame!.Initialization!;report["sessionCold"]=cold.ToJson();
        await session.SeekAsync(20);var forward=session.LastFrame!;
        var expected=reference.Single(sample=>sample.Frame==600);
        check(Archive.Hash(forward.Pixels.Rgba)==expected.Pixels&&Hash(AnimationJson(forward.ActorAnimations!).ToJsonString())==expected.Animations,
            "The production preview's forward frame agrees with independent full native reconstruction");
        long started=Stopwatch.GetTimestamp();await session.SeekAsync(8);var backward=session.LastFrame!;
        report["sessionBackwardMs"]=Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var warm=backward.Initialization!;report["sessionWarm"]=warm.ToJson();expected=reference.Single(sample=>sample.Frame==240);
        check(Archive.Hash(backward.Pixels.Rgba)==expected.Pixels&&Hash(AnimationJson(backward.ActorAnimations!).ToJsonString())==expected.Animations
            &&backward.Frame==240&&!session.IsPlaying,
            "Backward preview reconstruction restores exact independently simulated pixels, animation and paused position");
        check(warm.ModelResources is {} resource&&resource["assetDecodes"]!.GetValue<long>()==0&&resource["clipCompilations"]!.GetValue<long>()==0
            &&resource["assetHits"]!.GetValue<long>()>0&&resource["clipHits"]!.GetValue<long>()>0,
            "The production SceneState retains verified model bytes and immutable clips across native disposal and backward replay");
        await session.SeekAsync(20);var replay=session.LastFrame!;
        check(replay.Pixels.Rgba.SequenceEqual(forward.Pixels.Rgba)&&replay.ActorAnimations!.SequenceEqual(forward.ActorAnimations!),
            "Forward replay after a cached reconstruction preserves the original fully evaluated pose and image");
    }
}
