using System.Text.Json.Nodes;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.Desktop.Tests;

static class ActorCanvasTests
{
    public static void Run(Action<bool, string> check)
    {
        void Reject(Action action, string name)
        {
            bool rejected = false;
            try { action(); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException) { rejected = true; }
            check(rejected, name);
        }
        static bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;
        static bool Contains(IsolatedActorCanvas canvas, ActorCanvasBounds bounds) => canvas.X < bounds.X && canvas.Y < bounds.Y
            && canvas.X + canvas.Width > bounds.X + bounds.Width && canvas.Y + canvas.Height > bounds.Y + bounds.Height;

        var bounds = new ActorCanvasBounds("live2d", 1000, 1000, -.1, .2, .4, .6, 0, 61, 61);
        var tight = bounds.Fit(); var padded = bounds.Fit(1.5); var cineform = bounds.Fit(1, "cineform");
        check(tight.Width == 402 && tight.Height == 602 && Contains(tight.Canvas, bounds),
            "Complete-motion fitting includes off-canvas geometry and one pixel of antialiasing room per edge");
        check(padded.Width == 603 && padded.Height == 903
            && Near(padded.Canvas.X + padded.Canvas.Width / 2, bounds.X + bounds.Width / 2)
            && Near(padded.Canvas.Y + padded.Canvas.Height / 2, bounds.Y + bounds.Height / 2),
            "The canvas multiplier expands both axes around one fixed motion center");
        check(cineform.Width == 416 && cineform.Height == 608 && Contains(cineform.Canvas, bounds)
            && Near(cineform.Canvas.X + cineform.Canvas.Width / 2, tight.Canvas.X + tight.Canvas.Width / 2)
            && Near(cineform.Canvas.Y + cineform.Canvas.Height / 2, tight.Canvas.Y + tight.Canvas.Height / 2),
            "CineForm alignment pads outward and never changes the motion center or clips a pose");
        check(Near(cineform.Canvas.Width * cineform.Canvas.ReferenceWidth, cineform.Width)
            && Near(cineform.Canvas.Height * cineform.Canvas.ReferenceHeight, cineform.Height),
            "Canvas fitting preserves actor pixel scale instead of enlarging the model");
        var odd = bounds with { Width = .401, Height = .601 };
        check(odd.Fit(1, "h264").Width % 2 == 0 && odd.Fit(1, "h264").Height % 2 == 0,
            "H264 canvas fitting pads odd extents to legal even dimensions");
        var tiny = bounds with { Width = .0001, Height = .0001 };
        var tinyCineform = tiny.Fit(1, "cineform");
        VideoEncoding.Validate("cineform", tinyCineform.Width, tinyCineform.Height);
        check(tiny.Fit().Width == 16 && tinyCineform.Width == 16 && tinyCineform.Height == 32,
            "Small visible actors retain the common and CineForm minimum canvas sizes");
        Reject(() => bounds.Fit(.99), "Complete-motion fitting rejects a multiplier that would trim the union");
        Reject(() => bounds.Fit(double.NaN), "Canvas fitting rejects a non-finite multiplier");
        Reject(() => bounds.Fit(100), "Canvas fitting reports the real render limit rather than silently scaling the character");
        Reject(() => (bounds with { VisibleFrames = 0 }).Fit(), "An invisible performance does not invent a fallback motion rectangle");
        check(ActorIsolation.ReadCanvas(ActorIsolation.CanvasJson(padded.Canvas), "live2d") == padded.Canvas,
            "Fixed reference canvas coordinates round-trip through the shared native protocol");
        Reject(() => ActorIsolation.ReadCanvas(ActorIsolation.CanvasJson(tight.Canvas), null), "A fixed actor canvas requires an isolated actor");
        var invalid = ActorIsolation.CanvasJson(tight.Canvas); invalid["width"] = 0;
        Reject(() => ActorIsolation.ReadCanvas(invalid, "live2d"), "Zero-width fixed canvases are rejected");
        invalid = ActorIsolation.CanvasJson(tight.Canvas); invalid["extra"] = true;
        Reject(() => ActorIsolation.ReadCanvas(invalid, "live2d"), "Canvas protocol rejects unknown fields instead of ignoring a misspelt setting");
        Reject(() => (tight with { Width = tight.Width + 1 }).Validate(), "A canvas fit cannot silently change the original actor pixel scale");

        string directory = Path.Combine(Path.GetTempPath(), "director-actor-canvas-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var metadata = JsonNode.Parse("""
                {"canvas":{"width":1000,"height":1000},"defaultMotion":"idle","defaultExpression":"normal",
                 "motions":["idle","wave"],"expressions":["normal"],
                 "parameters":[{"id":"Angle","min":-30,"max":30,"default":0}]}
                """)!;
            var document = Live2DDocument.Create("document-test-model", metadata: metadata);
            document.Time = 1; document.SetMotion("wave"); document.SetParameter("Angle", 12);
            document.SetTransform(new(.6, .4, .9, 7, .8)); document.Range = new(20, 50, 30);
            string path = Path.Combine(directory, "canvas.live2d.json"); document.Save(path);
            var original = document.ToSceneDocument(); var transform = document.Transform;
            document.ApplyCanvasFit(padded);
            var fitted = document.ToSceneDocument();
            check(document.HasUnsavedChanges && document.Canvas == padded.Canvas && document.CanvasMultiplier == 1.5
                && document.Width == padded.Width && document.Height == padded.Height,
                "A workbench canvas fit persists output size, reference rectangle and multiplier as one authored edit");
            check(JsonNode.DeepEquals(original.Root["commands"], fitted.Root["commands"])
                && JsonNode.DeepEquals(original.Root["output"]?["playbackViewport"], fitted.Root["output"]?["playbackViewport"])
                && document.Time == 1 && document.Range == new TimelineRange(20, 50, 30) && document.Transform == transform,
                "Fitting a complete motion preserves all parameter/motion history, source waits, range, cursor and transform");
            document.Undo(); check(document.Canvas is null && document.CanvasMultiplier == 1 && !document.HasUnsavedChanges,
                "Undo restores the original canvas and saved state in one operation");
            document.Redo(); document.Save();
            var reopened = Live2DDocument.Open(path);
            check(reopened.Canvas == padded.Canvas && reopened.CanvasMultiplier == 1.5 && reopened.Transform == transform && !reopened.HasUnsavedChanges,
                "Saving and reopening retains the same complete-motion composition and multiplier");
            var first = reopened.ToJson(); reopened.ApplyCanvasFit(padded);
            check(reopened.ToJson() == first && !reopened.HasUnsavedChanges,
                "Applying the same original-canvas fit twice never accumulates a second crop or transform");
            var exported = SceneAssetExport.ApplyCanvasFit(fitted, cineform);
            check(exported.Output.Width == cineform.Width && exported.Output.Height == cineform.Height
                && JsonNode.DeepEquals(exported.Root["commands"], fitted.Root["commands"])
                && fitted.Output.Width == padded.Width,
                "Changing export codec reuses the analysis with outward padding on an independent project copy");
            using (var snapshot = exported.CreateSnapshot(Path.Combine(directory, "snapshots")))
            {
                var native = DirectorProject.Read(snapshot.FilePath);
                check(ActorIsolation.ReadCanvas(native.Output["isolateCanvas"], native.Output["isolateActor"]!.ToString()) == cineform.Canvas
                    && native.Output["canvasMultiplier"] is null,
                    "The exact fixed canvas reaches ordinary native export snapshots without workbench-only metadata");
            }
            var bad = exported.Root; bad["output"]!["isolateCanvas"]!["height"] = -1;
            check(SceneDocument.Parse(bad.ToJsonString()).Validate().HasErrors,
                "Desktop validation rejects malformed fixed output rectangles before queueing");
            string badProject = Path.Combine(directory, "invalid.project.json"); File.WriteAllText(badProject, bad.ToJsonString());
            Reject(() => DirectorProject.Read(badProject), "The native project reader independently rejects the same malformed rectangle");
            Reject(() => document.ApplyCanvasFit(padded with { ActorId = "other" }), "A stale analysis for another actor cannot be applied to this document");
            reopened.SetOutput(1000, 1000);
            check(reopened.Canvas is null && reopened.CanvasMultiplier == 1 && reopened.Transform == transform,
                "Choosing an ordinary canvas removes the fixed crop while preserving the actor transform");
            var corrupted = JsonNode.Parse(document.ToJson())!; corrupted["output"]!["canvasMultiplier"] = .5;
            Reject(() => Live2DDocument.Parse(corrupted.ToJsonString()), "Saved Live2D canvases reject a multiplier below one");

            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            bool cancelled = false;
            try
            {
                ActorCanvasAnalysis.AnalyzeAsync(new("missing-archive", "missing-project", "missing-core", "missing-ffmpeg"),
                    0, 61, cancellationToken: cancellation.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { cancelled = true; }
            check(cancelled, "Cancelling an analysis before it starts avoids loading any archive, native renderer or project");
        }
        finally { Directory.Delete(directory, true); }
    }
}
