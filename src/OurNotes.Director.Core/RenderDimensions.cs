namespace OurNotes.Director.Core;

/// <summary>Shared bounds for final output, independent of the much smaller interactive viewport.</summary>
public static class RenderDimensions
{
    public const int MaxSide = 16384;
    public const long MaxPixels = 132_710_400;

    public static void Validate(int width, int height, int supersample = 1)
    {
        if (width is < 16 or > MaxSide || height is < 16 or > MaxSide)
            throw new ArgumentException($"画布宽高必须在 16–{MaxSide} 像素之间。");
        if (supersample is < 1 or > 4) throw new ArgumentException("超采样倍率必须在 1–4 之间。");
        if ((long)width * height * supersample * supersample > MaxPixels)
            throw new ArgumentException("超采样后的渲染画布超过约 1.32 亿像素。原始尺寸输出请选择 1 倍采样，或减小画布尺寸。");
    }
}
