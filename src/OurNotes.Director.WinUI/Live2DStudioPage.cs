using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace OurNotes.Director.WinUI;

/// <summary>A single-model editor. Its document compiles to the same native project used by export.</summary>
internal sealed partial class Live2DStudioPage : UserControl, IAsyncDisposable
{
    readonly MainWindow window;
    readonly NativeViewport viewport = new();
    readonly TimelineScrubber scrubber;
    readonly TextBlock title = Ui.Text("未打开模型", 14), modelName = Ui.Text("选择一个 Live2D 模型", 14);
    readonly TextBlock modelDetail = Ui.Text("", 11, true), frameLabel = Ui.Text("00:00:00", 12), rangeLabel = Ui.Text("完整动画", 11, true);
    readonly InfoBar notice = new() { IsClosable = true };
    readonly Slider playhead = new() { Minimum = 0, Maximum = 3, StepFrequency = 1d / 30 };
    readonly Button playButton, previousButton, nextButton, pngButton, movieButton, sequenceButton, chooseButton;
    readonly ContentControl inspectorBody = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    readonly Dictionary<string, ToggleButton> inspectorTabs = new(StringComparer.Ordinal);
    readonly Dictionary<string, FrameworkElement> inspectorViews = new(StringComparer.Ordinal);
    readonly StackPanel welcome;
    readonly CancellationTokenSource lifetime = new();
    readonly SemaphoreSlim previewGate = new(1, 1);
    readonly DispatcherQueueTimer previewTimer, draftTimer;
    Live2DDocument? document;
    Live2DDocument? presentedDocument;
    SceneSnapshot? previewInput;
    string? loadedSourceKey;
    string activeTab = "controls";
    long loadVersion, editVersion, previewSessionId, pauseVersion;
    bool refreshing, disposed, active, previewDirty, replayRequired, transformPending, playbackRequested, restartingLoop, readingParameters;
    DateTime lastParameterRead;
    Live2DTransform? dragOrigin;
    Live2DDocument? dragDocument;

    internal bool HasUnsavedChanges => document?.HasUnsavedChanges == true;
    static string DraftPath => Path.Combine(DesktopSettings.DataDirectory, "studio-live2d-draft.json");
    TimelineRange PlaybackRange => document?.Range ?? TimelineRange.Full(document?.Duration ?? 3, 30);

