using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.WinUI;

internal sealed partial class SceneStudioPage
{
    private StudioSceneDocument? _commandDraftDocument;
    private string? _commandDraftId, _commandDraftOriginal, _commandDialogueInitial;
    private bool _applyingCommandDraft, _validatingCommandInputs;
    private long _commandDraftGeneration;
    private readonly Dictionary<string, string> _commandFieldErrors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Action> _commandFieldValidators = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FrameworkElement> _commandInputs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _commandInputsChanged = new(StringComparer.Ordinal);
    private readonly HashSet<string> _commandInputsPending = new(StringComparer.Ordinal);

    private bool CommandFieldsChanged => _commandDraft is not null && (_commandDraft.ToJsonString() != _commandDraftOriginal
        || CommandDialogueChanged);
    private bool CommandDialogueChanged => _dialogueText is not null && !SameTextBoxText(_dialogueText.Text, _commandDialogueInitial);
    private bool CommandJsonChanged => _commandJson is not null && !SameTextBoxText(_commandJson.Text, _commandJsonInitial);
    private bool HasCommandDraft => ReferenceEquals(_commandDraftDocument, _document) && _commandDraftId is not null
        && (CommandFieldsChanged || CommandJsonChanged || _commandFieldErrors.Count > 0 || _commandInputsPending.Count > 0);
    private bool PreserveCommandDraft => HasCommandDraft && _commandDraftId == _selectedCommand && !_applyingCommandDraft;

