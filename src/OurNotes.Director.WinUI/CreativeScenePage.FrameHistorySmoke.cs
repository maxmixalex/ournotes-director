using System.Diagnostics;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.WinUI;

internal sealed partial class CreativeScenePage
{
    async Task RunOriginalFrameHistorySmokeAsync(Action<bool,string> check)
    {
        string saved=document.ToJson();
        // Fill a known, densely visited interval through ordinary editor seeks.
        // Also run real playback, recording its opportunistic coverage separately.
        // The pointer test must not assume every skipped playback frame is cached.
        for(int frame=3481;frame<=3492;frame++)await SeekAsync(frame/30d);
        var beforePlayback=viewport.LastFrame!;long session=beforePlayback.SessionId;
        long stamp=Stopwatch.GetTimestamp();viewport.Play(117);
        var playing=await viewport.WaitForFrameAsync(beforePlayback.Sequence,TimeSpan.FromSeconds(30));
        while(playing.IsPlaying||playing.Frame<3509)
            playing=await viewport.WaitForFrameAsync(playing.Sequence,TimeSpan.FromSeconds(30));
        await viewport.PauseForEditingAsync();
        var afterPlayback=viewport.LastFrame!;
        double playbackMs=Stopwatch.GetElapsedTime(stamp).TotalMilliseconds;
        check(afterPlayback.NativeStateSynchronized&&afterPlayback.Frame>=3509
            &&afterPlayback.SessionId==session&&afterPlayback.FrameCache?.Frames>0,
            "真实原剧情播放继续推进 native 状态，并保留有界的实际帧历史");

        var points=timeline.ReverseScrubCoordinates(3492/30d,3481/30d);
        var samples=await viewport.SendTimelineScrubSmokeAsync(window,points.Element,points.StartX,points.EndX,
            "millsage-playback-reverse-trace.json");
        await scrubber.FlushAsync();
        var completed=viewport.LastFrame!;
        var cached=samples.Where(sample=>sample.Cached).ToArray();
        var report=new JsonObject
        {
            ["schema"]="ournotes-director/original-frame-history-smoke/1",["source"]="10060",
            ["visitedFirst"]=3481,["visitedLast"]=3492,["playbackElapsedMs"]=playbackMs,
            ["playbackLastFrame"]=afterPlayback.Frame,["playbackCache"]=Cache(afterPlayback.FrameCache),
            ["heldFrames"]=samples.Count,["cachedHeldFrames"]=cached.Length,
            ["distinctCachedFrames"]=cached.Select(value=>value.Frame).Distinct().Count(),
            ["settledFrame"]=completed.Frame,["expectedFrame"]=(int)Math.Round(points.EndTime*30),
            ["settledScale"]=completed.ScenePreviewScale,["settledNativeSynchronized"]=completed.NativeStateSynchronized,
            ["settledRenderMs"]=completed.RenderMilliseconds,["settledSimulationFrames"]=completed.Performance?.SimulationFrames,
            ["device"]=completed.Device,["wallClockIsDiagnosticOnly"]=true
        };
        DesktopSettings.AtomicWrite(App.SmokeEvidencePath("millsage-frame-history-workbench.json"),report.ToJsonString(DesktopSettings.JsonOptions));
        check(cached.Select(value=>value.Frame).Distinct().Count()>=4
            &&cached.All(value=>value.Session==session&&!value.Playing&&value.NativeFrame==afterPlayback.Frame),
            "播放后真实鼠标连续回拖已访问区域，至少四个精确历史帧在松手前显示且不重建 native 会话");
        check(cached.Where(value=>value.Pixels is not null).Select(value=>value.Pixels).Distinct().Count()>=4,
            "连续回拖改变的是实际缓存像素，不只是时间码或相邻旧图");
        check(completed.Frame==(int)Math.Round(points.EndTime*30)&&completed.NativeStateSynchronized
            &&!completed.IsCached&&completed.ScenePreviewScale==1&&!viewport.IsPlaying&&document.ToJson()==saved,
            "长原剧情回拖松手后同步最终真实 native 帧与完整质量，保持暂停且不改写文档");
        await App.CaptureCheckpointAsync("millsage-frame-history-settled");
    }

    static JsonObject? Cache(PreviewFrameCacheDiagnostics? value)=>value is not {} cache?null:new JsonObject
    {
        ["frames"]=cache.Frames,["retainedBytes"]=cache.RetainedBytes,["limitBytes"]=cache.LimitBytes,
        ["storeMs"]=cache.StoreMilliseconds,["restoreMs"]=cache.RestoreMilliseconds,
        ["capturedFrames"]=cache.CapturedFrames,["captureMs"]=cache.CaptureMilliseconds,
        ["ranges"]=new JsonArray((cache.Ranges??[]).Select(range=>(JsonNode)new JsonObject
            {["first"]=range.First,["last"]=range.Last}).ToArray())
    };
}
