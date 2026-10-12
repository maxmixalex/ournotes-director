using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace OurNotes.Director.WinUI;

/// <summary>A scene editor: the document, native viewport and timeline share one authored state.</summary>
internal sealed class SceneStudioPage : UserControl, IAsyncDisposable
{
    private readonly MainWindow _window;
    private readonly NativeViewport _viewport = App.TraceConstruction("Scene NativeViewport", () => new NativeViewport());
    private readonly StudioTimeline _timeline = App.TraceConstruction("Scene StudioTimeline", () => new StudioTimeline());
    private readonly ListView _objects = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly Button _addActor;
    private readonly StackPanel _properties = Ui.Stack(12);
    private readonly Dictionary<string, NumberBox> _numbers = new(StringComparer.Ordinal);
    private readonly TextBlock _name = new() { Text = "未命名场景", FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _source = Ui.Text("", 11, true);
    private readonly TextBlock _hint = Ui.Text("选择对象后，在画布中拖动；滚轮调整大小。", 11, true);
    private readonly CheckBox _autoKey = new() { Content = "自动关键帧", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _loop = new() { Content = "循环", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly InfoBar _notice = new() { IsClosable = true };
    private readonly StackPanel _welcome;
    private readonly DispatcherQueueTimer _propertyTimer;
    private readonly DispatcherQueueTimer _draftTimer;
    private readonly SemaphoreSlim _loadGate = new(1);
    private StudioSceneDocument? _document;
    private StudioSceneDocument? _previewDocument;
    private SceneSnapshot? _previewInput;
    private StudioTransform? _dragStart;
    private string? _dragId, _selectedMarker;
    private string? _savedJson;
    private Action? _pendingProperty;
    private Task _pendingLiveEdit = Task.CompletedTask;
    private Task _pendingPreviewCommit = Task.CompletedTask;
    private long _loadVersion;
    private bool _refreshing, _disposed, _looping, _suppressReload;
    internal bool HasUnsavedChanges => _document is not null && _document.ToJson() != _savedJson;

    internal SceneStudioPage(MainWindow window)
    {
        App.TraceStartup("SceneStudioPage fields initialized");
        _window = window;
        var file = new DropDownButton { Content = new FontIcon { Glyph = "\uE8E5", FontSize = 15 }, Padding = new(10, 7, 10, 7) };
        var menu = new MenuFlyout();
        Menu("新建场景", () => NewAsync(false)); Menu("打开原剧情", () => NewAsync(true));
        menu.Items.Add(new MenuFlyoutSeparator()); Menu("打开项目…", () => OpenAsync()); Menu("另存为…", () => SaveAsync(true));
        Menu("恢复草稿", RestoreDraftAsync); file.Flyout = menu; ToolTipService.SetToolTip(file, "文件");
        void Menu(string label, Func<Task> action)
        {
            var item = new MenuFlyoutItem { Text = label }; item.Click += async (_, _) => await SafeAsync(action); menu.Items.Add(item);
        }
        var toolbar = new Grid { ColumnSpacing = 10 };
        toolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var editTools = Ui.Row(5, file, StudioLayout.AsyncTool("保存", () => SaveAsync(), "\uE74E"),
            StudioLayout.Tool("", () => Edit(() => _document?.Undo()), "\uE7A7"), StudioLayout.Tool("", () => Edit(() => _document?.Redo()), "\uE7A6"));
        ToolTipService.SetToolTip(editTools.Children[2], "撤销 · Ctrl+Z"); ToolTipService.SetToolTip(editTools.Children[3], "重做 · Ctrl+Y");
        toolbar.Children.Add(editTools); toolbar.Children.Add(_name); Grid.SetColumn(_name, 1);
        var finalTools = Ui.Row(7, _loop, StudioLayout.AsyncTool("JSON", ShowJsonAsync, "\uE943"),
            StudioLayout.AsyncTool("导出", ExportAsync, "\uE898", true));
        toolbar.Children.Add(finalTools); Grid.SetColumn(finalTools, 2);

        var left = new Grid { RowSpacing = 9 };
        left.RowDefinitions.Add(new() { Height = GridLength.Auto }); left.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        left.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _addActor = StudioLayout.AsyncTool("添加角色", AddActorAsync, "\uE710");
        left.Children.Add(Ui.Stack(8, StudioLayout.Section("场景对象"), _addActor));
        left.Children.Add(_objects); Grid.SetRow(_objects, 1);
        var objectFooter = Ui.Stack(8, _source, StudioLayout.Tool("移除所选角色", RemoveSelected, "\uE74D"));
        left.Children.Add(objectFooter); Grid.SetRow(objectFooter, 2);
        _objects.SelectionChanged += (_, _) =>
        {
            if (_refreshing || _document is null) return;
            SelectObject((_objects.SelectedItem as ListViewItem)?.Tag?.ToString());
        };

        _welcome = Ui.Stack(13, new TextBlock { Text = "布置你的第一个场景", FontSize = 21, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)), HorizontalAlignment = HorizontalAlignment.Center },
            new TextBlock { Text = "添加角色，在画布中摆放，再用时间轴编排动作。", FontSize = 13,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 205, 211, 221)), TextWrapping = TextWrapping.Wrap },
            Ui.Row(8, StudioLayout.AsyncTool("新建场景", () => NewAsync(false), "\uE710", true), StudioLayout.AsyncTool("打开项目", () => OpenAsync(), "\uE8E5")),
            StudioLayout.AsyncTool("粘贴场景 JSON", PasteAsync, "\uE77F"));
        _welcome.HorizontalAlignment = HorizontalAlignment.Center; _welcome.VerticalAlignment = VerticalAlignment.Center;
        var center = new Grid(); center.Children.Add(_viewport); center.Children.Add(_welcome);
        _notice.VerticalAlignment = VerticalAlignment.Top; _notice.Margin = new(8); center.Children.Add(_notice);
        var right = Ui.Scroll(Ui.Stack(6, StudioLayout.Section("属性"), _autoKey, _properties, _hint));
        Content = App.TraceConstruction("Scene StudioLayout", () => new StudioLayout(left, center, right, _timeline, toolbar));
        _properties.Children.Add(Ui.Text("在左侧或画布中选择角色、相机与画面。", 13, true));

