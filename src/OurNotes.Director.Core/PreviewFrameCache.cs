using System.Diagnostics;
using System.IO.Compression;
using System.Buffers;

namespace OurNotes.Director.Core;

/// <summary>Exact rendered frames, never native checkpoints. The owning scene clears
/// this cache when its document, transient edits or presentation dimensions change.
/// Compressed bytes and geometry are private; each hit returns an independent image.</summary>
internal sealed class PreviewFrameCache(long limitBytes=128L*1024*1024)
{
    const int MaximumEntries=1024;
    readonly long limitBytes=limitBytes>=0?limitBytes:throw new ArgumentOutOfRangeException(nameof(limitBytes));
    readonly Dictionary<int,LinkedListNode<Entry>> entries=[];
    readonly LinkedList<Entry> recent=[];
    long retained;
    public int Count=>entries.Count;
    public long RetainedBytes=>retained;
    public long LimitBytes=>limitBytes;
    public long Hits{get;private set;}
    public long Misses{get;private set;}
    public double StoreMilliseconds{get;private set;}
    public double RestoreMilliseconds{get;private set;}
    public IReadOnlyList<PreviewCachedFrameRange> Ranges()
    {
        var ranges=new List<PreviewCachedFrameRange>();
        foreach(int number in entries.Keys.Order())
        {
            if(ranges.Count>0&&ranges[^1].Last+1==number)ranges[^1]=ranges[^1] with{Last=number};
            else ranges.Add(new(number,number));
        }
        return ranges.ToArray();
    }

    internal sealed record Frame(int Number,Pixels Pixels,double Scale,IReadOnlyList<PreviewTarget> Targets,
        IReadOnlyList<PreviewLipSync> LipSync,IReadOnlyList<PreviewActorAnimation> Animations);
    sealed record Entry(int Number,int Width,int Height,double Scale,byte[] Bytes,bool Compressed,
        PreviewTarget[] Targets,PreviewLipSync[] LipSync,PreviewActorAnimation[] Animations,long Cost);

    public void Clear(){entries.Clear();recent.Clear();retained=0;}

    public bool Contains(int number,double minimumScale)=>entries.TryGetValue(number,out var entry)&&entry.Value.Scale>=minimumScale;

    public bool TryGet(int number,double minimumScale,out Frame? frame)
    {
        frame=null;
        if(!entries.TryGetValue(number,out var node)||node.Value.Scale<minimumScale){Misses++;return false;}
        long started=Stopwatch.GetTimestamp();var entry=node.Value;
        var bytes=GC.AllocateUninitializedArray<byte>(checked(entry.Width*entry.Height*4));
        if(entry.Compressed)
        {
            if(!BrotliDecoder.TryDecompress(entry.Bytes,bytes,out int written)||written!=bytes.Length)
                throw new InvalidDataException("An in-memory preview frame is incomplete.");
        }
        else entry.Bytes.CopyTo(bytes,0);
        // StageDragPlane contains an array. Do not expose that array, or any stored
        // collection, through a public PreviewFrame supplied to an editor client.
        frame=new(number,new(entry.Width,entry.Height,bytes),entry.Scale,CopyTargets(entry.Targets),
            entry.LipSync.ToArray(),entry.Animations.ToArray());
        recent.Remove(node);recent.AddFirst(node);Hits++;
        RestoreMilliseconds+=Stopwatch.GetElapsedTime(started).TotalMilliseconds;return true;
    }

    public void Store(Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if(limitBytes==0||frame.Number<0||frame.Scale is not (1 or .5 or .25))return;
        var pixels=frame.Pixels;
        long length=(long)pixels.Width*pixels.Height*4;
        if(length<=0||length>int.MaxValue||pixels.ByteLength!=length)return; // GPU-only presentation has no CPU payload.
        if(entries.TryGetValue(frame.Number,out var old)&&old.Value.Scale>=frame.Scale)return;
        long started=Stopwatch.GetTimestamp();
        // A single frame also has a bounded temporary working set. Large canvases
        // remain renderable without allocating a second image just for this cache.
        if(length>limitBytes)return;
        var raw=ArrayPool<byte>.Shared.Rent((int)length);
        var compressed=ArrayPool<byte>.Shared.Rent(BrotliEncoder.GetMaxCompressedLength((int)length));
        byte[] bytes;bool packed;
        try
        {
            for(int y=0;y<pixels.Height;y++)pixels.GetRgbaRow(y).CopyTo(raw.AsSpan(y*pixels.Width*4,pixels.Width*4));
            packed=BrotliEncoder.TryCompress(raw.AsSpan(0,(int)length),compressed,out int size,quality:0,window:22)&&size<length;
            bytes=(packed?compressed.AsSpan(0,size):raw.AsSpan(0,(int)length)).ToArray();
        }
        finally{ArrayPool<byte>.Shared.Return(compressed);ArrayPool<byte>.Shared.Return(raw);}
        var targets=CopyTargets(frame.Targets);var lips=frame.LipSync.ToArray();var animations=frame.Animations.ToArray();
        long metadata=256L+targets.Sum(t=>192L+2L*(t.Id.Length+t.Label.Length)+(t.StagePlacement?.LocalToClip.Length??0)*4L)
            +lips.Sum(l=>96L+2L*(l.TargetId.Length+l.Engine.Length+(l.Missing?.Length??0)))
            +animations.Sum(a=>112L+2L*(a.TargetId.Length+a.Model.Length+a.Motion.Length+a.Expression.Length));
        long cost=bytes.LongLength+metadata;
        if(cost<=limitBytes)
        {
            if(old is not null)Remove(old);
            while(recent.Last is {} last&&(retained+cost>limitBytes||entries.Count>=MaximumEntries))Remove(last);
            var entry=new Entry(frame.Number,pixels.Width,pixels.Height,frame.Scale,bytes,packed,targets,lips,animations,cost);
            entries.Add(frame.Number,recent.AddFirst(entry));retained+=cost;
        }
        StoreMilliseconds+=Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    static PreviewTarget[] CopyTargets(IReadOnlyList<PreviewTarget> values)=>values.Select(value=>value.StagePlacement is {} plane
        ?value with{StagePlacement=plane with{LocalToClip=plane.LocalToClip.ToArray()}}:value).ToArray();
    void Remove(LinkedListNode<Entry> node){recent.Remove(node);entries.Remove(node.Value.Number);retained-=node.Value.Cost;}
}
