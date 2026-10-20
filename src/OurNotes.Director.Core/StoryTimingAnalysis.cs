using System.Text;
using System.Diagnostics;

namespace OurNotes.Director.Core;

/// <summary>Resource-backed timing using the same ADV handlers as preview/export,
/// without first creating and drawing a disposable program-monitor session.
/// Call on a dedicated worker: native resources are created and disposed on that thread.</summary>
public static class StoryTimingAnalysis
{
    public static NativePlaybackTiming Analyze(ScenePreviewSource source,
        CancellationToken cancellationToken = default, Action<NativePlaybackTiming>? progress = null,
        Action<StoryTimingAnalysisDiagnostics>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        var phase=Stopwatch.StartNew();
        var project = DirectorProject.Read(source.ProjectPath);
        var document = DirectorProject.Document(source.ProjectPath);
        string hash = Archive.Hash(Encoding.UTF8.GetBytes(document.ToJsonString()));
        cancellationToken.ThrowIfCancellationRequested();
        var cache = new AdvCueCache();
        NativePlaybackTiming timing;
        double preparation,playback;
        int models,modelTextures,captureWaits,captureDraws;
        IReadOnlyDictionary<string,double> initialization;
        using(var native = new AdvStoryRenderer(new Archive(source.ArchivePath), project.Site,
            source.CubismPath, project.Adv, project.Language, source.Width, source.Height,
            source.ProjectPath, source.FfmpegPath, 1, document, true, cache, cancellationToken,prefetchAudio:true))
        {
            preparation=phase.Elapsed.TotalMilliseconds;phase.Restart();
            timing=native.AnalyzeTiming(hash,progress,cancellationToken);
            playback=phase.Elapsed.TotalMilliseconds;phase.Restart();
            initialization=new Dictionary<string,double>(native.InitializationTimings);
            models=native.Scene.PreloadedActorCount;modelTextures=native.Scene.PreloadedModelTextureCount;
            captureWaits=native.Scene.CaptureWaitCount;captureDraws=native.Scene.CaptureDrawCount;
        }
        diagnostics?.Invoke(new(preparation,playback,phase.Elapsed.TotalMilliseconds,initialization,
            models,modelTextures,captureWaits,captureDraws,cache.DecodeAttempts,cache.DecodedPcmBytes,cache.DecodeMilliseconds,cache.RetainedPcmBytes));
        return timing;
    }
}
