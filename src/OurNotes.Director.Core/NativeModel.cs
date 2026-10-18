using System.Runtime.InteropServices;

namespace OurNotes.Director.Core;

public sealed unsafe class NativeModel : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate uint Ver();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Check(nint p,uint size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate nint Revive(nint p,uint size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate uint Size(nint p);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate nint Init(nint p,nint m,uint size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate nint Ptr(nint p);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Count(nint p);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void UpdateFn(nint p);
    readonly nint library; void* mocMemory; void* modelMemory; nint model;
    readonly Dictionary<string,Delegate> functions=new();
    T Fn<T>(string name) where T:Delegate {if(!functions.TryGetValue(name,out var f))functions[name]=f=Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library,name));return (T)f;}
    public uint Version => Fn<Ver>("csmGetVersion")();
    /// <summary>Changes only after Cubism evaluates a new displayed pose.</summary>
    public long Revision { get; private set; }
    public string[] ParameterIds {get;private set;}=[];
    public float[] Min {get;private set;}=[]; public float[] Max {get;private set;}=[]; public float[] Defaults {get;private set;}=[];
    public int DrawableCount => Fn<Count>("csmGetDrawableCount")(model);
    public nint Pointer(string suffix)=>Fn<Ptr>("csmGet"+suffix)(model);
    public int[] Ints(string suffix,int n){var a=new int[n];Marshal.Copy(Pointer(suffix),a,0,n);return a;}
    public float[] Floats(string suffix,int n){var a=new float[n];Marshal.Copy(Pointer(suffix),a,0,n);return a;}
    public NativeModel(string dll,byte[] bytes)
    {
        library=NativeLogging.Load(Path.GetFullPath(dll));
        try {
            NativeLogging.Redirect(library);
            mocMemory=NativeMemory.AlignedAlloc((nuint)((bytes.Length+63L)/64*64),64);if(mocMemory==null)throw new OutOfMemoryException();Marshal.Copy(bytes,0,(nint)mocMemory,bytes.Length);
            if(Fn<Check>("csmHasMocConsistency")((nint)mocMemory,(uint)bytes.Length)!=1)throw new InvalidDataException("Moc consistency failed");
            var moc=Fn<Revive>("csmReviveMocInPlace")((nint)mocMemory,(uint)bytes.Length);if(moc==0)throw new InvalidDataException("Moc revive failed");
            var size=Fn<Size>("csmGetSizeofModel")(moc);modelMemory=NativeMemory.AlignedAlloc((nuint)((size+15UL)/16*16),16);if(modelMemory==null)throw new OutOfMemoryException();
            model=Fn<Init>("csmInitializeModelInPlace")(moc,(nint)modelMemory,size);if(model==0)throw new InvalidDataException("Model init failed");
            int n=Fn<Count>("csmGetParameterCount")(model);var ids=(nint*)Pointer("ParameterIds");ParameterIds=Enumerable.Range(0,n).Select(i=>Marshal.PtrToStringUTF8(ids[i])!).ToArray();
            Min=Floats("ParameterMinimumValues",n);Max=Floats("ParameterMaximumValues",n);Defaults=Floats("ParameterDefaultValues",n);Update(Defaults);
        }catch{Dispose();throw;}
    }
    public void Update(float[] values){Marshal.Copy(values,0,Pointer("ParameterValues"),values.Length);Fn<UpdateFn>("csmUpdateModel")(model);Revision++;}
    public void Dispose(){NativeMemory.AlignedFree(modelMemory);modelMemory=null;NativeMemory.AlignedFree(mocMemory);mocMemory=null;if(library!=0&&model!=-1){NativeLibrary.Free(library);model=-1;}}
}
