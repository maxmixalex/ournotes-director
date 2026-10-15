using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.UI;

namespace OurNotes.Director.WinUI;

internal sealed record TimelineItem(string Id, string Actor, string Kind, string Label,
    double Start, double End, bool CanMove = true, bool CanResize = false, bool CanTrim = false,
    bool IsKey = false, bool Enabled = true, float[]? Waveform = null,
    double AudioIn = 0, double AudioDuration = 0, double SourceDuration = 0);
internal sealed record TimelineLane(string Actor, string Kind, string Label, IReadOnlyList<TimelineItem> Items);

/// <summary>Shared, viewport-virtualized timeline. The owner edits the actual project;
/// this control holds only read-only display data, never a second editable scene.</summary>
internal sealed class ClipTimeline : UserControl
{
    const double RowHeight = 42;
    readonly Canvas canvas = new() { Background = Brush(0, 0, 0, 0) };
    readonly Canvas labels = new(), ruler = new();
    readonly ScrollViewer scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    readonly Line playhead = new() { Stroke = Brush(255, 103, 178, 250), StrokeThickness = 2, IsHitTestVisible = false };
    readonly Dictionary<string, Border> bars = [];
    readonly TextBlock time = new() { FontSize = 12, FontFamily = new("Consolas"), VerticalAlignment = VerticalAlignment.Center, MinWidth = 155 };
    readonly Button play = new() { Content = "播放", Padding = new(12, 5, 12, 5) };
    readonly Slider zoom = new() { Minimum = .025, Maximum = 300, Value = 80, Width = 80 };
    readonly CheckBox repeat = new() { Content = "循环", MinWidth = 58 };
    readonly CheckBox snapping = new() { Content = "吸附", IsChecked = true, MinWidth = 58 };
    IReadOnlyList<TimelineLane> lanes = [];
    TimelineItem[] items = [];
    string? selected, sourceKey;
    TimelineItem? dragging;
    Border? dragBar;
    double position, length = 3, scale = 80, pointerStart, proposed;
    int framesPerSecond = 30, dragMode;
    bool fitPending = true, drawing, scrubbing, playing;
    public event Action? PlayRequested;
    public event Action<double>? SeekRequested;
    public event Action<string>? ClipSelected;
    public event Action<string, double, bool>? ClipEdited;
    public event Action<string, double>? ClipTrimmed;
    public event Action<string, string, double>? AddRequested;
    public event Action<string, double, string?>? AssetDropped;
    public Func<string, MenuFlyout>? ContextMenuFactory { get; set; }
    public Func<string, string, double, MenuFlyout>? TrackMenuFactory { get; set; }
    public bool Repeat { get => repeat.IsChecked == true; set => repeat.IsChecked = value; }
    public double Position
    {
        get => position;
        set
        {
            if (!double.IsFinite(value)) return;
            position = Math.Max(0, value); playhead.X1 = playhead.X2 = position * scale;
            time.Text = $"{TimeSpan.FromSeconds(position):mm\\:ss\\.ff} / {TimeSpan.FromSeconds(length):mm\\:ss\\.ff}";
        }
    }
    public bool IsPlaying
    {
        get => playing;
        set { if (playing == value) return; playing = value; play.Content = value ? "暂停" : "播放"; }
    }
    internal int VisibleClipCount => bars.Count;
    static SolidColorBrush Brush(byte a, byte r, byte g, byte b) => new(Color.FromArgb(a, r, g, b));
    static string S(JsonObject value, string key) => CreativeScene.Text(value, key);
    static double N(JsonObject value, string key, double fallback = 0) => CreativeScene.Number(value, key, fallback);

