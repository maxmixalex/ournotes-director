using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace OurNotes.Director.WinUI;

internal static class StudioMenus
{
    internal static MenuFlyoutItem Item(string title, Action action, string? glyph = null, bool enabled = true, string? shortcut = null)
    {
        var item = new MenuFlyoutItem { Text = title, IsEnabled = enabled };
        if (glyph is not null) item.Icon = new FontIcon { Glyph = glyph };
        if (shortcut is not null) item.KeyboardAcceleratorTextOverride = shortcut;
        item.Click += async (_, _) =>
        {
            try { action(); }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (MainWindow.Current is { } window) await window.ShowErrorAsync(error.Message); }
        };
        return item;
    }
    internal static MenuFlyoutItem Async(string title, Func<Task> action, string? glyph = null, bool enabled = true, string? shortcut = null)
        => Item(title, async () =>
        {
            try { await action(); }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (MainWindow.Current is { } window) await window.ShowErrorAsync(error.Message); }
        }, glyph, enabled, shortcut);

    internal static Button Button(string title, Func<MenuFlyout> create, string? glyph = null)
    {
        var button = StudioLayout.Tool(title, () => { }, glyph);
        button.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)); button.BorderThickness = new(0);
        button.Click += (_, _) => { var menu = create(); menu.ShowAt(button); };
        return button;
    }
    internal static void Context(FrameworkElement element, Func<bool> select, Func<MenuFlyout> create)
    {
        element.ContextRequested += (_, args) =>
        {
            if (!select()) { args.Handled = true; return; }
            var options = new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions();
            if (args.TryGetPosition(element, out Point point)) options.Position = point;
            create().ShowAt(element, options); args.Handled = true;
        };
    }
}
