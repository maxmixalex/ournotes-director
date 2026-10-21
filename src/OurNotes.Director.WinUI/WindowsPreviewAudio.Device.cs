using System.Runtime.InteropServices;
using OurNotes.Director.Core;

namespace OurNotes.Director.WinUI;

// Both the real Windows device and deterministic output regressions run the same pump.
internal interface IPreviewPcmSource : IAsyncDisposable
{
    long AvailableSamples { get; }
    bool IsComplete { get; }
    Exception? Failure { get; }
    void RequestThrough(long samples);
    int Read(long startSample, Span<byte> destination);
}

internal sealed class SoundtrackPcmSource(ScenePreviewSource source) : IPreviewPcmSource
{
    readonly PreviewSoundtrack soundtrack = new(source);
    public long AvailableSamples => soundtrack.AvailableSamples;
    public bool IsComplete => soundtrack.IsComplete;
    public Exception? Failure => soundtrack.Failure;
    public void RequestThrough(long samples) => soundtrack.RequestThrough(samples);
    public int Read(long startSample, Span<byte> destination) => soundtrack.Read(startSample, destination);
    public ValueTask DisposeAsync() => soundtrack.DisposeAsync();
}

internal interface IPreviewAudioDevice : IDisposable
{
    string Name { get; }
    bool MultimediaScheduling { get; }
    long PositionSamples { get; }
    bool IsQueued(int index);
    Span<byte> Buffer(int index);
    void Submit(int index, int bytes);
    void Pause();
    void Restart();
    void Reset();
}

/// <summary>All native handles, callbacks and WAVEHDR storage belong to the audio pump.</summary>
internal sealed class WinMmPreviewAudioDevice : IPreviewAudioDevice
{
    const uint Prepared = 2, InQueue = 16, TimeSamples = 2, TimeBytes = 4, CallbackEvent = 0x50000;
    readonly nint[] headers;
    readonly int blockBytes;
    nint device, multimediaTask;
    public string Name => "Windows waveform output (48 kHz / PCM16 / stereo)";
    public bool MultimediaScheduling => multimediaTask != 0;

