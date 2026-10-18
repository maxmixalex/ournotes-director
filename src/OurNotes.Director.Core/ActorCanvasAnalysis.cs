using System.Diagnostics;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

public sealed record ActorCanvasProgress(int CompletedFrames, int TotalFrames, int VisibleFrames)
{
    public double Fraction => TotalFrames == 0 ? 0 : Math.Clamp(CompletedFrames / (double)TotalFrames, 0, 1);
}

/// <summary>The union of the evaluated visible geometry throughout one native frame
/// interval, in the original isolated canvas. Bounds are never clipped to 0..1.</summary>
public sealed record ActorCanvasBounds(string ActorId, int ReferenceWidth, int ReferenceHeight,
    double X, double Y, double Width, double Height, int StartFrame, int EndFrameExclusive, int VisibleFrames)
{
    /// <summary>Keep the union's center and original pixel scale. Multiplier adds
    /// transparent room around the complete motion; codec alignment only expands it.</summary>
    public ActorCanvasFit Fit(double multiplier = 1, string codec = "ffv1")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ActorId);
        RenderDimensions.Validate(ReferenceWidth, ReferenceHeight, 1);
        if (!double.IsFinite(multiplier) || multiplier < 1)
            throw new ArgumentOutOfRangeException(nameof(multiplier), "完整动作画布倍率必须为不小于 1 的有限数值。");
        if (!double.IsFinite(X) || !double.IsFinite(Y) || !double.IsFinite(Width) || !double.IsFinite(Height)
            || Width <= 0 || Height <= 0 || VisibleFrames <= 0)
            throw new InvalidOperationException("所分析的演出区间没有可见角色，无法确定完整动作画布。");
        codec = VideoEncoding.NormalizeCodec(codec);
        // One reference pixel on either edge retains antialiasing at the geometric
        // boundary. All later rounding and codec padding is symmetric, never a crop.
        double requestedWidth = (Width * ReferenceWidth + 2) * multiplier;
        double requestedHeight = (Height * ReferenceHeight + 2) * multiplier;
        if (!double.IsFinite(requestedWidth) || !double.IsFinite(requestedHeight)
            || requestedWidth > RenderDimensions.MaxSide || requestedHeight > RenderDimensions.MaxSide)
            throw new ArgumentException("完整动作画布超过输出尺寸上限，请减小模型缩放或画布倍率。");
        int Align(double pixels, int alignment, int minimum)
            => Math.Max(minimum, checked(((int)Math.Ceiling(pixels) + alignment - 1) / alignment * alignment));
        int width = Align(requestedWidth, codec == "cineform" ? 16 : codec == "h264" ? 2 : 1, 16);
        int height = Align(requestedHeight, codec == "cineform" ? 8 : codec == "h264" ? 2 : 1, codec == "cineform" ? 32 : 16);
        RenderDimensions.Validate(width, height, 1);
        double normalizedWidth = width / (double)ReferenceWidth, normalizedHeight = height / (double)ReferenceHeight;
        var canvas = new IsolatedActorCanvas(ReferenceWidth, ReferenceHeight,
            X + Width / 2 - normalizedWidth / 2, Y + Height / 2 - normalizedHeight / 2,
            normalizedWidth, normalizedHeight);
        return new(width, height, multiplier, canvas, ActorId);
    }
}

public sealed record ActorCanvasFit(int Width, int Height, double Multiplier, IsolatedActorCanvas Canvas, string ActorId = "")
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Canvas);
        RenderDimensions.Validate(Width, Height, 1);
        _ = ActorIsolation.ReadCanvas(ActorIsolation.CanvasJson(Canvas), "isolated");
        if (!double.IsFinite(Multiplier) || Multiplier < 1) throw new ArgumentOutOfRangeException(nameof(Multiplier));
        if (Math.Abs(Canvas.Width * Canvas.ReferenceWidth - Width) > 1e-6
            || Math.Abs(Canvas.Height * Canvas.ReferenceHeight - Height) > 1e-6)
            throw new ArgumentException("动作画布尺寸必须保持原始人物像素比例。");
    }
}

