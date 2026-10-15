using System.Runtime.InteropServices;
using OurNotes.Director.Core;

namespace OurNotes.Director.WinUI;

/// <summary>Windows device-clocked PCM playback. Only the pump owns WAVEHDRs and the
/// output handle. The render thread reads the consumed-sample clock; it never feeds audio.</summary>
internal sealed class WindowsPreviewAudio : IPreviewPlaybackClock, IAsyncDisposable
{
    const int BlockSamples = 960, BlockBytes = BlockSamples * 4, BufferCount = 3;
    const uint Done = 1, Prepared = 2, TimeSamples = 2, TimeBytes = 4;
    readonly object gate = new();
    readonly AutoResetEvent wake = new(false);
    readonly Task pump;
    readonly List<Task> retired = [];
    PreviewSoundtrack? source;
    bool wanted, closing, deviceFailed, started;
    long revision;
    double requestedTime, position, volume = 1;
    string status = "语音就绪";
    public event Action<string>? StatusChanged;
    public event Action<Exception>? Failed;
    public string Status { get { lock (gate) return status; } }
    public double Volume { get { lock (gate) return volume; } set { lock (gate) volume = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 1; } }
    public double? PositionSeconds { get { lock (gate) return source is null || deviceFailed ? null : position; } }

    public WindowsPreviewAudio() => pump = Task.Factory.StartNew(Pump, CancellationToken.None,
        TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public void Configure(ScenePreviewSource next)
    {
        var created = new PreviewSoundtrack(next);
        lock (gate)
        {
            if (closing) { Retire(created); return; }
            Retire(source); source = created; wanted = false; deviceFailed = false;
            revision++; started = false; position = requestedTime = 0; SetStatus("语音准备中"); wake.Set();
        }
    }
    public void Clear()
    {
        lock (gate)
        {
            if (closing) return;
            wanted = false; started = false; Retire(source); source = null; revision++;
            SetStatus("无场景语音"); wake.Set();
        }
    }
    void Retire(PreviewSoundtrack? old)
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
            if (closing || source is null) return;
            requestedTime = position = seconds; wanted = true; started = true; deviceFailed = false; revision++;
            source.RequestThrough((long)(seconds * AdvAudio.SampleRate) + 2 * AdvAudio.SampleRate); wake.Set();
        }
    }
    public void Pause() { lock (gate) { if (closing) return; wanted = false; wake.Set(); } }
    public bool Resume()
    {
        lock (gate)
        {
            if (closing || source is null || deviceFailed || !started) return false;
            wanted = true; wake.Set(); return true;
        }
    }
    void SetStatus(string value)
    {
        if (status == value) return; status = value;
        // Subscribers marshal to their own UI thread; no renderer state is exposed.
        try { StatusChanged?.Invoke(value); } catch { }
    }

    unsafe void Pump()
    {
        Thread.CurrentThread.Name ??= "OurNotes Windows audio output";
        nint device = 0; WaveHeader** headers = stackalloc WaveHeader*[BufferCount];
        for (int i = 0; i < BufferCount; i++) headers[i] = null;
        long activeRevision = -1, nextSample = 0, startSample = 0;
        bool paused = true;
        try
        {
            while (true)
            {
                lock (gate)
                {
                    if (closing) break;
                    try
                    {
                        if (revision != activeRevision)
                        {
                            if (device != 0) Check(waveOutReset(device), "清空音频队列");
                            activeRevision = revision;
                            startSample = nextSample = (long)Math.Round(requestedTime * AdvAudio.SampleRate);
                            position = startSample / (double)AdvAudio.SampleRate; paused = true;
                            if (device != 0) Check(waveOutPause(device), "暂停音频设备");
                        }
                        if (!wanted || source is null || deviceFailed)
                        {
                            if (device != 0 && !paused)
                            {
                                Check(waveOutPause(device), "暂停语音"); paused = true;
                                position = (startSample + Math.Min(DeviceSamples(device), nextSample - startSample)) / (double)AdvAudio.SampleRate;
                            }
                            if (source is not null && !deviceFailed) SetStatus("已暂停");
                        }
                        else
                        {
                            if (source.Failure is { } preparationError) throw new IOException("语音解码失败。", preparationError);
                            if (device == 0)
                            {
                                var format = new WaveFormat { Tag = 1, Channels = 2, SamplesPerSecond = 48000,
                                    AverageBytesPerSecond = 192000, BlockAlign = 4, BitsPerSample = 16 };
                                Check(waveOutOpen(out device, uint.MaxValue, ref format, 0, 0, 0), "打开 Windows 默认音频设备");
                                Check(waveOutPause(device), "准备语音缓冲"); paused = true;
                                for (int i = 0; i < BufferCount; i++)
                                {
                                    headers[i] = (WaveHeader*)NativeMemory.AllocZeroed((nuint)sizeof(WaveHeader));
                                    headers[i]->Data = (nint)NativeMemory.Alloc(BlockBytes);
                                    headers[i]->Length = BlockBytes;
                                    Check(waveOutPrepareHeader(device, headers[i], (uint)sizeof(WaveHeader)), "准备音频块");
                                }
                            }
                            bool queued = false, buffering = false;
                            for (int i = 0; i < BufferCount; i++)
                            {
                                var header = headers[i];
                                if ((header->Flags & 16) != 0) { queued = true; continue; } // WHDR_INQUEUE
                                var output = new Span<byte>((void*)header->Data, BlockBytes);
                                int samples = source.Read(nextSample, output);
                                if (samples < 0) { buffering = true; break; }
                                if (samples == 0) break;
                                ApplyVolume(output[..(samples * 4)], volume);
                                header->Length = checked((uint)(samples * 4));
                                Check(waveOutWrite(device, header, (uint)sizeof(WaveHeader)), "播放场景语音");
                                nextSample += samples; queued = true;
                            }
                            if (queued && paused) { Check(waveOutRestart(device), "继续场景语音"); paused = false; }
                            long consumed = DeviceSamples(device);
                            position = (startSample + Math.Min(consumed, nextSample - startSample)) / (double)AdvAudio.SampleRate;
                            SetStatus(buffering && !queued ? "正在缓冲语音…" : volume == 0 ? "静音" : "语音播放");
                        }
                    }
                    catch (Exception error)
                    {
                        wanted = false; deviceFailed = true;
                        CloseDevice(ref device, headers); paused = true;
                        SetStatus("音频不可用：" + error.Message);
                        try { Failed?.Invoke(error); } catch { }
                    }
                }
                wake.WaitOne(5);
            }
        }
        finally { CloseDevice(ref device, headers); }
    }

    static unsafe long DeviceSamples(nint device)
    {
        var time = new MultimediaTime { Type = TimeSamples };
        Check(waveOutGetPosition(device, ref time, (uint)sizeof(MultimediaTime)), "读取音频时钟");
        return time.Type switch
        {
            TimeSamples => time.Value,
            TimeBytes => time.Value / 4,
            1 => (long)time.Value * 48,
            _ => throw new IOException("音频驱动没有提供受支持的播放时钟。")
        };
    }

    static void ApplyVolume(Span<byte> bytes, double volume)
    {
        if (volume >= .9999) return;
        var values = MemoryMarshal.Cast<byte, short>(bytes);
        for (int i = 0; i < values.Length; i++) values[i] = (short)Math.Round(values[i] * volume);
    }
    static unsafe void CloseDevice(ref nint device, WaveHeader** headers)
    {
        if (device != 0) _ = waveOutReset(device);
        for (int i = 0; i < BufferCount; i++)
        {
            if (headers[i] == null) continue;
            if (device != 0 && (headers[i]->Flags & Prepared) != 0)
                _ = waveOutUnprepareHeader(device, headers[i], (uint)sizeof(WaveHeader));
            NativeMemory.Free((void*)headers[i]->Data); NativeMemory.Free(headers[i]); headers[i] = null;
        }
        if (device != 0) { _ = waveOutClose(device); device = 0; }
    }
    static void Check(uint result, string operation)
    { if (result != 0) throw new IOException($"{operation}失败（Windows 音频错误 {result}）。"); }

    public async ValueTask DisposeAsync()
    {
        lock (gate) { if (closing) return; closing = true; wanted = false; wake.Set(); }
        await pump.ConfigureAwait(false);
        Task[] pending;
        lock (gate) { Retire(source); source = null; pending = retired.ToArray(); }
        foreach (var task in pending) await task.ConfigureAwait(false);
        wake.Dispose();
    }
    [StructLayout(LayoutKind.Sequential, Pack = 2)] struct WaveFormat
    { public ushort Tag, Channels; public uint SamplesPerSecond, AverageBytesPerSecond; public ushort BlockAlign, BitsPerSample, Extra; }
    [StructLayout(LayoutKind.Sequential)] struct WaveHeader
    { public nint Data; public uint Length, Recorded; public nuint User; public uint Flags, Loops; public nint Next; public nuint Reserved; }
    [StructLayout(LayoutKind.Explicit, Size = 12)] struct MultimediaTime
    { [FieldOffset(0)] public uint Type; [FieldOffset(4)] public uint Value; }
    [DllImport("winmm.dll")] static extern uint waveOutOpen(out nint output, uint device, ref WaveFormat format, nuint callback, nuint instance, uint flags);
    [DllImport("winmm.dll")] static extern unsafe uint waveOutPrepareHeader(nint output, WaveHeader* header, uint size);
    [DllImport("winmm.dll")] static extern unsafe uint waveOutUnprepareHeader(nint output, WaveHeader* header, uint size);
    [DllImport("winmm.dll")] static extern unsafe uint waveOutWrite(nint output, WaveHeader* header, uint size);
    [DllImport("winmm.dll")] static extern uint waveOutGetPosition(nint output, ref MultimediaTime time, uint size);
    [DllImport("winmm.dll")] static extern uint waveOutPause(nint output);
    [DllImport("winmm.dll")] static extern uint waveOutRestart(nint output);
    [DllImport("winmm.dll")] static extern uint waveOutReset(nint output);
    [DllImport("winmm.dll")] static extern uint waveOutClose(nint output);
}