    public unsafe WinMmPreviewAudioDevice(AutoResetEvent wake, int bufferCount, int blockSamples)
    {
        headers = new nint[bufferCount]; blockBytes = checked(blockSamples * PreviewSoundtrack.BytesPerSampleFrame);
        try
        {
            uint taskIndex = 0;
            multimediaTask = AvSetMmThreadCharacteristicsW("Audio", ref taskIndex);
            if (multimediaTask != 0) AvSetMmThreadPriority(multimediaTask, 1);
            var format = new WaveFormat { Tag = 1, Channels = 2, SamplesPerSecond = AdvAudio.SampleRate,
                AverageBytesPerSecond = AdvAudio.SampleRate * PreviewSoundtrack.BytesPerSampleFrame,
                BlockAlign = PreviewSoundtrack.BytesPerSampleFrame, BitsPerSample = 16 };
            Check(waveOutOpen(out device, uint.MaxValue, ref format,
                (nuint)wake.SafeWaitHandle.DangerousGetHandle(), 0, CallbackEvent), "打开 Windows 默认音频设备");
            Pause();
            for (int i = 0; i < bufferCount; i++)
            {
                var header = (WaveHeader*)NativeMemory.AllocZeroed((nuint)sizeof(WaveHeader));
                headers[i] = (nint)header;
                header->Data = (nint)NativeMemory.Alloc((nuint)blockBytes);
                header->Length = (uint)blockBytes;
                Check(waveOutPrepareHeader(device, header, (uint)sizeof(WaveHeader)), "准备音频块");
            }
        }
        catch { Dispose(); throw; }
    }
    public unsafe bool IsQueued(int index) => (Volatile.Read(ref ((WaveHeader*)headers[index])->Flags) & InQueue) != 0;
    public unsafe Span<byte> Buffer(int index) => new((void*)((WaveHeader*)headers[index])->Data, blockBytes);
    public unsafe void Submit(int index, int bytes)
    {
        if (bytes <= 0 || bytes > blockBytes || bytes % PreviewSoundtrack.BytesPerSampleFrame != 0)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        var header = (WaveHeader*)headers[index];
        if (IsQueued(index)) throw new InvalidOperationException("音频设备仍在使用此缓冲。");
        header->Length = (uint)bytes;
        Check(waveOutWrite(device, header, (uint)sizeof(WaveHeader)), "播放场景语音");
    }
    public unsafe long PositionSamples
    {
        get
        {
            var time = new MultimediaTime { Type = TimeSamples };
            Check(waveOutGetPosition(device, ref time, (uint)sizeof(MultimediaTime)), "读取音频时钟");
            return time.Type switch
            {
                TimeSamples => time.Value,
                TimeBytes => time.Value / PreviewSoundtrack.BytesPerSampleFrame,
                1 => time.Value * (long)AdvAudio.SampleRate / 1000,
                _ => throw new IOException("音频驱动没有提供受支持的播放时钟。")
            };
        }
    }
    public void Pause() => Check(waveOutPause(device), "暂停场景语音");
    public void Restart() => Check(waveOutRestart(device), "继续场景语音");
    public void Reset() => Check(waveOutReset(device), "清空音频队列");
    public unsafe void Dispose()
    {
        if (device != 0) waveOutReset(device);
        for (int i = 0; i < headers.Length; i++)
        {
            var header = (WaveHeader*)headers[i];
            if (header == null) continue;
            if (device != 0 && (header->Flags & Prepared) != 0) waveOutUnprepareHeader(device, header, (uint)sizeof(WaveHeader));
            NativeMemory.Free((void*)header->Data); NativeMemory.Free(header); headers[i] = 0;
        }
        if (device != 0) { waveOutClose(device); device = 0; }
        if (multimediaTask != 0) { AvRevertMmThreadCharacteristics(multimediaTask); multimediaTask = 0; }
    }
    static void Check(uint result, string operation)
    { if (result != 0) throw new IOException($"{operation}失败（Windows 音频错误 {result}）。"); }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct WaveFormat
    {
        public ushort Tag, Channels;
        public uint SamplesPerSecond, AverageBytesPerSecond;
        public ushort BlockAlign, BitsPerSample, Extra;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct WaveHeader
    {
        public nint Data;
        public uint Length, Recorded;
        public nuint User;
        public uint Flags, Loops;
        public nint Next;
        public nuint Reserved;
    }
    [StructLayout(LayoutKind.Explicit, Size = 12)]
    struct MultimediaTime
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(4)] public uint Value;
    }
    [DllImport("winmm.dll")] static extern uint waveOutOpen(out nint output, uint device, ref WaveFormat format, nuint callback, nuint instance, uint flags);
    [DllImport("winmm.dll")] static extern unsafe uint waveOutPrepareHeader(nint output, WaveHeader* header, uint size);
    [DllImport("winmm.dll")] static extern unsafe uint waveOutUnprepareHeader(nint output, WaveHeader* header, uint size);
    [DllImport("winmm.dll")] static extern unsafe uint waveOutWrite(nint output, WaveHeader* header, uint size);
    [DllImport("winmm.dll")] static extern uint waveOutGetPosition(nint output, ref MultimediaTime time, uint size);
    [DllImport("winmm.dll")] static extern uint waveOutPause(nint output);
    [DllImport("winmm.dll")] static extern uint waveOutRestart(nint output);
    [DllImport("winmm.dll")] static extern uint waveOutReset(nint output);
    [DllImport("winmm.dll")] static extern uint waveOutClose(nint output);
    [DllImport("avrt.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] static extern nint AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);
    [DllImport("avrt.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool AvSetMmThreadPriority(nint task, int priority);
    [DllImport("avrt.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool AvRevertMmThreadCharacteristics(nint task);
}
