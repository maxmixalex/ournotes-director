using System.Text.Json.Nodes;
using OurNotes.Director.Core;

namespace OurNotes.Director.Desktop.Core;

/// <summary>A non-destructive render override. The source program and authored timeline are
/// retained verbatim so a current-frame image includes its evaluated expression and lip sync.</summary>
public static class SceneAssetExport
{
    public static (int Width, int Height) Canvas(JsonNode metadata, string codec = "ffv1")
    {
        static int Dimension(JsonNode? value)
        {
            if (!double.TryParse(value?.ToString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number) || number < 16)
                throw new ArgumentException("角色缺少有效的原始画布尺寸。");
            return checked((int)Math.Ceiling(number));
        }
        int width = Dimension(metadata["canvas"]?["width"]), height = Dimension(metadata["canvas"]?["height"]);
        if (VideoEncoding.NormalizeCodec(codec) == "cineform")
        { width = checked((width + 15) / 16 * 16); height = Math.Max(32, checked((height + 7) / 8 * 8)); }
        RenderDimensions.Validate(width, height, 1);
        return (width, height);
    }

    public static SceneDocument Isolate(SceneDocument source, string target, int width, int height, int supersample = 1)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Kind != SceneDocumentKind.UnifiedProject) throw new ArgumentException("请先将旧项目转换为统一场景项目。");
        if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("请选择要导出的角色。");
        RenderDimensions.Validate(width, height, supersample);
        var root = source.Root;
        var output = root["output"] as JsonObject;
        if (output is null) { output = new JsonObject(); root["output"] = output; }
        output["width"] = width; output["height"] = height; output["supersample"] = supersample;
        output["clean"] = true; output["letterbox"] = "none"; output["isolateActor"] = target;
        return SceneDocument.Parse(root.ToJsonString(DesktopSettings.JsonOptions), baseDirectory: source.BaseDirectory);
    }
}
