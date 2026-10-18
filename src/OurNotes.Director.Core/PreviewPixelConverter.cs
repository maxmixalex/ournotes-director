using System.Numerics;
using System.Runtime.InteropServices;

namespace OurNotes.Director.Core;

/// <summary>Display-only conversion. Export buffers remain straight-alpha RGBA.</summary>
public static class PreviewPixelConverter
{
    public static void CopyBgraPremultiplied(Pixels pixels,Span<byte> bgra)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        int stride=checked(pixels.Width*4);
        if(pixels.ByteLength!=checked(stride*pixels.Height)||bgra.Length!=pixels.ByteLength)
            throw new ArgumentException("RGBA and BGRA buffers must contain the same complete image.");
        for(int y=0;y<pixels.Height;y++)CopyBgraPremultiplied(pixels.GetRgbaRow(y),bgra.Slice(y*stride,stride));
    }
    public static void CopyBgraPremultiplied(ReadOnlySpan<byte> rgba,Span<byte> bgra)
    {
        if(rgba.Length%4!=0||bgra.Length!=rgba.Length)throw new ArgumentException("RGBA and BGRA buffers must contain the same whole pixels.");
        int i=0;
        if(Vector.IsHardwareAccelerated&&BitConverter.IsLittleEndian)
        {
            var input=MemoryMarshal.Cast<byte,uint>(rgba);var output=MemoryMarshal.Cast<byte,uint>(bgra);
            var mask=new Vector<uint>(255);var rounding=new Vector<uint>(127);var one=Vector<uint>.One;
            int end=input.Length-Vector<uint>.Count;
            for(;i<=end;i+=Vector<uint>.Count)
            {
                var value=new Vector<uint>(input.Slice(i));var alpha=value>>24;
                var r=value&mask;var g=(value>>8)&mask;var b=(value>>16)&mask;
                if(!Vector.EqualsAll(alpha,mask))
                {
                    r=r*alpha+rounding;g=g*alpha+rounding;b=b*alpha+rounding;
                    // Exact integer division by 255 over the 0..65152 input range.
                    r=(r+one+(r>>8))>>8;g=(g+one+(g>>8))>>8;b=(b+one+(b>>8))>>8;
                }
                (b|(g<<8)|(r<<16)|(alpha<<24)).CopyTo(output.Slice(i));
            }
            i*=4;
        }
        for(;i<rgba.Length;i+=4)
        {
            int a=rgba[i+3];bgra[i]=(byte)((rgba[i+2]*a+127)/255);
            bgra[i+1]=(byte)((rgba[i+1]*a+127)/255);bgra[i+2]=(byte)((rgba[i]*a+127)/255);bgra[i+3]=(byte)a;
        }
    }
}
