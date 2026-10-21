using System.Diagnostics;

namespace OurNotes.Director.Core;

/// <summary>Decoded immutable model textures retained by one preview/native thread.
/// The caller re-reads and verifies actual archive bytes before every lookup. Native
/// model, pose, physics and GL resources are reconstructed independently.</summary>
internal sealed class PreviewTextureCache(long limitBytes=256L*1024*1024)
{
    readonly Dictionary<string,(Pixels Pixels,LinkedListNode<string> Node)> entries=new(StringComparer.Ordinal);
    readonly LinkedList<string> order=[];
    public long LimitBytes{get;}=limitBytes>=0?limitBytes:throw new ArgumentOutOfRangeException(nameof(limitBytes));
    public long RetainedBytes{get;private set;}
    public long Hits{get;private set;}
    public long Decodes{get;private set;}
    public long SourceBytes{get;private set;}
    public long DecodedBytes{get;private set;}
    public double DecodeMilliseconds{get;private set;}
    public int Count=>entries.Count;

    public Pixels Read(byte[] verifiedPng,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SourceBytes+=verifiedPng.Length;
        string key=Archive.Hash(verifiedPng);
        if(entries.TryGetValue(key,out var cached))
        {
            Hits++;order.Remove(cached.Node);order.AddLast(cached.Node);return cached.Pixels;
        }
        long stamp=Stopwatch.GetTimestamp();Decodes++;
        Pixels image;
        try{image=Png.Decode(verifiedPng,cancellationToken);}
        finally{DecodeMilliseconds+=Stopwatch.GetElapsedTime(stamp).TotalMilliseconds;}
        DecodedBytes+=image.ByteLength;
        if(image.ByteLength>LimitBytes)return image;
        while(RetainedBytes>LimitBytes-image.ByteLength&&order.First is {} first)
        {
            RetainedBytes-=entries[first.Value].Pixels.ByteLength;
            entries.Remove(first.Value);order.RemoveFirst();
        }
        entries.Add(key,(image,order.AddLast(key)));RetainedBytes+=image.ByteLength;
        return image;
    }
}