    // WinUI's RichEdit normalizes newlines to CR, including after a TextBox is first loaded.
    // Ignore only that representation change; JSON content and escaped string values remain significant.
    private static string NormalizeTextBoxLineEndings(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    private static bool SameTextBoxText(string? left, string? right)
        => left == right || left is not null && right is not null
            && NormalizeTextBoxLineEndings(left) == NormalizeTextBoxLineEndings(right);

    private void ClearCommandDraft()
    {
        _commandDraftDocument = null; _commandDraftId = null; _commandDraftOriginal = null; _commandDialogueInitial = null;
        _commandDraft = null; _commandJson = null; _commandJsonInitial = null; _dialogueText = null; _commandEnabled = null;
        _commandFieldErrors.Clear(); _commandFieldValidators.Clear(); _commandInputs.Clear(); _commandInputsChanged.Clear(); _commandInputsPending.Clear();
        _commandDraftGeneration++;
    }
    private void CommandDraftChanged()
    {
        if (_applyingCommandDraft || _document is null) return;
        RefreshHeader(); _draftTimer.Stop(); _draftTimer.Start();
        if (_commandFieldErrors.Count > 0) _hint.Text = "输入尚未应用：" + _commandFieldErrors.Values.First();
    }
    private bool IsCurrentCommandDraft(StudioSceneDocument? document, string? id, long generation)
        => ReferenceEquals(document, _document) && ReferenceEquals(document, _commandDraftDocument)
            && id == _commandDraftId && generation == _commandDraftGeneration;

    private void SynchronizePendingCommandInputs()
    {
        if (_applyingCommandDraft || !ReferenceEquals(_commandDraftDocument, _document)) return;
        foreach (string key in _commandInputsPending.ToArray())
            if (_commandFieldValidators.TryGetValue(key, out var validate)) validate();
    }

    /// <summary>Applies a valid draft before navigation or persistence; invalid input stays on screen.</summary>
    private bool TryFlushCommandDraft(bool reload = true)
    {
        if (_applyingCommandDraft) return true;
        // TextChanged is asynchronous. Read pending TextChanging edits before testing errors or conflicts.
        SynchronizePendingCommandInputs();
        if (!HasCommandDraft) return true;
        if (_commandFieldErrors.Count > 0)
        { Notice(string.Join(Environment.NewLine, _commandFieldErrors.Values), false); return false; }
        if (CommandJsonChanged && CommandFieldsChanged)
        {
            Notice("字段和指令 JSON 都有未应用修改，请先选择“应用指令”或“应用指令 JSON”，再继续。", false); return false;
        }
        return TryApplyCommandDraft(CommandJsonChanged, reload);
    }

    private bool TryApplyCommandDraft(bool useJson, bool reload = true)
    {
        var document = _commandDraftDocument; string? id = _commandDraftId;
        if (document is null || !ReferenceEquals(document, _document) || id is null || _commandDraft is null) return false;
        bool transaction = false;
        try
        {
            if (!useJson)
            {
                SynchronizePendingCommandInputs();
                _validatingCommandInputs = true;
                try { foreach (var validate in _commandFieldValidators.Values.ToArray()) validate(); }
                finally { _validatingCommandInputs = false; }
                if (_commandFieldErrors.Count > 0) throw new ArgumentException(string.Join(Environment.NewLine, _commandFieldErrors.Values));
            }
            JsonObject value = useJson
                ? JsonNode.Parse(_commandJson?.Text ?? "") as JsonObject ?? throw new ArgumentException("一条指令需要是 JSON 对象。")
                : (JsonObject)_commandDraft.DeepClone();
            string? text = !useJson && CommandDialogueChanged ? NormalizeTextBoxLineEndings(_dialogueText!.Text) : null;
            FlushProperty(); _applyingCommandDraft = true;
            document.BeginEdit("修改指令"); transaction = true;
            document.UpdateCommand(id, value);
            if (text is not null && !SameTextBoxText(text, document.GetCommandText(id))) document.SetCommandText(id, text);
            document.CommitEdit(); transaction = false;
            ClearCommandDraft(); BuildProperties(); RefreshHeader(); RefreshTimeline();
            if (reload && !_suppressReload) _ = SafeAsync(ReloadAsync);
            return true;
        }
        catch (Exception failure)
        {
            if (transaction) document.CancelEdit();
            Notice(failure.Message, false); return false;
        }
        finally { _applyingCommandDraft = false; }
    }

    private void DiscardCommandDraft()
    {
        if (!HasCommandDraft) return;
        ClearCommandDraft(); BuildProperties(); RefreshHeader();
        Notice("已撤销这条指令尚未应用的修改，已保存的场景内容保持不变。", false);
    }
    private void UndoSceneEdit()
    {
        if (HasCommandDraft) { DiscardCommandDraft(); return; }
        Edit(() => _document?.Undo());
    }

    private void SyncObjectSelection(string? id)
    {
        bool selecting = _selectingCommandTarget, refreshing = _refreshing;
        _selectingCommandTarget = _refreshing = true;
        try
        {
            if (_document is not null) _document.SelectedId = id;
            _viewport.SelectedTargetId = id?.StartsWith('$') == true ? null : id;
            _objects.SelectedItem = _objects.Items.OfType<ListViewItem>().FirstOrDefault(item => item.Tag?.ToString() == id);
        }
        finally { _selectingCommandTarget = selecting; _refreshing = refreshing; }
    }
    private void RestoreCommandSelection()
    {
        bool updating = _updatingCommands; _updatingCommands = true;
        try { _commands.SelectedItem = _commands.Items.OfType<ListViewItem>().FirstOrDefault(item => item.Tag?.ToString() == _selectedCommand); }
        finally { _updatingCommands = updating; }
        SyncObjectSelection(_document?.SelectedId);
    }

    private void BindNumberInput(NumberBox box, string[] path, string label, double initial, bool integer, Action<double> change)
    {
        string key = string.Join('.', path); long generation = _commandDraftGeneration;
        _commandInputs[key] = box; box.ValidationMode = NumberBoxValidationMode.Disabled;
        void Validate()
        {
            if (generation != _commandDraftGeneration || !_commandInputsChanged.Contains(key)) return;
            string input = box.Text?.Trim() ?? "";
            bool parsed = double.TryParse(input, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out double number)
                || double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
            if (!parsed || !double.IsFinite(number) || integer && number != Math.Truncate(number))
                _commandFieldErrors[key] = label + (integer ? "：请填写有效整数。" : "：请填写有限数值。");
            else { _commandFieldErrors.Remove(key); if (!_validatingCommandInputs) change(number); }
            CommandDraftChanged();
        }
        _commandFieldValidators[key] = Validate;
        box.RegisterPropertyChangedCallback(NumberBox.TextProperty, (_, _) =>
        {
            if (generation != _commandDraftGeneration) return;
            // NumberBox formats its initial value after its template loads. That is not a source edit.
            if (!_commandInputsChanged.Contains(key)
                && double.TryParse(box.Text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out double value) && value == initial) return;
            if (!_commandInputsChanged.Contains(key) && double.IsNaN(initial) && string.IsNullOrEmpty(box.Text)) return;
            _commandInputsChanged.Add(key); Validate();
        });
        box.ValueChanged += (_, _) =>
        {
            if (generation != _commandDraftGeneration || box.Value.Equals(initial) && !_commandInputsChanged.Contains(key)) return;
            _commandInputsChanged.Add(key); Validate();
        };
    }

    private JsonObject? CaptureCommandDraft()
    {
        SynchronizePendingCommandInputs();
        if (!HasCommandDraft || _document?.Commands.FirstOrDefault(command => command.Id == _commandDraftId) is not { } command) return null;
        var inputs = new JsonObject();
        foreach (string key in _commandInputsChanged)
            if (_commandInputs.TryGetValue(key, out var control)) inputs[key] = control is NumberBox number ? number.Text : ((TextBox)control).Text;
        return new() { ["section"] = command.Section, ["index"] = command.Index, ["data"] = _commandDraft?.DeepClone(),
            ["dialogue"] = _dialogueText?.Text, ["json"] = _commandJson?.Text, ["inputs"] = inputs };
    }
    private void RestoreCommandDraft(JsonObject draft)
    {
        if (_document?.Commands.FirstOrDefault(command => command.Section == draft["section"]?.ToString()
            && command.Index == draft["index"]?.GetValue<int>()) is not { } command || draft["data"] is not JsonObject data) return;
        SelectCommand(command.Id);
        _properties.Children.Clear(); _numbers.Clear(); ClearCommandDraft(); BuildCommandProperties(command, data);
        if (_dialogueText is not null && draft["dialogue"] is { } dialogue) _dialogueText.Text = dialogue.ToString();
        if (draft["inputs"] is JsonObject inputs)
            foreach (var pair in inputs)
                if (_commandInputs.TryGetValue(pair.Key, out var control))
                { if (control is NumberBox number) number.Text = pair.Value?.ToString() ?? ""; else ((TextBox)control).Text = pair.Value?.ToString() ?? ""; }
        if (_commandJson is not null && draft["json"] is { } json) _commandJson.Text = json.ToString();
        SynchronizePendingCommandInputs();
        RefreshHeader();
    }
}
