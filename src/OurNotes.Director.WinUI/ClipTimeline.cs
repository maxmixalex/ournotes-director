using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using OurNotes.Director.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.UI;

namespace OurNotes.Director.WinUI;

/// <summary>A clip editor, not a drawing of interpreter rows. Track names and the ruler stay
/// fixed while scrolling; playback moves one line instead of rebuilding the visual tree.</summary>
internal sealed class ClipTimeline : UserControl
{
    const double RowHeight = 44;
    readonly Canvas canvas = new() { Background = B(0, 0, 0, 0) };
    readonly Canvas labels = new(), ruler = new();
    readonly ScrollViewer scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    readonly Line playhead = new() { Stroke = B(255, 103, 178, 250), StrokeThickness = 2, IsHitTestVisible = false };
    readonly Dictionary<string, Border> bars = new();
    readonly TextBlock time = new() { FontSize = 12, FontFamily = new FontFamily("Consolas"), VerticalAlignment = VerticalAlignment.Center };
    readonly Button play = new() { Content = "播放", Padding = new(12, 5, 12, 5) };
    readonly Slider zoom = new() { Minimum = 20, Maximum = 200, Value = 80, Width = 96 };
    readonly CheckBox repeat = new() { Content = "循环", IsChecked = false, MinWidth = 64 };
    CreativeScene? scene;
    List<(string Actor, string Kind, string Label)> tracks = [];
    double position, seconds = 3, scale = 80;
    string? selected, dragging;
    Border? dragBar;
    double pointerStart, clipStart, clipDuration, proposed;
    bool resizing;
    public event Action? PlayRequested;
    public event Action<double>? SeekRequested;
    public event Action<string>? ClipSelected;
    public event Action<string, double, bool>? ClipEdited;
    public event Action<string, string, double>? AddRequested;
    public event Action<string, double>? AssetDropped;
    public Func<JsonObject, MenuFlyout>? ContextMenuFactory { get; set; }
    public bool Repeat => repeat.IsChecked == true;
    public double Position
    {
        get => position;
        set
        {
            position = Math.Max(0, value); playhead.X1 = playhead.X2 = position * scale;
            time.Text = TimeSpan.FromSeconds(position).ToString(@"mm\:ss\.ff");
        }
    }
    public bool IsPlaying { set => play.Content = value ? "暂停" : "播放"; }
    static SolidColorBrush B(byte a, byte r, byte g, byte b) => new(Color.FromArgb(a, r, g, b));
    static string S(JsonObject c, string key) => CreativeScene.Text(c, key);
    static double N(JsonObject c, string key, double fallback = 0) => CreativeScene.Number(c, key, fallback);
    internal ClipTimeline()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new(25) });
        root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star), MinHeight = 80 });
        root.ColumnDefinitions.Add(new() { Width = new(150) });
        root.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var transport = new Grid { Padding = new(12, 7, 12, 7) };
        transport.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); transport.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var controls = Ui.Row(8, StudioLayout.Tool("回到开头", () => SeekRequested?.Invoke(0), "\uE892"), play, time, repeat);
        play.Click += (_, _) => PlayRequested?.Invoke();
        var z = Ui.Row(8, new TextBlock { Text = "缩放", FontSize = 11, VerticalAlignment = VerticalAlignment.Center }, zoom);
        transport.Children.Add(controls); transport.Children.Add(z); Grid.SetColumn(z, 1);
        root.Children.Add(transport); Grid.SetColumnSpan(transport, 2);
        var heading = new TextBlock { Text = "演出轨道", FontSize = 11, Margin = new(12, 3, 0, 0) };
        root.Children.Add(heading); Grid.SetRow(heading, 1);
        root.Children.Add(ruler); Grid.SetRow(ruler, 1); Grid.SetColumn(ruler, 1);
        root.Children.Add(labels); Grid.SetRow(labels, 2);
        scroll.Content = canvas; root.Children.Add(scroll); Grid.SetRow(scroll, 2); Grid.SetColumn(scroll, 1);
        Content = root; IsTabStop = true;
        zoom.ValueChanged += (_, _) => { scale = zoom.Value; Draw(); };
        scroll.ViewChanged += (_, _) => { DrawLabels(); DrawRuler(); };
        scroll.SizeChanged += (_, _) => Draw(); ActualThemeChanged += (_, _) => Draw();
        canvas.PointerPressed += (_, args) =>
        {
            if (args.Handled) return;
            var p = args.GetCurrentPoint(canvas);
            if (p.Properties.IsLeftButtonPressed) { SeekRequested?.Invoke(At(p.Position.X)); args.Handled = true; }
        };
        canvas.AllowDrop = true;
        canvas.DragOver += (_, args) => { if (args.DataView.Contains("OurNotes.SceneAsset")) args.AcceptedOperation = DataPackageOperation.Copy; };
        canvas.Drop += async (_, args) =>
        {
            if (!args.DataView.Contains("OurNotes.SceneAsset")) return;
            double at = At(args.GetPosition(canvas).X);
            if (await args.DataView.GetDataAsync("OurNotes.SceneAsset") is string data) AssetDropped?.Invoke(data, at);
            args.Handled = true;
        };
        KeyDown += (_, args) =>
        {
            if (args.OriginalSource is TextBox or NumberBox) return;
            if (args.Key == Windows.System.VirtualKey.Space) { PlayRequested?.Invoke(); args.Handled = true; }
        };
        Loaded += (_, _) => Draw();
    }
    public void Refresh(CreativeScene document, string? selectedClip)
    {
        scene = document; selected = selectedClip; seconds = Math.Max(3, scene.Duration + .5);
        tracks.Clear();
        var names = scene.Actors.OfType<JsonObject>().ToDictionary(a => S(a, "id"), a => CreativeScene.Text(a, "name", S(a, "id")));
        var actors = names.Keys.Concat(scene.Clips.OfType<JsonObject>().Select(c => S(c, "actor")).Where(a => a.Length > 0)).Distinct().ToArray();
        foreach (string actor in actors)
            foreach (string kind in new[] { "motion", "expression", "voice", "transform" })
                tracks.Add((actor, kind, names.GetValueOrDefault(actor, actor) + " · " + KindName(kind)));
        foreach (var group in scene.Clips.OfType<JsonObject>().Where(c => S(c, "kind") != "setup")
            .Select(c => (Actor: S(c, "actor"), Kind: S(c, "kind"))).Distinct())
            if (!tracks.Any(t => t.Actor == group.Actor && t.Kind == group.Kind))
                tracks.Add((group.Actor, group.Kind, (names.GetValueOrDefault(group.Actor, group.Actor.Length == 0 ? "场景" : group.Actor)) + " · " + KindName(group.Kind)));
        if (!tracks.Any(t => t.Actor == "" && t.Kind == "camera")) tracks.Add(("", "camera", "场景 · 镜头"));
        if (!tracks.Any(t => t.Actor == "" && t.Kind == "grade")) tracks.Add(("", "grade", "场景 · 画面"));
        Draw();
    }
    public static string KindName(string kind) => kind switch
    { "motion" => "动作", "expression" => "表情", "voice" => "对白与语音", "transform" => "位置", "camera" => "镜头", "grade" => "画面", "setup" => "加载", _ => "原有演出" };
    double At(double x) => Math.Clamp(Math.Round(x / scale * 30) / 30, 0, 36000);
    double Snap(double value, string exclude)
    {
        double snapped = Math.Max(0, Math.Round(value * 30) / 30), distance = 7 / scale;
        if (scene is null) return snapped;
        foreach (var edge in scene.Clips.OfType<JsonObject>().Where(c => S(c, "id") != exclude)
            .SelectMany(c => new[] { N(c, "start"), N(c, "start") + N(c, "duration") }).Append(position))
            if (Math.Abs(value - edge) < distance) { snapped = edge; distance = Math.Abs(value - edge); }
        return Math.Max(0, snapped);
    }
    void Draw()
    {
        if (scene is null || !IsLoaded) return;
        canvas.Children.Clear(); bars.Clear();
        canvas.Width = Math.Max(200, Math.Max(scroll.ActualWidth - 16, seconds * scale)); canvas.Height = Math.Max(100, tracks.Count * RowHeight);
        for (int i = 0; i < tracks.Count; i++)
        {
            var line = new Line { X1 = 0, X2 = canvas.Width, Y1 = (i + 1) * RowHeight, Y2 = (i + 1) * RowHeight, Stroke = B(40, 128, 128, 128), IsHitTestVisible = false };
            canvas.Children.Add(line);
            var track = tracks[i];
            foreach (var clip in scene.Clips.OfType<JsonObject>().Where(c => S(c, "actor") == track.Actor && S(c, "kind") == track.Kind))
                DrawClip(clip, i);
        }
        playhead.Y1 = 0; playhead.Y2 = canvas.Height; canvas.Children.Add(playhead); Position = position;
        DrawLabels(); DrawRuler();
    }
    void DrawClip(JsonObject clip, int row)
    {
        string id = S(clip, "id"), kind = S(clip, "kind");
        double width = Math.Max(30, N(clip, "duration", 1d / 30) * scale);
        var color = kind switch { "voice" => B(255, 56, 105, 94), "expression" => B(255, 108, 78, 114),
            "motion" => B(255, 67, 87, 130), "camera" => B(255, 121, 95, 59), _ => B(255, 73, 79, 91) };
        var bar = new Border { Width = width, Height = RowHeight - 8, CornerRadius = new(4), Background = color,
            BorderThickness = new(id == selected ? 2 : 1), BorderBrush = id == selected ? B(255, 166, 214, 255) : B(90, 210, 220, 235),
            Opacity = clip["enabled"]?.GetValue<bool>() == false ? .35 : 1, Padding = new(6, 3, 6, 3), Tag = id };
        var body = new Grid();
        var label = new TextBlock { Text = CreativeScene.Text(clip, "label", KindName(kind)), FontSize = 11, Foreground = B(255, 248, 249, 251),
            TextTrimming = TextTrimming.CharacterEllipsis, IsHitTestVisible = false };
        body.Children.Add(label);
        if (clip["waveform"] is JsonArray waveform && waveform.Count > 1)
        {
            var wave = new Polyline { Stroke = B(130, 190, 238, 221), StrokeThickness = 1, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Bottom };
            for (int i = 0; i < waveform.Count; i++)
                wave.Points.Add(new Point(i * Math.Max(1, width - 16) / (waveform.Count - 1), 23 - Math.Clamp(waveform[i]?.GetValue<float>() ?? 0, 0, 1) * 10));
            body.Children.Insert(0, wave);
        }
        bar.Child = body; Canvas.SetLeft(bar, N(clip, "start") * scale); Canvas.SetTop(bar, row * RowHeight + 4);
        canvas.Children.Add(bar); bars[id] = bar;
        ToolTipService.SetToolTip(bar, $"{label.Text}\n{N(clip, "start"):0.00} — {N(clip, "start") + N(clip, "duration"):0.00} 秒 · 拖动安排时间");
        bar.PointerPressed += (_, args) =>
        {
            var point = args.GetCurrentPoint(bar); if (!point.Properties.IsLeftButtonPressed) return;
            ClipSelected?.Invoke(id); selected = id;
            dragging = id; dragBar = bar; clipStart = N(clip, "start"); clipDuration = N(clip, "duration");
            pointerStart = args.GetCurrentPoint(canvas).Position.X; resizing = point.Position.X > bar.ActualWidth - 7 && scene!.CanResize(clip);
            proposed = resizing ? clipDuration : clipStart;
            bar.CapturePointer(args.Pointer); args.Handled = true;
        };
        bar.PointerMoved += (_, args) =>
        {
            if (dragging != id || dragBar != bar) return;
            double delta = (args.GetCurrentPoint(canvas).Position.X - pointerStart) / scale;
            proposed = resizing ? Math.Max(1d / 30, Snap(clipStart + clipDuration + delta, id) - clipStart) : Snap(clipStart + delta, id);
            if (resizing) bar.Width = Math.Max(12, proposed * scale); else Canvas.SetLeft(bar, proposed * scale);
            args.Handled = true;
        };
        bar.PointerReleased += (_, args) =>
        {
            if (dragging != id) return;
            bool resize = resizing; double next = proposed; dragging = null; dragBar = null;
            bar.ReleasePointerCapture(args.Pointer); args.Handled = true;
            if (Math.Abs(next - (resize ? clipDuration : clipStart)) > .0001) ClipEdited?.Invoke(id, next, resize);
        };
        bar.PointerCaptureLost += (_, _) => { if (dragging == id) { dragging = null; dragBar = null; Draw(); } };
        bar.ContextRequested += (_, args) =>
        { if (ContextMenuFactory is not null) { ClipSelected?.Invoke(id); ContextMenuFactory(clip).ShowAt(bar); args.Handled = true; } };
    }
    void DrawLabels()
    {
        labels.Children.Clear(); labels.Clip = new RectangleGeometry { Rect = new(0, 0, 150, Math.Max(0, scroll.ActualHeight)) };
        for (int i = 0; i < tracks.Count; i++)
        {
            double top = i * RowHeight - scroll.VerticalOffset; if (top < -RowHeight || top > scroll.ActualHeight) continue;
            var track = tracks[i];
            var button = new Button { Content = track.Label, Width = 148, Height = RowHeight - 1,
                HorizontalContentAlignment = HorizontalAlignment.Left, Background = B(0, 0, 0, 0), BorderThickness = new(0), FontSize = 11, Padding = new(10, 0, 4, 0) };
            ToolTipService.SetToolTip(button, "点击为这条轨道添加片段");
            button.Click += (_, _) => AddRequested?.Invoke(track.Actor, track.Kind, position);
            Canvas.SetTop(button, top); labels.Children.Add(button);
        }
    }
    void DrawRuler()
    {
        ruler.Children.Clear(); double width = Math.Max(0, scroll.ActualWidth);
        ruler.Clip = new RectangleGeometry { Rect = new(0, 0, width, 25) };
        double tick = scale >= 100 ? .5 : scale >= 45 ? 1 : 2;
        double from = Math.Floor(scroll.HorizontalOffset / scale / tick) * tick;
        for (double t = from; t < (scroll.HorizontalOffset + width) / scale + tick; t += tick)
        {
            var label = new TextBlock { Text = t.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "s", FontSize = 10, Opacity = .7, IsHitTestVisible = false };
            Canvas.SetLeft(label, t * scale - scroll.HorizontalOffset + 3); Canvas.SetTop(label, 5); ruler.Children.Add(label);
        }
        ruler.Background = B(1, 0, 0, 0);
        ruler.PointerPressed -= SeekRuler; ruler.PointerPressed += SeekRuler;
    }
    void SeekRuler(object sender, PointerRoutedEventArgs args)
    { if (args.GetCurrentPoint(ruler).Properties.IsLeftButtonPressed) { SeekRequested?.Invoke(At(args.GetCurrentPoint(ruler).Position.X + scroll.HorizontalOffset)); args.Handled = true; } }
}
