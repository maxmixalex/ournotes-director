using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Diagnostics;
using OurNotes.Director.Core;

namespace OurNotes.Director.WinUI;

/// <summary>Windows device-clocked PCM playback. Only the pump owns WAVEHDRs and the
/// output handle. The render thread reads the consumed-sample clock; it never feeds audio.</summary>
internal sealed class WindowsPreviewAudio : IPreviewPlaybackClock, IAsyncDisposable
{
    internal const int BlockSamples = 960, BufferCount = 8;
    readonly object gate = new();
    readonly AutoResetEvent wake = new(false);
    readonly Func<AutoResetEvent, int, int, IPreviewAudioDevice> openDevice;
    readonly Func<ScenePreviewSource, IPreviewPcmSource> createSource;
    Task? pump;
    readonly List<Task> retired = [];
    IPreviewPcmSource? source;
    bool wanted, closing, deviceFailed, started, activated;
    long revision;
    double requestedTime, position, volume = 1;
    long submittedSamples, pumpIterations, startRequestedAt;
    int sourceConfigurations;
    double? firstPlaybackWaitMilliseconds;
    int queuedBuffers, underruns, resets, restarts;
    long queuedSamples;
    bool buffering, outputRunning, multimediaScheduling;
    string outputDevice = "not opened";
    string status = "语音就绪";
    public event Action<string>? StatusChanged;
    public event Action<Exception>? Failed;
    public string Status { get { lock (gate) return status; } }
    public double Volume { get { lock (gate) return volume; } set { lock (gate) volume = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 1; } }
    public double? PositionSeconds { get { lock (gate) return source is null || deviceFailed ? null : position; } }

    // No audio worker is started for an empty or silent workbench.
    public WindowsPreviewAudio() : this((signal, count, samples) => new WinMmPreviewAudioDevice(signal, count, samples),
        next => new SoundtrackPcmSource(next)) { }

    internal WindowsPreviewAudio(Func<AutoResetEvent, int, int, IPreviewAudioDevice> openDevice,
        Func<ScenePreviewSource, IPreviewPcmSource> createSource)
    { this.openDevice = openDevice; this.createSource = createSource; }

    internal JsonObject Diagnostics()
    {
        lock (gate) return new JsonObject
        {
            ["schema"] = "ournotes-director/preview-audio/1", ["device"] = outputDevice,
            ["sampleRate"] = AdvAudio.SampleRate, ["channels"] = 2, ["bitsPerSample"] = 16,
            ["blockSampleFrames"] = BlockSamples, ["bufferCapacity"] = BufferCount,
            ["queuedBuffers"] = queuedBuffers, ["queuedSampleFrames"] = queuedSamples,
            ["submittedSampleFrames"] = submittedSamples, ["positionSeconds"] = position,
            ["availableSampleFrames"] = source?.AvailableSamples ?? 0, ["sourceComplete"] = source?.IsComplete ?? false,
            ["sourceConfigurations"] = sourceConfigurations, ["firstPlaybackWaitMilliseconds"] = firstPlaybackWaitMilliseconds,
            ["buffering"] = buffering, ["outputRunning"] = outputRunning, ["wanted"] = wanted,
            ["activated"] = activated,
            ["underruns"] = underruns, ["deviceResets"] = resets, ["deviceRestarts"] = restarts,
            ["pumpIterations"] = pumpIterations, ["multimediaScheduling"] = multimediaScheduling,
            ["failed"] = deviceFailed, ["status"] = status
        };
    }

