using System.Collections.Concurrent;

namespace OurNotes.Director.Core;

/// <summary>Bounded speculative PCM decoding. Native handlers still request every cue
/// themselves; a failed or oversized speculative decode is retried by that request.</summary>
internal sealed class StoryAudioPrefetch : IDisposable
{
    internal const long MaximumSpeculativeBytes = 8L * 1024 * 1024;
    readonly AdvCueCache cache;
    readonly Func<Func<long, CancellationToken, long, AdvCue>> decoderFactory;
    readonly Func<long, CancellationToken, long, AdvCue> decode;
    readonly long[] candidates;
    readonly Dictionary<long, int> positions;
    readonly BlockingCollection<long> pending;
    readonly SemaphoreSlim slots;
    readonly CancellationTokenSource cancellation;
    readonly Task[] workers;
    readonly object gate = new();
    readonly int lookAhead;
    int next, requested = -1;
    bool disposed;

    public StoryAudioPrefetch(AdvCueCache cache, IEnumerable<long> candidates,
        Func<Func<long, CancellationToken, long, AdvCue>> decoderFactory, CancellationToken cancellationToken,
        int concurrency = 4, int lookAhead = 8)
    {
        if (concurrency is < 1 or > 4 || lookAhead < concurrency || lookAhead > 32)
            throw new ArgumentOutOfRangeException(nameof(concurrency));
        this.cache = cache; this.decoderFactory = decoderFactory; decode = decoderFactory(); this.lookAhead = lookAhead;
        this.candidates = candidates.Distinct().ToArray();
        positions = this.candidates.Select((id, index) => (id, index)).ToDictionary(pair => pair.id, pair => pair.index);
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        pending = new(lookAhead); slots = new(concurrency, concurrency);
        Fill();
        workers = Enumerable.Range(0, Math.Min(concurrency, this.candidates.Length))
            .Select(_ => Task.Factory.StartNew(Work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
    }

    public AdvCue Get(long id)
    {
        cancellation.Token.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (positions.TryGetValue(id, out int index))
            {
                requested = Math.Max(requested, index);
                // A native branch can skip far ahead. Do not decode the entire
                // skipped region merely to catch up with the next actual voice.
                next = Math.Max(next, index);
            }
            Fill();
        }
        try { return cache.Get(id, () => Decode(id, long.MaxValue, decode)); }
        catch (AdvAudioPrefetchLimitException)
        {
            // A concurrent speculative request may have installed this Lazy first.
            // Size is only a prefetch budget, never a limit on the original audio.
            return cache.Get(id, () => Decode(id, long.MaxValue, decode));
        }
    }

    AdvCue Decode(long id, long limit, Func<long, CancellationToken, long, AdvCue> decoder)
    {
        slots.Wait(cancellation.Token);
        try { return decoder(id, cancellation.Token, limit); }
        finally { slots.Release(); }
    }

    void Fill()
    {
        // Called under gate, except during construction before workers exist.
        if (disposed || cancellation.IsCancellationRequested) return;
        int end = Math.Min(candidates.Length, requested + 1 + lookAhead);
        while (next < end && pending.TryAdd(candidates[next])) next++;
    }

    void Work()
    {
        try
        {
            var workerDecode = decoderFactory();
            foreach (long id in pending.GetConsumingEnumerable(cancellation.Token))
            {
                lock (gate) Fill();
                try { cache.Get(id, () => Decode(id, MaximumSpeculativeBytes, workerDecode)); }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    // A branch may never execute, or local audio may replace VoiceIDs.
                    // Only the native demand path is allowed to report a cue failure.
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; pending.CompleteAdding(); }
        cancellation.Cancel();
        try { Task.WaitAll(workers); }
        finally { pending.Dispose(); slots.Dispose(); cancellation.Dispose(); }
    }

    public static IEnumerable<long> VoiceCandidates(AdvProgram program)
    {
        foreach (var row in program.Initialize.Concat(program.Rows).Concat(program.Finalize))
        {
            if (row.Ignored || row.Command is not ("Talk" or "Voice" or "ChatTalk" or "Subtitles")
                || row.Text("AudioPath").Length > 0 || row.Data["VoiceIDs"] is not System.Text.Json.Nodes.JsonArray ids) continue;
            foreach (var id in ids)
                if (long.TryParse(id?.ToString(), System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out long value)) yield return value;
        }
    }
}

internal sealed class AdvAudioPrefetchLimitException : IOException
{
    public AdvAudioPrefetchLimitException() : base("Audio exceeds the speculative decode budget.") { }
}
