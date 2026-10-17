namespace OurNotes.Director.Desktop.Core;

/// <summary>A frame-exact half-open interval. The marked out-frame is included in the export,
/// while Duration measures the PCM/video interval, not the timestamp of its last frame.</summary>
public sealed record TimelineRange
{
    public int StartFrame { get; }
    public int EndFrameExclusive { get; }
    public int FramesPerSecond { get; }
    public int FrameCount => EndFrameExclusive - StartFrame;
    public double Start => StartFrame / (double)FramesPerSecond;
    public double End => EndFrameExclusive / (double)FramesPerSecond;
    public double LastFrameTime => (EndFrameExclusive - 1) / (double)FramesPerSecond;
    public double Duration => FrameCount / (double)FramesPerSecond;

    public TimelineRange(int startFrame, int endFrameExclusive, int framesPerSecond)
    {
        if (framesPerSecond is < 1 or > 120 || startFrame < 0 || endFrameExclusive <= startFrame
            || endFrameExclusive > 36000L * framesPerSecond)
            throw new ArgumentOutOfRangeException(nameof(endFrameExclusive), "区间至少包含一帧，且不能超出十小时。");
        StartFrame = startFrame; EndFrameExclusive = endFrameExclusive; FramesPerSecond = framesPerSecond;
    }

    public static TimelineRange Full(double lastFrameTime, int fps)
    {
        if (!double.IsFinite(lastFrameTime) || lastFrameTime < 0 || lastFrameTime > 36000 || fps is < 1 or > 120)
            throw new ArgumentOutOfRangeException(nameof(lastFrameTime));
        return new(0, Math.Min(36000 * fps, checked((int)Math.Floor(lastFrameTime * fps + 1e-8)) + 1), fps);
    }

    public TimelineRange MarkIn(double time)
    {
        int frame = FrameAt(time);
        return new(frame, Math.Max(frame + 1, EndFrameExclusive), FramesPerSecond);
    }

    public TimelineRange MarkOut(double time)
    {
        int frame = FrameAt(time);
        return new(Math.Min(StartFrame, frame), frame + 1, FramesPerSecond);
    }

    public TimelineRange ClampTo(TimelineRange full)
    {
        if (full.FramesPerSecond != FramesPerSecond) throw new ArgumentException("区间帧率发生变化。");
        int end = Math.Min(EndFrameExclusive, full.EndFrameExclusive);
        return new(Math.Min(StartFrame, end - 1), end, FramesPerSecond);
    }

    public static TimelineRange ForClip(double start, double duration, int fps, double lastFrameTime)
    {
        if (!double.IsFinite(start) || !double.IsFinite(duration) || start < 0 || start >= 36000 || duration <= 0)
            throw new ArgumentException("片段范围无效。");
        var full = Full(lastFrameTime, fps);
        int first = Math.Clamp((int)Math.Floor(start * fps + 1e-8), 0, full.EndFrameExclusive - 1);
        double endTime = Math.Min(lastFrameTime + 1d / fps, start + duration);
        int end = Math.Clamp((int)Math.Ceiling(endTime * fps - 1e-8), first + 1, full.EndFrameExclusive);
        return new(first, end, fps);
    }

    int FrameAt(double time)
    {
        if (!double.IsFinite(time) || time < 0 || time > 36000) throw new ArgumentOutOfRangeException(nameof(time));
        return Math.Min(36000 * FramesPerSecond - 1, checked((int)Math.Floor(time * FramesPerSecond + 1e-8)));
    }
}
