namespace OurNotes.Director.Core;

// Each renderer owns a GL thread. Simple thread-local increments record actual work
// without locks, event objects, per-draw stopwatches, or another pass over the model.
internal static class PreviewNativeWork
{
    [ThreadStatic] internal static long CompiledPrograms;
    [ThreadStatic] internal static double ShaderCompilationMilliseconds;
    [ThreadStatic] internal static long MeshUploads;
    [ThreadStatic] internal static long MeshUploadBytes;
    [ThreadStatic] internal static long PoseRefreshes;
    [ThreadStatic] internal static long MaskPasses;
    [ThreadStatic] internal static long ModelDraws;
    [ThreadStatic] internal static long PresentationCopies;

    internal static Snapshot Read()=>new(CompiledPrograms,ShaderCompilationMilliseconds,MeshUploads,
        MeshUploadBytes,PoseRefreshes,MaskPasses,ModelDraws,PresentationCopies);

    internal readonly record struct Snapshot(long CompiledPrograms,double ShaderCompilationMilliseconds,
        long MeshUploads,long MeshUploadBytes,long PoseRefreshes,long MaskPasses,long ModelDraws,
        long PresentationCopies);
}
