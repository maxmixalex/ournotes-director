using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using OurNotes.Director.Desktop.Core;
using Windows.Media.Core;
using Windows.UI;

namespace OurNotes.Director.WinUI;

internal sealed class ResultView : UserControl
{
    private readonly MainWindow _window;
    private readonly Action<string, IReadOnlyDictionary<string, string?>> _openCommand;
    private readonly StackPanel _body = Ui.Stack(16);
    private readonly TextBlock _title = Ui.Text("结果与预览", 20);
    private readonly InfoBar _info = new() { IsClosable = true };
    private EngineResult? _result;
    private string _command = "";
    private MediaPlayerElement? _player;
    private CancellationTokenSource? _previewCancellation = new();
    private (string Path, ContentControl Host)? _automaticPreview;
    private bool _windowClosed;
    internal event Action<int>? PageRequested;

    internal ResultView(MainWindow window, Action<string, IReadOnlyDictionary<string, string?>> openCommand)
    {
        _window = window; _openCommand = openCommand;
        Content = Ui.Scroll(Ui.Stack(16, _title, _info, _body));
        _body.Children.Add(Ui.Card(Ui.Stack(10, new FontIcon { Glyph = "\uE8B9", FontSize = 36, Opacity = .4, HorizontalAlignment = HorizontalAlignment.Left },
            Ui.Text("选择资源或运行操作，结果会显示在这里。", 15, true), Ui.Text("图片支持透明棋盘预览；导出完成后可直接打开文件或所在目录。", 12, true))));
        Loaded += (_, _) =>
        {
            if (_windowClosed) return;
            bool resume = _previewCancellation is null;
            _previewCancellation ??= new();
            if (resume && _automaticPreview is { } preview && preview.Host.Content is null) _ = ShowFileSafelyAsync(preview.Path, preview.Host);
        };
        Unloaded += (_, _) => CancelPreview();
        window.Closed += (_, _) => { _windowClosed = true; CancelPreview(); };
    }

    internal void SetBusy(string title)
    { RestartPreview(); _title.Text = title; _info.Severity = InfoBarSeverity.Informational; _info.Title = "正在处理"; _info.Message = "任务会在后台运行。"; _info.IsOpen = true; }

    private CancellationToken PreviewToken => _previewCancellation?.Token ?? new CancellationToken(true);

    private void CancelPreview()
    {
        var cancellation = _previewCancellation; _previewCancellation = null;
        if (cancellation is not null)
        {
            try { cancellation.Cancel(); }
            finally { cancellation.Dispose(); }
        }
        _player?.MediaPlayer?.Pause();
    }

    private CancellationToken RestartPreview()
    {
        CancelPreview();
        if (!_windowClosed) _previewCancellation = new();
        return PreviewToken;
    }

    internal void Show(EngineResult result, string command, IReadOnlyDictionary<string, string?>? context = null)
    {
        RestartPreview(); _player = null; _automaticPreview = null;
        _result = result; _command = command; _body.Children.Clear();
        _title.Text = CommandCatalog.All.FirstOrDefault(c => c.Id == command)?.Label ?? "任务结果";
        _info.IsOpen = !result.Success; _info.Title = result.Cancelled ? "任务已取消" : "任务未完成";
        _info.Message = result.Error ?? ""; _info.Severity = result.Cancelled ? InfoBarSeverity.Informational : InfoBarSeverity.Error;
        if (result.OutputPath is { } path && (File.Exists(path) || Directory.Exists(path))) AddOutput(path);
        if (result.Result is JsonObject obj) AddSummary(obj);
        var rows = ResultRow.Read(result.Json, command, context);
        if (rows.Count > 0) AddRows(rows);
        if (result.Result is JsonObject page && page["nextOffset"] is JsonValue next && next.TryGetValue<int>(out int offset))
            _body.Children.Add(Ui.Button("下一页", () => PageRequested?.Invoke(offset), "\uE72A"));
        var details = new TextBox { Text = result.StandardOutput, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, MinHeight = 180, MaxHeight = 420 };
        ScrollViewer.SetHorizontalScrollBarVisibility(details, ScrollBarVisibility.Auto);
        var toolbar = Ui.Row(8, Ui.Button("复制结果 JSON", () => MainWindow.Copy(result.StandardOutput), "\uE8C8"),
            Ui.AsyncButton("保存结果…", async () =>
            { var file = await _window.PickFileAsync(true, "result.json", [".json"]); if (file is not null) DesktopSettings.AtomicWrite(file, result.StandardOutput); }, "\uE74E"));
        _body.Children.Add(new Expander { Header = "完整结果 JSON", Content = Ui.Stack(12, toolbar, details), HorizontalAlignment = HorizontalAlignment.Stretch,
            IsExpanded = rows.Count == 0 && result.OutputPath is null });
        if (result.Log.Length > 0)
            _body.Children.Add(new Expander { Header = "运行日志", HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = Ui.Stack(8, Ui.Button("复制日志", () => MainWindow.Copy(result.Log)),
                    new TextBox { Text = result.Log, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 260, FontSize = 12 }) });
    }

