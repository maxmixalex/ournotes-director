using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

static class PreviewFrameHistoryNativeTests
{
    // Same binary, same original story, identical final draws. Extra captures
    // cover the initial Stage transition, active particles and the reported seek.
    public static Dictionary<int,PreviewFrameCache.Frame> Run(string fixture,string project,string core,string ffmpeg,
        Action<bool,string> check,JsonObject report)
    {
        var textures=new PreviewTextureCache();var models=new PreviewModelCache();var cues=new AdvCueCache();
        var history=new Dictionary<int,PreviewFrameCache.Frame>();
        var runs=new JsonArray();report["historyParity"]=runs;
        var baseline=new List<string>();var baselinePlayback=new List<string>();
        int[] endpoints=[61,1001,3480];int[] captureFrames=[0,1,2,58,59,60,998,999,1000,3474,3475,3476,3477,3478,3479];
        foreach(bool enabled in new[]{false,true})
        {
            var archive=new Archive(fixture){PreviewTextures=textures,PreviewModels=models};
            using var renderer=new AdvStoryRenderer(archive,"intl",core,"10060","zh-Hans",960,540,project,ffmpeg,1,cueCache:cues);
            var samples=new JsonArray();var playback=new JsonArray();
            var run=new JsonObject{["enabled"]=enabled,["samples"]=samples,["playback"]=playback};runs.Add((JsonNode)run);
            void Capture(int frame)
            {
                if(!captureFrames.Contains(frame)||!renderer.CanDrawHistory)return;
                history.Add(frame,renderer.CaptureHistoryFrame(false));
            }
            for(int i=0;i<endpoints.Length;i++)
            {
                int frame=endpoints[i];long stamp=Stopwatch.GetTimestamp();
                var pixels=renderer.FrameWithHistory(frame,false,null,false,default,enabled?Capture:null);
                var state=State(renderer,pixels);string serialized=state.ToJsonString();
                samples.Add((JsonNode)new JsonObject{["frame"]=frame,["elapsedMs"]=Stopwatch.GetElapsedTime(stamp).TotalMilliseconds,["state"]=state});
                if(!enabled)baseline.Add(serialized);
                else check(serialized==baseline[i],$"Extra quarter-size draws preserve exact final RGBA, both native parameter buffers, particles, PCM and commands at frame {frame}");
                Console.WriteLine($"History parity enabled={enabled}, frame={frame}");
            }
            var cache=new PreviewFrameCache();int nextCapture=3481,captured=0;
            long playbackStarted=Stopwatch.GetTimestamp();double captureMs=0;
            for(int frame=3481;frame<=3492;frame++)
            {
                renderer.CapturePreviewPixels=enabled&&frame>=nextCapture;renderer.PreviewCaptureScale=.25;
                long stamp=Stopwatch.GetTimestamp();var pixels=renderer.Frame(frame);
                if(renderer.CapturedPreviewPixels is {} image)
                {
                    double before=cache.StoreMilliseconds;
                    cache.Store(new(frame,image,.25,renderer.PreviewTargets(),renderer.Scene.PreviewLipSyncState(),renderer.Scene.PreviewAnimationState()));
                    double cost=renderer.PreviewCaptureMilliseconds+cache.StoreMilliseconds-before;
                    captureMs+=cost;captured++;nextCapture=frame+Math.Clamp((int)Math.Ceiling(cost/2),1,30);
                    check(image.Width==240&&image.Height==135&&image.ByteLength==240*135*4,
                        $"Playback history {frame} copies a real quarter-size RGBA image without replacing the full presentation");
                }
                string state=State(renderer,pixels).ToJsonString();
                if(!enabled)baselinePlayback.Add(state);
                else check(state==baselinePlayback[frame-3481],$"Bounded playback readback preserves full native presentation and state at frame {frame}");
                playback.Add((JsonNode)new JsonObject{["frame"]=frame,["elapsedMs"]=Stopwatch.GetElapsedTime(stamp).TotalMilliseconds,
                    ["readbackMs"]=renderer.PreviewCaptureMilliseconds,["stored"]=cache.Contains(frame,.25)});
            }
            run["playbackElapsedMs"]=Stopwatch.GetElapsedTime(playbackStarted).TotalMilliseconds;
            run["playbackCapturedFrames"]=captured;run["playbackCaptureAndStoreMs"]=captureMs;
            run["playbackRetainedBytes"]=cache.RetainedBytes;run["device"]=renderer.Device;
        }
        check(captureFrames.All(history.ContainsKey),"Original-story Stage, particle and near-target capture probes all executed at their actual frame");
        report["capturedReferenceFrames"]=new JsonArray(history.Keys.Order().Select(frame=>(JsonNode)JsonValue.Create(frame)!).ToArray());
        return history;
    }

    static JsonObject State(AdvStoryRenderer renderer,Pixels pixels)
    {
        var evaluated=new JsonObject();var native=new JsonObject();
        foreach(string name in new[]{"hotaru_child","hotaru","kanata"})
        {
            var actor=renderer.Scene.Character(name);if(actor is null)continue;
            evaluated[name]=FloatHash(actor.Values);native[name]=FloatHash(actor.Core.Floats("ParameterValues",actor.Core.ParameterIds.Length));
        }
        var animations=new JsonArray(renderer.Scene.PreviewAnimationState().Select(value=>(JsonNode)new JsonObject
        { ["id"]=value.TargetId,["model"]=value.Model,["motion"]=value.Motion,["expression"]=value.Expression,["timeBits"]=BitConverter.DoubleToInt64Bits(value.MotionTime) }).ToArray());
        var lips=new JsonArray(renderer.Scene.PreviewLipSyncState().Select(value=>(JsonNode)new JsonObject
        { ["id"]=value.TargetId,["engine"]=value.Engine,["active"]=value.IsActive,["mouthBits"]=BitConverter.SingleToInt32Bits(value.MouthOpen),["missing"]=value.Missing }).ToArray());
        var commands=new JsonArray(renderer.Player.CommandTimings().Select(value=>(JsonNode)new JsonObject
        { ["section"]=value.Section,["index"]=value.Index,["command"]=value.Command,["start"]=value.StartFrame,["end"]=value.EndFrame,["ordinal"]=value.Ordinal,["key"]=value.Key }).ToArray());
        return new JsonObject{["frame"]=renderer.Loop.Frame,["pixels"]=Archive.Hash(pixels.Rgba),["evaluated"]=evaluated,["native"]=native,
            ["animations"]=animations,["lips"]=lips,["pcm"]=FloatHash(renderer.Audio.MixFrame()),["commands"]=commands,
            ["particles"]=JsonOutput.SerializeToNode(renderer.Frames.Snapshot()),["captures"]=renderer.Scene.CaptureDrawCount};
    }
    static string FloatHash(float[] values)=>Archive.Hash(MemoryMarshal.AsBytes(values.AsSpan()).ToArray());
}
