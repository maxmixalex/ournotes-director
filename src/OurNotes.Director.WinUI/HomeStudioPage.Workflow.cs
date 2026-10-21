using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OurNotes.Director.WinUI;

internal sealed partial class HomeStudioPage
{
    MenuFlyout HomeViewMenu()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(StudioMenus.Item("画布适应窗口", viewport.FitCanvas, "\uE9A6", true, "F"));
        var follow = new ToggleMenuFlyoutItem { Text = "单击片段时定位画面", IsChecked = timeline.FollowSelection };
        follow.Click += (_, _) => timeline.FollowSelection = follow.IsChecked; menu.Items.Add(follow);
        menu.Items.Add(StudioMenus.Item("画布与房间设置", () => Select(SceneId), "\uE740", document is not null));
        var loop = new ToggleMenuFlyoutItem { Text = "循环播放", IsChecked = timeline.Repeat };
        loop.Click += (_, _) => timeline.Repeat = loop.IsChecked; menu.Items.Add(loop);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(StudioMenus.Async("主页 JSON…", ShowJsonAsync, "\uE943"));
        return menu;
    }
    MenuFlyout HomeObjectMenu()
    {
        var menu = new MenuFlyout(); var key = document?.SelectedCharacterKey;
        if (key is not null)
        {
            menu.Items.Add(StudioMenus.Async("导出此角色图片…", () => ExportCharacterAsync("frame"), "\uEB9F"));
            menu.Items.Add(StudioMenus.Async("导出此角色动画…", () => ExportCharacterAsync("clip"), "\uE714"));
            menu.Items.Add(new MenuFlyoutSeparator());
            var solo = new ToggleMenuFlyoutItem { Text = "仅预览此角色", IsChecked = previewOnly.IsChecked == true };
            solo.Click += (_, _) => previewOnly.IsChecked = solo.IsChecked; menu.Items.Add(solo);
            menu.Items.Add(StudioMenus.Item("添加位置关键帧", () => Edit(() => document!.AddTransformKey(key, document.Playhead)), "\uE710"));
            menu.Items.Add(StudioMenus.Item("恢复原始状态", () => Edit(() => document!.ResetCharacter(key)), "\uE777"));
        }
        else
        {
            menu.Items.Add(StudioMenus.Async("打开主页…", ChooseHomeAsync, "\uE8B7"));
            menu.Items.Add(StudioMenus.Item("画布适应窗口", viewport.FitCanvas, "\uE9A6"));
            menu.Items.Add(StudioMenus.Async("导出当前画面…", async () =>
            {
                var current = document; await scrubber.FlushAsync();
                if (current is not null && !disposed && ReferenceEquals(current, document))
                { Pause(); await StudioExportDialog.ShowHomeAsync(window, current, current.Playhead, initialKind: "frame"); }
            }, "\uEB9F", document is not null));
        }
        return menu;
    }
    async Task ExportCharacterAsync(string kind)
    {
        var current = document; await scrubber.FlushAsync();
        if (current is null || disposed || !ReferenceEquals(current, document)) return;
        CommitDrag();
        if (current.SelectedCharacterKey is null) return;
        Pause(); await StudioExportDialog.ShowHomeAsync(window, current, current.Playhead, initialKind: kind, isolated: true, initialRange:timeline.WorkRange);
    }
}
