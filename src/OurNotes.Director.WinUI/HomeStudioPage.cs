using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace OurNotes.Director.WinUI;

/// <summary>A native home composition editor. Selection, isolated preview and export scope are independent.</summary>
internal sealed partial class HomeStudioPage : UserControl, IAsyncDisposable
{
    const string SceneId = "$scene";
    readonly MainWindow window;
    readonly NativeViewport viewport = new();
    readonly StudioTimeline timeline = new();
    readonly ListView objects = new() { SelectionMode = ListViewSelectionMode.Single, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    readonly StackPanel properties = Ui.Stack(11);
    readonly TextBlock title = Ui.Text("未打开主页", 15), sourceLabel = Ui.Text("", 12, true);
    readonly CheckBox autoKey = new() { Content = "自动关键帧", IsChecked = true };
    readonly CheckBox previewOnly = new() { Content = "仅预览此角色", IsEnabled = false };
    readonly CheckBox repeat = new() { Content = "循环播放", IsChecked = true };
    readonly InfoBar notice = new() { IsClosable = true };
    readonly StackPanel welcome;
    readonly Dictionary<string, NumberBox> numbers = new(StringComparer.Ordinal);
    readonly CancellationTokenSource lifetime = new();
    readonly SemaphoreSlim previewGate = new(1, 1);
    readonly DispatcherQueueTimer previewTimer, draftTimer;
    HomeStudioDocument? document;
    Metadata? metadata;
    HomeEditState? pendingPreview;
    string? selectedMarker, dragTarget;
    HomeTransform? dragStart, dragValue;
    double dragTime;
    long loadVersion, editVersion;
    bool refreshing, active, disposed, restored, previewLoaded, suppressPreview, playbackRequested;
    string? archiveOverride;
    HomeStudioDocument? dragDocument;
    ComboBox? animationBox, loopBox, framingBox;
    CheckBox? visibleBox, roomBox, postBox;

    internal bool HasUnsavedChanges => document?.HasUnsavedChanges ?? false;
    string ArchivePath => archiveOverride ?? window.Settings.ArchivePath;
    static string DraftPath => Path.Combine(DesktopSettings.DataDirectory, "studio-home-draft.json");

    internal HomeStudioPage(MainWindow window)
    {
        this.window = window;
        ToolTipService.SetToolTip(autoKey, "开启时只编辑当前时刻；关闭时整体调整已有轨道，保留原有运动和关键帧。");
        HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Stretch;
        var file = StudioLayout.Tool("文件", () => { }, "\uE8B7");
        var menu = new MenuFlyout();
        void Menu(string text, Func<Task> action) { var item = new MenuFlyoutItem { Text = text }; item.Click += (_, _) => _ = SafeAsync(action); menu.Items.Add(item); }
        Menu("选择主页 / 新建", ChooseHomeAsync); Menu("打开项目…", () => OpenAsync()); Menu("另存为…", () => SaveAsync(true)); Menu("恢复草稿", RestoreDraftAsync); Menu("主页 JSON…", ShowJsonAsync);
        file.Flyout = menu;
        var toolbar = new Grid { ColumnSpacing = 8 };
        toolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); toolbar.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); toolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var edits = Ui.Row(5, file, StudioLayout.AsyncTool("保存", () => SaveAsync(), "\uE74E"),
            StudioLayout.Tool("", () => Edit(() => document?.Undo()), "\uE7A7"), StudioLayout.Tool("", () => Edit(() => document?.Redo()), "\uE7A6"));
        ToolTipService.SetToolTip(edits.Children[2], "撤销 · Ctrl+Z"); ToolTipService.SetToolTip(edits.Children[3], "重做 · Ctrl+Y");
        edits.Children.Add(StudioMenus.Button("视图", HomeViewMenu));
        toolbar.Children.Add(edits); toolbar.Children.Add(title); Grid.SetColumn(title, 1); title.VerticalAlignment = VerticalAlignment.Center;
        var finalTools = Ui.Row(7, StudioLayout.AsyncTool("导出…", ExportAsync, "\uE898", true));
        toolbar.Children.Add(finalTools); Grid.SetColumn(finalTools, 2);

