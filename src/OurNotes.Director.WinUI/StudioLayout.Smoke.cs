using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace OurNotes.Director.WinUI;

internal sealed partial class StudioLayout
{
    internal async Task RunLayoutSmokeAsync(Window window, Action<bool, string> check, bool realInput = false)
    {
        RestorePanels(); UpdateLayout();
        var original = panels.ToDictionary(panel => panel.Id, PanelBounds);
        bool nonoverlap = panels.All(panel => panel.ActualWidth > 100 && panel.ActualHeight > 100);
        for (int i = 0; i < panels.Count; i++)
            for (int j = i + 1; j < panels.Count; j++)
            {
                var a = original[panels[i].Id]; var b = original[panels[j].Id];
                nonoverlap &= a.Right <= b.Left + 1 || b.Right <= a.Left + 1 || a.Bottom <= b.Top + 1 || b.Bottom <= a.Top + 1;
            }
        check(nonoverlap, WorkspaceKey + " 工作区面板真实边界有效且互不重叠");
        var monitor = original["monitor"]; var effects = original["effects"];
        check(dual
            ? monitor.Right <= effects.Left && monitor.Width > effects.Width
            : effects.Right <= monitor.Left && original["project"].Bottom <= dock.ActualHeight + 1
                && original["project"].Top >= effects.Bottom && original["timeline"].Top >= monitor.Bottom
                && original["project"].Right <= original["timeline"].Left,
            WorkspaceKey + (dual ? " 双区保持左侧大监视器和右侧检查器" : " 四区保持效果控件、节目监视器、项目素材、时间轴布局"));
        check(ProgramMonitorPanel.SelectedContent.ActualWidth > 180 && ProgramMonitorPanel.SelectedContent.ActualHeight > 100,
            WorkspaceKey + " 监视器实际内容区域可见");

        foreach (var panel in panels)
        {
            var child = panel.SelectedContent; var parent = VisualTreeHelper.GetParent(child);
            FocusPanel(panel.Id);
            await InvokeWindowCommandAsync("最大化当前面板"); UpdateLayout();
            var expanded = PanelBounds(panel);
            check(ReferenceEquals(maximized, panel) && ReferenceEquals(panel.SelectedContent, child)
                && ReferenceEquals(VisualTreeHelper.GetParent(child), parent)
                && Math.Abs(expanded.Width - dock.ActualWidth) < 2 && Math.Abs(expanded.Height - dock.ActualHeight) < 2,
                WorkspaceKey + " 窗口菜单最大化 " + panel.Id + " 并保留编辑控件实例");
            await InvokeWindowCommandAsync("恢复面板大小"); UpdateLayout();
            check(maximized is null && panels.All(item => SameBounds(PanelBounds(item), original[item.Id])),
                WorkspaceKey + " 窗口菜单恢复 " + panel.Id + " 后各分隔位置不变");
        }
        if (realInput)
        {
            var saved = proportions; double width = top.ColumnDefinitions[0].ActualWidth;
            double otherWidth = bottom.ColumnDefinitions[0].ActualWidth;
            await SendSplitterDragAsync(window, topSplitter, dual ? -24 : 24); UpdateLayout();
            check(Math.Abs(top.ColumnDefinitions[0].ActualWidth - width) > 8
                && (dual || Math.Abs(bottom.ColumnDefinitions[0].ActualWidth - otherWidth) < 1),
                WorkspaceKey + " 真实鼠标拖动上分隔条只改变该组宽度");
            proportions = saved; RestorePanels(); SaveLayout(); UpdateLayout();

            FocusPanel("monitor"); await MovePointerAsync(window, EffectControlsPanel);
            await SendMaximizeKeyAsync(window, false); UpdateLayout();
            check(ReferenceEquals(maximized, EffectControlsPanel), WorkspaceKey + " 真实反引号按键最大化鼠标所在面板");
            await SendMaximizeKeyAsync(window, false); UpdateLayout();
            check(maximized is null, WorkspaceKey + " 再次反引号按键恢复面板");
            FocusPanel("monitor"); await MovePointerAsync(window, EffectControlsPanel);
            await SendMaximizeKeyAsync(window, true); UpdateLayout();
            check(ReferenceEquals(maximized, ProgramMonitorPanel), WorkspaceKey + " Shift加反引号最大化键盘焦点面板");
            await SendMaximizeKeyAsync(window, true); UpdateLayout();
            check(maximized is null && panels.All(item => SameBounds(PanelBounds(item), original[item.Id])),
                WorkspaceKey + " 快捷键恢复保留原四区或双区尺寸");
        }
        FocusPanel("monitor");
    }

    Rect PanelBounds(WorkspacePanel panel)
    {
        var at = panel.TransformToVisual(dock).TransformPoint(new Point());
        return new(at.X, at.Y, panel.ActualWidth, panel.ActualHeight);
    }
    static bool SameBounds(Rect first, Rect second) => Math.Abs(first.X - second.X) < 1 && Math.Abs(first.Y - second.Y) < 1
        && Math.Abs(first.Width - second.Width) < 1 && Math.Abs(first.Height - second.Height) < 1;

