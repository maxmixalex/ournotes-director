using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace OurNotes.Director.WinUI;

public partial class App : Application
{
    private static readonly JsonSerializerOptions ReportOptions = new() { WriteIndented = true };
    private readonly string? smokeReportPath;
    private MainWindow? window;
    private bool smokeStarted;
    private JsonObject? startupEvidence;
    private readonly JsonArray captures = [];
    private readonly JsonArray startupTrace = [];
    private readonly JsonArray interfaceFailures = [];
    private static App? instance;
    private nint smokeGraphicsDriver;

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibraryEx(string name, nint file, uint flags);

    public App()
    {
        instance = this;
        WindowsGpuPreference.Initialize();
        smokeReportPath = ReadSmokeReportPath();
        if (smokeReportPath is not null)
            AppDomain.CurrentDomain.FirstChanceException += (_, args) =>
            {
                if (args.Exception is not InvalidCastException || interfaceFailures.Count >= 8) return;
                interfaceFailures.Add(new JsonObject
                {
                    ["exception"] = args.Exception.ToString(),
                    ["managedStack"] = Environment.StackTrace
                });
            };
        UnhandledException += (_, args) =>
        {
            WriteFailure(args.Exception, args.Message);
            if (smokeReportPath is not null) Environment.Exit(1);
        };
        TraceStartup("App.InitializeComponent");
        InitializeComponent();
        TraceStartup("App initialized");
    }

    internal static void TraceStartup(string stage)
    {
        if (instance is not { smokeReportPath: not null } app) return;
        app.startupTrace.Add(new JsonObject { ["stage"] = stage, ["utc"] = DateTimeOffset.UtcNow.ToString("O") });
        Console.Error.WriteLine("[startup] " + stage);
    }

