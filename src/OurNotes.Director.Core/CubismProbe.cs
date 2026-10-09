using System.Runtime.InteropServices;

namespace OurNotes.Director.Core;

/// <summary>Actual Native Core load/update smoke test, not an ADV renderer.</summary>
public static unsafe class CubismProbe
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint VersionFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CheckFn(nint data, uint size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint ReviveFn(nint data, uint size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint SizeFn(nint moc);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint InitFn(nint moc, nint memory, uint size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void UpdateFn(nint model);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CountFn(nint model);
    public static object Run(string dll, byte[] bytes)
    {
        if (!Environment.Is64BitProcess) throw new PlatformNotSupportedException("Native Core probe requires a 64-bit process");
        var library = NativeLogging.Load(Path.GetFullPath(dll));
        void* mocMemory = null; void* modelMemory = null;
        try
        {
            NativeLogging.Redirect(library);
            T Fn<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
            mocMemory = NativeMemory.AlignedAlloc((nuint)((bytes.Length + 63L) / 64 * 64), 64);
            Marshal.Copy(bytes, 0, (nint)mocMemory, bytes.Length);
            if (Fn<CheckFn>("csmHasMocConsistency")((nint)mocMemory, (uint)bytes.Length) != 1) throw new InvalidDataException("Moc consistency failed");
            var moc = Fn<ReviveFn>("csmReviveMocInPlace")((nint)mocMemory, (uint)bytes.Length);
            if (moc == 0) throw new InvalidDataException("Core rejected moc3");
            var size = Fn<SizeFn>("csmGetSizeofModel")(moc);
            modelMemory = NativeMemory.AlignedAlloc((nuint)((size + 15UL) / 16 * 16), 16);
            var model = Fn<InitFn>("csmInitializeModelInPlace")(moc, (nint)modelMemory, size);
            if (model == 0) throw new InvalidDataException("Core model initialization failed");
            Fn<UpdateFn>("csmUpdateModel")(model);
            return JsonOutput.Object(("coreVersion", Fn<VersionFn>("csmGetVersion")()), ("parameters", Fn<CountFn>("csmGetParameterCount")(model)), ("parts", Fn<CountFn>("csmGetPartCount")(model)), ("drawables", Fn<CountFn>("csmGetDrawableCount")(model)), ("modelMemoryBytes", size), ("updated", true), ("rendered", false), ("dll", Path.GetFullPath(dll)));
        }
        finally { NativeMemory.AlignedFree(modelMemory); NativeMemory.AlignedFree(mocMemory); NativeLibrary.Free(library); }
    }
}
