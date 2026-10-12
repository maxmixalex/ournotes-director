using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using OurNotes.Director.Desktop.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.UI;

namespace OurNotes.Director.WinUI;

public sealed class MainWindow : Window
{
    internal new static MainWindow? Current { get; private set; }
    internal DesktopSettings Settings { get; }
    internal TaskQueue Jobs { get; private set; }
    internal EngineRunner Engine => new(Settings.ResolveEngine());
    private readonly Grid _root = new();
    private readonly ContentControl _content = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly Dictionary<string, FrameworkElement> _pages = new(StringComparer.Ordinal);
    private readonly ToggleButton _sceneTab = new() { Content = "场景工作台", Padding = new(22, 9, 22, 9) };
    private readonly ToggleButton _homeTab = new() { Content = "主页工作台", Padding = new(22, 9, 22, 9) };
    private readonly TextBlock _status = Ui.Text("准备就绪", 12, true);
    private readonly ProgressBar _progress = new() { Width = 100, Height = 4, Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed };
    private readonly TextBlock _archiveStatus = Ui.Text("", 12, true);
    private readonly Border _drawer = new() { Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Right,
        BorderThickness = new(1, 0, 0, 0), BorderBrush = new SolidColorBrush(Color.FromArgb(55, 128, 128, 128)), Padding = new(16) };
    private readonly ContentControl _drawerContent = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly TextBlock _drawerTitle = new() { FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private string? _drawerKey;
    private SceneStudioPage? _scene;
    private HomeStudioPage? _home;
    private bool _closing, _dialogOpen, _closeApproved;
    internal string ActivePage { get; private set; } = "scenes";
    internal SceneStudioPage Scene => _scene ??= new(this);
    internal HomeStudioPage Home => _home ??= new(this);

    public MainWindow()
    {
        App.TraceStartup("MainWindow fields initialized");
        Current = this; Title = "OurNotes Director";
        _root.Style = (Style)Application.Current.Resources["WorkspaceRootStyle"];
        Settings = DesktopSettings.Load(); Jobs = new TaskQueue(Engine); Jobs.Changed += TaskChanged;
        App.TraceStartup("MainWindow position");
        bool positioned = false;
        try
        {
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
            if (area?.WorkArea is { Width: > 0, Height: > 0 } work)
            {
                int width = Math.Min(1520, work.Width), height = Math.Min(980, work.Height);
                AppWindow.MoveAndResize(new(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height));
                positioned = true;
            }
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or NotSupportedException or InvalidCastException)
        { App.TraceStartup("Display placement unavailable: " + error.Message); }
        if (!positioned) AppWindow.Resize(new(1100, 740));
        App.TraceStartup("MainWindow chrome");
        _root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new Grid { Padding = new(16, 9, 16, 9), ColumnSpacing = 18 };
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = "OURNOTES", FontSize = 18, VerticalAlignment = VerticalAlignment.Center,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var tabs = Ui.Row(4, _sceneTab, _homeTab); tabs.HorizontalAlignment = HorizontalAlignment.Center;
        header.Children.Add(tabs); Grid.SetColumn(tabs, 1);
        _sceneTab.Click += (_, _) => Navigate("scenes"); _homeTab.Click += (_, _) => Navigate("home");
        var actions = Ui.Row(6, StudioLayout.Tool("素材库", () => ShowResourceManager(), "\uE8B7"),
            StudioLayout.Tool("", () => Navigate("tasks"), "\uE9F9"), StudioLayout.Tool("", () => Navigate("settings"), "\uE713"));
        ToolTipService.SetToolTip(actions.Children[1], "导出任务"); ToolTipService.SetToolTip(actions.Children[2], "设置");
        header.Children.Add(actions); Grid.SetColumn(actions, 2); _root.Children.Add(header);
        _content.Margin = new(10, 0, 10, 0); _root.Children.Add(_content); Grid.SetRow(_content, 1);
        var footer = new Grid { Padding = new(14, 5, 14, 6), ColumnSpacing = 10 };
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        footer.Children.Add(_progress); footer.Children.Add(_status); Grid.SetColumn(_status, 1);
        footer.Children.Add(_archiveStatus); Grid.SetColumn(_archiveStatus, 2); _root.Children.Add(footer); Grid.SetRow(footer, 2);
        var drawerGrid = new Grid { RowSpacing = 14 };
        drawerGrid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        drawerGrid.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        var drawerHeader = new Grid(); drawerHeader.Children.Add(_drawerTitle);
        var close = StudioLayout.Tool("关闭", CloseDrawer, "\uE711"); close.HorizontalAlignment = HorizontalAlignment.Right;
        drawerHeader.Children.Add(close); drawerGrid.Children.Add(drawerHeader);
        drawerGrid.Children.Add(_drawerContent); Grid.SetRow(_drawerContent, 1); _drawer.Child = drawerGrid;
        _root.Children.Add(_drawer); Grid.SetRow(_drawer, 1);
        _root.SizeChanged += (_, _) => _drawer.Width = Math.Min(820, Math.Max(460, _root.ActualWidth - 90));
        _root.ActualThemeChanged += (_, _) => _drawer.Background = new SolidColorBrush(_root.ActualTheme == ElementTheme.Dark
            ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 248, 248, 248));
        App.TraceStartup("MainWindow attach root");
        Content = _root; ApplySettings();
        App.TraceStartup("MainWindow initial scene");
        Navigate("scenes");
        App.TraceStartup("MainWindow scene attached");
        AppWindow.Closing += async (_, args) =>
        {
            if (_closeApproved) return;
            args.Cancel = true; if (_closing) return; _closing = true;
            try
            {
                bool dirty = _scene?.HasUnsavedChanges == true || _home?.HasUnsavedChanges == true;
                if ((Jobs.IsBusy || dirty) && !await ConfirmAsync("关闭工作台？", Jobs.IsBusy
                    ? "还有导出任务正在处理。关闭会停止这些任务，未保存的项目会保留为草稿。"
                    : "未保存的修改会保留为草稿，下次可以继续编辑。", "关闭")) return;
                await SaveAndCloseAsync(); _closeApproved = true; Close();
            }
            finally { _closing = false; }
        };
    }

