using System.Diagnostics;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

public static class VoiceFile
{
    public static AdvCue Decode(string path, long id, string ffmpeg)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("语音文件不存在。", path);
        var info = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-v", "error", "-nostdin", "-i", path, "-vn", "-f", "f32le", "-ar", "48000", "-ac", "1", "pipe:1" }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("无法解码语音。");
        using var data = new MemoryStream();
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            var buffer = new byte[65536]; int read;
            while ((read = process.StandardOutput.BaseStream.Read(buffer)) > 0)
            {
                if (data.Length + read > 256L * 1024 * 1024) throw new IOException("单段语音过长，请先裁剪后导入。");
                data.Write(buffer, 0, read);
            }
            process.WaitForExit();
            if (process.ExitCode != 0) throw new IOException(errors.GetAwaiter().GetResult());
            var bytes = data.ToArray();
            if (bytes.Length == 0 || bytes.Length % 4 != 0) throw new InvalidDataException("文件没有可播放的语音。");
            var pcm = new float[bytes.Length / 4]; Buffer.BlockCopy(bytes, 0, pcm, 0, bytes.Length);
            if (pcm.Any(sample => !float.IsFinite(sample))) throw new InvalidDataException("音频样本无效。");
            return new(id, 2, pcm, 1);
        }
        finally { if (!process.HasExited) process.Kill(true); }
    }
    public static float[] Peaks(AdvCue cue, int count = 256)
    {
        var peaks = new float[count];
        for (int i = 0; i < cue.Samples; i++)
        { int at = (int)((long)i * count / cue.Samples); peaks[at] = Math.Max(peaks[at], Math.Abs(cue.Pcm[i * cue.Channels])); }
        return peaks;
    }
}

public sealed class ExternalVoiceBank(AdvAudio audio, string baseDirectory, string ffmpeg, VoiceDecodeCache? cache = null)
{
    readonly Dictionary<string, AdvCue> externalVoices = new(StringComparer.OrdinalIgnoreCase);
    long externalVoiceId = -1;
    public AdvRow Resolve(AdvRow row)
    {
        string requested = row.Text("AudioPath");
        if (requested.Length == 0) return row;
        string path = Path.GetFullPath(requested, baseDirectory);
        var file = new FileInfo(path);
        string key = path + "\0" + file.Length + "\0" + file.LastWriteTimeUtc.Ticks + "\0" + row.Text("AudioIn") + "\0" + row.Text("AudioDuration");
        if (!externalVoices.TryGetValue(key, out var cue))
        {
            var decoded = (cache ?? VoiceDecodeCache.Shared).Read(path, ffmpeg);
            var fields = row.Data.AsObject();
            var range = VoiceSampleRange.FromSeconds(CreativeScene.Number(fields, "AudioIn"),
                CreativeScene.Number(fields, "AudioDuration"), decoded.Samples);
            cue = decoded with { SoundId = externalVoiceId--,
                Pcm = range.Offset == 0 && range.Count == decoded.Samples ? decoded.Pcm
                    : decoded.Pcm.AsSpan(range.Offset, range.Count).ToArray() };
            externalVoices.Add(key, cue); audio.AddCue(cue);
        }
        var data = row.Data.DeepClone(); data["VoiceIDs"] = new JsonArray(cue.SoundId);
        return row with { Data = data };
    }
}

public sealed partial class AdvUi
{
    public string AudioProjectDirectory { get; set; } = Environment.CurrentDirectory;
    public string AudioFfmpegPath { get; set; } = "ffmpeg";
    ExternalVoiceBank? externalVoiceBank;
    AdvRow ResolveExternalVoice(AdvRow row) => (externalVoiceBank ??= new(audio, AudioProjectDirectory, AudioFfmpegPath)).Resolve(row);
}