    public void Configure(ScenePreviewSource next, bool prepareOnly = false)
    {
        var created = createSource(next);
        lock (gate)
        {
            if (closing) { Retire(created); return; }
            Retire(source); source = created; wanted = false; deviceFailed = false;
            sourceConfigurations++; activated = !prepareOnly; firstPlaybackWaitMilliseconds = null;
            pump ??= Task.Factory.StartNew(Pump, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            revision++; started = false; position = requestedTime = 0; SetStatus("语音准备中"); wake.Set();
        }
    }
    public void Clear()
    {
        lock (gate)
        {
            if (closing) return;
            wanted = false; started = activated = false; Retire(source); source = null; revision++;
            SetStatus("无场景语音"); wake.Set();
        }
    }
    // Loading may prepare PCM while an old renderer command is still finishing.
    // Its Start/Resume must never activate an unaccepted candidate soundtrack.
    public void Activate() { lock (gate) { if (!closing && source is not null) { activated = true; wake.Set(); } } }
    void Retire(IPreviewPcmSource? old)
    {
        if (old is null) return;
        retired.RemoveAll(task => task.IsCompleted);
        retired.Add(old.DisposeAsync().AsTask());
    }
    public void Start(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0 || seconds > 36000) throw new ArgumentOutOfRangeException(nameof(seconds));
        lock (gate)
        {
            if (closing || source is null || !activated) return;
            requestedTime = position = seconds; wanted = true; started = true; deviceFailed = false; revision++;
            startRequestedAt = Stopwatch.GetTimestamp(); firstPlaybackWaitMilliseconds = null;
            source.RequestThrough((long)(seconds * AdvAudio.SampleRate) + 2 * AdvAudio.SampleRate); wake.Set();
        }
    }
    public void Pause() { lock (gate) { if (closing) return; wanted = false; wake.Set(); } }
    public bool Resume()
    {
        lock (gate)
        {
            if (closing || source is null || !activated || deviceFailed || !started) return false;
            wanted = true; wake.Set(); return true;
        }
    }
    void SetStatus(string value)
    {
        if (status == value) return; status = value;
        // Subscribers marshal to their own UI thread; no renderer state is exposed.
        try { StatusChanged?.Invoke(value); } catch { }
    }

