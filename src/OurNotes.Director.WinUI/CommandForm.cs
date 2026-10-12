using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.WinUI;

internal sealed class CommandForm : UserControl
{
    private readonly MainWindow _window;
    internal CommandDefinition Definition { get; }
    private readonly Dictionary<string, Func<string?>> _getters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Action<string?>> _setters = new(StringComparer.Ordinal);
    private readonly HashSet<string> _touched = new(StringComparer.Ordinal);
    private readonly InfoBar _errors = new() { Severity = InfoBarSeverity.Error, IsClosable = true };
    internal string? DefaultCodec { get; set; }

    public CommandForm(MainWindow window, CommandDefinition definition, IReadOnlyDictionary<string, string?>? values = null, bool includeOutput = true)
    {
        _window = window; Definition = definition;
        var basic = Ui.Stack(16); var advanced = Ui.Stack(16);
        foreach (var field in definition.Fields)
        {
            if (!includeOutput && (field.Key is "out" or "project" || field.Position == 0 && field.Kind == CommandFieldKind.InputFile)) continue;
            string? value = values?.GetValueOrDefault(field.Key);
            if (values?.ContainsKey(field.Key) == true) _touched.Add(field.Key);
            if (value is null && field.Key == "out" && field.Required && field.Kind != CommandFieldKind.OutputFile)
            {
                if (definition.Id == "archive") value = string.IsNullOrWhiteSpace(window.Settings.ArchivePath)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OurNotes Archive") : window.Settings.ArchivePath;
                else
                {
                    string name = field.SuggestedFileName ?? (field.Kind is CommandFieldKind.NewDirectory or CommandFieldKind.OutputDirectory ? "export" : "output.json");
                    if (field.Kind == CommandFieldKind.NewDirectory) name += "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                    value = Path.Combine(window.Settings.OutputDirectory, name);
                }
                _touched.Add(field.Key);
            }
            var control = MakeField(field, value);
            (field.Advanced ? advanced : basic).Children.Add(control);
        }
        var content = Ui.Stack(18, _errors, basic);
        if (advanced.Children.Count > 0) content.Children.Add(new Expander { Header = "更多选项", Content = advanced, HorizontalAlignment = HorizontalAlignment.Stretch });
        Content = content;
    }

    internal Dictionary<string, string?> Values() => _getters.Where(p => _touched.Contains(p.Key)
        && !(p.Key == "out" && string.IsNullOrWhiteSpace(p.Value()) && Definition.Fields.Any(f => f.Key == "out" && f.Required && f.Kind == CommandFieldKind.OutputFile)))
        .ToDictionary(p => p.Key, p => p.Value(), StringComparer.Ordinal);
    internal void SetValue(string key, string? value) { if (_setters.TryGetValue(key, out var setter)) { setter(value); _touched.Add(key); } }
    internal void ShowErrors(IEnumerable<CommandValidationError> errors)
    { _errors.Message = string.Join(Environment.NewLine, errors.Select(e => e.Message)); _errors.IsOpen = _errors.Message.Length > 0; }

