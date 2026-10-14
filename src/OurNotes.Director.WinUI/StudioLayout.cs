using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace OurNotes.Director.WinUI;

/// <summary>Editor chrome: calm docked surfaces, independently resizable side inspectors and timeline.</summary>
internal sealed class StudioLayout : UserControl
{
    internal StudioLayout(FrameworkElement left, FrameworkElement viewport, FrameworkElement right,
        FrameworkElement timeline, FrameworkElement toolbar)
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Stretch;
        var root = new Grid();
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star), MinHeight = 160 });
        root.RowDefinitions.Add(new() { Height = new(5) });
        root.RowDefinitions.Add(new() { Height = new(220), MinHeight = 138 });
        root.Children.Add(Panel(toolbar, 6));
        var body = new Grid();
        body.ColumnDefinitions.Add(new() { Width = new(214), MinWidth = 156 });
        body.ColumnDefinitions.Add(new() { Width = new(5) });
        body.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star), MinWidth = 200 });
        body.ColumnDefinitions.Add(new() { Width = new(5) });
        body.ColumnDefinitions.Add(new() { Width = new(290), MinWidth = 208 });
        var leftFrame = Panel(left); var rightFrame = Panel(right);
        body.Children.Add(leftFrame); body.Children.Add(viewport); body.Children.Add(rightFrame);
        Grid.SetColumn(viewport, 2); Grid.SetColumn(rightFrame, 4); Grid.SetRow(body, 1); root.Children.Add(body);
        var timelineFrame = Panel(timeline, 0); Grid.SetRow(timelineFrame, 3); root.Children.Add(timelineFrame);
        bool resizedBottom = false, resizedLeft = false, resizedRight = false;
        Thumb Grip(string hint)
        {
            var thumb = new Thumb { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
                Background = new SolidColorBrush(Color.FromArgb(14, 128, 128, 128)) };
            ToolTipService.SetToolTip(thumb, hint); return thumb;
        }
        var bottom = Grip("拖动调整编排区高度");
        bottom.DragDelta += (_, args) => { resizedBottom = true; root.RowDefinitions[3].Height = new(Math.Clamp(root.RowDefinitions[3].ActualHeight - args.VerticalChange, 138, Math.Max(138, ActualHeight - 240))); };
        Grid.SetRow(bottom, 2); root.Children.Add(bottom);
        var leftGrip = Grip("拖动调整对象栏宽度"); Grid.SetColumn(leftGrip, 1); body.Children.Add(leftGrip);
        leftGrip.DragDelta += (_, args) => { resizedLeft = true; body.ColumnDefinitions[0].Width = new(Math.Clamp(body.ColumnDefinitions[0].ActualWidth + args.HorizontalChange, 156, Math.Max(156, ActualWidth - body.ColumnDefinitions[4].ActualWidth - 280))); };
        var rightGrip = Grip("拖动调整检查器宽度"); Grid.SetColumn(rightGrip, 3); body.Children.Add(rightGrip);
        rightGrip.DragDelta += (_, args) => { resizedRight = true; body.ColumnDefinitions[4].Width = new(Math.Clamp(body.ColumnDefinitions[4].ActualWidth - args.HorizontalChange, 208, Math.Max(208, ActualWidth - body.ColumnDefinitions[0].ActualWidth - 280))); };
        Content = root;
        SizeChanged += (_, args) =>
        {
            bool compact = args.NewSize.Width < 1100;
            if (!resizedLeft) body.ColumnDefinitions[0].Width = new(compact ? 176 : 214);
            if (!resizedRight) body.ColumnDefinitions[4].Width = new(compact ? 246 : 290);
            if (!resizedBottom) root.RowDefinitions[3].Height = new(args.NewSize.Height < 650 ? 180 : 230);
        };
    }
    internal static Border Panel(UIElement child, double padding = 12) => new()
    {
        Child = child, Padding = new(padding),
        Style = (Style)Application.Current.Resources["StudioPanelStyle"]
    };
    internal static TextBlock Section(string title) => new()
    {
        Text = title, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Margin = new(0, 2, 0, 6), VerticalAlignment = VerticalAlignment.Center
    };
    internal static Button Tool(string label, Action action, string? glyph = null)
    { var button = Ui.Button(label, action, glyph); button.Padding = new(9, 5, 9, 5); return button; }
    internal static Button AsyncTool(string label, Func<Task> action, string? glyph = null, bool accent = false)
    { var button = Ui.AsyncButton(label, action, glyph, accent); button.Padding = new(9, 5, 9, 5); return button; }

    internal static void PairFields(StackPanel panel, NumberBox first, NumberBox second)
    {
        int index = panel.Children.IndexOf(first); if (index < 0 || !panel.Children.Contains(second)) return;
        panel.Children.Remove(first); panel.Children.Remove(second);
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        row.Children.Add(first); row.Children.Add(second); Grid.SetColumn(second, 1);
        first.SpinButtonPlacementMode = second.SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden;
        panel.Children.Insert(index, row);
    }
}
