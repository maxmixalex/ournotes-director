using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace OurNotes.Director.WinUI;

internal static class Ui
{
    public static TextBlock Text(string text, double size = 14, bool muted = false, bool selectable = false) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Opacity = muted ? .68 : 1,
        IsTextSelectionEnabled = selectable
    };

    public static StackPanel Stack(double spacing = 12, params UIElement[] children)
    {
        var panel = new StackPanel { Spacing = spacing };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    public static StackPanel Row(double spacing = 8, params UIElement[] children)
    {
        var row = Stack(spacing, children); row.Orientation = Orientation.Horizontal; return row;
    }

    public static Button Button(string label, Action click, string? icon = null, bool accent = false)
    {
        var button = new Button { Padding = new Thickness(14, 9, 14, 9) };
        button.Content = icon is null ? label : Row(8, new FontIcon { Glyph = icon, FontSize = 15 }, new TextBlock { Text = label });
        if (accent && Application.Current.Resources.TryGetValue("AccentButtonStyle", out var style)) button.Style = (Style)style;
        button.Click += async (_, _) =>
        {
            try { click(); }
            catch (Exception error) { if (MainWindow.Current is { } window) await window.ShowErrorAsync(error.Message); }
        };
        AutomationProperties.SetName(button, label);
        return button;
    }

    public static Button AsyncButton(string label, Func<Task> click, string? icon = null, bool accent = false)
    {
        var button = Button(label, () => { }, icon, accent);
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await click(); }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (MainWindow.Current is { } window) await window.ShowErrorAsync(error.Message); }
            finally { button.IsEnabled = true; }
        };
        return button;
    }

    public static Border Card(UIElement child, double padding = 20) => new()
    {
        Child = child, Padding = new Thickness(padding), CornerRadius = new CornerRadius(8),
        BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128))
    };

    public static StackPanel Heading(string title, string description) => Stack(7,
        new TextBlock { Text = title, FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap },
        Text(description, 14, true));

    public static ScrollViewer Scroll(UIElement content) => new()
    {
        Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch
    };

    public static TextBox Input(string label, string value = "", string? placeholder = null)
    {
        var box = new TextBox { Header = label, Text = value, PlaceholderText = placeholder ?? "", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(box, label); return box;
    }

    public static ComboBox Choice(string label, IEnumerable<(string Value, string Label)> choices, string selected = "")
    {
        var box = new ComboBox { Header = label, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var choice in choices)
        {
            var item = new ComboBoxItem { Content = choice.Label, Tag = choice.Value };
            box.Items.Add(item); if (choice.Value == selected) box.SelectedItem = item;
        }
        if (box.SelectedIndex < 0 && box.Items.Count > 0) box.SelectedIndex = 0;
        AutomationProperties.SetName(box, label); return box;
    }

    public static string Selected(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";

    public static FrameworkElement Columns(FrameworkElement left, FrameworkElement right, double leftWeight = 1, double rightWeight = 1)
    {
        var grid = new Grid { ColumnSpacing = 20, RowSpacing = 20 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(leftWeight, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(rightWeight, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(left); grid.Children.Add(right); Grid.SetColumn(right, 1);
        grid.SizeChanged += (_, e) =>
        {
            bool narrow = e.NewSize.Width < 760;
            Grid.SetColumn(right, narrow ? 0 : 1); Grid.SetRow(right, narrow ? 1 : 0);
            Grid.SetColumnSpan(left, narrow ? 2 : 1); Grid.SetColumnSpan(right, narrow ? 2 : 1);
        };
        return grid;
    }
}
