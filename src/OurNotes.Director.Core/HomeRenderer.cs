using System.Diagnostics;
using System.Numerics;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

/// <summary>Offline Spine and room rendering. No browser, real-time capture, or Cubism library is involved.</summary>
public sealed class HomeRenderer : IDisposable
{
    readonly OffscreenGl gl;
    readonly SpineDrawing drawing;
    readonly HomeRoom? room;
    readonly HomePostFx? post;
    readonly HomeAnimation animation;
    readonly int width, height, supersample, fps;
    readonly float[] projection;
    public string Device => gl.Device;
    public float[] Crop { get; }
    public bool Transparent { get; }
    public int PendingFrames => gl.PendingPixels;
    public int RoomMeshCount => room?.VisibleMeshes ?? 0;
    public bool PostProcessing => post is not null;

    public HomeRenderer(HomeAnimation animation, int width, int height, int supersample, HomeTiming timing, string framing = "scene", bool background = true)
    {
        ValidateDimensions(width, height, supersample);
        if (framing is not ("scene" or "fit")) throw new ArgumentException("Home --framing must be scene or fit.");
        this.animation = animation; this.width = width; this.height = height; this.supersample = supersample; fps = timing.Fps;
        Transparent = animation.Character is not null || !background;
        Crop = framing == "fit" ? animation.Fit(width / (float)height, timing.FirstFrame, timing.FrameCount, fps) : AdvMatrix.Identity();
        projection = AdvMatrix.Multiply(Crop, animation.Camera.ViewProjection(width / (float)height));
        gl = new(width * supersample, height * supersample);
        try
        {
            drawing = new(gl);
            if (!Transparent)
            {
                room = new(gl, animation.Scene.Home, animation.Camera, animation.Resources.Read);
                if (HomePostFx.Stack(animation.Scene.Home) is { } stack) post = new(gl, animation.Resources, stack);
            }
        }
        catch { post?.Dispose(); room?.Dispose(); drawing?.Dispose(); gl.Dispose(); throw; }
    }

    public static void ValidateDimensions(int width, int height, int supersample)
    {
        if (width < 16 || height < 16 || width > 7680 || height > 7680) throw new ArgumentException("Home dimensions must be 16..7680.");
        if (supersample is < 1 or > 4) throw new ArgumentException("Supersample must be 1..4.");
        if ((long)width * height * supersample * supersample > 132_710_400) throw new ArgumentException("Supersampled home render target exceeds 132 megapixels.");
    }

    void Draw(int frame)
    {
        animation.Advance(frame, fps);
        gl.Begin(Transparent ? [0, 0, 0, 0] : animation.Camera.ClearColor);
        var items = room?.Items(projection, frame, fps).ToList() ?? [];
        foreach (var part in animation.Parts)
        {
            if (part.Actor.Bounds is not { } bounds) continue;
            var p = AdvMatrix.Transform(part.World, new(bounds.Center, 1));
            var matrix = AdvMatrix.Multiply(projection, part.World);
            items.Add(new(part.Queue, part.SortingOrder, Vector3.Distance(animation.Camera.Position, new(p.X, p.Y, p.Z)), items.Count,
                () => drawing.Draw(part.Actor, matrix, true, part.DepthWrite, part.StraightAlphaInput)));
        }
        foreach (var item in HomeRoom.Sort(items)) item.Draw();
        post?.Render(frame);
    }
    public Pixels Frame(int frame) { Draw(frame); return gl.Present(width, height, supersample, 0, static (_, _, _) => { }, true); }
    public void QueueFrame(int frame)
    {
        if (PendingFrames >= 3) throw new InvalidOperationException("Read a queued home frame before advancing.");
        Draw(frame); gl.QueuePresent(width, height, supersample, 0, static (_, _, _) => { }, true);
    }
    public Pixels ReadQueuedFrame() => gl.ReadQueued();
    public void Dispose() { post?.Dispose(); room?.Dispose(); drawing.Dispose(); gl.Dispose(); }
}

public static class HomeExport
{
    public static JsonObject Run(HomeAnimation animation, string mode, string output, HomeTiming timing, int width = 1920, int height = 1080,
        int supersample = 2, string framing = "scene", bool background = true, string codec = "cineform", string quality = VideoEncoding.DefaultCineFormQuality,
        bool overwrite = false, string? ffmpeg = null, double[]? times = null, int columns = 3)
    {
        if (mode is not ("frame" or "contact" or "clip" or "sequence")) throw new ArgumentException("Unknown home export mode: " + mode);
        HomeRenderer.ValidateDimensions(width, height, supersample);
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
                var natural = HomeTiming.Plan(animation.Duration, fps: timing.Fps, loop: animation.AllLooping);
                int last = mode == "frame" ? timing.FirstFrame : (int)Math.Floor(times!.Max() * timing.Fps + 1e-8);
                fitTiming = new(0, Math.Max(natural.FrameCount, last + 1), timing.Fps);
            }
            using var renderer = new HomeRenderer(animation, width, height, supersample, fitTiming, framing, background);
            var result = animation.Describe();
            result["output"] = output; result["mode"] = mode; result["width"] = width; result["height"] = height; result["supersample"] = supersample;
            result["fps"] = timing.Fps; result["firstFrame"] = timing.FirstFrame; result["frameCount"] = mode == "frame" ? 1 : timing.FrameCount;
            result["duration"] = mode == "frame" ? 1d / timing.Fps : timing.Duration; result["framing"] = framing;
            result["cropMatrix"] = new JsonArray(renderer.Crop.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
            result["background"] = !renderer.Transparent; result["alpha"] = mode == "clip" && codec == "h264" ? "none" : "straight";
            result["roomMeshes"] = renderer.RoomMeshCount; result["device"] = renderer.Device;
            result["postProcessing"] = renderer.PostProcessing;
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
                    if (encoder.ExitCode != 0) throw new IOException(errors!.GetAwaiter().GetResult());
                }
            }
            result["wallSeconds"] = wall.Elapsed.TotalSeconds;
            if (mode == "sequence") { File.WriteAllBytes(Path.Combine(temp, "animation.json"), JsonOutput.SerializeToUtf8Bytes(result)); Directory.Move(temp, output); }
            else File.Move(temp, output, overwrite);
            return result;
        }
        catch (IOException ex) when (encoder is not null && encoder.HasExited && encoder.ExitCode != 0)
        { throw new IOException("Home video encoder failed: " + errors!.GetAwaiter().GetResult(), ex); }
        finally
        {
            if (encoder is not null) { if (!encoder.HasExited) encoder.Kill(true); encoder.Dispose(); }
            if (File.Exists(temp)) File.Delete(temp);
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
        }
    }
}
