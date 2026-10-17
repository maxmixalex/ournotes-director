using Microsoft.UI.Xaml;

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
}
