using System.Numerics;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

/// <summary>A fixed camera-space rectangle covering every sampled frame; coordinates are clip space, Y up.</summary>
public sealed record HomeAnimationBounds(Vector2 Min, Vector2 Max, int Samples, int Fps, double PixelsPerUnit,
    IReadOnlyList<HomePartBounds> Parts)
{
    public Vector2 Size => Max - Min;
    public Vector2 Center => (Min + Max) / 2;
    public float[] Crop(float margin = .06f, float sourceAspect = 1, float outputAspect = 1)
    {
        if (!float.IsFinite(margin) || margin < 0 || margin >= .5f) throw new ArgumentOutOfRangeException(nameof(margin));
        if (!float.IsFinite(sourceAspect) || sourceAspect <= 0 || !float.IsFinite(outputAspect) || outputAspect <= 0) throw new ArgumentOutOfRangeException(nameof(sourceAspect));
        float zoom = Math.Min(2 * outputAspect * (1 - margin) / Math.Max(Size.X * sourceAspect, 1e-6f), 2 * (1 - margin) / Math.Max(Size.Y, 1e-6f));
        var matrix = AdvMatrix.Identity(); matrix[0] = zoom * sourceAspect / outputAspect; matrix[5] = zoom;
        matrix[12] = -Center.X * matrix[0]; matrix[13] = -Center.Y * zoom;
        return matrix;
    }
}

/// <summary>Bounds of one component's rendered attachments in original image pixels, not atlas page dimensions.</summary>
public sealed record HomePartBounds(string Path, string Skeleton, float SourceScale, SpineDesignBounds Design,
    Vector2 Min, Vector2 Max);

/// <summary>Native artwork resolution, expanded for full motion, a fixed margin, and the selected codec's alignment.</summary>
public sealed record HomeNativeCanvas(int Width, int Height, double Duration, HomeAnimationBounds Bounds, string Codec, float SourceAspect)
{
    public JsonObject ToJson() => new()
    {
        ["width"] = Width, ["height"] = Height, ["duration"] = Duration, ["codec"] = Codec, ["sourceAspect"] = SourceAspect,
        ["samples"] = Bounds.Samples, ["fps"] = Bounds.Fps, ["sourcePixelsPerClipUnit"] = Bounds.PixelsPerUnit,
        ["bounds"] = new JsonObject { ["minX"] = Bounds.Min.X, ["minY"] = Bounds.Min.Y, ["maxX"] = Bounds.Max.X, ["maxY"] = Bounds.Max.Y },
        ["parts"] = new JsonArray(Bounds.Parts.Select(p => (JsonNode?)new JsonObject
        {
            ["path"] = p.Path, ["skeleton"] = p.Skeleton, ["sourceScale"] = p.SourceScale,
            ["design"] = new JsonObject { ["x"] = p.Design.X, ["y"] = p.Design.Y, ["width"] = p.Design.Width, ["height"] = p.Design.Height },
            ["animation"] = new JsonObject { ["minX"] = p.Min.X, ["minY"] = p.Min.Y, ["maxX"] = p.Max.X, ["maxY"] = p.Max.Y },
        }).ToArray()),
    };
}

public sealed partial class HomeAnimation
{
    readonly Dictionary<(float Aspect, int First, int Count, int Fps), HomeAnimationBounds> sourceBounds = [];

