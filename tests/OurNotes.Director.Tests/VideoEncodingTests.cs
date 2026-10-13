using System.Text.Json.Nodes;
using OurNotes.Director.Core;

public static class VideoEncodingTests
{
    public static void Run(Action<bool, string> assert)
    {
        void Reject(Action action, string description, string? contains = null)
        {
            try { action(); }
            catch (ArgumentException ex) { assert(contains is null || ex.Message.Contains(contains, StringComparison.Ordinal), description); return; }
            throw new Exception("Expected rejection: " + description);
        }

        VideoEncoding.Validate("cineform", 1920, 1080);
        VideoEncoding.Validate("cfhd", 1440, 2160, "high");
        VideoEncoding.ValidateOutputPath("cineform", "transparent.MOV");
        assert(true, "CineForm accepts standard HD and portrait dimensions without resizing");
        Reject(() => VideoEncoding.Validate("cineform", 1080, 1920), "portrait CineForm width is rejected with a supported suggestion", "1088x1920");
        Reject(() => VideoEncoding.Validate("cineform", 1920, 1081), "CineForm enforces Adobe's eight-row alignment", "1920x1080");
        Reject(() => VideoEncoding.Validate("cineform", 16, 24), "CineForm rejects images shorter than the wavelet encoder supports", "16x32");
        Reject(() => VideoEncoding.Validate("cineform", 64, 64, "16"), "CineForm rejects a generic CRF or qscale used as quality");
        Reject(() => VideoEncoding.ValidateOutputPath("cineform", "transparent.mp4"), "CineForm rejects an incompatible output extension");
        Reject(() => VideoEncoding.RawVideoStartInfo("ffmpeg", "cineform", 65, 64, 30, "temporary"), "raw video rejects unsupported dimensions before starting FFmpeg");
        Reject(() => VideoEncoding.RawVideoStartInfo("ffmpeg", "cineform", 64, 64, double.NaN, "temporary"), "raw video rejects nonfinite frame rates");
        assert(VideoEncoding.RawVideoThreadLimit(1920, 1080) is null && VideoEncoding.RawVideoThreadLimit(4096, 2160) is null,
            "normal HD and DCI 4K exports retain automatic encoder parallelism");
        assert(VideoEncoding.RawVideoThreadLimit(5120, 2880) == 2 && VideoEncoding.RawVideoThreadLimit(7680, 4320) == 1
            && VideoEncoding.RawVideoThreadLimit(6000, 9000) == 1,
            "larger artwork bounds encoder/filter frame buffers by actual pixel count");
        var large = VideoEncoding.RawVideoStartInfo("ffmpeg", "cineform", 6000, 9000, 30, "native.mov").ArgumentList.ToArray();
        string? Option(string name) { int i = Array.IndexOf(large, name); return i >= 0 && i + 1 < large.Length ? large[i + 1] : null; }
        assert(Option("-threads:v") == "1" && Option("-filter_threads") == "1" && Option("-video_size") == "6000x9000"
            && Option("-quality") == "film1" && Option("-vf") == "format=rgba64le",
            "54 MP CineForm uses bounded buffers without resizing, reducing quality or changing alpha conversion");
        var emptyFailure = VideoEncoding.EncoderFailure(137, "");
        var detailedFailure = VideoEncoding.EncoderFailure(7, "encoder-specific failure");
        assert(emptyFailure.Message.Contains("137", StringComparison.Ordinal) && emptyFailure.Message.Contains("not completed", StringComparison.Ordinal)
            && !emptyFailure.Message.Contains("memory", StringComparison.OrdinalIgnoreCase) && detailedFailure.Message.Contains("encoder-specific failure", StringComparison.Ordinal),
            "encoder failures always identify the exit code, preserve stderr and do not guess their cause");

        var path = Path.Combine(Path.GetTempPath(), "ournotes-encoding-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var document = new JsonObject
            {
                ["format"] = DirectorProject.Format, ["version"] = 1,
                ["source"] = new JsonObject { ["adv"] = "fixture" },
                ["output"] = new JsonObject { ["codec"] = "cineform", ["cineformQuality"] = "high", ["width"] = 1920, ["height"] = 1080 }
            };
            File.WriteAllText(path, document.ToJsonString());
            var project = DirectorProject.Read(path);
            assert(Archive.S(project.Output, "codec") == "cineform" && Archive.S(project.Output, "cineformQuality") == "high",
                "unified projects retain the CineForm codec and quality selection");
            document["output"]!["cineformQuality"] = "ultra";
            File.WriteAllText(path, document.ToJsonString());
            Reject(() => DirectorProject.Read(path), "invalid project CineForm quality fails before loading native assets");
        }
        finally { File.Delete(path); }
    }
}