    private async Task SaveAndCloseAsync()
    {
        _scene?.SaveDraft(); _home?.SaveDraft();
        _scene?.Deactivate(); _home?.Deactivate();
        foreach (var page in _pages.Values.OfType<CommandPage>()) page.SaveForm();
        SaveSettings(); await Jobs.DisposeAsync();
        if (_scene is not null) await _scene.DisposeAsync();
        if (_home is not null) await _home.DisposeAsync();
    }

    internal void SetStatus(string text) => _status.Text = text;

    internal void SaveSettings()
    {
        try { Settings.Save(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { _status.Text = "设置暂时无法保存：" + error.Message; }
    }

    internal void ApplySettings()
    {
        _root.RequestedTheme = Settings.Theme switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        _archiveStatus.Text = string.IsNullOrWhiteSpace(Settings.ArchivePath) ? "选择素材库" : Path.GetFileName(Settings.ArchivePath.TrimEnd(Path.DirectorySeparatorChar));
    }

    internal async Task ReconnectEngineAsync()
    {
        if (Jobs.IsBusy) throw new InvalidOperationException("请等当前任务结束后再更换引擎。");
        Jobs.Changed -= TaskChanged; await Jobs.DisposeAsync(); Jobs = new TaskQueue(Engine); Jobs.Changed += TaskChanged;
    }

    internal void Navigate(string key)
    {
        if (key == "start") key = "scenes";
        if (key is "scenes" or "home")
        {
            if (ActivePage != key) { _scene?.Deactivate(); _home?.Deactivate(); }
            ActivePage = key; CloseDrawer();
            _content.Content = key == "scenes" ? Scene : Home;
            _sceneTab.IsChecked = key == "scenes"; _homeTab.IsChecked = key == "home";
            return;
        }
        var page = GetToolPage(key);
        if (page is SettingsPage settings) settings.Refresh();
        if (page is TasksPage tasks) tasks.Refresh();
        ShowDrawer(key, key switch { "tasks" => "导出任务", "settings" => "设置", "archive" => "下载素材", _ => "素材与工具" }, page);
    }

    private FrameworkElement GetToolPage(string key)
    {
        if (_pages.TryGetValue(key, out var page)) return page;
        page = key switch
        {
            "tasks" => new TasksPage(this), "settings" => new SettingsPage(this), "scene-tools" => new ScenePage(this),
            "archive" => new CommandPage(this, CommandGroup.Archive), "home-tools" => new CommandPage(this, CommandGroup.Home),
            "stories" => new CommandPage(this, CommandGroup.Stories), "models" => new CommandPage(this, CommandGroup.Models),
            "assets" => new CommandPage(this, CommandGroup.Assets), "media" => new CommandPage(this, CommandGroup.Media),
            "diagnostics" => new CommandPage(this, CommandGroup.Diagnostics), _ => throw new ArgumentException("未知工具：" + key)
        };
        _pages[key] = page; return page;
    }

    internal void ShowDrawer(string key, string title, FrameworkElement content)
    {
        _drawerKey = key; _drawerTitle.Text = title; _drawerContent.Content = content;
        _drawer.Background = new SolidColorBrush(_root.ActualTheme == ElementTheme.Dark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 248, 248, 248));
        _drawer.Width = Math.Min(820, Math.Max(460, _root.ActualWidth - 90)); _drawer.Visibility = Visibility.Visible;
    }
    internal void CloseDrawer() { _drawer.Visibility = Visibility.Collapsed; _drawerContent.Content = null; _drawerKey = null; }

    internal void ShowResourceManager()
    {
        var path = Ui.Text(string.IsNullOrWhiteSpace(Settings.ArchivePath) ? "先选择已有素材，或下载所需乐队。" : Settings.ArchivePath, 13, true);
        var panel = Ui.Stack(18, path, Ui.Row(8, StudioLayout.AsyncTool("选择素材文件夹", async () =>
        {
            if (await PickFolderAsync() is not { } folder) return;
            Settings.ArchivePath = folder; SaveSettings(); ApplySettings(); path.Text = folder;
        }, "\uE8B7"), StudioLayout.Tool("下载 / 更新素材", () => Navigate("archive"), "\uE896")),
            new Expander { Header = "素材检索与其他工具", HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = Ui.Stack(8, StudioLayout.Tool("剧情与对白", () => Navigate("stories")), StudioLayout.Tool("模型与动作", () => Navigate("models")),
                    StudioLayout.Tool("场景检查与续渲", () => Navigate("scene-tools")), StudioLayout.Tool("主页素材与批量处理", () => Navigate("home-tools")),
                    StudioLayout.Tool("场景素材", () => Navigate("assets")), StudioLayout.Tool("媒体处理", () => Navigate("media")), StudioLayout.Tool("运行诊断", () => Navigate("diagnostics"))) });
        ShowDrawer("resources", "素材库", panel);
    }

    internal void OpenCommand(string id, IReadOnlyDictionary<string, string?>? values = null)
    {
        var definition = CommandCatalog.Get(id);
        string key = definition.Group switch
        {
            CommandGroup.Archive => "archive", CommandGroup.Home => "home-tools", CommandGroup.Stories => "stories", CommandGroup.Models => "models",
            CommandGroup.Assets => "assets", CommandGroup.Media => "media", CommandGroup.Scenes => "scene-tools", _ => "diagnostics"
        };
        var page = GetToolPage(key); ShowDrawer(key, definition.Label, page);
        if (page is CommandPage commandPage) commandPage.SelectCommand(id, values);
        else if (page is ScenePage scene) scene.SelectOperation(id, values);
    }

    internal async Task<EngineResult> RunAsync(string id, IReadOnlyDictionary<string, string?> values, CommandContext? context = null)
    {
        var definition = CommandCatalog.Get(id); var build = CommandLineBuilder.Build(id, values, context ?? Settings.Context());
        if (!build.IsValid) throw new ArgumentException(string.Join(Environment.NewLine, build.Errors.Select(e => e.Message)));
        var request = new EngineRequest(id, definition.Label, build.Arguments,
            context?.WorkingDirectory ?? Environment.CurrentDirectory, build.OutputPath, build.OutputKind);
        var task = Jobs.Enqueue(request); _status.Text = definition.Label + "已加入任务";
        var result = await task.Completion;
        if (id == "archive" && result.Success && result.OutputPath is { } archive)
        { Settings.ArchivePath = archive; SaveSettings(); ApplySettings(); }
        return result;
    }

    internal async Task<EngineResult> QueryAsync(string id, IReadOnlyDictionary<string, string?>? values = null, CancellationToken cancellationToken = default)
    {
        var build = CommandLineBuilder.Build(id, values ?? new Dictionary<string, string?>(), Settings.Context());
        if (!build.IsValid) throw new ArgumentException(string.Join(Environment.NewLine, build.Errors.Select(e => e.Message)));
        return await Engine.RunAsync(new EngineRequest(id, CommandCatalog.Get(id).Label, build.Arguments, Environment.CurrentDirectory), cancellationToken: cancellationToken);
    }

    private void TaskChanged(DesktopTask task) => DispatcherQueue.TryEnqueue(() =>
    {
        var running = Jobs.Tasks.FirstOrDefault(t => t.Status == DesktopTaskStatus.Running);
        _progress.Visibility = running is null ? Visibility.Collapsed : Visibility.Visible;
        _progress.IsIndeterminate = running?.Fraction is null; _progress.Value = running?.Fraction ?? 0;
        _status.Text = running is null ? task.Request.Title + " · " + task.StatusLabel : running.Request.Title + " · " + running.Message;
        if (_drawerKey == "tasks" && _pages.TryGetValue("tasks", out var page)) ((TasksPage)page).RefreshProgress(task);
    });

    internal async Task<string?> PickFileAsync(bool save = false, string? suggestedName = null, IReadOnlyList<string>? extensions = null)
    {
        if (save)
        {
            // Windows App SDK 2.x returns only a path; the exporter owns file creation.
            var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(AppWindow.Id)
            { SuggestedFileName = Path.GetFileName(suggestedName ?? "output") };
            if (extensions is { Count: > 0 })
            {
                picker.FileTypeChoices.Add("输出文件", extensions.ToList());
                string extension = Path.GetExtension(picker.SuggestedFileName);
                string preferred = extensions.Contains(extension, StringComparer.OrdinalIgnoreCase) ? extension : extensions[0];
                if (preferred.StartsWith('.') && preferred != ".*")
                {
                    picker.DefaultFileExtension = preferred;
                    picker.SuggestedFileName = Path.GetFileNameWithoutExtension(picker.SuggestedFileName);
                }
            }
            var result = await picker.PickSaveFileAsync(); return result?.Path;
        }
        else
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id);
            if (extensions is { Count: > 0 })
                foreach (var extension in extensions) picker.FileTypeFilter.Add(extension);
            var result = await picker.PickSingleFileAsync(); return result?.Path;
        }
    }

    internal async Task<string?> PickFolderAsync()
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(AppWindow.Id);
        var result = await picker.PickSingleFolderAsync(); return result?.Path;
    }

    internal async Task ShowErrorAsync(string message) => await ShowMessageAsync("需要调整", message);
    internal async Task ShowMessageAsync(string title, string message)
    {
        if (_dialogOpen) { _status.Text = message; return; }
        _dialogOpen = true;
        try { await new ContentDialog { XamlRoot = _root.XamlRoot, Title = title, Content = Ui.Text(message), CloseButtonText = "知道了", RequestedTheme = _root.ActualTheme }.ShowAsync(); }
        finally { _dialogOpen = false; }
    }

    internal async Task<bool> ConfirmAsync(string title, string description, string primary)
    {
        if (_dialogOpen) return false; _dialogOpen = true;
        try
        {
            var result = await new ContentDialog { XamlRoot = _root.XamlRoot, Title = title, Content = Ui.Text(description),
                PrimaryButtonText = primary, CloseButtonText = "返回", DefaultButton = ContentDialogButton.Close, RequestedTheme = _root.ActualTheme }.ShowAsync();
            return result == ContentDialogResult.Primary;
        }
        finally { _dialogOpen = false; }
    }

    internal XamlRoot RootXaml => _root.XamlRoot;
    internal static void Copy(string text) { var package = new DataPackage(); package.SetText(text); Clipboard.SetContent(package); Clipboard.Flush(); }
    internal static void OpenPath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("找不到这个文件或目录。", path);
        Process.Start(new ProcessStartInfo { FileName = Path.GetFullPath(path), UseShellExecute = true });
    }

    public async Task RunSmokeTestAsync(string reportPath)
    {
        var checks = new JsonArray(); var pages = new JsonArray();
        void Check(bool condition, string description) { if (!condition) throw new InvalidOperationException(description); checks.Add(description); }
        foreach (var key in new[] { "scenes", "home" })
        {
            Navigate(key); _root.UpdateLayout(); await Task.Delay(100);
            Check(_content.Content is FrameworkElement { ActualWidth: > 0, ActualHeight: > 0 }, key + "工作台已在实际窗口布局"); pages.Add(key);
        }
        Check(pages.Count == 2 && _sceneTab.Content?.ToString() == "场景工作台" && _homeTab.Content?.ToString() == "主页工作台", "主界面只有两个创作工作台");
        Navigate("scenes"); _root.UpdateLayout(); await Task.Delay(100);
        await Scene.RunSmokeTestAsync(Check);
        Navigate("home"); _root.UpdateLayout(); await Task.Delay(100);
        await Home.RunSmokeTestAsync(Check);
        var help = await Engine.RunAsync(new EngineRequest("help", "引擎检查", ["help"], Path.GetDirectoryName(reportPath)!));
        Check(help.Success && help.Json?["commands"] is JsonArray, "原生导出执行器可用");
        string png = Path.Combine(Path.GetDirectoryName(reportPath)!, "local-alpha-probe.png");
        await File.WriteAllBytesAsync(png, OurNotes.Director.Core.Png.Encode(new OurNotes.Director.Core.Pixels(2, 1, [255, 0, 0, 0, 0, 255, 0, 128])));
        var probe = await RunAsync("image.probe", new Dictionary<string, string?> { ["input"] = png });
        Check(probe.Success, "后台队列通过真实本地媒体操作");
        Check(Jobs.Tasks.Any(t => t.Request.CommandId == "image.probe" && t.Status == DesktopTaskStatus.Succeeded && File.Exists(t.ReportPath)), "任务结果持久保存");
        CloseDrawer(); _root.UpdateLayout(); await Task.Delay(150);
        DesktopSettings.AtomicWrite(reportPath, new JsonObject { ["schema"] = "ournotes-director/desktop-smoke/1", ["ok"] = true,
            ["checks"] = checks, ["pages"] = pages, ["commands"] = CommandCatalog.All.Count,
            ["result"] = "原生工作台、实时画布、场景编辑与后台导出检查通过" }.ToJsonString(DesktopSettings.JsonOptions));
    }
}
