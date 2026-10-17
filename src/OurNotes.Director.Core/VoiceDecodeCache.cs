namespace OurNotes.Director.Core;

/// <summary>Shares immutable decoded PCM across audio preparation and preview replay.
/// File identity includes size/mtime and the decoder. Trimming is not part of that key.</summary>
public sealed class VoiceDecodeCache
{
    public static VoiceDecodeCache Shared { get; } = new();
    readonly object gate = new();
    readonly long byteLimit;
    readonly Func<string, string, AdvCue> decode;
    readonly Dictionary<Key, Entry> entries = new();
    readonly LinkedList<Key> recent = new();
    long bytes, decodes, hits;
    readonly record struct Key(string Path, long Length, long Modified, string Decoder);
    sealed class Entry(Lazy<AdvCue> value, LinkedListNode<Key> node)
    {
        public Lazy<AdvCue> Value { get; } = value;
        public LinkedListNode<Key> Node { get; } = node;
        public long Bytes;
    }
    public VoiceDecodeCache(long byteLimit = 128L * 1024 * 1024,
        Func<string, string, AdvCue>? decoder = null)
    {
        if (byteLimit < 0) throw new ArgumentOutOfRangeException(nameof(byteLimit));
        this.byteLimit = byteLimit;
        decode = decoder ?? ((path, ffmpeg) => VoiceFile.Decode(path, 0, ffmpeg));
    }
    public long DecodeCount => Interlocked.Read(ref decodes);
    public long HitCount => Interlocked.Read(ref hits);
    public long RetainedBytes { get { lock (gate) return bytes; } }

    /// <summary>The returned PCM is shared and must not be mutated by the caller.</summary>
    public AdvCue Read(string path, string ffmpeg)
    {
        path = Path.GetFullPath(path);
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("语音文件不存在。", path);
        string Identity(string value) => OperatingSystem.IsWindows() ? value.ToUpperInvariant() : value;
        var key = new Key(Identity(path), info.Length, info.LastWriteTimeUtc.Ticks, Identity(ffmpeg));
        Entry entry;
        lock (gate)
        {
            if (entries.TryGetValue(key, out var cached))
            {
                entry = cached; recent.Remove(entry.Node); recent.AddFirst(entry.Node);
                Interlocked.Increment(ref hits);
            }
            else
            {
                var node = recent.AddFirst(key);
                entry = new(new Lazy<AdvCue>(() =>
                {
                    Interlocked.Increment(ref decodes);
                    var cue = decode(path, ffmpeg);
                    var current = new FileInfo(path);
                    if (!current.Exists || current.Length != key.Length || current.LastWriteTimeUtc.Ticks != key.Modified)
                        throw new IOException("语音文件在解码时发生变化，请重新载入。");
                    return cue;
                }, LazyThreadSafetyMode.ExecutionAndPublication), node);
                entries.Add(key, entry);
            }
        }
        AdvCue result;
        try { result = entry.Value.Value; }
        catch
        {
            lock (gate) { if (entries.GetValueOrDefault(key) == entry) Remove(key, entry); }
            throw;
        }
        lock (gate)
        {
            if (entries.GetValueOrDefault(key) == entry && entry.Bytes == 0)
            {
                entry.Bytes = result.Pcm.LongLength * sizeof(float); bytes += entry.Bytes;
                // References in active renderers stay valid after cache eviction.
                while ((bytes > byteLimit || entries.Count > 64) && recent.Last is { } oldest)
                    Remove(oldest.Value, entries[oldest.Value]);
            }
        }
        return result;
    }
    void Remove(Key key, Entry entry)
    { entries.Remove(key); recent.Remove(entry.Node); bytes -= entry.Bytes; }
}

/// <summary>Round both endpoints in the source sample clock. Computing an independent
/// float duration with Ceiling could duplicate a sample when adjacent clips are split.</summary>
public readonly record struct VoiceSampleRange(int Offset, int Count)
{
    public static VoiceSampleRange FromSeconds(double start, double duration, int totalSamples)
    {
        if (!double.IsFinite(start) || !double.IsFinite(duration) || start < 0 || duration < 0 || totalSamples <= 0)
            throw new ArgumentException("语音裁剪范围无效。");
        double first = Math.Round(start * AdvAudio.SampleRate, MidpointRounding.AwayFromZero);
        if (first >= totalSamples) throw new ArgumentException("语音入点超出文件长度。");
        int offset = checked((int)first);
        double end = duration == 0 ? totalSamples
            : Math.Round((start + duration) * AdvAudio.SampleRate, MidpointRounding.AwayFromZero);
        int last = end >= totalSamples ? totalSamples : checked((int)end);
        if (last <= offset) throw new ArgumentException("语音片段必须至少包含一个采样。");
        return new(offset, last - offset);
    }
}
