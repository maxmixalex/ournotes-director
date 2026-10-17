namespace OurNotes.Director.Desktop.Core;

public sealed partial class StudioSceneDocument
{
    /// <summary>Supplies the shared output dialog with a frozen native document and an
    /// explicit range. It deliberately does not call Reload, Compile or SetDuration:
    /// absolute AtFrame clips are already compiled and must never be re-timed by the
    /// old sparse-keyframe editor. Use only as an export view, not an editable project.</summary>
    public static StudioSceneDocument CreateExportView(SceneDocument source, double seconds)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!double.IsFinite(seconds) || seconds <= 0 || seconds > 36000)
            throw new ArgumentOutOfRangeException(nameof(seconds));
        int frames = checked((int)Math.Ceiling(seconds * FramesPerSecond - 1e-8));
        return new StudioSceneDocument(source, Math.Max(1, frames));
    }

    private StudioSceneDocument(SceneDocument source, int exportFrameCount)
    {
        document = SceneDocument.Parse(source.Text, source.FilePath, source.BaseDirectory);
        durationMode = StudioDurationMode.Fixed;
        durationFrames = exportFrameCount;
        playbackRangeFrames = exportFrameCount;
        timingKnown = false;
        timingMessage = "导出快照保留已编译的片段时序，不使用旧关键帧编译器。";
    }
}
