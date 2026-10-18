using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OurNotes.Director.Desktop.Core;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace OurNotes.Director.WinUI;

/// <summary>Docked workspaces with independent saved splitters and reversible panel maximization.</summary>
internal sealed partial class StudioLayout : UserControl
{
    sealed class Splitter : Thumb
    {
        internal Splitter(bool horizontal, string hint)
        {
            HorizontalAlignment = HorizontalAlignment.Stretch; VerticalAlignment = VerticalAlignment.Stretch;
            Background = new SolidColorBrush(Color.FromArgb(30, 128, 128, 128));
            ProtectedCursor = InputSystemCursor.Create(horizontal ? InputSystemCursorShape.SizeWestEast : InputSystemCursorShape.SizeNorthSouth);
            ToolTipService.SetToolTip(this, hint); AutomationProperties.SetName(this, hint);
        }
    }

    readonly Grid dock = new();
    readonly Grid top = new();
    readonly Grid bottom = new();
    readonly Border commandFrame;
    readonly FrameworkElement commandBar;
    readonly List<WorkspacePanel> panels = [];
    readonly Splitter topSplitter, bottomSplitter, rowSplitter;
    readonly bool dual;
    WorkspaceLayoutSettings proportions;
    WorkspacePanel? maximized, active;
    internal string WorkspaceKey { get; }
    internal IReadOnlyList<WorkspacePanel> Panels => panels;
    internal WorkspacePanel EffectControlsPanel => panels.First(panel => panel.Id == "effects");
    internal WorkspacePanel ProgramMonitorPanel => panels.First(panel => panel.Id == "monitor");
    internal WorkspacePanel ProjectPanel => panels.First(panel => panel.Id == "project");
    internal WorkspacePanel TimelinePanel => panels.First(panel => panel.Id == "timeline");
    internal WorkspacePanel? MaximizedPanel => maximized;
    internal Button WindowMenuButton { get; }

    internal StudioLayout(FrameworkElement left, FrameworkElement viewport, FrameworkElement right,
        FrameworkElement timeline, FrameworkElement toolbar, string? workspaceKey = null)
        : this(viewport, right, toolbar, workspaceKey ?? MainWindow.Current?.ActivePage ?? "scenes", left, timeline) { }

    internal StudioLayout(FrameworkElement viewport, FrameworkElement inspector, FrameworkElement toolbar, string workspaceKey)
        : this(viewport, inspector, toolbar, workspaceKey, null, null) { }