        var left = new Grid { RowSpacing = 9 };
        left.RowDefinitions.Add(new() { Height = GridLength.Auto }); left.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); left.RowDefinitions.Add(new() { Height = GridLength.Auto });
        left.Children.Add(Ui.Stack(8, Ui.Row(6, StudioLayout.Section("主页对象"), StudioMenus.Button("…", HomeObjectMenu)), StudioLayout.AsyncTool("选择主页", ChooseHomeAsync, "\uE8E5")));
        left.Children.Add(objects); Grid.SetRow(objects, 1);
        var footer = Ui.Stack(4, sourceLabel); left.Children.Add(footer); Grid.SetRow(footer, 2);
        welcome = Ui.Stack(13, new TextBlock { Text = "让主页动起来", FontSize = 21, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
            Ui.Text("选择主页，在画布中摆放角色，用时间轴编排动作。", 13, true),
            Ui.Row(8, StudioLayout.AsyncTool("选择主页", ChooseHomeAsync, "\uE8E5", true), StudioLayout.AsyncTool("打开项目", () => OpenAsync(), "\uE8E5")),
            StudioLayout.AsyncTool("粘贴主页 JSON", ShowJsonAsync, "\uE77F"));
        welcome.HorizontalAlignment = HorizontalAlignment.Center; welcome.VerticalAlignment = VerticalAlignment.Center;
        foreach (var label in welcome.Children.OfType<TextBlock>()) label.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 220, 230, 241));
        var center = new Grid(); center.Children.Add(viewport); center.Children.Add(welcome);
        notice.VerticalAlignment = VerticalAlignment.Top; notice.Margin = new(8); center.Children.Add(notice);
        var right = Ui.Scroll(Ui.Stack(6, StudioLayout.Section("检查器"), autoKey, properties));
        Content = new StudioLayout(left, center, right, timeline, toolbar);
        properties.Children.Add(Ui.Text("在左侧或画布中选择角色，调整其动作与位置。", 13, true));

        objects.SelectionChanged += (_, _) => { if (!refreshing) Select((objects.SelectedItem as ListViewItem)?.Tag?.ToString()); };
        previewOnly.Checked += (_, _) => { if (!refreshing) _ = SafeAsync(ReloadPreviewAsync); };
        previewOnly.Unchecked += (_, _) => { if (!refreshing) _ = SafeAsync(ReloadPreviewAsync); };
        viewport.SelectionChanged += (_, id) => Select(id ?? SceneId);
        viewport.ContextMenuFactory = HomeObjectMenu;
        viewport.ObjectDragged += (_, drag) => Drag(drag);
        viewport.ObjectScaled += (_, scale) => ScaleCharacter(scale);
        viewport.FramePresented += (_, frame) => OnFrame(frame);
        viewport.Failed += (_, error) => Notice(error.Message);
        timeline.SeekRequested += time => _ = SafeAsync(() => SeekAsync(time));
        timeline.PlayPauseRequested += () => _ = SafeAsync(TogglePlaybackAsync);
        timeline.DurationChanged += value => { if (!refreshing && document is not null) Edit(() => document.SetDuration(value), reload: true); };
        timeline.MarkerSelected += marker =>
        {
            selectedMarker = marker.Id; var key = document?.TimelineKeys().FirstOrDefault(k => k.Id == marker.Id);
            Select(key?.CharacterKey ?? SceneId); _ = SafeAsync(() => SeekAsync(marker.Time));
        };
        timeline.MarkerMoved += (marker, time) => Edit(() => document?.MoveKey(marker.Id, time));
        timeline.AddKeyRequested += AddKey;
        timeline.DeleteKeyRequested += () => { if (selectedMarker is { } id) Edit(() => { document?.DeleteKey(id); selectedMarker = null; }); };
        previewTimer = DispatcherQueue.CreateTimer(); previewTimer.Interval = TimeSpan.FromMilliseconds(35); previewTimer.IsRepeating = false;
        previewTimer.Tick += (_, _) => _ = SafeAsync(ApplyPreviewAsync);
        draftTimer = DispatcherQueue.CreateTimer(); draftTimer.Interval = TimeSpan.FromMilliseconds(800); draftTimer.IsRepeating = false; draftTimer.Tick += (_, _) => SaveDraft();
        Loaded += (_, _) => { active = true; _ = SafeAsync(ActivateAsync); };
        Unloaded += (_, _) => Deactivate();
        KeyDown += (_, args) =>
        {
            bool ctrl = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
            if (!ctrl) return;
            switch (args.Key)
            {
                case VirtualKey.S: args.Handled = true; _ = SafeAsync(() => SaveAsync()); break;
                case VirtualKey.O: args.Handled = true; _ = SafeAsync(() => OpenAsync()); break;
                case VirtualKey.Z: if (args.OriginalSource is not TextBox) { args.Handled = true; Edit(() => document?.Undo()); } break;
                case VirtualKey.Y: if (args.OriginalSource is not TextBox) { args.Handled = true; Edit(() => document?.Redo()); } break;
            }
        };
        AllowDrop = true;
        DragOver += (_, args) => { if (args.DataView.Contains(StandardDataFormats.StorageItems)) args.AcceptedOperation = DataPackageOperation.Copy; };
        Drop += (_, args) => _ = SafeAsync(async () => { if (args.DataView.Contains(StandardDataFormats.StorageItems)) { var files = await args.DataView.GetStorageItemsAsync(); if (files.FirstOrDefault()?.Path is { } path) await OpenAsync(path); } });
    }

    async Task SafeAsync(Func<Task> action)
    { try { if (!disposed) await action(); } catch (OperationCanceledException) { } catch (Exception error) { if (!disposed) Notice(error.Message); } }
    void Notice(string message, bool error = true)
    { notice.ActionButton = null; notice.Title = error ? "需要调整" : "主页提示"; notice.Message = message; notice.Severity = error ? InfoBarSeverity.Warning : InfoBarSeverity.Informational; notice.IsOpen = true; }
    void Pause() { playbackRequested = false; viewport.Pause(); timeline.IsPlaying = false; }
    async Task ActivateAsync()
    {
        if (!restored)
        {
            restored = true;
            if (document is null && File.Exists(DraftPath))
            {
                var draft = JsonNode.Parse(await File.ReadAllTextAsync(DraftPath, lifetime.Token)) as JsonObject;
                if (draft is not null) SetDocument(HomeStudioDocument.FromDraft(draft));
            }
        }
        if (document is not null && !previewLoaded && !string.IsNullOrWhiteSpace(ArchivePath)) await LoadMetadataAndPreviewAsync();
        else if (document is not null && pendingPreview is not null) await ApplyPreviewAsync();
    }
    async Task<bool> ConfirmReplaceAsync()
    {
        if (!HasUnsavedChanges) return true;
        var dialog = new ContentDialog { XamlRoot = window.RootXaml, Title = "保存当前主页项目？", Content = "打开另一个项目之前，可以先保存当前修改。",
            PrimaryButtonText = "保存", SecondaryButtonText = "不保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
        var answer = await dialog.ShowAsync();
        if (answer == ContentDialogResult.Primary) { await SaveAsync(); return !HasUnsavedChanges; }
        return answer == ContentDialogResult.Secondary;
    }
    async Task ChooseHomeAsync()
    {
        if (string.IsNullOrWhiteSpace(ArchivePath)) { window.ShowResourceManager(); return; }
        var rows = await Task.Run(() => new HomeCatalog(new Archive(ArchivePath)).List(), lifetime.Token);
        if (rows.Count == 0) { Notice("当前归档没有主页素材。请在素材管理中下载包含主页的归档。", false); return; }
        var search = Ui.Input("查找主页", placeholder: "输入名称或编号");
        var list = new ListView { Height = 350, SelectionMode = ListViewSelectionMode.Single, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var all = rows.OfType<JsonObject>().ToArray();
        void Filter()
        {
            list.Items.Clear();
            foreach (var row in all.Where(r => r.ToJsonString().Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                string label = LocalName(row["names"], window.Settings.Language, "主页 " + row["spotId"]);
                var item = new ListViewItem { Tag = row, Content = Ui.Stack(3, Ui.Text(label), Ui.Text((row["site"]?.ToString() == "jp" ? "日服" : "国际服") + " · " + row["spotId"], 12, true)) };
                list.Items.Add(item);
            }
            if (list.Items.Count > 0) list.SelectedIndex = 0;
        }
        search.TextChanged += (_, _) => Filter(); Filter();
        var dialog = new ContentDialog { XamlRoot = window.RootXaml, Title = "选择主页", Content = Ui.Stack(12, search, list), PrimaryButtonText = "开始创作", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || (list.SelectedItem as ListViewItem)?.Tag is not JsonObject selected) return;
        if (!await ConfirmReplaceAsync()) return;
        long spot = long.Parse(selected["spotId"]!.ToString(), CultureInfo.InvariantCulture); string site = selected["site"]!.ToString();
        var data = await Task.Run(() => ReadMetadata(ArchivePath, new(spot, site), window.Settings.Language), lifetime.Token);
        var project = new HomeProject(data.Source) { Duration = Math.Max(1d / 30, data.Duration) };
        SetDocument(new(project), data); await ReloadPreviewAsync();
    }

    internal async Task OpenAsync(string? path = null)
    {
        path ??= await window.PickFileAsync(extensions: [".json"]); if (path is null) return;
        var replacement = HomeStudioDocument.Open(path);
        if (!await ConfirmReplaceAsync()) return;
        SetDocument(replacement); window.Settings.RememberProject(path); window.SaveSettings(); await LoadMetadataAndPreviewAsync();
    }
    internal async Task SaveAsync(bool saveAs = false)
    {
        CommitDrag(); var current = document; if (current is null) return;
        string? path = current.FilePath;
        if (saveAs || path is null) path = await window.PickFileAsync(true, path is null ? "home.json" : Path.GetFileName(path), [".json"]);
        if (path is null) return;
        current.Save(path); window.Settings.RememberProject(path); window.SaveSettings(); RefreshHeader(); SaveDraft();
    }
    void SetDocument(HomeStudioDocument value, Metadata? data = null)
    {
        Pause(); viewport.InvalidateSource(); document = value; metadata = data; loadVersion++; editVersion++; previewLoaded = false; pendingPreview = null; selectedMarker = null;
        dragStart = dragValue = null; dragTarget = null; dragDocument = null;
        refreshing = true; previewOnly.IsChecked = false; refreshing = false;
        welcome.Visibility = Visibility.Collapsed; notice.IsOpen = false;
        RefreshObjects(); RefreshTimeline(); BuildProperties(); RefreshHeader();
    }
    async Task LoadMetadataAndPreviewAsync()
    {
        if (document is null) return;
        if (string.IsNullOrWhiteSpace(ArchivePath)) { Notice("请先在素材管理中选择归档目录。", false); return; }
        var current = document; var version = ++loadVersion; string archive = ArchivePath;
        var data = await Task.Run(() => ReadMetadata(archive, current.Project.Source, window.Settings.Language), lifetime.Token);
        if (disposed || version != loadVersion || current != document) return;
        metadata = data; RefreshObjects(); RefreshTimeline(); BuildProperties(); RefreshHeader(); await ReloadPreviewAsync();
    }
    async Task ReloadPreviewAsync()
    {
        if (document is null || suppressPreview || disposed) return;
        if (!active) { previewLoaded = false; return; }
        if (string.IsNullOrWhiteSpace(ArchivePath)) { Notice("请先选择归档目录。", false); return; }
        Pause(); var current = document; long version = ++loadVersion; previewLoaded = false;
        string? character = previewOnly.IsChecked == true ? current.SelectedCharacterKey : null;
        var project = current.Project;
        var (width, height) = PreviewSize(project.Output.Width, project.Output.Height);
        await previewGate.WaitAsync(lifetime.Token);
        try
        {
            if (version != loadVersion || current != document || disposed) return;
            await viewport.LoadHomeAsync(new(ArchivePath, project, character, width, height));
            if (version != loadVersion || current != document || disposed) return;
            await viewport.SetHomeEditStateAsync(current.Project.EditState);
            await viewport.SeekAsync(current.Playhead); viewport.SelectedTargetId = current.SelectedCharacterKey;
            previewLoaded = true; pendingPreview = null; notice.IsOpen = false;
            if (metadata?.RecoveryNote is { Length: > 0 } recoveryNote) Notice(recoveryNote, false);
            if (viewport.LastFrame is { } presented) OnFrame(presented);
            if (!active) viewport.Pause();
        }
        finally { previewGate.Release(); }
    }
    void QueuePreview(HomeEditState? state = null)
    {
        pendingPreview = state ?? document?.Project.EditState; editVersion++;
        if (!active || suppressPreview || disposed) return;
        previewTimer.Stop(); previewTimer.Start();
    }
    async Task ApplyPreviewAsync()
    {
        if (!previewLoaded || pendingPreview is null || document is null || !active || disposed || suppressPreview) return;
        var current = document; var state = pendingPreview; long version = editVersion;
        await previewGate.WaitAsync(lifetime.Token);
        try
        {
            if (current != document || version != editVersion || !previewLoaded || disposed) return;
            await viewport.SetHomeEditStateAsync(state);
            if (version == editVersion) pendingPreview = null;
        }
        finally { previewGate.Release(); }
    }

    void Edit(Action action, bool reload = false)
    {
        if (refreshing || disposed) return;
        try
        {
            var previousSource = document?.Project.Source;
            var previousOutput = document?.Project.Output;
            var previousDuration = document?.Project.Duration;
            Pause(); action();
            RefreshHeader(); RefreshObjects(); RefreshTimeline(); RefreshValues();
            draftTimer.Stop(); draftTimer.Start();
            if (document?.Project.Source != previousSource) _ = SafeAsync(LoadMetadataAndPreviewAsync);
            else if (reload || document?.Project.Output != previousOutput || document?.Project.Duration != previousDuration) _ = SafeAsync(ReloadPreviewAsync);
            else QueuePreview();
        }
        catch (Exception error) { Notice(error.Message); RefreshValues(); RefreshTimeline(); }
    }
    void Select(string? id)
    {
        if (refreshing || document is null) return;
        string? key = id is null or SceneId ? null : id;
        if (metadata is not null && key is not null && !metadata.Characters.Any(c => c.Key == key)) return;
        bool changed = document.SelectedCharacterKey != key, wasIsolated = previewOnly.IsChecked == true;
        document.SelectedCharacterKey = key;
        refreshing = true;
        objects.SelectedItem = objects.Items.OfType<ListViewItem>().FirstOrDefault(item => item.Tag?.ToString() == (key ?? SceneId));
        previewOnly.IsEnabled = key is not null && metadata?.Characters.FirstOrDefault(c => c.Key == key)?.Supported != false;
        if (key is null) previewOnly.IsChecked = false;
        viewport.SelectedTargetId = key;
        refreshing = false; BuildProperties();
        if (changed && wasIsolated) _ = SafeAsync(ReloadPreviewAsync);
    }
    void RefreshObjects()
    {
        refreshing = true; objects.Items.Clear();
        if (document is not null)
        {
            objects.Items.Add(new ListViewItem { Tag = SceneId, Content = Ui.Text("房间与画面") });
            foreach (var character in metadata?.Characters ?? [])
            {
                bool visible = document.TransformAt(character.Key, document.Playhead).Visible;
                var item = new ListViewItem { Tag = character.Key, Content = Ui.Stack(2, Ui.Text(character.Name + (visible ? "" : " · 已隐藏")), Ui.Text(character.Key, 11, true)) };
                ToolTipService.SetToolTip(item, character.Supported ? "拖动摆放 · Ctrl+滚轮缩放角色 · 右键导出素材" : character.Error);
                StudioMenus.Context(item, () => { Select(character.Key); return document?.SelectedCharacterKey == character.Key; }, HomeObjectMenu);
                objects.Items.Add(item);
            }
            objects.SelectedItem = objects.Items.OfType<ListViewItem>().FirstOrDefault(item => item.Tag?.ToString() == (document.SelectedCharacterKey ?? SceneId));
        }
        previewOnly.IsEnabled = document?.SelectedCharacterKey is { } selected && metadata?.Characters.FirstOrDefault(c => c.Key == selected)?.Supported != false;
        refreshing = false;
    }
    void RefreshHeader()
    {
        title.Text = document is null ? "未打开主页" : (document.FilePath is { } file ? Path.GetFileNameWithoutExtension(file) : metadata?.Name ?? "未命名主页") + (HasUnsavedChanges ? " · 未保存" : "");
        sourceLabel.Text = document is null ? "" : (document.Project.Source.Site == "jp" ? "日服" : "国际服") + " · 主页 " + document.Project.Source.SpotId;
    }
    void RefreshTimeline()
    {
        if (document is null) return;
        refreshing = true; timeline.Duration = document.Project.Duration; timeline.Position = document.Playhead;
        var keys = document.TimelineKeys(); var tracks = new List<StudioTimelineTrack>();
        foreach (var character in metadata?.Characters ?? [])
            foreach (var kind in new[] { "transform", "animation" })
            {
                string id = character.Key + ":" + kind;
                tracks.Add(new(id, character.Name + (kind == "transform" ? " · 位置" : " · 动作"), keys.Where(k => k.TrackId == id).Select(k => new StudioTimelineMarker(k.Id, k.TrackId, k.Time, k.Label, k.Kind)).ToArray()));
            }
        tracks.Add(new("grade", "画面 · 调色", keys.Where(k => k.TrackId == "grade").Select(k => new StudioTimelineMarker(k.Id, k.TrackId, k.Time, k.Label, k.Kind)).ToArray()));
        timeline.SetTracks(tracks); refreshing = false;
    }

    void BuildProperties()
    {
        refreshing = true; properties.Children.Clear(); numbers.Clear(); animationBox = loopBox = framingBox = null; visibleBox = roomBox = postBox = null;
        if (document is null) { properties.Children.Add(Ui.Text("选择主页，开始创作。", 13, true)); refreshing = false; return; }
        if (document.SelectedCharacterKey is not { } key)
        {
            properties.Children.Add(Ui.Text("画布", 16));
            AddNumber("canvasWidth", "宽度（像素）", 16, RenderDimensions.MaxSide, 16, Pause);
            AddNumber("canvasHeight", "高度（像素）", 16, RenderDimensions.MaxSide, 8, Pause);
            AddNumber("canvasFps", "帧率", 1, 120, 1, Pause);
            properties.Children.Add(StudioLayout.Tool("应用画布尺寸", ApplyCanvas, "\uE740"));
            if (metadata is not null)
            {
                properties.Children.Add(Ui.Text($"原始完整动画 · {metadata.SourceDuration:0.###} 秒", 12, true));
                properties.Children.Add(StudioLayout.Tool("时间轴适配完整动画", () => Edit(() => document.SetDuration(
                    HomeTiming.Plan(metadata.SourceDuration, fps: document.Project.Output.Fps, loop: metadata.AllLooping).Duration)), "\uE916"));
            }
            var room = roomBox = new CheckBox { Content = "显示房间", IsChecked = document.Project.EditState.Background };
            var post = postBox = new CheckBox { Content = "原始后期效果", IsChecked = document.Project.EditState.PostProcessing };
            room.Checked += (_, _) => Edit(() => document.SetScene(background: true)); room.Unchecked += (_, _) => Edit(() => document.SetScene(background: false));
            post.Checked += (_, _) => Edit(() => document.SetScene(postProcessing: true)); post.Unchecked += (_, _) => Edit(() => document.SetScene(postProcessing: false));
            properties.Children.Add(Ui.Text("房间与画面", 16)); properties.Children.Add(room); properties.Children.Add(post);
            properties.Children.Add(Ui.Text("关闭房间后，画布保留原生透明背景。", 12, true));
            var framing = framingBox = Ui.Choice("构图", [("scene", "原场景相机"), ("fit", "原始动画固定适配")], document.Project.EditState.Framing);
            framing.SelectionChanged += (_, _) => Edit(() => document.SetScene(framing: Ui.Selected(framing))); properties.Children.Add(framing);
            AddNumber("exposure", "曝光（EV）", -10, 10, .1, GradeChanged);
            AddNumber("contrast", "对比度", 0, 4, .05, GradeChanged);
            AddNumber("saturation", "饱和度", 0, 4, .05, GradeChanged);
            AddNumber("vignette", "暗角", 0, 1, .05, GradeChanged);
            properties.Children.Add(StudioLayout.Tool("添加调色关键帧", () => Edit(() => document.AddGradeKey(document.Playhead)), "\uE710"));
        }
        else
        {
            var character = metadata?.Characters.FirstOrDefault(c => c.Key == key);
            properties.Children.Add(Ui.Text(DisplayNames.Character(character?.Name ?? key), 16));
            
            if (character?.Supported == false) { properties.Children.Add(Ui.Text(character.Error ?? "该角色暂不支持独立编辑。", 13, true)); refreshing = false; return; }
            animationBox = Ui.Choice("动作", new[] { ("", "原始动作") }.Concat((character?.Animations ?? []).Select(name => (name, DisplayNames.Animation(name)))), "");
            loopBox = Ui.Choice("循环", [("source", "沿用原设置"), ("once", "播放一次"), ("loop", "持续循环")], "source");
            animationBox.SelectionChanged += (_, _) => AnimationChanged(); loopBox.SelectionChanged += (_, _) => AnimationChanged();
            properties.Children.Add(animationBox); properties.Children.Add(loopBox);
            AddNumber("speed", "动作速度", .05, 8, .1, AnimationChanged);
            AddNumber("sourceTime", "动作内起点（秒）", 0, 36000, .1, AnimationChanged);
            properties.Children.Add(StudioLayout.Tool("在此处开始动作", AnimationChanged, "\uE768"));
            visibleBox = new CheckBox { Content = "显示角色" }; visibleBox.Checked += (_, _) => TransformChanged(); visibleBox.Unchecked += (_, _) => TransformChanged(); properties.Children.Add(visibleBox);
            AddNumber("x", "水平位移（%）", -1000, 1000, 1, TransformChanged);
            AddNumber("y", "垂直位移（%）", -1000, 1000, 1, TransformChanged);
            AddNumber("scale", "缩放（%）", 1, 2000, 5, TransformChanged);
            
            
            
        }
        foreach (var pair in new[] { ("x", "y"), ("speed", "sourceTime"), ("canvasWidth", "canvasHeight"), ("exposure", "contrast"), ("saturation", "vignette") })
            if (numbers.TryGetValue(pair.Item1, out var first) && numbers.TryGetValue(pair.Item2, out var second)) StudioLayout.PairFields(properties, first, second);
        refreshing = false; RefreshValues();
    }
    void ApplyCanvas()
    {
        if (document is null || refreshing) return;
        if (new[] { "canvasWidth", "canvasHeight", "canvasFps" }.Any(key => !numbers.TryGetValue(key, out var number) || !double.IsFinite(number.Value)))
        { Notice("请填写完整的画布宽度、高度和帧率。"); return; }
        double width = Number("canvasWidth", document.Project.Output.Width), height = Number("canvasHeight", document.Project.Output.Height), fps = Number("canvasFps", document.Project.Output.Fps);
        if (width != Math.Truncate(width) || height != Math.Truncate(height) || fps != Math.Truncate(fps)) { Notice("画布宽度、高度和帧率需要填写整数。"); return; }
        Edit(() => document.SetOutput(document.Project.Output with { Width = checked((int)width), Height = checked((int)height), Fps = checked((int)fps) }), reload: true);
    }
    void AddNumber(string id, string label, double min, double max, double step, Action changed)
    {
        var number = new NumberBox { Header = label, Minimum = min, Maximum = max, SmallChange = step, LargeChange = step * 10, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        number.ValueChanged += (_, _) => { if (!refreshing && double.IsFinite(number.Value)) changed(); };
        numbers[id] = number; properties.Children.Add(number);
    }
    void RefreshValues()
    {
        if (document is null) return;
        refreshing = true;
        void Value(string id, double value) { if (numbers.TryGetValue(id, out var number) && number.FocusState == FocusState.Unfocused) number.Value = value; }
        if (document.SelectedCharacterKey is { } key)
        {
            var transform = document.TransformAt(key, document.Playhead); var cue = document.Track(key).AnimationAt(document.Playhead);
            Value("x", transform.X * 100); Value("y", transform.Y * 100); Value("scale", transform.Scale * 100); Value("speed", cue?.Speed ?? 1);
            Value("sourceTime", cue?.SourceTime ?? 0);
            if (visibleBox is not null) visibleBox.IsChecked = transform.Visible;
            if (animationBox is not null) animationBox.SelectedItem = animationBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == (cue?.Name ?? ""));
            if (loopBox is not null) loopBox.SelectedItem = loopBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == (cue?.Loop is null ? "source" : cue.Loop.Value ? "loop" : "once"));
        }
        else
        {
            Value("canvasWidth", document.Project.Output.Width); Value("canvasHeight", document.Project.Output.Height); Value("canvasFps", document.Project.Output.Fps);
            if (roomBox is not null) roomBox.IsChecked = document.Project.EditState.Background;
            if (postBox is not null) postBox.IsChecked = document.Project.EditState.PostProcessing;
            if (framingBox is not null) framingBox.SelectedItem = framingBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == document.Project.EditState.Framing);
            var grade = document.Project.EditState.GradeAt(document.Playhead);
            Value("exposure", grade.Exposure); Value("contrast", grade.Contrast); Value("saturation", grade.Saturation); Value("vignette", grade.Vignette);
        }
        refreshing = false;
    }
    double Number(string id, double fallback) => numbers.TryGetValue(id, out var value) && double.IsFinite(value.Value) ? value.Value : fallback;
    void TransformChanged()
    {
        if (refreshing || document?.SelectedCharacterKey is not { } key) return;
        Edit(() => document.SetTransform(key, document.Playhead, new(Number("x", 0) / 100, Number("y", 0) / 100, Number("scale", 100) / 100, visibleBox?.IsChecked != false), autoKey.IsChecked == true));
    }
    void GradeChanged()
    {
        if (refreshing || document is null) return;
        Edit(() => document.SetGrade(document.Playhead, new(Number("exposure", 0), Number("contrast", 1), Number("saturation", 1), Number("vignette", 0)), autoKey.IsChecked == true));
    }
    void AnimationChanged()
    {
        if (refreshing || document?.SelectedCharacterKey is not { } key || animationBox is null || loopBox is null) return;
        string name = Ui.Selected(animationBox); bool? loop = Ui.Selected(loopBox) switch { "once" => false, "loop" => true, _ => null };
        Edit(() => document.SetAnimation(key, document.Playhead, name.Length == 0 ? null : name, loop, Number("speed", 1), Number("sourceTime", 0)));
    }
    void AddKey()
    {
        if (document is null) return;
        if (document.SelectedCharacterKey is { } key) Edit(() => document.AddTransformKey(key, document.Playhead));
        else Edit(() => document.AddGradeKey(document.Playhead));
    }

    void Drag(PreviewDrag drag)
    {
        if (document is null || refreshing || disposed) return;
        if (dragStart is null || dragTarget != drag.TargetId || dragDocument != document)
        { Pause(); dragTarget = drag.TargetId; dragDocument = document; dragTime = document.Playhead; dragStart = document.TransformAt(drag.TargetId, dragTime); }
        var value = dragStart with { X = Math.Clamp(dragStart.X + drag.DeltaX, -10, 10), Y = Math.Clamp(dragStart.Y + drag.DeltaY, -10, 10) };
        dragValue = value;
        if (drag.IsCompleted)
        { Edit(() => document.SetTransform(drag.TargetId, dragTime, value, autoKey.IsChecked == true)); dragStart = dragValue = null; dragTarget = null; dragDocument = null; }
        else QueuePreview(WithTransform(document.Project.EditState, drag.TargetId, dragTime, value, autoKey.IsChecked == true));
    }
    void CommitDrag()
    {
        if (dragStart is null || dragTarget is null || document != dragDocument || dragValue is null) return;
        document!.SetTransform(dragTarget, dragTime, dragValue, autoKey.IsChecked == true); pendingPreview = document.Project.EditState;
        dragStart = dragValue = null; dragTarget = null; dragDocument = null; RefreshHeader(); RefreshTimeline();
    }
    void ScaleCharacter(PreviewScale scale)
    {
        if (document is null) return;
        var value = document.TransformAt(scale.TargetId, document.Playhead);
        Edit(() => document.SetTransform(scale.TargetId, document.Playhead, value with { Scale = Math.Clamp(value.Scale * scale.Factor, .01, 20) }, autoKey.IsChecked == true));
    }
    static HomeEditState WithTransform(HomeEditState state, string key, double time, HomeTransform value, bool keyframe)
    {
        var tracks = new Dictionary<string, HomeCharacterTrack>(state.Characters, StringComparer.Ordinal); var track = tracks.GetValueOrDefault(key) ?? new();
        tracks[key] = track.WithTransformAt(time, value, keyframe);
        return state with { Characters = tracks };
    }
    async Task SeekAsync(double time)
    {
        if (document is null) return; Pause(); document.Playhead = document.Snap(time); timeline.Position = document.Playhead;
        RefreshValues();
        if (previewLoaded && !suppressPreview) { await ApplyPreviewAsync(); await viewport.SeekAsync(document.Playhead); }
    }
    async Task TogglePlaybackAsync()
    {
        if (document is null) return;
        if (viewport.IsPlaying) { Pause(); return; }
        if (!previewLoaded) await ReloadPreviewAsync();
        if (!previewLoaded) return;
        await ApplyPreviewAsync();
        if (document.Playhead >= document.Project.Duration - 1d / document.Project.Output.Fps) await viewport.SeekAsync(0);
        playbackRequested = true; viewport.Play(); timeline.IsPlaying = true;
    }
    void OnFrame(PreviewFrame frame)
    {
        if (disposed || document is null || !previewLoaded || !viewport.HasCurrentSource) return;
        document.Playhead = Math.Min(frame.Time, document.Project.Duration); timeline.Position = document.Playhead; timeline.IsPlaying = frame.IsPlaying;
        if (!frame.IsPlaying) RefreshValues();
        if (playbackRequested && !frame.IsPlaying && frame.Frame >= Math.Floor(document.Project.Duration * document.Project.Output.Fps + 1e-8))
        {
            playbackRequested = false;
            if (active && repeat.IsChecked == true)
                _ = SafeAsync(async () => { await viewport.SeekAsync(0); if (active) { playbackRequested = true; viewport.Play(); } });
        }
    }

    async Task ExportAsync()
    { CommitDrag(); if (document is not null) { Pause(); await StudioExportDialog.ShowHomeAsync(window, document, document.Playhead); } }
    async Task ShowJsonAsync()
    {
        CommitDrag(); Pause();
        var original = document; var input = new TextBox { Text = original?.ToJson() ?? "", PlaceholderText = "粘贴完整的主页项目 JSON（ournotes-home-project/1）", AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 400,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"), IsSpellCheckEnabled = false };
        var error = Ui.Text("", 12); HomeProject? replacement = null;
        var content = Ui.Stack(10, Ui.Text("高级项目编辑；应用后，画布、时间轴和导出使用同一份内容。", 13, true),
            Ui.Row(8, StudioLayout.Tool("复制项目", () => MainWindow.Copy(input.Text)), StudioLayout.AsyncTool("粘贴", async () => { var data = Clipboard.GetContent(); if (data.Contains(StandardDataFormats.Text)) input.Text = await data.GetTextAsync(); })), input, error);
        var dialog = new ContentDialog { XamlRoot = window.RootXaml, Title = "主页项目 JSON", Content = content, PrimaryButtonText = "应用", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
        dialog.PrimaryButtonClick += (_, args) => { try { replacement = HomeProject.Parse(Unfence(input.Text)); } catch (Exception failure) { args.Cancel = true; error.Text = failure.Message; } };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || replacement is null || document != original || disposed) return;
        bool reload = original is null || !previewLoaded || metadata is null || replacement.Source != original.Project.Source
            || replacement.Output != original.Project.Output || replacement.Duration != original.Project.Duration;
        if (original is null) SetDocument(new HomeStudioDocument(replacement));
        else
        {
            original.ReplaceText(replacement.ToJson().ToJsonString());
            if (reload) { viewport.InvalidateSource(); previewLoaded = false; metadata = null; loadVersion++; }
            RefreshHeader(); RefreshObjects(); RefreshTimeline(); BuildProperties();
        }
        SaveDraft();
        if (!reload) { QueuePreview(); await ApplyPreviewAsync(); return; }
        if (string.IsNullOrWhiteSpace(ArchivePath))
        {
            Notice("主页项目已保留。选择素材目录后即可继续预览，也可以先保存项目文件。", false);
            notice.ActionButton = StudioLayout.AsyncTool("选择素材目录", async () =>
            {
                if (await window.PickFolderAsync() is not { } path || disposed) return;
                window.Settings.ArchivePath = path; window.SaveSettings(); window.ApplySettings();
                await SafeAsync(LoadMetadataAndPreviewAsync);
            }, "\uE8B7");
            return;
        }
        await LoadMetadataAndPreviewAsync();
    }
    static string Unfence(string text)
    {
        text = text.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal) && text.EndsWith("```", StringComparison.Ordinal) && text.IndexOf('\n') is > 0 and var start) return text[(start + 1)..^3].Trim();
        return text;
    }
    internal void SaveDraft()
    {
        CommitDrag(); if (document is null || suppressPreview) return;
        try { DesktopSettings.AtomicWrite(DraftPath, document.ToDraft().ToJsonString(DesktopSettings.JsonOptions)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { window.SetStatus("主页草稿暂时无法保存：" + error.Message); }
    }
    async Task RestoreDraftAsync()
    {
        if (!File.Exists(DraftPath)) { Notice("还没有保存的主页草稿。", false); return; }
        var draft = JsonNode.Parse(await File.ReadAllTextAsync(DraftPath, lifetime.Token)) as JsonObject ?? throw new InvalidDataException("主页草稿无效。");
        if (!await ConfirmReplaceAsync()) return;
        SetDocument(HomeStudioDocument.FromDraft(draft)); await LoadMetadataAndPreviewAsync();
    }
    internal void Deactivate()
    {
        active = false; Pause(); previewTimer.Stop(); draftTimer.Stop(); SaveDraft();
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return; Deactivate(); disposed = true; lifetime.Cancel(); await viewport.DisposeAsync(); lifetime.Dispose();
    }

    sealed record CharacterInfo(string Key, string Name, IReadOnlyList<string> Animations, bool Supported = true, string? Error = null);
    sealed record Metadata(HomeProjectSource Source, string Name, double Duration, double SourceDuration, bool AllLooping, IReadOnlyList<CharacterInfo> Characters, string? RecoveryNote = null);
    static Metadata ReadMetadata(string archivePath, HomeProjectSource source, string language)
    {
        var scene = new HomeCatalog(new Archive(archivePath)).Open(source.SpotId, source.Site, source.AdvId);
        var resources = new HomeResources(scene); var complete = new HomeAnimation(resources); var characters = new List<CharacterInfo>();
        foreach (var character in scene.Characters)
        {
            try
            {
                var animation = new HomeAnimation(resources, character);
                var names = animation.Parts.Select(p => p.Actor.Asset.Animations.Select(a => a.Name).ToHashSet(StringComparer.Ordinal)).ToArray();
                var common = names.Length == 0 ? new HashSet<string>(StringComparer.Ordinal) : names[0]; foreach (var namesInPart in names.Skip(1)) common.IntersectWith(namesInPart);
                characters.Add(new(character.Key, DisplayNames.Character(character.Name), common.Order(StringComparer.Ordinal).ToArray()));
            }
            catch (NotSupportedException error) { characters.Add(new(character.Key, DisplayNames.Character(character.Name), [], false, error.Message)); }
        }
        string? recoveryNote = scene.UnresolvedSpineParts.Count > 0
            ? "此主页有缺少有效组件信息的动画部件，无法安全补全。详情见导出诊断中的 unresolvedSpineParts。"
            : scene.SpineRecoveries.Count > 0 ? "已依据逐帧共有骨骼补全角色背层。原始独立组件变换未被上游导出；补全结果使用角色坐标对齐。" : null;
        return new(new(scene.SpotId, scene.Site, scene.AdvId), LocalName(scene.Spot["name"], language, "主页 " + scene.SpotId),
            HomeTiming.Plan(complete.Duration, fps: 30, loop: complete.AllLooping).Duration, complete.Duration, complete.AllLooping, characters, recoveryNote);
    }
    static string LocalName(JsonNode? names, string language, string fallback)
    {
        if (names is JsonObject obj)
        {
            string alias = language switch { "zh-Hans" => "simplifiedChinese", "zh-Hant" => "traditionalChinese", "ja" => "japanese", "en" => "english", "ko" => "korean", _ => language };
            foreach (string key in new[] { language, alias, "zh-Hans", "simplifiedChinese", "ja", "japanese", "en", "english" }.Distinct(StringComparer.Ordinal)) if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)) return text;
            foreach (var pair in obj) if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)) return text;
        }
        return names is JsonValue node && node.TryGetValue<string>(out var name) && name.Length > 0 ? name : fallback;
    }
    static (int Width, int Height) PreviewSize(int width, int height)
    {
        double scale = Math.Min(1, Math.Min(960d / width, 600d / height));
        return (Math.Max(16, (int)Math.Round(width * scale)), Math.Max(16, (int)Math.Round(height * scale)));
    }

    internal async Task RunSmokeTestAsync(Action<bool, string> check)
    {
        var savedDocument = document; var savedMetadata = metadata;
        bool retainFixture = false;
        suppressPreview = true;
        try
        {
            var model = new HomeStudioDocument(new HomeProject(new HomeProjectSource(77, "intl", 42)) { Duration = 2, Output = new(320, 180, 30, 1) });
            SetDocument(model, new(model.Project.Source, "测试主页", 2, 1, false, [new("alice", "测试角色", ["home_start"])]));
            model.SetTransform("alice", 0, new()); model.SetTransform("alice", 1, new(.2, .1, 1.4));
            Select("alice"); model.Playhead = .5; RefreshValues(); RefreshTimeline(); UpdateLayout();
            check(Math.Abs(model.TransformAt("alice", .5).X - .1) < 1e-9 && model.TimelineKeys().Count == 2, "主页位置关键帧真实插值并进入时间轴");
            check(viewport.IsLoaded && viewport.ActualWidth > 0 && timeline.IsLoaded && objects.Items.Count == 2 && numbers.ContainsKey("scale"), "主页画布、对象和属性在原生工作台布局");
            string text = model.ToJson(); model.SetScene(background: false); model.Undo(); check(model.ToJson() == text, "主页编辑撤销保留完整项目");
            using var snapshot = model.CreateSnapshot(Path.Combine(DesktopSettings.DataDirectory, "smoke-inputs"));
            check(HomeProject.Read(snapshot.FilePath).EditState.Characters["alice"].Keys.Count == 2, "主页导出快照包含实际角色编排");
            if (Environment.GetEnvironmentVariable("OURNOTES_STUDIO_FIXTURE") is { Length: > 0 } fixture)
            {
                archiveOverride = fixture; suppressPreview = false; metadata = ReadMetadata(fixture, model.Project.Source, "zh-Hans");
                model.ResetCharacter("alice"); model.Playhead = .5; await ReloadPreviewAsync();
                var before = viewport.LastFrame ?? throw new InvalidOperationException("主页原生预览没有呈现帧。");
                var original = before.Targets.Single(t => t.Id == "alice");
                check(viewport.HitTest(original.X + original.Width / 2, original.Y + original.Height / 2)?.Id == "alice", "主页画布按真实角色边界命中对象");
                autoKey.IsChecked = false;
                Drag(new("alice", .1, .05, false)); await ApplyPreviewAsync();
                Drag(new("alice", .1, .05, true)); await ApplyPreviewAsync();
                var moved = viewport.LastFrame!.Targets.Single(t => t.Id == "alice");
                check(Math.Abs(moved.X - original.X - .1) < .002 && Math.Abs(moved.Y - original.Y - .05) < .002, "主页拖动偏移实际进入原生画布");
                model.SetScene(background: false); QueuePreview(); await ApplyPreviewAsync();
                var pixels = viewport.LastFrame!.Pixels;
                check(pixels.Rgba.Where((_, index) => index % 4 == 3).Any(alpha => alpha == 0), "主页房间开关输出真实透明像素");
                await viewport.SeekAsync(.5); byte[] first = viewport.LastFrame!.Pixels.Rgba.ToArray();
                await viewport.SeekAsync(1); await viewport.SeekAsync(.5);
                check(first.SequenceEqual(viewport.LastFrame!.Pixels.Rgba), "主页时间轴往返定位得到相同原生画面");
                await viewport.SeekAsync(0); var sequence = viewport.LastFrame!.Sequence; viewport.Play();
                var playing = await viewport.WaitForFrameAsync(sequence); if (playing.Time <= 0) playing = await viewport.WaitForFrameAsync(playing.Sequence);
                viewport.Pause(); check(playing.Time > 0, "主页原生会话持续播放并呈现新帧");
                model.SetTransform("alice", 0, new(.1, .05)); model.SetTransform("alice", 1.5, new(.25, .05, 1.15));
                QueuePreview(); await ApplyPreviewAsync(); await viewport.SeekAsync(.5);
                RefreshObjects(); RefreshTimeline(); BuildProperties(); RefreshHeader(); UpdateLayout();
                retainFixture = true;
            }
        }
        finally
        {
            Pause(); suppressPreview = false;
            if (!retainFixture)
            {
                archiveOverride = null; previewLoaded = false;
                if (savedDocument is not null) SetDocument(savedDocument, savedMetadata);
                else { document = null; metadata = null; objects.Items.Clear(); properties.Children.Clear(); welcome.Visibility = Visibility.Visible; RefreshHeader(); }
            }
        }
    }
}
