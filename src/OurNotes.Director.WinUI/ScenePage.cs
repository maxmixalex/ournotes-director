using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OurNotes.Director.Desktop.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace OurNotes.Director.WinUI;

internal sealed partial class ScenePage : UserControl
{
    private readonly MainWindow _window;
    private readonly TextBox _editor = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        FontSize = 14, PlaceholderText = "把 agent 生成的场景 JSON 粘贴到这里，或从文件、模板开始。",
        MinHeight = 340, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
        AllowDrop = true
    };
    private readonly TextBlock _documentTitle = Ui.Text("未命名场景", 13, true);
    private readonly TextBlock _position = Ui.Text("第 1 行，第 1 列", 12, true);
    private readonly InfoBar _validation = new() { IsOpen = true, IsClosable = true, Severity = InfoBarSeverity.Informational, Title = "从场景开始",
        Message = "粘贴 JSON、打开项目，或选择模板。你可以随时切换到参数编辑。" };
    private readonly ComboBox _operation;
    private readonly ContentControl _exportFormHost = new();
    private readonly ContentControl _visualHost = new();
    private readonly Pivot _tabs = new();
    private readonly ResultView _result;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _draftTimer;
    private CommandForm? _exportForm;
    private SceneDocument? _document;
    private string? _path;
    private string _baseDirectory = Environment.CurrentDirectory;
    private string _savedText = "";
    private bool _settingText;
    internal bool HasUnsavedChanges => _editor.Text != _savedText;
    private static string DraftPath => Path.Combine(DesktopSettings.DataDirectory, "scene-draft.json");

    internal ScenePage(MainWindow window)
    {
        _window = window;
        _draftTimer = DispatcherQueue.CreateTimer(); _draftTimer.Interval = TimeSpan.FromMilliseconds(800); _draftTimer.IsRepeating = false;
        _draftTimer.Tick += (_, _) => SaveDraft();
        _editor.TextChanged += (_, _) =>
        {
            if (_settingText) return;
            _documentTitle.Text = (_path is null ? "未命名场景" : Path.GetFileName(_path)) + (HasUnsavedChanges ? " · 未保存" : "");
            _draftTimer.Stop(); _draftTimer.Start();
        };
        _editor.SelectionChanged += (_, _) =>
        {
            string prefix = _editor.Text[..Math.Min(_editor.SelectionStart, _editor.Text.Length)];
            int line = prefix.Count(c => c == '\n') + 1; int last = prefix.LastIndexOf('\n');
            _position.Text = $"第 {line} 行，第 {prefix.Length - last} 列 · {_editor.Text.Length:N0} 字符";
        };
        _editor.DragOver += (_, e) => { if (e.DataView.Contains(StandardDataFormats.StorageItems) || e.DataView.Contains(StandardDataFormats.Text)) e.AcceptedOperation = DataPackageOperation.Copy; };
        _editor.Drop += async (_, e) =>
        {
            try
            {
                if (e.DataView.Contains(StandardDataFormats.StorageItems))
                {
                    var items = await e.DataView.GetStorageItemsAsync();
                    if (items.FirstOrDefault() is Windows.Storage.StorageFile file) await OpenPathAsync(file.Path);
                }
                else if (e.DataView.Contains(StandardDataFormats.Text)) await ReplaceEditorAsync(await e.DataView.GetTextAsync());
            }
            catch (Exception error) { await window.ShowErrorAsync(error.Message); }
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_editor, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(_editor, ScrollBarVisibility.Auto);
        var toolbar = new CommandBar { DefaultLabelPosition = CommandBarDefaultLabelPosition.Right, IsOpen = false };
        void Tool(string label, string glyph, Func<Task> action)
        {
            var button = new AppBarButton { Label = label, Icon = new FontIcon { Glyph = glyph } };
            button.Click += async (_, _) => { try { await action(); } catch (Exception error) { await window.ShowErrorAsync(error.Message); } };
            toolbar.PrimaryCommands.Add(button);
        }
        Tool("新建", "\uE710", NewAsync); Tool("打开", "\uE8E5", () => OpenAsync()); Tool("粘贴 JSON", "\uE77F", PasteAsync);
        Tool("保存", "\uE74E", () => SaveAsync(false)); Tool("另存为", "\uE792", () => SaveAsync(true));
        Tool("格式化", "\uE8E9", () => { var document = ParseCurrent(); document.Format(); SetDocumentText(document); ValidateCurrent(); return Task.CompletedTask; });
        Tool("复制 JSON", "\uE8C8", () => { MainWindow.Copy(_editor.Text); return Task.CompletedTask; });
        var agent = new AppBarButton { Label = "复制给 agent 的场景说明", Icon = new FontIcon { Glyph = "\uE8F2" } };
        agent.Click += (_, _) => CopyAgentInstructions(); toolbar.SecondaryCommands.Add(agent);
        var restore = new AppBarButton { Label = "恢复自动保存的草稿", Icon = new FontIcon { Glyph = "\uE777" } };
        restore.Click += async (_, _) => { try { await RestoreDraftAsync(); } catch (Exception error) { await window.ShowErrorAsync(error.Message); } };
        toolbar.SecondaryCommands.Add(restore);
        var editorGrid = new Grid { RowSpacing = 8 };
        editorGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        editorGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        editorGrid.Children.Add(_editor); Grid.SetRow(_position, 1); editorGrid.Children.Add(_position);
        _tabs.Items.Add(new PivotItem { Header = "JSON 编辑", Content = editorGrid });
        _tabs.Items.Add(new PivotItem { Header = "参数编辑", Content = Ui.Scroll(_visualHost) });
        _tabs.SelectionChanged += (_, _) => { if (_tabs.SelectedIndex == 1) RefreshVisualEditor(); };
        _operation = Ui.Choice("输出与检查", CommandCatalog.ForGroup(CommandGroup.Scenes).Select(c => (c.Id, c.Label)), "render.clip");
        _operation.SelectionChanged += (_, _) => RebuildExportForm();
        _result = new ResultView(window, (id, values) => window.OpenCommand(id, values));
        var sidebar = Ui.Stack(16, _operation, _exportFormHost, Ui.AsyncButton("运行所选操作", RunSelectedAsync, "\uE768", true),
            Ui.Row(8, Ui.AsyncButton("检查 JSON", () => { ValidateCurrent(); return Task.CompletedTask; }, "\uE73E"),
                Ui.AsyncButton("预览一帧", PreviewAsync, "\uE91B")),
            Ui.Text("“检查 JSON”验证结构与参数；“校验统一项目”还会实际加载素材并检查首帧。导出任务可在左侧任务页取消。", 12, true));
        var main = new Grid { ColumnSpacing = 20 };
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        main.Children.Add(_tabs); var options = Ui.Scroll(sidebar); main.Children.Add(options); Grid.SetColumn(options, 1);
        main.SizeChanged += (_, e) => main.ColumnDefinitions[1].Width = new GridLength(e.NewSize.Width < 900 ? 280 : 330);
        var editorArea = new Grid { RowSpacing = 12 };
        editorArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); editorArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        editorArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        editorArea.Children.Add(Ui.Stack(5, _documentTitle, toolbar)); Grid.SetRow(_validation, 1); editorArea.Children.Add(_validation);
        Grid.SetRow(main, 2); editorArea.Children.Add(main);
        var pages = new Pivot(); pages.Items.Add(new PivotItem { Header = "编辑场景", Content = editorArea });
        pages.Items.Add(new PivotItem { Header = "预览与结果", Content = _result });
        _resultPivot = pages;
        var layout = new Grid { RowSpacing = 12 };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(Ui.Heading("场景工作台", "从资源开始创作，也可以直接使用 agent 生成的场景 JSON。"));
        Grid.SetRow(pages, 1); layout.Children.Add(pages); Content = layout;
        AddShortcut(VirtualKey.S, () => SaveAsync(false)); AddShortcut(VirtualKey.O, () => OpenAsync()); AddShortcut(VirtualKey.Enter, PreviewAsync);
        RebuildExportForm();
        if (File.Exists(DraftPath)) { _validation.Title = "发现自动保存的草稿"; _validation.Message = "可从工具栏的更多菜单恢复草稿，或直接打开其他项目。"; }
    }
    private readonly Pivot _resultPivot;

    private void AddShortcut(VirtualKey key, Func<Task> action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = VirtualKeyModifiers.Control };
        accelerator.Invoked += async (_, e) => { e.Handled = true; try { await action(); } catch (Exception error) { await _window.ShowErrorAsync(error.Message); } };
        KeyboardAccelerators.Add(accelerator);
    }

    private SceneDocument ParseCurrent()
    {
        if (string.IsNullOrWhiteSpace(_editor.Text)) throw new ArgumentException("请先粘贴 JSON、打开项目文件或新建场景。");
        if (_document is null || _document.FilePath != _path) _document = SceneDocument.Parse(_editor.Text, _path, _baseDirectory);
        else _document.ReplaceText(_editor.Text);
        return _document;
    }

    private void SetDocumentText(SceneDocument document)
    {
        _document = document; _path = document.FilePath; _baseDirectory = document.BaseDirectory;
        if (_exportForm is not null) _exportForm.DefaultCodec = document.Output.Codec;
        _settingText = true; _editor.Text = document.Text; _settingText = false;
        _documentTitle.Text = (_path is null ? "未命名场景" : Path.GetFileName(_path)) + (HasUnsavedChanges ? " · 未保存" : "");
        SaveDraft();
        if (_tabs.SelectedIndex == 1) RefreshVisualEditor();
    }

    private bool ValidateCurrent()
    {
        _validation.IsOpen = true;
        try
        {
            var validation = ParseCurrent().Validate();
            _validation.Severity = validation.HasErrors ? InfoBarSeverity.Error : InfoBarSeverity.Success;
            _validation.Title = validation.HasErrors ? "需要修正项目" : "JSON 结构检查通过";
            _validation.Message = validation.Summary + (validation.Issues.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine,
                validation.Issues.Take(8).Select(i => i.Path + "：" + i.Message)) : "");
            return !validation.HasErrors;
        }
        catch (SceneDocumentParseException error)
        {
            _validation.Severity = InfoBarSeverity.Error; _validation.Title = $"JSON 第 {error.Error.Line} 行、第 {error.Error.Column} 列有问题";
            _validation.Message = error.Error.Message;
            _editor.Select(Math.Clamp(error.Error.Offset, 0, _editor.Text.Length), 0); return false;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        { _validation.Severity = InfoBarSeverity.Error; _validation.Title = "尚未完成检查"; _validation.Message = error.Message; return false; }
    }

    internal async Task OpenAsync(string? path = null)
    { path ??= await _window.PickFileAsync(extensions: [".json"]); if (path is not null) await OpenPathAsync(path); }

    internal async Task OpenPathAsync(string path)
    {
        if (!await MayReplaceAsync()) return;
        var document = SceneDocument.Open(path); _savedText = document.Text; _path = Path.GetFullPath(path);
        SetDocumentText(document); _window.Settings.RememberProject(path); _window.SaveSettings(); ValidateCurrent();
        _resultPivot.SelectedIndex = 0;
    }

    private async Task<bool> MayReplaceAsync() => !HasUnsavedChanges || string.IsNullOrWhiteSpace(_editor.Text)
        || await _window.ConfirmAsync("替换当前场景？", "当前修改尚未保存到项目文件。请先保存需要保留的版本，或继续替换。", "替换");

    private async Task PasteAsync()
    {
        var content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.Text)) throw new ArgumentException("剪贴板里没有文本。请先复制场景 JSON。");
        await ReplaceEditorAsync(await content.GetTextAsync());
    }

    private async Task ReplaceEditorAsync(string text)
    {
        if (!await MayReplaceAsync()) return;
        _path = null; _document = null; _savedText = ""; _baseDirectory = Environment.CurrentDirectory;
        try { var document = SceneDocument.Parse(text, baseDirectory: _baseDirectory); SetDocumentText(document); }
        catch (SceneDocumentParseException) { _editor.Text = text; }
        ValidateCurrent(); SaveDraft();
    }

    private async Task SaveAsync(bool saveAs)
    {
        var document = ParseCurrent();
        string? path = _path;
        if (saveAs || path is null) path = await _window.PickFileAsync(true, Path.GetFileName(_path ?? "scene.json"), [".json"]);
        if (path is null) return;
        document.Save(path); _savedText = document.Text; SetDocumentText(document);
        _window.Settings.RememberProject(path); _window.SaveSettings();
    }

    internal void SaveDraft()
    {
        if (string.IsNullOrWhiteSpace(_editor.Text)) return;
        try
        {
            DesktopSettings.AtomicWrite(DraftPath, new JsonObject { ["text"] = _editor.Text, ["path"] = _path, ["baseDirectory"] = _baseDirectory,
                ["savedText"] = _savedText, ["updatedAt"] = DateTimeOffset.Now.ToString("O") }.ToJsonString(DesktopSettings.JsonOptions));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { _documentTitle.Text = "草稿暂时无法保存：" + error.Message; }
    }

    private async Task RestoreDraftAsync()
    {
        if (!File.Exists(DraftPath)) throw new FileNotFoundException("没有可恢复的草稿。");
        if (!await MayReplaceAsync()) return;
        var draft = JsonNode.Parse(await File.ReadAllTextAsync(DraftPath))!;
        _path = draft["path"]?.ToString(); _baseDirectory = draft["baseDirectory"]?.ToString() ?? Environment.CurrentDirectory;
        _savedText = draft["savedText"]?.ToString() ?? ""; _document = null; _editor.Text = draft["text"]?.ToString() ?? ""; ValidateCurrent();
    }

    private async Task NewAsync()
    {
        var template = Ui.Choice("模板", SceneTemplates.All.Select(t => (t.Key, t.Label)), "transparent-actor");
        var hint = Ui.Text(SceneTemplates.Get("transparent-actor").Description, 12, true);
        template.SelectionChanged += (_, _) => hint.Text = SceneTemplates.Get(Ui.Selected(template)).Description;
        var dialog = new ContentDialog { XamlRoot = _window.RootXaml, Title = "新建场景", PrimaryButtonText = "选择素材", CloseButtonText = "返回",
            Content = Ui.Stack(14, template, hint, Ui.Text("接下来从归档中选择来源剧情和人物模型。", 12, true)) };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (!await MayReplaceAsync()) return;
        var inputs = new Dictionary<string, string?>();
        var adv = await ResourcePicker.PickAsync(_window, CommandCatalog.Get("story.show").Fields.First(f => f.Key == "advId"), inputs);
        if (adv is null) return;
        inputs["site"] = adv.RelatedValues.GetValueOrDefault("site") ?? _window.Settings.Site;
        string? model = null, second = null;
        if (Ui.Selected(template) != "story-patch")
        {
            var selected = await ResourcePicker.PickAsync(_window, CommandCatalog.Get("model.show").Fields.First(f => f.Key == "modelId"), inputs);
            if (selected is null) return; model = selected.Value;
        }
        if (Ui.Selected(template) == "duet-background")
        {
            var selected = await ResourcePicker.PickAsync(_window, CommandCatalog.Get("model.show").Fields.First(f => f.Key == "modelId") with { Label = "第二个人物模型" }, inputs);
            if (selected is null) return; second = selected.Value;
        }
        var document = SceneTemplates.Create(Ui.Selected(template), new SceneSource(adv.Value, adv.RelatedValues.GetValueOrDefault("site") ?? _window.Settings.Site, _window.Settings.Language), model, second);
        _savedText = ""; _path = null; SetDocumentText(document); ValidateCurrent(); _tabs.SelectedIndex = 1;
    }

    private void RebuildExportForm()
    {
        string id = Ui.Selected(_operation); if (id.Length == 0) return;
        SaveForm();
        _window.Settings.Forms.TryGetValue("scene:" + id, out var values);
        var definition = CommandCatalog.Get(id);
        if (id != "shot.init") definition = definition with { Fields = definition.Fields.Where(f => f.Key != "project").ToArray() };
        _exportForm = new CommandForm(_window, definition, values) { DefaultCodec = _document?.Output.Codec }; _exportFormHost.Content = _exportForm;
    }

    internal void SaveForm()
    { if (_exportForm is not null) _window.Settings.Forms["scene:" + _exportForm.Definition.Id] = _exportForm.Values(); }

    internal async void SelectOperation(string id, IReadOnlyDictionary<string, string?>? values)
    {
        try
        {
            var item = _operation.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag?.ToString() == id);
            if (item is not null) _operation.SelectedItem = item;
            if (values is not null) foreach (var pair in values)
            {
                if (pair.Key == "project" && pair.Value is { } path) await OpenAsync(path);
                else _exportForm?.SetValue(pair.Key, pair.Value);
            }
        }
        catch (Exception error) { await _window.ShowErrorAsync(error.Message); }
    }

    private async Task RunSelectedAsync()
    {
        string id = Ui.Selected(_operation); if (_exportForm is null) return;
        var values = _exportForm.Values(); _window.Settings.Forms["scene:" + id] = values; _window.SaveSettings();
        if (id == "shot.init")
        {
            var result = await _window.RunAsync(id, values); _result.Show(result, id); _resultPivot.SelectedIndex = 1; return;
        }
        await RunDocumentAsync(id, values);
    }

    private async Task PreviewAsync()
    {
        if (!ValidateCurrent()) return;
        var output = ParseCurrent().Output;
        var selected = _exportForm?.Values() ?? new Dictionary<string, string?>();
        int Dimension(string key, int fallback, int max)
        {
            if (!selected.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value)) return fallback;
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int dimension) || dimension < 16 || dimension > max)
                throw new ArgumentException("预览的" + (key == "width" ? "宽度" : "高度") + "必须为 16 至 " + max + " 的整数。");
            return dimension;
        }
        int sourceWidth = Dimension("width", output.Width, 7680), sourceHeight = Dimension("height", output.Height, 4320);
        double scale = Math.Min(1, 960d / Math.Max(sourceWidth, sourceHeight));
        int width = Math.Max(16, (int)Math.Round(sourceWidth * scale)), height = Math.Max(16, (int)Math.Round(sourceHeight * scale));
        var path = Path.Combine(DesktopSettings.DataDirectory, "previews", "scene-" + Guid.NewGuid().ToString("N") + ".png");
        var accepted = CommandCatalog.Get("render.frame").Fields.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        var values = selected.Where(p => accepted.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        values["out"] = path; values["width"] = width.ToString(CultureInfo.InvariantCulture); values["height"] = height.ToString(CultureInfo.InvariantCulture);
        values["supersample"] = "1"; values.Remove("overwrite");
        await RunDocumentAsync("render.frame", values);
    }

    private async Task RunDocumentAsync(string id, Dictionary<string, string?> values)
    {
        if (!ValidateCurrent()) return;
        var document = ParseCurrent(); using var snapshot = document.CreateSnapshot(Path.Combine(DesktopSettings.DataDirectory, "snapshots"));
        values["project"] = snapshot.FilePath;
        var context = _window.Settings.Context(document.BaseDirectory) with { Site = null, Language = null };
        var build = CommandLineBuilder.Build(id, values, context); _exportForm?.ShowErrors(build.Errors); if (!build.IsValid) return;
        _result.SetBusy(CommandCatalog.Get(id).Label); _resultPivot.SelectedIndex = 1;
        var result = await _window.RunAsync(id, values, context); _result.Show(result, id, values);
    }

    internal async Task SetSourceAsync(string adv, string? site)
    {
        if (string.IsNullOrWhiteSpace(_editor.Text)) SetDocumentText(SceneTemplates.Create("story-patch", new SceneSource(adv, site ?? _window.Settings.Site, _window.Settings.Language)));
        else { var document = ParseCurrent(); document.SetSource(adv: adv, site: site); SetDocumentText(document); }
        ValidateCurrent(); _resultPivot.SelectedIndex = 0; await Task.CompletedTask;
    }

    private void CopyAgentInstructions()
    {
        string text = "请为 OurNotes Director 生成完整场景 JSON 对象，可直接粘贴到 WinUI 场景工作台。\n"
            + "统一格式：format=ournotes-director/project，version=1；source 包含 adv、site、language；output 包含 width、height、supersample、codec、cineformQuality、clean、letterbox。\n"
            + "使用现有游戏指令及 Director.Actor、Director.Camera、Director.Parameter、Director.Scene；原始字段名与大小写必须保留。省略 initialize/commands/finalize 表示继承原剧情，空数组表示清空对应段。不要虚构素材路径或模型动作名称。\n"
            + "透明视频选 CineForm film1；宽为16的倍数，高为8的倍数且至少32。模型与动作可从软件的角色模型页查询；主页动画由主页动画页独立导出。\n"
            + "JSON 结构正确之后还需在软件中校验素材与预览。参考当前项目：\n" + _editor.Text;
        MainWindow.Copy(text);
    }

    internal async Task RunSmokeTestAsync(Action<bool, string> check)
    {
        var sample = SceneTemplates.Create("transparent-actor", "10060", "adv_live2d_hotaru_016_casual_spring_01");
        check(!HasUnsavedChanges || string.IsNullOrWhiteSpace(_editor.Text), "隔离测试中的场景编辑器无需替换确认");
        var clipboard = new DataPackage(); clipboard.SetText("```json\n" + sample.Text + "\n```");
        Clipboard.SetContent(clipboard); Clipboard.Flush();
        await PasteAsync();
        check(ValidateCurrent(), "通过真实剪贴板粘贴的 JSON 代码块在编辑器中通过结构检查");
        var pasted = ParseCurrent();
        check(pasted.Kind == SceneDocumentKind.UnifiedProject && JsonNode.DeepEquals(pasted.Root, sample.Root)
            && !_editor.Text.Contains("```", StringComparison.Ordinal), "粘贴处理移除代码围栏并完整保留场景 JSON 参数");
        pasted.SetOutput(width: 1280, height: 720); SetDocumentText(pasted); _tabs.SelectedIndex = 1; RefreshVisualEditor(); UpdateLayout();
        await Task.Delay(80); UpdateLayout();
        check(_visualHost.Content is FrameworkElement { IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0 }, "场景可视化参数编辑已实际创建并布局");
        static NumberBox? FindNumber(DependencyObject node, string header)
        {
            if (node is NumberBox number && number.Header?.ToString() == header) return number;
            int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
                if (FindNumber(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, i), header) is { } child) return child;
            return null;
        }
        check(FindNumber(_visualHost, "宽度")?.Value == 1280 && FindNumber(_visualHost, "高度")?.Value == 720
            && ParseCurrent().Output is { Width: 1280, Height: 720 }, "可视化宽高控件与粘贴后的 JSON 文档一致");
        _tabs.SelectedIndex = 0; _resultPivot.SelectedIndex = 0;
        check(ParseCurrent().Output is { Codec: "cineform", CineformQuality: "film1" }
            && _editor.Text.Contains("cineform", StringComparison.Ordinal), "剪贴板和参数编辑保留透明 CineForm film1 输出设置");
    }
}