    async Task InvokeWindowCommandAsync(string title)
    {
        var menu = CreateWindowMenu(); menu.ShowAt(WindowMenuButton);
        try
        {
            await Task.Delay(35);
            var item = menu.Items.OfType<MenuFlyoutItem>().First(entry => entry.Text == title);
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(item) ?? new MenuFlyoutItemAutomationPeer(item);
            if (peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke) throw new InvalidOperationException("窗口菜单缺少原生 Invoke 接口。");
            invoke.Invoke(); await Task.Delay(35);
        }
        finally { menu.Hide(); }
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    struct WorkspaceInput
    {
        [FieldOffset(0)] internal uint Type;
        [FieldOffset(8)] internal int X;
        [FieldOffset(12)] internal int Y;
        [FieldOffset(8)] internal ushort Key;
        [FieldOffset(12)] internal uint KeyFlags;
        [FieldOffset(20)] internal uint MouseFlags;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct WorkspacePoint { internal int X, Y; }
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, [In] WorkspaceInput[] inputs, int size);
    [DllImport("user32.dll")] static extern bool ClientToScreen(nint window, ref WorkspacePoint point);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);

    static WorkspacePoint ScreenPoint(Window window, FrameworkElement element)
    {
        var point = element.TransformToVisual(window.Content).TransformPoint(new(element.ActualWidth / 2, element.ActualHeight / 2));
        double scale = element.XamlRoot.RasterizationScale;
        var screen = new WorkspacePoint { X = (int)Math.Round(point.X * scale), Y = (int)Math.Round(point.Y * scale) };
        if (!ClientToScreen(WinRT.Interop.WindowNative.GetWindowHandle(window), ref screen)) throw new InvalidOperationException("无法定位工作区真实输入坐标。");
        return screen;
    }
    static void Inject(params WorkspaceInput[] input)
    {
        if (SendInput((uint)input.Length, input, Marshal.SizeOf<WorkspaceInput>()) != input.Length)
            throw new InvalidOperationException("工作区真实键鼠输入未被系统接受：" + Marshal.GetLastWin32Error());
    }
    static async Task MovePointerAsync(Window window, FrameworkElement element)
    {
        if (Environment.GetEnvironmentVariable("OURNOTES_REAL_SCENE_FIXTURE") is not { Length: > 0 })
            throw new InvalidOperationException("真实键鼠输入需要专用烟测数据。");
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        window.Activate(); SetForegroundWindow(hwnd); await Task.Delay(80);
        if (GetForegroundWindow() != hwnd) throw new InvalidOperationException("工作台未获得真实键鼠焦点。");
        var point = ScreenPoint(window, element);
        MovePointer(point.X, point.Y);
        await Task.Delay(35);
    }
    static void MovePointer(int x, int y)
    {
        int left = GetSystemMetrics(76), top = GetSystemMetrics(77), width = GetSystemMetrics(78), height = GetSystemMetrics(79);
        Inject(new WorkspaceInput { X = (int)Math.Round((x - left) * 65535d / Math.Max(1, width - 1)),
            Y = (int)Math.Round((y - top) * 65535d / Math.Max(1, height - 1)), MouseFlags = 0x0001 | 0x8000 | 0x4000 | 0x2000 });
    }
    static async Task SendSplitterDragAsync(Window window, Thumb splitter, double dx)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DragCompletedEventHandler handler = (_, _) => completed.TrySetResult(); splitter.DragCompleted += handler;
        try
        {
            await MovePointerAsync(window, splitter); var start = ScreenPoint(window, splitter);
            Inject(new WorkspaceInput { MouseFlags = 0x0002 }); await Task.Delay(25);
            MovePointer(start.X + (int)Math.Round(dx * splitter.XamlRoot.RasterizationScale), start.Y);
            await Task.Delay(35); Inject(new WorkspaceInput { MouseFlags = 0x0004 });
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { Inject(new WorkspaceInput { MouseFlags = 0x0004 }); splitter.DragCompleted -= handler; }
    }
    static async Task SendMaximizeKeyAsync(Window window, bool shift)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        KeyEventHandler handler = (_, args) => { if (args.Key == (VirtualKey)192) completed.TrySetResult(); };
        window.Content.AddHandler(KeyDownEvent, handler, true);
        try
        {
            if (shift) Inject(new WorkspaceInput { Type = 1, Key = (ushort)VirtualKey.Shift });
            Inject(new WorkspaceInput { Type = 1, Key = 192 }, new WorkspaceInput { Type = 1, Key = 192, KeyFlags = 2 });
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            if (shift) Inject(new WorkspaceInput { Type = 1, Key = (ushort)VirtualKey.Shift, KeyFlags = 2 });
            window.Content.RemoveHandler(KeyDownEvent, handler);
        }
        await Task.Delay(35);
    }
}
