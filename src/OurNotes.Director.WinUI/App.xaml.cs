using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    public App()
    {
        smokeReportPath = ReadSmokeReportPath();
        UnhandledException += (_, args) =>
        {
            WriteFailure(args.Exception);
            if (smokeReportPath is not null) Environment.Exit(1);
        };
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            window = new MainWindow();
            if (smokeReportPath is not null)
            {
                if (window.Content is not FrameworkElement root)
                    throw new InvalidOperationException("The main window has no FrameworkElement content to verify.");
                root.Loaded += OnSmokeRootLoaded;
            }
            window.Activate();
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
            if (window is null || smokeReportPath is null || sender is not FrameworkElement root)
                throw new InvalidOperationException("The smoke test was invoked without a loaded main window.");
            root.Loaded -= OnSmokeRootLoaded;
            root.UpdateLayout();
            var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
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
            await window.RunSmokeTestAsync(smokeReportPath);
            captures.Add(await CaptureAsync(root, Path.ChangeExtension(smokeReportPath, "ready.png")));

            var report = JsonNode.Parse(await File.ReadAllTextAsync(smokeReportPath)) as JsonObject
                ?? throw new InvalidDataException("The UI smoke test did not write a JSON object report.");
            if (report["ok"]?.GetValue<bool>() != true)
                throw new InvalidDataException("The UI smoke test did not report success.");
            report["startup"] = startupEvidence.DeepClone();
            report["captures"] = captures.DeepClone();
            await File.WriteAllTextAsync(smokeReportPath, report.ToJsonString(ReportOptions), new UTF8Encoding(false));
            Environment.Exit(0);
        }
        catch (Exception exception)
        {
            WriteFailure(exception);
            Environment.Exit(1);
        }
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

    private void WriteFailure(Exception exception)
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
                report["exception"] = exception.ToString();
                report["startup"] = startupEvidence?.DeepClone();
                report["captures"] = captures.DeepClone();
                Directory.CreateDirectory(Path.GetDirectoryName(smokeReportPath)!);
                File.WriteAllText(smokeReportPath, report.ToJsonString(ReportOptions), new UTF8Encoding(false));
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
