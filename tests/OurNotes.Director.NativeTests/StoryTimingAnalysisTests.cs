using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

static class StoryTimingAnalysisTests
{
    const string WorkerEnvironment = "OURNOTES_TEST_BLOCKED_AUDIO_DECODER";

    public static async Task<int?> HandleDecoderWorkerAsync(string[] args)
    {
        if (Environment.GetEnvironmentVariable(WorkerEnvironment) is not { Length: > 0 } path
            || !args.Contains("f32le")) return null;
        if (Directory.Exists(path)) path = Path.Combine(path, Environment.ProcessId + ".json");
        int input = Array.IndexOf(args, "-i");
        File.WriteAllText(path + ".tmp", new JsonObject { ["pid"] = Environment.ProcessId,
            ["input"] = input >= 0 ? args[input + 1] : "" }.ToJsonString());
        File.Move(path + ".tmp", path, true);
        await Task.Delay(Timeout.Infinite); return 0;
    }

    public static async Task<int> RunAsync(string fixture, string output)
    {
        Directory.CreateDirectory(output);
        var checks = new JsonArray(); var measurements = new JsonArray();
        var report = new JsonObject { ["schema"] = "ournotes-director/story-analysis-regression/1", ["ok"] = false,
            ["source"] = "10462", ["checks"] = checks, ["measurements"] = measurements };
        void Check(bool value, string name)
        {
            checks.Add((JsonNode)new JsonObject { ["name"] = name, ["ok"] = value });
            if (!value) throw new InvalidOperationException(name);
        }
        try
        {
            PrefetchControl(Check);
            await CancelledDecoder(output, Check, report);
            NativeTestGraphics.Configure();
            string core = NativeDependencies.CoreLibrary(baseDirectory: NativeTestGraphics.PackageDirectory);
            string ffmpeg = NativeDependencies.MediaTool("ffmpeg", baseDirectory: NativeTestGraphics.PackageDirectory);
            var archive = new Archive(fixture);
            var project = new JsonObject { ["format"] = DirectorProject.Format, ["version"] = 1,
                ["source"] = new JsonObject { ["adv"] = "10462", ["site"] = "intl", ["language"] = "zh-Hans" },
                ["output"] = new JsonObject() };
            string path = Path.Combine(output, "original.project.json"); File.WriteAllText(path, project.ToJsonString());
            string hash = Archive.Hash(Encoding.UTF8.GetBytes(project.ToJsonString()));
            var source = new ScenePreviewSource(fixture, path, core, ffmpeg, 960, 540, 36000);
            NativePlaybackTiming? reference = null;
            foreach (string mode in new[] { "sequential", "afterInitialization", "duringInitialization", "duringInitialization", "afterInitialization", "sequential" })
            {
                var watch = Stopwatch.StartNew();
                NativePlaybackTiming timing;
                StoryTimingAnalysisDiagnostics? diagnostics=null;
                bool parallel=mode!="sequential";
                if (mode=="duringInitialization") timing = StoryTimingAnalysis.Analyze(source,diagnostics:value=>diagnostics=value);
                else
                {
                    var cache=new AdvCueCache();
                    using var native = new AdvStoryRenderer(archive, "intl", core, "10462", "zh-Hans",
                        960, 540, path, ffmpeg, 1, project, true, cache);
                    using var prefetch=parallel?new StoryAudioPrefetch(cache,StoryAudioPrefetch.VoiceCandidates(native.Player.Program),native.PrefetchAudio!,CancellationToken.None):null;
                    if(prefetch is not null)native.Audio.ResolveCue=prefetch.Get;
                    timing = native.AnalyzeTiming(hash);
                }
                measurements.Add((JsonNode)new JsonObject { ["prefetch"] = parallel, ["prefetchMode"]=mode,
                    ["diagnostics"]=diagnostics?.ToJson(), ["ms"] = watch.Elapsed.TotalMilliseconds,
                    ["endFrame"] = timing.EndFrame, ["commands"] = timing.Commands.Count });
                reference ??= timing;
                Check(timing.IsComplete && timing.ProjectHash == reference.ProjectHash && timing.EndFrame == reference.EndFrame
                    && timing.Duration == reference.Duration && timing.Commands.SequenceEqual(reference.Commands),
                    "Real original story preserves every native command start/end/ordinal and its final frame: " + mode);
            }
            report["endFrame"] = reference!.EndFrame; report["commands"] = reference.Commands.Count;
            // Serial and prefetched analyses both use timingOnly. Compare against
            // the real rendering path as well, so a shared shortcut cannot make
            // those two agree on a wrong command schedule.
            var fullWatch = Stopwatch.StartNew();
            using (var full = new AdvStoryRenderer(archive, "intl", core, "10462", "zh-Hans",
                960, 540, path, ffmpeg, 1, project, false, new AdvCueCache()))
            {
                var fullTiming = full.AnalyzeTiming(hash);
                Check(fullTiming.IsComplete && fullTiming.ProjectHash == reference!.ProjectHash
                    && fullTiming.EndFrame == reference.EndFrame && fullTiming.Duration == reference.Duration
                    && fullTiming.Commands.SequenceEqual(reference.Commands),
                    "Timing-only visual-resource shortcuts preserve the full renderer's native command schedule");
                report["fullRendererReference"] = new JsonObject { ["ms"] = fullWatch.Elapsed.TotalMilliseconds,
                    ["endFrame"] = fullTiming.EndFrame, ["commands"] = fullTiming.Commands.Count };
            }
            var manifest = archive.Manifest("intl", "stories", "10462");
            var definition = JsonNode.Parse(archive.Logical("intl", manifest, "story.json"))!;
            var episode = JsonNode.Parse(archive.Logical("intl", manifest, Archive.S(definition, "episode")))!;
            var scene = JsonNode.Parse(archive.Logical("intl", manifest, Archive.S(definition, "scene")))!;
            var program = AdvProgram.From(episode, scene["settings"]!["playerSettings"]!);
            var voiceIds = StoryAudioPrefetch.VoiceCandidates(program).Distinct().Take(8).ToArray();
            using (var prefetch = new StoryAudioPrefetch(new AdvCueCache(), voiceIds,
                AdvAudio.IndependentDecoder(archive, "intl", manifest, episode, ffmpeg), CancellationToken.None))
            {
                foreach (long id in voiceIds.Take(2))
                {
                    var actual = prefetch.Get(id); var expected = AdvAudio.Decode(archive, "intl", manifest, episode, id, ffmpeg);
                    Check(actual.Channels == expected.Channels && actual.Samples == expected.Samples
                        && actual.Category == expected.Category && actual.LoopStart == expected.LoopStart && actual.LoopEnd == expected.LoopEnd
                        && actual.Pcm.SequenceEqual(expected.Pcm), "Prefetched real voice retains its complete, sample-identical native PCM: " + id);
                }
            }
            await CancelledNativeAnalysis(source,output,Check,report);
            await StoryTimingRenderStateTests.RunAsync(archive,core,ffmpeg,output,Check,report);
            Check(File.ReadAllText(path) == project.ToJsonString(), "Native analysis never modifies the input source project");
            report["ok"] = true;
            Console.WriteLine(report.ToJsonString());
            return 0;
        }
        finally { File.WriteAllText(Path.Combine(output, "report.json"), report.ToJsonString(new() { WriteIndented = true })); }
    }

