namespace OurNotes.Director.Core;

/// <summary>Bounds of the current native drawable geometry, normalized to the output canvas.</summary>
public sealed record PreviewTarget(string Id, string Label, double X, double Y, double Width, double Height);

/// <summary>A private project snapshot, using the same document and renderer as final scene export.</summary>
public sealed record ScenePreviewSource(string ArchivePath, string ProjectPath, string CubismPath,
    string FfmpegPath, int Width = 960, int Height = 540, double Duration = 60);

/// <summary>Preview resolution is independent from the saved project's final export resolution.</summary>
public sealed record HomePreviewSource(string ArchivePath, HomeProject Project, string? Character = null,
    int Width = 960, int Height = 540);

/// <summary>One directly read native RGBA frame; no image or video intermediary is involved.</summary>
public sealed record PreviewFrame(long Sequence, long SessionId, int Frame, double Time, Pixels Pixels,
    IReadOnlyList<PreviewTarget> Targets, double RenderMilliseconds, double FramesPerSecond,
    bool IsPlaying, string Device);

/// <summary>Canvas-normalized drag displacement, cumulative from the initial pointer press.</summary>
public sealed record PreviewDrag(string TargetId, double DeltaX, double DeltaY, bool IsCompleted);

/// <summary>Incremental scale factor from a wheel gesture over the selected native object.</summary>
public sealed record PreviewScale(string TargetId, double Factor, bool IsCompleted);
