using System.Diagnostics;
using System.Numerics;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

/// <summary>Offline Spine and room rendering. No browser, real-time capture, or Cubism library is involved.</summary>
public sealed class HomeRenderer : IDisposable
{
    readonly OffscreenGl gl;
    readonly SpineDrawing drawing;
    HomeRoom? room;
    HomePostFx? post;
    readonly HomeAnimation animation;
    int width, height, supersample;
    readonly int fps;
    readonly float renderAspect;
    OffscreenGl.DrawingCanvas? previewCanvas;
    float[] projection;
    readonly HomeTiming timing;
    readonly bool fitEditedBounds;
    readonly float sourceAspect;
    readonly float[] sourceProjection;
    HomeEditState? editState;
    string framing;
    bool background;
    IReadOnlyList<PreviewTarget> targets = [];
    public string Device => gl.Device;
    public bool PreferGpuPresentation { get => gl.PreferGpuPresentation; set => gl.PreferGpuPresentation = value; }
    public GpuPreviewSurface? GpuSurface => gl.GpuSurface;
    public string? GpuFallbackReason => gl.GpuFallbackReason;
    public void DisableGpuPresentation(string reason) => gl.DisableGpuPresentation(reason);

    public float[] Crop { get; private set; }
    public bool Transparent => animation.Character is not null || !background;
    public int PendingFrames => gl.PendingPixels;
    public int RoomMeshCount => Transparent ? 0 : room?.VisibleMeshes ?? 0;
    public bool PostProcessing => !Transparent && (editState?.PostProcessing ?? true) && post is not null;

    public HomeRenderer(HomeAnimation animation, int width, int height, int supersample, HomeTiming timing, string framing = "scene", bool background = true,
        HomeEditState? editState = null, bool fitEditedBounds = false, float? sourceAspect = null)
    {
        ValidateDimensions(width, height, supersample);
        if (framing is not ("scene" or "fit")) throw new ArgumentException("Home --framing must be scene or fit.");
        this.animation = animation; this.width = width; this.height = height; this.supersample = supersample; fps = timing.Fps;
        renderAspect = width / (float)height;
        this.timing = timing; this.framing = editState?.Framing ?? framing; this.background = editState?.Background ?? background;
        this.fitEditedBounds = fitEditedBounds && animation.Character is not null;
        this.sourceAspect = sourceAspect ?? renderAspect;
        if (!float.IsFinite(this.sourceAspect) || this.sourceAspect <= 0) throw new ArgumentOutOfRangeException(nameof(sourceAspect));
        sourceProjection = animation.Camera.ViewProjection(this.sourceAspect);
        // Fitting establishes one source crop. Subsequent edits remain deliberate screen-space moves
        // and do not make the viewport chase a dragged character or change the zoom while playing.
        framing = this.framing;
        Crop = ComputeCrop(editState);
        projection = AdvMatrix.Multiply(Crop, this.fitEditedBounds && framing == "fit" ? sourceProjection : animation.Camera.ViewProjection(renderAspect));
        this.editState = editState;
        gl = new(width * supersample, height * supersample);
        try
        {
            drawing = new(gl);
            if (editState is not null) SetEditState(editState);
            else EnsureRoom();
        }
        catch { post?.Dispose(); room?.Dispose(); drawing?.Dispose(); gl.Dispose(); throw; }
    }

    float[] ComputeCrop(HomeEditState? state)
    {
        if (framing != "fit") return AdvMatrix.Identity();
        if (fitEditedBounds)
        {
            animation.SetEditState(state);
            return animation.MeasureBounds(sourceAspect, timing.FirstFrame, timing.FrameCount, fps)
                .Crop(sourceAspect: sourceAspect, outputAspect: renderAspect);
        }
        animation.SetEditState(null);
        return animation.Fit(renderAspect, timing.FirstFrame, timing.FrameCount, fps);
    }

    void EnsureRoom()
    {
        if (Transparent) return;
        room ??= new(gl, animation.Scene.Home, animation.Camera, animation.Resources.Read);
        if ((editState?.PostProcessing ?? true) && post is null && HomePostFx.Stack(animation.Scene.Home) is { } stack)
            post = new(gl, animation.Resources, stack);
    }

