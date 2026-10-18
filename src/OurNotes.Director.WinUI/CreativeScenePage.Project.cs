using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.WinUI;

internal sealed partial class CreativeScenePage
{
    readonly ContentControl projectItems = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    readonly TextBlock projectContext = new() { FontSize = 11, Opacity = .8, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    readonly List<ToggleButton> projectTabs = [];
    readonly StackPanel projectActions = new() { Orientation = Orientation.Horizontal, Spacing = 3 };
    bool projectShowsActors = true;

    void ResetProjectLibrary()
    {
        assetRequest?.Cancel(); assetVersion++;
        assetRows.Clear(); assets.Items.Clear(); search.Text = "";
        ShowProjectActors();
    }

    FrameworkElement BuildProjectLibrary()
    {
        var panel = new Grid { RowSpacing = 5 };
        for (int i = 0; i < 5; i++) panel.RowDefinitions.Add(new() { Height = i == 3 ? new(1, GridUnitType.Star) : GridLength.Auto });
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var (label, index) in new[] { ("角色", -1), ("动作", 0), ("表情", 1), ("语音", 2) })
        {
            var tab = new ToggleButton { Content = label, Tag = index, FontSize = 12, Padding = new(8, 3, 8, 3), MinWidth = 0 };
            tab.Click += (_, _) =>
            {
                if (index < 0) ShowProjectActors();
                else { assetKind.SelectedIndex = index; ShowProjectAssets(); }
            };
            projectTabs.Add(tab); tabs.Children.Add(tab);
        }
        panel.Children.Add(tabs);
        var context = new Grid { ColumnSpacing = 4 };
        context.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); context.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        context.Children.Add(projectContext); context.Children.Add(projectActions); Grid.SetColumn(projectActions, 1);
        panel.Children.Add(context); Grid.SetRow(context, 1);
        search.MinHeight = 28; search.FontSize = 12; search.Padding = new(6, 3, 6, 3);
        panel.Children.Add(search); Grid.SetRow(search, 2);
        panel.Children.Add(projectItems); Grid.SetRow(projectItems, 3);
        assetHint.TextWrapping = TextWrapping.NoWrap; assetHint.TextTrimming = TextTrimming.CharacterEllipsis;
        panel.Children.Add(assetHint); Grid.SetRow(assetHint, 4);
        ShowProjectActors(); return panel;
    }
    void ShowProjectActors()
    {
        projectShowsActors = true; projectItems.Content = actors; search.Visibility = Visibility.Collapsed;
        foreach (var tab in projectTabs) tab.IsChecked = (int)tab.Tag == -1;
        projectActions.Children.Clear();
        var add = Button("添加", AddActorAsync, "\uE710"); add.FontSize = 11; projectActions.Children.Add(add);
        assetHint.Text = "双击角色在 Live2D 中打开"; UpdateProjectContext();
    }
    void ShowProjectAssets()
    {
        projectShowsActors = false; projectItems.Content = assets; search.Visibility = Visibility.Visible;
        foreach (var tab in projectTabs) tab.IsChecked = (int)tab.Tag == assetKind.SelectedIndex;
        projectActions.Children.Clear();
        var insert = Button("", InsertSelectedAssetAsync, "\uE710"); ToolTipService.SetToolTip(insert, "插入播放头 / 替换所选片段 · Enter"); projectActions.Children.Add(insert);
        if (AssetKind != "voice") { var preview = Button("", PreviewSelectedAssetAsync, "\uE768"); ToolTipService.SetToolTip(preview, "在 Live2D 中预览 · 双击"); projectActions.Children.Add(preview); }
        if (AssetKind == "voice") { var file = Button("", ImportVoiceAsync, "\uE8B7"); ToolTipService.SetToolTip(file, "导入声音文件…"); projectActions.Children.Add(file); }
        assetHint.Text = AssetKind == "voice" ? "Enter 插入；拖到轨道安排时间" : "双击预览；Enter 插入；拖到时间轴编排";
        UpdateProjectContext();
    }
    void UpdateProjectContext()
    {
        var actor = document.Actors.OfType<JsonObject>().FirstOrDefault(a => S(a, "id") == selectedActor);
        projectContext.Text = projectShowsActors ? $"{document.Actors.Count} 个角色" : actor is null ? "先选择角色" : DisplayNames.Character(CreativeScene.Text(actor, "name", selectedActor ?? ""));
    }
    Task InsertSelectedAssetAsync() => (assets.SelectedItem as ListViewItem)?.Tag is ResultRow row
        ? InsertAssetAsync(row, AssetKind, playhead) : Task.CompletedTask;
    async Task PreviewSelectedAssetAsync()
    {
        if ((assets.SelectedItem as ListViewItem)?.Tag is not ResultRow row) return;
        if (AssetKind == "voice") { await InsertAssetAsync(row, AssetKind, playhead); return; }
        var actor = document.Actors.OfType<JsonObject>().FirstOrDefault(a => S(a, "id") == selectedActor);
        if (actor is null) return;
        viewport.Pause();
        string model = S(actor, "model"), name = CreativeScene.Text(actor, "name", model), kind = AssetKind;
        window.Navigate("live2d");
        await window.Live2D.OpenModelAsync(model, name, kind == "motion" ? row.Id : null, kind == "expression" ? row.Id : null, site: document.Site, language: document.Language);
    }
    async Task OpenActorInLive2DAsync(TimelineRange? range = null)
    {
        if (selectedActor is not { Length: > 0 } actor) return;
        await scrubber.FlushAsync(); viewport.Pause();
        var native = SceneDocument.Parse(Compiled().ToJsonString(), baseDirectory: directory);
        string? model = viewport.LastFrame?.ActorAnimations?.FirstOrDefault(a => a.TargetId == actor)?.Model
            ?? document.Actors.OfType<JsonObject>().FirstOrDefault(a => S(a, "id") == actor)?["model"]?.ToString();
        if (model is null) throw new InvalidOperationException("请先载入所选角色。");
        JsonNode metadata = await Task.Run(() => new Archive(window.Settings.ArchivePath).Model(document.Site, model));
        double time = playhead; TimelineRange? selectedRange = range ?? timeline.WorkRange;
        window.Navigate("live2d");
        await window.Live2D.OpenSceneActorAsync(native, actor, metadata, time, selectedRange, duration: document.Duration);
    }
}
