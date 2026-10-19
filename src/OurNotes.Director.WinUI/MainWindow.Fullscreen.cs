using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace OurNotes.Director.WinUI;

public sealed partial class MainWindow
{
    StudioLayout? fullscreenLayout;
    WorkspacePanel? priorMaximizedPanel;
    AppWindowPresenter? priorPresenter;
    readonly Dictionary<UIElement, Visibility> fullscreenHidden = new();
    internal bool IsMonitorFullscreen => fullscreenLayout is not null;

    void OnMonitorFullscreenKey(object sender, KeyRoutedEventArgs args)
    {
        if (args.Handled || dialogOpen || drawer.Visibility == Visibility.Visible
            || VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot).Any(p => p.IsOpen && p.Child is { } child && !Descendants<ToolTip>(child).Any())) return;
        if (args.Key is not (VirtualKey.F1 or VirtualKey.Escape)) return;
        if (new[] { VirtualKey.Control, VirtualKey.Menu, VirtualKey.Shift }.Any(key =>
            (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0)) return;
        if (!IsMonitorFullscreen && (args.Key != VirtualKey.F1 || !MonitorOwnsFocus())) return;
        args.Handled = true;
        if (args.KeyStatus.WasKeyDown) return;
        try { if (IsMonitorFullscreen) ExitMonitorFullscreen(); else EnterMonitorFullscreen(); }
        catch (Exception error) { ExitMonitorFullscreen(); SetStatus("无法切换节目监视器全屏：" + error.Message); }
    }

    bool MonitorOwnsFocus()
    {
        var monitor = ActiveLayout?.ProgramMonitorPanel;
        for (var element = FocusManager.GetFocusedElement(root.XamlRoot) as DependencyObject;
            element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is TextBox or NumberBox or PasswordBox) return false;
            if (ReferenceEquals(element, monitor)) return true;
        }
        return false;
    }

    void EnterMonitorFullscreen()
    {
        if (fullscreenLayout is not null || ActiveLayout is not { } layout) return;
        priorMaximizedPanel = layout.MaximizedPanel; priorPresenter = AppWindow.Presenter;
        fullscreenLayout = layout;
        // Only visibility and grid spans change. In particular, never remove and
        // reparent a live viewport: that would unload its render/seek session.
        layout.RestorePanels(); layout.ToggleMaximize(layout.ProgramMonitorPanel);
        layout.ProgramMonitorPanel.SetImmersive(true);
        foreach (var child in root.Children)
            if (!ReferenceEquals(child, content)) { fullscreenHidden.Add(child, child.Visibility); child.Visibility = Visibility.Collapsed; }
        Grid.SetRow(content, 0); Grid.SetRowSpan(content, root.RowDefinitions.Count);
        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        layout.ProgramMonitorPanel.FocusContent();
    }

    internal void ExitMonitorFullscreen()
    {
        if (fullscreenLayout is not { } layout) return;
        fullscreenLayout = null;
        layout.ProgramMonitorPanel.SetImmersive(false);
        layout.RestorePanels();
        if (priorMaximizedPanel is { } previous) layout.ToggleMaximize(previous);
        Grid.SetRow(content, 2); Grid.SetRowSpan(content, 1);
        foreach (var (element, visibility) in fullscreenHidden) element.Visibility = visibility;
        fullscreenHidden.Clear(); priorMaximizedPanel = null;
        var presenter = priorPresenter; priorPresenter = null;
        if (presenter is not null) AppWindow.SetPresenter(presenter);
        layout.FocusActivePanel();
    }
}
