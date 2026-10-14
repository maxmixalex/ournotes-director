using Microsoft.UI.Xaml.Controls;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.WinUI;

internal sealed partial class SceneStudioPage
{
    private MenuFlyout SceneEditMenu()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(StudioMenus.Item("撤销", UndoSceneEdit, "\uE7A7", _document?.CanUndo == true || HasCommandDraft, "Ctrl+Z"));
        menu.Items.Add(StudioMenus.Item("重做", () => Edit(() => _document?.Redo()), "\uE7A6", _document?.CanRedo == true, "Ctrl+Y"));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(StudioMenus.Async("添加角色…", AddActorAsync, "\uE77B"));
        menu.Items.Add(StudioMenus.Async("插入指令…", ChooseCommandAsync, "\uE710", _document is not null));
        menu.Items.Add(StudioMenus.Async("新建单人镜头…", () => CreateSingleActorShotAsync(false), "\uE8B9"));
        return menu;
    }
    private MenuFlyout SceneViewMenu()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(StudioMenus.Async("画布尺寸…", CanvasSettingsAsync, "\uE740", _document is not null));
        menu.Items.Add(StudioMenus.Item("画布适应窗口", _viewport.FitCanvas, "\uE9A6", true, "F"));
        var loop = new ToggleMenuFlyoutItem { Text = "循环播放", IsChecked = _loop.IsChecked == true };
        loop.Click += (_, _) => _loop.IsChecked = loop.IsChecked; menu.Items.Add(loop);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(StudioMenus.Async("场景 JSON…", ShowJsonAsync, "\uE943", true, "Ctrl+Shift+J"));
        return menu;
    }
    private MenuFlyout ActorContextMenu()
    {
        var menu = new MenuFlyout();
        bool actor = _document?.SelectedId is { } id && !id.StartsWith('$');
        if (actor)
        {
            menu.Items.Add(StudioMenus.Async("导出此角色图片…", () => ExportActorAsync("frame"), "\uEB9F"));
            menu.Items.Add(StudioMenus.Async("导出此角色动画…", () => ExportActorAsync("clip"), "\uE714"));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(StudioMenus.Item("添加位置变化", () => InsertNativeCommand("Director.Actor"), "\uE7C2"));
            menu.Items.Add(StudioMenus.Item("添加动作", () => InsertNativeCommand("Motion"), "\uE768"));
            menu.Items.Add(StudioMenus.Item("添加表情", () => InsertNativeCommand("Expression"), "\uE76E"));
            menu.Items.Add(StudioMenus.Item("添加对白", () => InsertNativeCommand("Talk"), "\uE8BD"));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(StudioMenus.Item("移除角色 / 退场", RemoveSelected, "\uE74D", true, "Delete"));
        }
        else
        {
            menu.Items.Add(StudioMenus.Async("添加角色…", AddActorAsync, "\uE77B"));
            menu.Items.Add(StudioMenus.Async("画布尺寸…", CanvasSettingsAsync, "\uE740", _document is not null));
            menu.Items.Add(StudioMenus.Item("画布适应窗口", _viewport.FitCanvas, "\uE9A6"));
            menu.Items.Add(StudioMenus.Async("导出当前画面…", () => ExportImageAsync(), "\uEB9F", _document is not null));
        }
        return menu;
    }
    private MenuFlyout CommandContextMenu()
    {
        var menu = new MenuFlyout(); var command = SelectedCommand();
        menu.Items.Add(StudioMenus.Async("在此后插入…", ChooseCommandAsync, "\uE710", _document is not null));
        menu.Items.Add(StudioMenus.Item("复制指令", DuplicateSelectedCommand, "\uE8C8", command is not null, "Ctrl+D"));
        menu.Items.Add(StudioMenus.Item("上移", () => MoveSelectedCommand(-1), "\uE74A", command?.Index > 0, "Alt+↑"));
        menu.Items.Add(StudioMenus.Item("下移", () => MoveSelectedCommand(1), "\uE74B", command is not null, "Alt+↓"));
        if (command is not null)
        {
            menu.Items.Add(StudioMenus.Item(command.Enabled ? "停用指令" : "启用指令", () => Edit(() => _document!.SetCommandEnabled(command.Id, !command.Enabled))));
            menu.Items.Add(StudioMenus.Item("复制为 JSON", () => MainWindow.Copy(command.Data.ToJsonString(DesktopSettings.JsonOptions)), "\uE943"));
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(StudioMenus.Item("删除", RemoveSelectedCommand, "\uE74D", command is not null, "Delete"));
        return menu;
    }
    private async Task ExportImageAsync()
    {
        if (!TryFlushCommandDraft()) return;
        FlushProperty(); await _pendingPreviewCommit;
        if (_document is not null) { _viewport.Pause(); await StudioExportDialog.ShowSceneAsync(_window, _document, _document.Time, initialKind: "frame"); }
    }
    private async Task ExportActorAsync(string kind)
    {
        if (!TryFlushCommandDraft()) return;
        FlushProperty(); await _pendingPreviewCommit;
        if (_document?.SelectedId is not { } id || id.StartsWith('$')) return;
        _viewport.Pause();
        var evaluated = _viewport.HasCurrentSource && ReferenceEquals(_previewDocument, _document)
            ? _viewport.LastFrame?.ActorAnimations?.FirstOrDefault(actor => actor.TargetId == id) : null;
        string? model = evaluated?.Model ?? _document.Actors.FirstOrDefault(actor => actor.Id == id)?.Model;
        if (string.IsNullOrWhiteSpace(model)) throw new InvalidOperationException("请先载入这个角色，再导出素材。");
        var metadata = await Task.Run(() => new Archive(_window.Settings.ArchivePath).Model(_document.Document.Source.Site, model));
        await StudioExportDialog.ShowSceneAsync(_window, _document, _document.Time, initialKind: kind, actorId: id, actorMetadata: metadata);
    }
    private void AttachActorMenu(ListViewItem item, string id)
        => StudioMenus.Context(item, () => { SelectObject(id); return _document?.SelectedId == id; }, ActorContextMenu);
}
