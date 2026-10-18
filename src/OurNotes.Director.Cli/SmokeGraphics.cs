using System.Runtime.InteropServices;
using OurNotes.Director.Core;

/// <summary>Explicit Windows smoke-test graphics for CLI processes launched by the real editor queue.</summary>
static class SmokeGraphics
{
    const uint LoadLibrarySearchDllLoadDir = 0x00000100;
    const uint LoadLibrarySearchDefaultDirs = 0x00001000;
    static nint handle;

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    static extern nint LoadLibraryEx(string name, nint file, uint flags);

    public static void Configure()
    {
        // The GUI resolver belongs to its process. Its queued CLI child must opt in
        // independently; ordinary launches keep the system graphics driver lookup.
        if (!OperatingSystem.IsWindows() || handle != 0
            || !Directory.Exists(Environment.GetEnvironmentVariable("OURNOTES_REAL_SCENE_FIXTURE"))
            || Environment.GetEnvironmentVariable("OURNOTES_TEST_OPENGL") is not { Length: > 0 } driver) return;
        if (!Path.IsPathFullyQualified(driver) || !File.Exists(driver))
            throw new FileNotFoundException("The smoke-test OpenGL driver must be an existing absolute path.", driver);
        Environment.SetEnvironmentVariable("GALLIUM_DRIVER", "llvmpipe");
        Environment.SetEnvironmentVariable("LIBGL_ALWAYS_SOFTWARE", "1");
        handle = LoadLibraryEx(Path.GetFullPath(driver), 0, LoadLibrarySearchDllLoadDir | LoadLibrarySearchDefaultDirs);
        if (handle == 0)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Cannot load the smoke-test OpenGL driver.");
        NativeLibrary.SetDllImportResolver(typeof(OffscreenGl).Assembly, (name, _, _) => name == "opengl32.dll" ? handle : 0);
    }
}
