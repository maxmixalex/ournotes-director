using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.WinUI;

internal sealed partial class SceneStudioPage
{
    private readonly ListView _commands = new() { SelectionMode = ListViewSelectionMode.Single, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _commandSection = Ui.Choice("", [("all", "全部指令"), ("initialize", "开场准备"), ("commands", "镜头与对白"), ("finalize", "结束处理")], "all");
    private readonly TextBox _commandSearch = new() { PlaceholderText = "搜索指令、角色、对白", Width = 190 };
    private readonly TextBlock _commandCount = Ui.Text("", 11, true);
    private readonly TextBlock _commandTime = Ui.Text("", 11, true);
    private readonly TextBlock _durationStatus = Ui.Text("", 11, true);
    private readonly Slider _commandSeek = new() { Minimum = 0, Maximum = 5, StepFrequency = 1d / 30, MinWidth = 110 };
    private RadioButton? _commandsTab, _timelineTab;
    private FrameworkElement? _commandPanel;
    private Button? _commandPlay;
    private string? _selectedCommand;
    private JsonObject? _commandDraft;
    private TextBox? _commandJson;
    private string? _commandJsonInitial;
    private TextBox? _dialogueText;
    private CheckBox? _commandEnabled;
    private string? _commandListSignature;
    private readonly Dictionary<string, TextBlock> _commandTimeLabels = new(StringComparer.Ordinal);
    private long _lastPropertyRefresh;
    private long _lastTimingUiRefresh;
    private NativePlaybackTiming? _lastTiming;
    private bool _updatingCommands;
    private bool _selectingCommandTarget;
    private JsonArray? _commandDragOrigin;
    private string? _commandDragId;

    // Keeping the two surfaces in one dock preserves the viewport while changing how a shot is authored.
    private FrameworkElement CreateCommandDock()
    {
        var commandsTab = _commandsTab = new RadioButton { Content = "指令", GroupName = "SceneDock", IsChecked = true };
        var timelineTab = _timelineTab = new RadioButton { Content = "时间轴", GroupName = "SceneDock" };
        var header = new Grid { Padding = new(10, 4, 10, 4), ColumnSpacing = 10 };
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(Ui.Row(10, commandsTab, timelineTab)); header.Children.Add(_commandCount); Grid.SetColumn(_commandCount, 1);
        _commandCount.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(_durationStatus); Grid.SetColumn(_durationStatus, 2); _durationStatus.VerticalAlignment = VerticalAlignment.Center;

        var panel = new Grid();
        panel.RowDefinitions.Add(new() { Height = GridLength.Auto }); panel.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        var insert = new DropDownButton { Content = "+ 插入指令", Padding = new(10, 6, 10, 6) };
        var flyout = new MenuFlyout();
        foreach (var (name, label) in new[] { ("Character", "角色入场"), ("Motion", "切换动作"), ("Expression", "切换表情"),
                     ("Talk", "对白与语音"), ("Delay", "等待一段时间"), ("Director.Actor", "位置与大小"),
                     ("Director.Camera", "相机运动"), ("Director.Scene", "背景与后处理") })
        {
            var item = new MenuFlyoutItem { Text = label }; item.Click += (_, _) => InsertNativeCommand(name); flyout.Items.Add(item);
        }
        flyout.Items.Add(new MenuFlyoutSeparator());
        var all = new MenuFlyoutItem { Text = "所有指令…" }; all.Click += async (_, _) => await SafeAsync(ChooseCommandAsync); flyout.Items.Add(all); insert.Flyout = flyout;
        _commandPlay = StudioLayout.AsyncTool("播放", TogglePlaybackAsync, "\uE768");
        var toolbar = Ui.Row(6, _commandPlay, insert,
            StudioLayout.Tool("复制", DuplicateSelectedCommand, "\uE8C8"),
            StudioLayout.Tool("", () => MoveSelectedCommand(-1), "\uE74A"),
            StudioLayout.Tool("", () => MoveSelectedCommand(1), "\uE74B"),
            StudioLayout.Tool("", RemoveSelectedCommand, "\uE74D"), _commandSection, _commandSearch, _commandTime);
        ToolTipService.SetToolTip(toolbar.Children[3], "上移所选指令"); ToolTipService.SetToolTip(toolbar.Children[4], "下移所选指令");
        ToolTipService.SetToolTip(toolbar.Children[5], "删除所选指令");
        var tools = new ScrollViewer { Content = toolbar, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new(8, 0, 8, 4) };
        panel.Children.Add(tools); panel.Children.Add(_commands); Grid.SetRow(_commands, 1);
        _commandPanel = panel;
        var surfaces = new Grid(); surfaces.Children.Add(panel); surfaces.Children.Add(_timeline); _timeline.Visibility = Visibility.Collapsed;
        var root = new Grid(); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.Children.Add(header); root.Children.Add(surfaces); Grid.SetRow(surfaces, 1);
        _commandSeek.Margin = new(12, 0, 12, 0); root.Children.Add(_commandSeek); Grid.SetRow(_commandSeek, 2);
        void Switch(bool commandMode)
        {
            panel.Visibility = commandMode ? Visibility.Visible : Visibility.Collapsed;
            _timeline.Visibility = commandMode ? Visibility.Collapsed : Visibility.Visible;
            _commandSeek.Visibility = commandMode ? Visibility.Visible : Visibility.Collapsed;
        }
        commandsTab.Checked += (_, _) => Switch(true); timelineTab.Checked += (_, _) => Switch(false);
        _commandSection.SelectionChanged += (_, _) => RefreshCommands(true);
        _commandSearch.TextChanged += (_, _) => RefreshCommands(true);
        _commands.SelectionChanged += (_, _) =>
        {
            if (_updatingCommands || _document is null) return;
            if ((_commands.SelectedItem as ListViewItem)?.Tag is string id) SelectCommand(id, false);
        };
        _commands.DoubleTapped += (_, _) => { if (SelectedCommand() is { Start: double time }) _ = SafeAsync(() => SeekAsync(time)); };
        _commandSeek.ValueChanged += (_, _) => { if (!_updatingCommands) _ = SafeAsync(() => SeekAsync(_commandSeek.Value)); };
        _commands.KeyDown += (_, args) =>
        {
            if (args.OriginalSource is TextBox or NumberBox) return;
            if (args.Key == Windows.System.VirtualKey.Delete) { RemoveSelectedCommand(); args.Handled = true; }
            else if (args.Key == Windows.System.VirtualKey.Space) { _ = SafeAsync(TogglePlaybackAsync); args.Handled = true; }
        };
        return root;
    }

    private StudioCommand? SelectedCommand() => _document?.Commands.FirstOrDefault(command => command.Id == _selectedCommand);
    private static string SectionLabel(string section) => section switch { "initialize" => "开场", "finalize" => "结束", _ => "正文" };

    private void RefreshCommands(bool force = false)
    {
        if (_document is null) return;
        var commands = _document.Commands.Where(command => !command.IsGenerated).ToArray();
        string signature = _document.Document.Text;
        _commandCount.Text = $"{commands.Length} 条指令 · 双击定位画面";
        static string TimeLabel(StudioCommand command) => command.Start is double start ? command.End is double end && end > start + .01 ? $"{start:0.00}–{end:0.00} s" : $"{start:0.00} s" : "依执行顺序";
        if (!force && signature == _commandListSignature)
        {
            foreach (var command in commands) if (_commandTimeLabels.TryGetValue(command.Id, out var label)) label.Text = TimeLabel(command);
            UpdateDurationStatus(); return;
        }
        _commandListSignature = signature;
        _updatingCommands = true;
        _commands.Items.Clear();
        _commandTimeLabels.Clear();
        string section = Ui.Selected(_commandSection), query = _commandSearch.Text.Trim();
        foreach (var command in commands.Where(c => (section == "all" || c.Section == section)
                     && (query.Length == 0 || (c.Summary + " " + c.Command + " " + c.Data.ToJsonString()).Contains(query, StringComparison.OrdinalIgnoreCase))))
        {
            var enabled = new CheckBox { IsChecked = command.Enabled, MinWidth = 24, MinHeight = 28, VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(enabled, "启用这条指令");
            enabled.Click += (_, _) =>
            {
                bool value = enabled.IsChecked == true;
                if (!Edit(() => _document?.SetCommandEnabled(command.Id, value)))
                    enabled.IsChecked = _document?.Commands.FirstOrDefault(row => row.Id == command.Id)?.Enabled ?? command.Enabled;
            };
            var row = new Grid { ColumnSpacing = 10, Opacity = command.Enabled ? 1 : .5 };
            foreach (var size in new[] { GridLength.Auto, new GridLength(62), new GridLength(132), new GridLength(1, GridUnitType.Star), new GridLength(112) }) row.ColumnDefinitions.Add(new() { Width = size });
            var position = Ui.Text($"{SectionLabel(command.Section)} {command.Index + 1}", 11, true);
            var title = Ui.Text(StudioCommandCatalog.Get(command.Command).Label, 12);
            var detail = new TextBlock { Text = command.Summary, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center, IsTextSelectionEnabled = false };
            var time = Ui.Text(TimeLabel(command), 11, true); _commandTimeLabels[command.Id] = time;
            FrameworkElement[] cells = [enabled, position, title, detail, time];
            for (int i = 0; i < cells.Length; i++) { cells[i].VerticalAlignment = VerticalAlignment.Center; row.Children.Add(cells[i]); Grid.SetColumn(cells[i], i); }
            var item = new ListViewItem { Content = row, Tag = command.Id, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new(8, 3, 8, 3) };
            ToolTipService.SetToolTip(item, command.Command + " · " + command.Summary); _commands.Items.Add(item);
            if (command.Id == _selectedCommand) _commands.SelectedItem = item;
        }
        _updatingCommands = false;
        if (_selectedCommand is not null && !commands.Any(c => c.Id == _selectedCommand)) _selectedCommand = null;
        UpdateDurationStatus();
    }

    private void SelectCommand(string id, bool reveal = true)
    {
        if (id != _selectedCommand && !TryFlushCommandDraft()) { RestoreCommandSelection(); return; }
        FlushProperty(); _selectedCommand = id; _selectedMarker = null;
        if (reveal)
        {
            _updatingCommands = true;
            _commands.SelectedItem = _commands.Items.OfType<ListViewItem>().FirstOrDefault(item => item.Tag?.ToString() == id);
            if (_commands.SelectedItem is { } item) _commands.ScrollIntoView(item);
            _updatingCommands = false;
        }
        if (SelectedCommand() is { } command && command.Target.Length > 0)
            SyncObjectSelection(command.Target);
        BuildProperties();
    }

    private void InsertNativeCommand(string name)
    {
        if (_document is null) { _ = SafeAsync(async () => { await NewAsync(false); if (_document is not null) InsertNativeCommand(name); }); return; }
        if (!TryFlushCommandDraft(reload: false)) return;
        var selected = SelectedCommand();
        string section = selected?.Section ?? (Ui.Selected(_commandSection) is "initialize" or "finalize" ? Ui.Selected(_commandSection) : "commands");
        int index = selected is null ? _document.CommandSections.First(s => s.Section == section).Commands.Count : selected.Index + 1;
        string? target = selected?.Target;
        if (string.IsNullOrWhiteSpace(target)) target = _document.SelectedId?.StartsWith('$') == false ? _document.SelectedId : _document.Actors.FirstOrDefault()?.Id;
        string? id = null;
        Edit(() => id = _document.InsertCommand(section, index, StudioCommandCatalog.Create(name, target)));
        if (id is not null) SelectCommand(id);
    }

    private async Task ChooseCommandAsync()
    {
        var search = new TextBox { PlaceholderText = "输入中文用途或原指令名" };
        var list = new ListView { Height = 330, SelectionMode = ListViewSelectionMode.Single };
        var dialog = new ContentDialog { XamlRoot = _window.RootXaml, Title = "插入指令", PrimaryButtonText = "插入", CloseButtonText = "取消",
            IsPrimaryButtonEnabled = false, Content = Ui.Stack(10, search, list) };
        void Populate()
        {
            list.Items.Clear();
            foreach (var descriptor in StudioCommandCatalog.All.Where(d => (d.Label + " " + d.Command + " " + d.Description).Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase)))
                list.Items.Add(new ListViewItem { Tag = descriptor.Command, Content = Ui.Stack(2, Ui.Text(descriptor.Label + " · " + descriptor.Command), Ui.Text(descriptor.Description, 11, true)) });
        }
        search.TextChanged += (_, _) => Populate(); list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedItem is not null;
        Populate(); if (await dialog.ShowAsync() == ContentDialogResult.Primary && (list.SelectedItem as ListViewItem)?.Tag is string name) InsertNativeCommand(name);
    }

    private void DuplicateSelectedCommand()
    { if (TryFlushCommandDraft(reload: false) && SelectedCommand() is { } command) { string? id = null; Edit(() => id = _document!.DuplicateCommand(command.Id)); if (id is not null) SelectCommand(id); } }
    private void RemoveSelectedCommand()
    { if (TryFlushCommandDraft(reload: false) && SelectedCommand() is { } command) { _selectedCommand = null; Edit(() => _document!.RemoveCommand(command.Id)); } }
    private void MoveSelectedCommand(int offset)
    {
        if (!TryFlushCommandDraft(reload: false) || _document is null || SelectedCommand() is not { } command) return;
        int count = _document.CommandSections.First(s => s.Section == command.Section).Commands.Count;
        int index = Math.Clamp(command.Index + offset, 0, count - 1);
        Edit(() => _document.MoveCommand(command.Id, command.Section, index)); SelectCommand(command.Id);
    }

    private void BuildCommandProperties(StudioCommand command, JsonObject? restoredDraft = null)
    {
        _autoKey.Visibility = Visibility.Collapsed;
        _commandDraft = (JsonObject)(restoredDraft ?? command.Data).DeepClone();
        _commandDraftDocument = _document; _commandDraftId = command.Id; _commandDraftOriginal = command.Data.ToJsonString();
        var descriptor = StudioCommandCatalog.Get(command.Command);
        _properties.Children.Add(StudioLayout.Section(descriptor.Label));
        _properties.Children.Add(Ui.Text(descriptor.Description, 11, true));
        _properties.Children.Add(Ui.Text(command.Command + " · " + SectionLabel(command.Section) + " " + (command.Index + 1), 11, true));
        _commandEnabled = new CheckBox { Content = "启用这条指令", IsChecked = _commandDraft["IgnoreData"]?.ToString() is not ("1" or "true") };
        _commandEnabled.Click += (_, _) => SetDraftValue(["IgnoreData"], JsonValue.Create(_commandEnabled.IsChecked == true ? 0 : 1));
        _properties.Children.Add(_commandEnabled);
        var position = Ui.Choice("执行阶段", [("initialize", "开场准备"), ("commands", "镜头与对白"), ("finalize", "结束处理")], command.Section);
        position.SelectionChanged += (_, _) => { string section = Ui.Selected(position); if (section != command.Section) Edit(() => _document!.MoveCommand(command.Id, section, _document.CommandSections.First(s => s.Section == section).Commands.Count)); };
        _properties.Children.Add(position);
        if (command.Start is double time) _properties.Children.Add(StudioLayout.AsyncTool($"查看此处 · {time:0.00} s", () => SeekAsync(time), "\uE768"));
        _dialogueText = null;
        if (command.Command is "Talk" or "Subtitles" or "Location" or "ChatTalk")
        {
            _dialogueText = new TextBox { Header = "对白文本", Text = _document!.GetCommandText(command.Id), AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap, MinHeight = 96, PlaceholderText = "直接输入角色要说的话" };
            _commandDialogueInitial = _dialogueText.Text;
            _dialogueText.TextChanged += (_, _) => CommandDraftChanged();
            _properties.Children.Add(_dialogueText);
        }
        if (command.Command is "Talk" or "Voice")
        {
            _properties.Children.Add(StudioLayout.AsyncTool("从台词查找语音…", async () =>
            {
                var document = _commandDraftDocument; string? id = _commandDraftId; long generation = _commandDraftGeneration;
                var voice = await ResourcePicker.PickAsync(_window, new("soundId", "语音", CommandFieldKind.Text, "") { LookupCommandId = "voice.search" },
                    new Dictionary<string, string?> { ["site"] = _document!.Document.Source.Site, ["advId"] = _document.Document.Source.Adv, ["query"] = _dialogueText?.Text });
                if (voice is null) return;
                if (!IsCurrentCommandDraft(document, id, generation)) { Notice("所选指令已改变，请重新选择语音。", false); return; }
                if (!long.TryParse(voice.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long sound)) throw new ArgumentException("语音编号必须是归档中的实际数字编号。");
                SetDraftValue(["VoiceIDs"], new JsonArray(sound)); SetDraftValue(["IgnoreLipSync"], JsonValue.Create(false));
                if (_dialogueText is not null && string.IsNullOrWhiteSpace(_dialogueText.Text)) _dialogueText.Text = voice.RelatedValues.GetValueOrDefault("displayName") ?? "";
                ApplyCommandDraft();
            }, "\uE8D6"));
        }
        var fields = Ui.Stack(9);
        foreach (var entry in _commandDraft.ToArray())
        {
            if (entry.Key is "cmd" or "Command" or "Key" or "IgnoreData" or "i" || entry.Key == "AdvTextID" && _dialogueText is not null) continue;
            BuildCommandField(fields, entry.Key, entry.Value, [entry.Key], descriptor, 0);
        }
        foreach (var field in descriptor.Fields.Where(field => !_commandDraft.ContainsKey(field.Key) && !field.Key.Contains('.') && field.Key is not ("IgnoreData" or "AdvTextID")))
            BuildCommandField(fields, field.Key, null, [field.Key], descriptor, 0);
        _properties.Children.Add(fields);
        _properties.Children.Add(StudioLayout.Tool("应用指令", ApplyCommandDraft, "\uE73E"));
        _properties.Children.Add(StudioLayout.Tool("撤销未应用修改", DiscardCommandDraft, "\uE7A7"));
        _properties.Children.Add(StudioLayout.AsyncTool("添加字段…", AddCommandFieldAsync, "\uE710"));
        _commandJsonInitial = _commandDraft.ToJsonString(DesktopSettings.JsonOptions);
        _commandJson = new TextBox { Text = _commandJsonInitial, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas"), FontSize = 11, Height = 220 };
        _commandJson.TextChanged += (_, _) => CommandDraftChanged();
        ScrollViewer.SetVerticalScrollBarVisibility(_commandJson, ScrollBarVisibility.Auto); ScrollViewer.SetHorizontalScrollBarVisibility(_commandJson, ScrollBarVisibility.Auto);
        _properties.Children.Add(new Expander { Header = "这条指令的 JSON", HorizontalAlignment = HorizontalAlignment.Stretch, Content = Ui.Stack(8, _commandJson,
            StudioLayout.Tool("应用指令 JSON", ApplyCommandJson, "\uE73E"), StudioLayout.Tool("复制指令", () => MainWindow.Copy(_commandJson.Text), "\uE8C8")) });
        _hint.Text = "指令按列表顺序执行。修改字段后应用；复制、移动或插入指令可快速编排镜头。";
    }

    private void BuildCommandField(StackPanel panel, string key, JsonNode? value, string[] path, StudioCommandDescriptor descriptor, int depth)
    {
        long generation = _commandDraftGeneration;
        var definition = descriptor.Fields.FirstOrDefault(field => field.Key == string.Join('.', path) || field.Key == key);
        string label = definition?.Label ?? CommandFieldLabel(key);
        if (value is JsonObject obj && depth < 3)
        {
            var body = Ui.Stack(8);
            foreach (var entry in obj.ToArray()) BuildCommandField(body, entry.Key, entry.Value, [.. path, entry.Key], descriptor, depth + 1);
            panel.Children.Add(new Expander { Header = label, IsExpanded = true, HorizontalAlignment = HorizontalAlignment.Stretch, Content = body }); return;
        }
        void Change(JsonNode? next) { if (generation == _commandDraftGeneration) SetDraftValue(path, next); }
        if (value is JsonArray array && array.Count is >= 2 and <= 4 && !key.EndsWith("IDs", StringComparison.OrdinalIgnoreCase)
            && array.All(item => item is JsonValue number && number.TryGetValue<double>(out double component) && double.IsFinite(component) && Math.Abs(component) < 9007199254740992d))
        {
            var current = (JsonArray)array.DeepClone(); var grid = new Grid { ColumnSpacing = 5 };
            string[] axes = key is "background" or "tint" ? ["红", "绿", "蓝", "透明"] : ["X", "Y", "Z", "W"];
            for (int index = 0; index < array.Count; index++)
            {
                int axis = index; grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
                var vectorBox = new NumberBox { Header = axes[index], Value = Number(array[index], 0), SmallChange = .01,
                    SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden, MinWidth = 42 };
                BindNumberInput(vectorBox, [.. path, axis.ToString(CultureInfo.InvariantCulture)], label + " " + axes[axis], vectorBox.Value, false,
                    number => { current[axis] = number; Change(current.DeepClone()); });
                grid.Children.Add(vectorBox); Grid.SetColumn(vectorBox, index);
            }
            panel.Children.Add(Ui.Stack(3, Ui.Text(label, 12), grid)); return;
        }
        if (definition is { Kind: CommandFieldKind.Choice, Choices.Count: > 0 })
        {
            string selected = value?.ToString() ?? definition.DefaultValue ?? "";
            var choices = definition.Choices.Select(choice => (choice.Value, choice.Label)).ToList();
            if (selected.Length > 0 && choices.All(choice => choice.Value != selected)) choices.Add((selected, selected));
            var choiceBox = Ui.Choice(label, choices, selected);
            _commandInputs[string.Join('.', path)] = choiceBox;
            choiceBox.SelectionChanged += (_, _) => Change(JsonValue.Create(Ui.Selected(choiceBox))); panel.Children.Add(choiceBox); return;
        }
        if (value is JsonValue boolean && boolean.TryGetValue<bool>(out _) || definition?.Kind == CommandFieldKind.Boolean)
        {
            var control = new CheckBox { Content = label, IsChecked = value?.ToString() == "1" || value?.ToString().Equals("true", StringComparison.OrdinalIgnoreCase) == true };
            _commandInputs[string.Join('.', path)] = control;
            control.Click += (_, _) => Change(JsonValue.Create(control.IsChecked == true)); panel.Children.Add(control); return;
        }
        bool opaqueId = key.EndsWith("ID", StringComparison.OrdinalIgnoreCase) || key.EndsWith("IDs", StringComparison.OrdinalIgnoreCase);
        bool finiteNumber = value is JsonValue numeric && numeric.TryGetValue<double>(out double nativeNumber) && double.IsFinite(nativeNumber) && Math.Abs(nativeNumber) < 9007199254740992d;
        if (!opaqueId && (finiteNumber || value is null && definition?.Kind is CommandFieldKind.Number or CommandFieldKind.Integer))
        {
            var control = new NumberBox { Header = label, Value = double.TryParse(value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double n) ? n : double.NaN,
                SmallChange = definition?.Kind == CommandFieldKind.Integer ? 1 : .1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
            BindNumberInput(control, path, label, control.Value, definition?.Kind == CommandFieldKind.Integer, number => Change(JsonValue.Create(number)));
            panel.Children.Add(control); return;
        }
        bool json = value is JsonArray or JsonObject || value?.GetValueKind() == System.Text.Json.JsonValueKind.Number;
        var input = new TextBox { Header = label, Text = json ? value!.ToJsonString() : value?.ToString() ?? "",
            PlaceholderText = definition?.Placeholder ?? (definition?.Required == true ? "需要填写或从素材库选择" : ""),
            AcceptsReturn = key.Contains("Text", StringComparison.OrdinalIgnoreCase), TextWrapping = TextWrapping.Wrap };
        string fieldPath = string.Join('.', path); _commandInputs[fieldPath] = input;
        void ValidateInput()
        {
            if (generation != _commandDraftGeneration || !_commandInputsChanged.Contains(fieldPath)) return;
            if (json)
            {
                try { var parsed = JsonNode.Parse(input.Text); if (!_validatingCommandInputs) Change(parsed); _commandFieldErrors.Remove(fieldPath); }
                catch (System.Text.Json.JsonException error) { _commandFieldErrors[fieldPath] = label + "：JSON 无效，" + error.Message; }
            }
            else if (opaqueId && value is JsonValue idValue && idValue.TryGetValue<long>(out _))
            {
                if (!long.TryParse(input.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedId))
                    _commandFieldErrors[fieldPath] = label + "：请填写有效的整数资源编号。";
                else { _commandFieldErrors.Remove(fieldPath); if (!_validatingCommandInputs) Change(JsonValue.Create(parsedId)); }
            }
            else if (!_validatingCommandInputs) Change(JsonValue.Create(input.Text));
            CommandDraftChanged();
        }
        _commandFieldValidators[fieldPath] = ValidateInput;
        input.TextChanged += (_, _) => { _commandInputsChanged.Add(fieldPath); ValidateInput(); };
        panel.Children.Add(input);
        if (key is "Model" or "MotionName" or "ExpressionName" or "VoiceName" or "SoundId")
        {
            string lookup = key == "Model" ? "model.show" : key == "MotionName" ? "motion.list" : key == "ExpressionName" ? "expression.list" : "voice.search";
            string field = key == "Model" ? "modelId" : key == "MotionName" ? "motion" : key == "ExpressionName" ? "expression" : "soundId";
            var pickField = new CommandField(field, label, CommandFieldKind.Text, "") { LookupCommandId = lookup == "model.show" ? "model.list" : lookup };
            panel.Children.Add(StudioLayout.AsyncTool("选择" + label, async () =>
            {
                var document = _commandDraftDocument; string? id = _commandDraftId;
                var current = SelectedCommand();
                string? model = _commandDraft?["Model"]?.ToString() ?? _document?.Actors.FirstOrDefault(actor => actor.Id == current?.Target)?.Model;
                var selected = await ResourcePicker.PickAsync(_window, pickField, new Dictionary<string, string?>
                { ["site"] = _document?.Document.Source.Site, ["modelId"] = model, ["advId"] = _document?.Document.Source.Adv });
                if (selected is not null)
                {
                    if (!IsCurrentCommandDraft(document, id, generation)) { Notice("所选指令已改变，请重新选择资源。", false); return; }
                    input.Text = selected.Value;
                    if (_commandEnabled is not null && !string.IsNullOrWhiteSpace(_commandDraft?["TargetName"]?.ToString()))
                    { _commandEnabled.IsChecked = true; SetDraftValue(["IgnoreData"], JsonValue.Create(0)); }
                }
            }, "\uE8B7"));
        }
        if (!string.IsNullOrEmpty(definition?.Description)) panel.Children.Add(Ui.Text(definition.Description, 10, true));
    }

    private void SetDraftValue(string[] path, JsonNode? value)
    {
        if (_commandDraft is null) return;
        JsonObject current = _commandDraft;
        foreach (string segment in path[..^1]) { if (current[segment] is not JsonObject) current[segment] = new JsonObject(); current = (JsonObject)current[segment]!; }
        current[path[^1]] = value;
        if (_commandJson is not null && _commandJson.Text == _commandJsonInitial)
        { _commandJsonInitial = _commandDraft.ToJsonString(DesktopSettings.JsonOptions); _commandJson.Text = _commandJsonInitial; }
        CommandDraftChanged();
    }
    private void ApplyCommandDraft() => TryApplyCommandDraft(false);
    private void ApplyCommandJson() => TryApplyCommandDraft(true);
    private async Task AddCommandFieldAsync()
    {
        if (_commandDraft is null) return;
        var document = _commandDraftDocument; string? id = _commandDraftId; long generation = _commandDraftGeneration;
        var key = Ui.Input("字段名", "", "例如 Values.opacity");
        var value = Ui.Input("字段值（JSON）", "0", "数字、true、字符串、数组或对象");
        var error = Ui.Text("", 11, true);
        var dialog = new ContentDialog { XamlRoot = _window.RootXaml, Title = "添加指令字段", PrimaryButtonText = "添加", CloseButtonText = "取消", Content = Ui.Stack(10, key, value, error) };
        JsonNode? parsed = null;
        dialog.PrimaryButtonClick += (_, args) => { try { if (key.Text.Split('.').Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("请填写字段名。"); parsed = JsonNode.Parse(value.Text); } catch (Exception failure) { args.Cancel = true; error.Text = failure.Message; } };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (!IsCurrentCommandDraft(document, id, generation)) { Notice("所选指令已改变，请重新添加字段。", false); return; }
        SetDraftValue(key.Text.Split('.'), parsed); ApplyCommandDraft();
    }
    private static string CommandFieldLabel(string key) => key switch
    {
        "TargetName" => "作用对象", "Model" => "角色模型", "MotionName" => "动作", "ExpressionName" => "表情", "VoiceName" or "SoundId" => "语音 / 音效",
        "Duration" => "持续时间（秒）", "Frames" => "等待帧数（30 帧 = 1 秒）", "Ease" => "过渡曲线", "Space" => "坐标空间",
        "IsNoWait" => "与后续指令同时执行", "Values" => "画面参数", "position" => "位置", "rotation" => "旋转", "scale" => "比例",
        "height" => "画布高度比例", "opacity" => "不透明度", "visible" => "可见", "grade" => "后处理", "exposure" => "曝光",
        "contrast" => "对比度", "saturation" => "饱和度", "vignette" => "暗角", "background" => "背景颜色", "stageVisible" => "显示舞台背景",
        "framing" => "构图", "zoom" => "缩放", "roll" => "画面旋转", "animation" => "动画选项", "physics" => "物理效果", "breath" => "呼吸",
        "eyeBlink" => "自动眨眼", "loop" => "循环动作", "Letterbox" => "上下黑边", "Text" => "对白文本", _ => key
    };
    private void UpdateDurationStatus()
    {
        if (_document is null) return;
        bool automatic = _document.DurationMode == StudioDurationMode.Automatic;
        _durationStatus.Text = automatic ? _document.ResolvedDuration is double duration ? $"完整时长 {duration:0.00} 秒" :
            _lastTiming?.Error is { } error ? "时长分析未完成 · " + error : "正在计算完整时长…" : $"指定时长 {_document.Duration:0.00} 秒";
        _timeline.SetDurationMode(automatic, !automatic || _document.ResolvedDuration is not null);
    }
    private void UpdateCommandTransport(PreviewFrame? frame = null)
    {
        if (_document is null) return;
        _updatingCommands = true; _commandSeek.Maximum = Math.Max(.1, _document.Duration); _commandSeek.Value = _document.Time; _updatingCommands = false;
        _commandTime.Text = $"{_document.Time:0.00} s";
        if (_commandPlay is not null)
        {
            bool playing = frame?.IsPlaying ?? _viewport.IsPlaying;
            if ((_commandPlay.Tag as bool?) != playing) { _commandPlay.Tag = playing; _commandPlay.Content = playing ? "暂停" : "播放"; }
        }
    }

    private async Task<bool> DragCommandAsync(PreviewDrag drag)
    {
        if (SelectedCommand() is not { Command: "Director.Actor" } command || command.Target != drag.TargetId
            || command.Data["Space"]?.ToString() != "screen" || _commandDraft?["Values"] is not JsonObject values) return false;
        _viewport.Pause(); _timeline.IsPlaying = false;
        if (_commandDragId != command.Id || _commandDragOrigin is null)
        { _commandDragId = command.Id; _commandDragOrigin = values["position"] is JsonArray position ? (JsonArray)position.DeepClone() : new JsonArray(.5, .5); }
        var camera = _document!.Sample(_document.Time).Camera;
        double angle = camera.Roll * Math.PI / 180, zoom = Math.Max(.001, camera.Zoom);
        var output = _document.Document.Output;
        double renderWidth = _viewport.LastFrame?.Pixels.Width ?? output.Width, renderHeight = _viewport.LastFrame?.Pixels.Height ?? output.Height;
        double activeHeight = output.Letterbox == "none" ? renderHeight : Math.Min(renderHeight, Math.Floor(renderWidth / (renderWidth > renderHeight ? 2.1666667 : 1.7777778) + .5));
        double aspect = renderWidth / activeHeight, vertical = drag.DeltaY * renderHeight / activeHeight;
        double dx = (drag.DeltaX * Math.Cos(angle) - vertical * Math.Sin(angle) / aspect) / zoom;
        double dy = (drag.DeltaX * Math.Sin(angle) * aspect + vertical * Math.Cos(angle)) / zoom;
        var moved = new JsonArray(Number(_commandDragOrigin[0], .5) + dx, Number(_commandDragOrigin[1], .5) + dy);
        SetDraftValue(["Values", "position"], moved);
        await _viewport.ApplySceneEditsAsync([new JsonObject { ["cmd"] = "Director.Actor", ["TargetName"] = command.Target, ["Space"] = "screen",
            ["Values"] = new JsonObject { ["position"] = moved.DeepClone() } }]);
        if (drag.IsCompleted)
        { _commandDragOrigin = null; _commandDragId = null; await CommitCommandTransformAsync(); }
        return true;
    }
    private async Task<bool> ScaleCommandAsync(PreviewScale scale)
    {
        if (SelectedCommand() is not { Command: "Director.Actor" } command || command.Target != scale.TargetId
            || command.Data["Space"]?.ToString() != "screen" || _commandDraft?["Values"] is not JsonObject values) return false;
        _viewport.Pause(); _timeline.IsPlaying = false;
        double height = Math.Clamp(Number(values["height"], 1) * scale.Factor, .001, 20);
        SetDraftValue(["Values", "height"], JsonValue.Create(height));
        await _viewport.ApplySceneEditsAsync([new JsonObject { ["cmd"] = "Director.Actor", ["TargetName"] = command.Target, ["Space"] = "screen",
            ["Values"] = new JsonObject { ["height"] = height } }]);
        if (scale.IsCompleted) await CommitCommandTransformAsync();
        return true;
    }
    private async Task CommitCommandTransformAsync()
    {
        bool previous = _suppressReload; _suppressReload = true;
        try { ApplyCommandDraft(); } finally { _suppressReload = previous; }
        if (!previous) await CommitPreviewAsync();
    }
}
