using System.Diagnostics;
using System.Globalization;

namespace OurNotes.Director.Core;

// Shared by every RGBA renderer. CineForm preserves the caller's straight-alpha
// pixels: encoding does not matte, premultiply, crop, pad or resize the image.
public static class VideoEncoding
{
    public const string DefaultCineFormQuality = "film1";

    public static string NormalizeCodec(string codec) => codec.ToLowerInvariant() switch
    {
        "cfhd" or "cineform" => "cineform",
        "h264" or "prores" or "ffv1" => codec.ToLowerInvariant(),
        _ => throw new ArgumentException("--codec must be h264, cineform, prores, or ffv1")
    };

    public static string NormalizeCineFormQuality(string quality) => quality.ToLowerInvariant() switch
    {
        "film3+" or "film3" or "film2+" or "film2" or "film1.5" or "film1+" or "film1" or
        "high+" or "high" or "medium+" or "medium" or "low+" or "low" => quality.ToLowerInvariant(),
        _ => throw new ArgumentException("--cineform-quality must be film3+, film3, film2+, film2, film1.5, film1+, film1, high+, high, medium+, medium, low+, or low (default film1)")
    };

    public static void Validate(string codec, int width, int height, string cineformQuality = DefaultCineFormQuality)
    {
        codec = NormalizeCodec(codec);
        if (width <= 0 || height <= 0) throw new ArgumentException("Video dimensions must be positive");
        if (codec == "h264" && (width % 2 != 0 || height % 2 != 0)) throw new ArgumentException("H264 requires even dimensions");
        if (codec != "cineform") return;
        _ = NormalizeCineFormQuality(cineformQuality);
        // FFmpeg requires width % 16 == 0 and height >= 32. Adobe's CC 2015
        // CineForm documentation additionally requires height % 8 == 0.
        if (width % 16 != 0 || height < 32 || height % 8 != 0)
        {
            long suggestedWidth = Math.Max(16, ((long)width + 8) / 16 * 16);
            long suggestedHeight = Math.Max(32, ((long)height + 4) / 8 * 8);
            throw new ArgumentException($"CineForm for Premiere Pro 2015 requires width divisible by 16 and height divisible by 8 (at least 32). {width}x{height} is unsupported; try {suggestedWidth}x{suggestedHeight}. No automatic scaling or padding is applied.");
        }
    }

    public static void ValidateOutputPath(string codec, string path)
    {
        if (NormalizeCodec(codec) == "cineform" && !Path.GetExtension(path).Equals(".mov", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("CineForm output requires a .mov file for Premiere Pro 2015");
    }

    public static string Format(string codec) => NormalizeCodec(codec) switch
    {
        "h264" => "mp4",
        "ffv1" => "matroska",
        _ => "mov"
    };

    public static string[] Arguments(string codec, string cineformQuality = DefaultCineFormQuality) => NormalizeCodec(codec) switch
    {
        // -quality is cfhd's own 0..12 enum (smaller means higher quality), not
        // FFmpeg's generic -q:v. Use names to avoid Adobe's inverse slider scale.
        // Packed RGBA64 preserves alpha endpoints during swscale's 8 -> 12 bit
        // conversion. The direct RGBA8 -> GBRAP12 path maps alpha 0 to 8/4095,
        // which cfhd can decode as alpha 1/255 across an otherwise empty canvas.
        "cineform" => ["-vf", "format=rgba64le", "-c:v", "cfhd", "-pix_fmt", "gbrap12le", "-quality", NormalizeCineFormQuality(cineformQuality), "-tag:v", "CFHD"],
        "prores" => ["-c:v", "prores_ks", "-profile:v", "4", "-pix_fmt", "yuva444p10le", "-alpha_bits", "16"],
        "ffv1" => ["-c:v", "ffv1", "-level", "3", "-pix_fmt", "bgra"],
        _ => ["-c:v", "libx264", "-crf", "16", "-preset", "medium", "-pix_fmt", "yuv420p"]
    };

    public static ProcessStartInfo RawVideoStartInfo(string ffmpeg, string codec, int width, int height,
        double fps, string output, string cineformQuality = DefaultCineFormQuality)
    {
        Validate(codec, width, height, cineformQuality);
        if (!double.IsFinite(fps) || fps <= 0) throw new ArgumentException("Video fps must be finite and positive");
        var start = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true
        };
        // The destination may be an internal temporary file without an extension.
        // Public entry points validate the final path; the muxer is explicit here.
        foreach (var argument in new[] { "-v", "error", "-nostdin", "-y", "-f", "rawvideo", "-pixel_format", "rgba",
            "-video_size", $"{width}x{height}", "-framerate", fps.ToString("R", CultureInfo.InvariantCulture), "-i", "pipe:0", "-an" }
            .Concat(Arguments(codec, cineformQuality)).Concat(["-f", Format(codec), output])) start.ArgumentList.Add(argument);
        return start;
    }
}