    void Pump()
    {
        Thread.CurrentThread.Name ??= "OurNotes Windows audio output";
        IPreviewAudioDevice? device = null;
        long activeRevision = -1, nextSample = 0, startSample = 0;
        bool paused = true, prebuffer = true;
        try
        {
            while (true)
            {
                int wait = Timeout.Infinite;
                lock (gate)
                {
                    if (closing) break;
                    try
                    {
                        if (revision != activeRevision)
                        {
                            if (device is not null) { device.Reset(); resets++; device.Pause(); }
                            activeRevision = revision;
                            startSample = nextSample = (long)Math.Round(requestedTime * AdvAudio.SampleRate);
                            position = startSample / (double)AdvAudio.SampleRate; paused = prebuffer = true;
                            queuedBuffers = 0; queuedSamples = 0; buffering = outputRunning = false;
                        }
                        if (!wanted || source is null || deviceFailed)
                        {
                            if (device is not null && !paused)
                            {
                                device.Pause(); paused = true;
                                ReadPosition();
                            }
                            outputRunning = false;
                            if (source is not null && !deviceFailed)
                            {
                                if (source.Failure is { } error) throw new IOException("语音解码失败。", error);
                                bool preparing = !source.IsComplete && source.AvailableSamples < BlockSamples * BufferCount;
                                SetStatus(preparing ? "语音准备中" : "已暂停");
                                if (preparing) wait = 25;
                            }
                        }
                        else
                        {
                            wait = 5;
                            if (source.Failure is { } preparationError) throw new IOException("语音解码失败。", preparationError);
                            if (device is null)
                            {
                                device = openDevice(wake, BufferCount, BlockSamples);
                                outputDevice = device.Name; multimediaScheduling = device.MultimediaScheduling;
                                device.Pause(); paused = prebuffer = true;
                            }

                            // All submitted buffers were consumed, but the source has more
                            // audio. Do not restart every newly available 20 ms fragment.
                            // Rebase a stopped device clock at the last consumed sample and
                            // fill the queue again; no source samples are skipped or replayed.
                            // A user pause may win the race with detecting an exhausted
                            // queue, so an empty paused device needs this recovery too.
                            if (CountQueued() == 0 && nextSample > startSample
                                && !(source.IsComplete && nextSample >= source.AvailableSamples))
                            {
                                device.Reset(); resets++; device.Pause(); paused = prebuffer = true;
                                startSample = nextSample; position = startSample / (double)AdvAudio.SampleRate;
                                underruns++;
                            }

                            bool sourceWaiting = false, end = false;
                            for (int i = 0; i < BufferCount; i++)
                            {
                                if (device.IsQueued(i)) continue;
                                var output = device.Buffer(i);
                                int samples = source.Read(nextSample, output);
                                if (samples < 0) { sourceWaiting = true; break; }
                                if (samples == 0) { end = true; break; }
                                int bytes = checked(samples * PreviewSoundtrack.BytesPerSampleFrame);
                                ApplyVolume(output[..bytes], volume);
                                device.Submit(i, bytes);
                                nextSample += samples; submittedSamples += samples;
                            }
                            queuedBuffers = CountQueued();
                            // A complete short ending may be smaller than the normal
                            // 160 ms reserve. A user pause retains its existing queue.
                            if (queuedBuffers > 0 && paused && (!prebuffer || queuedBuffers == BufferCount || end))
                            {
                                device.Restart(); restarts++; paused = prebuffer = false;
                                firstPlaybackWaitMilliseconds ??= Stopwatch.GetElapsedTime(startRequestedAt).TotalMilliseconds;
                            }
                            ReadPosition();
                            buffering = sourceWaiting || prebuffer && !end;
                            outputRunning = !paused;
                            if (end && queuedBuffers == 0)
                            {
                                if (!paused) { device.Pause(); paused = true; }
                                // WHDR_DONE proves the final partial block was consumed.
                                // A driver's millisecond clock can round that boundary
                                // down; do not leave the video waiting forever at EOF.
                                position = nextSample / (double)AdvAudio.SampleRate; queuedSamples = 0;
                                outputRunning = false; SetStatus("语音结束"); wait = Timeout.Infinite;
                            }
                            else SetStatus(prebuffer ? "正在缓冲语音…" : volume == 0 ? "静音" : "语音播放");
                        }
                    }
                    catch (Exception error)
                    {
                        wanted = false; deviceFailed = true;
                        device?.Dispose(); device = null; paused = prebuffer = true;
                        queuedBuffers = 0; queuedSamples = 0; buffering = outputRunning = false;
                        SetStatus("音频不可用：" + error.Message);
                        try { Failed?.Invoke(error); } catch { }
                    }
                    pumpIterations++;
                }
                wake.WaitOne(wait);
            }
        }
        finally { device?.Dispose(); }

        int CountQueued()
        {
            int count = 0;
            for (int i = 0; i < BufferCount; i++) if (device!.IsQueued(i)) count++;
            return count;
        }
        void ReadPosition()
        {
            long consumed = Math.Clamp(device!.PositionSamples, 0, nextSample - startSample);
            position = (startSample + consumed) / (double)AdvAudio.SampleRate;
            queuedSamples = nextSample - startSample - consumed;
        }
    }

    static void ApplyVolume(Span<byte> bytes, double volume)
    {
        if (volume >= .9999) return;
        var values = MemoryMarshal.Cast<byte, short>(bytes);
        for (int i = 0; i < values.Length; i++) values[i] = (short)Math.Round(values[i] * volume);
    }
    public async ValueTask DisposeAsync()
    {
        lock (gate) { if (closing) return; closing = true; wanted = false; wake.Set(); }
        if (pump is not null) await pump.ConfigureAwait(false);
        Task[] pending;
        lock (gate) { Retire(source); source = null; pending = retired.ToArray(); }
        foreach (var task in pending) await task.ConfigureAwait(false);
        wake.Dispose();
    }
}
