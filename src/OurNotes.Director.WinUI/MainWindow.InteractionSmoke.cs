using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace OurNotes.Director.WinUI;

public sealed partial class MainWindow
{
    internal static IEnumerable<T> Descendants<T>(DependencyObject node) where T : DependencyObject
    {
        if (node is T value) yield return value;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(node, i))) yield return child;
    }
    async Task RunEditingInteractionSmokeAsync(Action<bool, string> check)
    {
        Navigate("scenes"); root.UpdateLayout(); await Task.Delay(100);
        // The legacy SceneStudioPage is no longer the active scene workspace.
        // Exercise the real native composition bridge from the actual smoke route.
        Descendants<NativeViewport>(ActiveLayout!.ProgramMonitorPanel).Single().RunGpuCompositionSmoke(check);
        if (Scene.LastStoryImportReport is { } importReport)
            OurNotes.Director.Desktop.Core.DesktopSettings.AtomicWrite(App.SmokeEvidencePath("story-import.json"), importReport.ToJsonString(OurNotes.Director.Desktop.Core.DesktopSettings.JsonOptions));
        foreach (string workspace in new[] { "scenes", "live2d", "home" })
        {
            Navigate(workspace); root.UpdateLayout(); await Task.Delay(100);
            var layout = ActiveLayout!;
            var viewport = Descendants<NativeViewport>(layout.ProgramMonitorPanel).Single();
            if (viewport.HasCurrentSource) await viewport.PauseForEditingAsync();
            long? session = viewport.LastFrame?.SessionId;
            int? frame = viewport.LastFrame?.Frame;
            var originalPresenter = AppWindow.Presenter;
            var originalMaximized = layout.MaximizedPanel;
            viewport.Focus(FocusState.Keyboard);
            await viewport.SendShellKeySmokeAsync(this, VirtualKey.F1);
            root.UpdateLayout();
            check(IsMonitorFullscreen && AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen
                && layout.ProgramMonitorPanel.IsMaximized && fullscreenHidden.Count > 0,
                workspace + "：实际 F1 进入节目监视器全屏，隐藏编辑器外框");
            await App.CaptureCheckpointAsync(workspace + "-monitor-fullscreen");
            await viewport.SendShellKeySmokeAsync(this, (VirtualKey)192); root.UpdateLayout();
            check(IsMonitorFullscreen && layout.MaximizedPanel == layout.ProgramMonitorPanel,
                workspace + "：全屏内反引号不恢复被隐藏的其他面板");
            await viewport.SendShellKeySmokeAsync(this, VirtualKey.F1); root.UpdateLayout();
            check(!IsMonitorFullscreen && AppWindow.Presenter.Kind == originalPresenter.Kind
                && layout.MaximizedPanel == originalMaximized && Grid.GetRow(content) == 2 && Grid.GetRowSpan(content) == 1,
                workspace + "：再次 F1 恢复窗口和先前面板布局");
            check(viewport.LastFrame?.SessionId == session && viewport.LastFrame?.Frame == frame && !viewport.IsPlaying,
                workspace + "：全屏来回保留原生渲染会话、暂停姿态和播放状态");

            layout.RestorePanels(); layout.ToggleMaximize(layout.ProgramMonitorPanel);
            viewport.Focus(FocusState.Keyboard);
            await viewport.SendShellKeySmokeAsync(this, VirtualKey.F1);
            await viewport.SendShellKeySmokeAsync(this, VirtualKey.Escape); root.UpdateLayout();
            check(!IsMonitorFullscreen && AppWindow.Presenter.Kind == originalPresenter.Kind
                && layout.MaximizedPanel == layout.ProgramMonitorPanel,
                workspace + "：从已最大化监视器进入全屏后，Esc 恢复先前最大化布局");
            viewport.Focus(FocusState.Keyboard);
            await viewport.SendShellKeySmokeAsync(this, VirtualKey.F1);
            AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
            await Task.Delay(70); root.UpdateLayout();
            check(!IsMonitorFullscreen && fullscreenHidden.Count == 0
                && layout.MaximizedPanel == layout.ProgramMonitorPanel && Grid.GetRow(content) == 2,
                workspace + "：系统恢复普通窗口时同步恢复编辑器外框和先前面板布局");
            layout.RestorePanels();
            if (originalMaximized is not null) layout.ToggleMaximize(originalMaximized);
            layout.EffectControlsPanel.FocusContent();
            await viewport.SendShellKeySmokeAsync(this, VirtualKey.F1);
            check(!IsMonitorFullscreen, workspace + "：焦点在检查器时 F1 不误触发节目监视器全屏");

            // These controls are deliberately inside the monitor. Testing only
            // inspector focus would not exercise the text-input exclusion.
            if (workspace == "scenes") await VerifyMonitorTextFocusSmokeAsync(layout, viewport, check);
        }
        Navigate("scenes"); root.UpdateLayout(); await Task.Delay(100);
        await Scene.RunTimelineInteractionSmokeAsync(check);
        Navigate("settings"); root.UpdateLayout(); await Task.Delay(100);
        WindowsGpuPreference.RunRegressionSmoke(check);
        string registryFixture = @"Software\OurNotes\DirectorTests\" + Guid.NewGuid().ToString("N");
        try
        {
            using var registry = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(registryFixture)!;
            string[] apps = OurNotes.Director.Desktop.Core.GpuPreferencePolicy.Executables(Environment.ProcessPath, Settings.ResolveEngine());
            foreach (string app in apps) registry.SetValue(app, "AutoHDREnable=1;GpuPreference=1;");
            WindowsGpuPreference.ApplyToKey(registry, apps, 2);
            check(apps.Length == 2 && apps.All(app => registry.GetValue(app)?.ToString() == "AutoHDREnable=1;GpuPreference=2;"),
                "相同生产设置代码在隔离注册表同时写入编辑器与导出引擎显卡偏好，保留其他选项");
            WindowsGpuPreference.ApplyToKey(registry, apps, 0);
            check(apps.All(app => registry.GetValue(app)?.ToString() == "AutoHDREnable=1;"),
                "选择自动只移除显卡覆盖项，未清空其他图形设置");
        }
        finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(registryFixture, false); }
        check(Descendants<Microsoft.UI.Xaml.Controls.ComboBox>(drawerContent).Any(box => box.Header?.ToString() == "渲染显卡（重启后生效）"),
            "实际设置抽屉提供自动、节能、高性能显卡选择和当前设备诊断");
        await App.CaptureCheckpointAsync("graphics-settings"); CloseDrawer();
    }

    async Task VerifyMonitorTextFocusSmokeAsync(StudioLayout layout, NativeViewport viewport, Action<bool, string> check)
    {
        if (layout.ProgramMonitorPanel.SelectedContent is not Panel monitor)
            throw new InvalidOperationException("节目监视器缺少可用的输入检查容器。");
        var inputHost = new StackPanel { Width = 200, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top };
        Control[] inputs = [new TextBox(), new NumberBox(), new PasswordBox(), new RichEditBox(), new AutoSuggestBox()];
        foreach (var input in inputs) inputHost.Children.Add(input);
        monitor.Children.Add(inputHost);
        try
        {
            root.UpdateLayout();
            foreach (var input in inputs)
            {
                check(input.Focus(FocusState.Keyboard), input.GetType().Name + " 获得节目监视器内的实际键盘焦点");
                await viewport.SendShellKeySmokeAsync(this, VirtualKey.F1);
                check(!IsMonitorFullscreen && !MonitorOwnsFocus(),
                    "节目监视器内 " + input.GetType().Name + " 输入框的 F1 不误触发全屏");
            }
        }
        finally
        {
            ExitMonitorFullscreen(); monitor.Children.Remove(inputHost); viewport.Focus(FocusState.Keyboard);
        }
    }
}
