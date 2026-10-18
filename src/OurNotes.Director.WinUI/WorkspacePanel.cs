using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace OurNotes.Director.WinUI;

/// <summary>A docked editor panel. Switching tabs preserves the controls and their editing state.</summary>
internal sealed class WorkspacePanel : UserControl
{
    sealed record PanelTab(string Id, string Title, FrameworkElement Element, Button Button);
    readonly Border frame = new();
    readonly StackPanel tabs = new() { Orientation = Orientation.Horizontal, Spacing = 0 };
    readonly Grid pages = new();
    readonly Border toolbar = new() { Visibility = Visibility.Collapsed, Padding = new(6, 3, 6, 3) };
    readonly List<PanelTab> entries = [];
    string selectedTabId = "";
    bool active;
    WeakReference<Control>? lastFocus;

    internal string Id { get; }
    internal string Title => entries.FirstOrDefault(tab => tab.Id == selectedTabId)?.Title ?? Id;
    internal string SelectedTabId => selectedTabId;
    internal FrameworkElement SelectedContent => entries.First(tab => tab.Id == selectedTabId).Element;
    internal bool IsActive => active;
    internal bool IsPointerInside { get; private set; }
    internal bool IsMaximized { get; set; }
    internal event Action<WorkspacePanel>? Activated;
    internal event Action<WorkspacePanel>? MaximizeRequested;
    internal event Action<WorkspacePanel>? ResetLayoutRequested;
    internal event Action<WorkspacePanel>? TabSelected;

    internal WorkspacePanel(string id, string title, FrameworkElement content, double padding = 8)
    {
        Id = id; HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Stretch;
        IsTabStop = true; UseSystemFocusVisuals = false;
        AutomationProperties.SetName(this, title + "面板");
        var root = new Grid();
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        var header = new Grid();
        header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var tabScroll = new ScrollViewer { Content = tabs, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled };
        header.Children.Add(tabScroll);
        var menu = new Button { Content = new FontIcon { Glyph = "\uE700", FontSize = 10 }, Width = 27,
            Style = (Style)Application.Current.Resources["WorkspaceTabStyle"], Padding = new(4), MinHeight = 26 };
        AutomationProperties.SetName(menu, title + "面板菜单");
        ToolTipService.SetToolTip(menu, "面板菜单 · 双击标签或按 ` 最大化");
        menu.Click += (_, _) => { Activated?.Invoke(this); CreateMenu().ShowAt(menu); };
        header.Children.Add(menu); Grid.SetColumn(menu, 1);
        var headerFrame = new Border { Child = header, Style = (Style)Application.Current.Resources["WorkspaceHeaderStyle"] };
        headerFrame.DoubleTapped += (_, args) => { Activated?.Invoke(this); MaximizeRequested?.Invoke(this); args.Handled = true; };
        root.Children.Add(headerFrame);
        root.Children.Add(toolbar); Grid.SetRow(toolbar, 1);
        pages.Margin = new(padding); root.Children.Add(pages); Grid.SetRow(pages, 2);
        frame.Child = root; Content = frame; SetActive(false);
        AddTab(id, title, content, true);
        AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => Activated?.Invoke(this)), true);
        GotFocus += (_, args) =>
        {
            for (var element = args.OriginalSource as DependencyObject; element is not null && element != this; element = VisualTreeHelper.GetParent(element))
                if (element is Control control) { lastFocus = new(control); break; }
            Activated?.Invoke(this);
        };
        PointerEntered += (_, _) => IsPointerInside = true;
        PointerExited += (_, _) => IsPointerInside = false;
    }

    internal void AddTab(string id, string title, FrameworkElement content, bool select = false)
    {
        if (entries.Any(tab => tab.Id == id)) throw new ArgumentException("面板标签已经存在：" + id, nameof(id));
        var button = new Button { Content = title, Style = (Style)Application.Current.Resources["WorkspaceTabStyle"] };
        AutomationProperties.SetName(button, title);
        button.Click += (_, _) => { SelectTab(id); Activated?.Invoke(this); };
        tabs.Children.Add(button); pages.Children.Add(content); entries.Add(new(id, title, content, button));
        content.Visibility = Visibility.Collapsed;
        if (select || selectedTabId.Length == 0) SelectTab(id);
    }

    internal bool SelectTab(string id)
    {
        if (!entries.Any(tab => tab.Id == id)) return false;
        selectedTabId = id;
        foreach (var tab in entries)
        {
            bool selected = tab.Id == id;
            tab.Element.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            tab.Button.Style = (Style)Application.Current.Resources[selected ? "WorkspaceSelectedTabStyle" : "WorkspaceTabStyle"];
        }
        TabSelected?.Invoke(this); return true;
    }

    internal void SetToolbar(FrameworkElement? tools)
    { toolbar.Child = tools; toolbar.Visibility = tools is null ? Visibility.Collapsed : Visibility.Visible; }

    internal void SetActive(bool value)
    {
        active = value;
        frame.Style = (Style)Application.Current.Resources[value ? "WorkspacePanelActiveStyle" : "WorkspacePanelFrameStyle"];
    }

    internal void FocusContent()
    {
        // NativeViewport and ClipTimeline own their keyboard input; preserve that route.
        static Control? Focusable(DependencyObject parent)
        {
            if (parent is Control { IsTabStop: true, IsEnabled: true, Visibility: Visibility.Visible } control) return control;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                if (Focusable(VisualTreeHelper.GetChild(parent, i)) is { } child) return child;
            return null;
        }
        Activated?.Invoke(this);
        if (lastFocus is not null && lastFocus.TryGetTarget(out var previous) && previous.IsLoaded && previous.IsEnabled)
            for (DependencyObject? ancestor = previous; ancestor is not null && ancestor is not WorkspacePanel; ancestor = VisualTreeHelper.GetParent(ancestor))
                if (ReferenceEquals(ancestor, SelectedContent) && previous.Focus(FocusState.Programmatic)) return;
        if (Focusable(SelectedContent) is { } focus) focus.Focus(FocusState.Programmatic);
        else Focus(FocusState.Programmatic);
    }

    MenuFlyout CreateMenu()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(StudioMenus.Item(IsMaximized ? "恢复面板大小" : "最大化此面板", () => MaximizeRequested?.Invoke(this), shortcut: "`"));
        if (entries.Count > 1)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            foreach (var tab in entries)
            {
                var item = new ToggleMenuFlyoutItem { Text = tab.Title, IsChecked = tab.Id == selectedTabId };
                item.Click += (_, _) => SelectTab(tab.Id); menu.Items.Add(item);
            }
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(StudioMenus.Item("恢复工作区布局", () => ResetLayoutRequested?.Invoke(this)));
        return menu;
    }
}