    private void AddSummary(JsonObject obj)
    {
        var labels = new Dictionary<string, string>
        {
            ["total"] = "匹配结果", ["spotId"] = "主页", ["advId"] = "剧情", ["frameCount"] = "总帧数", ["duration"] = "时长（秒）",
            ["sourceDuration"] = "原动画长度（秒）", ["width"] = "宽度", ["height"] = "高度", ["fps"] = "帧率", ["codec"] = "视频编码",
            ["cineformQuality"] = "CineForm 质量", ["fileCount"] = "导出文件数", ["spotCount"] = "主页数", ["verified"] = "校验通过",
            ["includesSourceEnd"] = "包含动画结束帧", ["incremental"] = "增量更新", ["complete"] = "完整输出", ["finished"] = "演出结束"
        };
        var summary = Ui.Stack(6);
        foreach (var (key, label) in labels)
            if (obj[key] is JsonValue value) summary.Children.Add(Ui.Text(label + "：" + (value.ToString() switch { "true" => "是", "false" => "否", var text => text }), 13));
        if (summary.Children.Count > 0) _body.Children.Add(Ui.Card(summary, 16));
    }

    private void AddRows(IReadOnlyList<ResultRow> rows)
    {
        var search = Ui.Input("筛选本页", "", "按标题、角色或编号筛选");
        var list = new ListView { MaxHeight = 440, SelectionMode = ListViewSelectionMode.Single };
        var actions = Ui.Stack(10);
        void Fill()
        {
            list.Items.Clear();
            foreach (var row in rows.Where(r => r.SearchText.Contains(search.Text, StringComparison.OrdinalIgnoreCase)))
                list.Items.Add(new ListViewItem { Tag = row, Content = Ui.Stack(4, Ui.Text(row.Title, 15), Ui.Text(row.Subtitle, 12, true)), HorizontalContentAlignment = HorizontalAlignment.Stretch });
        }
        search.TextChanged += (_, _) => Fill(); Fill();
        list.SelectionChanged += (_, _) =>
        {
            actions.Children.Clear();
            if ((list.SelectedItem as ListViewItem)?.Tag is not ResultRow row) return;
            actions.Children.Add(Ui.Text(row.Title, 17));
            var buttons = Ui.Stack(8);
            foreach (var action in row.Actions(_command)) buttons.Children.Add(Ui.Button(action.Label, () => _openCommand(action.CommandId, action.Values)));
            string? site = row.Context.GetValueOrDefault("site");
            string? adv = row.Context.GetValueOrDefault("advId") ?? row.Context.GetValueOrDefault("adv");
            if (adv is { Length: > 0 } && row.Kind is "story" or "command" or "dialogue" or "voice.group" or "voice")
                buttons.Children.Add(Ui.AsyncButton("用作场景来源", async () => { _window.Navigate("scenes"); await _window.Scene.SetSourceAsync(adv, site); }));
            if (row.Data is JsonObject unsupported && unsupported["unsupportedReason"] is JsonValue reason)
                actions.Children.Add(Ui.Text("独立导出暂不可用：" + reason, 12, true));
            if (row.Data is JsonObject exported && exported["output"] is JsonValue outputValue)
            {
                string path = outputValue.ToString();
                if (!Path.IsPathRooted(path) && _result?.OutputPath is { } root && Directory.Exists(root)) path = Path.GetFullPath(path, root);
                if (File.Exists(path) || Directory.Exists(path)) buttons.Children.Add(Ui.Button("打开此输出", () => MainWindow.OpenPath(path), "\uE8A7"));
            }
            if (row.Id.Length > 0) buttons.Children.Add(Ui.Button(row.Kind == "parameter" ? "复制参数名" : "复制编号或名称", () => MainWindow.Copy(row.Id)));
            buttons.Children.Add(Ui.Button("复制所选 JSON", () => MainWindow.Copy(row.Data.ToJsonString(DesktopSettings.JsonOptions)), "\uE8C8"));
            actions.Children.Add(buttons);
            actions.Children.Add(new Expander { Header = "所选条目数据", HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = new TextBox { Text = row.Data.ToJsonString(DesktopSettings.JsonOptions), AcceptsReturn = true, IsReadOnly = true, MaxHeight = 260, TextWrapping = TextWrapping.Wrap, FontSize = 12 } });
        };
        _body.Children.Add(Ui.Stack(12, search, list, actions));
    }

