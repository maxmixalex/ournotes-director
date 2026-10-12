using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace OurNotes.Director.Desktop.Core;

public static class MediaPreview
{
    public static async Task<string> CreateAsync(string input, string ffmpeg, bool still, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(input)) throw new FileNotFoundException("找不到要预览的文件。", input);
        if (!File.Exists(ffmpeg)) throw new FileNotFoundException("找不到随包的 FFmpeg。", ffmpeg);
        var info = new FileInfo(input);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(info.FullName + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks + "|" + still))).ToLowerInvariant();
        string directory = Path.Combine(DesktopSettings.DataDirectory, "previews"); Directory.CreateDirectory(directory);
        string output = Path.Combine(directory, key + (still ? ".png" : ".mp4"));
        if (File.Exists(output)) return output;
        string temporary = output + "." + Guid.NewGuid().ToString("N") + (still ? ".png" : ".mp4");
        try
        {
            using var process = new Process { StartInfo = new ProcessStartInfo
            {
                FileName = ffmpeg, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true
            } };
            string[] arguments = still
                ? ["-hide_banner", "-nostdin", "-v", "error", "-i", input, "-frames:v", "1", "-pix_fmt", "rgba", temporary]
                : ["-hide_banner", "-nostdin", "-v", "error", "-i", input, "-map", "0:v:0", "-map", "0:a?", "-vf", "scale=960:-2", "-c:v", "libx264",
                    "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p", "-c:a", "aac", "-movflags", "+faststart", temporary];
            foreach (var arg in arguments) process.StartInfo.ArgumentList.Add(arg);
            process.Start();
            using var registration = cancellationToken.Register(() =>
            { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
            var error = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
            await Task.WhenAll(error, stdout, process.WaitForExitAsync()); cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new IOException("无法生成播放预览：" + await error);
            File.Move(temporary, output, true); return output;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
