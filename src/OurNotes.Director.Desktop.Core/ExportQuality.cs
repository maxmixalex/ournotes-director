using System.Text.Json.Nodes;
using OurNotes.Director.Core;

namespace OurNotes.Director.Desktop.Core;

/// <summary>Lossy encoder quality is independent of geometric supersampling.
/// Transparent character defaults retain more faint hair and costume coverage.</summary>
public static class ExportQuality
{
    public const string TransparentCineFormQuality = "film3+";

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
