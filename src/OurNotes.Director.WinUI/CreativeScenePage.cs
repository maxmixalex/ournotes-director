using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace OurNotes.Director.WinUI;

/// <summary>Windows scene authoring: choose an actor, audition/select its assets, arrange clips.
/// The native instruction editor is deliberately not the primary interaction model.</summary>
internal sealed partial class CreativeScenePage : UserControl, IAsyncDisposable
{
    readonly MainWindow window;
    readonly NativeViewport viewport = new();
    readonly ClipTimeline timeline = new();
    readonly ListView actors = new() { SelectionMode = ListViewSelectionMode.Single };
    readonly ListView assets = new() { SelectionMode = ListViewSelectionMode.Single, CanDragItems = true };
    readonly ComboBox assetKind = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly TextBox search = new() { PlaceholderText = "筛选动作或表情…" };
    readonly StackPanel inspector = new() { Spacing = 12 };
    readonly TextBlock title = new() { Text = "未命名场景", FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock assetHint = new() { Text = "选择角色后，在这里选择动作和表情。", FontSize = 11, TextWrapping = TextWrapping.Wrap, Opacity = .7 };
    readonly InfoBar notice = new() { IsOpen = false, IsClosable = true };
    readonly SemaphoreSlim loadGate = new(1);
    readonly Stack<string> undo = new(), redo = new();
    readonly List<ResultRow> assetRows = [];
    CreativeScene document;
    IDisposable? previewInput;
    string? selectedActor, selectedClip, path;
    string directory = Environment.CurrentDirectory;
    string savedText;
    double playhead;
    bool refreshing, disposed, playingLast;
    long loadVersion, assetVersion;
    CancellationTokenSource? assetRequest;
    bool previewDirty = true;
    string? dragActor;
    double dragX, dragY, dragHeight;
    readonly string draftPath = Path.Combine(DesktopSettings.DataDirectory, "creative-scene-draft.json");
    internal bool HasUnsavedChanges => document.ToJson() != savedText;
    static string S(JsonObject o, string k) => CreativeScene.Text(o, k);
    static double N(JsonObject o, string k, double fallback = 0) => CreativeScene.Number(o, k, fallback);
    internal CreativeScenePage(MainWindow window)
    {
        this.window = window; document = CreativeScene.Blank(window.Settings.Site, window.Settings.Language); savedText = document.ToJson();
        HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Stretch;
        var toolbar = new Grid { ColumnSpacing = 16 };
        toolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); toolbar.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); toolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var file = StudioMenus.Button("文件", FileMenu);
        var edit = StudioMenus.Button("编辑", EditMenu);
        var view = StudioMenus.Button("视图", ViewMenu);
        toolbar.Children.Add(Ui.Row(4, file, edit, view));
        toolbar.Children.Add(title); Grid.SetColumn(title, 1);
        var publish = Ui.Row(8, Button("保存", () => SaveAsync(), "\uE74E"), Button("导出…", () => ExportAsync("clip"), "\uE898", true));
        toolbar.Children.Add(publish); Grid.SetColumn(publish, 2);