        _timeline.SeekRequested += time => _ = SafeAsync(() => SeekAsync(time));
        _timeline.PlayPauseRequested += () => _ = SafeAsync(TogglePlaybackAsync);
        _timeline.DurationChanged += value => { if (!_refreshing && _document is not null) Edit(() => { if (_document.CanEditTimeline) _document.SetDuration(value); else _document.SetPlaybackDuration(value); }); };
        _timeline.MarkerSelected += marker => { _selectedMarker = marker.Id; SelectObject(marker.TrackId); _ = SafeAsync(() => SeekAsync(marker.Time)); };
        _timeline.MarkerMoved += (marker, time) => Edit(() => _document?.MoveKeyframe(marker.Id, time));
        _timeline.AddKeyRequested += AddKey;
        _timeline.DeleteKeyRequested += () => { if (_selectedMarker is { } key) Edit(() => _document?.DeleteKeyframe(key)); };
        _viewport.SelectionChanged += (_, id) => SelectObject(id);
        _viewport.ObjectDragged += (_, drag) => _ = SafeAsync(() => DragAsync(drag));
        _viewport.ObjectScaled += (_, scale) => _ = SafeAsync(() => ScaleAsync(scale));
        _viewport.FramePresented += (_, frame) => OnFrame(frame);
        _viewport.Failed += (_, error) => Notice(error.Message);
        App.TraceStartup("SceneStudioPage dispatcher timers");
        _propertyTimer = DispatcherQueue.CreateTimer(); _propertyTimer.Interval = TimeSpan.FromMilliseconds(220);
        _propertyTimer.IsRepeating = false; _propertyTimer.Tick += (_, _) => FlushProperty();
        _draftTimer = DispatcherQueue.CreateTimer(); _draftTimer.Interval = TimeSpan.FromMilliseconds(800);
        _draftTimer.IsRepeating = false; _draftTimer.Tick += (_, _) => SaveDraft();
        AllowDrop = true;
        DragOver += (_, args) =>
        { if (args.DataView.Contains(StandardDataFormats.StorageItems) || args.DataView.Contains(StandardDataFormats.Text)) args.AcceptedOperation = DataPackageOperation.Copy; };
        Drop += async (_, args) => await SafeAsync(async () =>
        {
            if (args.DataView.Contains(StandardDataFormats.StorageItems))
            { var files = await args.DataView.GetStorageItemsAsync(); if (files.FirstOrDefault()?.Path is { } path) await OpenPathAsync(path); }
            else if (args.DataView.Contains(StandardDataFormats.Text)) await ApplyJsonAsync(await args.DataView.GetTextAsync());
        });
        KeyDown += (_, args) =>
        {
            bool ctrl = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
            if (!ctrl) return;
            switch (args.Key)
            {
                case VirtualKey.S: args.Handled = true; _ = SafeAsync(() => SaveAsync()); break;
                case VirtualKey.O: args.Handled = true; _ = SafeAsync(() => OpenAsync()); break;
                case VirtualKey.Z: if (args.OriginalSource is not TextBox) { args.Handled = true; Edit(() => _document?.Undo()); } break;
                case VirtualKey.Y: if (args.OriginalSource is not TextBox) { args.Handled = true; Edit(() => _document?.Redo()); } break;
            }
        };
        Unloaded += (_, _) => Deactivate();
        App.TraceStartup("SceneStudioPage initialized");
    }

    private void Notice(string text, bool error = true)
    { _notice.Title = error ? "暂时无法预览" : "场景提示"; _notice.Message = text; _notice.Severity = error ? InfoBarSeverity.Warning : InfoBarSeverity.Informational; _notice.IsOpen = true; }
    private async Task SafeAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { Notice(error.Message); }
    }
    private async Task<bool> ConfirmReplaceAsync()
    {
        FlushProperty();
        return !HasUnsavedChanges || await _window.ConfirmAsync("打开另一个场景？", "当前修改会先保留为草稿。", "继续");
    }
    private async Task<SceneSource?> ChooseSourceAsync()
    {
        if (string.IsNullOrWhiteSpace(_window.Settings.ArchivePath)) { _window.ShowResourceManager(); return null; }
        var choice = await ResourcePicker.PickAsync(_window, CommandCatalog.Get("story.show").Fields.First(f => f.Key == "advId") with { Label = "素材来源" },
            new Dictionary<string, string?> { ["site"] = _window.Settings.Site });
        return choice is null ? null : new(choice.Value, choice.RelatedValues.GetValueOrDefault("site") ?? _window.Settings.Site, _window.Settings.Language);
    }
    private async Task NewAsync(bool original)
    {
        if (!await ConfirmReplaceAsync()) return;
        var source = await ChooseSourceAsync(); if (source is null) return;
        SaveDraft(); SetDocument(original ? StudioSceneDocument.FromSceneDocument(SceneTemplates.Create("story-patch", source)) : StudioSceneDocument.Create(source, 5));
        await ReloadAsync();
    }
    internal async Task SetSourceAsync(string adv, string? site = null)
    {
        if (!await ConfirmReplaceAsync()) return;
        SaveDraft(); SetDocument(StudioSceneDocument.FromSceneDocument(SceneTemplates.Create("story-patch", new SceneSource(adv, site ?? _window.Settings.Site, _window.Settings.Language))));
        await ReloadAsync();
    }
    internal async Task OpenAsync(string? path = null)
    {
        path ??= await _window.PickFileAsync(extensions: [".json"]); if (path is not null) await OpenPathAsync(path);
    }
    internal async Task OpenPathAsync(string path)
    {
        if (!await ConfirmReplaceAsync()) return;
        SaveDraft(); var document = SceneDocument.Open(path);
        SetDocument(StudioSceneDocument.FromSceneDocument(document)); _savedJson = _document!.ToJson();
        _window.Settings.RememberProject(path); _window.SaveSettings(); await ReloadAsync();
    }
    internal async Task SaveAsync(bool saveAs = false)
    {
        FlushProperty(); if (_document is null) return;
        string? path = _document.Document.FilePath;
        if (saveAs || path is null) path = await _window.PickFileAsync(true, path is null ? "scene.json" : Path.GetFileName(path), [".json"]);
        if (path is null) return;
        _document.Save(path); _savedJson = _document.ToJson();
        _window.Settings.RememberProject(path); _window.SaveSettings(); RefreshHeader(); SaveDraft();
    }
    private void SetDocument(StudioSceneDocument document)
    {
        if (_document is not null) _document.Changed -= DocumentChanged;
        _viewport.InvalidateSource(); _previewDocument = null; _document = document; _savedJson = null; _selectedMarker = null;
        _document.Changed += DocumentChanged; _welcome.Visibility = Visibility.Collapsed; _notice.IsOpen = false;
        RefreshObjects(); RefreshTimeline(); BuildProperties(); RefreshHeader();
    }
    private void DocumentChanged()
    {
        if (_disposed) return;
        RefreshHeader(); RefreshObjects(); RefreshTimeline(); _draftTimer.Stop(); _draftTimer.Start();
    }
    private void RefreshHeader()
    {
        _name.Text = (_document?.Document.FilePath is { } file ? Path.GetFileNameWithoutExtension(file) : "未命名场景") + (HasUnsavedChanges ? " · 未保存" : "");
        _source.Text = _document is null ? "" : "素材来源 · " + _document.Document.Source.Adv;
    }
    private void Edit(Action action)
    {
        try
        {
            FlushProperty(); action(); BuildProperties(); RefreshTimeline();
            if (!_suppressReload) _ = SafeAsync(ReloadAsync);
        }
        catch (Exception error) { Notice(error.Message, false); }
    }
    private async Task AddActorAsync()
    {
        if (_document is null) { await NewAsync(false); if (_document is null) return; }
        var selected = await ResourcePicker.PickAsync(_window, CommandCatalog.Get("model.show").Fields.First(f => f.Key == "modelId"),
            new Dictionary<string, string?> { ["site"] = _document.Document.Source.Site });
        if (selected is null) return;
        string id = _document.AddActor(selected.Value); SelectObject(id); await ReloadAsync();
    }
    private void RemoveSelected()
    {
        if (_document?.SelectedId is not { } id || id.StartsWith('$')) return;
        Edit(() => _document.RemoveActor(id)); SelectObject(StudioSceneDocument.SceneId);
    }
    private void RefreshObjects()
    {
        if (_document is null) return; _refreshing = true;
        _addActor.IsEnabled = _document.CanEditTimeline;
        _objects.Items.Clear();
        foreach (var actor in _document.Actors) Add(actor.Id, actor.Name, "\uE77B");
        Add(StudioSceneDocument.CameraId, "相机", "\uE722"); Add(StudioSceneDocument.SceneId, "画面与后处理", "\uE790");
        _objects.SelectedItem = _objects.Items.OfType<ListViewItem>().FirstOrDefault(i => i.Tag?.ToString() == _document.SelectedId);
        _refreshing = false;
        void Add(string id, string name, string icon) => _objects.Items.Add(new ListViewItem { Tag = id,
            Content = Ui.Row(8, new FontIcon { Glyph = icon, FontSize = 13 }, new TextBlock { Text = name, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis }), Padding = new(6, 8, 6, 8) });
    }
    private void SelectObject(string? id)
    {
        if (_document is null) return;
        FlushProperty(); _document.SelectedId = id; _viewport.SelectedTargetId = id?.StartsWith('$') == true ? null : id;
        _refreshing = true; _objects.SelectedItem = _objects.Items.OfType<ListViewItem>().FirstOrDefault(i => i.Tag?.ToString() == id); _refreshing = false;
        BuildProperties();
    }
    private void RefreshTimeline()
    {
        if (_document is null) return; _refreshing = true;
        _timeline.CanEditKeys = _document.CanEditTimeline;
        _timeline.Duration = _document.Duration; _timeline.Position = _document.Time;
        var tracks = _document.Actors.Select(a => (a.Id, a.Name)).Concat(new[] { (StudioSceneDocument.CameraId, "相机"), (StudioSceneDocument.SceneId, "后处理") });
        var keys = _document.Keyframes;
        _timeline.SetTracks(tracks.SelectMany(pair =>
        {
            var groups = keys.Where(k => k.ObjectId == pair.Item1).GroupBy(k => k.Track).ToArray();
            if (groups.Length == 0) return new[] { new StudioTimelineTrack(pair.Item1, pair.Item2, []) };
            return groups.Select(group => new StudioTimelineTrack(pair.Item1 + "/" + group.Key,
                pair.Item2 + " · " + TrackLabel(group.Key, group.First().Label),
                group.Select(k => new StudioTimelineMarker(k.Id, pair.Item1, k.Time, k.Label, k.IsEvent ? "motion" : "key")).ToArray()));
        }));
        _refreshing = false;
    }
    private static string TrackLabel(string track, string fallback) => track switch
    {
        "actor.position" => "位置", "actor.height" => "大小", "actor.rotation" or "actor.rotationZ" => "旋转", "actor.scale" => "比例",
        "camera.framing.x" => "横移", "camera.framing.y" => "纵移", "camera.framing.zoom" => "缩放", "camera.framing.roll" => "旋转",
        "camera" => "原始镜头", "scene" => "原始画面", _ => fallback
    };

    private void BuildProperties()
    {
        _properties.Children.Clear(); _numbers.Clear(); if (_document is null) return;
        _autoKey.IsEnabled = _document.CanEditTimeline;
        if (!_document.CanEditTimeline)
        {
            _properties.Children.Add(Ui.Text("原始剧情", 16));
            _properties.Children.Add(Ui.Text("原始指令的时序由剧情决定，视觉轨道暂为只读。可以实时播放、调整预览范围，或打开 JSON 继续修改。", 12, true));
            _properties.Children.Add(Ui.Text("下方“时长”是这次预览与导出的范围，不代表原剧情的总时长。", 12, true));
            _properties.Children.Add(StudioLayout.AsyncTool("打开 JSON", ShowJsonAsync, "\uE943"));
            _properties.Children.Add(StudioLayout.AsyncTool("新建可编排场景", () => NewAsync(false), "\uE710"));
            return;
        }
        var sample = _document.Sample(_document.Time); string? id = _document.SelectedId;
        _refreshing = true;
        if (id == StudioSceneDocument.CameraId)
        {
            _properties.Children.Add(StudioLayout.Section("相机构图"));
            AddNumber("cx", "横移", sample.Camera.X * 100, -1000, 1000, .5);
            AddNumber("cy", "纵移", sample.Camera.Y * 100, -1000, 1000, .5);
            AddNumber("zoom", "缩放（%）", sample.Camera.Zoom * 100, 1, 2000, 1);
            AddNumber("cr", "旋转（°）", sample.Camera.Roll, -3600, 3600, 1);
        }
        else if (id == StudioSceneDocument.SceneId)
        {
            _properties.Children.Add(StudioLayout.Section("背景"));
            var transparent = new CheckBox { Content = "透明背景", IsChecked = Number(sample.Background.ElementAtOrDefault(3), 1) < 1 };
            transparent.Click += (_, _) => Edit(() => _document.SetBackground(transparent.IsChecked == true ? new JsonArray(0, 0, 0, 0) : new JsonArray(.08, .09, .12, 1), _document.Time, _autoKey.IsChecked == true));
            _properties.Children.Add(transparent);
            _properties.Children.Add(StudioLayout.AsyncTool("选择背景图片", BackgroundImageAsync, "\uEB9F"));
            _properties.Children.Add(StudioLayout.Section("调色"));
            AddNumber("exposure", "曝光", sample.Grade.Exposure, -10, 10, .1);
            AddNumber("contrast", "对比度", sample.Grade.Contrast, 0, 4, .05);
            AddNumber("saturation", "饱和度", sample.Grade.Saturation, 0, 4, .05);
            AddNumber("vignette", "暗角", sample.Grade.Vignette, 0, 1, .05);
        }
        else if (sample.Actors.FirstOrDefault(a => a.Id == id) is { } actor)
        {
            _properties.Children.Add(StudioLayout.Section(actor.Name));
            var visible = new CheckBox { Content = "显示角色", IsChecked = actor.Visible };
            visible.Click += (_, _) => Edit(() => _document.SetActorVisibility(actor.Id, visible.IsChecked == true, _document.Time, _autoKey.IsChecked == true));
            _properties.Children.Add(visible);
            _properties.Children.Add(StudioLayout.AsyncTool("选择动作", () => PickAnimationAsync(false), "\uE768"));
            _properties.Children.Add(StudioLayout.AsyncTool("选择表情", () => PickAnimationAsync(true), "\uE76E"));
            _properties.Children.Add(Ui.Text(string.Join(" · ", new[] { actor.Motion, actor.Expression }.Where(v => !string.IsNullOrEmpty(v))), 11, true));
            bool screen = actor.Space == "screen";
            _properties.Children.Add(StudioLayout.Section("位置与大小"));
            AddNumber("x", screen ? "水平位置（%）" : "世界 X", actor.Transform.X * (screen ? 100 : 1), -1000, 1000, screen ? .5 : .01);
            AddNumber("y", screen ? "垂直位置（%）" : "世界 Y", actor.Transform.Y * (screen ? 100 : 1), -1000, 1000, screen ? .5 : .01);
            if (actor.Space == "world") AddNumber("z", "世界 Z", actor.Transform.Z, -1000, 1000, .01);
            AddNumber("height", "高度（%）", actor.Transform.Height * 100, .1, 2000, 1);
            AddNumber("roll", "旋转（°）", actor.Transform.Roll, -3600, 3600, 1);
            AddNumber("opacity", "不透明度（%）", actor.Transform.Opacity * 100, 0, 100, 1);
            if (!screen) _properties.Children.Add(Ui.Text("这个对象使用原场景世界坐标，可在这里调整。", 11, true));
        }
        else _properties.Children.Add(Ui.Text("选择一个对象开始编辑。", 13, true));
        _refreshing = false;
    }
    private void AddNumber(string key, string label, double value, double minimum, double maximum, double step)
    {
        var box = new NumberBox { Header = label, Value = value, Minimum = minimum, Maximum = maximum, SmallChange = step,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        _numbers[key] = box; _properties.Children.Add(box);
        box.ValueChanged += (_, _) => { if (!_refreshing && double.IsFinite(box.Value)) QueueProperty(); };
    }
    private void QueueProperty()
    {
        if (_document is null) return;
        var sample = _document.Sample(_document.Time); string? id = _document.SelectedId; double time = _document.Time; bool key = _autoKey.IsChecked == true;
        double N(string field, double fallback) => _numbers.TryGetValue(field, out var box) && double.IsFinite(box.Value) ? box.Value : fallback;
        IReadOnlyList<JsonObject> preview;
        if (id == StudioSceneDocument.CameraId)
        {
            var camera = new StudioCamera(N("cx", 0) / 100, N("cy", 0) / 100, N("zoom", 100) / 100, N("cr", 0));
            _pendingProperty = () => _document.SetCamera(camera, time, key);
            preview = [CameraRow(camera)];
        }
        else if (id == StudioSceneDocument.SceneId)
        {
            var grade = new StudioGrade(N("exposure", 0), N("contrast", 1), N("saturation", 1), N("vignette", 0));
            _pendingProperty = () => _document.SetGrade(grade, time, key);
            preview = [GradeRow(grade)];
        }
        else if (sample.Actors.FirstOrDefault(a => a.Id == id) is { } actor)
        {
            double divisor = actor.Space == "screen" ? 100 : 1;
            var transform = actor.Transform with { X = N("x", actor.Transform.X * divisor) / divisor, Y = N("y", actor.Transform.Y * divisor) / divisor,
                Z = N("z", actor.Transform.Z), Height = N("height", 95) / 100, Roll = N("roll", 0), Opacity = N("opacity", 100) / 100 };
            _pendingProperty = () =>
            {
                _document.BeginEdit("调整角色");
                _document.SetActorTransform(actor.Id, transform, time, key);
                _document.SetActorOpacity(actor.Id, transform.Opacity, time, key); _document.CommitEdit();
            };
            preview = [ActorRow(actor, transform)];
        }
        else return;
        _viewport.Pause(); _timeline.IsPlaying = false;
        if (_viewport.HasCurrentSource) _pendingLiveEdit = SafeAsync(() => _viewport.ApplySceneEditsAsync(preview));
        _propertyTimer.Stop(); _propertyTimer.Start();
    }
    private void FlushProperty()
    {
        _propertyTimer.Stop(); var pending = _pendingProperty; _pendingProperty = null;
        if (pending is null) return;
        try
        {
            pending();
            if (!_suppressReload)
            {
                var liveEdit = _pendingLiveEdit;
                _pendingPreviewCommit = SafeAsync(async () => { await liveEdit; await CommitPreviewAsync(); });
            }
        }
        catch (Exception error) { _document?.CancelEdit(); Notice(error.Message, false); }
    }
    private void RefreshValues()
    {
        if (_document is null || _pendingProperty is not null || _dragStart is not null) return;
        if (FocusManager.GetFocusedElement(XamlRoot) is DependencyObject focused && IsInside(focused, _properties)) return;
        var sample = _document.Sample(_document.Time); _refreshing = true;
        void Set(string key, double value) { if (_numbers.TryGetValue(key, out var box)) box.Value = value; }
        if (_document.SelectedId == StudioSceneDocument.CameraId)
        { Set("cx", sample.Camera.X * 100); Set("cy", sample.Camera.Y * 100); Set("zoom", sample.Camera.Zoom * 100); Set("cr", sample.Camera.Roll); }
        else if (_document.SelectedId == StudioSceneDocument.SceneId)
        { Set("exposure", sample.Grade.Exposure); Set("contrast", sample.Grade.Contrast); Set("saturation", sample.Grade.Saturation); Set("vignette", sample.Grade.Vignette); }
        else if (sample.Actors.FirstOrDefault(a => a.Id == _document.SelectedId) is { } actor)
        {
            double factor = actor.Space == "screen" ? 100 : 1; Set("x", actor.Transform.X * factor); Set("y", actor.Transform.Y * factor);
            Set("z", actor.Transform.Z);
            Set("height", actor.Transform.Height * 100); Set("roll", actor.Transform.Roll); Set("opacity", actor.Transform.Opacity * 100);
        }
        _refreshing = false;
    }
    private static bool IsInside(DependencyObject item, DependencyObject parent)
    {
        for (var current = item; current is not null; current = VisualTreeHelper.GetParent(current)) if (ReferenceEquals(current, parent)) return true;
        return false;
    }
    private async Task PickAnimationAsync(bool expression)
    {
        if (_document?.Sample(_document.Time).Actors.FirstOrDefault(a => a.Id == _document.SelectedId) is not { } actor) return;
        _viewport.Pause();
        string resource = expression ? "expression" : "motion";
        var selected = await ResourcePicker.PickAsync(_window, new(resource, expression ? "表情" : "动作", CommandFieldKind.Text, "") { LookupCommandId = resource + ".list" },
            new Dictionary<string, string?> { ["modelId"] = actor.Model, ["site"] = _document.Document.Source.Site });
        if (selected is null) return;
        if (expression) _document.SetExpression(actor.Id, selected.Value, _document.Time); else _document.SetMotion(actor.Id, selected.Value, _document.Time);
        BuildProperties(); await ReloadAsync();
    }
    private async Task BackgroundImageAsync()
    {
        if (_document is null || await _window.PickFileAsync(extensions: [".png"]) is not { } path) return;
        _document.SetBackgroundImage(path); await ReloadAsync();
    }
    private async Task DragAsync(PreviewDrag drag)
    {
        if (_document is null || !_viewport.HasCurrentSource) return;
        var sample = _document.Sample(_document.Time); var actor = sample.Actors.FirstOrDefault(a => a.Id == drag.TargetId);
        if (actor is null || actor.Space != "screen") return;
        _viewport.Pause(); _timeline.IsPlaying = false;
        if (_dragStart is null || _dragId != actor.Id) { FlushProperty(); _dragStart = actor.Transform; _dragId = actor.Id; SelectObject(actor.Id); }
        var output = _document.Document.Output;
        double renderWidth = _viewport.LastFrame?.Pixels.Width ?? output.Width, renderHeight = _viewport.LastFrame?.Pixels.Height ?? output.Height;
        double activeHeight = output.Letterbox == "none" ? renderHeight : Math.Min(renderHeight, Math.Floor(renderWidth / (renderWidth > renderHeight ? 2.1666667 : 1.7777778) + .5));
        double radians = sample.Camera.Roll * Math.PI / 180, aspect = renderWidth / activeHeight;
        double dx = drag.DeltaX * aspect, dy = -drag.DeltaY * renderHeight / activeHeight;
        double x = (dx * Math.Cos(radians) + dy * Math.Sin(radians)) / (sample.Camera.Zoom * aspect);
        double y = -(-dx * Math.Sin(radians) + dy * Math.Cos(radians)) / sample.Camera.Zoom;
        var transform = _dragStart with { X = _dragStart.X + x, Y = _dragStart.Y + y };
        await _viewport.ApplySceneEditsAsync([ActorRow(actor, transform)]);
        if (drag.IsCompleted)
        {
            _dragStart = null; _dragId = null;
            _document.SetActorTransform(actor.Id, transform.X, transform.Y, transform.Height, transform.Roll, _document.Time, _autoKey.IsChecked == true);
            RefreshValues(); await CommitPreviewAsync();
        }
    }
    private async Task ScaleAsync(PreviewScale scale)
    {
        if (_document?.Sample(_document.Time).Actors.FirstOrDefault(a => a.Id == scale.TargetId) is not { Space: "screen" } actor) return;
        _viewport.Pause(); SelectObject(actor.Id);
        var transform = actor.Transform with { Height = Math.Clamp(actor.Transform.Height * scale.Factor, .01, 20) };
        await _viewport.ApplySceneEditsAsync([ActorRow(actor, transform)]);
        _document.SetActorTransform(actor.Id, transform.X, transform.Y, transform.Height, transform.Roll, _document.Time, _autoKey.IsChecked == true);
        RefreshValues(); await CommitPreviewAsync();
    }
    private void AddKey()
    {
        if (_document is null) return; var sample = _document.Sample(_document.Time);
        if (_document.SelectedId == StudioSceneDocument.CameraId) Edit(() => _document.SetCamera(sample.Camera, _document.Time, true));
        else if (_document.SelectedId == StudioSceneDocument.SceneId) Edit(() => _document.SetGrade(sample.Grade, _document.Time, true));
        else if (sample.Actors.FirstOrDefault(a => a.Id == _document.SelectedId) is { } actor)
            Edit(() => _document.SetActorTransform(actor.Id, actor.Transform, _document.Time, true));
    }
    private static JsonObject ActorRow(StudioActor actor, StudioTransform transform) => new()
    {
        ["cmd"] = "Director.Actor", ["TargetName"] = actor.Id, ["Space"] = actor.Space, ["Duration"] = 0,
        ["Values"] = new JsonObject { ["position"] = actor.Space == "screen" ? new JsonArray(transform.X, transform.Y) : new JsonArray(transform.X, transform.Y, transform.Z),
            ["height"] = transform.Height, ["rotationZ"] = transform.Roll, ["opacity"] = transform.Opacity }
    };
    private static JsonObject CameraRow(StudioCamera camera) => new() { ["cmd"] = "Director.Camera", ["Duration"] = 0,
        ["Values"] = new JsonObject { ["framing"] = new JsonObject { ["x"] = camera.X, ["y"] = camera.Y, ["zoom"] = camera.Zoom, ["roll"] = camera.Roll } } };
    private static JsonObject GradeRow(StudioGrade grade) => new() { ["cmd"] = "Director.Scene", ["Duration"] = 0,
        ["Values"] = new JsonObject { ["grade"] = new JsonObject { ["exposure"] = grade.Exposure, ["contrast"] = grade.Contrast, ["saturation"] = grade.Saturation, ["vignette"] = grade.Vignette } } };

    private async Task CommitPreviewAsync()
    {
        if (_document is null || _disposed || _suppressReload) return;
        if (!_viewport.HasCurrentSource || !ReferenceEquals(_previewDocument, _document)) { await ReloadAsync(); return; }
        long version = ++_loadVersion;
        await _loadGate.WaitAsync();
        try
        {
            if (version != _loadVersion || _disposed) return;
            var snapshot = _document.ToSceneDocument().CreateSnapshot(Path.Combine(DesktopSettings.DataDirectory, "preview-inputs"));
            try
            {
                var pixels = _viewport.LastFrame!.Pixels;
                await _viewport.CommitSceneProjectAsync(new(_window.Settings.ArchivePath, snapshot.FilePath,
                    NativeDependencies.CoreLibrary(Empty(_window.Settings.NativeLibraryPath), Empty(_window.Settings.CubismSdkPath)),
                    NativeDependencies.MediaTool("ffmpeg", Empty(_window.Settings.FFmpegPath)), pixels.Width, pixels.Height, _document.Duration));
                _previewInput?.Dispose(); _previewInput = snapshot;
            }
            catch { if (!ReferenceEquals(_previewInput, snapshot)) snapshot.Dispose(); throw; }
        }
        finally { _loadGate.Release(); }
    }

    private async Task ReloadAsync()
    {
        if (_document is null || _disposed || _suppressReload) return;
        long version = ++_loadVersion;
        await _loadGate.WaitAsync();
        try
        {
            if (version != _loadVersion || _disposed || _document is null) return;
            if (!Directory.Exists(_window.Settings.ArchivePath)) { Notice("请选择素材库，之后即可在这里播放和编辑。", false); return; }
            var editor = _document;
            double restoreTime = editor.Time;
            var document = editor.ToSceneDocument();
            var snapshot = document.CreateSnapshot(Path.Combine(DesktopSettings.DataDirectory, "preview-inputs"));
            var output = document.Output; int width = Math.Min(960, output.Width); int height = Math.Max(16, (int)Math.Round(width * output.Height / (double)output.Width));
            if (height > 720) { width = Math.Max(16, (int)Math.Round(width * 720d / height)); height = 720; }
            string core = NativeDependencies.CoreLibrary(Empty(_window.Settings.NativeLibraryPath), Empty(_window.Settings.CubismSdkPath));
            try
            {
                _previewDocument = null;
                await _viewport.LoadSceneAsync(new(_window.Settings.ArchivePath, snapshot.FilePath, core,
                    NativeDependencies.MediaTool("ffmpeg", Empty(_window.Settings.FFmpegPath)), width, height, editor.Duration));
                _previewInput?.Dispose(); _previewInput = snapshot;
                if (version != _loadVersion || !ReferenceEquals(editor, _document) || _disposed) return;
                _previewDocument = editor;
                if (restoreTime > 0) await _viewport.SeekAsync(restoreTime);
                _viewport.SelectedTargetId = _document.SelectedId?.StartsWith('$') == true ? null : _document.SelectedId;
                _notice.IsOpen = false;
            }
            catch { if (!ReferenceEquals(_previewInput, snapshot)) snapshot.Dispose(); throw; }
        }
        finally { _loadGate.Release(); }
    }
    private static string? Empty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private async Task SeekAsync(double time)
    {
        FlushProperty(); await _pendingPreviewCommit; if (_document is null) return;
        _viewport.Pause(); _document.Time = Math.Clamp(time, 0, _document.Duration); _timeline.Position = _document.Time; _timeline.IsPlaying = false; RefreshValues();
        if (_viewport.HasCurrentSource) await _viewport.SeekAsync(_document.Time);
    }
    private async Task TogglePlaybackAsync()
    {
        FlushProperty(); await _pendingPreviewCommit; if (_document is null) return;
        if (_viewport.IsPlaying) { _viewport.Pause(); _timeline.IsPlaying = false; return; }
        if (!_viewport.HasCurrentSource) await ReloadAsync();
        if (!_viewport.HasCurrentSource) return;
        if (_document.Time >= _document.Duration - 1d / 30) await SeekAsync(0);
        _viewport.Play(); _timeline.IsPlaying = true;
    }
    private void OnFrame(PreviewFrame frame)
    {
        if (_document is null || _disposed || !_viewport.HasCurrentSource || !ReferenceEquals(_previewDocument, _document)) return;
        _document.Time = Math.Min(frame.Time, _document.Duration); _timeline.Position = _document.Time; _timeline.IsPlaying = frame.IsPlaying;
        RefreshValues();
        if (frame.IsPlaying && frame.Time >= _document.Duration - 1d / 30 && !_looping)
        {
            _viewport.Pause(); _timeline.IsPlaying = false;
            if (_loop.IsChecked == true)
            {
                _looping = true; _ = SafeAsync(async () => { try { await _viewport.SeekAsync(0); if (!_disposed && IsLoaded) { _viewport.Play(); _timeline.IsPlaying = true; } } finally { _looping = false; } });
            }
        }
    }
    private async Task ExportAsync()
    {
        FlushProperty(); if (_document is null) return; _viewport.Pause();
        await StudioExportDialog.ShowSceneAsync(_window, _document, _document.Time);
    }
    private async Task PasteAsync()
    {
        var clipboard = Clipboard.GetContent();
        if (!clipboard.Contains(StandardDataFormats.Text)) { await ShowJsonAsync(); return; }
        await ApplyJsonAsync(await clipboard.GetTextAsync());
    }
    private async Task ApplyJsonAsync(string text)
    {
        FlushProperty(); await _pendingPreviewCommit;
        var parsed = SceneDocument.Parse(text, baseDirectory: _document?.Document.BaseDirectory);
        if (_document is null) SetDocument(StudioSceneDocument.FromSceneDocument(parsed));
        else { SaveDraft(); _document.ReplaceJson(text); RefreshObjects(); RefreshTimeline(); BuildProperties(); }
        await ReloadAsync();
    }
    private async Task ShowJsonAsync()
    {
        _viewport.Pause(); FlushProperty();
        var input = new TextBox { Text = _document?.ToJson() ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas"), FontSize = 12, MinHeight = 240, Height = 380, PlaceholderText = "粘贴 agent 生成的场景 JSON" };
        ScrollViewer.SetHorizontalScrollBarVisibility(input, ScrollBarVisibility.Auto); ScrollViewer.SetVerticalScrollBarVisibility(input, ScrollBarVisibility.Auto);
        var message = new InfoBar { IsClosable = false, Severity = InfoBarSeverity.Error };
        var tools = Ui.Row(7, StudioLayout.AsyncTool("粘贴", async () =>
        { var data = Clipboard.GetContent(); if (data.Contains(StandardDataFormats.Text)) input.Text = await data.GetTextAsync(); }, "\uE77F"),
            StudioLayout.Tool("复制", () => MainWindow.Copy(input.Text), "\uE8C8"), StudioLayout.Tool("给 agent 的说明", () => MainWindow.Copy(AgentInstructions())));
        var dialog = new ContentDialog { XamlRoot = _window.RootXaml, Title = "场景 JSON", PrimaryButtonText = "应用到场景", CloseButtonText = "取消",
            Content = Ui.Stack(12, tools, input, message), DefaultButton = ContentDialogButton.Primary };
        StudioSceneDocument? replacement = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try { replacement = StudioSceneDocument.FromSceneDocument(SceneDocument.Parse(input.Text, _document?.Document.FilePath, _document?.Document.BaseDirectory)); }
            catch (Exception error) when (error is FormatException or ArgumentException or InvalidOperationException)
            { args.Cancel = true; message.Title = "JSON 需要调整"; message.Message = error.Message; message.IsOpen = true; }
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && replacement is not null)
        { SaveDraft(); if (_document is null) SetDocument(replacement); else { _document.ReplaceJson(replacement.ToJson()); RefreshObjects(); RefreshTimeline(); BuildProperties(); } await ReloadAsync(); }
    }
    private string AgentInstructions() => "请为 OurNotes Director 生成场景 JSON，只返回一个 JSON 对象。格式为 ournotes-director/project，version 为 1。source 使用实际素材来源；output 使用 cineform、cineformQuality film1。角色用 Character 创建，再用 Director.Actor（Space=screen）摆放；position=[x,y] 从左上归一化，height 为画布比例。使用原生 Motion/Expression、Director.Camera、Director.Scene 和 Delay 编排时间，不要编造模型或动作。保留未要求更改的字段。以下是当前项目：\n" + (_document?.ToJson() ?? "请先向我确认实际素材编号。");
    private static string DraftPath => Path.Combine(DesktopSettings.DataDirectory, "studio-scene-draft.json");
    internal void SaveDraft()
    {
        if (_document is null) return;
        try { DesktopSettings.AtomicWrite(DraftPath, new JsonObject { ["project"] = _document.Document.Root, ["filePath"] = _document.Document.FilePath,
            ["baseDirectory"] = _document.Document.BaseDirectory, ["time"] = _document.Time, ["playbackDuration"] = _document.Duration }.ToJsonString(DesktopSettings.JsonOptions)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { _window.SetStatus("草稿暂时无法保存：" + error.Message); }
    }
    private async Task RestoreDraftAsync()
    {
        if (!File.Exists(DraftPath)) { Notice("还没有保存的草稿。", false); return; }
        if (!await ConfirmReplaceAsync()) return;
        var draft = JsonNode.Parse(await File.ReadAllTextAsync(DraftPath))!;
        SetDocument(StudioSceneDocument.FromSceneDocument(SceneDocument.Parse(draft["project"]!.ToJsonString(), draft["filePath"]?.ToString(), draft["baseDirectory"]?.ToString())));
        if (!_document!.CanEditTimeline) _document.SetPlaybackDuration(Number(draft["playbackDuration"], _document.Duration));
        _document.Time = Number(draft["time"], 0); await ReloadAsync();
    }
    internal void Deactivate() { _viewport.Pause(); _timeline.IsPlaying = false; FlushProperty(); SaveDraft(); }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true; _propertyTimer.Stop(); _draftTimer.Stop();
        await _viewport.DisposeAsync(); _previewInput?.Dispose();
    }
    private static double Number(JsonNode? value, double fallback) => value is JsonValue node && node.TryGetValue<double>(out var number) ? number : fallback;

    internal async Task RunSmokeTestAsync(Action<bool, string> check)
    {
        _suppressReload = true;
        try
        {
            var model = StudioSceneDocument.Create(new("42", "intl", "zh-Hans"), 4);
            string actor = model.AddActor("fixture-model", "测试角色");
            SetDocument(model); _document!.SelectedId = actor;
            model.SetActorTransform(actor, .3, .5, .8, 0, 0, true);
            model.SetActorTransform(actor, .7, .5, .8, 0, 2, true);
            check(Math.Abs(model.Sample(1).Actors.Single().Transform.X - .5) < .02, "场景时间轴真实插值角色位置");
            SelectObject(actor); _document.Time = 1; RefreshValues(); RefreshTimeline(); UpdateLayout();
            check(_viewport.IsLoaded && _viewport.ActualWidth > 0 && _timeline.IsLoaded && _timeline.ActualWidth > 0, "实时画布和时间轴在原生工作台中布局");
            check(_numbers.ContainsKey("x") && _objects.Items.Count == 3, "对象列表和选中角色属性真实可用");
            MainWindow.Copy("```json\n" + model.ToJson() + "\n```");
            await PasteAsync();
            check(_document.Document.Kind == SceneDocumentKind.UnifiedProject && !_document.ToJson().Contains("```", StringComparison.Ordinal), "剪贴板 JSON 进入同一场景编辑模型");
            model = _document; string before = model.ToJson(); model.SetActorTransform(actor, .8, .5, .8, 0, 2, true); model.Undo();
            check(model.ToJson() == before, "角色编辑可撤销并恢复完整项目");
        }
        finally { _suppressReload = false; }
        if (Environment.GetEnvironmentVariable("OURNOTES_STUDIO_FIXTURE") is not { Length: > 0 } fixture) return;
        _window.Settings.ArchivePath = Path.GetFullPath(fixture);
        SetDocument(StudioSceneDocument.Open(Path.Combine(fixture, "studio-scene.json")));
        _document!.Time = .5;
        await ReloadAsync();
        var first = await _viewport.WaitForFrameAsync();
        check(_viewport.HasCurrentSource && first.Frame == 15 && Math.Abs(_document.Time - .5) < .001,
            "场景实际原生渲染并在载入后保留播放头位置");
        check(first.Pixels.Rgba.Where((_, index) => index % 4 == 3).Any(alpha => alpha > 0)
            && first.Pixels.Rgba.Where((_, index) => index % 4 == 3).Any(alpha => alpha < 255),
            "场景原生画布包含真实颜色和透明度");
        SelectObject(StudioSceneDocument.SceneId); UpdateLayout();
        _numbers["exposure"].Value = .7; FlushProperty(); await _pendingPreviewCommit;
        var changed = await _viewport.WaitForFrameAsync(first.Sequence);
        check(!changed.Pixels.Rgba.SequenceEqual(first.Pixels.Rgba) && changed.SessionId == first.SessionId,
            "修改后处理属性直接刷新现有原生场景");
        await SeekAsync(0); await SeekAsync(.5);
        check(_viewport.LastFrame!.Pixels.Rgba.SequenceEqual(changed.Pixels.Rgba), "回拖播放头后恢复已经提交的画面与后处理");
        long sequence = _viewport.LastFrame.Sequence;
        await TogglePlaybackAsync();
        var playing = await _viewport.WaitForFrameAsync(sequence);
        _viewport.Pause(); _timeline.IsPlaying = false;
        check(playing.IsPlaying && playing.Frame > 15, "场景在原生窗口中连续播放新帧");
        await SeekAsync(.5); _savedJson = _document.ToJson(); RefreshHeader();
    }
}
