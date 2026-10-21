using System.Text.Json.Nodes;
using OurNotes.Director.Core;

/// <summary>Real Cubism geometry and normal rendered output exercise the same fixed
/// canvas. The bounds pass itself performs no per-frame drawing or readback.</summary>
static class ActorCanvasNativeTests
{
    public static async Task RunAsync(ScenePreviewSource input, string parameter, double pinned,
        Action<bool, string> check, JsonObject report, string outputDirectory)
    {
        const int end = 91;
        var source = DirectorProject.Document(input.ProjectPath).DeepClone().AsObject();
        string actor = source["output"]!["isolateActor"]!.ToString();
        // Older isolated projects can retain this output label without a source
        // playback viewport; isolation still has no opening letterbox wait.
        source["output"]!["letterbox"] = "game";
        source["output"]!["isolateTransform"] = ActorIsolation.TransformJson(new(.64, .35, 1.1, 13));
        source["commands"]!.AsArray().Insert(2, new JsonObject { ["cmd"] = "Director.Parameter", ["TargetName"] = actor,
            ["Parameter"] = parameter, ["Value"] = pinned, ["Duration"] = 0, ["AtFrame"] = 15 });
        source["commands"]!.AsArray().Insert(3, new JsonObject { ["cmd"] = "Director.Parameter", ["TargetName"] = actor,
            ["Parameter"] = parameter, ["Operation"] = "release", ["Duration"] = 0, ["AtFrame"] = 60 });
        string original = Path.Combine(outputDirectory, "canvas-original.project.json");
        File.WriteAllText(original, source.ToJsonString());
        var analysisSource = input with { ProjectPath = original };
        var bounds = await ActorCanvasAnalysis.AnalyzeAsync(analysisSource, 0, end);
        var fit = bounds.Fit(1, "cineform");
        fit.Validate();
        var samples = await Task.Run(() =>
        {
            var frames = new List<PreviewTarget?>();
            var project = DirectorProject.Read(original);
            using var native = new AdvStoryRenderer(new Archive(input.ArchivePath), project.Site, input.CubismPath,
                project.Adv, project.Language, bounds.ReferenceWidth, bounds.ReferenceHeight, original, input.FfmpegPath, 1);
            for (int frame = 0; frame < end; frame++) frames.Add(native.MeasureIsolatedBounds(frame, bounds.ReferenceWidth, bounds.ReferenceHeight));
            check(native.Timings.GetValueOrDefault("readbackAndResolve") == 0
                && native.Timings.GetValueOrDefault("isolatedActor") == 0,
                "Complete-motion analysis advances actual native geometry without final draws or GPU pixel readback");
            return frames;
        });
        static bool Near(double a, double b) => Math.Abs(a - b) < 1e-5;
        var visible = samples.OfType<PreviewTarget>().ToArray();
        check(bounds.StartFrame == 0 && bounds.EndFrameExclusive == end && bounds.VisibleFrames == visible.Length && visible.Length > 0
            && Near(bounds.X, visible.Min(value => value.X)) && Near(bounds.Y, visible.Min(value => value.Y))
            && Near(bounds.X + bounds.Width, visible.Max(value => value.X + value.Width))
            && Near(bounds.Y + bounds.Height, visible.Max(value => value.Y + value.Height)),
            "The complete-motion rectangle includes all 91 evaluated frames, authored pins and model rotation, including off-canvas geometry");
        check(samples.Where(value => value is not null).All(value =>
            value!.X >= fit.Canvas.X && value.Y >= fit.Canvas.Y
            && value.X + value.Width <= fit.Canvas.X + fit.Canvas.Width
            && value.Y + value.Height <= fit.Canvas.Y + fit.Canvas.Height),
            "One fixed, outward-aligned canvas contains every visible pose in the animation");

        var cropped = source.DeepClone().AsObject();
        cropped["output"]!["width"] = fit.Width; cropped["output"]!["height"] = fit.Height;
        cropped["output"]!["isolateCanvas"] = ActorIsolation.CanvasJson(fit.Canvas);
        string fitted = Path.Combine(outputDirectory, "canvas-fitted.project.json");
        File.WriteAllText(fitted, cropped.ToJsonString());
        var fittedSource = analysisSource with { ProjectPath = fitted };
        var repeated = await ActorCanvasAnalysis.AnalyzeAsync(fittedSource, 0, end);
        check(repeated == bounds && repeated.Fit(1, "cineform") == fit,
            "Analyzing an already fitted native project retains the original coordinate system without cumulative crops");
        var interval = await ActorCanvasAnalysis.AnalyzeAsync(fittedSource, 30, 61);
        var intervalSamples = samples.Skip(30).Take(31).OfType<PreviewTarget>().ToArray();
        check(interval.StartFrame == 30 && interval.EndFrameExclusive == 61 && interval.VisibleFrames == intervalSamples.Length
            && Near(interval.X, intervalSamples.Min(value => value.X)) && Near(interval.Y, intervalSamples.Min(value => value.Y))
            && Near(interval.X + interval.Width, intervalSamples.Max(value => value.X + value.Width))
            && Near(interval.Y + interval.Height, intervalSamples.Max(value => value.Y + value.Height)),
            "An explicit range still replays the earlier native parameter history before measuring its first frame");

        var pictures = new JsonArray();
        await Task.Run(() =>
        {
            var project = DirectorProject.Read(fitted);
            using var native = new AdvStoryRenderer(new Archive(input.ArchivePath), project.Site, input.CubismPath,
                project.Adv, project.Language, fit.Width, fit.Height, fitted, input.FfmpegPath, 1);
            foreach (int frame in new[] { 0, 45, 90 })
            {
                var pixels = native.Frame(frame, true); var target = native.PreviewTargets().SingleOrDefault();
                var raw = samples[frame];
                if (raw is null)
                {
                    check(target is null && pixels.Rgba.Where((_, index) => index % 4 == 3).All(alpha => alpha == 0),
                        "A source-hidden frame remains genuinely transparent inside the fixed canvas");
                    continue;
                }
                check(target is not null && Near(target.X, (raw.X - fit.Canvas.X) / fit.Canvas.Width)
                    && Near(target.Y, (raw.Y - fit.Canvas.Y) / fit.Canvas.Height)
                    && Near(target.Width, raw.Width / fit.Canvas.Width) && Near(target.Height, raw.Height / fit.Canvas.Height),
                    "Normal native drawing maps the evaluated pose through the same fixed analysis rectangle at frame " + frame);
                int opaque = 0, transparent = 0;
                for (int index = 3; index < pixels.Rgba.Length; index += 4)
                    if (pixels.Rgba[index] == 0) transparent++; else opaque++;
                bool clearEdges = true;
                for (int x = 0; x < pixels.Width; x++)
                    clearEdges &= pixels.Rgba[x * 4 + 3] == 0 && pixels.Rgba[((pixels.Height - 1) * pixels.Width + x) * 4 + 3] == 0;
                for (int y = 0; y < pixels.Height; y++)
                    clearEdges &= pixels.Rgba[y * pixels.Width * 4 + 3] == 0 && pixels.Rgba[(y * pixels.Width + pixels.Width - 1) * 4 + 3] == 0;
                check(opaque > 0 && transparent > 0 && clearEdges,
                    "The fitted PNG contains the actual model and transparent, uncut edges at frame " + frame);
                string file = Path.Combine(outputDirectory, "canvas-frame-" + frame.ToString("D4") + ".png");
                File.WriteAllBytes(file, Png.Encode(pixels));
                pictures.Add((JsonNode)new JsonObject { ["frame"] = frame, ["width"] = pixels.Width, ["height"] = pixels.Height,
                    ["opaquePixels"] = opaque, ["transparentPixels"] = transparent, ["transparentEdges"] = clearEdges,
                    ["file"] = Path.GetFileName(file) });
            }
        });
        report["completeMotionCanvas"] = new JsonObject
        {
            ["referenceWidth"] = bounds.ReferenceWidth, ["referenceHeight"] = bounds.ReferenceHeight,
            ["x"] = bounds.X, ["y"] = bounds.Y, ["width"] = bounds.Width, ["height"] = bounds.Height,
            ["startFrame"] = bounds.StartFrame, ["endFrameExclusive"] = bounds.EndFrameExclusive,
            ["visibleFrames"] = bounds.VisibleFrames, ["outputWidth"] = fit.Width, ["outputHeight"] = fit.Height,
            ["canvas"] = ActorIsolation.CanvasJson(fit.Canvas), ["samples"] = pictures
        };
    }
}
