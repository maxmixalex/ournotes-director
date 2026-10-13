using System.Collections.Concurrent;

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
    public long LimitBytes{get;init;}=128L*1024*1024;
    public AdvCue Get(long id,Func<AdvCue> decode)
    {
        var lazy=entries.GetOrAdd(id,_=>new(decode,LazyThreadSafetyMode.ExecutionAndPublication));
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
