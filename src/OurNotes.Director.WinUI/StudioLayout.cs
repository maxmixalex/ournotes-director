using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace OurNotes.Director.WinUI;

/// <summary>The two studios share the same editing surface and panel proportions.</summary>
internal sealed class StudioLayout : UserControl
{
    internal StudioLayout(FrameworkElement left, FrameworkElement viewport, FrameworkElement right,
        FrameworkElement timeline, FrameworkElement toolbar)
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        var root = new Grid { RowSpacing = 4 };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star), MinHeight = 160 });
        root.RowDefinitions.Add(new() { Height = new(8) });
        root.RowDefinitions.Add(new() { Height = new(238), MinHeight = 144 });
        root.Children.Add(toolbar);
        var body = new Grid { ColumnSpacing = 8 };
        body.ColumnDefinitions.Add(new() { Width = new(194), MinWidth = 160 });
        body.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star), MinWidth = 240 });
        body.ColumnDefinitions.Add(new() { Width = new(250), MinWidth = 210 });
        var leftFrame = Panel(left); var rightFrame = Panel(right);
        body.Children.Add(leftFrame); body.Children.Add(viewport); body.Children.Add(rightFrame);
        Grid.SetColumn(viewport, 1); Grid.SetColumn(rightFrame, 2); Grid.SetRow(body, 1); root.Children.Add(body);
        var timelineFrame = Panel(timeline, 0);
        Grid.SetRow(timelineFrame, 3); root.Children.Add(timelineFrame);
        var resize = new Thumb { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Color.FromArgb(24, 128, 128, 128)) };
        ToolTipService.SetToolTip(resize, "上下拖动，调整编排区域高度");
        bool resized = false;
        resize.DragDelta += (_, args) =>
        {
            resized = true;
            root.RowDefinitions[3].Height = new(Math.Clamp(root.RowDefinitions[3].ActualHeight - args.VerticalChange, 144, Math.Max(144, ActualHeight - 240)));
        };
        Grid.SetRow(resize, 2); root.Children.Add(resize);
        Content = root;
        SizeChanged += (_, args) =>
        {
            bool compact = args.NewSize.Width < 1100;
            body.ColumnDefinitions[0].Width = new(compact ? 170 : 206);
            body.ColumnDefinitions[2].Width = new(compact ? 218 : 260);
            if (!resized) root.RowDefinitions[3].Height = new(args.NewSize.Height < 650 ? 190 : 260);
        };
    }

    internal static Border Panel(UIElement child, double padding = 12) => new()
    {
        Child = child, Padding = new(padding), CornerRadius = new(6), BorderThickness = new(1),
        BorderBrush = new SolidColorBrush(Color.FromArgb(38, 128, 128, 128))
    };

    internal static TextBlock Section(string title) => new()
    {
        Text = title, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Margin = new(0, 2, 0, 8)
    };

    internal static Button Tool(string label, Action action, string? glyph = null)
    {
        var button = Ui.Button(label, action, glyph); button.Padding = new(10, 6, 10, 6); return button;
    }

    internal static Button AsyncTool(string label, Func<Task> action, string? glyph = null, bool accent = false)
    {
        var button = Ui.AsyncButton(label, action, glyph, accent); button.Padding = new(10, 6, 10, 6); return button;
    }
}
