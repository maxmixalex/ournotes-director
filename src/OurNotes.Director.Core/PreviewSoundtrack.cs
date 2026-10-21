using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;

namespace OurNotes.Director.Core;

/// <summary>The presentation frame is a follower of this transport, never the audio producer.</summary>
public interface IPreviewPlaybackClock
{
    double? PositionSeconds { get; }
    void Start(double seconds);
    void Pause();
    /// <summary>Resume queued samples without seeking back to the last video frame.</summary>
    bool Resume() => false;
}

/// <summary>Seekable PCM from the same interpreter and mixer used for export. A separate
/// producer stays ahead of playback, so a slow draw cannot truncate or repeat a syllable.
/// Samples are spooled to disk, not retained as a second in-memory copy of every cue.</summary>
public sealed class PreviewSoundtrack : IAsyncDisposable
{
    public const int BytesPerSampleFrame = 4; // signed 16-bit, stereo, 48 kHz
    readonly CancellationTokenSource cancellation = new();
    readonly AutoResetEvent demand = new(false);
    readonly SafeFileHandle file;
    readonly string path;
    readonly Task producer;
    long available, requested;
    int completed, disposed;
    Exception? failure;
    public long AvailableSamples => Interlocked.Read(ref available);
    public bool IsComplete => Volatile.Read(ref completed) != 0;
    public Exception? Failure => Volatile.Read(ref failure);

    public PreviewSoundtrack(ScenePreviewSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!double.IsFinite(source.Duration) || source.Duration <= 0 || source.Duration > 36000)
            throw new ArgumentOutOfRangeException(nameof(source));
        // Freeze the document now. Subsequent edits cannot mutate a running audio producer.
        var document = DirectorProject.Document(source.ProjectPath).DeepClone();
        var project = DirectorProject.Read(source.ProjectPath);
        path = NativeTemporary.Create("preview-soundtrack", ".pcm");
        file = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);
        requested = AdvAudio.SampleRate * 2L;
        producer = Task.Factory.StartNew(() => Produce(source, document, project),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public void RequestThrough(long samples)
    {
        samples = Math.Clamp(samples, 0, 36000L * AdvAudio.SampleRate);
        while (true)
        {
            long previous = Interlocked.Read(ref requested);
            if (samples <= previous) break;
            if (Interlocked.CompareExchange(ref requested, samples, previous) == previous) break;
        }
        if (Volatile.Read(ref disposed) == 0) demand.Set();
    }

    /// <returns>Sample frames read; -1 means buffering. An incomplete block is returned only at EOF.</returns>
    public int Read(long startSample, Span<byte> destination)
    {
        if (startSample < 0 || destination.Length % BytesPerSampleFrame != 0)
            throw new ArgumentOutOfRangeException(nameof(startSample));
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Failure is { } error) throw new IOException("无法准备场景语音：" + error.Message, error);
        int count = destination.Length / BytesPerSampleFrame;
        RequestThrough(startSample + count + AdvAudio.SampleRate * 2L);
        long ready = AvailableSamples - startSample;
        if (ready < count && !IsComplete) return -1;
        count = (int)Math.Min(count, Math.Max(0, ready));
        int bytes = count * BytesPerSampleFrame, read = 0;
        while (read < bytes)
        {
            int next = RandomAccess.Read(file, destination.Slice(read, bytes - read), startSample * BytesPerSampleFrame + read);
            if (next == 0) throw new EndOfStreamException("场景语音缓存不完整。");
            read += next;
        }
        return count;
    }

    void Produce(ScenePreviewSource source, JsonNode document, DirectorProject project)
    {
        try
        {
            Thread.CurrentThread.Name ??= "OurNotes audio preparation";
            cancellation.Token.ThrowIfCancellationRequested();
            if(ExternalVoiceTimeline.Supports(document))
            {
                ProduceTimedVoices(source,document); return;
            }
            using var native = new AdvStoryRenderer(new Archive(source.ArchivePath), project.Site,
                source.CubismPath, project.Adv, project.Language, source.Width, source.Height,
                source.ProjectPath, source.FfmpegPath, 1, document, timingOnly: true,cancellationToken:cancellation.Token);
            var mix = new float[AdvAudio.SamplesPerFrame * 2];
            var pcm = new byte[mix.Length * sizeof(short)];
            int limit = source.DurationMode == PreviewDurationMode.Automatic ? 30 * 36000
                : Math.Max(1,Story.Frames(source.Duration));
            for (int frame = 0; frame < limit; frame++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                while (AvailableSamples >= Interlocked.Read(ref requested))
                {
                    demand.WaitOne(100);
                    cancellation.Token.ThrowIfCancellationRequested();
                }
                native.Loop.Drain(); native.Player.ThrowIfFailed();
                native.Audio.MixFrame(mix);
                ToPcm16(mix, pcm);
                RandomAccess.Write(file, pcm, (long)frame * pcm.Length);
                Interlocked.Add(ref available, AdvAudio.SamplesPerFrame);
                if (source.DurationMode == PreviewDurationMode.Automatic && native.Player.Finished) break;
                native.Scene.AdvanceRenderState(); native.Frames.AdvanceRenderState();
                native.Ui.Chat?.AdvanceRenderState(); native.Loop.Drain(); native.Player.Step();
            }
            native.Player.ThrowIfFailed();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) { Volatile.Write(ref failure, error); }
        finally { Volatile.Write(ref completed, 1); }
    }

    void ProduceTimedVoices(ScenePreviewSource source,JsonNode document)
    {
        var timeline=new ExternalVoiceTimeline(document,Path.GetDirectoryName(Path.GetFullPath(source.ProjectPath))!,source.FfmpegPath);
        var mix=new float[AdvAudio.SamplesPerFrame*2];var pcm=new byte[mix.Length*sizeof(short)];
        int limit=source.DurationMode==PreviewDurationMode.Automatic?30*36000:Math.Max(1,Story.Frames(source.Duration));
        for(int frame=0;frame<limit;frame++)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            while(AvailableSamples>=Interlocked.Read(ref requested)){demand.WaitOne(100);cancellation.Token.ThrowIfCancellationRequested();}
            timeline.MixFrame(mix);ToPcm16(mix,pcm);RandomAccess.Write(file,pcm,(long)frame*pcm.Length);
            Interlocked.Add(ref available,AdvAudio.SamplesPerFrame);
            if(source.DurationMode==PreviewDurationMode.Automatic&&timeline.Player.Finished)break;
            timeline.Step();
        }
    }

    public static void ToPcm16(ReadOnlySpan<float> source, Span<byte> destination)
    {
        if (destination.Length != source.Length * 2) throw new ArgumentException("PCM output size mismatch.");
        var output = MemoryMarshal.Cast<byte, short>(destination);
        for (int i = 0; i < source.Length; i++)
        {
            float sample = float.IsFinite(source[i]) ? Math.Clamp(source[i], -1, 1) : 0;
            output[i] = (short)Math.Clamp((int)MathF.Round(sample * 32767), short.MinValue, short.MaxValue);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        cancellation.Cancel(); demand.Set();
        try { await producer.ConfigureAwait(false); }
        finally { file.Dispose(); demand.Dispose(); cancellation.Dispose(); NativeTemporary.Delete(path); }
    }
}