    private void AddOutput(string path)
    {
        var buttons = Ui.Row(8, Ui.Button(Directory.Exists(path) ? "打开输出目录" : "打开文件", () => MainWindow.OpenPath(path), "\uE8A7"),
            Ui.Button("所在目录", () => MainWindow.OpenPath(Directory.Exists(path) ? path : Path.GetDirectoryName(path)!), "\uE838"));
        if (_command == "shot.init" && File.Exists(path) && Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
            buttons.Children.Add(Ui.AsyncButton("在场景工作台打开", async () =>
            { _window.Navigate("scenes"); await _window.Scene.OpenPathAsync(path); }, "\uE8A5"));
        _body.Children.Add(Ui.Stack(8, buttons, Ui.Text(path, 12, true)));
        if (Directory.Exists(path))
        {
            var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Take(200).ToArray();
            var combo = Ui.Choice("导出文件", files.Select(p => (p, Path.GetRelativePath(path, p))));
            var previewHost = new ContentControl();
            _body.Children.Add(Ui.Stack(10, combo, Ui.AsyncButton("预览所选文件", async () =>
                await ShowFileAsync(Ui.Selected(combo), previewHost, RestartPreview())), previewHost));
        }
        else
        {
            var preview = new ContentControl(); _body.Children.Add(preview);
            _automaticPreview = (path, preview);
            _ = ShowFileSafelyAsync(path, preview);
        }
    }

    private async Task ShowFileSafelyAsync(string path, ContentControl host)
    {
        var token = PreviewToken;
        try { await ShowFileAsync(path, host, token); }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!token.IsCancellationRequested) host.Content = Ui.Text("预览暂不可用：" + error.Message, 12, true); }
    }

    private async Task ShowFileAsync(string path, ContentControl host, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".png")
        {
            var bitmap = new BitmapImage(); var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = await file.OpenReadAsync(); await bitmap.SetSourceAsync(stream);
            cancellationToken.ThrowIfCancellationRequested();
            var image = new Image { Source = bitmap, Stretch = Stretch.Uniform, MaxHeight = 520, MinHeight = 200 };
            var checker = new Canvas(); var grid = new Grid { MinHeight = 200, MaxHeight = 520, CornerRadius = new CornerRadius(6) };
            grid.Children.Add(checker); grid.Children.Add(image);
            grid.SizeChanged += (_, e) =>
            {
                checker.Children.Clear();
                for (int y = 0; y < e.NewSize.Height; y += 22) for (int x = 0; x < e.NewSize.Width; x += 22)
                {
                    var cell = new Microsoft.UI.Xaml.Shapes.Rectangle { Width = 22, Height = 22,
                        Fill = new SolidColorBrush((x / 22 + y / 22) % 2 == 0 ? Color.FromArgb(255, 208, 208, 208) : Color.FromArgb(255, 238, 238, 238)) };
                    Canvas.SetLeft(cell, x); Canvas.SetTop(cell, y); checker.Children.Add(cell);
                }
            };
            host.Content = Ui.Stack(8, Ui.Text("透明棋盘预览", 12, true), grid);
        }
        else if (extension is ".mov" or ".mkv" or ".mp4" or ".webm")
        {
            var display = new ContentControl();
            string ffmpeg = string.IsNullOrWhiteSpace(_window.Settings.FFmpegPath) ? Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe") : _window.Settings.FFmpegPath;
            host.Content = Ui.Stack(10, Ui.Text("可生成播放代理，或检查第一帧的真实透明通道。原始导出文件不会改变。", 12, true),
                Ui.Row(8, Ui.AsyncButton("播放预览", async () =>
                {
                    var token = RestartPreview(); token.ThrowIfCancellationRequested();
                    var spinner = new ProgressRing { IsActive = true, Width = 30, Height = 30 }; display.Content = spinner;
                    try
                    {
                        var proxy = await MediaPreview.CreateAsync(path, ffmpeg, false, token);
                        token.ThrowIfCancellationRequested();
                        _player?.MediaPlayer?.Pause(); _player = new MediaPlayerElement { AreTransportControlsEnabled = true, AutoPlay = false, Height = 340 };
                        _player.Source = MediaSource.CreateFromUri(new Uri(proxy)); display.Content = _player;
                    }
                    finally { if (ReferenceEquals(display.Content, spinner)) display.Content = null; }
                }, "\uE768"), Ui.AsyncButton("检查透明帧", async () =>
                {
                    var token = RestartPreview(); token.ThrowIfCancellationRequested();
                    var frame = await MediaPreview.CreateAsync(path, ffmpeg, true, token);
                    await ShowFileAsync(frame, display, token);
                }, "\uE91B")), display);
        }
        else if (extension is ".wav" or ".m4a" or ".mp3" or ".ogg")
        {
            _player?.MediaPlayer?.Pause(); _player = new MediaPlayerElement { AreTransportControlsEnabled = true, AutoPlay = false, Height = 120,
                Source = MediaSource.CreateFromUri(new Uri(Path.GetFullPath(path))) }; host.Content = _player;
        }
        else if (extension is ".json" or ".srt" or ".txt")
        {
            string text = await File.ReadAllTextAsync(path, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            host.Content = new TextBox { Text = text, AcceptsReturn = true, IsReadOnly = true, MaxHeight = 420, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        }
        else host.Content = Ui.Text("此格式可通过“打开文件”交给相应程序查看。", 12, true);
    }
}
