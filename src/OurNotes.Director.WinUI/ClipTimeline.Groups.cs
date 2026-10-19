using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.WinUI;

internal sealed partial class ClipTimeline
{
    readonly HashSet<string> collapsedGroups = new(StringComparer.Ordinal);
    readonly Slider heightSlider = new() { Minimum = 24, Maximum = 108, Value = 36, StepFrequency = 2, Width = 180 };
    bool heightLoaded;
    internal string ViewKey { get; } = MainWindow.Current?.ActivePage ?? "scenes";
    static string GroupOf(TimelineLane lane) => lane.Actor.Length > 0 ? "actor:" + lane.Actor : "scene:" + lane.Kind;
    static string GroupOf(TimelineItem clip) => clip.Actor.Length > 0 ? "actor:" + clip.Actor : "scene:" + clip.Kind;
    FrameworkElement CreateTrackHeader()
    {
        var heightButton = new Button { Content = "行高", FontSize = 11, MinWidth = 0, MinHeight = 26, Padding = new(5, 2, 5, 2) };
        AutomationProperties.SetName(heightSlider, "时间轴轨道高度");
        ToolTipService.SetToolTip(heightButton, "纵向缩放轨道 · Ctrl+Shift+滚轮");
        var presets = Ui.Row(4, StudioLayout.Tool("紧凑", () => SetRowHeight(24)),
            StudioLayout.Tool("标准", () => SetRowHeight(36)), StudioLayout.Tool("详细", () => SetRowHeight(72)));
        var flyout = new Flyout { Content = Ui.Stack(8, Ui.Text("轨道高度", 12), heightSlider, presets) };
        heightButton.Flyout = flyout;
        flyout.Closed += (_, _) => MainWindow.Current?.SaveSettings();
        heightSlider.ValueChanged += (_, args) => SetRowHeight(args.NewValue);
        Loaded += (_, _) =>
        {
            if (heightLoaded) return; heightLoaded = true;
            if (MainWindow.Current?.Settings.Forms.GetValueOrDefault("timeline." + ViewKey)?.GetValueOrDefault("rowHeight") is { } saved
                && double.TryParse(saved, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) SetRowHeight(value);
        };
        var header = new Grid { ColumnSpacing = 2 };
        header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(trackFilter); header.Children.Add(heightButton); Grid.SetColumn(heightButton, 1);
        return header;
    }
    internal void SetRowHeight(double value, double anchor = 0)
    {
        if (!double.IsFinite(value) || dragging is not null) return;
        double next = Math.Clamp(Math.Round(value), 24, 108);
        if (next == rowHeight) return;
        anchor = Math.Clamp(anchor, 0, Math.Max(0, scroll.ViewportHeight));
        double row = (scroll.VerticalOffset + anchor) / rowHeight;
        rowHeight = next;
        if (heightSlider.Value != next) heightSlider.Value = next;
        Draw(); scroll.UpdateLayout(); scroll.ChangeView(null, Math.Max(0, row * next - anchor), null, true); Draw();
        if (MainWindow.Current is { } window)
        {
            string key = "timeline." + ViewKey;
            if (!window.Settings.Forms.TryGetValue(key, out var values)) window.Settings.Forms[key] = values = new(StringComparer.Ordinal);
            values["rowHeight"] = next.ToString(CultureInfo.InvariantCulture);
        }
    }
    IReadOnlyList<TimelineLane> BuildGroupedLanes(IReadOnlyList<TimelineLane> tracks)
    {
        var result = new List<TimelineLane>();
        foreach (var group in tracks.GroupBy(GroupOf))
        {
            var first = group.First();
            string label = first.Actor.Length > 0 ? first.Label.Split(" · ")[0] : KindName(first.Kind);
            var children = group.ToArray();
            result.Add(new(first.Actor, "$group", label, children.SelectMany(l => l.Items).ToArray(), group.Key, true));
            if (!collapsedGroups.Contains(group.Key)) result.AddRange(children.Select(l => l with { GroupKey = group.Key }));
        }
        return result;
    }
    internal void ToggleGroup(string key)
    {
        if (dragging is not null) return;
        int oldRow = lanes.ToList().FindIndex(l => l.IsGroup && l.GroupKey == key);
        if (oldRow < 0) return;
        double offset = oldRow * RowHeight - scroll.VerticalOffset;
        if (!collapsedGroups.Add(key)) collapsedGroups.Remove(key);
        UpdateLanes(); Draw(); scroll.UpdateLayout();
        int nextRow = lanes.ToList().FindIndex(l => l.IsGroup && l.GroupKey == key);
        scroll.ChangeView(null, Math.Max(0, nextRow * RowHeight - offset), null, true); Draw();
    }
    void SetAllGroupsCollapsed(bool collapse)
    {
        if (dragging is not null) return;
        collapsedGroups.Clear();
        if (collapse) foreach (var key in allLanes.Select(GroupOf)) collapsedGroups.Add(key);
        UpdateLanes(); Draw(); scroll.ChangeView(null, 0, null, true);
    }
    void ExpandForClip(TimelineItem clip)
    {
        if (collapsedGroups.Remove(GroupOf(clip))) { UpdateLanes(); Draw(); }
    }
    Button CreateLaneLabel(TimelineLane lane)
    {
        var body = new Grid { ColumnSpacing = 5 };
        body.ColumnDefinitions.Add(new() { Width = new(12) });
        body.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        if (lane.IsGroup) body.Children.Add(new FontIcon { Glyph = collapsedGroups.Contains(lane.GroupKey!) ? "\uE76C" : "\uE70D", FontSize = 10 });
        var label = new TextBlock { Text = lane.IsGroup ? lane.Label : KindName(lane.Kind), FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        if (lane.IsGroup) label.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        body.Children.Add(label); Grid.SetColumn(label, 1);
        var button = new Button { Content = body, Width = 162, Height = RowHeight - 1, MinHeight = 0, MinWidth = 0,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = Brush(lane.IsGroup ? (byte)22 : (byte)0, 128, 128, 128),
            BorderThickness = new(0), Padding = new(lane.IsGroup ? 5 : 15, 0, 5, 0), Tag = lane.GroupKey };
        AutomationProperties.SetName(button, lane.IsGroup ? (collapsedGroups.Contains(lane.GroupKey!) ? "展开 " : "收起 ") + lane.Label : lane.Label);
        ToolTipService.SetToolTip(button, lane.IsGroup ? lane.Label + " · " + lane.Items.Count + " 个片段" : lane.Label + " · 点击添加演出");
        button.Click += (_, _) => { if (lane.IsGroup) ToggleGroup(lane.GroupKey!); else AddRequested?.Invoke(lane.Actor, lane.Kind, position); };
        return button;
    }
    void DrawGroupSummary(TimelineLane lane, int row, double left, double right)
    {
        // Merge occupied screen intervals instead of constructing a control for
        // every hidden clip. A collapsed long story stays cheap to paint.
        double start = double.NaN, end = 0;
        void Segment()
        {
            if (!double.IsFinite(start)) return;
            var line = new Rectangle { Width = Math.Max(2, end - start), Height = 4,
                Fill = Brush(100, 130, 155, 190), IsHitTestVisible = false };
            Canvas.SetLeft(line, start); Canvas.SetTop(line, row * RowHeight + RowHeight / 2 - 2); canvas.Children.Add(line);
        }
        foreach (var item in lane.Items.Where(i => i.Enabled && i.Start * scale < right && i.End * scale > left).OrderBy(i => i.Start))
        {
            double a = Math.Max(left, item.Start * scale), b = Math.Min(right, item.End * scale);
            if (double.IsFinite(start) && a <= end + 2) end = Math.Max(end, b);
            else { Segment(); start = a; end = b; }
        }
        Segment();
    }
}