    internal ClipTimeline()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = new(26) });
        root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star), MinHeight = 80 });
        root.ColumnDefinitions.Add(new() { Width = new(164) }); root.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var transport = new Grid { Padding = new(12, 7, 12, 7), ColumnSpacing = 12 };
        transport.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); transport.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var beginning = StudioLayout.Tool("", () => SeekRequested?.Invoke(0), "\uE892"); ToolTipService.SetToolTip(beginning, "回到开头 · Home");
        var controls = Ui.Row(7, beginning, play, time, repeat, snapping); play.Click += (_, _) => PlayRequested?.Invoke();
        var navigation = Ui.Row(6, StudioLayout.Tool("整段", Fit, "\uE9A6"), zoom); ToolTipService.SetToolTip(zoom, "时间轴缩放 · Ctrl+滚轮");
        transport.Children.Add(controls); transport.Children.Add(navigation); Grid.SetColumn(navigation, 1);
        root.Children.Add(transport); Grid.SetColumnSpan(transport, 2);
        var heading = new TextBlock { Text = "演出轨道", FontSize = 11, Margin = new(12, 4, 0, 0) };
        root.Children.Add(heading); Grid.SetRow(heading, 1);
        root.Children.Add(ruler); Grid.SetRow(ruler, 1); Grid.SetColumn(ruler, 1);
        root.Children.Add(labels); Grid.SetRow(labels, 2); scroll.Content = canvas;
        root.Children.Add(scroll); Grid.SetRow(scroll, 2); Grid.SetColumn(scroll, 1); Content = root; IsTabStop = true;
        zoom.ValueChanged += (_, _) => { scale = zoom.Value; Draw(); };
        scroll.ViewChanged += (_, _) => { if (dragging is null) Draw(); };
        scroll.SizeChanged += (_, _) => { if (fitPending) Fit(); else Draw(); };
        ActualThemeChanged += (_, _) => Draw(); Loaded += (_, _) => { if (fitPending) Fit(); else Draw(); };
        canvas.PointerPressed += (_, args) =>
        {
            if (args.Handled) return; var p = args.GetCurrentPoint(canvas);
            if (p.Properties.IsLeftButtonPressed) { SeekRequested?.Invoke(At(p.Position.X)); args.Handled = true; }
        };
        canvas.DoubleTapped += (_, args) =>
        {
            if (args.Handled) return; var p = args.GetPosition(canvas); var lane = LaneAt(p.Y);
            if (lane is not null) { AddRequested?.Invoke(lane.Actor, lane.Kind, At(p.X)); args.Handled = true; }
        };
        canvas.ContextRequested += (_, args) =>
        {
            if (args.Handled || TrackMenuFactory is null || !args.TryGetPosition(canvas, out var p)) return;
            var lane = LaneAt(p.Y); if (lane is null) return;
            TrackMenuFactory(lane.Actor, lane.Kind, At(p.X)).ShowAt(canvas, p); args.Handled = true;
        };
        canvas.AllowDrop = true;
        canvas.DragOver += (_, args) => { if (args.DataView.Contains("OurNotes.SceneAsset")) args.AcceptedOperation = DataPackageOperation.Copy; };
        canvas.Drop += async (_, args) =>
        {
            if (!args.DataView.Contains("OurNotes.SceneAsset")) return;
            var p = args.GetPosition(canvas); var target = LaneAt(p.Y)?.Actor;
            if (await args.DataView.GetDataAsync("OurNotes.SceneAsset") is string data) AssetDropped?.Invoke(data, At(p.X), target);
            args.Handled = true;
        };
        ruler.Background = Brush(1, 0, 0, 0);
        ruler.PointerPressed += (_, args) =>
        { if (args.GetCurrentPoint(ruler).Properties.IsLeftButtonPressed) { scrubbing = true; ruler.CapturePointer(args.Pointer); Scrub(args); } };
        ruler.PointerMoved += (_, args) => { if (scrubbing) Scrub(args); };
        ruler.PointerReleased += (_, args) => { if (scrubbing) { Scrub(args); scrubbing = false; ruler.ReleasePointerCapture(args.Pointer); } };
        ruler.PointerCaptureLost += (_, _) => scrubbing = false;
        KeyDown += (_, args) =>
        {
            if (args.Handled || TextInput(args.OriginalSource as DependencyObject)) return;
            if (args.Key == Windows.System.VirtualKey.Space) PlayRequested?.Invoke();
            else if (args.Key == Windows.System.VirtualKey.Home) SeekRequested?.Invoke(0);
            else if (args.Key == Windows.System.VirtualKey.End) SeekRequested?.Invoke(length);
            else if (args.Key is Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Right)
                SeekRequested?.Invoke(Math.Clamp(position + (args.Key == Windows.System.VirtualKey.Left ? -1 : 1) / (double)framesPerSecond, 0, length));
            else return;
            args.Handled = true;
        };
        PointerWheelChanged += (_, args) =>
        {
            if ((Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) == 0) return;
            double anchor = scroll.HorizontalOffset / scale; zoom.Value = Math.Clamp(scale * Math.Pow(1.15, args.GetCurrentPoint(this).Properties.MouseWheelDelta / 120d), zoom.Minimum, zoom.Maximum);
            scroll.ChangeView(anchor * scale, null, null); args.Handled = true;
        };
    }
    static bool TextInput(DependencyObject? node)
    { for (; node is not null; node = VisualTreeHelper.GetParent(node)) if (node is TextBox or NumberBox) return true; return false; }
    TimelineLane? LaneAt(double y)
    { int index = (int)Math.Floor(y / RowHeight); return index >= 0 && index < lanes.Count ? lanes[index] : null; }
    void Scrub(PointerRoutedEventArgs args)
    { SeekRequested?.Invoke(At(args.GetCurrentPoint(ruler).Position.X + scroll.HorizontalOffset)); args.Handled = true; }
    static string ClipLabel(JsonObject clip)
    {
        string kind = S(clip, "kind"), label = CreativeScene.Text(clip, "label", KindName(kind));
        return kind is "motion" or "expression" && clip["sourcePerformance"]?.GetValue<bool>() == true ? DisplayNames.Animation(label) : label;
    }
    public void Refresh(CreativeScene document, string? selectedClip)
    {
        var clipViews = document.Clips.OfType<JsonObject>().Where(c => S(c, "kind") != "setup").Select(c => new TimelineItem(
            S(c, "id"), S(c, "actor"), S(c, "kind"), ClipLabel(c),
            N(c, "start"), Math.Max(N(c, "start") + 1d / 30, CreativeClipEditing.DisplayEnd(document, c)),
            CanResize: document.CanResize(c), CanTrim: CreativeClipEditing.CanTrim(c), Enabled: c["enabled"]?.GetValue<bool>() != false,
            Waveform: (c["waveform"] as JsonArray)?.Select(v => float.TryParse(v?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 0).ToArray(),
            AudioIn: N(c["data"]!.AsObject(), "AudioIn"), AudioDuration: N(c["data"]!.AsObject(), "AudioDuration", N(c, "duration")), SourceDuration: N(c, "sourceDuration"))).ToArray();
        var names = document.Actors.OfType<JsonObject>().ToDictionary(a => S(a, "id"), a => DisplayNames.Character(CreativeScene.Text(a, "name", S(a, "id"))), StringComparer.Ordinal);
        var result = new List<TimelineLane>();
        foreach (string actor in names.Keys.Concat(clipViews.Select(c => c.Actor).Where(a => a.Length > 0)).Distinct())
        {
            foreach (string kind in new[] { "motion", "expression", "voice", "transform" })
            {
                var group = clipViews.Where(c => c.Actor == actor && c.Kind == kind).ToArray();
                if (document.Root["imported"]?.GetValue<bool>() == true && group.Length == 0) continue;
                result.Add(new(actor, kind, names.GetValueOrDefault(actor, actor) + " · " + KindName(kind), group));
            }
        }
        foreach (var group in clipViews.GroupBy(c => (c.Actor, c.Kind)))
            if (!result.Any(t => t.Actor == group.Key.Actor && t.Kind == group.Key.Kind))
                result.Add(new(group.Key.Actor, group.Key.Kind, names.GetValueOrDefault(group.Key.Actor, group.Key.Actor.Length == 0 ? "场景" : group.Key.Actor) + " · " + KindName(group.Key.Kind), group.ToArray()));
        if (!result.Any(t => t.Actor == "" && t.Kind == "camera")) result.Add(new("", "camera", "场景 · 镜头", []));
        if (!result.Any(t => t.Actor == "" && t.Kind == "grade")) result.Add(new("", "grade", "场景 · 画面", []));
        SetTracks(document.Source.ToJsonString() + document.Root["imported"], document.Duration, 30, result, selectedClip);
    }
    public void SetTracks(string identity, double duration, int fps, IReadOnlyList<TimelineLane> tracks, string? selection)
    {
        if (!double.IsFinite(duration) || duration <= 0 || fps <= 0) throw new ArgumentException("时间轴范围无效。");
        if (sourceKey != identity) { sourceKey = identity; fitPending = true; }
        length = duration; framesPerSecond = fps; lanes = tracks; items = tracks.SelectMany(t => t.Items).ToArray(); selected = selection;
        if (fitPending && IsLoaded) Fit(); else Draw();
    }
    public static string KindName(string kind) => kind switch
    { "motion" => "动作", "expression" => "表情", "voice" => "对白与语音", "transform" => "位置", "camera" => "镜头", "grade" => "画面", "setup" => "加载", "hold" => "停留", _ => "原有演出" };
    public void Fit()
    {
        if (scroll.ActualWidth <= 20) { fitPending = true; return; }
        fitPending = false; scale = Math.Clamp((scroll.ActualWidth - 28) / Math.Max(3, length + .25), zoom.Minimum, zoom.Maximum);
        zoom.Value = scale; scroll.ChangeView(0, null, null); Draw();
    }
    double At(double x) => Math.Clamp(Math.Round(x / scale * framesPerSecond) / framesPerSecond, 0, 36000);
    double Snap(double value, string exclude)
    {
        double result = Math.Max(0, Math.Round(value * framesPerSecond) / framesPerSecond);
        if (snapping.IsChecked != true) return result; double distance = 7 / scale;
        foreach (double edge in items.Where(c => c.Id != exclude).SelectMany(c => new[] { c.Start, c.End }).Append(position))
            if (Math.Abs(value - edge) < distance) { result = edge; distance = Math.Abs(value - edge); }
        return Math.Max(0, result);
    }
    void Draw()
    {
        if (!IsLoaded || drawing || dragging is not null) return; drawing = true;
        try
        {
            canvas.Children.Clear(); bars.Clear(); canvas.Width = Math.Max(200, Math.Max(scroll.ActualWidth - 16, (length + .5) * scale)); canvas.Height = Math.Max(100, lanes.Count * RowHeight);
            double left = scroll.HorizontalOffset, right = left + Math.Max(100, scroll.ActualWidth), top = scroll.VerticalOffset, bottom = top + Math.Max(100, scroll.ActualHeight);
            int first = Math.Max(0, (int)(top / RowHeight) - 1), last = Math.Min(lanes.Count - 1, (int)(bottom / RowHeight) + 1);
            for (int row = first; row <= last; row++)
            {
                canvas.Children.Add(new Line { X1 = left, X2 = right, Y1 = (row + 1) * RowHeight, Y2 = (row + 1) * RowHeight, Stroke = Brush(40, 128, 128, 128), IsHitTestVisible = false });
                foreach (var item in lanes[row].Items.Where(c => c.Start * scale < right + 20 && c.End * scale > left - 20)) DrawClip(item, row, left, right);
            }
            playhead.Y1 = top; playhead.Y2 = bottom; canvas.Children.Add(playhead); Position = position; DrawLabels(first, last); DrawRuler();
        }
        finally { drawing = false; }
    }
    public void Select(string? id, bool notify = false)
    {
        selected = id;
        foreach (var (key, bar) in bars) { bar.BorderThickness = new(key == id ? 2 : 1); bar.BorderBrush = key == id ? Brush(255, 166, 214, 255) : Brush(90, 210, 220, 235); }
        if (notify && id is not null) ClipSelected?.Invoke(id);
    }
    void DrawClip(TimelineItem clip, int row, double left, double right)
    {
        double fullStart = clip.Start * scale, fullEnd = clip.End * scale;
        double visibleStart = clip.IsKey ? fullStart - 5 : Math.Max(fullStart, left - 8), visibleEnd = clip.IsKey ? fullStart + 5 : Math.Min(fullEnd, right + 8);
        double width = clip.IsKey ? 10 : Math.Max(5, visibleEnd - visibleStart);
        var color = clip.Kind switch { "voice" => Brush(255, 48, 103, 88), "expression" => Brush(255, 103, 76, 117), "motion" => Brush(255, 60, 88, 133), "camera" => Brush(255, 125, 96, 52), _ => Brush(255, 70, 78, 92) };
        var bar = new Border { Width = width, Height = clip.IsKey ? 10 : RowHeight - 8, CornerRadius = new(clip.IsKey ? 1 : 4), Background = color,
            BorderThickness = new(clip.Id == selected ? 2 : 1), BorderBrush = clip.Id == selected ? Brush(255, 166, 214, 255) : Brush(90, 210, 220, 235), Opacity = clip.Enabled ? 1 : .35, Padding = new(clip.IsKey ? 0 : 5, 2, 3, 2) };
        if (clip.IsKey) bar.RenderTransform = new RotateTransform { Angle = 45, CenterX = 5, CenterY = 5 };
        else
        {
            var body = new Grid();
            if (clip.Waveform is { Length: > 1 } peaks && clip.SourceDuration > 0)
            {
                var wave = new Polyline { Stroke = Brush(130, 185, 239, 215), StrokeThickness = 1, IsHitTestVisible = false };
                double actualWidth = Math.Max(1, width - 10); int count = Math.Clamp((int)actualWidth, 2, 400);
                for (int i = 0; i < count; i++)
                {
                    double t = (visibleStart - fullStart + i * actualWidth / (count - 1)) / Math.Max(1e-9, fullEnd - fullStart);
                    double sourceTime = clip.AudioIn + Math.Clamp(t, 0, 1) * clip.AudioDuration;
                    int index = Math.Clamp((int)Math.Floor(sourceTime / clip.SourceDuration * peaks.Length), 0, peaks.Length - 1);
                    wave.Points.Add(new(i * actualWidth / (count - 1), 24 - Math.Clamp(peaks[index], 0, 1) * 11));
                }
                body.Children.Add(wave);
            }
            body.Children.Add(new TextBlock { Text = clip.Label, FontSize = 11, Foreground = Brush(255, 248, 249, 251), TextTrimming = TextTrimming.CharacterEllipsis, IsHitTestVisible = false });
            bar.Child = body;
        }
        Canvas.SetLeft(bar, visibleStart); Canvas.SetTop(bar, row * RowHeight + (clip.IsKey ? 16 : 4)); canvas.Children.Add(bar); bars[clip.Id] = bar;
        ToolTipService.SetToolTip(bar, $"{clip.Label}\n{clip.Start:0.###}–{clip.End:0.###} 秒" + (clip.CanTrim ? " · 拖两端裁剪" : clip.CanMove ? " · 拖动安排时间" : " · 原始动作"));
        bar.PointerPressed += (_, args) =>
        {
            var point = args.GetCurrentPoint(canvas); if (!point.Properties.IsLeftButtonPressed) return;
            Select(clip.Id, true); args.Handled = true; if (!clip.CanMove) return;
            dragging = clip; dragBar = bar; pointerStart = point.Position.X;
            dragMode = !clip.IsKey && clip.CanTrim && Math.Abs(pointerStart - fullStart) < 8 ? -1
                : !clip.IsKey && clip.CanResize && Math.Abs(pointerStart - fullEnd) < 8 ? 1 : 0;
            proposed = dragMode == 1 ? clip.End - clip.Start : clip.Start; bar.CapturePointer(args.Pointer);
        };
        bar.PointerMoved += (_, args) =>
        {
            if (dragging?.Id != clip.Id || dragBar != bar) return;
            double delta = (args.GetCurrentPoint(canvas).Position.X - pointerStart) / scale;
            proposed = dragMode == 1 ? Math.Max(1d / framesPerSecond, Snap(clip.End + delta, clip.Id) - clip.Start)
                : dragMode == -1 ? Math.Min(clip.End - 1d / framesPerSecond, Snap(clip.Start + delta, clip.Id)) : Snap(clip.Start + delta, clip.Id);
            if (dragMode == 1) bar.Width = Math.Max(5, proposed * scale);
            else { Canvas.SetLeft(bar, proposed * scale); if (dragMode == -1) bar.Width = Math.Max(5, (clip.End - proposed) * scale); }
            args.Handled = true;
        };
        bar.PointerReleased += (_, args) =>
        {
            if (dragging?.Id != clip.Id) return;
            double next = proposed; int mode = dragMode; dragging = null; dragBar = null; bar.ReleasePointerCapture(args.Pointer); args.Handled = true;
            if (Math.Abs(next - (mode == 1 ? clip.End - clip.Start : clip.Start)) > .00001)
            { if (mode == -1) ClipTrimmed?.Invoke(clip.Id, next); else ClipEdited?.Invoke(clip.Id, next, mode == 1); }
            Draw();
        };
        bar.PointerCaptureLost += (_, _) => { if (dragging?.Id == clip.Id) { dragging = null; dragBar = null; Draw(); } };
        bar.DoubleTapped += (_, args) => { SeekRequested?.Invoke(clip.Start); args.Handled = true; };
        bar.ContextRequested += (_, args) => { Select(clip.Id, true); ContextMenuFactory?.Invoke(clip.Id).ShowAt(bar); args.Handled = true; };
    }
    void DrawLabels(int first, int last)
    {
        labels.Children.Clear(); labels.Clip = new RectangleGeometry { Rect = new(0, 0, 164, Math.Max(0, scroll.ActualHeight)) };
        for (int i = first; i <= last; i++)
        {
            var lane = lanes[i]; var button = new Button { Content = new TextBlock { Text = lane.Label, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis },
                Width = 162, Height = RowHeight - 1, HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = Brush(0, 0, 0, 0), BorderThickness = new(0), Padding = new(10, 0, 5, 0) };
            ToolTipService.SetToolTip(button, lane.Label + " · 点击添加演出"); button.Click += (_, _) => AddRequested?.Invoke(lane.Actor, lane.Kind, position);
            Canvas.SetTop(button, i * RowHeight - scroll.VerticalOffset); labels.Children.Add(button);
        }
    }
    void DrawRuler()
    {
        ruler.Children.Clear(); double width = Math.Max(0, scroll.ActualWidth); ruler.Clip = new RectangleGeometry { Rect = new(0, 0, width, 26) };
        double tick = .5; while (tick * scale < 55) tick *= 2;
        for (double t = Math.Floor(scroll.HorizontalOffset / scale / tick) * tick; t < (scroll.HorizontalOffset + width) / scale + tick; t += tick)
        {
            var label = new TextBlock { Text = t >= 60 ? TimeSpan.FromSeconds(t).ToString(@"mm\:ss") : t.ToString("0.#", CultureInfo.InvariantCulture) + "s", FontSize = 10, Opacity = .7, IsHitTestVisible = false };
            Canvas.SetLeft(label, t * scale - scroll.HorizontalOffset + 3); Canvas.SetTop(label, 5); ruler.Children.Add(label);
        }
    }
}
