using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.WinUI;

internal sealed partial class SettingsPage : UserControl
{
    private readonly MainWindow _window;
    private readonly TextBox _archive = Ui.Input("素材归档目录", placeholder: "包含 index 子目录的归档根目录");
    private readonly TextBox _output = Ui.Input("默认导出目录");
    private readonly ComboBox _site = Ui.Choice("默认站点", [("intl", "国际服"), ("jp", "日服")]);
    private readonly ComboBox _language = Ui.Choice("默认语言", [("zh-Hans", "简体中文"), ("zh-Hant", "繁体中文"), ("ja", "日语"), ("en", "英语"), ("ko", "韩语")]);
    private readonly ComboBox _theme = Ui.Choice("外观", [("Default", "跟随系统"), ("Light", "浅色"), ("Dark", "深色")]);
    private readonly Dictionary<string, TextBox> _tools = new(StringComparer.Ordinal);
    private readonly InfoBar _notice = new() { IsClosable = true };

    public SettingsPage(MainWindow window)
    {
        _window = window;
        var advanced = Ui.Stack(16, Ui.Text("通常保持留空，自动使用发布包中的运行组件。更换引擎前需要等待当前任务结束。", 13, true));
        foreach (var (key, label, folder) in new[]
        {
            ("engine", "原生引擎 advkit.exe", false), ("ffmpeg", "FFmpeg 路径", false),
            ("ffprobe", "FFprobe 路径", false), ("cubism", "Cubism SDK 目录", true), ("native", "原生渲染库路径", false)
        })
        {
            var input = Ui.Input(label, placeholder: "自动检测"); _tools[key] = input;
            advanced.Children.Add(PathField(input, folder));
        }
        Content = Ui.Scroll(Ui.Stack(22,
            Ui.Heading("设置", "保存常用目录和浏览偏好。场景项目中明确指定的站点、语言和导出参数会继续生效。"),
            Ui.Card(Ui.Stack(16, PathField(_archive, true), PathField(_output, true), Ui.Columns(_site, _language), _theme)),
            CreateGraphicsSettings(),
            new Expander { Header = "运行组件与自定义路径", Content = advanced, HorizontalAlignment = HorizontalAlignment.Stretch },
            Ui.Row(10, Ui.AsyncButton("保存设置", SaveAsync, "\uE74E", true),
                Ui.Button("检查运行组件", () => window.OpenCommand("probe.runtime"), "\uE9D9")), _notice));
    }

    private FrameworkElement PathField(TextBox box, bool directory)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.Children.Add(box);
        var button = Ui.AsyncButton("浏览…", async () =>
        {
            var path = directory ? await _window.PickFolderAsync() : await _window.PickFileAsync();
            if (path is not null) box.Text = path;
        });
        button.VerticalAlignment = VerticalAlignment.Bottom; Grid.SetColumn(button, 1); grid.Children.Add(button); return grid;
    }

    internal void Refresh()
    {
        var settings = _window.Settings;
        _archive.Text = settings.ArchivePath; _output.Text = settings.OutputDirectory;
        Select(_site, settings.Site); Select(_language, settings.Language); Select(_theme, settings.Theme);
        RefreshGraphicsSettings();
        _tools["engine"].Text = settings.EnginePath; _tools["ffmpeg"].Text = settings.FFmpegPath;
        _tools["ffprobe"].Text = settings.FFprobePath; _tools["cubism"].Text = settings.CubismSdkPath; _tools["native"].Text = settings.NativeLibraryPath;
    }

    private static void Select(ComboBox box, string value)
        => box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag?.ToString() == value) ?? box.Items[0];

    private async Task SaveAsync()
    {
        string archive = _archive.Text.Trim(), output = _output.Text.Trim();
        if (archive.Length > 0 && !Directory.Exists(archive)) throw new ArgumentException("素材归档目录不存在。请重新选择，或先到“下载与归档”创建归档。");
        if (output.Length == 0) throw new ArgumentException("请选择默认导出目录。");
        output = Path.GetFullPath(output);
        foreach (var (key, box) in _tools)
        {
            string path = box.Text.Trim();
            if (path.Length > 0 && !(key == "cubism" ? Directory.Exists(path) : File.Exists(path))) throw new ArgumentException(box.Header + "不存在，请重新选择。");
        }
        var settings = _window.Settings;
        string engine = _tools["engine"].Text.Trim();
        bool reconnect = !string.Equals(settings.EnginePath, engine, StringComparison.OrdinalIgnoreCase);
        if (reconnect && _window.Jobs.IsBusy) throw new InvalidOperationException("当前还有任务在处理。请等任务结束后再更换引擎。");
        Directory.CreateDirectory(output);
        int gpuPreference = int.Parse(Ui.Selected(gpuChoice), System.Globalization.CultureInfo.InvariantCulture);
        string gpuEngine = engine.Length == 0 ? Path.Combine(AppContext.BaseDirectory, "advkit.exe") : engine;
        bool gpuChanged = !WindowsGpuPreference.MatchesTargets(gpuPreference, gpuEngine);
        if (gpuChanged) WindowsGpuPreference.Apply(gpuPreference, gpuEngine);
        GpuPreferencePolicy.Store(settings, gpuPreference);
        settings.ArchivePath = archive.Length == 0 ? "" : Path.GetFullPath(archive); settings.OutputDirectory = output;
        settings.Site = Ui.Selected(_site); settings.Language = Ui.Selected(_language); settings.Theme = Ui.Selected(_theme);
        settings.EnginePath = engine; settings.FFmpegPath = _tools["ffmpeg"].Text.Trim(); settings.FFprobePath = _tools["ffprobe"].Text.Trim();
        settings.CubismSdkPath = _tools["cubism"].Text.Trim(); settings.NativeLibraryPath = _tools["native"].Text.Trim();
        settings.Save(); _window.ApplySettings();
        if (reconnect) await _window.ReconnectEngineAsync();
        RefreshGraphicsSettings();
        _notice.Severity = InfoBarSeverity.Success; _notice.Title = "设置已保存";
        _notice.Message = gpuPreference != WindowsGpuPreference.StartupPreference
            ? "显卡偏好已保存。重新启动应用后生效。" : gpuChanged ? "编辑器和导出引擎的显卡偏好已保存。" : "新的操作会使用这些设置。";
        _notice.IsOpen = true;
    }
}