    private FrameworkElement MakeField(CommandField field, string? value)
    {
        string label = field.Label + (field.Required ? " *" : "");
        string placeholder = field.Placeholder ?? (field.DefaultValue is { Length: > 0 } d ? "默认：" + d : "可留空");
        if (field.Key == "out" && field.Required && field.Kind == CommandFieldKind.OutputFile) placeholder = "自动命名，或选择文件…";
        FrameworkElement input;
        if (field.Kind == CommandFieldKind.Boolean)
        {
            var toggle = new CheckBox { Content = field.Label, IsChecked = value == "true" };
            toggle.Checked += (_, _) => _touched.Add(field.Key); toggle.Unchecked += (_, _) => _touched.Add(field.Key);
            _getters[field.Key] = () => toggle.IsChecked == true ? "true" : null;
            _setters[field.Key] = v => toggle.IsChecked = v == "true"; input = toggle;
        }
        else if (field.Kind == CommandFieldKind.Choice && !field.AllowCustomValue)
        {
            var options = new[] { ("", field.DefaultValue is { } defaultChoice ? "自动 / 默认（" + (field.Choices.FirstOrDefault(c => c.Value == defaultChoice)?.Label ?? defaultChoice) + "）" : "不限定 / 使用项目设置") }
                .Concat(field.Choices.Select(c => (c.Value, c.Label)));
            var box = Ui.Choice(label, options, value ?? "");
            box.SelectionChanged += (_, _) => _touched.Add(field.Key);
            _getters[field.Key] = () => Ui.Selected(box);
            _setters[field.Key] = v => { box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag?.ToString() == v) ?? box.Items[0]; };
            input = box;
        }
        else if (field.Kind == CommandFieldKind.MultiChoice && !field.AllowCustomValue)
        {
            var selected = (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
            var group = Ui.Stack(6, Ui.Text(label)); var boxes = new Dictionary<string, CheckBox>();
            foreach (var choice in field.Choices)
            {
                var box = new CheckBox { Content = choice.Label, IsChecked = selected.Contains(choice.Value) }; boxes[choice.Value] = box; group.Children.Add(box);
                box.Checked += (_, _) => _touched.Add(field.Key); box.Unchecked += (_, _) => _touched.Add(field.Key);
            }
            _getters[field.Key] = () => string.Join(',', boxes.Where(p => p.Value.IsChecked == true).Select(p => p.Key));
            _setters[field.Key] = v => { var set = (v ?? "").Split(',').ToHashSet(); foreach (var pair in boxes) pair.Value.IsChecked = set.Contains(pair.Key); };
            if (field.DefaultValue is { } defaults) group.Children.Add(Ui.Text("全部留空时使用默认：" + defaults, 12, true));
            input = group;
        }
        else if (field.Kind is CommandFieldKind.Integer or CommandFieldKind.Number)
        {
            var box = new NumberBox { Header = label, PlaceholderText = placeholder, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                SmallChange = field.Kind == CommandFieldKind.Integer ? 1 : .1, LargeChange = field.Kind == CommandFieldKind.Integer ? 10 : 1,
                Value = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : double.NaN,
                Minimum = field.Minimum ?? double.MinValue, Maximum = field.Maximum ?? double.MaxValue };
            box.ValueChanged += (_, _) => _touched.Add(field.Key);
            _getters[field.Key] = () => double.IsNaN(box.Value) ? null : box.Value.ToString("G", CultureInfo.InvariantCulture);
            _setters[field.Key] = v => box.Value = double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : double.NaN;
            input = box;
        }
        else
        {
            var box = Ui.Input(label, value ?? "", placeholder);
            box.TextChanged += (_, _) => _touched.Add(field.Key);
            _getters[field.Key] = () => box.Text; _setters[field.Key] = v => box.Text = v ?? "";
            bool path = field.Kind is CommandFieldKind.InputFile or CommandFieldKind.InputDirectory or CommandFieldKind.OutputFile or CommandFieldKind.OutputDirectory or CommandFieldKind.NewDirectory;
            if (path || field.LookupCommandId is not null || field.Choices.Count > 0)
            {
                var grid = new Grid { ColumnSpacing = 8 };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.Children.Add(box);
                var button = Ui.AsyncButton(path ? "浏览…" : "选择…", async () =>
                {
                    if (path)
                    {
                        string? chosen;
                        if (field.Kind is CommandFieldKind.InputDirectory or CommandFieldKind.OutputDirectory or CommandFieldKind.NewDirectory)
                        {
                            chosen = await _window.PickFolderAsync();
                            if (chosen is not null && field.Kind == CommandFieldKind.NewDirectory)
                                chosen = Path.Combine(chosen, (field.SuggestedFileName ?? "export") + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            string? name = Path.GetFileName(box.Text.Length > 0 ? box.Text : field.SuggestedFileName);
                            IReadOnlyList<string> extensions = field.FileExtensions;
                            if (field.Key == "out" && Definition.Id.EndsWith(".clip", StringComparison.Ordinal))
                            {
                                string? codec = _getters.GetValueOrDefault("codec")?.Invoke();
                                if (string.IsNullOrWhiteSpace(codec)) codec = DefaultCodec ?? Definition.Fields.FirstOrDefault(f => f.Key == "codec")?.DefaultValue ?? "h264";
                                string extension = codec switch { "h264" => ".mp4", "ffv1" => ".mkv", _ => ".mov" };
                                if (box.Text.Length == 0) name = Path.ChangeExtension(name, extension);
                                extensions = extensions.OrderBy(e => e == extension ? 0 : 1).ToArray();
                            }
                            chosen = await _window.PickFileAsync(field.Kind == CommandFieldKind.OutputFile, name, extensions);
                        }
                        if (chosen is not null) box.Text = chosen;
                    }
                    else if (field.LookupCommandId is not null)
                    {
                        var selected = await ResourcePicker.PickAsync(_window, field, Values());
                        if (selected is not null)
                        {
                            box.Text = selected.Value;
                            foreach (var pair in selected.RelatedValues) SetValue(pair.Key, pair.Value);
                        }
                    }
                    else
                    {
                        var combo = Ui.Choice(field.Label, field.Choices.Select(c => (c.Value, c.Label)), box.Text);
                        var dialog = new ContentDialog { XamlRoot = _window.RootXaml, Title = field.Label, Content = combo, PrimaryButtonText = "使用", CloseButtonText = "返回" };
                        if (await dialog.ShowAsync() == ContentDialogResult.Primary) box.Text = Ui.Selected(combo);
                    }
                });
                button.VerticalAlignment = VerticalAlignment.Bottom; Grid.SetColumn(button, 1); grid.Children.Add(button); input = grid;
            }
            else input = box;
        }
        var row = Ui.Stack(5, input);
        if (field.Description.Length > 0) row.Children.Add(Ui.Text(field.Description, 12, true));
        if (field.Key == "out" && field.Required && field.Kind == CommandFieldKind.OutputFile)
            row.Children.Add(Ui.Text("留空按所选资源和格式命名，保存到：" + _window.Settings.OutputDirectory, 12, true));
        return row;
    }
}