    static async Task CancelledNativeAnalysis(ScenePreviewSource source,string output,Action<bool,string> check,JsonObject report)
    {
        string workers=Path.Combine(output,"cancelled-native-analysis");Directory.CreateDirectory(workers);
        foreach(string file in Directory.EnumerateFiles(workers,"*.json"))File.Delete(file);
        string executable=Environment.ProcessPath!;
        if(Path.GetFileNameWithoutExtension(executable).Equals("dotnet",StringComparison.OrdinalIgnoreCase))
            executable=Path.Combine(AppContext.BaseDirectory,"OurNotes.Director.NativeTests"+(OperatingSystem.IsWindows()?".exe":""));
        string? previous=Environment.GetEnvironmentVariable(WorkerEnvironment);
        using var cancellation=new CancellationTokenSource();
        Task<NativePlaybackTiming>? task=null;
        try
        {
            Environment.SetEnvironmentVariable(WorkerEnvironment,workers);
            task=Task.Factory.StartNew(()=>StoryTimingAnalysis.Analyze(source with{FfmpegPath=executable},cancellation.Token),
                CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
            var start=Stopwatch.StartNew();
            while(!Directory.EnumerateFiles(workers,"*.json").Any()&&!task.IsCompleted&&start.Elapsed<TimeSpan.FromSeconds(10))await Task.Delay(20);
            check(Directory.EnumerateFiles(workers,"*.json").Any(),"Native timing initialization starts real independent voice decoders");
            start.Restart();cancellation.Cancel();bool stopped=false;
            try{await task.WaitAsync(TimeSpan.FromSeconds(10));}catch(OperationCanceledException){stopped=true;}
            var children=Directory.EnumerateFiles(workers,"*.json").Select(path=>JsonNode.Parse(File.ReadAllText(path))!).ToArray();
            bool Reaped(JsonNode child)
            {
                bool exited;try{using var process=Process.GetProcessById(child["pid"]!.GetValue<int>());exited=process.HasExited;}catch(ArgumentException){exited=true;}
                return exited&&!File.Exists(child["input"]!.ToString());
            }
            check(stopped&&children.Length>0&&children.All(Reaped),
                "Cancelling native analysis joins every started decoder and removes its private audio input before returning");
            report["nativeCancellation"]=new JsonObject{["cancelled"]=stopped,["startedDecoders"]=children.Length,["cancelMs"]=start.Elapsed.TotalMilliseconds,["allChildrenReaped"]=children.All(Reaped)};
        }
        finally
        {
            cancellation.Cancel();
            // This is failure cleanup only: the assertion above observes the
            // production cancellation result before any fallback kills a child.
            if(task is {IsCompleted:false})foreach(string path in Directory.EnumerateFiles(workers,"*.json"))
            {
                int pid=JsonNode.Parse(File.ReadAllText(path))!["pid"]!.GetValue<int>();
                try{using var child=Process.GetProcessById(pid);if(!child.HasExited)child.Kill(true);}
                catch(ArgumentException){}
            }
            try{if(task is not null)await task.WaitAsync(TimeSpan.FromSeconds(10));}catch(OperationCanceledException){}
            finally{Environment.SetEnvironmentVariable(WorkerEnvironment,previous);}
        }
    }

    static void PrefetchControl(Action<bool, string> check)
    {
        var decoded = new ConcurrentDictionary<long, int>();
        using var release = new ManualResetEventSlim(); using var started = new CountdownEvent(4);
        using var initial = new CountdownEvent(8); using var skipped = new ManualResetEventSlim();
        int active = 0, peak = 0, calls = 0, factories = 0;
        Func<long, CancellationToken, long, AdvCue> Factory()
        {
            Interlocked.Increment(ref factories);
            return (id, token, _) =>
            {
                decoded.AddOrUpdate(id, 1, (_, count) => count + 1);
                int current = Interlocked.Increment(ref active);
                lock (decoded) peak = Math.Max(peak, current);
                if (Interlocked.Increment(ref calls) <= 4) started.Signal();
                try
                {
                    release.Wait(token);
                    if (id <= 8) initial.Signal(); if (id == 88) skipped.Set();
                    return new(id, 2, [(float)id, .5f], 1);
                }
                finally { Interlocked.Decrement(ref active); }
            };
        }
        using (var prefetch = new StoryAudioPrefetch(new AdvCueCache(), Enumerable.Range(1, 100).Select(id => (long)id), Factory, CancellationToken.None))
        {
            check(started.Wait(TimeSpan.FromSeconds(10)) && peak == 4, "Audio prefetch runs at most four independent decoders");
            release.Set();
            check(initial.Wait(TimeSpan.FromSeconds(10)) && decoded.Keys.All(id => id <= 8), "Prefetch stops after eight lookahead cues without an actual native demand");
            var first = prefetch.Get(1); var again = prefetch.Get(1);
            check(ReferenceEquals(first, again) && decoded[1] == 1, "Native demands reuse the same complete cached PCM without duplicate decoding");
            _ = prefetch.Get(80);
            check(skipped.Wait(TimeSpan.FromSeconds(10)) && !decoded.Keys.Any(id => id is >= 10 and < 80),
                "A native forward branch advances the bounded lookahead without decoding the skipped region");
            check(peak <= 4 && factories == 5, "Demand decoding shares the four-worker limit and each worker parses its source snapshot once");
        }

        using var attempted = new CountdownEvent(3);
        using (var prefetch = new StoryAudioPrefetch(new AdvCueCache(), new long[] { 1, 2, 3 }, () => (id, _, limit) =>
        {
            if (limit != long.MaxValue) attempted.Signal();
            if (id == 2) throw new InvalidDataException("unreachable missing cue");
            if (id == 3 && limit != long.MaxValue) throw new AdvAudioPrefetchLimitException();
            return new(id, 2, Enumerable.Repeat(.25f, 64).ToArray(), 1);
        }, CancellationToken.None, concurrency: 3))
        {
            check(attempted.Wait(TimeSpan.FromSeconds(10)) && prefetch.Get(1).Samples == 64,
                "A missing cue in an unexecuted branch does not fail source analysis");
            bool failed = false; try { prefetch.Get(2); } catch (InvalidDataException) { failed = true; }
            check(failed, "The same missing cue still reports its original failure when a native handler requests it");
            check(prefetch.Get(3).Samples == 64, "An oversized speculative cue is retried in full on demand rather than being truncated or rejected");
        }
        using var blocked = new CountdownEvent(4); int pending = 0;
        var cancelled = new StoryAudioPrefetch(new AdvCueCache(), Enumerable.Range(1, 20).Select(id => (long)id), () => (id, token, _) =>
        {
            Interlocked.Increment(ref pending); blocked.Signal();
            try { token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested(); return new(id, 2, [.5f], 1); }
            finally { Interlocked.Decrement(ref pending); }
        }, CancellationToken.None);
        try { check(blocked.Wait(TimeSpan.FromSeconds(10)), "Cancellation fixture has four active decoders"); }
        finally { cancelled.Dispose(); }
        check(pending == 0, "Disposing analysis cancels and joins every active prefetch worker");
    }

    static async Task CancelledDecoder(string output, Action<bool, string> check, JsonObject report)
    {
        string folder = Path.Combine(output, "cancelled-decoder"), pidFile = Path.Combine(folder, "decoder.pid");
        Directory.CreateDirectory(Path.Combine(folder, "index")); File.WriteAllText(Path.Combine(folder, "index", "stats.json"), "{}");
        var archive = new Archive(folder); var manifest = new JsonObject { ["files"] = new JsonObject() };
        void Add(string logical, byte[] bytes)
        {
            string asset = "assets/" + Archive.Hash(bytes) + ".json";
            string path = Path.Combine(folder, "sites", "intl", asset); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes);
            manifest["files"]![logical] = new JsonObject { ["asset"] = asset, ["size"] = bytes.Length };
        }
        Add("audio/fixture/cues.json", Encoding.UTF8.GetBytes("""{"cue":{"file":"cue.wav","channels":1,"sampleRate":48000}}"""));
        Add("audio/fixture/cue.wav", [0, 1, 2, 3]);
        var episode = JsonNode.Parse("""{"sounds":{"1":{"_category":2,"_soundCueSheetID":"sheet","_cueName":"cue"}},"cuesheets":{"sheet":{"_cueSheetName":"fixture"}}}""")!;
        string executable = Environment.ProcessPath!;
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            executable = Path.Combine(AppContext.BaseDirectory, "OurNotes.Director.NativeTests" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        string? previous = Environment.GetEnvironmentVariable(WorkerEnvironment);
        using var cancellation = new CancellationTokenSource();
        Task<AdvCue>? task = null;
        try
        {
            if (File.Exists(pidFile)) File.Delete(pidFile);
            Environment.SetEnvironmentVariable(WorkerEnvironment, pidFile);
            task = Task.Run(() => AdvAudio.Decode(archive, "intl", manifest, episode, 1, executable, cancellation.Token));
            var limit = Stopwatch.StartNew();
            while (!File.Exists(pidFile) && !task.IsCompleted && limit.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
            check(File.Exists(pidFile), "The cancellation test starts an actual blocked decoder child process");
            var worker = JsonNode.Parse(await File.ReadAllTextAsync(pidFile))!;
            int pid = worker["pid"]!.GetValue<int>();
            var started = Stopwatch.StartNew(); cancellation.Cancel(); bool stopped = false;
            try { await task.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { stopped = true; }
            bool exited; try { using var child = Process.GetProcessById(pid); exited = child.HasExited; } catch (ArgumentException) { exited = true; }
            check(stopped && exited && !File.Exists(worker["input"]!.ToString()),
                "Cancelling a blocked decode reaps its child process and removes its private input before returning");
            var result = new JsonObject { ["cancelled"] = stopped, ["childExited"] = exited,
                ["inputRemoved"] = !File.Exists(worker["input"]!.ToString()), ["cancelMs"] = started.Elapsed.TotalMilliseconds };
            report["cancellation"] = result;
            File.WriteAllText(Path.Combine(folder, "result.json"), result.ToJsonString());
        }
        finally
        {
            try
            {
                cancellation.Cancel();
                if (task is { IsCompleted: false } && File.Exists(pidFile))
                {
                    int pid = JsonNode.Parse(await File.ReadAllTextAsync(pidFile))!["pid"]!.GetValue<int>();
                    try { using var child = Process.GetProcessById(pid); if (!child.HasExited) child.Kill(true); }
                    catch (ArgumentException) { }
                }
                if (task is not null) try { await task.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { }
            }
            finally { Environment.SetEnvironmentVariable(WorkerEnvironment, previous); }
        }
    }
}
