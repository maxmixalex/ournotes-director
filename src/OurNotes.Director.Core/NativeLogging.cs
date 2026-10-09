using System.Runtime.InteropServices;
namespace OurNotes.Director.Core;
public static class NativeLogging
{
    [DllImport("libc.so.6")] static extern int dup(int fd);
    [DllImport("libc.so.6")] static extern int dup2(int source,int destination);
    [DllImport("libc.so.6")] static extern int close(int fd);
    [DllImport("libc.so.6")] static extern int fflush(nint stream);
    static readonly object loadLock=new();
    static bool consoleReady;
    public static void ConfigureConsole()
    {
        if(!OperatingSystem.IsLinux()||consoleReady)return;
        int output=dup(1);if(output<0)throw new IOException("Cannot preserve JSON stdout");
        if(dup2(2,1)<0){close(output);throw new IOException("Cannot redirect native stdout");}
        // Core also writes directly through C stdio during model update/unload.
        // Managed JSON keeps the original pipe, native diagnostics use stderr.
        var stream=new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(output,true),FileAccess.Write);
        Console.SetOut(new StreamWriter(stream,new System.Text.UTF8Encoding(false)){AutoFlush=true});
        consoleReady=true;
    }
    // Linux Core writes its version banner from the ELF constructor before a
    // logging callback can be installed. Keep the CLI's stdout valid JSON.
    public static nint Load(string path)
    {
        if(!OperatingSystem.IsLinux())return NativeLibrary.Load(path);
        lock(loadLock){
            fflush(0);int original=dup(1);if(original<0)throw new IOException("Cannot preserve stdout");
            try{if(dup2(2,1)<0)throw new IOException("Cannot redirect native log");return NativeLibrary.Load(path);}
            finally{fflush(0);dup2(original,1);close(original);}
        }
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Log(nint message);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Set(Log handler);
    static readonly Log handler=p=>Console.Error.WriteLine(Marshal.PtrToStringUTF8(p));
    public static void Redirect(nint library)=>Marshal.GetDelegateForFunctionPointer<Set>(NativeLibrary.GetExport(library,"csmSetLogFunction"))(handler);
}
