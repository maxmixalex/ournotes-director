using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

/// <summary>Opt-in public-artwork regression for the actual reported home, downloaded separately from source.</summary>
internal static class StudioHomeRealTests
{
    public static int Run(string archivePath, string outputDirectory)
    {
        archivePath = Path.GetFullPath(archivePath); outputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var checks = new JsonArray(); var report = new JsonObject
        {
            ["schema"] = "ournotes-director/studio-home-regression/1", ["ok"] = false,
            ["site"] = "intl", ["spot"] = 40002, ["adv"] = 10855, ["checks"] = checks,
        };
        void Check(bool condition, string message)
        {
            checks.Add((JsonNode)new JsonObject { ["name"] = message, ["ok"] = condition });
            if (!condition) throw new InvalidOperationException("Real home: " + message);
        }
        var wall = Stopwatch.StartNew();
        try
        {
            NativeTestGraphics.Configure();
            var archive = new Archive(archivePath); var catalog = new HomeCatalog(archive); var scene = catalog.Open(40002, "intl", 10855);
            string manifestHash = Archive.Hash(File.ReadAllBytes(archive.SitePath("intl", scene.ManifestPath)));
            byte[] originalSpot = scene.Read(scene.SpotPath);
            var sourcePart = scene.Spot["spineCharacters"]!.AsArray().Single(p => Archive.S(p!, "path").EndsWith("/Hotaru_back", StringComparison.Ordinal))!;
            Check(sourcePart["skeletonData"] is null && sourcePart["world"] is null && sourcePart["animation"] is null,
                "the reported source still has its original missing Hotaru_back component, without fabricated archive metadata");
            Check(scene.Characters.Count == 5 && scene.SpineParts.Count == 12 && scene.SelectCharacter("hotaru").Parts.Count == 2,
                "the complete 40002 scene includes all five characters and both Hotaru layers");
            var recovery = scene.SpineRecoveries.Single();
            Check(recovery.SharedBones == 55 && recovery.Samples == 241 && recovery.MaximumBoneErrorPixels <= .05,
                "all 55 shared Hotaru bones match through the actual four-second, 241-frame animation");
            Check(!recovery.ToJson()["sourceMatrixAvailable"]!.GetValue<bool>() && !recovery.ToJson()["originalSceneProjectionExact"]!.GetValue<bool>(),
                "the diagnostic identifies bone-aligned reconstruction rather than claiming exact missing prefab placement");
            report["source"] = scene.ToJson(); report["manifestSha256"] = manifestHash;
            var tap = catalog.Open(40002, "intl", 10853);
            Check(tap.SelectCharacter("hotaru").Parts.Count == 2 && tap.SpineRecoveries.Count == 1,
                "the Hotaru tap story resolves the same complete character as the area's home source");

            var resources = new HomeResources(scene); var hotaru = scene.SelectCharacter("hotaru");
            var animation = new HomeAnimation(resources, hotaru); var timing = HomeTiming.Plan(animation.Duration, fps: 60);
            Check(animation.Duration == 4 && !animation.AllLooping && timing.FrameCount == 241 && timing.LastFrame == 240,
                "natural duration includes the actual final source pose, with no arbitrary ten-second default");
            var native = animation.MeasureNativeCanvas(fps: 60, sourceAspect: 16f / 9);
            Check(native.Width >= 448 && native.Height >= 1056 && native.Width % 16 == 0 && native.Height % 8 == 0 && native.Bounds.Parts.Count == 2,
                "native artwork dimensions include the whole skirt, legs and shoes instead of an upper-body or 16:9 crop");
            report["nativeCanvas"] = native.ToJson();
            File.WriteAllBytes(Path.Combine(outputDirectory, "native-canvas.json"), JsonOutput.SerializeToUtf8Bytes(native.ToJson()));
            animation.Advance(120, 60);
            var held = animation.Parts.SelectMany(p => p.Actor.Mesh.Batches).SelectMany(b => b.Positions).ToArray();
            animation.MeasureBounds(16f / 9, 0, 241, 60);
            Check(held.SequenceEqual(animation.Parts.SelectMany(p => p.Actor.Mesh.Batches).SelectMany(b => b.Positions)),
                "inspecting native bounds never changes the currently displayed real animation pose");

            const int width = 320, height = 560;
            var contact = new Pixels(width * 3, height * 3, new byte[width * height * 9 * 4]);
            Pixels? first = null, middle = null, last = null; string device;
            float[] crop;
            using (var renderer = new HomeRenderer(animation, width, height, 1, timing, "fit"))
            {
                bool inside = true, fractionalAlpha = false;
                for (int frame = 0; frame < timing.FrameCount; frame++)
                {
                    var pixels = renderer.Frame(frame); inside &= IsInside(pixels, 2);
                    fractionalAlpha |= pixels.Rgba.Where((_, i) => i % 4 == 3).Any(a => a is > 0 and < 255);
                    if (frame % 30 == 0) Cell(contact, pixels, frame / 30);
                    if (frame == 0) first = pixels;
                    if (frame == 120) middle = pixels;
                    if (frame == 240) last = pixels;
                }
                Check(inside && fractionalAlpha && renderer.Transparent && renderer.RoomMeshCount == 0,
                    "every real output frame stays inside a fixed full-character crop and preserves source edge/shadow alpha");
                Check(!first!.Rgba.SequenceEqual(middle!.Rgba) && renderer.Frame(120).Rgba.SequenceEqual(middle.Rgba),
                    "actual face/hand motion changes over time and backward seeking reproduces the same frame");
                renderer.QueueFrame(0); renderer.QueueFrame(120); renderer.QueueFrame(240);
                Check(renderer.ReadQueuedFrame().Rgba.SequenceEqual(first.Rgba) && renderer.ReadQueuedFrame().Rgba.SequenceEqual(middle.Rgba)
                    && renderer.ReadQueuedFrame().Rgba.SequenceEqual(last!.Rgba), "queued readback preserves real first/middle/end frame order");
                crop = renderer.Crop; device = renderer.Device;
            }
            Save(outputDirectory, "hotaru-contact.png", contact); Save(outputDirectory, "hotaru-complete.png", middle!);

            // Reproduce the previous renderer's missing component with the same crop and actual
            // front actor. No original files or declarations are edited for this comparison.
            Pixels frontPixels, backPixels;
            using (var gl = new OffscreenGl(width, height))
            using (var drawing = new SpineDrawing(gl))
            {
                animation.Advance(120, 60);
                Pixels Layer(string suffix)
                {
                    var part = animation.Parts.Single(p => p.Path.EndsWith(suffix, StringComparison.Ordinal));
                    gl.Begin([0, 0, 0, 0]);
                    drawing.Draw(part.Actor, AdvMatrix.Multiply(AdvMatrix.Multiply(crop, animation.Camera.ViewProjection(width / (float)height)), part.World),
                        true, part.DepthWrite, part.StraightAlphaInput);
                    return gl.Present(width, height, 1, 0, static (_, _, _) => { }, true);
                }
                frontPixels = Layer("/Hotaru"); backPixels = Layer("/Hotaru_back");
            }
            int rearVisible = 0, overlap = 0, frontErrors = 0, rearErrors = 0;
            for (int i = 0; i < middle!.Rgba.Length; i += 4)
            {
                if (frontPixels.Rgba[i + 3] == 255 && backPixels.Rgba[i + 3] > 64)
                { overlap++; if (Enumerable.Range(0, 4).Any(c => Math.Abs(middle.Rgba[i + c] - frontPixels.Rgba[i + c]) > 2)) frontErrors++; }
                if (frontPixels.Rgba[i + 3] == 0 && backPixels.Rgba[i + 3] > 64)
                { rearVisible++; if (Enumerable.Range(0, 4).Any(c => Math.Abs(middle.Rgba[i + c] - backPixels.Rgba[i + c]) > 2)) rearErrors++; }
            }
            Check(rearVisible > 3000 && overlap > 100 && frontErrors == 0 && rearErrors == 0,
                "real recovered legs are visible while opaque front attachments correctly cover the overlapping back artwork");
            report["layerValidation"] = new JsonObject { ["newVisibleRearPixels"] = rearVisible, ["overlapPixels"] = overlap,
                ["frontOrderErrors"] = frontErrors, ["rearOrderErrors"] = rearErrors };
            Save(outputDirectory, "hotaru-before-front-only.png", frontPixels); Save(outputDirectory, "hotaru-back-layer.png", backPixels);
            using (var renderer = new HomeRenderer(new(resources), 960, 540, 1, timing))
            {
                var pixels = renderer.Frame(120); Save(outputDirectory, "home-complete-scene.png", pixels);
                Check(renderer.RoomMeshCount > 0 && !renderer.Transparent && pixels.Rgba.Where((_, i) => i % 4 == 3).All(a => a == 255),
                    "the actual complete room and restored character render together as an opaque home scene");
            }

            string ffmpeg = NativeDependencies.MediaTool("ffmpeg", baseDirectory: NativeTestGraphics.PackageDirectory);
            string ffprobe = NativeDependencies.MediaTool("ffprobe", baseDirectory: NativeTestGraphics.PackageDirectory);
            string movie = Path.Combine(outputDirectory, "hotaru-native.mov");
            var movieTiming = HomeTiming.Plan(animation.Duration, fps: 30);
            report["export"] = HomeExport.Run(new(resources, hotaru), "clip", movie, movieTiming, native.Width, native.Height, 1, "fit", false,
                ffmpeg: ffmpeg, overwrite: true);
            var stream = JsonNode.Parse(Media.Run(ffprobe, ["-v", "error", "-count_frames", "-show_streams", "-of", "json", movie]))!["streams"]!.AsArray().Single()!;
            Check(stream["codec_name"]!.ToString() == "cfhd" && stream["pix_fmt"]!.ToString() == "gbrap12le"
                && int.Parse(stream["width"]!.ToString(), CultureInfo.InvariantCulture) == native.Width && int.Parse(stream["height"]!.ToString(), CultureInfo.InvariantCulture) == native.Height
                && int.Parse(stream["nb_read_frames"]!.ToString(), CultureInfo.InvariantCulture) == movieTiming.FrameCount,
                "full-duration native-size CineForm MOV has the real alpha pixel format and every first-through-final frame");
            string decoded = Path.Combine(outputDirectory, "hotaru-mov-last.png");
            Media.Run(ffmpeg, ["-v", "error", "-nostdin", "-y", "-i", movie, "-vf", "select=eq(n\\," + movieTiming.LastFrame + ")", "-frames:v", "1", "-pix_fmt", "rgba", "-update", "1", decoded]);
            var lastDecoded = Png.Decode(File.ReadAllBytes(decoded));
            Check(IsInside(lastDecoded, 2) && lastDecoded.Rgba.Where((_, i) => i % 4 == 3).Any(a => a == 0)
                && lastDecoded.Rgba.Where((_, i) => i % 4 == 3).Any(a => a == 255), "decoded CineForm final frame retains full-body geometry and transparent background");
            Check(Archive.Hash(File.ReadAllBytes(archive.SitePath("intl", scene.ManifestPath))) == manifestHash
                && originalSpot.SequenceEqual(scene.Read(scene.SpotPath)), "all inspection and rendering leave source manifests and original spot bytes unchanged");
            report["device"] = device; report["ok"] = true;
            Console.WriteLine($"Passed {checks.Count} real 40002 home checks; native Hotaru {native.Width}x{native.Height}, {movieTiming.FrameCount} CineForm frames.");
            return 0;
        }
        catch (Exception error) { report["error"] = error.ToString(); throw; }
        finally
        {
            report["wallSeconds"] = wall.Elapsed.TotalSeconds;
            File.WriteAllBytes(Path.Combine(outputDirectory, "report.json"), JsonOutput.SerializeToUtf8Bytes(report));
        }
    }

    static void Save(string directory, string name, Pixels pixels) => File.WriteAllBytes(Path.Combine(directory, name), Png.Encode(pixels));
    static void Cell(Pixels sheet, Pixels pixels, int index)
    {
        int x = index % 3 * pixels.Width, y = index / 3 * pixels.Height;
        for (int row = 0; row < pixels.Height; row++) Buffer.BlockCopy(pixels.Rgba, row * pixels.Width * 4, sheet.Rgba,
            ((row + y) * sheet.Width + x) * 4, pixels.Width * 4);
    }
    static bool IsInside(Pixels p, int margin)
    {
        bool visible = false;
        for (int y = 0; y < p.Height; y++) for (int x = 0; x < p.Width; x++)
        {
            if (p.Rgba[(y * p.Width + x) * 4 + 3] < 3) continue;
            visible = true; if (x < margin || x >= p.Width - margin || y < margin || y >= p.Height - margin) return false;
        }
        return visible;
    }
}
