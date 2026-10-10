using OurNotes.Director.Core;

static class NativeDependencyTests
{
    public static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "ournotes-dependencies-" + Guid.NewGuid().ToString("N"));
        var application = Path.Combine(root, "package with spaces");
        var elsewhere = Path.Combine(root, "unrelated cwd");
        var sdk = Path.Combine(root, "external sdk");
        var current = Environment.CurrentDirectory;
        var oldSdk = Environment.GetEnvironmentVariable("CUBISM_NATIVE_SDK");
        var oldMotion = Environment.GetEnvironmentVariable("CUBISM_MOTIONSYNC_DLL");
        string Put(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "path-resolution fixture; not a native library");
            return path;
        }
        try
        {
            Environment.SetEnvironmentVariable("CUBISM_NATIVE_SDK", null);
            Environment.SetEnvironmentVariable("CUBISM_MOTIONSYNC_DLL", null);
            Directory.CreateDirectory(elsewhere);
            Environment.CurrentDirectory = elsewhere;
            var core = Put(Path.Combine(application, "native", NativeDependencies.CoreFileName));
            Put(Path.Combine(elsewhere, NativeDependencies.CoreFileName));
            check(NativeDependencies.CoreLibrary(baseDirectory: application) == core, "packaged Core resolves independently of the current working directory");
            var external = Put(Path.Combine(sdk, "Core", "dll", OperatingSystem.IsWindows() ? "windows" : "linux", "x86_64", NativeDependencies.CoreFileName));
            check(NativeDependencies.CoreLibrary(sdk: sdk, baseDirectory: application) == external, "explicit SDK overrides the packaged Core");
            Environment.SetEnvironmentVariable("CUBISM_NATIVE_SDK", sdk);
            check(NativeDependencies.CoreLibrary(baseDirectory: application) == external, "SDK environment override remains supported");
            check(NativeDependencies.CoreLibrary(core, sdk, application) == core, "explicit DLL overrides the SDK environment");
            Environment.SetEnvironmentVariable("CUBISM_NATIVE_SDK", null);
            try { NativeDependencies.CoreLibrary(Path.Combine(root, "missing.dll"), baseDirectory: application); throw new Exception("Missing explicit DLL was silently replaced."); }
            catch (FileNotFoundException) { check(true, "missing explicit Core fails rather than silently selecting the bundled copy"); }

            var motion = Put(Path.Combine(application, "native", NativeDependencies.MotionSyncFileName));
            check(NativeDependencies.MotionSyncLibrary(core, application) == (OperatingSystem.IsWindows() ? motion : null), "Windows resolves the packaged MotionSync engine; Linux selects the managed CRI path");
            foreach (var tool in new[] { "ffmpeg", "ffprobe" })
            {
                var fileName = tool + (OperatingSystem.IsWindows() ? ".exe" : "");
                var bundled = Put(Path.Combine(application, "tools", fileName));
                Put(Path.Combine(elsewhere, fileName));
                check(NativeDependencies.MediaTool(tool, baseDirectory: application) == bundled, tool + " uses the application tools directory from an unrelated cwd");
                var custom = Path.Combine(elsewhere, "custom-" + fileName);
                check(NativeDependencies.MediaTool(tool, custom, application) == custom, tool + " retains an explicit custom path");
                File.Delete(bundled);
                check(NativeDependencies.MediaTool(tool, baseDirectory: application) == tool, tool + " keeps PATH fallback for development installations");
            }
        }
        finally
        {
            Environment.CurrentDirectory = current;
            Environment.SetEnvironmentVariable("CUBISM_NATIVE_SDK", oldSdk);
            Environment.SetEnvironmentVariable("CUBISM_MOTIONSYNC_DLL", oldMotion);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
