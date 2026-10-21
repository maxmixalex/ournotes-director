using System.Security.Cryptography;
using OurNotes.Director.Core;

static class SegmentedPixelsTests
{
    public static void Run(Action<bool,string> check)
    {
        const int width=299,height=448;
        var data=new byte[width*height*4];new Random(291).NextBytes(data);
        var ordinary=new Pixels(width,height,data);var same=new Pixels(width,height,data);
        check(ordinary.HasContiguousRgba&&ReferenceEquals(ordinary.Rgba,data)&&ordinary==same&&ordinary.GetHashCode()==same.GetHashCode(),
            "Ordinary Pixels keeps its original array reference and record equality");
        var (w,h,bytes)=ordinary;
        check(w==width&&h==height&&ReferenceEquals(bytes,data)&&ordinary with{}==ordinary
            &&ordinary with{Rgba=(byte[])data.Clone()}!=ordinary,
            "Pixels retains positional construction, deconstruction and with-array replacement");
        var segmented=Pixels.CopySegmented(width,height,data);var clone=segmented with{};int hash=segmented.GetHashCode();
        check(segmented.ByteLength==data.Length&&!segmented.HasContiguousRgba&&segmented==clone,
            "Segmented snapshots have the complete byte count without a contiguous frame array");
        bool rowsMatch=true;
        for(int y=0;y<height;y++)rowsMatch&=segmented.GetRgbaRow(y).SequenceEqual(data.AsSpan(y*width*4,width*4));
        check(rowsMatch&&!segmented.HasContiguousRgba,"Top-down rows remain exact across every 64 KiB storage boundary without materializing RGBA");
        var actual=new byte[data.Length];var expected=new byte[data.Length];
        PreviewPixelConverter.CopyBgraPremultiplied(data,expected);PreviewPixelConverter.CopyBgraPremultiplied(segmented,actual);
        check(actual.SequenceEqual(expected)&&!segmented.HasContiguousRgba,"Segmented premultiplied BGRA matches the vectorized legacy converter exactly without joining blocks");
        using(var digest=IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            for(int y=0;y<height;y++)digest.AppendData(segmented.GetRgbaRow(y));
            check(digest.GetHashAndReset().SequenceEqual(SHA256.HashData(data))&&!segmented.HasContiguousRgba,
                "Actual pixel SHA-256 can inspect every pixel without allocating a contiguous frame");
        }
        check(segmented.ToString()==ordinary.ToString()&&!segmented.HasContiguousRgba,
            "Formatting Pixels for diagnostics does not silently materialize a large snapshot");
        var snapshot=segmented.Rgba;
        check(snapshot.SequenceEqual(data)&&!ReferenceEquals(snapshot,data)&&ReferenceEquals(snapshot,clone.Rgba)
            &&segmented==clone&&hash==segmented.GetHashCode()&&segmented with{Rgba=snapshot}==segmented,
            "Explicit Rgba materializes one permanent array shared with record copies without changing equality or hash");
        var independent=new byte[data.Length];Array.Fill(independent,(byte)13);
        for(int i=0;i<8;i++)_ = Pixels.CopySegmented(width,height,independent);
        check(snapshot.SequenceEqual(data)&&ReferenceEquals(segmented.Rgba,snapshot),
            "An externally retained Rgba array is never returned to a pool or overwritten by later snapshots");
        var delayed=Pixels.CopySegmented(width,height,data);var copy=(byte[])data.Clone();Array.Fill(data,(byte)0);
        check(delayed.Rgba.SequenceEqual(copy),"Deferred Rgba owns its source bytes and cannot observe a producer's reused readback buffer");
        var png=Pixels.CopySegmented(width,height,copy);
        check(Png.Encode(png).SequenceEqual(Png.Encode(new(width,height,copy))),"Segmented and ordinary snapshots encode byte-identical PNG files");
        var wide=Pixels.CopySegmented(16384,2,new byte[16384*2*4]);
        check(wide.GetRgbaRow(1).Length==65536&&!wide.HasContiguousRgba,"The widest supported renderer row fits an individual non-LOH block");
        bool rejected=false;try{_ = Pixels.CopySegmented(16385,1,new byte[16385*4]);}catch(ArgumentOutOfRangeException){rejected=true;}
        check(rejected,"Rows wider than the renderer limit cannot silently allocate oversized segmented blocks");
        check(new Pixels(640,480,[]).ByteLength==0,"GPU metadata-only Pixels still permits an empty CPU buffer");
    }
}
