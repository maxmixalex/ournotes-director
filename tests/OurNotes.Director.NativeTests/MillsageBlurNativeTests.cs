using System.Security.Cryptography;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

/// <summary>Real 10060 shaders and the reported 01:56 source-story sample.
/// Tests the depth texture's coordinate contract, not just the Volume state.</summary>
static class MillsageBlurNativeTests
{
    public static async Task<int> RunAsync(string fixture, string output)
    {
        fixture = Path.GetFullPath(fixture); output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var checks = new JsonArray();
        var report = new JsonObject { ["ok"] = false, ["story"] = "10060", ["sampleFrame"] = 3480, ["checks"] = checks };
        void Check(bool value, string name)
        {
            checks.Add((JsonNode)new JsonObject { ["name"] = name, ["ok"] = value });
            if (!value) throw new InvalidOperationException(name);
            Console.WriteLine(name);
        }
        try
        {
            NativeTestGraphics.Configure();
            await Task.Run(() =>
            {
                var archive = new Archive(fixture); var manifest = archive.Manifest("intl", "stories", "10060");
                var definition = JsonNode.Parse(archive.Logical("intl", manifest, "story.json"))!;
                var scene = JsonNode.Parse(archive.Logical("intl", manifest, Archive.S(definition, "scene")))!;
                var profiles = JsonNode.Parse(archive.Logical("intl", manifest, Archive.S(definition, "postEffects")))!["postEffects"]!;
                var reminiscence = AdvVolume.Evaluate([new(profiles["adv_effect_posteffect_reminiscence2"]!)]);
                Check(Actor.N(reminiscence["DepthOfField"]!["mode"]) == 2
                    && Actor.N(reminiscence["DepthOfField"]!["focusDistance"]) == 17
                    && Actor.N(reminiscence["DepthOfField"]!["focalLength"]) == 135,
                    "10060 retains its original reminiscence Bokeh profile (17 m focus, 135 mm lens)");
                CheckDepthPixels(archive, manifest, scene, reminiscence,
                    AdvVolume.Evaluate([new(profiles["adv_effect_posteffect_blur"]!)]), output, Check, report);
                CheckStorySample(archive, output, Check, report);
            });
            report["ok"] = true;
            Console.WriteLine($"Passed {checks.Count} millsage native depth, cache and 01:56 checks."); return 0;
        }
        catch (Exception error) { report["exception"] = error.ToString(); throw; }
        finally { File.WriteAllText(Path.Combine(output, "millsage-blur-report.json"), report.ToJsonString(new() { WriteIndented = true })); }
    }

