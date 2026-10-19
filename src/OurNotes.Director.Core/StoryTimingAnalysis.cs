using System.Text;

namespace OurNotes.Director.Core;

/// <summary>Resource-backed timing using the same ADV handlers as preview/export,
/// without first creating and drawing a disposable program-monitor session.
/// Call on a dedicated worker: native resources are created and disposed on that thread.</summary>
public static class StoryTimingAnalysis
{
    public static NativePlaybackTiming Analyze(ScenePreviewSource source,
        CancellationToken cancellationToken = default, Action<NativePlaybackTiming>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        var project = DirectorProject.Read(source.ProjectPath);
        var document = DirectorProject.Document(source.ProjectPath);
        string hash = Archive.Hash(Encoding.UTF8.GetBytes(document.ToJsonString()));
        cancellationToken.ThrowIfCancellationRequested();
        var cache = new AdvCueCache();
        using var native = new AdvStoryRenderer(new Archive(source.ArchivePath), project.Site,
            source.CubismPath, project.Adv, project.Language, source.Width, source.Height,
            source.ProjectPath, source.FfmpegPath, 1, document, true, cache, cancellationToken);
        var voices = StoryAudioPrefetch.VoiceCandidates(native.Player.Program).ToArray();
        using var prefetch = voices.Length == 0 ? null : new StoryAudioPrefetch(cache, voices, native.PrefetchAudio!, cancellationToken);
        if (prefetch is not null) native.Audio.ResolveCue = prefetch.Get;
        return native.AnalyzeTiming(hash, progress, cancellationToken);
    }
}