    public void SetEditState(HomeEditState state)
    {
        if (PendingFrames > 0) throw new InvalidOperationException("Read queued home frames before changing the composition.");
        HomeProject.ValidateEdits(state);
        if (framing != state.Framing || fitEditedBounds && !ReferenceEquals(editState, state))
        {
            framing = state.Framing;
            Crop = ComputeCrop(state);
            projection = AdvMatrix.Multiply(Crop, fitEditedBounds && framing == "fit" ? sourceProjection : animation.Camera.ViewProjection(renderAspect));
        }
        animation.SetEditState(state); editState = state; background = state.Background; EnsureRoom();
    }

    public IReadOnlyList<PreviewTarget> PreviewTargets() => targets;

    /// <summary>Redraws the same home composition into a new presentation target.
    /// Spine state, room textures, shaders and the source camera remain alive.</summary>
    public void ResizePreview(int width, int height, int supersample = 1)
    {
        ValidateDimensions(width, height, supersample);
        if (PendingFrames > 0) throw new InvalidOperationException("Read queued home frames before resizing a preview.");
        if (this.width == width && this.height == height && this.supersample == supersample) return;
        int sampledWidth = checked(width * supersample), sampledHeight = checked(height * supersample);
        var next = sampledWidth == gl.Width && sampledHeight == gl.Height ? null
            : gl.CreateDrawingCanvas(sampledWidth, sampledHeight);
        post?.ReleasePresentationTargets();
        previewCanvas?.Dispose(); previewCanvas = next;
        this.width = width; this.height = height; this.supersample = supersample;
    }

    public static void ValidateDimensions(int width, int height, int supersample)
    {
        RenderDimensions.Validate(width, height, supersample);
    }

    void Draw(int frame)
    {
        animation.Advance(frame, fps);
        gl.Begin(Transparent ? [0, 0, 0, 0] : animation.Camera.ClearColor);
        double time = frame / (double)fps;
        var items = Transparent ? [] : room?.Items(projection, frame, fps).ToList() ?? [];
        var targetBounds = new Dictionary<string, (Vector2 Min, Vector2 Max)>(StringComparer.Ordinal);
        foreach (var group in animation.Parts.GroupBy(p => p.SourceOrder))
        {
            var source = animation.SourceParts[group.Key];
            var center = source.Actor.Bounds?.Center ?? Vector3.Zero;
            var p = AdvMatrix.Transform(source.World, new(center, 1));
            var batches = new List<(HomeAnimation.Part Part, SpineMeshBatch Batch, float[] Matrix)>();
            foreach (var part in group)
            {
                if (!animation.TransformFor(part, time).Visible) continue;
                var matrix = fitEditedBounds && framing == "fit"
                    ? AdvMatrix.Multiply(Crop, animation.EditedMatrix(part, sourceProjection, sourceAspect, time))
                    : animation.EditedMatrix(part, projection, renderAspect, time);
                foreach (var batch in part.Actor.Mesh.Batches)
                {
                    batches.Add((part, batch, matrix));
                    if (part.CharacterKey is null) continue;
                    for (int i = 0; i < batch.Positions.Length; i += 3)
                    {
                        var projected = AdvMatrix.Transform(matrix, new(batch.Positions[i], batch.Positions[i + 1], batch.Positions[i + 2], 1));
                        if (projected.W <= 0) continue;
                        var point = new Vector2((projected.X / projected.W + 1) / 2, (1 - projected.Y / projected.W) / 2);
                        var bounds = targetBounds.GetValueOrDefault(part.CharacterKey, (new(float.PositiveInfinity), new(float.NegativeInfinity)));
                        targetBounds[part.CharacterKey] = (Vector2.Min(bounds.Item1, point), Vector2.Max(bounds.Item2, point));
                    }
                }
            }
            if (batches.Count == 0) continue;
            items.Add(new(source.Queue, source.SortingOrder, Vector3.Distance(animation.Camera.Position, new(p.X, p.Y, p.Z)), items.Count,
                () => { foreach (var current in batches.OrderBy(b => b.Batch.DrawOrder)) drawing.DrawBatch(current.Batch, current.Matrix, true, current.Part.DepthWrite, current.Part.StraightAlphaInput); }));
        }
        foreach (var item in HomeRoom.Sort(items)) item.Draw();
        if (PostProcessing) post!.Render(frame);
        if (editState is not null) gl.GradeScene(editState.GradeAt(time).ToGradeSpec());
        targets = targetBounds.Select(pair => new PreviewTarget(pair.Key, animation.Scene.Characters.First(c => c.Key == pair.Key).Name,
            pair.Value.Min.X, pair.Value.Min.Y, pair.Value.Max.X - pair.Value.Min.X, pair.Value.Max.Y - pair.Value.Min.Y)).ToArray();
    }
    public Pixels Frame(int frame)
    {
        using var canvas = gl.UseDrawingCanvas(previewCanvas);
        Draw(frame); return gl.Present(width, height, supersample, 0, static (_, _, _) => { }, Transparent);
    }
    public void QueueFrame(int frame)
    {
        if (PendingFrames >= 3) throw new InvalidOperationException("Read a queued home frame before advancing.");
        using var canvas = gl.UseDrawingCanvas(previewCanvas);
        Draw(frame); gl.QueuePresent(width, height, supersample, 0, static (_, _, _) => { }, Transparent);
    }
    public Pixels ReadQueuedFrame() => gl.ReadQueued();
    public void Dispose() { post?.Dispose(); room?.Dispose(); drawing.Dispose(); previewCanvas?.Dispose(); gl.Dispose(); }
}