    internal Live2DStudioPage(MainWindow window)
    {
        this.window = window;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        scrubber = new((time, token) => SeekAsync(time, token), error => Notice(error.Message));
        previewTimer = DispatcherQueue.CreateTimer(); previewTimer.IsRepeating = false; previewTimer.Interval = TimeSpan.FromMilliseconds(45);
        previewTimer.Tick += (_, _) => _ = SafeAsync(ApplyPreviewEditsAsync);
        draftTimer = DispatcherQueue.CreateTimer(); draftTimer.IsRepeating = false; draftTimer.Interval = TimeSpan.FromMilliseconds(800);
        draftTimer.Tick += (_, _) => SaveDraft();

        chooseButton = StudioLayout.AsyncTool("选择模型…", () => SafeAsync(NewAsync), "\uE77B");
        playButton = IconTool("播放 / 暂停（空格）", "\uE768", () => SafeAsync(TogglePlayAsync));
        previousButton = IconTool("上一帧（左方向键）", "\uE892", () => SafeAsync(() => StepAsync(-1)));
        nextButton = IconTool("下一帧（右方向键）", "\uE893", () => SafeAsync(() => StepAsync(1)));
        pngButton = StudioLayout.AsyncTool("PNG", () => SafeAsync(() => ExportAsync("frame")), "\uEB9F");
        movieButton = StudioLayout.AsyncTool("动画…", () => SafeAsync(() => ExportAsync("clip")), "\uE714");
        sequenceButton = StudioLayout.AsyncTool("帧序列…", () => SafeAsync(() => ExportAsync("sequence")), "\uE8B9");
        ToolTipService.SetToolTip(pngButton, "保存当前姿态为透明 PNG");
        ToolTipService.SetToolTip(movieButton, "导出透明 MOV 动画");
        ToolTipService.SetToolTip(sequenceButton, "导出透明 PNG 帧序列");
        AutomationProperties.SetName(playhead, "Live2D 播放头");

        var transport = new Grid { ColumnSpacing = 8 };
        transport.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        transport.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        transport.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        transport.Children.Add(Ui.Row(2, previousButton, playButton, nextButton,
            IconTool("适应画布（F）", "\uE9A6", () => { viewport.FitCanvas(); return Task.CompletedTask; })));
        frameLabel.VerticalAlignment = VerticalAlignment.Center; frameLabel.HorizontalAlignment = HorizontalAlignment.Center;
        transport.Children.Add(frameLabel); Grid.SetColumn(frameLabel, 1);
        var outputTools = Ui.Row(2, pngButton, movieButton, sequenceButton);
        transport.Children.Add(outputTools); Grid.SetColumn(outputTools, 2);
        var rangeTools = Ui.Row(4,
            StudioLayout.Tool("入点", () => MarkRange(true)), StudioLayout.Tool("出点", () => MarkRange(false)),
            StudioLayout.Tool("清除区间", ClearRange), rangeLabel);
        rangeLabel.VerticalAlignment = VerticalAlignment.Center;
        var transportPanel = Ui.Stack(2, transport, playhead, rangeTools);
        transportPanel.Margin = new(8, 4, 8, 5);
        welcome = Ui.Stack(12, Ui.Text("Live2D 工作台", 22),
            Ui.Text("选择模型，预览动作与表情，调整参数并输出透明素材。", 13, true),
            Ui.Row(6, StudioLayout.AsyncTool("选择模型", () => SafeAsync(NewAsync), "\uE77B", true),
                StudioLayout.AsyncTool("打开项目", () => SafeAsync(() => OpenAsync()), "\uE8E5")));
        welcome.HorizontalAlignment = HorizontalAlignment.Center; welcome.VerticalAlignment = VerticalAlignment.Center;
        var stage = new Grid(); stage.Children.Add(viewport); stage.Children.Add(welcome);
        notice.VerticalAlignment = VerticalAlignment.Top; notice.Margin = new(8); stage.Children.Add(notice);
        var monitor = new Grid(); monitor.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        monitor.RowDefinitions.Add(new() { Height = GridLength.Auto }); monitor.Children.Add(stage);
        monitor.Children.Add(transportPanel); Grid.SetRow(transportPanel, 1);

        var modelHeader = new Grid { ColumnSpacing = 5 };
        modelHeader.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        modelHeader.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        modelHeader.Children.Add(Ui.Stack(2, modelName, modelDetail)); modelHeader.Children.Add(chooseButton); Grid.SetColumn(chooseButton, 1);
        var tabs = new Grid { ColumnSpacing = 1 };
        foreach (var (key, label) in new[] { ("controls", "控制"), ("motion", "动作"), ("expression", "表情"), ("parameters", "参数") })
        {
            int column = tabs.ColumnDefinitions.Count; tabs.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            var tab = new ToggleButton { Content = label, Padding = new(6, 5, 6, 5), HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(tab, "Live2D " + label); tab.Click += (_, _) => SelectInspector(key);
            inspectorTabs.Add(key, tab); tabs.Children.Add(tab); Grid.SetColumn(tab, column);
        }
        inspectorViews.Add("controls", CreateControlInspector());
        inspectorViews.Add("motion", CreateAnimationInspector(false));
        inspectorViews.Add("expression", CreateAnimationInspector(true));
        inspectorViews.Add("parameters", CreateParameterInspector());
        var inspector = new Grid { RowSpacing = 8 };
        inspector.RowDefinitions.Add(new() { Height = GridLength.Auto }); inspector.RowDefinitions.Add(new() { Height = GridLength.Auto });
        inspector.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); inspector.Children.Add(modelHeader);
        inspector.Children.Add(tabs); Grid.SetRow(tabs, 1); inspector.Children.Add(inspectorBody); Grid.SetRow(inspectorBody, 2);

        var toolbar = new Grid { ColumnSpacing = 8 };
        toolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); toolbar.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        toolbar.Children.Add(Ui.Row(2, StudioMenus.Button("文件", FileMenu), StudioMenus.Button("编辑", EditMenu), StudioMenus.Button("视图", ViewMenu)));
        title.VerticalAlignment = VerticalAlignment.Center; toolbar.Children.Add(title); Grid.SetColumn(title, 1);
        var save = StudioLayout.AsyncTool("保存", () => SafeAsync(() => SaveAsync()), "\uE74E"); toolbar.Children.Add(save); Grid.SetColumn(save, 2);
        Content = new StudioLayout(monitor, inspector, toolbar, "live2d");

