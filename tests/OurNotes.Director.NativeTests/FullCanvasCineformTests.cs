using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

// A real, original-size regression. Rendering/encoding/decoding run in separate
// scopes so the check itself does not keep multiple 54 MP native surfaces alive.
static class FullCanvasCineformTests
{
    const int Frames = 2;

    public static JsonObject Run(Archive archive, string projectPath, string outputDirectory,
        string core, string ffmpeg, string ffprobe)
    {
        Directory.CreateDirectory(outputDirectory);
        string movie = Path.Combine(outputDirectory, "full-canvas-cineform.mov");
        bool keep = Environment.GetEnvironmentVariable("OURNOTES_KEEP_FULL_CANVAS") == "1";
        var checks = new JsonArray();
        var report = new JsonObject
        {
            ["schema"] = "ournotes-director/full-canvas-cineform/1", ["ok"] = false,
            ["checks"] = checks, ["movieRetained"] = keep, ["movie"] = Path.GetFileName(movie)
        };
        void Check(bool ok, string name)
        {
            checks.Add((JsonNode)new JsonObject { ["name"] = name, ["ok"] = ok });
            if (!ok) throw new InvalidOperationException(name);
        }
        var wall = Stopwatch.StartNew();
        try
        {
            var source = DirectorProject.Read(projectPath);
            int width = int.Parse(Archive.S(source.Output, "width")), height = int.Parse(Archive.S(source.Output, "height"));
            Check(width == 6000 && height == 9000 && Archive.S(source.Output, "supersample") == "1",
                "the real model keeps its original 6000x9000 canvas at supersample 1");
            report["width"] = width; report["height"] = height; report["supersample"] = 1;
            report["fps"] = 30; report["requestedFrames"] = Frames;
            report["projectSha256"] = HashFile(projectPath);
            report["encoderThreads"] = VideoEncoding.RawVideoThreadLimit(width, height);

            // These arrays retain only alpha. The RGBA frames and native renderer
            // return before the formal video path allocates its own frame buffers.
            byte[][] reference = Reference(archive, source, projectPath, outputDirectory, core, ffmpeg, width, height);
            GC.Collect(); GC.WaitForPendingFinalizers();
            long stamp = Stopwatch.GetTimestamp();
            _ = AdvStoryRenderer.Run(archive, source.Site, core, source.Adv, source.Language, projectPath,
                "clip", movie, width, height, 0, Frames / 30d, "cineform", true, true, ffmpeg,
                supersample: 1, cineformQuality: VideoEncoding.DefaultCineFormQuality);
            report["encodeMilliseconds"] = Stopwatch.GetElapsedTime(stamp).TotalMilliseconds;
            report["cineformQuality"] = VideoEncoding.DefaultCineFormQuality;
            report["movieBytes"] = new FileInfo(movie).Length;
            report["movieSha256"] = HashFile(movie);
            GC.Collect(); GC.WaitForPendingFinalizers();

            // ffprobe -count_frames decodes the stream; it needs the same bounded
            // decoder budget as the pixel comparison below on a 54 MP source.
            var probe = JsonNode.Parse(Media.Run(ffprobe,
                ["-v", "error", "-threads", "1", "-count_frames", "-show_streams", "-show_format", "-of", "json", movie]))!;
            var video = probe["streams"]!.AsArray().Single(row => Archive.S(row!, "codec_type") == "video")!;
            report["probe"] = probe;
            Check(Archive.S(video, "codec_name") == "cfhd" && Archive.S(video, "codec_tag_string") == "CFHD"
                && Archive.S(video, "pix_fmt") == "gbrap12le", "formal MOV contains tagged RGB+alpha CineForm");
            Check(Archive.S(video, "width") == width.ToString() && Archive.S(video, "height") == height.ToString(),
                "encoded video retains every original canvas pixel without resizing");
            Check(Archive.S(video, "nb_read_frames") == Frames.ToString() && Archive.S(video, "nb_frames") == Frames.ToString()
                && Archive.S(video, "avg_frame_rate") == "30/1", "ffprobe decodes exactly two frames at 30 fps");
            report["frameCount"] = Frames;

            var alpha = DecodeAlpha(ffmpeg, movie, reference);
            report["alpha"] = alpha;
            // Film1 is lossy, including alpha. The prior complete real-animation
            // benchmark in CINEFORM.md measured a six-level worst-case alpha error.
            // Keep the empty/opaque endpoint checks exact rather than hiding a
            // conversion bias inside that permitted edge quantization.
            report["maxPermittedAlphaError"] = 6;
            foreach (var frame in alpha)
            {
                Check(frame!["transparentPixels"]!.GetValue<long>() > 0 && frame["opaquePixels"]!.GetValue<long>() > 0
                    && frame["partialAlphaPixels"]!.GetValue<long>() > 0,
                    $"decoded frame {frame["frame"]} retains empty canvas, opaque artwork and antialiased alpha");
                Check(frame["transparentLeaks"]!.GetValue<long>() == 0 && frame["opaqueChanges"]!.GetValue<long>() == 0
                    && frame["maxAbsoluteError"]!.GetValue<int>() <= 6,
                    $"decoded frame {frame["frame"]} preserves alpha endpoints and the documented Film1 alpha-error bound");
            }
            report["ok"] = true;
            Console.WriteLine($"Full-canvas CineForm: {width}x{height}, {Frames} frames, alpha verified; {report["movieBytes"]} bytes.");
            return report;
        }
        catch (Exception error) { report["exception"] = error.ToString(); throw; }
        finally
        {
            report["wallMilliseconds"] = wall.Elapsed.TotalMilliseconds;
            if (!keep && File.Exists(movie)) File.Delete(movie);
            File.WriteAllText(Path.Combine(outputDirectory, "full-canvas-cineform.json"), report.ToJsonString(new() { WriteIndented = true }));
        }
    }