        var library = new Grid { RowSpacing = 8 };
        library.RowDefinitions.Add(new() { Height = GridLength.Auto }); library.RowDefinitions.Add(new() { Height = new(130), MinHeight = 70 });
        library.RowDefinitions.Add(new() { Height = GridLength.Auto }); library.RowDefinitions.Add(new() { Height = GridLength.Auto });
        library.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); library.RowDefinitions.Add(new() { Height = GridLength.Auto });
        library.Children.Add(Ui.Row(8, StudioLayout.Section("角色"), Button("添加…", AddActorAsync, "\uE710")));
        library.Children.Add(actors); Grid.SetRow(actors, 1);
        foreach (var (key, label) in new[] { ("motion", "动作"), ("expression", "表情"), ("voice", "语音台词") }) assetKind.Items.Add(new ComboBoxItem { Content = label, Tag = key });
        assetKind.SelectedIndex = 0; library.Children.Add(assetKind); Grid.SetRow(assetKind, 2);
        library.Children.Add(search); Grid.SetRow(search, 3); library.Children.Add(assets); Grid.SetRow(assets, 4);
        var libraryFooter = Ui.Stack(8, assetHint, Button("导入声音…", ImportVoiceAsync, "\uE8D6"));
        library.Children.Add(libraryFooter); Grid.SetRow(libraryFooter, 5);
        var center = new Grid(); center.Children.Add(viewport);
        notice.VerticalAlignment = VerticalAlignment.Top; notice.Margin = new(12); center.Children.Add(notice);
        Content = new StudioLayout(library, center, Ui.Scroll(inspector), timeline, toolbar);

        actors.SelectionChanged += (_, _) =>
        {
            if (refreshing) return;
            selectedActor = (actors.SelectedItem as ListViewItem)?.Tag as string; selectedClip = null;
            viewport.SelectedTargetId = selectedActor; BuildInspector(); _ = Safe(LoadAssetsAsync);
        };
        viewport.SelectionChanged += (_, id) =>
        {
            if (refreshing || id == selectedActor) return;
            selectedActor = id; selectedClip = null; RefreshActors(); BuildInspector(); _ = Safe(LoadAssetsAsync);
        };
        viewport.ObjectDragged += (_, drag) => _ = Safe(() => DragAsync(drag));
        viewport.ObjectScaled += (_, scale) => _ = Safe(() => ScaleAsync(scale));
        viewport.FramePresented += (_, frame) =>
        {
            if (disposed || !viewport.HasCurrentSource) return;
            playhead = frame.Time; timeline.Position = playhead; timeline.IsPlaying = frame.IsPlaying;
            bool ended = playingLast && !frame.IsPlaying; playingLast = frame.IsPlaying;
            if (ended && timeline.Repeat && playhead >= document.Duration - .04) _ = Safe(async () => { await SeekAsync(0); viewport.Play(); });
        };
        viewport.Failed += (_, error) => ShowError(error.Message);
        viewport.ContextMenuFactory = ActorMenu;
        timeline.PlayRequested += () => _ = Safe(TogglePlayAsync);
        timeline.SeekRequested += seconds => _ = Safe(() => SeekAsync(seconds));
        timeline.ClipSelected += id => { selectedClip = id; selectedActor = S(document.Clip(id), "actor"); viewport.Pause(); RefreshActors(); BuildInspector(); };
        timeline.ClipEdited += (id, seconds, resize) => _ = Safe(() => EditAsync(() => { if (resize) document.ResizeClip(id, seconds); else document.MoveClip(id, seconds); }));
        timeline.AddRequested += (actor, kind, at) => _ = Safe(() => AddToTrackAsync(actor, kind, at));
        timeline.ContextMenuFactory = ClipMenu;
        timeline.AssetDropped += (data, at) => _ = Safe(() => DropAssetAsync(data, at));
        assetKind.SelectionChanged += (_, _) =>
        { search.PlaceholderText = AssetKind == "voice" ? "输入台词，按 Enter 搜索…" : "筛选名称…"; _ = Safe(LoadAssetsAsync); };
        search.TextChanged += (_, _) => { if (AssetKind != "voice") FilterAssets(); };
        search.KeyDown += (_, args) => { if (args.Key == VirtualKey.Enter) { _ = Safe(LoadAssetsAsync); args.Handled = true; } };
        assets.DoubleTapped += (_, _) => { if ((assets.SelectedItem as ListViewItem)?.Tag is ResultRow row) _ = Safe(() => InsertAssetAsync(row, AssetKind, playhead)); };
        assets.DragItemsStarting += (_, args) =>
        {
            if (args.Items.FirstOrDefault() is not ListViewItem { Tag: ResultRow row }) { args.Cancel = true; return; }
            var data = new JsonObject { ["kind"] = AssetKind, ["actor"] = selectedActor, ["resource"] = row.Id, ["label"] = row.Title, ["data"] = row.Data.DeepClone() };
            args.Data.SetData("OurNotes.SceneAsset", data.ToJsonString()); args.Data.RequestedOperation = DataPackageOperation.Copy;
        };
        KeyDown += (_, args) =>
        {
            bool ctrl = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
            if (ctrl && args.Key == VirtualKey.S) { _ = Safe(() => SaveAsync()); args.Handled = true; return; }
            if (args.OriginalSource is TextBox or NumberBox) return;
            if (ctrl && args.Key == VirtualKey.Z) { _ = Safe(() => UndoAsync(false)); args.Handled = true; }
            else if (ctrl && args.Key == VirtualKey.Y) { _ = Safe(() => UndoAsync(true)); args.Handled = true; }
            else if (args.Key == VirtualKey.Delete && selectedClip is not null) { _ = Safe(DeleteClipAsync); args.Handled = true; }
            else if (args.Key == VirtualKey.Space) { _ = Safe(TogglePlayAsync); args.Handled = true; }
            else if (args.Key == VirtualKey.F) { viewport.FitCanvas(); args.Handled = true; }
        };
        Loaded += (_, _) => { Refresh(); viewport.SetEmptyCanvas((int)N(document.Canvas, "width"), (int)N(document.Canvas, "height")); };
        Unloaded += (_, _) => Deactivate(); Refresh();
    }
    string AssetKind => ((assetKind.SelectedItem as ComboBoxItem)?.Tag as string) ?? "motion";
    Button Button(string text, Func<Task> action, string glyph, bool primary = false) => StudioLayout.AsyncTool(text, () => Safe(action), glyph, primary);
    async Task Safe(Func<Task> action)
    { try { if (!disposed) await action(); } catch (OperationCanceledException) { } catch (Exception error) { if (!disposed) ShowError(error.Message); } }
    void ShowError(string message) { notice.Severity = InfoBarSeverity.Error; notice.Message = message; notice.IsOpen = true; }
    void Refresh()
    {
        RefreshActors(); timeline.Refresh(document, selectedClip); timeline.Position = playhead; BuildInspector();
        title.Text = (path is null ? "未命名场景" : Path.GetFileNameWithoutExtension(path)) + (HasUnsavedChanges ? " ●" : "");
    }
    void RefreshActors()
    {
        refreshing = true; actors.Items.Clear();
        foreach (var actor in document.Actors.OfType<JsonObject>())
        {
            string id = S(actor, "id"), name = CreativeScene.Text(actor, "name", id);
            var item = new ListViewItem { Tag = id, Content = Ui.Row(8, new FontIcon { Glyph = "\uE77B", FontSize = 14 }, new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis }), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            item.ContextRequested += (_, args) => { selectedActor = id; selectedClip = null; BuildInspector(); ActorMenu().ShowAt(item); args.Handled = true; };
            actors.Items.Add(item); if (id == selectedActor) actors.SelectedItem = item;
        }
        refreshing = false;
    }
    async Task EditAsync(Action action, bool reload = true)
    {
        viewport.Pause(); string before = document.ToJson();
        try { action(); document.Validate(); }
        catch { document = CreativeScene.Parse(before); Refresh(); throw; }
        if (document.ToJson() == before) return;
        undo.Push(before); redo.Clear(); previewDirty = true; SaveDraft(); Refresh();
        if (reload) await ReloadAsync();
    }
    async Task UndoAsync(bool forward)
    {
        var from = forward ? redo : undo; var to = forward ? undo : redo; if (from.Count == 0) return;
        viewport.Pause(); to.Push(document.ToJson()); document = CreativeScene.Parse(from.Pop());
        selectedClip = null; previewDirty = true; Refresh(); SaveDraft(); await ReloadAsync();
    }
    async Task DeleteClipAsync()
    { if (selectedClip is { } id) await EditAsync(() => { document.RemoveClip(id); selectedClip = null; }); }
    async Task LoadAssetsAsync()
    {
        if (disposed || !IsLoaded) return;
        assetRequest?.Cancel(); assetRequest?.Dispose(); assetRequest = new();
        var token = assetRequest.Token; long version = ++assetVersion; string kind = AssetKind;
        assetRows.Clear(); FilterAssets();
        if (!Directory.Exists(window.Settings.ArchivePath)) { assetHint.Text = "添加角色前，先选择素材库。空白画布仍可编辑、保存。"; return; }
        JsonObject? actor = document.Actors.OfType<JsonObject>().FirstOrDefault(a => S(a, "id") == selectedActor);
        if (actor is null && kind != "voice") { assetHint.Text = "先选择一个角色。"; return; }
        var request = new Dictionary<string, string?> { ["site"] = document.Site, ["limit"] = "200" };
        if (kind == "voice")
        { if (search.Text.Trim().Length == 0) { assetHint.Text = "按台词搜索归档语音，或直接导入声音文件。"; return; } request["query"] = search.Text.Trim(); }
        else request["modelId"] = S(actor!, "model");
        string command = kind == "voice" ? "voice.search" : kind + ".list";
        assetHint.Text = "正在读取素材…";
        var result = await window.QueryAsync(command, request, token);
        if (version != assetVersion || disposed) return;
        if (!result.Success) throw new IOException(result.Error ?? "素材读取失败。");
        assetRows.AddRange(ResultRow.ForPicker(result.Json, command, kind == "voice" ? "voiceId" : kind, request)); FilterAssets();
        assetHint.Text = assetRows.Count == 0 ? "没有匹配素材。" : "双击添加到播放头，或拖到时间轴。";
    }
    void FilterAssets()
    {
        assets.Items.Clear(); string query = AssetKind == "voice" ? "" : search.Text.Trim();
        foreach (var row in assetRows.Where(row => row.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            var item = new ListViewItem { Tag = row, Content = new TextBlock { Text = row.Title, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = false }, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            ToolTipService.SetToolTip(item, row.Subtitle); assets.Items.Add(item);
        }
    }
    public void Deactivate() { viewport.Pause(); SaveDraft(); }
    public void SaveDraft()
    {
        if (disposed || !HasUnsavedChanges) return;
        try { Directory.CreateDirectory(DesktopSettings.DataDirectory); DesktopSettings.AtomicWrite(draftPath, document.ToJson()); }
        catch (IOException error) { ShowError("草稿未能保存：" + error.Message); }
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return; SaveDraft(); disposed = true; assetRequest?.Cancel(); loadVersion++;
        await loadGate.WaitAsync();
        try { await viewport.DisposeAsync(); previewInput?.Dispose(); }
        finally { loadGate.Release(); assetRequest?.Dispose(); }
    }
}
