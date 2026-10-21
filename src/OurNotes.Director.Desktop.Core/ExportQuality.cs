using System.Text.Json.Nodes;
using OurNotes.Director.Core;

namespace OurNotes.Director.Desktop.Core;

/// <summary>Lossy encoder quality is independent of geometric supersampling.
/// New exports use a practical starting point; explicit project choices win.</summary>
public static class ExportQuality
{
    public const string TransparentCineFormQuality = VideoEncoding.DefaultCineFormQuality;
    public const int DefaultLive2DLongSide = 1920;

    public static (int Width, int Height) InitialLive2DCanvas(JsonNode? metadata)
    {
        var original = metadata?["canvas"] is not null ? SceneAssetExport.Canvas(metadata) : (Width: 1080, Height: 1920);
        long longest = Math.Max(original.Width, original.Height), scale = Math.Min(DefaultLive2DLongSide, longest);
        // Preserve the model's canvas proportion, then expand only to the codec's
        // pixel alignment. Integer division avoids floating rounding turning an
        // exact 1920-pixel long side into another complete 8/16-pixel block.
        int Align(int size, int alignment, int minimum) => Math.Max(minimum,
            checked((int)((size * scale + longest * alignment - 1) / (longest * alignment)) * alignment));
        int width = Align(original.Width, 16, 16), height = Align(original.Height, 8, 32);
        return (width, height);
    }

    public static IReadOnlyList<(string Value, string Label)> CineFormChoices { get; } =
        Array.AsReadOnly<(string Value, string Label)>([
            ("film3+", "最高画质 · Film 3+（文件较大）"),
            ("film3", "更高画质 · Film 3"),
            ("film2+", "Film 2+"), ("film2", "Film 2"), ("film1.5", "Film 1.5"), ("film1+", "Film 1+"),
            ("film1", "均衡 · Film 1"), ("high+", "High+"), ("high", "较小体积 · High"),
            ("medium+", "Medium+"), ("medium", "Medium"), ("low+", "Low+"), ("low", "Low")
        ]);

    public static string InitialCineFormQuality(JsonObject? output, bool transparent)
        => output?["cineformQuality"] is { } explicitQuality
            ? VideoEncoding.NormalizeCineFormQuality(explicitQuality.ToString())
            : transparent ? TransparentCineFormQuality : VideoEncoding.DefaultCineFormQuality;
}