    /// <summary>
    /// Samples the actual runtime mesh, including attachments, clipping and character slot filters.
    /// Inspection uses independent animation state, so it never resets a playing preview or alters a held pose.
    /// Source measurements are cached for repeated inspection and fitted previews of this animation.
    /// </summary>
    public HomeAnimationBounds MeasureBounds(float aspect, int firstFrame, int frameCount, int fps, CancellationToken cancellationToken = default)
    {
        if (!float.IsFinite(aspect) || aspect <= 0) throw new ArgumentOutOfRangeException(nameof(aspect));
        if (firstFrame < 0 || frameCount < 1 || fps is < 1 or > 120 || (long)firstFrame + frameCount > 36_000L * fps + 1)
            throw new ArgumentOutOfRangeException(nameof(frameCount), "Home bounds require a positive frame interval of at most ten hours.");
        var key = (aspect, firstFrame, frameCount, fps);
        cancellationToken.ThrowIfCancellationRequested();
        if (editState is null && sourceBounds.TryGetValue(key, out var cached)) return cached;
        var sampler = new HomeAnimation(Resources, Character, animationOverride, loopOverride);
        sampler.SetEditState(editState);
        var projection = sampler.Camera.ViewProjection(aspect);
        var min = new Vector2(float.PositiveInfinity); var max = new Vector2(float.NegativeInfinity);
        double density = 0;
        var perPart = new Dictionary<string, (Part Part, Vector2 Min, Vector2 Max)>(StringComparer.Ordinal);
        for (int frame = firstFrame, last = checked(firstFrame + frameCount); frame < last; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sampler.Advance(frame, fps);
            foreach (var part in sampler.Parts)
            {
                if (!sampler.TransformFor(part, frame / (double)fps).Visible) continue;
                var matrix = sampler.EditedMatrix(part, projection, aspect, frame / (double)fps);
                foreach (var batch in part.Actor.Mesh.Batches)
                    for (int vertex = 0; vertex < batch.Positions.Length; vertex += 3)
                    {
                        var position = new Vector4(batch.Positions[vertex], batch.Positions[vertex + 1], batch.Positions[vertex + 2], 1);
                        var projected = AdvMatrix.Transform(matrix, position);
                        if (projected.W <= 0) throw new NotSupportedException("Cannot fit home geometry crossing behind the scene camera; use --framing scene.");
                        var point = new Vector2(projected.X / projected.W, projected.Y / projected.W);
                        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y)) throw new InvalidDataException("Home bounds contain a non-finite projected vertex.");
                        min = Vector2.Min(min, point); max = Vector2.Max(max, point);
                        var original = new Vector2(position.X, position.Y) / part.Actor.Asset.SourceScale;
                        var bounds = perPart.GetValueOrDefault(part.Path, (part, new(float.PositiveInfinity), new(float.NegativeInfinity)));
                        perPart[part.Path] = (part, Vector2.Min(bounds.Item2, original), Vector2.Max(bounds.Item3, original));
                        // The largest singular value of the local pixel-to-camera Jacobian keeps one
                        // source pixel per output pixel on the least foreshortened axis. A ground shadow
                        // viewed nearly edge-on must not inflate the character canvas by thousands of pixels.
                        double w = projected.W, square = w * w, scale = part.Actor.Asset.SourceScale;
                        double a = (matrix[0] * w - projected.X * matrix[3]) / square * scale * aspect;
                        double b = (matrix[4] * w - projected.X * matrix[7]) / square * scale * aspect;
                        double c = (matrix[1] * w - projected.Y * matrix[3]) / square * scale;
                        double d = (matrix[5] * w - projected.Y * matrix[7]) / square * scale;
                        double trace = a * a + b * b + c * c + d * d, determinant = a * d - b * c;
                        double singular = Math.Sqrt((trace + Math.Sqrt(Math.Max(0, trace * trace - 4 * determinant * determinant))) / 2);
                        if (singular > 1e-12) density = Math.Max(density, 1 / singular);
                    }
            }
        }
        if (perPart.Count == 0) throw new InvalidDataException("No visible Spine geometry in the requested animation interval.");
        var result = new HomeAnimationBounds(min, max, frameCount, fps, density,
            perPart.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new HomePartBounds(p.Key, p.Value.Part.Actor.Asset.Name,
                p.Value.Part.Actor.Asset.SourceScale, p.Value.Part.Actor.Asset.DesignBounds, p.Value.Min, p.Value.Max)).ToArray());
        if (editState is null) sourceBounds[key] = result;
        return result;
    }

    /// <summary>Measures a single character in the real scene camera at original artwork resolution.</summary>
    public HomeNativeCanvas MeasureNativeCanvas(int fps = 60, double? duration = null, string codec = "cineform", float sourceAspect = 1, CancellationToken cancellationToken = default)
    {
        if (Character is null) throw new InvalidOperationException("原始角色尺寸需要先选择一个角色。");
        codec = VideoEncoding.NormalizeCodec(codec);
        var timing = HomeTiming.Plan(Duration, duration: duration, fps: fps, loop: AllLooping);
        var bounds = MeasureBounds(sourceAspect, 0, timing.FrameCount, fps, cancellationToken);
        if (!double.IsFinite(bounds.PixelsPerUnit) || bounds.PixelsPerUnit <= 0) throw new InvalidDataException("无法从角色的原始像素比例计算画布。");
        const double coverage = .94; // Same fixed safety margin as HomeAnimation.Fit.
        int width = checked((int)Math.Ceiling(bounds.Size.X * sourceAspect * bounds.PixelsPerUnit / coverage));
        int height = checked((int)Math.Ceiling(bounds.Size.Y * bounds.PixelsPerUnit / coverage));
        int widthUnit = codec == "cineform" ? 16 : codec == "h264" ? 2 : 1, heightUnit = codec == "cineform" ? 8 : codec == "h264" ? 2 : 1;
        width = Math.Max(16, checked((width + widthUnit - 1) / widthUnit * widthUnit));
        height = Math.Max(codec == "cineform" ? 32 : 16, checked((height + heightUnit - 1) / heightUnit * heightUnit));
        HomeRenderer.ValidateDimensions(width, height, 1);
        return new(width, height, Duration, bounds, codec, sourceAspect);
    }
}
