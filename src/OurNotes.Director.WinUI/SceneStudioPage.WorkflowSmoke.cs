using Microsoft.UI.Xaml.Controls;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.WinUI;

internal sealed partial class SceneStudioPage
{
    private async Task RunBlankWorkspaceSmokeAsync(Action<bool, string> check)
    {
        string archive = _window.Settings.ArchivePath;
        try
        {
            _window.Settings.ArchivePath = Path.Combine(Path.GetTempPath(), "absent-archive-" + Guid.NewGuid().ToString("N"));
            await NewAsync(false);
            UpdateLayout();
            check(_document is not null && _document.Document.Source.IsLibrary
                && _document.Document.Root["source"]?["adv"] is null && !_document.Document.Validate().HasErrors,
                "没有素材库时也能通过实际新建入口建立有效空白场景，不弹出原剧情选择器");
            check(_viewport.LastFrame is null && !_viewport.HasCurrentSource && _viewport.ActualWidth > 0,
                "空白工作台不展示上一个原生项目的过期画面");
            check(SceneViewMenu().Items.OfType<MenuFlyoutItem>().Any(item => item.Text == "场景 JSON…")
                && ActorContextMenu().Items.OfType<MenuFlyoutItem>().Any(item => item.Text == "添加角色…"),
                "空白场景的 JSON 和添加素材仍可由可发现的菜单进入");
            string before = _document!.ToJson();
            _viewport.FitCanvas();
            check(_document.ToJson() == before, "适应画布只改变编辑器视图，不改动导出构图或项目 JSON");
        }
        finally { _window.Settings.ArchivePath = archive; }
    }

    private async Task RunObjectMenuSmokeAsync(Action<bool, string> check)
    {
        var menu = ActorContextMenu();
        check(menu.Items.OfType<MenuFlyoutItem>().Any(item => item.Text == "导出此角色图片…" && item.IsEnabled)
            && menu.Items.OfType<MenuFlyoutItem>().Any(item => item.Text == "导出此角色动画…" && item.IsEnabled),
            "选中角色的上下文菜单同时提供当前姿态 PNG 和动画入口");
        menu.ShowAt(_objects);
        await Task.Delay(40);
        menu.Hide();
        check(_document?.SelectedId is { } id && !id.StartsWith('$'),
            "实际弹出和关闭角色菜单不会清除当前选中对象");
    }
}
