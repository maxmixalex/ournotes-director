using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.WinUI;

internal sealed class CommandPage : UserControl
{
    private readonly MainWindow _window;
    private readonly CommandGroup _group;
    private readonly ComboBox _operations;
    private readonly ContentControl _formHost = new();
    private readonly TextBlock _description = Ui.Text("", 14, true);
    private readonly ResultView _results;
    private CommandForm? _form;
    private string _currentId = "";
    private readonly Button _run;
    private readonly InfoBar _notice = new() { IsClosable = true };

    public CommandPage(MainWindow window, CommandGroup group)
    {
        _window = window; _group = group;
        var commands = CommandCatalog.ForGroup(group);
        _operations = Ui.Choice("要做什么", commands.Select(c => (c.Id, c.Label)));
        _operations.SelectionChanged += (_, _) => LoadSelected();
        _run = Ui.AsyncButton("运行", RunAsync, "\uE768", true);
        _results = new ResultView(window, (id, values) => window.OpenCommand(id, values));
        _results.PageRequested += async offset =>
        {
            try { _form?.SetValue("offset", offset.ToString(System.Globalization.CultureInfo.InvariantCulture)); await RunAsync(); }
            catch (Exception error) { await window.ShowErrorAsync(error.Message); }
        };
        var left = Ui.Stack(18, _operations, _description, _formHost, Ui.Row(10, _run,
            Ui.Button("查看任务", () => window.Navigate("tasks"), "\uE9F9")), _notice);
        string intro = group switch
        {
            CommandGroup.Archive => "选择乐队和保存位置，下载所需素材。再次运行会自动校验并增量更新。",
            CommandGroup.Home => "浏览主页、选择角色，把完整动画或透明分层素材导出到剪辑软件。",
            CommandGroup.Stories => "按标题或台词找到剧情，查看演出、提取配音与字幕，再导出所需片段。",
            CommandGroup.Models => "查找角色，预览动作和表情，获取可用于场景 JSON 的名称与参数。",
            CommandGroup.Assets => "浏览舞台、插图、边框和特效，搜索并提取原始素材。",
            CommandGroup.Media => "检查、裁剪或转换本地视频，合并音轨与字幕，检查透明图片。",
            _ => "检查运行组件、素材和项目。详细结果可复制或保存，便于排查问题。"
        };
        var layout = new Grid { RowSpacing = 20 };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(Ui.Heading(CommandCatalog.GroupLabel(group), intro));
        var columns = new Grid { ColumnSpacing = 22 };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(390) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var formScroll = Ui.Scroll(left); formScroll.Padding = new Thickness(0, 0, 8, 14);
        columns.Children.Add(formScroll); columns.Children.Add(_results); Grid.SetColumn(_results, 1);
        columns.SizeChanged += (_, e) => columns.ColumnDefinitions[0].Width = new GridLength(e.NewSize.Width < 850 ? Math.Max(280, e.NewSize.Width * .47) : 390);
        Grid.SetRow(columns, 1); layout.Children.Add(columns); Content = layout;
        if (commands.Count > 0) SelectCommand(commands[0].Id);
    }

    internal void SelectCommand(string id, IReadOnlyDictionary<string, string?>? values = null)
    {
        var selected = _operations.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag?.ToString() == id);
        if (selected is null) return;
        if (!ReferenceEquals(_operations.SelectedItem, selected)) _operations.SelectedItem = selected;
        else LoadSelected();
        if (values is not null && _form is not null) foreach (var pair in values) _form.SetValue(pair.Key, pair.Value);
    }

    private void LoadSelected()
    {
        SaveForm(); _currentId = Ui.Selected(_operations);
        if (_currentId.Length == 0) return;
        var definition = CommandCatalog.Get(_currentId);
        _description.Text = definition.Description;
        _window.Settings.Forms.TryGetValue(_currentId, out var values);
        _form = new CommandForm(_window, definition, values); _formHost.Content = _form;
        if (_run is not null) _run.Content = Ui.Row(8, new FontIcon { Glyph = "\uE768", FontSize = 14 }, new TextBlock
            { Text = definition.OutputKind is CommandOutputKind.Image or CommandOutputKind.Video or CommandOutputKind.Audio or CommandOutputKind.Directory or CommandOutputKind.File ? "开始导出" : "查看结果" });
        _notice.IsOpen = false;
    }

    internal void SaveForm()
    { if (_form is not null && _currentId.Length > 0) { _window.Settings.Forms[_currentId] = _form.Values(); _window.SaveSettings(); } }

    private async Task RunAsync()
    {
        if (_form is null) return;
        var id = _currentId; var values = _form.Values();
        var context = _window.Settings.Context();
        if (id is "media.clip" or "media.mux" && values.GetValueOrDefault("input") is { Length: > 0 } input && File.Exists(input))
        {
            var probe = await _window.QueryAsync("media.probe", new Dictionary<string, string?> { ["input"] = input });
            if (!probe.Success) throw new ArgumentException(probe.Error);
            var streams = probe.Result?["streams"] as System.Text.Json.Nodes.JsonArray;
            var video = streams?.OfType<System.Text.Json.Nodes.JsonObject>().FirstOrDefault(s => s["codec_type"]?.ToString() == "video");
            if (video is not null) context = context with
            {
                SourceWidth = (int?)video["width"], SourceHeight = (int?)video["height"], SourceCodec = video["codec_name"]?.ToString(),
                SourceHasAlpha = video["pix_fmt"]?.ToString() is { } pixel ? pixel.Contains("rgba", StringComparison.Ordinal) || pixel.Contains("bgra", StringComparison.Ordinal)
                    || pixel.StartsWith("gbrap", StringComparison.Ordinal) || pixel.StartsWith("yuva", StringComparison.Ordinal) : null
            };
        }
        var build = CommandLineBuilder.Build(id, values, context);
        _form.ShowErrors(build.Errors); if (!build.IsValid) return;
        SaveForm(); _notice.Severity = InfoBarSeverity.Informational; _notice.Title = "任务已加入队列";
        _notice.Message = "可以继续浏览其他页面；进度与取消操作在“任务与导出”中。"; _notice.IsOpen = true;
        _results.SetBusy(CommandCatalog.Get(id).Label);
        var result = await _window.RunAsync(id, values, context);
        _results.Show(result, id, values); _notice.Severity = result.Success ? InfoBarSeverity.Success : result.Cancelled ? InfoBarSeverity.Informational : InfoBarSeverity.Error;
        _notice.Title = result.Success ? "已完成" : result.Cancelled ? "已取消" : "任务未完成";
        _notice.Message = result.Error ?? (result.OutputPath is null ? "结果已显示在右侧。" : result.OutputPath); _notice.IsOpen = true;
    }
}
