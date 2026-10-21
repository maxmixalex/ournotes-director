using System.Diagnostics;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

static class PreviewFrameCacheNativeTests
{
    public static async Task<int> RunAsync(string fixture,string output)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        var checks=new JsonArray();var phases=new JsonArray();
        var report=new JsonObject{["schema"]="ournotes-director/exact-frame-cache/1",["ok"]=false,
            ["wallClockIsDiagnosticOnly"]=true,["checks"]=checks,["phases"]=phases};
        void Check(bool ok,string name)
        {checks.Add((JsonNode)new JsonObject{["name"]=name,["ok"]=ok});Console.WriteLine(name);if(!ok)throw new InvalidOperationException(name);}
        try
        {
            PreviewFrameCacheTests.Run(Check);NativeTestGraphics.Configure();
            await PreviewFrameMediaBoundaryTests.RunAsync(Check);
            string path=Path.Combine(output,"original.project.json");
            var project=new JsonObject{["format"]=DirectorProject.Format,["version"]=1,
                ["source"]=new JsonObject{["adv"]="10060",["site"]="intl",["language"]="zh-Hans"},["output"]=new JsonObject()};
            File.WriteAllText(path,project.ToJsonString());string saved=File.ReadAllText(path);
            string core=NativeDependencies.CoreLibrary(baseDirectory:NativeTestGraphics.PackageDirectory);
            string ffmpeg=NativeDependencies.MediaTool("ffmpeg",baseDirectory:NativeTestGraphics.PackageDirectory);
            var capturedReference=await Task.Run(()=>PreviewFrameHistoryNativeTests.Run(fixture,path,core,ffmpeg,Check,report));
            await using var session=new RealtimePreviewSession();
            async Task<PreviewFrame> Measure(string name,Func<Task> action)
            {
                long stamp=Stopwatch.GetTimestamp();await action();
                var frame=session.LastFrame??throw new InvalidOperationException("No frame: "+name);
                double elapsed=Stopwatch.GetElapsedTime(stamp).TotalMilliseconds;
                phases.Add((JsonNode)Snapshot(name,frame,elapsed));
                Console.WriteLine($"Frame cache {name}: frame={frame.Frame}, cached={frame.IsCached}, native={frame.FrameCache?.NativeFrame}, {elapsed:F2} ms");return frame;
            }
            await Measure("load",()=>session.LoadSceneAsync(new(Path.GetFullPath(fixture),path,core,ffmpeg,
                Width:960,Height:540,Duration:200,GpuPresentation:false,Supersample:1)));
            var reference=new Dictionary<int,PreviewFrame>();
            for(int i=0;i<=10;i++)
            {
                var frame=await Measure("firstVisit"+i,()=>session.SeekAsync(115+i/10d));reference.Add(frame.Frame,frame);
            }
            var retainedBeforeReverse=session.LastFrame!.FrameCache;
            report["retainedBeforeReverse"]=CacheJson(retainedBeforeReverse);
            var endpoint=session.LastFrame!;long originalSession=endpoint.SessionId;
            await session.SetScenePreviewScaleAsync(.25);
            int newHits=0;
            // These intermediate source frames were not requested by the editor.
            // Opportunistic capture is cost-bounded: report coverage, never assume
            // a particular machine can draw every candidate within the budget.
            for(int target=3479;target>=3475;target--)
            {
                if(reference.ContainsKey(target))continue;
                var sampled=await Measure("firstIntermediate"+target,()=>session.ScrubAsync(target/30d));
                Check(sampled.Frame==target&&sampled.Time==target/30d
                    &&(sampled.IsCached||sampled.NativeStateSynchronized),$"First reverse visit to intermediate frame {target} never displays a neighboring frame");
                if(sampled.IsCached)
                {
                    newHits++;var expected=capturedReference[target];
                    Check(sampled.ScenePreviewScale==.25&&sampled.Pixels.Width==240&&sampled.Pixels.Height==135
                        &&sampled.Pixels.Rgba.AsSpan().SequenceEqual(expected.Pixels.Rgba)
                        &&(sampled.ActorAnimations??[]).SequenceEqual(expected.Animations)
                        &&(sampled.LipSync??[]).SequenceEqual(expected.LipSync),
                        $"Intermediate cached frame {target} is the exact independently rendered quarter-size pose and truthful quality");
                }
            }
            report["firstIntermediateTargets"]=4;report["firstIntermediateHits"]=newHits;
            // Re-establish the native endpoint so the long known-frame reverse
            // sweep below measures history alone, independently of first misses.
            await session.SetScenePreviewScaleAsync(1);await session.SeekAsync(116);originalSession=session.LastFrame!.SessionId;
            await session.SetScenePreviewScaleAsync(.25);
            for(int i=9;i>=0;i--)
            {
                var cached=await Measure("reverseCached"+i,()=>session.ScrubAsync(115+i/10d));
                var expected=reference[cached.Frame];
                Check(cached.IsCached&&!cached.NativeStateSynchronized&&cached.SessionId==originalSession
                    &&cached.FrameCache?.NativeFrame==3480&&cached.Performance?.SimulationFrames==0
                    &&cached.Frame==3450+i*3&&cached.Time==cached.Frame/30d&&ExactImageState(expected,cached),
                    $"Continuous reverse scrub {cached.Frame} publishes that exact image and geometry without rebuilding or claiming native rewind");
            }
            var beforeCancel=session.LastFrame!;
            using(var cancel=new CancellationTokenSource())
            {
                cancel.Cancel();bool cancelled=false;
                try{await session.ScrubAsync(115.9,cancel.Token);}catch(OperationCanceledException){cancelled=true;}
                Check(cancelled&&ReferenceEquals(beforeCancel,session.LastFrame),"A cancelled cached seek preserves the last completed displayed frame");
            }
            await session.SetScenePreviewScaleAsync(1);
            var settled=await Measure("settle115Full",()=>session.SeekAsync(115));
            Check(!settled.IsCached&&settled.NativeStateSynchronized&&settled.FrameCache?.NativeFrame==3450
                &&settled.Performance?.SimulationFrames==3450&&ExactImageState(reference[3450],settled),
                "Mouse-up strict seek restores the real 115-second native state and exact full-quality image");
            Check(SameIntermediateHistory(beforeCancel,settled),
                "Mouse-up reconstruction performs no optional intermediate history draws or capture work");

            await session.SetScenePreviewScaleAsync(.25);
            var miss=await Measure("firstMiss114",()=>session.ScrubAsync(114));
            Check(!miss.IsCached&&miss.NativeStateSynchronized&&miss.Frame==3420&&miss.FrameCache?.NativeFrame==3420,
                "An uncached backward target performs native replay and publishes its own exact frame");
            Check(SameIntermediateHistory(settled,miss),
                "An uncached backward reconstruction performs no optional intermediate history draws or capture work");
            var hit=await Measure("repeat114",()=>session.ScrubAsync(114));
            Check(hit.IsCached&&hit.NativeStateSynchronized&&ExactImageState(miss,hit),
                "The same newly visited fractional frame is subsequently restored losslessly from history");
            var scrubbed=await Measure("forwardScrubMiss114_1",()=>session.ScrubAsync(114.1));
            Check(!scrubbed.IsCached&&scrubbed.NativeStateSynchronized&&scrubbed.Frame==3423
                &&scrubbed.SessionId==miss.SessionId&&scrubbed.Performance?.SimulationFrames==3
                &&SameIntermediateHistory(hit,scrubbed),
                "An explicit forward scrub miss advances the real native state without optional intermediate capture");
            var scrubbedHit=await Measure("repeatForwardScrub114_1",()=>session.ScrubAsync(114.1));
            Check(scrubbedHit.IsCached&&scrubbedHit.NativeStateSynchronized&&ExactImageState(scrubbed,scrubbedHit),
                "The explicit forward scrub still retains its exact final FBO and displayed state");
            var forward=await Measure("forwardResumesHistory114_2",()=>session.SeekAsync(114.2));
            Check(!forward.IsCached&&forward.NativeStateSynchronized&&forward.Frame==3426
                &&forward.SessionId==miss.SessionId&&forward.Performance?.SimulationFrames==3
                &&forward.FrameCache is {} forwardCache&&scrubbed.FrameCache is {} scrubbedCache
                &&forwardCache.CapturedFrames>scrubbedCache.CapturedFrames
                &&forwardCache.CaptureMilliseconds>scrubbedCache.CaptureMilliseconds,
                "A normal forward seek after an explicit scrub resumes bounded intermediate history capture");
            var forwardHit=await Measure("repeatForward114_2",()=>session.ScrubAsync(114.2));
            Check(forwardHit.IsCached&&forwardHit.NativeStateSynchronized&&ExactImageState(forward,forwardHit),
                "The stable forward target still retains its exact final FBO and displayed state");
            await session.ScrubAsync(115);
            var forwardPending=session.LastFrame!;
            var forwardSettled=await Measure("strictForwardSettle115",()=>session.SeekAsync(115));
            Check(forwardPending.IsCached&&!forwardPending.NativeStateSynchronized
                &&!forwardSettled.IsCached&&forwardSettled.NativeStateSynchronized&&forwardSettled.Frame==3450
                &&forwardSettled.SessionId==forward.SessionId&&forwardSettled.Performance?.SimulationFrames==24
                &&SameIntermediateHistory(forwardPending,forwardSettled),
                "Strict forward catch-up to the displayed cached frame omits intermediate capture without rebuilding");
            await session.ScrubAsync(115.5);
            var pending=session.LastFrame!;
            var parameters=await session.ReadActorParametersAsync("hotaru_child");
            var parameterState=session.LastFrame!;
            Check(pending.IsCached&&!pending.NativeStateSynchronized&&parameters.Count>0
                &&parameterState.NativeStateSynchronized&&parameterState.Frame==3465&&parameterState.FrameCache?.NativeFrame==3465
                &&SameIntermediateHistory(pending,parameterState),
                "Reading model parameters synchronizes the displayed native frame without optional intermediate capture");

            await session.ScrubAsync(115.8);
            var playStart=session.LastFrame!;
            session.Play(115.8+1/30d);await session.SynchronizeAsync();session.Pause();await session.SynchronizeAsync();
            var played=session.LastFrame!;
            Check(playStart.IsCached&&played.NativeStateSynchronized&&played.ScenePreviewScale==1
                &&played.Frame==3474&&played.FrameCache?.NativeFrame==3474&&ExactImageState(reference[3474],played)
                &&SameIntermediateHistory(playStart,played),
                "Playback restores native state and full quality before starting its clock without optional intermediate capture");
            using(var cancel=new CancellationTokenSource())
            {
                var before=session.LastFrame;session.SceneReconstructionStarted=cancel.Cancel;bool cancelled=false;
                try{await session.SeekAsync(113,cancel.Token);}catch(OperationCanceledException){cancelled=true;}
                finally{session.SceneReconstructionStarted=null;}
                Check(cancelled&&ReferenceEquals(before,session.LastFrame),"Cancellation inside reconstruction retains the last complete non-cached displayed frame");
                var recovered=await session.ReadActorParametersAsync("hotaru_child");
                Check(recovered.Count>0&&session.LastFrame!.NativeStateSynchronized&&session.LastFrame.Frame==3474,
                    "Parameter inspection after a cancelled reconstruction restores the actual displayed native state");
            }
            await session.ScrubAsync(115.5);
            var editable=parameters.First(parameter=>parameter.Maximum>parameter.Minimum);
            double editValue=(float)(editable.Minimum+(editable.Maximum-editable.Minimum)*.375);
            if(editValue==editable.Default)editValue=(float)(editable.Minimum+(editable.Maximum-editable.Minimum)*.625);
            await session.ApplySceneEditsAsync([new JsonObject{["cmd"]="Director.Parameter",["TargetName"]="hotaru_child",
                ["Parameter"]=editable.Id,["Value"]=editValue,["Duration"]=0}]);
            var edited=session.LastFrame!;var editedParameters=await session.ReadActorParametersAsync("hotaru_child");
            Check(!edited.IsCached&&edited.NativeStateSynchronized&&edited.Frame==3465&&edited.FrameCache?.Frames==0
                &&editedParameters.Single(p=>p.Id==editable.Id).Value==editValue,
                "Editing a cached pose restores its native frame and invalidates every old image before applying the parameter");
            await session.ResizePreviewAsync(480,270);
            var resized=await Measure("resizeInvalidation",()=>session.ScrubAsync(115.9));
            Check(!resized.IsCached&&resized.NativeStateSynchronized&&resized.Pixels.Width==480&&resized.Pixels.Height==270,
                "Resizing invalidates old-dimension image history before another scrub");
            Check(!session.IsPlaying&&File.ReadAllText(path)==saved,"Image history never modifies the original story document");
            report["ok"]=true;Console.WriteLine($"Passed {checks.Count} exact-frame history checks.");return 0;
        }
        catch(Exception error){report["exception"]=error.ToString();throw;}
        finally{File.WriteAllText(Path.Combine(output,"preview-frame-cache-report.json"),report.ToJsonString(new(){WriteIndented=true}));}
    }

    static bool ExactImageState(PreviewFrame a,PreviewFrame b)=>a.Pixels.Rgba.AsSpan().SequenceEqual(b.Pixels.Rgba)
        &&a.Targets.SequenceEqual(b.Targets)&&(a.ActorAnimations??[]).SequenceEqual(b.ActorAnimations??[])
        &&(a.LipSync??[]).SequenceEqual(b.LipSync??[])&&a.ScenePreviewScale==b.ScenePreviewScale;
    static bool SameIntermediateHistory(PreviewFrame before,PreviewFrame after)=>before.FrameCache is {} a&&after.FrameCache is {} b
        &&a.CapturedFrames==b.CapturedFrames&&a.CaptureMilliseconds==b.CaptureMilliseconds;
    static JsonObject Snapshot(string name,PreviewFrame frame,double elapsed)=>new()
    {
        ["name"]=name,["frame"]=frame.Frame,["seconds"]=frame.Time,["session"]=frame.SessionId,["scale"]=frame.ScenePreviewScale,
        ["cached"]=frame.IsCached,["nativeStateSynchronized"]=frame.NativeStateSynchronized,["elapsedMs"]=elapsed,
        ["pixelWidth"]=frame.Pixels.Width,["pixelHeight"]=frame.Pixels.Height,
        ["renderMs"]=frame.RenderMilliseconds,["rgbaSha256"]=Archive.Hash(frame.Pixels.Rgba),["device"]=frame.Device,
        ["initialization"]=frame.Initialization?.ToJson(),
        ["performance"]=frame.Performance is not {} p?null:new JsonObject{["simulationFrames"]=p.SimulationFrames,
            ["simulationMs"]=p.SimulationMilliseconds,["sceneMs"]=p.SceneMilliseconds,["readbackMs"]=p.ReadbackMilliseconds},
        ["cache"]=CacheJson(frame.FrameCache)
    };
    static JsonObject? CacheJson(PreviewFrameCacheDiagnostics? value)=>value is not {} c?null:new JsonObject
        {["frames"]=c.Frames,["retainedBytes"]=c.RetainedBytes,
            ["limitBytes"]=c.LimitBytes,["hits"]=c.Hits,["misses"]=c.Misses,["storeMs"]=c.StoreMilliseconds,
            ["restoreMs"]=c.RestoreMilliseconds,["nativeFrame"]=c.NativeFrame,["capturedFrames"]=c.CapturedFrames,
            ["captureMs"]=c.CaptureMilliseconds,["ranges"]=new JsonArray((c.Ranges??[]).Select(range=>(JsonNode)new JsonObject
                {["first"]=range.First,["last"]=range.Last}).ToArray())};
}
