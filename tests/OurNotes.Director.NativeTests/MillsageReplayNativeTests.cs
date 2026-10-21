using System.Diagnostics;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

/// <summary>The reported long reverse seek through the production preview session,
/// including its temporary drawing quality and exact full-quality restoration.</summary>
static class MillsageReplayNativeTests
{
    public static async Task<int> RunAsync(string fixture, string output)
    {
        fixture = Path.GetFullPath(fixture); output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var checks = new JsonArray(); var phases = new JsonArray();
        var report = new JsonObject { ["schema"] = "ournotes-director/millsage-replay/1", ["ok"] = false,
            ["wallClockIsDiagnosticOnly"] = true, ["checks"] = checks, ["phases"] = phases };
        void Check(bool value, string name)
        {
            checks.Add((JsonNode)new JsonObject { ["name"] = name, ["ok"] = value });
            if (!value) throw new InvalidOperationException(name);
            Console.WriteLine(name);
        }
        try
        {
            ParticleCurveCacheTests.Run(Check);
            report["particleCurveChecks"] = checks.Count;
            NativeTestGraphics.Configure();
            var document = new JsonObject { ["format"] = DirectorProject.Format, ["version"] = 1,
                ["source"] = new JsonObject { ["adv"] = "10060", ["site"] = "intl", ["language"] = "zh-Hans" },
                ["output"] = new JsonObject() };
            string path = Path.Combine(output, "original.project.json"), saved = document.ToJsonString();
            File.WriteAllText(path, saved);
            var project = DirectorProject.Read(path);
            report["source"] = new JsonObject { ["adv"] = project.Adv, ["site"] = project.Site,
                ["language"] = project.Language, ["archive"] = fixture, ["project"] = path,
                ["projectSha256"] = Archive.Hash(File.ReadAllBytes(path)) };
            Check(project.Adv == "10060" && project.Site == "intl" && project.Language == "zh-Hans",
                "The long replay loads the original millsage 10060 source without replacing its commands");

            string core = NativeDependencies.CoreLibrary(baseDirectory: NativeTestGraphics.PackageDirectory);
            string ffmpeg = NativeDependencies.MediaTool("ffmpeg", baseDirectory: NativeTestGraphics.PackageDirectory);
            await using var session = new RealtimePreviewSession();
            async Task<PreviewFrame> Measure(string name, Func<Task> operation)
            {
                Console.WriteLine("Millsage replay phase: " + name);
                long started = Stopwatch.GetTimestamp();
                await operation();
                double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var frame = session.LastFrame ?? throw new InvalidOperationException("The session did not publish a frame: " + name);
                phases.Add((JsonNode)Snapshot(name, frame, elapsed));
                Console.WriteLine($"{name}: frame {frame.Frame}, scale {frame.ScenePreviewScale}, {elapsed:F2} ms");
                return frame;
            }
            var loaded = await Measure("load", () => session.LoadSceneAsync(new(fixture, path, core, ffmpeg,
                Width: 960, Height: 540, Duration: 200, GpuPresentation: false, Supersample: 1)));
            Check(loaded.Frame == 0 && loaded.Initialization is not null,
                "The production session publishes the original first frame with construction diagnostics");
            var first = await Measure("seek116Full", () => session.SeekAsync(116));
            CheckFrame(first, 3480, 116, 1, Check);
            Check(first.ActorAnimations is [{ TargetId: "hotaru_child", Motion.Length: > 0, Expression.Length: > 0 }],
                "The 01:56 sample contains Hotaru's actual native motion and expression");
            Check(first.Pixels.Rgba.Where((_, index) => index % 4 != 3).Distinct().Count() > 64,
                "The full-quality reference contains a rendered scene rather than an empty image");
            File.WriteAllBytes(Path.Combine(output, "millsage-116s-before.png"), Png.Encode(first.Pixels));

            await session.SetScenePreviewScaleAsync(.25);
            var backward = await Measure("seek115_9Quarter", () => session.SeekAsync(115.9));
            CheckFrame(backward, 3477, 115.9, .25, Check);
            Check(backward.SessionId != first.SessionId && backward.Performance?.SimulationFrames == 3477,
                "Reverse seek reconstructs and simulates all 3477 original frames while publishing quarter-quality preview");
            Check(backward.Initialization is { TextureDecodes: 0, TextureCacheHits: > 0, ModelResources: { } resources }
                && resources["assetDecodes"]!.GetValue<long>() == 0 && resources["clipCompilations"]!.GetValue<long>() == 0
                && resources["assetHits"]!.GetValue<long>() > 0 && resources["clipHits"]!.GetValue<long>() > 0,
                "The long reconstruction reuses verified model sources, immutable motion curves and decoded textures");
            File.WriteAllBytes(Path.Combine(output, "millsage-115_9s-quarter.png"), Png.Encode(backward.Pixels));

            await session.SetScenePreviewScaleAsync(1);
            var full = await Measure("restore115_9Full", () => session.SeekAsync(115.9));
            CheckFrame(full, 3477, 115.9, 1, Check);
            Check(full.Performance?.SimulationFrames == 0 && Animations(full).ToJsonString() == Animations(backward).ToJsonString(),
                "Restoring full drawing quality at the same timestamp does not advance the native animation");
            var restored = await Measure("replay116Full", () => session.SeekAsync(116));
            CheckFrame(restored, 3480, 116, 1, Check);
            bool pixelsExact = first.Pixels.Rgba.SequenceEqual(restored.Pixels.Rgba);
            bool animationExact = Animations(first).ToJsonString() == Animations(restored).ToJsonString();
            report["exactFullQualityPixels"] = pixelsExact; report["exactNativeAnimation"] = animationExact;
            Check(pixelsExact, "Full-quality 01:56 pixels are byte-for-byte identical after the long quarter-quality reverse seek");
            Check(animationExact && restored.Performance?.SimulationFrames == 3,
                "The final three original simulation steps restore identical native animation requests and exact motion-time bits");
            Check(!session.IsPlaying && File.ReadAllText(path) == saved,
                "The roundtrip remains paused and leaves the original project document unchanged");
            File.WriteAllBytes(Path.Combine(output, "millsage-116s-restored.png"), Png.Encode(restored.Pixels));
            report["ok"] = true;
            Console.WriteLine($"Passed {checks.Count} particle-curve and millsage long-replay checks.");
            return 0;
        }
        catch (Exception error) { report["exception"] = error.ToString(); throw; }
        finally { File.WriteAllText(Path.Combine(output, "millsage-replay-report.json"), report.ToJsonString(new() { WriteIndented = true })); }
    }

