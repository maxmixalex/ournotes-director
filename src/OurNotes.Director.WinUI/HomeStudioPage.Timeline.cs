using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.WinUI;

internal sealed partial class HomeStudioPage
{
    readonly ListView animationLibrary = new() { SelectionMode = ListViewSelectionMode.Single, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    readonly TextBox animationSearch = new() { PlaceholderText = "筛选这个角色的动作…" };
    readonly TextBlock animationHint = new() { Text = "选择角色，然后双击动作添加到播放头。", FontSize = 11, Opacity = .7, TextWrapping = TextWrapping.Wrap };
    readonly Dictionary<string, TimelineItem> timelineItems = [];
    bool selectingTimeline;
    string? libraryKey;
    const string OriginalPrefix = "$original/";
    double PropertyTime => selectedMarker is { } id && timelineItems.TryGetValue(id, out var clip) ? clip.Start : document?.Playhead ?? 0;
    void ConnectTimeline()
    {
        timeline.Repeat = true;
        timeline.PlayRequested += () => _ = SafeAsync(TogglePlaybackAsync);
        timeline.ClipSelected += id =>
        {
            if (!timelineItems.TryGetValue(id, out var item)) return;
            Pause(); selectedMarker = id; selectingTimeline = true;
            try { Select(item.Actor.Length == 0 ? SceneId : item.Actor); }
            finally { selectingTimeline = false; }
        };
        timeline.ClipEdited += (id, time, _) =>
        {
            var key = document?.TimelineKeys().FirstOrDefault(k => k.Id == id);
            if (key is null || document is null) return;
            Edit(() =>
            {
                document.MoveKey(id, time);
                selectedMarker = document.TimelineKeys().FirstOrDefault(k => k.Kind == key.Kind && k.CharacterKey == key.CharacterKey && Math.Abs(k.Time - document.Snap(time)) < 1e-8)?.Id;
            });
            BuildProperties();
        };
        timeline.AddRequested += (actor, kind, time) =>
        {
            if (document is null) return;
            selectedMarker = null; Select(actor.Length == 0 ? SceneId : actor);
            _ = SafeAsync(async () =>
            {
                await SeekAsync(time);
                if (kind == "motion") { ShowHomeAnimations(); animationHint.Text = "双击一个动作，从播放头开始。"; }
                else AddKey();
            });
        };
        timeline.ContextMenuFactory = HomeClipMenu;
        timeline.TrackMenuFactory = (actor, kind, time) =>
        {
            var menu = new MenuFlyout();
            menu.Items.Add(StudioMenus.Async(kind == "motion" ? "在这里开始动作…" : "在这里记录变化", async () =>
            {
                selectedMarker = null; Select(actor.Length == 0 ? SceneId : actor); await SeekAsync(time);
                if (kind == "motion") ShowHomeAnimations(); else AddKey();
            }, "\uE710", document is not null));
            return menu;
        };
        animationSearch.TextChanged += (_, _) => RefreshAnimationLibrary(force: true);
        animationLibrary.DoubleTapped += (_, args) =>
        {
            if (animationLibrary.SelectedItem is not ListViewItem { Tag: string name }) return;
            ApplyLibraryAnimation(name); args.Handled = true;
        };
    }
    void RefreshAnimationLibrary(bool force = false)
    {
        string? key = document?.SelectedCharacterKey;
        if (!force && key == libraryKey) return; libraryKey = key; animationLibrary.Items.Clear();
        var character = metadata?.Characters.FirstOrDefault(c => c.Key == key);
        if (character is null) { animationHint.Text = "先选择一个角色。"; return; }
        string query = animationSearch.Text.Trim();
        foreach (var name in new[] { "" }.Concat(character.Animations))
        {
            string label = name.Length == 0 ? "原始演出" : DisplayNames.Animation(name);
            if (!(label + " " + name).Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            var item = new ListViewItem { Tag = name, Content = new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis, IsTextSelectionEnabled = false }, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            ToolTipService.SetToolTip(item, name.Length == 0 ? "使用归档中的原始动作组合" : name);
            item.ContextRequested += (_, args) =>
            {
                var menu = new MenuFlyout();
                menu.Items.Add(StudioMenus.Item(SelectedMotionClip is null ? "从播放头开始" : "替换所选动作", () => ApplyLibraryAnimation(name), "\uE768"));
                menu.ShowAt(item); args.Handled = true;
            };
            animationLibrary.Items.Add(item);
        }
        animationHint.Text = "双击插入；选中时间轴的动作后，双击可直接替换。";
    }
    TimelineItem? SelectedMotionClip => selectedMarker is { } id && timelineItems.TryGetValue(id, out var item)
        && item.Kind == "motion" && item.Actor == document?.SelectedCharacterKey ? item : null;
    void ApplyLibraryAnimation(string name)
    {
        if (document?.SelectedCharacterKey is not { } key) return;
        double at = SelectedMotionClip?.Start ?? document.Playhead; var cue = document.Track(key).AnimationAt(at);
        Edit(() => document.SetAnimation(key, at, name.Length == 0 ? null : name, cue?.Loop, cue?.Speed ?? 1, cue?.SourceTime ?? 0));
        selectedMarker = document.TimelineKeys().FirstOrDefault(k => k.CharacterKey == key && k.Kind == "clip" && Math.Abs(k.Time - document.Snap(at)) < 1e-8)?.Id;
        RefreshTimeline(); BuildProperties();
    }
    MenuFlyout HomeClipMenu(string id)
    {
        var menu = new MenuFlyout(); if (!timelineItems.TryGetValue(id, out var item)) return menu;
        menu.Items.Add(StudioMenus.Async("播放此处", async () => { await SeekAsync(item.Start); playbackRequested = true; viewport.Play(); }, "\uE768"));
        if (item.Kind == "motion") menu.Items.Add(StudioMenus.Item("替换动作…", () => ShowHomeAnimations(), "\uE8D2"));
        if (item.Actor.Length > 0)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(StudioMenus.Async("导出此角色图片…", () => ExportCharacterAsync("frame"), "\uEB9F"));
            menu.Items.Add(StudioMenus.Async("导出此角色动画…", () => ExportCharacterAsync("clip"), "\uE714"));
        }
        if (!id.StartsWith(OriginalPrefix, StringComparison.Ordinal))
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(StudioMenus.Item("删除变化", () => Edit(() => { document?.DeleteKey(id); selectedMarker = null; }), "\uE74D"));
        }
        return menu;
    }
    void RefreshTimeline()
    {
        if (document is null) return;
        var result = new List<TimelineLane>(); var keys = document.TimelineKeys(); double duration = document.Project.Duration;
        timelineItems.Clear();
        foreach (var character in metadata?.Characters ?? [])
        {
            var clips = new List<TimelineItem>(); var cues = document.Track(character.Key).Animations.OrderBy(c => c.Time).ToArray();
            if (cues.Length == 0 || cues[0].Time > 0)
                clips.Add(new(OriginalPrefix + character.Key, character.Key, "motion", "原始演出", 0, cues.Length == 0 ? duration : cues[0].Time, CanMove: false));
            for (int i = 0; i < cues.Length; i++)
            {
                var cue = cues[i]; var key = keys.First(k => k.CharacterKey == character.Key && k.Kind == "clip" && k.Time == cue.Time);
                clips.Add(new(key.Id, character.Key, "motion", cue.Name is null ? "原始演出" : DisplayNames.Animation(cue.Name), cue.Time, i + 1 < cues.Length ? cues[i + 1].Time : duration));
            }
            result.Add(new(character.Key, "motion", character.Name + " · 动作", clips));
            var positions = keys.Where(k => k.CharacterKey == character.Key && k.Kind != "clip").Select(k =>
                new TimelineItem(k.Id, character.Key, "transform", "位置 / 缩放", k.Time, k.Time + 1d / document.Project.Output.Fps, IsKey: true)).ToArray();
            result.Add(new(character.Key, "transform", character.Name + " · 位置", positions));
        }
        result.Add(new("", "grade", "房间 · 画面", keys.Where(k => k.CharacterKey is null).Select(k =>
            new TimelineItem(k.Id, "", "grade", "画面调整", k.Time, k.Time + 1d / document.Project.Output.Fps, IsKey: true)).ToArray()));
        foreach (var item in result.SelectMany(lane => lane.Items)) timelineItems[item.Id] = item;
        if (selectedMarker is not null && !timelineItems.ContainsKey(selectedMarker)) selectedMarker = null;
        timeline.SetTracks(document.Project.Source.ToString(), duration, document.Project.Output.Fps, result, selectedMarker);
        timeline.Position = document.Playhead;
    }
    MenuFlyout HomeEditMenu()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(StudioMenus.Item("撤销", () => Edit(() => document?.Undo()), "\uE7A7", document?.CanUndo == true, "Ctrl+Z"));
        menu.Items.Add(StudioMenus.Item("重做", () => Edit(() => document?.Redo()), "\uE7A6", document?.CanRedo == true, "Ctrl+Y"));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(StudioMenus.Item("在播放头记录变化", AddKey, "\uE710", document is not null));
        return menu;
    }
}
