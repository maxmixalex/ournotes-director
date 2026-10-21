using System.Diagnostics;
using System.Text.Json.Nodes;
using OurNotes.Director.Core;

// Exercise the same child process lifetime used by the three production RGBA
// exporters. No archive, renderer, or large frame buffers are required.
static class VideoEncodingLifecycleNativeTests
{
    internal static async Task<int> RunAsync(string ffmpeg, string output)
    {
        Directory.CreateDirectory(output);
        string interrupted = Path.Combine(output, "interrupted-encoder.mov");
        string completed = Path.Combine(output, "completed-encoder.mov");
        File.Delete(interrupted); File.Delete(completed);
        var checks = new JsonArray();
        var report = new JsonObject
        {
            ["schema"] = "ournotes-director/encoder-lifecycle/1", ["ok"] = false,
            ["platform"] = Environment.OSVersion.ToString(), ["ffmpeg"] = Path.GetFullPath(ffmpeg), ["checks"] = checks
        };
        void Check(bool ok, string name)
        {
            checks.Add((JsonNode)new JsonObject { ["name"] = name, ["ok"] = ok });
            if (!ok) throw new InvalidOperationException(name);
            Console.WriteLine("PASS " + name);
        }
        try
        {
            byte[] rgba = new byte[64 * 64 * 4];
            for (int pixel = 0; pixel < rgba.Length; pixel += 4)
            {
                rgba[pixel] = (byte)(pixel / 4 % 64 * 4);
                rgba[pixel + 1] = 120; rgba[pixel + 2] = 240; rgba[pixel + 3] = 200;
            }
            using (var encoder = Process.Start(VideoEncoding.RawVideoStartInfo(ffmpeg, "cineform", 64, 64, 30, interrupted))
                ?? throw new IOException("Cannot start real interrupted CineForm encoder"))
            {
                var stderr = encoder.StandardError.ReadToEndAsync();
                var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                encoder.EnableRaisingEvents = true;
                encoder.Exited += (_, _) => exited.TrySetResult();
                bool stopped = false;
                void Stop() { stopped = true; VideoEncoding.StopEncoder(encoder, stderr); }
                try
                {
                    // Enough packets for FFmpeg's ordinary input probing, while
                    // leaving stdin open so its real MOV output remains owned.
                    for (int frame = 0; frame < 32; frame++)
                        await encoder.StandardInput.BaseStream.WriteAsync(rgba).AsTask().WaitAsync(TimeSpan.FromSeconds(20));
                    await encoder.StandardInput.BaseStream.FlushAsync();
                    var deadline = DateTime.UtcNow.AddSeconds(20);
                    while (!File.Exists(interrupted) && !encoder.HasExited && DateTime.UtcNow < deadline) await Task.Delay(20);
                    Check(File.Exists(interrupted) && !encoder.HasExited, "real CineForm encoder is alive with an open partial MOV");
                    if (OperatingSystem.IsWindows())
                    {
                        bool sharing = false;
                        try { File.Delete(interrupted); }
                        catch (IOException error) { sharing = (error.HResult & 0xffff) == 32; }
                        Check(sharing && File.Exists(interrupted), "Windows rejects deletion of the real encoder's still-open MOV");
                    }
                    var original = new InvalidDataException("synthetic frame failure while real FFmpeg owns the video");
                    Exception? reported = null;
                    try
                    {
                        try { throw original; }
                        finally { Stop(); File.Delete(interrupted); }
                    }
                    catch (Exception error) { reported = error; }
                    await exited.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    Check(exited.Task.IsCompletedSuccessfully && !File.Exists(interrupted), "interrupted real FFmpeg exits before immediate temporary-file cleanup");
                    Check(ReferenceEquals(reported, original), "real encoder cleanup preserves the original frame failure");
                    report["interruptedStderr"] = await stderr.WaitAsync(TimeSpan.FromSeconds(20));
                }
                finally
                {
                    if (!stopped) Stop();
                }
            }
            using (var encoder = Process.Start(VideoEncoding.RawVideoStartInfo(ffmpeg, "cineform", 64, 64, 30, completed))
                ?? throw new IOException("Cannot start real completed CineForm encoder"))
            {
                var stderr = encoder.StandardError.ReadToEndAsync();
                try
                {
                    await encoder.StandardInput.BaseStream.WriteAsync(rgba).AsTask().WaitAsync(TimeSpan.FromSeconds(20));
                    encoder.StandardInput.Close();
                    await encoder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                    string errors = await stderr.WaitAsync(TimeSpan.FromSeconds(20));
                    Check(encoder.ExitCode == 0 && File.Exists(completed) && new FileInfo(completed).Length > 0,
                        "ordinary one-frame CineForm encoding completes before cleanup");
                    report["completedStderr"] = errors;
                }
                finally { VideoEncoding.StopEncoder(encoder, stderr); }
            }
            File.Delete(completed);
            Check(!File.Exists(completed), "already-exited encoder cleanup releases the completed output without changing its success");
            report["ok"] = true;
            Console.WriteLine($"Encoder lifetime: {checks.Count} checks passed.");
            return 0;
        }
        catch (Exception error)
        {
            report["exception"] = error.ToString();
            Console.Error.WriteLine(error); return 1;
        }
        finally
        {
            File.WriteAllText(Path.Combine(output, "encoder-lifecycle.json"), report.ToJsonString(new() { WriteIndented = true }));
        }
    }
}