    internal static T TraceConstruction<T>(string name, Func<T> create)
    {
        TraceStartup(name + " constructing");
        T result = create();
        TraceStartup(name + " constructed");
        return result;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            TraceStartup("OnLaunched");
            window = TraceConstruction("MainWindow", () => new MainWindow());
            if (smokeReportPath is not null)
            {
                if (window.Content is not FrameworkElement root)
                    throw new InvalidOperationException("The main window has no FrameworkElement content to verify.");
                root.Loaded += OnSmokeRootLoaded;
            }
            TraceStartup("Window.Activate");
            window.Activate();
            TraceStartup("Window activated");
        }
        catch (Exception exception)
        {
            WriteFailure(exception);
            Environment.Exit(1);
        }
    }

    private async void OnSmokeRootLoaded(object sender, RoutedEventArgs args)
    {
        if (smokeStarted) return;
        smokeStarted = true;
        try
        {
            TraceStartup("Root.Loaded");
            if (window is null || smokeReportPath is null || sender is not FrameworkElement root)
                throw new InvalidOperationException("The smoke test was invoked without a loaded main window.");
            root.Loaded -= OnSmokeRootLoaded;
            root.UpdateLayout();
            TraceStartup("Root arranged");
            var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
            TraceStartup("Window handle obtained");
            if (handle == IntPtr.Zero || root.ActualWidth <= 0 || root.ActualHeight <= 0)
                throw new InvalidOperationException("The main window did not create a visible, arranged XAML surface.");
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            var winUiNativePath = process.Modules.Cast<System.Diagnostics.ProcessModule>()
                .FirstOrDefault(module => string.Equals(module.ModuleName, "Microsoft.UI.Xaml.dll", StringComparison.OrdinalIgnoreCase))?.FileName;
            if (string.IsNullOrEmpty(winUiNativePath))
                throw new InvalidOperationException("The loaded WinUI native runtime could not be identified.");
            startupEvidence = new JsonObject
            {
                ["uiLoaded"] = true,
                ["width"] = root.ActualWidth,
                ["height"] = root.ActualHeight,
                ["windowHandle"] = handle.ToInt64(),
                ["windowTitle"] = window.Title,
                ["processId"] = Environment.ProcessId,
                ["runtimeDirectory"] = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),
                ["winUiNativePath"] = winUiNativePath
            };
            Directory.CreateDirectory(Path.GetDirectoryName(smokeReportPath)!);
            captures.Add(await CaptureAsync(root, Path.ChangeExtension(smokeReportPath, "startup.png")));
            LoadSmokeGraphicsDriver();
            await window.RunSmokeTestAsync(smokeReportPath);
            captures.Add(await CaptureAsync(root, Path.ChangeExtension(smokeReportPath, "ready.png")));
            window.Navigate("scenes");
            await Task.Delay(100);
            captures.Add(await CaptureAsync(root, Path.ChangeExtension(smokeReportPath, "scene.png")));

            var report = JsonNode.Parse(await File.ReadAllTextAsync(smokeReportPath)) as JsonObject
                ?? throw new InvalidDataException("The UI smoke test did not write a JSON object report.");
            if (report["ok"]?.GetValue<bool>() != true)
                throw new InvalidDataException("The UI smoke test did not report success.");
            report["startup"] = startupEvidence.DeepClone();
            report["captures"] = captures.DeepClone();
            report["startupTrace"] = startupTrace.DeepClone();
            OurNotes.Director.Desktop.Core.DesktopSettings.AtomicWrite(smokeReportPath, report.ToJsonString(ReportOptions));
            Environment.Exit(0);
        }
        catch (Exception exception)
        {
            if (smokeReportPath is not null && sender is FrameworkElement failedRoot)
            {
                try { captures.Add(await CaptureAsync(failedRoot, Path.ChangeExtension(smokeReportPath, "failed.png"))); }
                catch (Exception captureError) { TraceStartup("Failure screenshot unavailable: " + captureError.Message); }
            }
            WriteFailure(exception);
            Environment.Exit(1);
        }
    }

    private void LoadSmokeGraphicsDriver()
    {
        // Let WinUI initialize its own compositor before bringing in the CI-only Mesa
        // module and its dependencies. Normal launches always use the system driver.
        if (smokeReportPath is null || Environment.GetEnvironmentVariable("OURNOTES_TEST_OPENGL") is not { Length: > 0 } driver) return;
        if (!Path.IsPathFullyQualified(driver) || !File.Exists(driver)) throw new FileNotFoundException("The selected test graphics driver does not exist.", driver);
        smokeGraphicsDriver = LoadLibraryEx(driver, 0, 0x00000100 | 0x00001000);
        if (smokeGraphicsDriver == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Cannot load the CI graphics driver.");
        NativeLibrary.SetDllImportResolver(typeof(OurNotes.Director.Core.OffscreenGl).Assembly,
            (name, _, _) => name == "opengl32.dll" ? smokeGraphicsDriver : 0);
        TraceStartup("Test graphics driver loaded");
    }

    internal static async Task CaptureCheckpointAsync(string name)
    {
        if(instance is not {smokeReportPath:not null,window:not null} app||app.window.Content is not FrameworkElement root)return;
        app.captures.Add(await CaptureAsync(root,Path.Combine(Path.GetDirectoryName(app.smokeReportPath)!,name+".png")));
    }

    private static async Task<JsonObject> CaptureAsync(FrameworkElement root, string path)
    {
        root.UpdateLayout();
        if (root is not Microsoft.UI.Xaml.Controls.Panel { Background: Microsoft.UI.Xaml.Media.SolidColorBrush background }
            || background.Color.A != byte.MaxValue || background.Opacity != 1)
            throw new InvalidOperationException("The workspace root must have an opaque theme background before capturing its real XAML surface.");
        var image = new RenderTargetBitmap();
        await image.RenderAsync(root);
        if (image.PixelWidth <= 0 || image.PixelHeight <= 0)
            throw new InvalidOperationException("WinUI returned an empty rendered surface for the UI screenshot.");
        var pixelBuffer = await image.GetPixelsAsync();
        var pixels = new byte[checked((int)pixelBuffer.Length)];
        using (var reader = DataReader.FromBuffer(pixelBuffer)) reader.ReadBytes(pixels);
        if (pixels.Length != checked(image.PixelWidth * image.PixelHeight * 4))
            throw new InvalidDataException("WinUI returned an unexpected BGRA screenshot buffer length.");
        int nonOpaquePixels = 0;
        for (int i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != byte.MaxValue) nonOpaquePixels++;

        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)image.PixelWidth, (uint)image.PixelHeight, 96, 96, pixels);
        await encoder.FlushAsync();
        stream.Seek(0);
        using var pngReader = new DataReader(stream);
        var length = checked((uint)stream.Size);
        await pngReader.LoadAsync(length);
        var png = new byte[checked((int)length)];
        pngReader.ReadBytes(png);
        await File.WriteAllBytesAsync(path, png);
        if (nonOpaquePixels != 0)
            throw new InvalidOperationException($"The real WinUI screenshot contains {nonOpaquePixels} pixels with alpha below 255: {path}");
        return new JsonObject
        {
            ["path"] = Path.GetFullPath(path),
            ["width"] = image.PixelWidth,
            ["height"] = image.PixelHeight,
            ["opaque"] = true,
            ["pixelCount"] = pixels.Length / 4,
            ["nonOpaquePixels"] = nonOpaquePixels,
            ["source"] = "WinUI RenderTargetBitmap"
        };
    }

    private void WriteFailure(Exception exception, string? message = null)
    {
        try
        {
            if (smokeReportPath is not null)
            {
                JsonObject report;
                try { report = JsonNode.Parse(File.ReadAllText(smokeReportPath)) as JsonObject ?? new(); }
                catch { report = new JsonObject(); }
                report["schema"] ??= "ournotes-director/desktop-smoke/1";
                report["ok"] = false;
                report["status"] = "failed";
                report["exception"] = exception.ToString();
                report["message"] = message;
                report["managedStack"] = Environment.StackTrace;
                report["startupTrace"] = startupTrace.DeepClone();
                report["interfaceFailures"] = interfaceFailures.DeepClone();
                report["graphicsModules"] = GraphicsModules();
                report["startup"] = startupEvidence?.DeepClone();
                report["captures"] = captures.DeepClone();
                Directory.CreateDirectory(Path.GetDirectoryName(smokeReportPath)!);
                OurNotes.Director.Desktop.Core.DesktopSettings.AtomicWrite(smokeReportPath, report.ToJsonString(ReportOptions));
            }
            else
            {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "OurNotes", "Director", "logs");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "startup.log"),
                    $"{DateTimeOffset.UtcNow:O}{Environment.NewLine}{exception}{Environment.NewLine}");
            }
        }
        catch { /* Preserve the original startup failure if its diagnostic destination is unavailable. */ }
    }

    private static JsonArray GraphicsModules()
    {
        var result = new JsonArray();
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            foreach (var module in process.Modules.Cast<System.Diagnostics.ProcessModule>())
            {
                string name = module.ModuleName;
                if (!name.StartsWith("Microsoft.UI.", StringComparison.OrdinalIgnoreCase)
                    && !new[] { "opengl", "gallium", "dxgi", "dxcore", "d3d", "dcomp", "dwrite", "Windows.UI.Composition" }
                        .Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase))) continue;
                result.Add(new JsonObject { ["name"] = name, ["path"] = module.FileName, ["version"] = module.FileVersionInfo.FileVersion });
            }
        }
        catch (Exception error) { result.Add(new JsonObject { ["diagnosticError"] = error.Message }); }
        return result;
    }

    private static string? ReadSmokeReportPath()
    {
        var arguments = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(arguments, "--smoke-test");
        if (index < 0) return null;
        if (index + 1 >= arguments.Length || !Path.IsPathFullyQualified(arguments[index + 1]))
            throw new ArgumentException("--smoke-test requires an absolute JSON report path.");
        return Path.GetFullPath(arguments[index + 1]);
    }
}