    static void CheckFrame(PreviewFrame frame, int expectedFrame, double expectedTime, double scale, Action<bool, string> check)
        => check(frame.Frame == expectedFrame && frame.Time == expectedTime && frame.ScenePreviewScale == scale && !frame.IsPlaying
            && !frame.IsGpuPresented && frame.Pixels.Width == 960 && frame.Pixels.Height == 540 && frame.Pixels.Rgba.Length == 960 * 540 * 4
            && frame.Performance is not null && frame.Initialization is not null,
            $"Frame {expectedFrame} publishes the actual {expectedTime}s paused CPU image at scale {scale} with complete diagnostics");

    static JsonArray Animations(PreviewFrame frame) => new((frame.ActorAnimations ?? []).OrderBy(value => value.TargetId, StringComparer.Ordinal)
        .Select(value => (JsonNode)new JsonObject { ["actor"] = value.TargetId, ["model"] = value.Model,
            ["motion"] = value.Motion, ["expression"] = value.Expression, ["motionTime"] = value.MotionTime,
            ["motionTimeBits"] = BitConverter.DoubleToInt64Bits(value.MotionTime) }).ToArray());

    static JsonObject Snapshot(string name, PreviewFrame frame, double elapsed) => new()
    {
        ["name"] = name, ["sequence"] = frame.Sequence, ["session"] = frame.SessionId,
        ["frame"] = frame.Frame, ["seconds"] = frame.Time, ["scale"] = frame.ScenePreviewScale,
        ["width"] = frame.Pixels.Width, ["height"] = frame.Pixels.Height, ["cpuPixelBytes"] = frame.CpuPixelTransferBytes,
        ["gpuPresented"] = frame.IsGpuPresented, ["playing"] = frame.IsPlaying, ["device"] = frame.Device,
        ["elapsedMs"] = elapsed, ["renderMs"] = frame.RenderMilliseconds, ["rgbaSha256"] = Archive.Hash(frame.Pixels.Rgba),
        ["animations"] = Animations(frame), ["initialization"] = frame.Initialization?.ToJson(),
        ["performance"] = frame.Performance is not { } performance ? null : new JsonObject
        {
            ["simulationMs"] = performance.SimulationMilliseconds, ["sceneMs"] = performance.SceneMilliseconds,
            ["readbackMs"] = performance.ReadbackMilliseconds, ["targetMs"] = performance.TargetMilliseconds,
            ["allocatedBytes"] = performance.AllocatedBytes, ["simulationFrames"] = performance.SimulationFrames,
            ["diagnostics"] = performance.Diagnostics is not { } diagnostic ? null : new JsonObject
            {
                ["width"] = diagnostic.Width, ["height"] = diagnostic.Height, ["supersample"] = diagnostic.Supersample,
                ["gen0Collections"] = diagnostic.Gen0Collections, ["gen1Collections"] = diagnostic.Gen1Collections,
                ["gen2Collections"] = diagnostic.Gen2Collections, ["gcPauseMs"] = diagnostic.GcPauseMilliseconds,
                ["processAllocatedBytes"] = diagnostic.ProcessAllocatedBytes, ["compiledPrograms"] = diagnostic.CompiledPrograms,
                ["shaderCompilationMs"] = diagnostic.ShaderCompilationMilliseconds, ["meshUploads"] = diagnostic.MeshUploads,
                ["meshUploadBytes"] = diagnostic.MeshUploadBytes, ["poseRefreshes"] = diagnostic.PoseRefreshes,
                ["maskPasses"] = diagnostic.MaskPasses, ["modelDraws"] = diagnostic.ModelDraws,
                ["presentationCopies"] = diagnostic.PresentationCopies
            }
        }
    };
}