    StudioLayout(FrameworkElement viewport, FrameworkElement inspector, FrameworkElement toolbar, string workspaceKey,
        FrameworkElement? project, FrameworkElement? timeline)
    {
        WorkspaceKey = workspaceKey; dual = project is null;
        WindowMenuButton = StudioMenus.Button("窗口", CreateWindowMenu);
        if (toolbar is Grid toolbarGrid && toolbarGrid.Children.FirstOrDefault() is StackPanel { Orientation: Orientation.Horizontal } menus)
        { menus.Children.Add(WindowMenuButton); commandBar = toolbar; }
        else
        {
            var commands = new Grid { ColumnSpacing = 4 };
            commands.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); commands.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            commands.Children.Add(toolbar); commands.Children.Add(WindowMenuButton); Grid.SetColumn(WindowMenuButton, 1); commandBar = commands;
        }
        proportions = (MainWindow.Current?.Settings.WorkspaceLayouts.GetValueOrDefault(workspaceKey)
            ?? WorkspaceLayoutSettings.DefaultFor(workspaceKey)).Normalize(workspaceKey);
        HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Stretch;
        var root = new Grid();
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        commandFrame = Panel(commandBar, 3); root.Children.Add(commandFrame);
        root.Children.Add(dock); Grid.SetRow(dock, 1);
        dock.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        dock.RowDefinitions.Add(new() { Height = dual ? new(0) : new(5) });
        dock.RowDefinitions.Add(new() { Height = dual ? new(0) : new(1, GridUnitType.Star) });
        foreach (var row in new[] { top, bottom })
        {
            row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = new(5) });
            row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        }
        topSplitter = new(true, "拖动调整上方两个面板的宽度");
        bottomSplitter = new(true, "拖动调整项目素材和时间轴的宽度");
        rowSplitter = new(false, "拖动调整监视器和时间轴的高度");
        top.Children.Add(topSplitter); Grid.SetColumn(topSplitter, 1);
        bottom.Children.Add(bottomSplitter); Grid.SetColumn(bottomSplitter, 1);
        dock.Children.Add(top); dock.Children.Add(rowSplitter); Grid.SetRow(rowSplitter, 1);
        dock.Children.Add(bottom); Grid.SetRow(bottom, 2);

        var effects = AddPanel("effects", dual ? "模型检查器" : "效果控件", inspector, 6);
        var monitor = AddPanel("monitor", dual ? "模型监视器" : "节目监视器", viewport, 0);
        top.Children.Add(effects); Grid.SetColumn(effects, dual ? 2 : 0);
        top.Children.Add(monitor); Grid.SetColumn(monitor, dual ? 0 : 2);
        if (!dual)
        {
            var assets = AddPanel("project", "项目素材", project!, 6);
            var sequence = AddPanel("timeline", "时间轴", timeline!, 0);
            bottom.Children.Add(assets); bottom.Children.Add(sequence); Grid.SetColumn(sequence, 2);
        }
        else { bottom.Visibility = rowSplitter.Visibility = Visibility.Collapsed; }

        topSplitter.DragDelta += (_, args) => ResizeColumns(top, args.HorizontalChange, true);
        bottomSplitter.DragDelta += (_, args) => ResizeColumns(bottom, args.HorizontalChange, false);
        rowSplitter.DragDelta += (_, args) =>
        {
            double available = Math.Max(1, dock.ActualHeight - 5);
            proportions = proportions with { TopHeight = BoundedFraction(available, (top.ActualHeight + args.VerticalChange) / available, 160, 140) };
            ApplyProportions();
        };
        foreach (var splitter in new[] { topSplitter, bottomSplitter, rowSplitter }) splitter.DragCompleted += (_, _) => SaveLayout();
        SizeChanged += (_, _) => ApplyProportions();
        Unloaded += (_, _) => SaveLayout();
        Content = root; SetActive(monitor); ApplyProportions();
    }

    WorkspacePanel AddPanel(string id, string title, FrameworkElement child, double padding)
    {
        var panel = new WorkspacePanel(id, title, child, padding);
        panel.Activated += SetActive; panel.MaximizeRequested += ToggleMaximize;
        panel.ResetLayoutRequested += _ => ResetLayout(); panels.Add(panel); return panel;
    }

    internal FrameworkElement DetachCommandBar()
    { commandFrame.Child = null; commandFrame.Visibility = Visibility.Collapsed; return commandBar; }

    void SetActive(WorkspacePanel panel)
    { active = panel; foreach (var item in panels) item.SetActive(item == panel); }

    internal void FocusPanel(string id)
    {
        var panel = panels.FirstOrDefault(item => item.Id == id); if (panel is null) return;
        if (maximized is not null && maximized != panel) RestorePanels();
        SetActive(panel); panel.FocusContent();
    }
    internal void FocusActivePanel() => FocusPanel(active?.Id ?? "monitor");

    internal void ToggleMaximize(WorkspacePanel? panel = null)
    {
        if (maximized is not null) { RestorePanels(); return; }
        panel ??= active ?? ProgramMonitorPanel; SetActive(panel); maximized = panel;
        foreach (var item in panels) { item.Visibility = item == panel ? Visibility.Visible : Visibility.Collapsed; item.IsMaximized = item == panel; }
        bool inTop = top.Children.Contains(panel);
        var row = inTop ? top : bottom;
        top.Visibility = inTop ? Visibility.Visible : Visibility.Collapsed;
        bottom.Visibility = inTop ? Visibility.Collapsed : Visibility.Visible;
        topSplitter.Visibility = bottomSplitter.Visibility = rowSplitter.Visibility = Visibility.Collapsed;
        Grid.SetRow(row, 0); Grid.SetRowSpan(row, 3); Grid.SetColumn(panel, 0); Grid.SetColumnSpan(panel, 3);
        panel.Focus(FocusState.Programmatic);
    }

    internal void RestorePanels()
    {
        maximized = null;
        foreach (var panel in panels) { panel.Visibility = Visibility.Visible; panel.IsMaximized = false; Grid.SetColumnSpan(panel, 1); }
        top.Visibility = topSplitter.Visibility = Visibility.Visible;
        bottom.Visibility = bottomSplitter.Visibility = rowSplitter.Visibility = dual ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRow(top, 0); Grid.SetRowSpan(top, 1); Grid.SetRow(bottom, 2); Grid.SetRowSpan(bottom, 1);
        Grid.SetColumn(EffectControlsPanel, dual ? 2 : 0); Grid.SetColumn(ProgramMonitorPanel, dual ? 0 : 2);
        if (!dual) { Grid.SetColumn(ProjectPanel, 0); Grid.SetColumn(TimelinePanel, 2); }
        ApplyProportions();
    }

    internal void ResetLayout()
    {
        proportions = WorkspaceLayoutSettings.DefaultFor(WorkspaceKey); RestorePanels(); SaveLayout(); FocusPanel("monitor");
        MainWindow.Current?.SetStatus("已恢复当前工作区布局");
    }

    internal bool HandleShortcut(KeyRoutedEventArgs args)
    {
        if (args.Handled || args.Key != (VirtualKey)192 || IsKeyDown(VirtualKey.Control) || IsKeyDown(VirtualKey.Menu)) return false;
        for (var element = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
            if (element is TextBox or RichEditBox or AutoSuggestBox or NumberBox) return false;
        var target = IsKeyDown(VirtualKey.Shift) ? active : panels.FirstOrDefault(panel => panel.IsPointerInside) ?? active;
        ToggleMaximize(target); args.Handled = true; return true;
    }

    static bool IsKeyDown(VirtualKey key) => (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;

    internal MenuFlyout CreateWindowMenu()
    {
        var menu = new MenuFlyout();
        foreach (var panel in panels)
        {
            var item = new ToggleMenuFlyoutItem { Text = panel.Title, IsChecked = panel == active };
            item.Click += (_, _) => FocusPanel(panel.Id); menu.Items.Add(item);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(StudioMenus.Item(maximized is null ? "最大化当前面板" : "恢复面板大小", () => ToggleMaximize(), shortcut: "Shift+`"));
        menu.Items.Add(StudioMenus.Item("恢复工作区布局", ResetLayout));
        return menu;
    }

    void ResizeColumns(Grid row, double change, bool isTop)
    {
        double available = Math.Max(1, row.ActualWidth - 5);
        double fraction = BoundedFraction(available, (row.ColumnDefinitions[0].ActualWidth + change) / available, dual ? 280 : 210, dual ? 240 : 280);
        proportions = isTop ? proportions with { TopLeft = fraction } : proportions with { BottomLeft = fraction };
        ApplyProportions();
    }

    static double BoundedFraction(double available, double fraction, double minimumFirst, double minimumSecond)
    {
        if (available <= 0) return fraction;
        double first = Math.Min(minimumFirst, available * .45), second = Math.Min(minimumSecond, available * .45);
        return Math.Clamp(fraction, first / available, 1 - second / available);
    }

    void ApplyProportions()
    {
        void Columns(Grid row, double fraction)
        {
            fraction = BoundedFraction(Math.Max(0, ActualWidth - 5), fraction, dual ? 280 : 210, dual ? 240 : 280);
            row.ColumnDefinitions[0].Width = new(fraction, GridUnitType.Star);
            row.ColumnDefinitions[2].Width = new(1 - fraction, GridUnitType.Star);
        }
        Columns(top, proportions.TopLeft); Columns(bottom, proportions.BottomLeft);
        if (!dual)
        {
            double fraction = BoundedFraction(Math.Max(0, dock.ActualHeight - 5), proportions.TopHeight, 160, 140);
            dock.RowDefinitions[0].Height = new(fraction, GridUnitType.Star);
            dock.RowDefinitions[2].Height = new(1 - fraction, GridUnitType.Star);
        }
    }

    internal void SaveLayout()
    {
        if (MainWindow.Current is not { } window) return;
        window.Settings.WorkspaceLayouts[WorkspaceKey] = proportions.Normalize(WorkspaceKey); window.SaveSettings();
    }

    internal static Border Panel(UIElement child, double padding = 8) => new()
    { Child = child, Padding = new(padding), Style = (Style)Application.Current.Resources["StudioPanelStyle"] };

    internal static TextBlock Section(string title) => new()
    { Text = title, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new(0, 2, 0, 4), VerticalAlignment = VerticalAlignment.Center };

    internal static Button Tool(string label, Action action, string? glyph = null)
    { var button = Ui.Button(label, action, glyph); button.Padding = new(8, 3, 8, 3); button.FontSize = 12; button.MinHeight = 26; return button; }
    internal static Button AsyncTool(string label, Func<Task> action, string? glyph = null, bool accent = false)
    { var button = Ui.AsyncButton(label, action, glyph, accent); button.Padding = new(8, 3, 8, 3); button.FontSize = 12; button.MinHeight = 26; return button; }

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
