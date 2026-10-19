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
    readonly TextBlock gpuState = Ui.Text("", 12, true);
    int registeredGpuPreference;
    FrameworkElement CreateGraphicsSettings()
    {
        gpuChoice.SelectionChanged += (_, _) => RefreshGraphicsState();
        return Ui.Card(Ui.Stack(12, Ui.Text("图形", 16), gpuChoice, gpuState, actualGpu,
            Ui.Text("保存后重启应用，将同时切换编辑器和导出引擎的显卡偏好。下方显示当前渲染与呈现分别使用的设备。", 12, true),
            Ui.Button("Windows 图形设置…", () => Process.Start(new ProcessStartInfo("ms-settings:display-advancedgraphics") { UseShellExecute = true }), "\uE713")));
    }
    void RefreshGraphicsSettings()
    {
        registeredGpuPreference = WindowsGpuPreference.ReadCurrentPreference() ?? 0;
        Select(gpuChoice, registeredGpuPreference.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var item in gpuChoice.Items.OfType<ComboBoxItem>())
        {
            int preference = int.Parse(item.Tag!.ToString()!, System.Globalization.CultureInfo.InvariantCulture);
            string label = preference switch { 1 => "节能", 2 => "高性能", _ => "由 Windows 选择" };
            string? candidateAdapter = preference == 0 ? null : GpuPreviewSurface.PreferredAdapterName(preference);
            item.Content = candidateAdapter is null ? label : label + " — " + candidateAdapter;
        }
        var viewports = _window.ActiveLayout is { } layout ? MainWindow.Descendants<NativeViewport>(layout).ToArray() : [];
        var viewport = viewports.FirstOrDefault(value => value.LastFrame is not null);
        var frame = viewport?.LastFrame;
        actualGpu.Text = frame is null ? "当前工作台尚未创建渲染会话。" : "当前渲染（OpenGL）：" + frame.Device + "\n当前呈现："
            + (viewport!.PresentationAdapterName is { } adapter ? "D3D11 — " + adapter
                : "兼容像素显示" + (viewport.PresentationFailure is { Length: > 0 } failure ? "（" + failure + "）" : ""));
        RefreshGraphicsState();
    }
    void RefreshGraphicsState()
    {
        int selected = int.TryParse(Ui.Selected(gpuChoice), out int value) ? value : registeredGpuPreference;
        string launched = WindowsGpuPreference.StartupPreference switch { 1 => "节能", 2 => "高性能", _ => "由 Windows 选择" };
        gpuState.Text = selected != registeredGpuPreference ? "显卡选择尚未保存。保存后重启应用。"
            : registeredGpuPreference != WindowsGpuPreference.StartupPreference ? "显卡偏好已保存，等待重启应用。"
            : "本次启动偏好：" + launched;
        if (WindowsGpuPreference.StartupWarning is { } warning) gpuState.Text += "\n" + warning;
    }
}
