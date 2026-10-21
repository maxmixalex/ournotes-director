using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace OurNotes.Director.WinUI;

internal sealed partial class HomeStudioPage
{
    readonly ContentControl homeProjectItems = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    readonly ToggleButton homeObjectsTab = new() { Content = "主页角色", Padding = new(8, 3, 8, 3), FontSize = 12, MinWidth = 0 };
    readonly ToggleButton homeMotionsTab = new() { Content = "动作素材", Padding = new(8, 3, 8, 3), FontSize = 12, MinWidth = 0 };

    FrameworkElement BuildHomeProjectLibrary()
    {
        var grid = new Grid { RowSpacing = 5 };
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new Grid(); header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(Ui.Row(2, homeObjectsTab, homeMotionsTab));
        var choose = StudioLayout.AsyncTool("", () => SafeAsync(ChooseHomeAsync), "\uE8B7"); ToolTipService.SetToolTip(choose, "选择主页…");
        header.Children.Add(choose); Grid.SetColumn(choose, 1); grid.Children.Add(header);
        animationSearch.MinHeight = 28; animationSearch.FontSize = 12; animationSearch.Padding = new(6, 3, 6, 3);
        grid.Children.Add(animationSearch); Grid.SetRow(animationSearch, 1);
        grid.Children.Add(homeProjectItems); Grid.SetRow(homeProjectItems, 2);
        animationHint.TextWrapping = TextWrapping.NoWrap; animationHint.TextTrimming = TextTrimming.CharacterEllipsis;
        grid.Children.Add(animationHint); Grid.SetRow(animationHint, 3);
        homeObjectsTab.Click += (_, _) => ShowHomeObjects(); homeMotionsTab.Click += (_, _) => ShowHomeAnimations();
        ShowHomeObjects(); return grid;
    }
    void ShowHomeObjects()
    {
        homeObjectsTab.IsChecked = true; homeMotionsTab.IsChecked = false; homeProjectItems.Content = objects; animationSearch.Visibility = Visibility.Collapsed;
    }
    void ShowHomeAnimations()
    {
        homeObjectsTab.IsChecked = false; homeMotionsTab.IsChecked = true; homeProjectItems.Content = animationLibrary; animationSearch.Visibility = Visibility.Visible;
        RefreshAnimationLibrary(); animationLibrary.Focus(FocusState.Programmatic);
    }
    static bool HomeTextInput(DependencyObject? node)
    {
        for (; node is not null; node = VisualTreeHelper.GetParent(node)) if (node is TextBox or NumberBox or PasswordBox) return true;
        return false;
    }
}
