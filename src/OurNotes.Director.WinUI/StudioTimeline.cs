using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace OurNotes.Director.WinUI;

internal sealed record StudioTimelineMarker(string Id, string TrackId, double Time, string Label, string Kind = "key");
internal sealed record StudioTimelineTrack(string Id, string Label, IReadOnlyList<StudioTimelineMarker> Markers);

/// <summary>Native interactive ruler and keyframe tracks. Editing only occurs through its public callbacks.</summary>
internal sealed class StudioTimeline : UserControl
{
    private const double LabelWidth = 106, RulerHeight = 24, TrackHeight = 27;
    private readonly Canvas _canvas = new() { Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)) };
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly Button _play;
    private readonly StackPanel _editing;
    private readonly TextBlock _time = new() { FontFamily = new FontFamily("Consolas"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, MinWidth = 116 };
    private readonly NumberBox _duration = new() { Value = 5, Minimum = .1, Maximum = 36000, Width = 90,
        SmallChange = 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Header = null };
    private readonly Slider _zoom = new() { Minimum = 20, Maximum = 160, Value = 60, Width = 76, VerticalAlignment = VerticalAlignment.Center };
    private readonly Line _playhead = new() { StrokeThickness = 1.5, Stroke = new SolidColorBrush(Color.FromArgb(255, 55, 148, 225)), IsHitTestVisible = false };
    private IReadOnlyList<StudioTimelineTrack> _tracks = [];
    private StudioTimelineMarker? _dragging;
    private string? _selectedMarker;
    private double _position, _length = 5, _dragTime;
    private bool _playing, _updating, _scrubbing;
    internal event Action<double>? SeekRequested;
    internal event Action<double>? DurationChanged;
    internal event Action? PlayPauseRequested;
    internal event Action<StudioTimelineMarker>? MarkerSelected;
    internal event Action<StudioTimelineMarker, double>? MarkerMoved;
    internal event Action? AddKeyRequested;
    internal event Action? DeleteKeyRequested;

    internal StudioTimeline()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Stretch;
        _play = StudioLayout.Tool("播放", () => PlayPauseRequested?.Invoke(), "\uE768");
        var transport = new Grid { Padding = new(9, 6, 9, 6), ColumnSpacing = 8 };
        transport.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        transport.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        transport.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var playback = Ui.Row(6, StudioLayout.Tool("", () => SeekRequested?.Invoke(0), "\uE892"), _play, _time);
        ToolTipService.SetToolTip(playback.Children[0], "回到开头");
        var editing = _editing = Ui.Row(6, StudioLayout.Tool("关键帧", () => AddKeyRequested?.Invoke(), "\uE710"),
            StudioLayout.Tool("", () => DeleteKeyRequested?.Invoke(), "\uE74D"));
        editing.HorizontalAlignment = HorizontalAlignment.Center;
        ToolTipService.SetToolTip(editing.Children[1], "删除所选关键帧");
        var limits = Ui.Row(6, new TextBlock { Text = "时长", VerticalAlignment = VerticalAlignment.Center, FontSize = 12 }, _duration,
            new TextBlock { Text = "秒", VerticalAlignment = VerticalAlignment.Center, FontSize = 12 }, _zoom);
        ToolTipService.SetToolTip(_zoom, "时间轴缩放"); AutomationProperties.SetName(_zoom, "时间轴缩放");
        AutomationProperties.SetName(_duration, "场景时长（秒）");
        transport.Children.Add(playback); transport.Children.Add(editing); transport.Children.Add(limits);
        Grid.SetColumn(editing, 1); Grid.SetColumn(limits, 2);
        var root = new Grid(); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        root.Children.Add(transport); _scroll.Content = _canvas; root.Children.Add(_scroll); Grid.SetRow(_scroll, 1); Content = root;
        _scroll.SizeChanged += (_, _) => Draw();
        _zoom.ValueChanged += (_, _) => Draw();
        ActualThemeChanged += (_, _) => Draw();
        _duration.ValueChanged += (_, _) =>
        {
            if (_updating || !double.IsFinite(_duration.Value)) return;
            _length = Math.Clamp(_duration.Value, .1, 36000); Draw(); DurationChanged?.Invoke(_length);
        };
        _canvas.PointerPressed += PointerPressedOnCanvas;
        _canvas.PointerMoved += PointerMovedOnCanvas;
        _canvas.PointerReleased += PointerReleasedOnCanvas;
        _canvas.PointerCaptureLost += (_, _) => { _scrubbing = false; _dragging = null; Draw(); };
        KeyDown += (_, args) =>
        {
            if (args.OriginalSource is TextBox or NumberBox) return;
            if (args.Key == VirtualKey.Space) { PlayPauseRequested?.Invoke(); args.Handled = true; }
            if (args.Key is VirtualKey.Left or VirtualKey.Right) { SeekRequested?.Invoke(Math.Clamp(_position + (args.Key == VirtualKey.Right ? 1 : -1) / 30d, 0, _length)); args.Handled = true; }
            if (args.Key == VirtualKey.Delete && CanEditKeys) { DeleteKeyRequested?.Invoke(); args.Handled = true; }
        };
        IsTabStop = true; UpdateReadout();
    }

    internal double Duration
    {
        get => _length;
        set { _length = Math.Clamp(value, .1, 36000); _updating = true; _duration.Value = _length; _updating = false; Draw(); UpdateReadout(); }
    }
    internal double Position
    {
        get => _position;
        set { _position = Math.Clamp(value, 0, _length); UpdateReadout(); UpdatePlayhead(); }
    }
    internal bool IsPlaying
    {
        get => _playing;
        set { if (_playing == value) return; _playing = value; _play.Content = Ui.Row(7, new FontIcon { Glyph = value ? "\uE769" : "\uE768", FontSize = 13 }, new TextBlock { Text = value ? "暂停" : "播放" }); }
    }
    internal bool CanEditKeys { get => _editing.IsHitTestVisible; set { _editing.IsHitTestVisible = value; _editing.Opacity = value ? 1 : .35; foreach (var button in _editing.Children.OfType<Button>()) button.IsEnabled = value; } }
    internal void SetTracks(IEnumerable<StudioTimelineTrack> tracks) { _tracks = tracks.ToArray(); if (!_tracks.SelectMany(t => t.Markers).Any(m => m.Id == _selectedMarker)) _selectedMarker = null; Draw(); }
    private double PixelsPerSecond => Math.Max(_zoom.Value, Math.Max(1, _scroll.ActualWidth - LabelWidth - 20) / _length);
    private double At(double time) => LabelWidth + time * PixelsPerSecond;
    private double From(double x) => Math.Clamp(Math.Round((x - LabelWidth) / PixelsPerSecond * 30) / 30, 0, _length);
    private void UpdateReadout() => _time.Text = $"{_position:0.00} / {_length:0.00} s";
    private void UpdatePlayhead()
    {
        _playhead.X1 = _playhead.X2 = At(_position); _playhead.Y1 = 0; _playhead.Y2 = _canvas.Height;
    }
    private void Draw()
    {
        if (_scroll.ActualWidth <= 0) return;
        _canvas.Children.Clear();
        _canvas.Width = Math.Max(_scroll.ActualWidth - 14, At(_length) + 24);
        _canvas.Height = Math.Max(80, RulerHeight + Math.Max(2, _tracks.Count) * TrackHeight);
        bool dark = ActualTheme == ElementTheme.Dark;
        var textBrush = new SolidColorBrush(dark ? Color.FromArgb(230, 235, 235, 235) : Color.FromArgb(230, 45, 45, 45));
        var gridBrush = new SolidColorBrush(Color.FromArgb(35, 128, 128, 128));
        double tick = PixelsPerSecond >= 90 ? .5 : PixelsPerSecond >= 40 ? 1 : 2;
        while (_length / tick > 1000) tick *= 2;
        for (double t = 0; t <= _length + .001; t += tick)
        {
            var line = new Line { X1 = At(t), X2 = At(t), Y1 = 18, Y2 = _canvas.Height, Stroke = gridBrush, StrokeThickness = 1, IsHitTestVisible = false };
            _canvas.Children.Add(line);
            var label = new TextBlock { Text = t.ToString("0.#", CultureInfo.InvariantCulture), FontSize = 10, Foreground = textBrush, IsHitTestVisible = false };
            Canvas.SetLeft(label, At(t) + 3); Canvas.SetTop(label, 2); _canvas.Children.Add(label);
        }
        for (int i = 0; i < Math.Max(2, _tracks.Count); i++)
        {
            double top = RulerHeight + i * TrackHeight;
            var background = new Rectangle { Width = _canvas.Width, Height = TrackHeight, Fill = new SolidColorBrush(Color.FromArgb((byte)(i % 2 == 0 ? 10 : 0), 128, 128, 128)), IsHitTestVisible = false };
            Canvas.SetTop(background, top); _canvas.Children.Add(background);
            if (i >= _tracks.Count) continue;
            var track = _tracks[i];
            var label = new TextBlock { Text = track.Label, FontSize = 11, Foreground = textBrush, Width = LabelWidth - 14,
                TextTrimming = TextTrimming.CharacterEllipsis, IsHitTestVisible = false };
            Canvas.SetLeft(label, 9); Canvas.SetTop(label, top + 6); _canvas.Children.Add(label);
            foreach (var marker in track.Markers)
            {
                double time = _dragging?.Id == marker.Id ? _dragTime : marker.Time;
                var diamond = new Border { Width = 10, Height = 10, CornerRadius = new(1), Background = new SolidColorBrush(
                    marker.Kind == "motion" ? Color.FromArgb(255, 193, 139, 233) : Color.FromArgb(255, 59, 153, 224)),
                    BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(255, 16, 45, 80)),
                    BorderThickness = new(marker.Id == _selectedMarker ? 2 : 0),
                    RenderTransform = new RotateTransform { Angle = 45, CenterX = 5, CenterY = 5 }, Tag = marker };
                ToolTipService.SetToolTip(diamond, $"{marker.Label} · {time:0.00} s"); AutomationProperties.SetName(diamond, $"{track.Label} {marker.Label} {time:0.00} 秒");
                Canvas.SetLeft(diamond, At(time) - 5); Canvas.SetTop(diamond, top + 9); _canvas.Children.Add(diamond);
            }
        }
        _canvas.Children.Add(_playhead); UpdatePlayhead();
    }
    private void PointerPressedOnCanvas(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(_canvas); if (!point.Properties.IsLeftButtonPressed || point.Position.X < LabelWidth) return;
        Focus(FocusState.Pointer);
        _dragging = (args.OriginalSource as FrameworkElement)?.Tag as StudioTimelineMarker;
        if (_dragging is not null) { _dragTime = _dragging.Time; _selectedMarker = _dragging.Id; MarkerSelected?.Invoke(_dragging); Draw(); if (!CanEditKeys) { _dragging = null; args.Handled = true; return; } }
        else { _scrubbing = true; SeekRequested?.Invoke(From(point.Position.X)); }
        _canvas.CapturePointer(args.Pointer); args.Handled = true;
    }
    private void PointerMovedOnCanvas(object sender, PointerRoutedEventArgs args)
    {
        double time = From(args.GetCurrentPoint(_canvas).Position.X);
        if (_dragging is not null) { _dragTime = time; Draw(); args.Handled = true; }
        else if (_scrubbing) { SeekRequested?.Invoke(time); args.Handled = true; }
    }
    private void PointerReleasedOnCanvas(object sender, PointerRoutedEventArgs args)
    {
        var marker = _dragging; double time = _dragTime; _dragging = null; _scrubbing = false;
        _canvas.ReleasePointerCapture(args.Pointer);
        if (marker is not null && Math.Abs(time - marker.Time) > .0001) MarkerMoved?.Invoke(marker, time);
        Draw(); args.Handled = true;
    }
}
