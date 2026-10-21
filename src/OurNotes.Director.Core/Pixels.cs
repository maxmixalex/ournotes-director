using System.Runtime.CompilerServices;
using System.Text;

namespace OurNotes.Director.Core;

/// <summary>A stable RGBA snapshot. Ordinary images retain their supplied array.
/// Interactive CPU frames can own small row blocks instead; reading Rgba then
/// materializes one permanent, private contiguous snapshot for legacy consumers.</summary>
public record Pixels(int Width,int Height,byte[] Rgba)
{
    Storage storage=new(Rgba);

    public byte[] Rgba { get=>storage.Array(); init{if(!ReferenceEquals(storage.ExistingArray,value))storage=new(value);} }
    public int ByteLength=>storage.Length;
    /// <summary>Observation only: checking storage never materializes a large array.</summary>
    public bool HasContiguousRgba=>storage.HasArray;

    /// <summary>Reads a top-down row without joining an interactive frame's blocks.</summary>
    public ReadOnlySpan<byte> GetRgbaRow(int y)
    {
        if((uint)y>=(uint)Height)throw new ArgumentOutOfRangeException(nameof(y));
        int stride=checked(Width*4);
        if(stride<0||ByteLength!=checked(stride*Height))throw new InvalidDataException("Incomplete RGBA frame.");
        return storage.Row(y,stride);
    }

    /// <summary>Copies RGBA pixels into independently owned, whole-row blocks of at
    /// most 64 KiB. No block is pooled, returned, overwritten or exposed for writing.
    /// A single row may not exceed 64 KiB (the renderer's 16384-pixel side limit).</summary>
    public static Pixels CopySegmented(int width,int height,ReadOnlySpan<byte> rgba)
        =>CopySegmented(width,height,rgba,false);

    internal static Pixels CopySegmented(int width,int height,ReadOnlySpan<byte> rgba,bool bottomUp)
        =>new(width,height,[]){storage=new(width,height,rgba,bottomUp)};

    // Preserve the original record's array-identity equality and with/deconstruction
    // behavior. A segmented snapshot has one stable identity even after Rgba is read;
    // with-copies share its storage and the eventual permanent array.
    public virtual bool Equals(Pixels? other)=>other is not null&&EqualityContract==other.EqualityContract
        &&Width==other.Width&&Height==other.Height&&ReferenceEquals(storage.Identity,other.storage.Identity);
    public override int GetHashCode()=>HashCode.Combine(EqualityContract,Width,Height,RuntimeHelpers.GetHashCode(storage.Identity));
    protected virtual bool PrintMembers(StringBuilder builder)
    {
        // Logging a record must not silently request its full pixel snapshot.
        builder.Append("Width = ").Append(Width).Append(", Height = ").Append(Height).Append(", Rgba = System.Byte[]");return true;
    }

    sealed class Storage
    {
        const int MaximumBlockBytes=64*1024;
        byte[]? contiguous;
        readonly byte[][]? blocks;
        readonly int sourceStride,rowsPerBlock;
        public int Length{get;}
        public object Identity{get;}
        public bool HasArray=>Volatile.Read(ref contiguous) is not null;
        public byte[]? ExistingArray=>Volatile.Read(ref contiguous);

        public Storage(byte[] array){ArgumentNullException.ThrowIfNull(array);contiguous=array;Length=array.Length;Identity=array;}
        public Storage(int width,int height,ReadOnlySpan<byte> rgba,bool bottomUp)
        {
            if(width<=0||height<=0||width>MaximumBlockBytes/4)throw new ArgumentOutOfRangeException(nameof(width),"Segmented RGBA requires positive dimensions and rows no wider than 64 KiB.");
            sourceStride=checked(width*4);Length=checked(sourceStride*height);
            if(rgba.Length!=Length)throw new ArgumentException("Incomplete RGBA frame.",nameof(rgba));
            rowsPerBlock=MaximumBlockBytes/sourceStride;
            blocks=new byte[checked((height+rowsPerBlock-1)/rowsPerBlock)][];Identity=this;
            for(int block=0,y=0;block<blocks.Length;block++)
            {
                int count=Math.Min(rowsPerBlock,height-y);
                var bytes=blocks[block]=GC.AllocateUninitializedArray<byte>(checked(count*sourceStride));
                for(int row=0;row<count;row++,y++)
                    rgba.Slice((bottomUp?height-1-y:y)*sourceStride,sourceStride).CopyTo(bytes.AsSpan(row*sourceStride,sourceStride));
            }
        }
        public byte[] Array()
        {
            if(Volatile.Read(ref contiguous) is {} ready)return ready;
            // No finalizer or return-to-pool path exists. Even a caller that retains
            // only this byte[] after dropping Pixels owns a stable snapshot forever.
            lock(this)
            {
                if(contiguous is {} existing)return existing;
                var result=GC.AllocateUninitializedArray<byte>(Length);int offset=0;
                foreach(var block in blocks!){block.CopyTo(result,offset);offset+=block.Length;}
                Volatile.Write(ref contiguous,result);return result;
            }
        }
        public ReadOnlySpan<byte> Row(int y,int stride)
        {
            if(Volatile.Read(ref contiguous) is {} array)return array.AsSpan(checked(y*stride),stride);
            // A positional-record with-expression may reshape an image. Retain that
            // legacy behavior, materializing only if its new rows cross old blocks.
            if(stride!=sourceStride)return Array().AsSpan(checked(y*stride),stride);
            return blocks![y/rowsPerBlock].AsSpan((y%rowsPerBlock)*stride,stride);
        }
    }
}
