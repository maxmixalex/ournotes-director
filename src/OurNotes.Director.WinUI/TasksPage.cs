using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.WinUI;

internal sealed class TasksPage : UserControl
{
    private readonly MainWindow _window;
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly ResultView _result;
    private readonly TextBlock _summary = Ui.Text("", 13, true);
    private readonly TextBlock _state = Ui.Text("选择任务查看详细结果", 16);
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, Height = 6, Visibility = Visibility.Collapsed };
    private readonly Button _cancel;
    private readonly Button _settings;
    private readonly Button _report;
    private readonly Dictionary<string, (ListViewItem Item, TextBlock Status)> _rows = new(StringComparer.Ordinal);
    private DesktopTask? _selected;
    private EngineResult? _shown;

    public TasksPage(MainWindow window)
    {
        _window = window; _result = new ResultView(window, window.OpenCommand);
        _cancel = Ui.Button("取消任务", () => { if (_selected is { } task) { window.Jobs.Cancel(task); _state.Text = "正在取消…"; } }, "\uE711");
        _settings = Ui.AsyncButton("查看原设置", RestoreSettingsAsync, "\uE713");
        _report = Ui.AsyncButton("打开任务记录", () => { if (_selected?.ReportPath is { } path) MainWindow.OpenPath(path); return Task.CompletedTask; }, "\uE8A5");
        _list.SelectionChanged += (_, _) => { _selected = (_list.SelectedItem as ListViewItem)?.Tag as DesktopTask; _shown = null; ShowSelected(); };
        var right = new Grid { RowSpacing = 12 };
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        right.Children.Add(Ui.Stack(12, _state, _progress, Ui.Row(8, _cancel, _settings, _report)));
        Grid.SetRow(_result, 1); right.Children.Add(_result);
        var columns = new Grid { ColumnSpacing = 20 };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.Children.Add(_list); columns.Children.Add(right); Grid.SetColumn(right, 1);
        var layout = new Grid { RowSpacing = 18 };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(Ui.Heading("任务与导出", "任务按顺序运行。这里保留最近的结果、错误信息和原设置，关闭后也可继续查看。"));
        Grid.SetRow(_summary, 1); layout.Children.Add(_summary); Grid.SetRow(columns, 2); layout.Children.Add(columns); Content = layout;
    }

    internal void Refresh()
    {
        var tasks = _window.Jobs.Tasks;
        string? selectedId = _selected?.Id;
        if (_rows.Count != tasks.Count || tasks.Any(t => !_rows.ContainsKey(t.Id)))
        {
            _rows.Clear(); _list.Items.Clear();
            foreach (var task in tasks)
            {
                var status = Ui.Text(Label(task), 12, true);
                var item = new ListViewItem { Tag = task, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Content = Ui.Stack(5, Ui.Text(task.Request.Title, 15), status,
                        Ui.Text(task.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.CurrentCulture), 11, true)) };
                _rows[task.Id] = (item, status); _list.Items.Add(item);
                if (task.Id == selectedId) _list.SelectedItem = item;
            }
            if (_list.SelectedIndex < 0 && _list.Items.Count > 0) _list.SelectedIndex = 0;
        }
        foreach (var task in tasks) if (_rows.TryGetValue(task.Id, out var row)) row.Status.Text = Label(task);
        int waiting = tasks.Count(t => t.Status == DesktopTaskStatus.Queued), running = tasks.Count(t => t.Status == DesktopTaskStatus.Running);
        _summary.Text = tasks.Count == 0 ? "尚无任务。从场景或资源页面运行一次操作后，记录会出现在这里。"
            : $"{running} 个正在处理 · {waiting} 个等待中 · {tasks.Count} 条记录";
        ShowSelected();
    }

    internal void RefreshProgress(DesktopTask task)
    {
        if (!_rows.ContainsKey(task.Id)) { Refresh(); return; }
        _rows[task.Id].Status.Text = Label(task);
        if (_selected?.Id == task.Id) ShowSelected();
        if (task.Status != DesktopTaskStatus.Running) Refresh();
    }

    private static string Label(DesktopTask task) => task.StatusLabel + (task.Status == DesktopTaskStatus.Running && task.Fraction is { } fraction ? $" · {fraction:P0}" : "");

    private void ShowSelected()
    {
        var task = _selected;
        bool active = task?.Status is DesktopTaskStatus.Queued or DesktopTaskStatus.Running;
        _cancel.IsEnabled = active; _settings.IsEnabled = task is not null; _report.IsEnabled = task?.ReportPath is { } path && File.Exists(path);
        _progress.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        _progress.IsIndeterminate = task?.Fraction is null; _progress.Value = task?.Fraction ?? 0;
        if (task is null) return;
        _state.Text = task.Request.Title + " · " + task.StatusLabel + Environment.NewLine + task.Message;
        if (task.Result is { } result && !ReferenceEquals(_shown, result))
        { _shown = result; _result.Show(result, task.Request.CommandId, ReadValues(task.Request)); }
        else if (active && _shown is null) _result.SetBusy(task.Request.Title);
    }

    private Task RestoreSettingsAsync()
    {
        if (_selected is not { } task) return Task.CompletedTask;
        _window.OpenCommand(task.Request.CommandId, ReadValues(task.Request));
        return Task.CompletedTask;
    }

    private static Dictionary<string, string?> ReadValues(EngineRequest request)
    {
        var definition = CommandCatalog.Get(request.CommandId); var args = request.Arguments;
        var positional = args.Skip(definition.CommandTokens.Count).TakeWhile(a => !a.StartsWith('-')).ToArray();
        // Restoring a task replaces the whole form. Omitted old options must not
        // inherit values retained from a more recent operation in that form.
        var values = definition.Fields.ToDictionary(field => field.Key, _ => (string?)null, StringComparer.Ordinal);
        foreach (var field in definition.Fields)
        {
            if (field.Position is { } position && position < positional.Length)
            { values[field.Key] = positional[position]; continue; }
            if (field.Option is not { } option) continue;
            int index = args.ToList().IndexOf(option);
            if (index >= 0) values[field.Key] = field.Kind == CommandFieldKind.Boolean ? "true" : index + 1 < args.Count ? args[index + 1] : null;
        }
        // No output is overwritten by restoring settings. The regular form validates
        // the chosen destination again before another task can start.
        if (definition.Fields.Any(f => f.Key == "overwrite")) values["overwrite"] = null;
        return values;
    }
}
