using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using OurNotes.Director.Core;

static class PngDecodeTests
{
    public static void Run(Action<bool,string> check)
    {
        Pixels? retained=null;byte[]? saved=null;
        foreach(int type in new[]{0,2,3,4,6})foreach(int width in new[]{1,3,17,65})foreach(int firstFilter in Enumerable.Range(0,5))
        {
            var (png,expected)=Fixture(width,11,type,firstFilter);
            var decoded=Png.Decode(png);
            check(decoded.Width==width&&decoded.Height==11&&decoded.Rgba.SequenceEqual(expected),
                $"PNG type {type}, width {width}, starting filter {firstFilter} reconstructs exact source channels and alpha");
            if(retained is null){retained=decoded;saved=decoded.Rgba.ToArray();}
        }
        check(retained!.Rgba.SequenceEqual(saved!),"Decoding subsequent PNGs never mutates a retained output");
    }

    static (byte[] Png,byte[] Expected) Fixture(int width,int height,int type,int firstFilter)
    {
        int channels=type switch{0 or 3=>1,2=>3,4=>2,6=>4,_=>throw new ArgumentOutOfRangeException(nameof(type))};
        var scan=new byte[width*height*channels];
        for(int pixel=0;pixel<width*height;pixel++)for(int channel=0;channel<channels;channel++)
            scan[pixel*channels+channel]=(byte)(type==3?(pixel*73+pixel/width*101)%8:pixel*73+channel*47+pixel/width*101);
        var palette=Enumerable.Range(0,24).Select(i=>(byte)(i*73+17)).ToArray();
        byte[] alpha=[0,1,37,127,128,254,255];
        var expected=new byte[width*height*4];
        for(int pixel=0;pixel<width*height;pixel++)
        {
            int source=pixel*channels,target=pixel*4;
            if(type==3)
            {
                int index=scan[source];Array.Copy(palette,index*3,expected,target,3);
                expected[target+3]=index<alpha.Length?alpha[index]:(byte)255;
            }
            else
            {
                expected[target]=scan[source];expected[target+1]=type is 0 or 4?scan[source]:scan[source+1];
                expected[target+2]=type is 0 or 4?scan[source]:scan[source+2];
                expected[target+3]=type==6?scan[source+3]:type==4?scan[source+1]:(byte)255;
            }
        }
        using var filtered=new MemoryStream();int stride=width*channels;
        for(int y=0;y<height;y++)
        {
            int filter=(firstFilter+y)%5;filtered.WriteByte((byte)filter);
            for(int x=0;x<stride;x++)
            {
                int a=x>=channels?scan[y*stride+x-channels]:0;
                int b=y>0?scan[(y-1)*stride+x]:0;
                int c=y>0&&x>=channels?scan[(y-1)*stride+x-channels]:0;
                int prediction=filter switch
                {
                    0=>0,1=>a,2=>b,3=>(a+b)/2,
                    4=>new[]{a,b,c}.Select((value,index)=>(value,index,distance:Math.Abs(a+b-c-value)))
                        .OrderBy(item=>item.distance).ThenBy(item=>item.index).First().value,
                    _=>throw new InvalidOperationException()
                };
                filtered.WriteByte(unchecked((byte)(scan[y*stride+x]-prediction)));
            }
        }
        using var packed=new MemoryStream();
        using(var z=new ZLibStream(packed,CompressionLevel.SmallestSize,true))z.Write(filtered.ToArray());
        using var output=new MemoryStream();output.Write(new byte[]{137,80,78,71,13,10,26,10});
        void Chunk(string name,byte[] bytes)
        {
            var length=new byte[4];BinaryPrimitives.WriteInt32BigEndian(length,bytes.Length);output.Write(length);
            byte[] tag=Encoding.ASCII.GetBytes(name);output.Write(tag);output.Write(bytes);
            uint crc=0xffffffff;
            foreach(byte item in tag.Concat(bytes)){crc^=item;for(int bit=0;bit<8;bit++)crc=(crc&1)==0?crc>>1:(crc>>1)^0xedb88320;}
            BinaryPrimitives.WriteUInt32BigEndian(length,~crc);output.Write(length);
        }
        var header=new byte[13];BinaryPrimitives.WriteInt32BigEndian(header,width);BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4),height);
        header[8]=8;header[9]=(byte)type;Chunk("IHDR",header);
        if(type==3){Chunk("PLTE",palette);Chunk("tRNS",alpha);}
        Chunk("IDAT",packed.ToArray());Chunk("IEND",[]);
        return(output.ToArray(),expected);
    }
}
