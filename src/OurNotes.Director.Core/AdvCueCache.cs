using System.Collections.Concurrent;
using System.Diagnostics;

namespace OurNotes.Director.Core;

/// <summary>
/// A bounded cache shared by one preview's playback and duration-analysis interpreters.
/// Decoded PCM is immutable; a seek does not need to start FFmpeg for the same cue again.
/// </summary>
public sealed class AdvCueCache
{
    readonly ConcurrentDictionary<long,Lazy<AdvCue>> entries=[];
    readonly object gate=new();
    readonly LinkedList<long> order=[];
    readonly Dictionary<long,LinkedListNode<long>> nodes=[];
    long bytes;
    long decodeAttempts,decodedPcmBytes,decodeTicks;
    internal long DecodeAttempts=>Interlocked.Read(ref decodeAttempts);
    internal long DecodedPcmBytes=>Interlocked.Read(ref decodedPcmBytes);
    internal double DecodeMilliseconds=>Stopwatch.GetElapsedTime(0,Interlocked.Read(ref decodeTicks)).TotalMilliseconds;
    internal long RetainedPcmBytes{get{lock(gate)return bytes;}}
    public long LimitBytes{get;init;}=128L*1024*1024;
    public AdvCue Get(long id,Func<AdvCue> decode)
    {
        AdvCue Decode()
        {
            Interlocked.Increment(ref decodeAttempts);long started=Stopwatch.GetTimestamp();
            try{var cue=decode();Interlocked.Add(ref decodedPcmBytes,(long)cue.Pcm.Length*sizeof(float));return cue;}
            finally{Interlocked.Add(ref decodeTicks,Stopwatch.GetTimestamp()-started);}
        }
        var lazy=entries.GetOrAdd(id,_=>new(Decode,LazyThreadSafetyMode.ExecutionAndPublication));
        AdvCue cue;
        try{cue=lazy.Value;}
        catch{entries.TryRemove(new KeyValuePair<long,Lazy<AdvCue>>(id,lazy));throw;}
        lock(gate)
        {
            // Another consumer may have evicted this entry while its caller was
            // decoding. The returned PCM remains valid, but is no longer retained by
            // this cache and must not be added to its LRU byte accounting again.
            if(!entries.TryGetValue(id,out var current)||!ReferenceEquals(current,lazy))return cue;
            if(nodes.Remove(id,out var old))order.Remove(old);
            else bytes+=(long)cue.Pcm.Length*sizeof(float);
            nodes[id]=order.AddLast(id);
            while(bytes>LimitBytes&&order.First is {} first)
            {
                long remove=first.Value;order.RemoveFirst();nodes.Remove(remove);
                if(entries.TryRemove(remove,out var value)&&value.IsValueCreated)bytes-=(long)value.Value.Pcm.Length*sizeof(float);
            }
        }
        return cue;
    }
}
