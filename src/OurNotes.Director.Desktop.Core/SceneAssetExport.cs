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
        root["output"] = ActorIsolation.Output(root["output"] as JsonObject, target, width, height, supersample);
        return SceneDocument.Parse(root.ToJsonString(DesktopSettings.JsonOptions), baseDirectory: source.BaseDirectory);
    }

    /// <summary>Apply one complete-motion frame to every still, sequence and video
    /// output, retaining the original program, playback viewport and actor transform.</summary>
    public static SceneDocument ApplyCanvasFit(SceneDocument source, ActorCanvasFit fit)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(fit);
        fit.Validate();
        if (source.Kind != SceneDocumentKind.UnifiedProject) throw new ArgumentException("请先将旧项目转换为统一场景项目。");
        var root = source.Root;
        if (root["output"] is not JsonObject output || string.IsNullOrWhiteSpace(output["isolateActor"]?.ToString()))
            throw new ArgumentException("完整动作画布仅用于独立角色项目。");
        if (fit.ActorId.Length > 0 && fit.ActorId != output["isolateActor"]!.ToString())
            throw new ArgumentException("动作画布分析属于另一角色，请重新分析当前模型。");
        output["width"] = fit.Width; output["height"] = fit.Height;
        output["isolateCanvas"] = ActorIsolation.CanvasJson(fit.Canvas);
        return SceneDocument.Parse(root.ToJsonString(DesktopSettings.JsonOptions), baseDirectory: source.BaseDirectory);
    }
}
