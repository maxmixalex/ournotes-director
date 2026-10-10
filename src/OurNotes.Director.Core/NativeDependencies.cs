using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace OurNotes.Director.Core;

/// <summary>Resolve runtime dependencies from the application, never from an unrelated working directory.</summary>
public static class NativeDependencies
{
    public const string CoreSdkDirectory = "CubismSdkForNative-5-r.5";
    public const string MotionSyncSdkDirectory = "CubismSdkMotionSyncPluginForNative-5-r.1";
    public const string MotionSyncFileName = "Live2DCubismMotionSyncEngine_CRI.dll";
    public static string RuntimeIdentifier => OperatingSystem.IsWindows() ? "win-x64" : OperatingSystem.IsLinux() ? "linux-x64" : throw new PlatformNotSupportedException("Native rendering supports Windows x64 and Linux x64.");
    public static string CoreFileName => OperatingSystem.IsWindows() ? "Live2DCubismCore.dll" : "libLive2DCubismCore.so";

    static string BaseDirectory(string? directory) => Path.GetFullPath(directory ?? AppContext.BaseDirectory);
    static string RequireFile(string path, string message)
    {
        path = Path.GetFullPath(path);
        return File.Exists(path) ? path : throw new FileNotFoundException(message, path);
    }
    static string SdkCore(string sdk) => Path.Combine(sdk, "Core", "dll", OperatingSystem.IsWindows() ? "windows" : "linux", "x86_64", CoreFileName);

    public static string CoreLibrary(string? library = null, string? sdk = null, string? baseDirectory = null)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64) throw new PlatformNotSupportedException("Cubism Core requires an x64 process.");
        _ = RuntimeIdentifier;
        if (!string.IsNullOrEmpty(library)) return RequireFile(library, "The explicitly selected Cubism Core library does not exist.");
        sdk = string.IsNullOrEmpty(sdk) ? Environment.GetEnvironmentVariable("CUBISM_NATIVE_SDK") : sdk;
        if (!string.IsNullOrEmpty(sdk)) return RequireFile(SdkCore(sdk), "Cubism Core is missing from the selected SDK.");
        var application = BaseDirectory(baseDirectory);
        foreach (var candidate in new[] { Path.Combine(application, "native", CoreFileName), Path.Combine(application, CoreFileName) })
            if (File.Exists(candidate)) return candidate;
        // Keep source checkouts and older SDK-based packages usable too.
        for (var directory = new DirectoryInfo(application); directory is not null; directory = directory.Parent)
        {
            var candidate = SdkCore(Path.Combine(directory.FullName, CoreSdkDirectory));
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Cubism Core is missing from the application package. Extract the complete release package, or provide --cubism-sdk / --dll.", Path.Combine(application, "native", CoreFileName));
    }

    public static string? MotionSyncLibrary(string coreLibrary, string? baseDirectory = null)
    {
        // Live2D does not ship a Linux MotionSync Core. Linux uses the C# CRI
        // analyser and its archived game weights; Android's .so is not compatible.
        if (!OperatingSystem.IsWindows()) return null;
        var configured = Environment.GetEnvironmentVariable("CUBISM_MOTIONSYNC_DLL");
        if (!string.IsNullOrEmpty(configured)) return RequireFile(configured, "The explicitly selected MotionSync library does not exist.");
        var application = BaseDirectory(baseDirectory);
        var coreDirectory = Path.GetDirectoryName(Path.GetFullPath(coreLibrary))!;
        foreach (var candidate in new[] { Path.Combine(coreDirectory, MotionSyncFileName), Path.Combine(application, "native", MotionSyncFileName), Path.Combine(application, MotionSyncFileName) })
            if (File.Exists(candidate)) return candidate;
        for (var directory = new DirectoryInfo(coreDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, MotionSyncSdkDirectory, "Core", "CRI", "dll", "Windows", "x86_64", MotionSyncFileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static string MediaTool(string name, string? configured = null, string? baseDirectory = null)
    {
        if (name is not ("ffmpeg" or "ffprobe")) throw new ArgumentException("Expected ffmpeg or ffprobe.", nameof(name));
        if (!string.IsNullOrEmpty(configured))
            return Path.IsPathRooted(configured) || !string.IsNullOrEmpty(Path.GetDirectoryName(configured)) ? Path.GetFullPath(configured) : configured;
        var application = BaseDirectory(baseDirectory);
        var fileName = name + (OperatingSystem.IsWindows() ? ".exe" : "");
        foreach (var candidate in new[] { Path.Combine(application, "tools", fileName), Path.Combine(application, fileName) })
            if (File.Exists(candidate)) return candidate;
        return name;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate uint CoreVersion();
    public static uint ReadCoreVersion(string library)
    {
        var handle = NativeLogging.Load(Path.GetFullPath(library));
        try { return Marshal.GetDelegateForFunctionPointer<CoreVersion>(NativeLibrary.GetExport(handle, "csmGetVersion"))(); }
        finally { NativeLibrary.Free(handle); }
    }

    public static JsonObject Probe(string? library = null, string? sdk = null, string? ffmpeg = null, string? ffprobe = null)
    {
        var core = CoreLibrary(library, sdk);
        var version = ReadCoreVersion(core);
        var motionSync = MotionSyncLibrary(core);
        if (OperatingSystem.IsWindows() && motionSync is null)
            throw new FileNotFoundException("MotionSync 5.0.4 is missing from the application package. Extract the complete release package.");
        JsonObject Tool(string name, string? configured)
        {
            var path = MediaTool(name, configured);
            var banner = Media.Run(path, ["-version"]).Split('\n')[0].Trim();
            return new JsonObject { ["path"] = path, ["version"] = banner };
        }
        return new JsonObject
        {
            ["runtimeIdentifier"] = RuntimeIdentifier,
            ["applicationDirectory"] = AppContext.BaseDirectory,
            ["core"] = new JsonObject { ["path"] = core, ["version"] = version, ["versionHex"] = $"0x{version:x8}" },
            ["lipSync"] = motionSync is null
                ? new JsonObject { ["engine"] = "C# CRI Lips", ["nativeLibrary"] = false, ["weights"] = "From the selected game archive" }
                : new JsonObject { ["engine"] = "Cubism MotionSync CRI", ["nativeLibrary"] = true, ["path"] = motionSync, ["version"] = NativeMotionSync.ProbeVersion(motionSync) },
            ["ffmpeg"] = Tool("ffmpeg", ffmpeg),
            ["ffprobe"] = Tool("ffprobe", ffprobe)
        };
    }
}
