using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

/// <summary>One preview's bounded immutable model-source and animation data.
/// Native models, JsonNodes, transforms, physics, mouths and GL resources are never
/// shared. The caller reads and validates current source files before each lookup.</summary>
internal sealed class PreviewModelCache(long limitBytes=128L*1024*1024)
{
    sealed record Entry(object Value,long Bytes,LinkedListNode<string> Node);
    readonly Dictionary<string,Entry> entries=new(StringComparer.Ordinal);
    readonly LinkedList<string> order=[];
    public long LimitBytes{get;}=limitBytes>=0?limitBytes:throw new ArgumentOutOfRangeException(nameof(limitBytes));
    public long RetainedBytes{get;private set;}
    public int Count=>entries.Count;
    public long AssetHits{get;private set;}
    public long AssetDecodes{get;private set;}
    public long StoredBytesRead{get;private set;}
    public long DecodedBytes{get;private set;}
    public long MaterializationHits{get;private set;}
    public long Materializations{get;private set;}
    public long ClipHits{get;private set;}
    public long ClipCompilations{get;private set;}
    public long CompiledClips{get;private set;}

    public byte[] ReadAsset(string asset,byte[] currentStored,long? decodedSize,Func<byte[]> decode,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StoredBytesRead+=currentStored.LongLength;
        // The content-addressed asset name identifies the expected decoded SHA;
        // the actual stored-byte SHA prevents a cached success hiding replacement
        // or corruption of the compressed file at that same pathname.
        string key="asset\0"+asset+"\0"+Archive.Hash(currentStored);
        if(TryGet<byte[]>(key,out var cached))
        {
            if(decodedSize.HasValue&&decodedSize.Value!=cached.LongLength)
                throw new InvalidDataException($"Decoded size mismatch: {asset}");
            AssetHits++;return cached;
        }
        AssetDecodes++;byte[] bytes=decode();
        cancellationToken.ThrowIfCancellationRequested();DecodedBytes+=bytes.LongLength;
        Store(key,bytes,bytes.LongLength+96,cancellationToken);return bytes;
    }

    public byte[] Materialize(string manifestEntry,Func<byte[]> materialize,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string key="parts\0"+manifestEntry;
        if(TryGet<byte[]>(key,out var cached)){MaterializationHits++;return cached;}
        Materializations++;byte[] bytes=materialize();
        cancellationToken.ThrowIfCancellationRequested();
        Store(key,bytes,bytes.LongLength+96,cancellationToken);return bytes;
    }

    public Actor.Clip[] Clips(byte[] verifiedPrefab,IReadOnlyList<string> parameterIds,Func<Actor.Clip[]> compile,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Native parameter IDs are NUL-terminated, so NUL separates IDs. Include
        // the count to distinguish no IDs from one empty ID as well. Their order
        // is part of the compiled destination indices.
        string key="clips\0"+Archive.Hash(verifiedPrefab)+"\0"+parameterIds.Count+"\0"+string.Join('\0',parameterIds);
        if(TryGet<Actor.Clip[]>(key,out var cached)){ClipHits++;return cached;}
        ClipCompilations++;var clips=compile();
        cancellationToken.ThrowIfCancellationRequested();CompiledClips+=clips.Length;
        Store(key,clips,96+clips.LongLength*IntPtr.Size+clips.Sum(clip=>clip.RetainedBytes),cancellationToken);
        return clips;
    }

    bool TryGet<T>(string key,out T value) where T:class
    {
        if(entries.TryGetValue(key,out var entry))
        {
            order.Remove(entry.Node);order.AddLast(entry.Node);value=(T)entry.Value;return true;
        }
        value=null!;return false;
    }
    void Store(string key,object value,long valueBytes,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Include conservative managed object, string and LRU-node overhead in
        // the budget, rather than claiming that payload bytes are total memory.
        long bytes=checked(valueBytes+256+key.Length*2L);
        if(bytes>LimitBytes)return;
        while(RetainedBytes>LimitBytes-bytes&&order.First is {} first)
        {
            RetainedBytes-=entries[first.Value].Bytes;entries.Remove(first.Value);order.RemoveFirst();
        }
        entries.Add(key,new(value,bytes,order.AddLast(key)));RetainedBytes+=bytes;
    }
    public Snapshot Capture()=>new(AssetHits,AssetDecodes,StoredBytesRead,DecodedBytes,
        MaterializationHits,Materializations,ClipHits,ClipCompilations,CompiledClips);
    public JsonObject DiagnosticsSince(Snapshot before)=>new()
    {
        ["assetHits"]=AssetHits-before.AssetHits,["assetDecodes"]=AssetDecodes-before.AssetDecodes,
        ["storedBytesRead"]=StoredBytesRead-before.StoredBytesRead,["decodedBytes"]=DecodedBytes-before.DecodedBytes,
        ["materializationHits"]=MaterializationHits-before.MaterializationHits,["materializations"]=Materializations-before.Materializations,
        ["clipHits"]=ClipHits-before.ClipHits,["clipCompilations"]=ClipCompilations-before.ClipCompilations,
        ["compiledClips"]=CompiledClips-before.CompiledClips,["retainedEstimatedBytes"]=RetainedBytes,
        ["entries"]=Count,["limitBytes"]=LimitBytes
    };
    internal readonly record struct Snapshot(long AssetHits,long AssetDecodes,long StoredBytesRead,long DecodedBytes,
        long MaterializationHits,long Materializations,long ClipHits,long ClipCompilations,long CompiledClips);
}
