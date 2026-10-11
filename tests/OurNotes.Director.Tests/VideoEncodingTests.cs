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