public static class HomeExport
{
    public static JsonObject Run(HomeAnimation animation, string mode, string output, HomeTiming timing, int width = 1920, int height = 1080,
        int supersample = 2, string framing = "scene", bool background = true, string codec = "cineform", string quality = VideoEncoding.DefaultCineFormQuality,
        bool overwrite = false, string? ffmpeg = null, double[]? times = null, int columns = 3, HomeEditState? editState = null,
        float? sourceAspect = null, double? sourceDuration = null)
    {
        if (mode is not ("frame" or "contact" or "clip" or "sequence")) throw new ArgumentException("Unknown home export mode: " + mode);
        HomeRenderer.ValidateDimensions(width, height, supersample);
        if (editState is not null) { background = editState.Background; framing = editState.Framing; }
        bool transparent = animation.Character is not null || !background;
        codec = VideoEncoding.NormalizeCodec(codec);
        if (mode == "clip")
        {
            VideoEncoding.Validate(codec, width, height, quality); VideoEncoding.ValidateOutputPath(codec, output);
            if (transparent && codec == "h264") throw new ArgumentException("Transparent home animation requires cineform, prores or ffv1.");
            if (string.IsNullOrWhiteSpace(ffmpeg)) throw new ArgumentException("FFmpeg is required for home clip.");
        }
        output = Path.GetFullPath(output);
        if (Directory.Exists(output) || File.Exists(output) && (!overwrite || mode == "sequence")) throw new IOException("Output exists; use a new directory or --overwrite for a file.");
        if (mode == "sequence" && overwrite) throw new ArgumentException("Home sequence requires a new output directory.");
        if (mode == "contact")
        {
            if (times is null || times.Length is < 1 or > 100 || times.Any(t => !double.IsFinite(t) || t < 0 || t > 36000)) throw new ArgumentException("Contact requires 1..100 nonnegative times (maximum ten hours).");
            if (columns < 1 || columns > 100 || (long)width * height * Math.Min(times.Length, columns) * ((times.Length + columns - 1) / columns) > 64_000_000) throw new ArgumentException("Contact dimensions exceed 64 megapixels or columns are invalid.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        string temp = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Process? encoder = null; Task<string>? errors = null;
        var wall = Stopwatch.StartNew();
        try
        {
            // Frame/contact previews use the same whole-animation crop at every requested time.
            var fitTiming = timing;
            if (mode is "frame" or "contact")
            {
                var natural = HomeTiming.Plan(sourceDuration ?? animation.Duration, fps: timing.Fps, loop: animation.AllLooping);
                int last = mode == "frame" ? timing.FirstFrame : (int)Math.Floor(times!.Max() * timing.Fps + 1e-8);
                fitTiming = new(0, Math.Max(natural.FrameCount, last + 1), timing.Fps);
            }
            using var renderer = new HomeRenderer(animation, width, height, supersample, fitTiming, framing, background, editState,
                fitEditedBounds: editState is not null && animation.Character is not null && framing == "fit", sourceAspect: sourceAspect);
            var result = animation.Describe();
            result["output"] = output; result["mode"] = mode; result["width"] = width; result["height"] = height; result["supersample"] = supersample;
            result["fps"] = timing.Fps; result["firstFrame"] = timing.FirstFrame; result["frameCount"] = mode == "frame" ? 1 : timing.FrameCount;
            result["duration"] = mode == "frame" ? 1d / timing.Fps : timing.Duration; result["framing"] = framing;
            result["cropMatrix"] = new JsonArray(renderer.Crop.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
            result["background"] = !renderer.Transparent; result["alpha"] = mode == "clip" && codec == "h264" ? "none" : "straight";
            result["roomMeshes"] = renderer.RoomMeshCount; result["device"] = renderer.Device;
            result["postProcessing"] = renderer.PostProcessing;
            result["edited"] = editState is not null;
            double lastTime = mode == "frame" ? timing.FirstFrame / (double)timing.Fps
                : mode == "contact" ? Math.Floor(times!.Max() * timing.Fps + 1e-8) / timing.Fps : timing.LastFrame / (double)timing.Fps;
            result["includesSourceEnd"] = lastTime + 1e-7 >= animation.Duration;
            if (mode == "frame") File.WriteAllBytes(temp, Png.Encode(renderer.Frame(timing.FirstFrame)));
            else if (mode == "contact")
            {
                int cols = Math.Min(columns, times!.Length), rows = (times.Length + cols - 1) / cols;
                int outWidth = checked(cols * width), outHeight = checked(rows * height);
                var pixels = new byte[checked(outWidth * outHeight * 4)]; var cells = new JsonArray();
                for (int i = 0; i < times.Length; i++)
                {
                    int frame = (int)Math.Floor(times[i] * timing.Fps + 1e-8); var image = renderer.Frame(frame);
                    int x = i % cols * width, y = i / cols * height;
                    for (int row = 0; row < height; row++) Buffer.BlockCopy(image.Rgba, row * width * 4, pixels, ((row + y) * outWidth + x) * 4, width * 4);
                    cells.Add((JsonNode)new JsonObject { ["index"] = i, ["frame"] = frame, ["time"] = frame / (double)timing.Fps, ["x"] = x, ["y"] = y });
                }
                File.WriteAllBytes(temp, Png.Encode(new(outWidth, outHeight, pixels)));
                result["width"] = outWidth; result["height"] = outHeight; result["cells"] = cells; result["frameCount"] = times.Length;
            }
            else
            {
                if (mode == "sequence") Directory.CreateDirectory(temp);
                else
                {
                    encoder = Process.Start(VideoEncoding.RawVideoStartInfo(ffmpeg!, codec, width, height, timing.Fps, temp, quality)) ?? throw new IOException("Cannot start home video encoder.");
                    errors = encoder.StandardError.ReadToEndAsync();
                    result["codec"] = codec; result["cineformQuality"] = codec == "cineform" ? VideoEncoding.NormalizeCineFormQuality(quality) : null;
                }
                int written = 0;
                void WriteQueued()
                {
                    var pixels = renderer.ReadQueuedFrame();
                    if (mode == "sequence") File.WriteAllBytes(Path.Combine(temp, $"frame-{written:D06}.png"), Png.Encode(pixels));
                    else encoder!.StandardInput.BaseStream.Write(pixels.Rgba);
                    written++;
                }
                for (int i = 0; i < timing.FrameCount; i++)
                {
                    renderer.QueueFrame(timing.FirstFrame + i); if (renderer.PendingFrames >= 3) WriteQueued();
                    if (i % timing.Fps == 0) Console.Error.WriteLine($"home {animation.Scene.SpotId}/{animation.Character?.Key ?? "scene"}: {i + 1}/{timing.FrameCount}");
                }
                while (renderer.PendingFrames > 0) WriteQueued();
                if (encoder is not null)
                {
                    encoder.StandardInput.Close(); encoder.WaitForExit();
                    if (encoder.ExitCode != 0) throw VideoEncoding.EncoderFailure(encoder.ExitCode, errors!.GetAwaiter().GetResult());
                }
            }
            result["wallSeconds"] = wall.Elapsed.TotalSeconds;
            if (mode == "sequence") { File.WriteAllBytes(Path.Combine(temp, "animation.json"), JsonOutput.SerializeToUtf8Bytes(result)); Directory.Move(temp, output); }
            else File.Move(temp, output, overwrite);
            return result;
        }
        catch (IOException ex) when (encoder is not null && encoder.HasExited && encoder.ExitCode != 0)
        { throw VideoEncoding.EncoderFailure(encoder.ExitCode, errors!.GetAwaiter().GetResult(), ex); }
        finally
        {
            VideoEncoding.StopEncoder(encoder, errors);
            if (File.Exists(temp)) File.Delete(temp);
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
        }
    }
}
