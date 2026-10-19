using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.WinUI;

internal sealed partial class SettingsPage
{
    readonly ComboBox gpuChoice = Ui.Choice("渲染显卡（重启后生效）", [("0", "由 Windows 选择"), ("1", "节能显卡"), ("2", "高性能显卡")]);
    readonly TextBlock actualGpu = Ui.Text("尚未创建渲染会话。", 12, true);
    FrameworkElement CreateGraphicsSettings() => Ui.Card(Ui.Stack(12, Ui.Text("图形", 16), gpuChoice, actualGpu,
        Ui.Text("设置同时应用于编辑器和导出引擎。共享纹理仍由兼容的 OpenGL / D3D11 设备建立，实际显卡以下方诊断为准。", 12, true),
        Ui.Button("Windows 图形设置…", () => Process.Start(new ProcessStartInfo("ms-settings:display-advancedgraphics") { UseShellExecute = true }), "\uE713")));
    void RefreshGraphicsSettings()
    {
        Select(gpuChoice, (WindowsGpuPreference.ReadCurrentPreference() ?? GpuPreferencePolicy.Read(_window.Settings)).ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var item in gpuChoice.Items.OfType<ComboBoxItem>())
        {
            int preference = int.Parse(item.Tag!.ToString()!, System.Globalization.CultureInfo.InvariantCulture);
            string label = preference switch { 1 => "节能", 2 => "高性能", _ => "由 Windows 选择" };
            string? adapter = preference == 0 ? null : GpuPreviewSurface.PreferredAdapterName(preference);
            item.Content = adapter is null ? label : label + " — " + adapter;
        }
        var viewports = _window.ActiveLayout is { } layout ? MainWindow.Descendants<NativeViewport>(layout).ToArray() : [];
        var frame = viewports.Select(viewport => viewport.LastFrame).FirstOrDefault(value => value is not null);
        actualGpu.Text = frame is null ? "尚未创建渲染会话。" : "当前 OpenGL：" + frame.Device + "\n当前呈现："
            + (frame.GpuSurface is { } surface ? "D3D11 — " + surface.AdapterName : "兼容像素显示（" + frame.PresentationFallback + "）");
    }
}
