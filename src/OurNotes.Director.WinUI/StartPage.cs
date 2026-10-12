using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.WinUI;

internal sealed class StartPage : UserControl
{
    private readonly MainWindow _window;
    private readonly StackPanel _recent = Ui.Stack(8);
    private readonly TextBlock _archive = Ui.Text("", 13, true);
    private readonly InfoBar _warning = new() { Severity = InfoBarSeverity.Warning, IsClosable = true };

    public StartPage(MainWindow window)
    {
        _window = window;
        var source = Ui.Card(Ui.Stack(12, Ui.Text("1  准备素材", 19),
            Ui.Text("首次使用先下载素材；已经有归档时，直接选择目录即可。"), _archive,
            Ui.Row(8, Ui.AsyncButton("选择已有归档", ChooseArchiveAsync, "\uED25"),
                Ui.Button("下载素材", () => window.Navigate("archive"), "\uE896"))));
        var scene = Ui.Card(Ui.Stack(12, Ui.Text("2  创建场景", 19),
            Ui.Text("从模板选择人物，或粘贴 agent 生成的场景 JSON。可以随时在参数面板和 JSON 之间切换。"),
            Ui.Row(8, Ui.Button("打开场景工作台", () => window.Navigate("scenes"), "\uE8A5", true),
                Ui.AsyncButton("打开 JSON 文件", async () => { window.Navigate("scenes"); await window.Scene.OpenAsync(); }, "\uE8E5"))));
        var home = Ui.Card(Ui.Stack(12, Ui.Text("主页 Spine 动画", 19),
            Ui.Text("先查看主页与角色，再导出完整动画、单独角色或批量素材包。透明 MOV 可直接用于后期合成。"),
            Ui.Row(8, Ui.Button("浏览主页", () => window.OpenCommand("home.list"), "\uE7F4"),
                Ui.Button("批量导出", () => window.OpenCommand("home.export"), "\uE8B5"))));
        var media = Ui.Card(Ui.Stack(12, Ui.Text("预览与交付", 19),
            Ui.Text("抽帧预览、时间采样图集、PNG 序列与视频导出都在同一工作台中。任务会显示进度，并保留结果和日志。"),
            Ui.Row(8, Ui.Button("媒体工具", () => window.Navigate("media"), "\uE714"),
                Ui.Button("任务与导出", () => window.Navigate("tasks"), "\uE9F9"))));
        var docs = Ui.Row(8,
            Ui.AsyncButton("桌面使用说明", () => OpenDocumentAsync("DESKTOP.md"), "\uE8A5"),
            Ui.AsyncButton("场景 JSON 文档", () => OpenDocumentAsync("USER_GUIDE.md")),
            Ui.Button("运行组件检查", () => window.OpenCommand("probe.runtime"), "\uE9D9"));
        Content = Ui.Scroll(Ui.Stack(22,
            Ui.Heading("把场景变成可剪辑的动画", "选择资源、调整画面、预览，再导出。也可以把场景 JSON 直接粘贴进来。"),
            _warning, Ui.Columns(source, scene), Ui.Columns(home, media),
            Ui.Card(Ui.Stack(12, Ui.Text("最近打开的场景", 19), _recent)), docs));
    }

    internal void Refresh()
    {
        _archive.Text = string.IsNullOrWhiteSpace(_window.Settings.ArchivePath) ? "尚未选择素材归档" : _window.Settings.ArchivePath;
        _warning.Message = _window.Settings.LoadWarning ?? ""; _warning.IsOpen = _warning.Message.Length > 0;
        _recent.Children.Clear();
        foreach (var path in _window.Settings.RecentProjects)
        {
            var button = Ui.AsyncButton(Path.GetFileName(path), async () => { _window.Navigate("scenes"); await _window.Scene.OpenAsync(path); }, "\uE8A5");
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            _recent.Children.Add(Ui.Stack(3, button, Ui.Text(path, 12, true)));
        }
        if (_recent.Children.Count == 0) _recent.Children.Add(Ui.Text("打开或保存场景后，会在这里显示。", 13, true));
    }

    private async Task ChooseArchiveAsync()
    {
        if (await _window.PickFolderAsync() is not { } folder) return;
        if (!Directory.Exists(Path.Combine(folder, "index")))
            throw new ArgumentException("请选择包含 index 子目录的素材归档根目录。还没有归档时，请使用“下载素材”。");
        _window.Settings.ArchivePath = folder; _window.SaveSettings(); _window.ApplySettings(); Refresh();
    }

    private async Task OpenDocumentAsync(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(path)) MainWindow.OpenPath(path);
        else await _window.ShowMessageAsync("使用说明", "发布包中未找到 " + name + "。请在项目仓库的同名文件中查看完整说明。");
    }
}
