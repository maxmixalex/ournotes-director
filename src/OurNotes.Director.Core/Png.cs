using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Text;

namespace OurNotes.Director.Core;

public static class Png
{
    static int Int(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadInt32BigEndian(b);
    public static Pixels Decode(byte[] bytes,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!bytes.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10})) throw new InvalidDataException("Not PNG");
        int w=0,h=0,type=0; byte[]? palette=null,alpha=null;
        using var packed = new MemoryStream();
        for (int p=8;p<bytes.Length;)
        {
            var n=Int(bytes.AsSpan(p,4)); var tag=Encoding.ASCII.GetString(bytes,p+4,4); var data=bytes.AsSpan(p+8,n);
            if(tag=="IHDR") { w=Int(data); h=Int(data[4..]); if(data[8]!=8 || data[12]!=0) throw new NotSupportedException("PNG requires 8-bit noninterlaced input"); type=data[9]; }
            if(tag=="IDAT") packed.Write(data);
            if(tag=="PLTE") palette=data.ToArray(); if(tag=="tRNS") alpha=data.ToArray();
            p+=n+12;
        }
        int bpp=type switch {0=>1,2=>3,3=>1,4=>2,6=>4,_=>throw new NotSupportedException("PNG color type")};
        packed.Position=0; using var z=new ZLibStream(packed,CompressionMode.Decompress);
        int stride=checked(w*bpp);var scan=new byte[checked(stride*h)];
        for(int y=0;y<h;y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int filter=z.ReadByte();if(filter<0)throw new EndOfStreamException();
            var row=scan.AsSpan(y*stride,stride);z.ReadExactly(row);
            Unfilter(row,y==0?[]:scan.AsSpan((y-1)*stride,stride),bpp,filter);
        }
        if(type==6)return new(w,h,scan);
        var rgba=new byte[checked(w*h*4)];
        for(int i=0;i<w*h;i++) { int s=i*bpp,d=i*4; if(type==3) {int k=scan[s]; for(int j=0;j<3;j++) rgba[d+j]=palette![k*3+j]; rgba[d+3]=alpha is not null&&k<alpha.Length?alpha[k]:(byte)255;} else {rgba[d]=scan[s];rgba[d+1]=type is 0 or 4?scan[s]:scan[s+1];rgba[d+2]=type is 0 or 4?scan[s]:scan[s+2];rgba[d+3]=type==6?scan[s+3]:type==4?scan[s+1]:(byte)255;} }
        return new(w,h,rgba);
    }
    static void Unfilter(Span<byte> row,ReadOnlySpan<byte> previous,int bpp,int filter)
    {
        switch(filter)
        {
            case 0:return;
            case 1:
                for(int x=bpp;x<row.Length;x++)row[x]=unchecked((byte)(row[x]+row[x-bpp]));
                return;
            case 2:
                if(previous.IsEmpty)return;
                int index=0;
                if(Vector.IsHardwareAccelerated)
                    for(;index<=row.Length-Vector<byte>.Count;index+=Vector<byte>.Count)
                        (new Vector<byte>(row[index..])+new Vector<byte>(previous[index..])).CopyTo(row[index..]);
                for(;index<row.Length;index++)row[index]=unchecked((byte)(row[index]+previous[index]));
                return;
            case 3:
                if(previous.IsEmpty)
                {
                    for(int x=bpp;x<row.Length;x++)row[x]=unchecked((byte)(row[x]+row[x-bpp]/2));
                    return;
                }
                for(int x=0;x<Math.Min(bpp,row.Length);x++)row[x]=unchecked((byte)(row[x]+previous[x]/2));
                for(int x=bpp;x<row.Length;x++)row[x]=unchecked((byte)(row[x]+(row[x-bpp]+previous[x])/2));
                return;
            case 4:
                if(previous.IsEmpty){Unfilter(row,previous,bpp,1);return;}
                for(int x=0;x<Math.Min(bpp,row.Length);x++)row[x]=unchecked((byte)(row[x]+previous[x]));
                for(int x=bpp;x<row.Length;x++)row[x]=unchecked((byte)(row[x]+Paeth(row[x-bpp],previous[x],previous[x-bpp])));
                return;
            default:throw new InvalidDataException("PNG filter");
        }
    }
    static int Paeth(int a,int b,int c) {int p=a+b-c,pa=Math.Abs(p-a),pb=Math.Abs(p-b),pc=Math.Abs(p-c);return pa<=pb&&pa<=pc?a:pb<=pc?b:c;}
    public static byte[] Encode(Pixels image)
    {
        using var output=new MemoryStream();output.Write(new byte[]{137,80,78,71,13,10,26,10});
        void Chunk(string name,byte[] data) {Span<byte> n=stackalloc byte[4];BinaryPrimitives.WriteInt32BigEndian(n,data.Length);output.Write(n);var tag=Encoding.ASCII.GetBytes(name);output.Write(tag);output.Write(data);uint crc=0xffffffff;foreach(var b in tag.Concat(data)){crc^=b;for(int i=0;i<8;i++)crc=(crc&1)!=0?(crc>>1)^0xedb88320:crc>>1;}BinaryPrimitives.WriteUInt32BigEndian(n,~crc);output.Write(n);}
        var head=new byte[13];BinaryPrimitives.WriteInt32BigEndian(head,image.Width);BinaryPrimitives.WriteInt32BigEndian(head.AsSpan(4),image.Height);head[8]=8;head[9]=6;Chunk("IHDR",head);
        using var packed=new MemoryStream();using(var z=new ZLibStream(packed,CompressionLevel.Fastest,true)) for(int y=0;y<image.Height;y++){z.WriteByte(0);z.Write(image.Rgba,y*image.Width*4,image.Width*4);}Chunk("IDAT",packed.ToArray());Chunk("IEND",[]);return output.ToArray();
    }
}
