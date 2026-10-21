using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.WinUI;

internal sealed partial class CreativeScenePage
{
    async Task<bool> ConfirmReplaceAsync()
    {
        if (!HasUnsavedChanges) return true;
        viewport.Pause();
        var result = await new ContentDialog
        {
            XamlRoot = window.RootXaml, Title = "保存当前场景？",
            Content = "当前场景有未保存的修改。", PrimaryButtonText = "保存", SecondaryButtonText = "不保存", CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        }.ShowAsync();
        if (result == ContentDialogResult.Primary) { await SaveAsync(); return !HasUnsavedChanges; }
        if (result == ContentDialogResult.Secondary) { SaveDraft(); return true; }
        return false;
    }
    MenuFlyout TrackMenu(string actor, string kind, double time)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(StudioMenus.Async("在这里添加" + ClipTimeline.KindName(kind) + "…", () => Safe(() => AddToTrackAsync(actor, kind, time)), "\uE710"));
        if (kind == "voice") menu.Items.Add(StudioMenus.Async("导入声音文件…", () => Safe(async () =>
        {
            selectedActor = actor.Length == 0 ? null : actor; selectedClip = null; playhead = time;
            await ImportVoiceAsync();
        }), "\uE8D6"));
        menu.Items.Add(StudioMenus.Async("从这里播放", () => Safe(async () => { if(await SeekAfterScrubAsync(time))viewport.Play(); }), "\uE768"));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(StudioMenus.Async("插入原生指令…", () => Safe(() => InsertNativeClipAsync(actor, time)), "\uE943"));
        return menu;
    }
    async Task InsertNativeClipAsync(string? actor, double at)
    {
        viewport.Pause();
        var searchBox = new TextBox { PlaceholderText = "搜索用途或原始指令名…" };
        var list = new ListView { Height = 280, SelectionMode = ListViewSelectionMode.Single };
        var help = new TextBlock { Text = "普通动作、表情和声音优先从素材栏添加。这里用于保留或插入完整原生指令。", TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .75 };
        var dialog = new ContentDialog { XamlRoot = window.RootXaml, Title = "插入演出指令", PrimaryButtonText = "编辑参数…", CloseButtonText = "取消",
            IsPrimaryButtonEnabled = false, Content = Ui.Stack(10, searchBox, list, help) };
        void Filter()
        {
            list.Items.Clear(); string query = searchBox.Text.Trim();
            foreach (var command in StudioCommandCatalog.All.Where(c => c.Command is not ("GoTo" or "Wait" or "ForceAuto" or "ChoiceShow")
                && (c.Label + " " + c.Command).Contains(query, StringComparison.OrdinalIgnoreCase)))
                list.Items.Add(new ListViewItem { Tag = command.Command, Content = new TextBlock { Text = command.Label + " · " + command.Command, IsTextSelectionEnabled = false }, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        }
        searchBox.TextChanged += (_, _) => Filter(); list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedItem is not null;
        Filter(); if (await dialog.ShowAsync() != ContentDialogResult.Primary || list.SelectedItem is not ListViewItem { Tag: string name }) return;
        var initial = StudioCommandCatalog.Create(name, actor);
        var json = await EditJsonDialogAsync("指令参数 · " + StudioCommandCatalog.Get(name).Label, initial.ToJsonString(DesktopSettings.JsonOptions), text =>
        {
            var row = JsonNode.Parse(text) as JsonObject ?? throw new ArgumentException("指令必须是 JSON 对象。");
            var candidate = new CreativeScene(document.Root);
            candidate.AddClip(actor ?? "", CreativeScene.Kind(name), at, Math.Max(1d / 30, N(row, "Duration", 1)), row, StudioCommandCatalog.Get(name).Label);
        });
        if (json is null) return;
        await EditAsync(() =>
        {
            var row = JsonNode.Parse(json)!.AsObject(); string command = S(row, "cmd");
            selectedClip = document.AddClip(row["TargetName"]?.ToString() ?? actor ?? "", CreativeScene.Kind(command), at,
                Math.Max(1d / 30, N(row, "Duration", 1)), row, StudioCommandCatalog.Get(command).Label);
            if (AdvProgram.Row(row, "inserted clip").Ignored) document.Clip(selectedClip)["enabled"] = false;
        });
    }
}
