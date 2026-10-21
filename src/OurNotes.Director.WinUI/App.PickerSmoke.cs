using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace OurNotes.Director.WinUI;

public partial class App
{
    // ContentDialog is a popup and is not inside the root's RenderTargetBitmap tree.
    // Capture the actual visible native picker content, not a drawn UI mock-up.
    internal static async Task CapturePickerCheckpointAsync(FrameworkElement picker)
    {
        if (instance is not { smokeReportPath: not null } app) return;
        app.captures.Add(await CaptureAsync(picker,
            Path.Combine(Path.GetDirectoryName(app.smokeReportPath)!, "stage-picker.png")));
    }

    internal static string SmokeEvidencePath(string name)
    {
        if (instance is not { smokeReportPath: not null } app)
            throw new InvalidOperationException("Smoke evidence requires the explicit --smoke-test entry point.");
        if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name)
            throw new ArgumentException("Smoke evidence must use a plain file name.", nameof(name));
        return Path.Combine(Path.GetDirectoryName(app.smokeReportPath)!, name);
    }

    // Render the running popup itself. Its rounded edges may be transparent;
    // unlike the workspace screenshot, no opaque background is synthesized.
    internal static async Task CapturePopupCheckpointAsync(FrameworkElement popup, string name)
    {
        if (instance is not { smokeReportPath: not null } app) return;
        popup.UpdateLayout();
        var image = new RenderTargetBitmap();
        await image.RenderAsync(popup);
        if (image.PixelWidth <= 0 || image.PixelHeight <= 0)
            throw new InvalidOperationException("WinUI returned an empty popup screenshot.");
        var buffer = await image.GetPixelsAsync();
        byte[] pixels = new byte[checked((int)buffer.Length)];
        using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
        if (pixels.Length != checked(image.PixelWidth * image.PixelHeight * 4))
            throw new InvalidDataException("WinUI returned an unexpected popup BGRA buffer length.");
        int nonOpaque = 0, visible = 0;
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != byte.MaxValue) nonOpaque++;
            if (pixels[i] > 0) visible++;
        }
        if (visible == 0) throw new InvalidOperationException("WinUI rendered no visible popup content.");
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)image.PixelWidth, (uint)image.PixelHeight, 96, 96, pixels);
        await encoder.FlushAsync();
        stream.Seek(0);
        using var pngReader = new DataReader(stream);
        uint size = checked((uint)stream.Size);
        await pngReader.LoadAsync(size);
        byte[] png = new byte[checked((int)size)];
        pngReader.ReadBytes(png);
        string path = SmokeEvidencePath(name + ".png");
        await File.WriteAllBytesAsync(path, png);
        app.captures.Add(new JsonObject
        {
            ["path"] = Path.GetFullPath(path), ["width"] = image.PixelWidth, ["height"] = image.PixelHeight,
            ["opaque"] = nonOpaque == 0, ["pixelCount"] = pixels.Length / 4,
            ["nonOpaquePixels"] = nonOpaque, ["source"] = "WinUI RenderTargetBitmap",
            ["surface"] = "ContentDialog"
        });
    }
}