        viewport.TargetLabel = _ => modelName.Text;
        viewport.ObjectWheelHint = _ => "滚轮缩放模型 · Ctrl+滚轮缩放画布";
        viewport.FramePresented += (_, frame) => OnFrame(frame);
        viewport.Failed += (_, error) => Notice(error.Message);
        viewport.ObjectDragged += (_, drag) => MoveModel(drag);
        viewport.ObjectScaled += (_, scale) => ScaleModel(scale);
        playhead.ValueChanged += (_, args) => { if (!refreshing && document is not null) { Pause(); scrubber.Request(args.NewValue); } };
        Loaded += (_, _) => { active = true; if (document is not null && (previewDirty || !viewport.HasCurrentSource)) _ = SafeAsync(RefreshPreviewAsync); };
        Unloaded += (_, _) => Deactivate();
        KeyDown += OnKeyDown;
        AllowDrop = true;
        DragOver += (_, args) => { if (args.DataView.Contains(StandardDataFormats.StorageItems)) args.AcceptedOperation = DataPackageOperation.Copy; };
        Drop += (_, args) => _ = SafeAsync(async () =>
        {
            if (!args.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var files = await args.DataView.GetStorageItemsAsync(); if (files.FirstOrDefault()?.Path is { } path) await OpenAsync(path);
        });
        SelectInspector("controls"); RefreshControls();
    }

