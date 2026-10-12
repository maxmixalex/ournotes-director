using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OurNotes.Director.Desktop.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;

namespace OurNotes.Director.WinUI;

public sealed class MainWindow : Window
{
    internal new static MainWindow? Current { get; private set; }
    internal DesktopSettings Settings { get; }
    internal TaskQueue Jobs { get; private set; }
    internal EngineRunner Engine => new(Settings.ResolveEngine());
    private readonly NavigationView _navigation = new();
    private readonly Grid _root = new();
    private readonly ContentControl _content = new();
    private readonly Dictionary<string, FrameworkElement> _pages = new(StringComparer.Ordinal);
    private readonly TextBlock _status = Ui.Text("准备就绪", 12, true);
    private readonly ProgressBar _progress = new() { Width = 120, Height = 4, Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed };
    private readonly TextBlock _archiveStatus = Ui.Text("", 12, true);
    private bool _closing;
    private bool _dialogOpen;
    private bool _closeApproved;
    internal string ActivePage { get; private set; } = "start";
    internal ScenePage Scene => (ScenePage)GetPage("scenes");

    public MainWindow()
    {
        Current = this; Title = "OurNotes Director";
        _root.Style = (Style)Application.Current.Resources["WorkspaceRootStyle"];
        Settings = DesktopSettings.Load(); Jobs = new TaskQueue(Engine); Jobs.Changed += TaskChanged;
        bool positioned = false;
        try
        {
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
            if (area?.WorkArea is { Width: > 0, Height: > 0 } work)
            {
                int width = Math.Min(1380, work.Width), height = Math.Min(920, work.Height);
                AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - width) / 2,
                    work.Y + (work.Height - height) / 2, width, height));
                positioned = true;
            }
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or NotSupportedException) { }
        if (!positioned) AppWindow.Resize(new SizeInt32(960, 640));
        _navigation.IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed;
        _navigation.IsSettingsVisible = false;
        _navigation.PaneDisplayMode = NavigationViewPaneDisplayMode.Auto;
        _navigation.OpenPaneLength = 218;
        _navigation.PaneHeader = Ui.Stack(4,
            new TextBlock { Text = "OURNOTES", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }, Ui.Text("Director · 动画工作台", 12, true));
        foreach (var (key, title, glyph) in new[]
        {
            ("start", "开始", "\uE80F"), ("scenes", "场景工作台", "\uE8A5"), ("home", "主页动画", "\uE7F4"),
            ("stories", "剧情与对白", "\uE8F2"), ("models", "角色模型", "\uE77B"), ("assets", "素材资源", "\uE8B7"),
            ("media", "媒体工具", "\uE714"), ("archive", "下载与归档", "\uE896"), ("tasks", "任务与导出", "\uE9F9")
        }) _navigation.MenuItems.Add(new NavigationViewItem { Content = title, Tag = key, Icon = new FontIcon { Glyph = glyph } });
        _navigation.FooterMenuItems.Add(new NavigationViewItem { Content = "诊断检查", Tag = "diagnostics", Icon = new FontIcon { Glyph = "\uE9D9" } });
        _navigation.FooterMenuItems.Add(new NavigationViewItem { Content = "设置", Tag = "settings", Icon = new FontIcon { Glyph = "\uE713" } });
        _navigation.ItemInvoked += (_, args) => { if (args.InvokedItemContainer?.Tag is string key) Navigate(key); };
        var body = new Grid { Padding = new Thickness(28, 24, 28, 14), RowSpacing = 14 };
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.Children.Add(_content);
        var footer = new Grid { ColumnSpacing = 12 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(_progress); footer.Children.Add(_status); Grid.SetColumn(_status, 1);
        footer.Children.Add(_archiveStatus); Grid.SetColumn(_archiveStatus, 2); Grid.SetRow(footer, 1); body.Children.Add(footer);
        _navigation.Content = body; _root.Children.Add(_navigation); Content = _root;
        ApplySettings(); Navigate("start");
        AppWindow.Closing += async (_, args) =>
        {
            if (_closeApproved) return;
            if (Jobs.IsBusy || SceneIfCreated()?.HasUnsavedChanges == true)
            {
                args.Cancel = true;
                if (_closing) return; _closing = true;
                bool close = await ConfirmAsync("关闭工作台？", Jobs.IsBusy
                    ? "仍有任务在处理。关闭会取消这些任务；归档更新、剧情与项目帧序列已经完成的部分会保留，之后可以续跑。场景草稿会自动保存。"
                    : "场景尚未保存到项目文件。草稿会自动保留，下次打开可以恢复。", "关闭");
                if (close) { await SaveAndCloseAsync(); _closeApproved = true; Close(); }
                _closing = false;
            }
            else { _closeApproved = true; await SaveAndCloseAsync(); }
        };
    }

    private ScenePage? SceneIfCreated() => _pages.TryGetValue("scenes", out var page) ? (ScenePage)page : null;
    private async Task SaveAndCloseAsync()
    {
        foreach (var page in _pages.Values.OfType<CommandPage>()) page.SaveForm();
        SceneIfCreated()?.SaveForm(); SceneIfCreated()?.SaveDraft(); SaveSettings(); await Jobs.DisposeAsync();
    }

    internal void SaveSettings()
    {
        try { Settings.Save(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { _status.Text = "设置暂时无法保存：" + error.Message; }
    }

    internal void ApplySettings()
    {
        _root.RequestedTheme = Settings.Theme switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        _archiveStatus.Text = string.IsNullOrWhiteSpace(Settings.ArchivePath) ? "尚未选择素材归档" : "归档：" + Path.GetFileName(Settings.ArchivePath.TrimEnd(Path.DirectorySeparatorChar));
    }

    internal async Task ReconnectEngineAsync()
    {
        if (Jobs.IsBusy) throw new InvalidOperationException("请等当前任务结束后再更换引擎。");
        Jobs.Changed -= TaskChanged; await Jobs.DisposeAsync(); Jobs = new TaskQueue(Engine); Jobs.Changed += TaskChanged;
    }

    internal void Navigate(string key)
    {
        ActivePage = key; _content.Content = GetPage(key);
        foreach (var item in _navigation.MenuItems.Concat(_navigation.FooterMenuItems).OfType<NavigationViewItem>())
            if (item.Tag?.ToString() == key) _navigation.SelectedItem = item;
        if (key == "tasks") ((TasksPage)GetPage(key)).Refresh();
        if (key == "start") ((StartPage)GetPage(key)).Refresh();
        if (key == "settings") ((SettingsPage)GetPage(key)).Refresh();
    }

    private FrameworkElement GetPage(string key)
    {
        if (_pages.TryGetValue(key, out var page)) return page;
        page = key switch
        {
            "start" => new StartPage(this), "scenes" => new ScenePage(this), "tasks" => new TasksPage(this), "settings" => new SettingsPage(this),
            "archive" => new CommandPage(this, CommandGroup.Archive), "home" => new CommandPage(this, CommandGroup.Home),
            "stories" => new CommandPage(this, CommandGroup.Stories), "models" => new CommandPage(this, CommandGroup.Models),
            "assets" => new CommandPage(this, CommandGroup.Assets), "media" => new CommandPage(this, CommandGroup.Media),
            "diagnostics" => new CommandPage(this, CommandGroup.Diagnostics), _ => throw new ArgumentException("未知页面：" + key)
        };
        _pages[key] = page; return page;
    }

    internal void OpenCommand(string id, IReadOnlyDictionary<string, string?>? values = null)
    {
        var definition = CommandCatalog.Get(id);
        string key = definition.Group switch
        {
            CommandGroup.Archive => "archive", CommandGroup.Home => "home", CommandGroup.Stories => "stories", CommandGroup.Models => "models",
            CommandGroup.Assets => "assets", CommandGroup.Media => "media", CommandGroup.Scenes => "scenes", _ => "diagnostics"
        };
        Navigate(key);
        if (GetPage(key) is CommandPage commandPage) commandPage.SelectCommand(id, values);
        else if (GetPage(key) is ScenePage scene) scene.SelectOperation(id, values);
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
        if (ActivePage == "tasks" && _pages.TryGetValue("tasks", out var page)) ((TasksPage)page).RefreshProgress(task);
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
        var checks = new JsonArray(); var pages = new JsonArray(); var forms = new JsonArray();
        void Check(bool condition, string description) { if (!condition) throw new InvalidOperationException(description); checks.Add(description); }
        static CommandForm? FindForm(DependencyObject node, string commandId)
        {
            if (node is CommandForm form && form.Definition.Id == commandId) return form;
            int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
                if (FindForm(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, i), commandId) is { } child) return child;
            return null;
        }
        Check(CommandCatalog.All.Count >= 71, "所有 CLI 操作均有图形界面定义");
        foreach (var key in new[] { "start", "archive", "home", "stories", "models", "assets", "media", "diagnostics", "tasks", "settings", "scenes" })
        {
            Navigate(key); _root.UpdateLayout(); await Task.Delay(80);
            Check(_content.Content is FrameworkElement element && element.ActualWidth > 0, key + "页面已实际布局"); pages.Add(key);
        }
        foreach (var definition in CommandCatalog.All)
        {
            OpenCommand(definition.Id); _root.UpdateLayout(); await Task.Delay(25); _root.UpdateLayout();
            var form = _content.Content is DependencyObject content ? FindForm(content, definition.Id) : null;
            Check(form is { IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0 }, definition.Id + "操作表单已实际创建并布局");
            forms.Add(new JsonObject { ["id"] = definition.Id, ["page"] = ActivePage, ["width"] = form!.ActualWidth, ["height"] = form.ActualHeight });
        }
        Check(forms.Count == CommandCatalog.All.Count, "全部操作表单均经过真实 WinUI 布局检查");
        Navigate("scenes"); _root.UpdateLayout(); await Task.Delay(80);
        await Scene.RunSmokeTestAsync(Check);
        var help = await Engine.RunAsync(new EngineRequest("help", "引擎检查", ["help"], Path.GetDirectoryName(reportPath)!));
        Check(help.Success && help.Json?["commands"] is JsonArray, "真实随包原生引擎可启动并返回全部命令");
        string png = Path.Combine(Path.GetDirectoryName(reportPath)!, "local-alpha-probe.png");
        await File.WriteAllBytesAsync(png, OurNotes.Director.Core.Png.Encode(new OurNotes.Director.Core.Pixels(2, 1, [255, 0, 0, 0, 0, 255, 0, 128])));
        var probe = await RunAsync("image.probe", new Dictionary<string, string?> { ["input"] = png });
        Check(probe.Success && probe.Result is JsonObject, "没有素材归档时仍可通过真实任务队列检查本地透明 PNG");
        Check(Jobs.Tasks.Any(t => t.Request.CommandId == "image.probe" && t.Status == DesktopTaskStatus.Succeeded && File.Exists(t.ReportPath)), "真实后台任务已记录结果并持久保存");
        Navigate("tasks"); _root.UpdateLayout(); await Task.Delay(80);
        Navigate("scenes"); _root.UpdateLayout(); await Task.Delay(150);
        DesktopSettings.AtomicWrite(reportPath, new JsonObject { ["schema"] = "ournotes-director/desktop-smoke/1", ["ok"] = true,
            ["checks"] = checks, ["pages"] = pages, ["forms"] = forms, ["commands"] = CommandCatalog.All.Count,
            ["result"] = "WinUI 全部操作表单、剪贴板 JSON 编辑与原生任务队列通过" }.ToJsonString(DesktopSettings.JsonOptions));
    }
}