    static byte[][] Reference(Archive archive, DirectorProject source, string project, string output,
        string core, string ffmpeg, int width, int height)
    {
        var alpha = new byte[Frames][];
        using var renderer = new AdvStoryRenderer(archive, source.Site, core, source.Adv, source.Language,
            width, height, project, ffmpeg, 1);
        for (int frame = 0; frame < Frames; frame++)
        {
            var image = renderer.Frame(frame, true);
            alpha[frame] = new byte[checked(width * height)];
            for (int i = 0; i < alpha[frame].Length; i++) alpha[frame][i] = image.Rgba[i * 4 + 3];
            if (frame == 0) File.WriteAllBytes(Path.Combine(output, "full-canvas-cineform-preview.png"), Png.Encode(Thumbnail(image)));
        }
        return alpha;
    }

    static JsonArray DecodeAlpha(string ffmpeg, string movie, byte[][] reference)
    {
        var start = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in new[] { "-v", "error", "-nostdin", "-threads", "1", "-filter_threads", "1", "-i", movie,
            "-map", "0:v:0", "-frames:v", Frames.ToString(), "-vf", "format=rgba,alphaextract", "-pix_fmt", "gray",
            "-threads:v", "1", "-f", "rawvideo", "pipe:1" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Cannot start full-canvas alpha decoder");
        var errors = process.StandardError.ReadToEndAsync();
        var frames = new JsonArray(); var buffer = new byte[64 * 1024];
        try
        {
            for (int frame = 0; frame < Frames; frame++)
            {
                int offset = 0, maxError = 0;
                long transparent = 0, opaque = 0, partial = 0, leaks = 0, opaqueChanges = 0, absoluteError = 0;
                long visibleSource = 0, partialSource = 0, visibleError = 0, partialError = 0;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                while (offset < reference[frame].Length)
                {
                    int count = process.StandardOutput.BaseStream.Read(buffer, 0, Math.Min(buffer.Length, reference[frame].Length - offset));
                    if (count == 0) throw new InvalidDataException($"Decoded alpha ended early in frame {frame}: {offset}/{reference[frame].Length} pixels.");
                    hash.AppendData(buffer, 0, count);
                    for (int i = 0; i < count; i++)
                    {
                        byte actual = buffer[i], expected = reference[frame][offset + i];
                        if (actual == 0) transparent++; else if (actual == 255) opaque++; else partial++;
                        if (expected == 0 && actual != 0) leaks++;
                        if (expected == 255 && actual != 255) opaqueChanges++;
                        int error = Math.Abs(actual - expected); maxError = Math.Max(maxError, error); absoluteError += error;
                        if (expected > 0) { visibleSource++; visibleError += error; }
                        if (expected is > 0 and < 255) { partialSource++; partialError += error; }
                    }
                    offset += count;
                }
                frames.Add((JsonNode)new JsonObject
                {
                    ["frame"] = frame, ["pixels"] = offset, ["transparentPixels"] = transparent,
                    ["opaquePixels"] = opaque, ["partialAlphaPixels"] = partial, ["transparentLeaks"] = leaks,
                    ["opaqueChanges"] = opaqueChanges, ["maxAbsoluteError"] = maxError,
                    ["meanAbsoluteError"] = absoluteError / (double)offset,
                    ["sourceVisiblePixels"] = visibleSource, ["sourcePartialAlphaPixels"] = partialSource,
                    ["meanVisibleAbsoluteError"] = visibleSource == 0 ? 0 : visibleError / (double)visibleSource,
                    ["meanPartialAbsoluteError"] = partialSource == 0 ? 0 : partialError / (double)partialSource,
                    ["nativeSha256"] = Archive.Hash(reference[frame]), ["decodedSha256"] = Convert.ToHexStringLower(hash.GetHashAndReset())
                });
            }
            if (process.StandardOutput.BaseStream.ReadByte() != -1) throw new InvalidDataException("Alpha decoder produced more than two complete frames.");
            process.WaitForExit();
            if (process.ExitCode != 0) throw new IOException($"Full-canvas alpha decoder exited with code {process.ExitCode}: {errors.GetAwaiter().GetResult()}");
            return frames;
        }
        finally { if (!process.HasExited) process.Kill(true); }
    }

    static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    static Pixels Thumbnail(Pixels image)
    {
        const int width = 360;
        int height = Math.Max(1, (int)Math.Round(image.Height * width / (double)image.Width));
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int source = ((int)(y * (long)image.Height / height) * image.Width + (int)(x * (long)image.Width / width)) * 4;
            image.Rgba.AsSpan(source, 4).CopyTo(pixels.AsSpan((y * width + x) * 4, 4));
        }
        return new(width, height, pixels);
    }
}
