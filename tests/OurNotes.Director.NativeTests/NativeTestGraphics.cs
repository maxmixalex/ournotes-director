using System.Runtime.InteropServices;
using OurNotes.Director.Core;

/// <summary>Opt-in CI software driver; never changes normal application DLL lookup.</summary>
static class NativeTestGraphics
{
    static nint handle;
    public static string PackageDirectory=>Path.GetFullPath(Environment.GetEnvironmentVariable("OURNOTES_TEST_PACKAGE")??AppContext.BaseDirectory);
    [DllImport("kernel32.dll",EntryPoint="LoadLibraryExW",CharSet=CharSet.Unicode,SetLastError=true)]
    static extern nint LoadLibraryEx(string name,nint file,uint flags);
    public static void Configure()
    {
        if(!OperatingSystem.IsWindows()||handle!=0||Environment.GetEnvironmentVariable("OURNOTES_TEST_OPENGL") is not {Length:>0} driver)return;
        if(!Path.IsPathFullyQualified(driver)||!File.Exists(driver))throw new FileNotFoundException("The test OpenGL driver must be an existing absolute path.",driver);
        handle=LoadLibraryEx(driver,0,0x00000100|0x00001000);
        if(handle==0)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Cannot load the test OpenGL driver.");
        NativeLibrary.SetDllImportResolver(typeof(OffscreenGl).Assembly,(name,_,_)=>name=="opengl32.dll"?handle:0);
    }
}