    static void CheckDepthPixels(Archive archive, JsonNode manifest, JsonNode sourceScene, JsonNode reminiscence,
        JsonNode intentionalBlur, string output, Action<bool, string> check, JsonObject report)
    {
        using var gl = new OffscreenGl(128, 64);
        using var shaders = new UnityShaderLibrary(archive, "intl", manifest);
        var post = new AdvPostFx(gl, shaders);
        var loop = new AdvLoop(); var camera = AdvCamera.FromScene(loop, new(loop), sourceScene);
        var focused = reminiscence.DeepClone(); focused["DepthOfField"]!["focusDistance"] = camera.Far;
        var noDof = focused.DeepClone(); noDof["DepthOfField"]!["mode"] = 0;
        var intentionalReference = intentionalBlur.DeepClone(); intentionalReference["DepthOfField"]!["mode"] = 0;
        var samples = new JsonArray(); var observed = new Dictionary<(int Width, int Height), Pixels>();
        int frame = 0;
        Pixels Pattern(JsonNode stack, int width, int height)
        {
            var rgba = new byte[width * height * 4];
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                byte value = (byte)(((x / 4 + y / 4) & 1) == 0 ? 0 : 255); int offset = (y * width + x) * 4;
                rgba[offset] = rgba[offset + 1] = rgba[offset + 2] = value; rgba[offset + 3] = 255;
            }
            // A full-screen binary pattern makes any unintended depth blur
            // visible across the image, including away from texel (0, 0).
            uint texture = gl.Upload(new(width, height, rgba));
            gl.Begin([0, 0, 0, 1]); gl.Composite(texture, 1);
            post.Render(stack, camera, frame++);
            return gl.Present(width, height, 1, 0, static (_, _, _) => { });
        }
        void Size(int width, int height, bool repeat)
        {
            using var canvas = width == 128 && height == 64 ? null : gl.CreateDrawingCanvas(width, height);
            using var binding = gl.UseDrawingCanvas(canvas);
            var reference = Pattern(noDof, width, height); var actual = Pattern(focused, width, height);
            int maximumError = MaxRgbError(reference, actual);
            check(maximumError <= 2,
                $"{width}x{height}: focusing the cleared far plane preserves every pattern edge (max RGB error {maximumError})");
            if (repeat)
                check(actual.Rgba.SequenceEqual(observed[(width, height)].Rgba),
                    $"{width}x{height}: returning from another presentation size restores identical pixels");
            else
            {
                observed[(width, height)] = actual;
                samples.Add((JsonNode)new JsonObject { ["width"] = width, ["height"] = height,
                    ["maxFocusedRgbError"] = maximumError, ["rgbaSha256"] = Hash(actual) });
            }
            if (width == 128 && !repeat)
            {
                var clear = Pattern(intentionalReference, width, height); var blurred = Pattern(intentionalBlur, width, height);
                double difference = MeanRgbError(clear, blurred);
                check(difference > 10, "The original explicit blur profile still produces substantial real Bokeh blur");
                report["intentionalBlurMeanRgbError"] = difference;
                File.WriteAllBytes(Path.Combine(output, "dof-far-plane-pattern.png"), Png.Encode(actual));
                File.WriteAllBytes(Path.Combine(output, "dof-intentional-blur-pattern.png"), Png.Encode(blurred));
            }
        }
        Size(128, 64, false); Size(64, 32, false); Size(32, 16, false);
        long bytes = post.PresentationTargetBytes; int count = post.PresentationTargetCount;
        Size(128, 64, true); Size(64, 32, true); Size(32, 16, true);
        check(post.PresentationTargetBytes == bytes && post.PresentationTargetCount == count,
            "Revisiting the three preview sizes reuses the bounded post-processing/depth cache");
        post.ReleasePresentationTargets();
        check(post.PresentationTargetBytes == 0 && post.PresentationTargetCount == 0,
            "Viewport resize releases all presentation and camera-depth targets");
        Size(128, 64, true);
        report["shaderSamples"] = samples; report["threeSizeTargetBytes"] = bytes; report["threeSizeTargetCount"] = count;
    }

    static void CheckStorySample(Archive archive, string output, Action<bool, string> check, JsonObject report)
    {
        string core = NativeDependencies.CoreLibrary(baseDirectory: NativeTestGraphics.PackageDirectory);
        string ffmpeg = NativeDependencies.MediaTool("ffmpeg", baseDirectory: NativeTestGraphics.PackageDirectory);
        var commands = new JsonArray();
        using var renderer = new AdvStoryRenderer(archive, "intl", core, "10060", "zh-Hans", 960, 540, null, ffmpeg, 1);
        renderer.Player.OnCommand = (row, frame) =>
        {
            if (row.Command is "PostEffect" or "DoF" or "Focus" or "Zoom" or "Stage")
                commands.Add((JsonNode)new JsonObject { ["index"] = row.Index, ["frame"] = frame, ["row"] = row.Data.DeepClone() });
        };
        // Advance the original native player once to the exact user-reported
        // timestamp. Intermediate frames are simulated, without full-frame export.
        var pixels = renderer.Frame(3480, true);
        check(renderer.Loop.Frame == 3480 && !renderer.Player.Finished,
            "The unmodified millsage 10060 source reaches the reported 01:56 frame");
        check(pixels.Rgba.Where((_, i) => i % 4 != 3).Distinct().Count() > 64,
            "The 01:56 native sample contains the rendered scene, not an empty frame");
        var effects = commands.Where(command => command?["row"]?["cmd"]?.ToString() == "PostEffect").ToArray();
        check(effects.Length == 1 && effects[0]!["index"]!.ToString() == "14"
            && effects[0]!["row"]!["TargetAssetName"]!.ToString() == "adv_effect_posteffect_reminiscence2",
            "The 01:56 scene has the original active reminiscence effect, with no stale or extra PostEffect command");
        check(renderer.Scene.StageActors is [{ Id: "hotaru_child", Blur: 0 }],
            "Hotaru's original character-slot blur is zero at 01:56");
        check(commands.Last(command => command?["row"]?["cmd"]?.ToString() == "Focus")!["index"]!.ToString() == "79"
            && commands.Last(command => command?["row"]?["cmd"]?.ToString() == "Zoom")!["index"]!.ToString() == "81",
            "The reported sample includes the original Focus/20-second Zoom sequence");
        File.WriteAllBytes(Path.Combine(output, "millsage-10060-116s.png"), Png.Encode(pixels));
        var actors = new JsonArray();
        foreach (var actor in renderer.Scene.StageActors)
            actors.Add((JsonNode)new JsonObject { ["id"] = actor.Id, ["slot"] = actor.Slot, ["blur"] = actor.Blur });
        report["sourceSample"] = new JsonObject { ["rgbaSha256"] = Hash(pixels), ["stage"] = renderer.Scene.StageName,
            ["actors"] = actors, ["commands"] = commands };
    }

    static int MaxRgbError(Pixels a, Pixels b)
    {
        int maximum = 0;
        for (int i = 0; i < a.Rgba.Length; i++) if (i % 4 != 3) maximum = Math.Max(maximum, Math.Abs(a.Rgba[i] - b.Rgba[i]));
        return maximum;
    }
    static double MeanRgbError(Pixels a, Pixels b)
    {
        long difference = 0;
        for (int i = 0; i < a.Rgba.Length; i++) if (i % 4 != 3) difference += Math.Abs(a.Rgba[i] - b.Rgba[i]);
        return difference / (double)(a.Width * a.Height * 3);
    }
    static string Hash(Pixels image) => Convert.ToHexString(SHA256.HashData(image.Rgba));
}
