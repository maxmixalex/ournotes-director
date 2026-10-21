namespace OurNotes.Director.Core;

/// <summary>Bounds of the current native drawable geometry, normalized to the output canvas.</summary>
public sealed record PreviewTarget(string Id, string Label, double X, double Y, double Width, double Height, StageDragPlane? StagePlacement = null);

/// <summary>A private project snapshot, using the same document and renderer as final scene export.</summary>
public sealed record ScenePreviewSource(string ArchivePath, string ProjectPath, string CubismPath,
    string FfmpegPath, int Width = 960, int Height = 540, double Duration = 60,
    PreviewDurationMode DurationMode = PreviewDurationMode.Fixed, bool GpuPresentation = false, int Supersample = 1,
    bool SegmentedCpuFrames = false);

public enum PreviewDurationMode { Fixed, Automatic }

/// <summary>An observed execution, including repeated visits caused by GoTo.</summary>
public sealed record NativeCommandTiming(string Section,int Index,string Command,int StartFrame,
    int? EndFrame,int Ordinal,string Key);

/// <summary>
/// Duration is present only after the real interpreter finishes. Analysis progress is
/// a source frame count, never an estimate obtained by adding command durations.
/// </summary>
public sealed record NativePlaybackTiming(string ProjectHash,bool IsComplete,int? EndFrame,
    double? Duration,IReadOnlyList<NativeCommandTiming> Commands,int AnalyzedFrame=0,string? Error=null);

public sealed record PreviewPerformance(double SimulationMilliseconds,double SceneMilliseconds,
    double ReadbackMilliseconds,double TargetMilliseconds,long AllocatedBytes,int SimulationFrames,
    PreviewRenderDiagnostics? Diagnostics=null);

/// <summary>Per-render work deltas. GC counters/pause and process allocations include
/// all managed threads; native work counters belong to the rendering thread. These
/// observations distinguish a paused-pose draw from replay, uploads or cold shaders
/// without retaining pixels or allocating one diagnostic object for each frame.</summary>
public readonly record struct PreviewRenderDiagnostics(int Width,int Height,int Supersample,
    int Gen0Collections,int Gen1Collections,int Gen2Collections,double GcPauseMilliseconds,
    long ProcessAllocatedBytes,long CompiledPrograms,double ShaderCompilationMilliseconds,
    long MeshUploads,long MeshUploadBytes,long PoseRefreshes,long MaskPasses,long ModelDraws,
    long PresentationCopies);

/// <summary>Bounded exact-image history. NativeFrame is the actual interpreter
/// position; it can differ from a cached image only during an explicit scrub.</summary>
public readonly record struct PreviewFrameCacheDiagnostics(int Frames,long RetainedBytes,long LimitBytes,
    long Hits,long Misses,double StoreMilliseconds,double RestoreMilliseconds,int NativeFrame,
    int CapturedFrames=0,double CaptureMilliseconds=0,IReadOnlyList<PreviewCachedFrameRange>? Ranges=null);
public sealed record PreviewCachedFrameRange(int First,int Last);

public sealed record PreviewLipSync(string TargetId,string Engine,bool IsActive,float MouthOpen,string? Missing);

/// <summary>Actual active native requests; fades, physics and lip pose remain renderer state.</summary>
public sealed record PreviewActorAnimation(string TargetId,string Model,string Motion,string Expression,double MotionTime);

/// <summary>Preview resolution is independent from the saved project's final export resolution.</summary>
public sealed record HomePreviewSource(string ArchivePath, HomeProject Project, string? Character = null,
    int Width = 960, int Height = 540, bool GpuPresentation = false);

/// <summary>A native rendered frame. GPU presentation carries a shared surface and dimensions,
/// with an empty Rgba buffer; pixel-mode clients retain the original complete RGBA contract.</summary>
public sealed record PreviewFrame(long Sequence, long SessionId, int Frame, double Time, Pixels Pixels,
    IReadOnlyList<PreviewTarget> Targets, double RenderMilliseconds, double FramesPerSecond,
    bool IsPlaying, string Device,double Duration=0,
    PreviewDurationMode DurationMode=PreviewDurationMode.Fixed,NativePlaybackTiming? Timing=null,
    PreviewPerformance? Performance=null,IReadOnlyList<PreviewLipSync>? LipSync=null,
    IReadOnlyList<PreviewActorAnimation>? ActorAnimations=null, GpuPreviewSurface? GpuSurface=null, string? PresentationFallback=null)
{
    public double ScenePreviewScale { get; init; } = 1;
    public PreviewInitializationDiagnostics? Initialization { get; init; }
    public bool IsCached { get; init; }
    public bool NativeStateSynchronized { get; init; } = true;
    public PreviewFrameCacheDiagnostics? FrameCache { get; init; }
    public bool IsGpuPresented => GpuSurface is not null;
    public long CpuPixelTransferBytes => Pixels.ByteLength;
    public NativePlaybackTiming? NativeTiming=>Timing;
    public bool DurationResolved=>DurationMode==PreviewDurationMode.Fixed||Timing?.IsComplete==true;
}

/// <summary>Canvas-normalized drag displacement, cumulative from the initial pointer press.</summary>
public sealed record PreviewDrag(string TargetId, double DeltaX, double DeltaY, bool IsCompleted);

/// <summary>Incremental scale factor from a wheel gesture over the selected native object.</summary>
public sealed record PreviewScale(string TargetId, double Factor, bool IsCompleted);
