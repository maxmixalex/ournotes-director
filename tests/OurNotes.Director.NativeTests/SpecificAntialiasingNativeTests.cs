using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

/// <summary>The user's actual garage railing and Hotaru stage outfit, including
/// faint alpha after a real CineForm encode/decode. No synthetic model substitutes.</summary>
static class SpecificAntialiasingNativeTests
{
    const string Model = "adv_live2d_hotaru_016_live_01", ActorId = "workbench";
    readonly record struct Region(string Name, int X, int Y, int Width, int Height)
    {
        public JsonObject ToJson() => new() { ["name"] = Name, ["x"] = X, ["y"] = Y, ["width"] = Width, ["height"] = Height };
    }

    public static int Run(string directory, string homeFixture, string hotaruFixture, string ffmpeg)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        var checks = new JsonArray();
        var report = new JsonObject { ["ok"] = false, ["checks"] = checks,
            ["metric"] = "RGBA8 premultiplied colour and alpha mean squared error on matching output pixels",
            ["hotaruReference"] = "8x effective sampling: native 4x at twice the output dimensions, followed by premultiplied 2x area resolve; the intermediate PNG pixel grid is RGBA8" };
        void Check(bool value, string name)
        {
            checks.Add((JsonNode)new JsonObject { ["name"] = name, ["ok"] = value });
            if (!value) throw new InvalidOperationException(name);
            Console.WriteLine(name);
        }
        try
        {
            NativeTestGraphics.Configure();
            report["ffmpegVersion"] = Media.Run(ffmpeg, ["-version"]).Split('\n')[0].TrimEnd();
            RunHome(homeFixture, directory, report, Check);
            RunHotaru(hotaruFixture, ffmpeg, directory, report, Check);
            report["ok"] = true;
            Console.WriteLine($"Passed {checks.Count} exact-fixture antialiasing and decoded CineForm checks.");
            return 0;
        }
        catch (Exception error) { report["exception"] = error.ToString(); throw; }
        finally { File.WriteAllText(Path.Combine(directory, "specific-antialiasing-report.json"), report.ToJsonString(new() { WriteIndented = true })); }
    }

    static void RunHome(string fixture, string directory, JsonObject report, Action<bool, string> check)
    {
        var scene = new HomeCatalog(new Archive(fixture)).Open(40001, "intl", 10844);
        check(scene.SpotId == 40001 && scene.AdvId == 10844, "Home quality uses Millsage 一如既往的光景, spot 40001 and its original area source 10844");
        string manifest = Path.Combine(fixture, "sites", "intl", "stories", "10844.json");
        string sourceHash = Hash(File.ReadAllBytes(manifest));
        var home = new JsonObject { ["source"] = scene.ToJson(), ["sourceManifestSha256"] = sourceHash };
        report["home"] = home;
        var animation = new HomeAnimation(new HomeResources(scene));
        using var renderer = new HomeRenderer(animation, 960, 540, 1, HomeTiming.Plan(animation.Duration, fps: 30));
        home["device"] = renderer.Device;
        var samples = new Dictionary<int, Pixels>(); var hashes = new JsonArray(); home["images"] = hashes;
        Region railing = new("right-railing", 700, 200, 260, 340);
        foreach (int sampling in new[] { 1, 2, 4 })
        {
            renderer.ResizePreview(960, 540, sampling);
            var pixels = renderer.Frame(0); samples[sampling] = pixels;
            Save(directory, $"millsage-home-{sampling}x", pixels);
            Save(directory, $"millsage-home-railing-{sampling}x", Crop(pixels, railing));
            hashes.Add((JsonNode)new JsonObject { ["supersample"] = sampling, ["sha256"] = Hash(pixels.Rgba) });
        }
        double single = Error(samples[1], samples[4], railing), sampled = Error(samples[2], samples[4], railing);
        home["rightRailing"] = railing.ToJson(); home["rightRailing"]!["singleTo4xMse"] = single;
        home["rightRailing"]!["doubleTo4xMse"] = sampled;
        check(single > 1 && sampled < single * .5,
            "The actual right-side garage railing improves by more than half against the 4x reference when fullscreen uses 2x");
        var whole = new Region("full", 0, 0, 960, 540);
        home["wholeSingleTo4xMse"] = Error(samples[1], samples[4], whole);
        home["wholeDoubleTo4xMse"] = Error(samples[2], samples[4], whole);
        check(Error(samples[2], samples[4], whole) < Error(samples[1], samples[4], whole),
            "The complete original home frame also improves, rather than only a selected railing crop");
        renderer.ResizePreview(960, 540, 1);
        check(renderer.Frame(0).Rgba.SequenceEqual(samples[1].Rgba) && Hash(File.ReadAllBytes(manifest)) == sourceHash,
            "Leaving the antialiased home presentation restores the exact same source frame without modifying its manifest");
    }

    static void RunHotaru(string fixture, string ffmpeg, string directory, JsonObject report, Action<bool, string> check)
    {
        const int width = 640, height = 960, frame = 30;
        string manifest = Path.Combine(fixture, "sites", "intl", "models", Model + ".json");
        string sourceHash = Hash(File.ReadAllBytes(manifest));
        var hotaru = new JsonObject { ["model"] = Model, ["sourceManifestSha256"] = sourceHash,
            ["frame"] = frame, ["width"] = width, ["height"] = height };
        report["hotaru"] = hotaru;
        var project = new JsonObject
        {
            ["format"] = DirectorProject.Format, ["version"] = 1,
            ["source"] = new JsonObject { ["adv"] = "10462", ["site"] = "intl", ["language"] = "zh-Hans" },
            ["output"] = new JsonObject { ["width"] = width, ["height"] = height, ["clean"] = true,
                ["letterbox"] = "none", ["isolateActor"] = ActorId, ["supersample"] = 4,
                ["isolateTransform"] = ActorIsolation.TransformJson(new(.5, .5, .92, 0, 1)),
                ["codec"] = "cineform", ["cineformQuality"] = "film3+" },
            ["initialize"] = new JsonArray(), ["finalize"] = new JsonArray(), ["text"] = new JsonObject(),
            ["commands"] = new JsonArray(new JsonObject { ["cmd"] = "Character", ["TargetName"] = ActorId, ["Model"] = Model, ["AtFrame"] = 0 },
                new JsonObject { ["cmd"] = "In", ["TargetName"] = ActorId, ["PositionType"] = 3, ["Duration"] = 0, ["AtFrame"] = 0 },
                new JsonObject { ["cmd"] = "Delay", ["Frames"] = 0, ["AtFrame"] = 300 })
        };
        string path = Path.Combine(directory, "hotaru-stage-outfit.project.json"); File.WriteAllText(path, project.ToJsonString());
        string core = NativeDependencies.CoreLibrary(baseDirectory: NativeTestGraphics.PackageDirectory);
        using var renderer = new AdvStoryRenderer(new Archive(fixture), "intl", core, "10462", "zh-Hans", width, height, path, ffmpeg, 1);
        renderer.SetIsolatedTransform(new(.5, .5, .92, 0, 1)); hotaru["device"] = renderer.Device;
        var samples = new Dictionary<int, Pixels>(); var hashes = new JsonArray(); hotaru["images"] = hashes;
        foreach (int sampling in new[] { 1, 2, 4 })
        {
            renderer.ResizePreview(width, height, sampling); var pixels = renderer.Frame(frame, true); samples[sampling] = pixels;
            Save(directory, $"hotaru-stage-{sampling}x", pixels);
            hashes.Add((JsonNode)new JsonObject { ["supersample"] = sampling, ["sha256"] = Hash(pixels.Rgba) });
        }
        var actor = renderer.Scene.Character(ActorId)!; var nativeModel = actor.Core;
        var parameters = actor.PreviewParameters(); var animation = actor.PreviewAnimation(); var work = actor.DrawingWork;
        renderer.ResizePreview(width * 2, height * 2, 4);
        var reference = Reduce2(renderer.Frame(frame, true)); Save(directory, "hotaru-stage-8x-reference", reference);
        hotaru["referenceSha256"] = Hash(reference.Rgba);
        renderer.ResizePreview(width, height, 1);
        check(renderer.Frame(frame, true).Rgba.SequenceEqual(samples[1].Rgba) && renderer.Loop.Frame == frame
            && ReferenceEquals(nativeModel, renderer.Scene.Character(ActorId)!.Core)
            && actor.PreviewParameters().SequenceEqual(parameters) && actor.PreviewAnimation() == animation
            && actor.DrawingWork.MaskPasses == work.MaskPasses,
            "Hotaru 1x/2x/4x and larger reference share the exact native model, pose and original mask atlas; restoring 1x restores its bytes");
        check(Hash(File.ReadAllBytes(manifest)) == sourceHash, "Hotaru quality evaluation preserves the original model manifest");
        Region[] regions = [new("full", 0, 0, width, height), new("head", 250, 175, 160, 185),
            new("fine-hair", 235, 300, 45, 155), new("skirt", 150, 535, 160, 315), new("outer-skirt", 150, 590, 100, 250)];
        var errors = new JsonArray(); hotaru["regions"] = errors;
        foreach (var region in regions)
        {
            double single = Error(samples[1], reference, region), twice = Error(samples[2], reference, region), four = Error(samples[4], reference, region);
            var result = region.ToJson(); result["singleToReferenceMse"] = single; result["doubleToReferenceMse"] = twice; result["fourToReferenceMse"] = four;
            errors.Add((JsonNode)result);
            check(single > 0 && twice < single && four < twice * .5,
                $"Hotaru {region.Name}: 4x preserves finer coverage than 2x against the independently finer native pixel grid");
            if (region.Name == "full") continue;
            foreach (int sampling in new[] { 1, 2, 4 }) Save(directory, $"hotaru-{region.Name}-{sampling}x", Crop(samples[sampling], region));
            Save(directory, $"hotaru-{region.Name}-8x-reference", Crop(reference, region));
        }
        renderer.ResizePreview(width, height, 4); renderer.QueueFrame(frame, true);
        check(renderer.ReadQueuedFrame().Rgba.SequenceEqual(samples[4].Rgba) && renderer.PendingFrames == 0,
            "The actual Hotaru 4x video export queue submits the same fully resolved RGBA as its PNG, including fine alpha coverage");
        RunCineForm(samples[4], regions, ffmpeg, directory, hotaru, check);
    }

    static void RunCineForm(Pixels source, Region[] regions, string ffmpeg, string directory, JsonObject report, Action<bool, string> check)
    {
        var results = new JsonArray(); report["cineform"] = results;
        double? balancedError = null; int balancedWeakRetained = 0, balancedAlphaError = 0;
        foreach (string quality in new[] { "film1", "film3+" })
        {
            string name = "hotaru-stage-4x-" + quality, movie = Path.Combine(directory, name + ".mov");
            using (var encoder = Process.Start(VideoEncoding.RawVideoStartInfo(ffmpeg, "cineform", source.Width, source.Height, 30, movie, quality))
                ?? throw new IOException("Cannot start the CineForm encoder."))
            {
                var stderr = encoder.StandardError.ReadToEndAsync();
                for (int y = 0; y < source.Height; y++) encoder.StandardInput.BaseStream.Write(source.GetRgbaRow(y));
                encoder.StandardInput.Close(); encoder.WaitForExit(); string details = stderr.GetAwaiter().GetResult();
                if (encoder.ExitCode != 0) throw VideoEncoding.EncoderFailure(encoder.ExitCode, details);
            }
            var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "-v", "error", "-nostdin", "-i", movie, "-map", "0:v:0", "-pix_fmt", "rgba", "-f", "rawvideo", "pipe:1" }) start.ArgumentList.Add(arg);
            byte[] bytes;
            using (var decoder = Process.Start(start) ?? throw new IOException("Cannot start the CineForm decoder."))
            {
                var stderr = decoder.StandardError.ReadToEndAsync(); using var output = new MemoryStream();
                decoder.StandardOutput.BaseStream.CopyTo(output); decoder.WaitForExit(); string details = stderr.GetAwaiter().GetResult();
                if (decoder.ExitCode != 0) throw new IOException(details); bytes = output.ToArray();
            }
            check(bytes.Length == source.Width * source.Height * 4, $"Hotaru {quality}: decoded movie has one complete frame at the unchanged output size");
            var decoded = new Pixels(source.Width, source.Height, bytes); Save(directory, name + "-decoded", decoded);
            int maximumAlphaError = 0, weak = 0, weakRetained = 0, fractional = 0, fractionalRetained = 0, emptyChanged = 0;
            for (int i = 3; i < source.Rgba.Length; i += 4)
            {
                int a = source.Rgba[i], b = bytes[i]; maximumAlphaError = Math.Max(maximumAlphaError, Math.Abs(a - b));
                if (a == 0 && b != 0) emptyChanged++;
                if (a is >= 1 and <= 15) { weak++; if (b is > 0 and < 255) weakRetained++; }
                if (a is >= 16 and <= 239) { fractional++; if (b is > 0 and < 255) fractionalRetained++; }
            }
            double error = Error(source, decoded, regions[0]);
            var result = new JsonObject { ["quality"] = quality, ["sourceSha256"] = Hash(source.Rgba), ["decodedSha256"] = Hash(bytes),
                ["movieBytes"] = new FileInfo(movie).Length, ["codecMse"] = error, ["maximumAlphaError"] = maximumAlphaError,
                ["weakAlphaPixels1to15"] = weak, ["retainedWeakAlphaPixels"] = weakRetained,
                ["fractionalAlphaPixels16to239"] = fractional, ["retainedFractionalAlphaPixels"] = fractionalRetained,
                ["emptyAlphaPixelsChanged"] = emptyChanged };
            var regionErrors = new JsonArray(); result["regions"] = regionErrors; results.Add((JsonNode)result);
            foreach (var region in regions)
            {
                var item = region.ToJson(); item["codecMse"] = Error(source, decoded, region); regionErrors.Add((JsonNode)item);
                if (region.Name != "full") Save(directory, $"hotaru-{region.Name}-4x-{quality}-decoded", Crop(decoded, region));
            }
            check(emptyChanged == 0 && maximumAlphaError <= 8 && fractional > 100 && fractionalRetained >= fractional * .98,
                $"Hotaru {quality}: pure transparency and antialiased hair/costume coverage survive the actual encoded movie");
            if (quality == "film1") { balancedError = error; balancedWeakRetained = weakRetained; balancedAlphaError = maximumAlphaError; }
            else check(balancedError is { } balanced && error < balanced && weak > 100 && weakRetained >= balancedWeakRetained
                    && weakRetained > weak * .9 && maximumAlphaError <= balancedAlphaError,
                "Hotaru Film 3+ reduces actual codec error and retains more faint 1–15 alpha edge pixels than Film 1");
        }
    }

    static Pixels Reduce2(Pixels source)
    {
        if (source.Width % 2 != 0 || source.Height % 2 != 0) throw new ArgumentException("An even reference grid is required.");
        int width = source.Width / 2, height = source.Height / 2; var result = new byte[checked(width * height * 4)];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int r = 0, g = 0, b = 0, a = 0;
            for (int dy = 0; dy < 2; dy++) for (int dx = 0; dx < 2; dx++)
            {
                int p = ((y * 2 + dy) * source.Width + x * 2 + dx) * 4, alpha = source.Rgba[p + 3];
                a += alpha; r += source.Rgba[p] * alpha; g += source.Rgba[p + 1] * alpha; b += source.Rgba[p + 2] * alpha;
            }
            int d = (y * width + x) * 4;
            if (a > 0) { result[d] = (byte)((r + a / 2) / a); result[d + 1] = (byte)((g + a / 2) / a); result[d + 2] = (byte)((b + a / 2) / a); }
            result[d + 3] = (byte)((a + 2) / 4);
        }
        return new(width, height, result);
    }
    static double Error(Pixels source, Pixels reference, Region region)
    {
        if (source.Width != reference.Width || source.Height != reference.Height) throw new ArgumentException("Matching pixel grids are required.");
        double sum = 0; byte[] original = source.Rgba, expected = reference.Rgba;
        for (int y = region.Y; y < region.Y + region.Height; y++) for (int x = region.X; x < region.X + region.Width; x++)
        {
            int p = (y * source.Width + x) * 4; double a = original[p + 3] / 255d, b = expected[p + 3] / 255d;
            for (int channel = 0; channel < 3; channel++) { double difference = original[p + channel] * a - expected[p + channel] * b; sum += difference * difference; }
            double alpha = original[p + 3] - expected[p + 3]; sum += alpha * alpha;
        }
        return sum / (region.Width * region.Height * 4d);
    }
    static Pixels Crop(Pixels source, Region region)
    {
        var result = new byte[checked(region.Width * region.Height * 4)];
        for (int y = 0; y < region.Height; y++)
            Buffer.BlockCopy(source.Rgba, ((region.Y + y) * source.Width + region.X) * 4, result, y * region.Width * 4, region.Width * 4);
        return new(region.Width, region.Height, result);
    }
    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    static void Save(string directory, string name, Pixels pixels) => File.WriteAllBytes(Path.Combine(directory, name + ".png"), Png.Encode(pixels));
}