/// <summary>Uses the ordinary native command player, animation, Cubism deformation,
/// physics and audio-driven state. Each frame reads geometry, without rendering a
/// full image or issuing GPU readback. The caller owns the immutable source snapshot.</summary>
public static class ActorCanvasAnalysis
{
    public static Task<ActorCanvasBounds> AnalyzeAsync(ScenePreviewSource source, int startFrame, int endFrameExclusive,
        IProgress<ActorCanvasProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (startFrame < 0 || endFrameExclusive <= startFrame || endFrameExclusive > 30 * 36000)
            throw new ArgumentOutOfRangeException(nameof(endFrameExclusive), "动作分析需要十小时内的有效 30 fps 帧区间。");
        // The native GL context is constructed, used and disposed on this single
        // worker thread. No UI preview state, cursor or GPU presentation is changed.
        return Task.Run(() => Analyze(source, startFrame, endFrameExclusive, progress, cancellationToken), cancellationToken);
    }

    static ActorCanvasBounds Analyze(ScenePreviewSource source, int startFrame, int endFrameExclusive,
        IProgress<ActorCanvasProgress>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var project = DirectorProject.Read(source.ProjectPath);
        string actor = project.Output["isolateActor"]?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("请先选择独立 Live2D 角色，再分析完整动作画布。");
        var existing = ActorIsolation.ReadCanvas(project.Output["isolateCanvas"], actor);
        int referenceWidth = existing?.ReferenceWidth ?? project.Output["width"]?.GetValue<int>() ?? 1920;
        int referenceHeight = existing?.ReferenceHeight ?? project.Output["height"]?.GetValue<int>() ?? 1080;
        RenderDimensions.Validate(referenceWidth, referenceHeight, 1);
        var document = DirectorProject.Document(source.ProjectPath).DeepClone().AsObject();
        // Always analyze the same original coordinate system, including on a second
        // fit. Source commands, authored transforms and playback waits remain intact.
        var output = project.Output.DeepClone().AsObject();
        output["width"] = referenceWidth; output["height"] = referenceHeight; output["supersample"] = 1;
        // Do not synthesize playbackViewport here. In a legacy isolated project its
        // absence means no source letterbox wait, even if output.letterbox says game.
        // Introducing a new viewport would change the performance being measured.
        output.Remove("isolateCanvas"); document["output"] = output;
        progress?.Report(new(0, endFrameExclusive, 0));
        using var renderer = new AdvStoryRenderer(new Archive(source.ArchivePath), project.Site, source.CubismPath,
            project.Adv, project.Language, 32, 32, source.ProjectPath, source.FfmpegPath, 1, document);
        double left = double.PositiveInfinity, top = left, right = double.NegativeInfinity, bottom = right;
        int visible = 0;
        long reported = Stopwatch.GetTimestamp();
        for (int frame = startFrame; frame < endFrameExclusive; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Seeking the first sample still replays every earlier native step so
            // expression fades, sound-driven mouths and physics retain their history.
            var bounds = renderer.MeasureIsolatedBounds(frame, referenceWidth, referenceHeight, cancellationToken);
            if (bounds is not null)
            {
                if (bounds.Id != actor || !double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y)
                    || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height)
                    || bounds.Width <= 0 || bounds.Height <= 0)
                    throw new InvalidDataException("原生角色返回了无效动作范围。");
                left = Math.Min(left, bounds.X); top = Math.Min(top, bounds.Y);
                right = Math.Max(right, bounds.X + bounds.Width); bottom = Math.Max(bottom, bounds.Y + bounds.Height);
                visible++;
            }
            if (Stopwatch.GetElapsedTime(reported).TotalMilliseconds >= 150 || frame + 1 == endFrameExclusive)
            {
                progress?.Report(new(frame + 1, endFrameExclusive, visible));
                reported = Stopwatch.GetTimestamp();
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (visible == 0) throw new InvalidOperationException("所分析的演出区间没有可见角色，无法确定完整动作画布。");
        return new(actor, referenceWidth, referenceHeight, left, top, right - left, bottom - top,
            startFrame, endFrameExclusive, visible);
    }
}
