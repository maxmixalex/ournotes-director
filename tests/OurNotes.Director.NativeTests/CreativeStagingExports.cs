using System.Text.Json.Nodes;
using OurNotes.Director.Core;

static class CreativeStagingExports
{
    public static void Run(Archive archive, string core, string ffmpeg, string project,
        string output, string actor, Action<bool, string> check)
    {
        string movie = Path.Combine(output, "two-character-demo.mp4");
        var encoded = AdvStoryRenderer.Run(archive, "intl", core, DirectorProject.LibrarySource,
            "zh-Hans", project, "clip", movie, 640, 360, 0, 3, "h264", true, true, ffmpeg, 1);
        var details = JsonNode.Parse(JsonOutput.SerializeToUtf8Bytes(encoded))!;
        File.WriteAllText(Path.Combine(output, "export-report.json"), details.ToJsonString());
        string probe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        var streams = JsonNode.Parse(Media.Run(probe, ["-v", "error", "-count_frames", "-show_streams", "-of", "json", movie]))!["streams"]!.AsArray();
        check(streams.Any(s => s?["codec_type"]?.ToString() == "video" && s["nb_read_frames"]?.ToString() == "90")
            && streams.Any(s => s?["codec_type"]?.ToString() == "audio" && s["sample_rate"]?.ToString() == "48000"),
            "A complete three-second stage movie exports ninety decoded frames and a real 48 kHz audio track");
        string audio = Path.Combine(output, ".decoded-audio.f32");
        try
        {
            Media.Run(ffmpeg, ["-v", "error", "-y", "-i", movie, "-map", "0:a:0", "-f", "f32le", "-ac", "1", "-ar", "48000", audio]);
            byte[] bytes = File.ReadAllBytes(audio); var pcm = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, pcm, 0, bytes.Length);
            check(pcm.Any(s => Math.Abs(s) > .01), "The exported stage movie contains non-silent decoded dialogue, not only an empty audio stream");
        }
        finally { if (File.Exists(audio)) File.Delete(audio); }
        var isolated = DirectorProject.Document(project);
        isolated["output"]!["isolateActor"] = actor;
        isolated["output"]!["width"] = 640; isolated["output"]!["height"] = 960;
        string isolatedProject = Path.Combine(output, "isolated.project.json"), picture = Path.Combine(output, "isolated-current-pose.png");
        File.WriteAllText(isolatedProject, isolated.ToJsonString());
        AdvStoryRenderer.Run(archive, "intl", core, DirectorProject.LibrarySource, "zh-Hans", isolatedProject,
            "frame", picture, 640, 960, 2.5, 1, "ffv1", true, true, ffmpeg, 1);
        var pixels = Png.Decode(File.ReadAllBytes(picture));
        bool clear = false, visible = false;
        for (int i = 3; i < pixels.Rgba.Length; i += 4)
        { clear |= pixels.Rgba[i] == 0; visible |= pixels.Rgba[i] > 128; }
        check(clear && visible, "The same staged actor exports its evaluated current pose on a transparent PNG canvas");
    }
}