    static Button IconTool(string label, string glyph, Func<Task> action)
    {
        var button = StudioLayout.AsyncTool(label, action); button.Content = new FontIcon { Glyph = glyph, FontSize = 14 };
        button.Width = 32; button.Padding = new(5); ToolTipService.SetToolTip(button, label); return button;
    }
    void SelectInspector(string key)
    {
        activeTab = key; inspectorBody.Content = inspectorViews[key];
        foreach (var (name, tab) in inspectorTabs) tab.IsChecked = key == name;
        RefreshTimeDependentControls();
        if (key == "parameters" && document is not null && viewport.HasCurrentSource) _ = SafeAsync(ReadParametersAsync);
    }
    void OnKeyDown(object sender, KeyRoutedEventArgs args) => HandleWorkspaceKey(args);
    internal void HandleWorkspaceKey(KeyRoutedEventArgs args)
    {
        if (args.Handled) return;
        bool ctrl = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        bool shift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        bool text = IsTextInput(args.OriginalSource as DependencyObject);
        if (ctrl && args.Key == VirtualKey.S) { args.Handled = true; _ = SafeAsync(() => SaveAsync(shift)); }
        else if (ctrl && args.Key == VirtualKey.O) { args.Handled = true; _ = SafeAsync(() => OpenAsync()); }
        else if (ctrl && args.Key == VirtualKey.N) { args.Handled = true; _ = SafeAsync(NewAsync); }
        else if (ctrl && !text && args.Key is VirtualKey.Z or VirtualKey.Y) { args.Handled = true; Undo(args.Key == VirtualKey.Y || shift); }
        else if (!ctrl && !text && args.Key == VirtualKey.Space) { args.Handled = true; _ = SafeAsync(TogglePlayAsync); }
        else if (!ctrl && !text && args.Key is VirtualKey.Left or VirtualKey.Right) { args.Handled = true; _ = SafeAsync(() => StepAsync(args.Key == VirtualKey.Left ? -1 : 1)); }
        else if (!ctrl && !text && args.Key == VirtualKey.I) { args.Handled = true; MarkRange(true); }
        else if (!ctrl && !text && args.Key == VirtualKey.O) { args.Handled = true; MarkRange(false); }
        else if (!ctrl && !text && args.Key == VirtualKey.F) { args.Handled = true; viewport.FitCanvas(); }
    }
    static bool IsTextInput(DependencyObject? element)
    {
        for (; element is not null; element = VisualTreeHelper.GetParent(element))
            if (element is TextBox or NumberBox or PasswordBox) return true;
        return false;
    }
    async Task SafeAsync(Func<Task> action)
    { try { if (!disposed) await action(); } catch (OperationCanceledException) { } catch (Exception error) { if (!disposed) Notice(error.Message); } }
    void Notice(string message, bool error = true)
    {
        notice.Title = error ? "需要调整" : "Live2D"; notice.Message = message;
        notice.Severity = error ? InfoBarSeverity.Warning : InfoBarSeverity.Informational; notice.IsOpen = true;
    }
    void Pause()
    {
        pauseVersion++; playbackRequested = false; viewport.Pause();
        ((FontIcon)playButton.Content).Glyph = "\uE768";
    }
    void Edit(Action<Live2DDocument> change, bool live = false)
    {
        if (refreshing || document is null) return;
        Pause(); change(document); editVersion++; previewDirty = true; replayRequired |= !live;
        RefreshHeader(); RefreshParameterValues();
        previewTimer.Stop(); if (active) previewTimer.Start(); draftTimer.Stop(); draftTimer.Start();
    }
    void EditLive(Action<Live2DDocument> change) => Edit(change, true);
    void EditTransform(Action<Live2DDocument> change)
    {
        if (refreshing || document is null) return;
        transformPending = true; EditLive(change);
    }
    void Undo(bool redo)
    {
        if (document is null) return;
        Edit(current => { if (redo) current.Redo(); else current.Undo(); }); RefreshControls();
    }
    void MarkRange(bool start)
    {
        if (document is null) return;
        var full = TimelineRange.Full(document.Duration, 30);
        var range = document.Range ?? full;
        document.Range = (start ? range.MarkIn(document.Time) : range.MarkOut(document.Time)).ClampTo(full);
        RefreshHeader(); SaveDraft();
    }
    void ClearRange() { if (document is null) return; document.Range = null; RefreshHeader(); SaveDraft(); }
    void RefreshHeader()
    {
        title.Text = document is null ? "未打开模型" : (document.FilePath is null ? DisplayNames.Model(document.ModelId) : Path.GetFileNameWithoutExtension(document.FilePath)) + (HasUnsavedChanges ? " *" : "");
        modelName.Text = document is null ? "选择一个 Live2D 模型" : DisplayNames.Model(document.ModelId);
        modelDetail.Text = document is null ? "" : document.ModelId + " · " + document.Width + " × " + document.Height;
        rangeLabel.Text = document?.Range is { } range ? $"{range.Start:0.###}–{range.End:0.###} 秒 · {range.FrameCount} 帧" : "完整动画";
        RefreshTransport();
        foreach (var button in new[] { playButton, previousButton, nextButton, pngButton, movieButton, sequenceButton }) button.IsEnabled = document is not null;
        welcome.Visibility = document is null ? Visibility.Visible : Visibility.Collapsed;
    }
    void RefreshTransport()
    {
        frameLabel.Text = Timecode(document?.Time ?? 0);
        bool previous = refreshing; refreshing = true;
        playhead.Maximum = Math.Max(1d / 30, document?.Duration ?? 3); playhead.Value = document?.Time ?? 0;
        refreshing = previous;
    }
    static string Timecode(double seconds)
    {
        int frame = (int)Math.Floor(seconds * 30 + 1e-8);
        return string.Create(CultureInfo.InvariantCulture, $"{frame / 1800:00}:{frame / 30 % 60:00}:{frame % 30:00}");
    }
}
