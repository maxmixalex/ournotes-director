using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;
using Windows.ApplicationModel.DataTransfer;

namespace OurNotes.Director.WinUI;

internal sealed partial class CreativeScenePage
{
    MenuFlyout FileMenu()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(StudioMenus.Async("新建空白场景", () => Safe(() => NewAsync()), "\uE710"));
        menu.Items.Add(StudioMenus.Async("打开场景…", () => Safe(() => OpenAsync()), "\uE8E5"));
        menu.Items.Add(StudioMenus.Async("打开原剧情…", () => Safe(OpenStoryAsync), "\uE8A5"));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(StudioMenus.Async("另存为…", () => Safe(() => SaveAsync(true)), "\uE792"));
        menu.Items.Add(StudioMenus.Async("恢复未保存草稿", () => Safe(() => OpenAsync(draftPath)), "\uE777", File.Exists(draftPath)));
        return menu;
    }
    MenuFlyout EditMenu()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(StudioMenus.Async("撤销", () => Safe(() => UndoAsync(false)), "\uE7A7", undo.Count > 0, "Ctrl+Z"));
        menu.Items.Add(StudioMenus.Async("重做", () => Safe(() => UndoAsync(true)), "\uE7A6", redo.Count > 0, "Ctrl+Y"));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(StudioMenus.Async("粘贴场景 JSON…", () => Safe(PasteAsync), "\uE943"));
        return menu;
    }
    MenuFlyout ViewMenu()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(StudioMenus.Item("画布适应窗口", viewport.FitCanvas, "\uE9A6", true, "F"));
        menu.Items.Add(StudioMenus.Item("画布设置", () => { selectedActor = selectedClip = null; BuildInspector(); }, "\uE740"));
        menu.Items.Add(StudioMenus.Async("场景 JSON…", () => Safe(ShowJsonAsync), "\uE943"));
        menu.Items.Add(StudioMenus.Async("查看编译后的原生指令…", () => Safe(ShowNativeAsync), "\uE8A5"));
        return menu;
    }
    MenuFlyout ActorMenu()
    {
        var menu = new MenuFlyout();
        if (selectedActor is { Length: > 0 })
        {
            menu.Items.Add(StudioMenus.Async("导出当前姿态图片…", () => Safe(() => ExportAsync("frame", true)), "\uEB9F"));
            menu.Items.Add(StudioMenus.Async("导出这个角色的演出…", () => Safe(() => ExportAsync("clip", true)), "\uE714"));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(StudioMenus.Item("选择动作", () => { assetKind.SelectedIndex = 0; _ = Safe(LoadAssetsAsync); }));
            menu.Items.Add(StudioMenus.Item("选择表情", () => { assetKind.SelectedIndex = 1; _ = Safe(LoadAssetsAsync); }));
            menu.Items.Add(StudioMenus.Async("添加声音…", () => Safe(ImportVoiceAsync), "\uE8D6"));
        }
        else menu.Items.Add(StudioMenus.Async("添加角色…", () => Safe(AddActorAsync), "\uE77B"));
        return menu;
    }
    MenuFlyout ClipMenu(JsonObject clip)
    {
        string id = S(clip, "id"); var menu = new MenuFlyout();
        menu.Items.Add(StudioMenus.Async("播放此处", () => Safe(async () => { await SeekAsync(N(document.Clip(id), "start")); viewport.Play(); }), "\uE768"));
        menu.Items.Add(StudioMenus.Async("复制片段", () => Safe(() => EditAsync(() =>
        { var copy = (JsonObject)document.Clip(id).DeepClone(); copy["id"] = CreativeScene.Id(); copy["start"] = N(copy, "start") + N(copy, "duration"); document.Clips.Add(copy); selectedClip = S(copy, "id"); })), "\uE8C8"));
        menu.Items.Add(StudioMenus.Async(clip["enabled"]?.GetValue<bool>() == false ? "启用" : "停用", () => Safe(() => EditAsync(() => document.Clip(id)["enabled"] = document.Clip(id)["enabled"]?.GetValue<bool>() == false))));
        menu.Items.Add(StudioMenus.Async("片段原生 JSON…", () => Safe(() => EditClipJsonAsync(id)), "\uE943"));
        menu.Items.Add(new MenuFlyoutSeparator()); menu.Items.Add(StudioMenus.Async("删除", () => Safe(DeleteClipAsync), "\uE74D"));
        return menu;
    }
    internal async Task NewAsync(bool original = false)
    {
        if (original) { await OpenStoryAsync(); return; }
        SaveDraft(); viewport.InvalidateSource(); previewDirty = true;
        document = CreativeScene.Blank(window.Settings.Site, window.Settings.Language); savedText = document.ToJson();
        selectedActor = selectedClip = path = null; directory = Environment.CurrentDirectory; playhead = 0; undo.Clear(); redo.Clear();
        Refresh(); viewport.SetEmptyCanvas(1920, 1080); notice.IsOpen = false;
    }
    internal async Task OpenAsync(string? filename = null)
    {
        filename ??= await window.PickFileAsync(extensions: [".json"]); if (filename is null) return;
        string text = await File.ReadAllTextAsync(filename);
        var node = JsonNode.Parse(text)?.AsObject() ?? throw new ArgumentException("文件不是场景 JSON。");
        SaveDraft();
        if (S(node, "format") == CreativeScene.Format)
        {
            var next = new CreativeScene(node); document = next; path = filename;
            directory = Path.GetDirectoryName(Path.GetFullPath(filename))!; savedText = next.ToJson();
            selectedActor = selectedClip = null; playhead = 0; undo.Clear(); redo.Clear(); previewDirty = true; Refresh(); await ReloadAsync();
        }
        else await ImportNativeAsync(text, Path.GetDirectoryName(Path.GetFullPath(filename))!);
    }
    async Task OpenStoryAsync()
    {
        var selected = await ResourcePicker.PickAsync(window, new("adv", "原剧情", CommandFieldKind.Text, "") { LookupCommandId = "story.list" },
            new Dictionary<string, string?> { ["site"] = window.Settings.Site, ["lang"] = window.Settings.Language });
        if (selected is null) return;
        var native = SceneTemplates.Create("story-patch", new SceneSource(selected.Value, window.Settings.Site, window.Settings.Language));
        await ImportNativeAsync(native.Text, Environment.CurrentDirectory);
    }
    async Task ImportNativeAsync(string json, string baseDirectory)
    {
        if (!Directory.Exists(window.Settings.ArchivePath)) throw new DirectoryNotFoundException("请先在素材库中选择已经下载的归档。");
        SaveDraft(); viewport.Pause();
        notice.Severity = InfoBarSeverity.Informational; notice.Message = "正在读取原剧情的实际演出和语音时间…"; notice.IsOpen = true;
        var expanded = StudioSceneDocument.FromSceneDocument(SceneDocument.Parse(json, baseDirectory: baseDirectory));
        await Task.Run(() => { var archive = new Archive(window.Settings.ArchivePath); expanded.ExpandInheritedSections(archive); expanded.ResolveSourceModels(archive); });
        var native = expanded.ToSceneDocument();
        var snapshot = native.CreateSnapshot(Path.Combine(DesktopSettings.DataDirectory, "creative-preview"));
        try
        {
            var size = PreviewSize(native.Output.Width, native.Output.Height);
            await viewport.LoadSceneAsync(Source(snapshot.FilePath, size.Width, size.Height, 36000, PreviewDurationMode.Automatic));
            var timing = await viewport.ResolveSceneTimingAsync();
            var imported = CreativeScene.FromPerformance(native.Root, timing);
            foreach (var actor in expanded.Actors)
                if (!string.IsNullOrWhiteSpace(actor.Model) && !imported.Actors.OfType<JsonObject>().Any(a => S(a, "id") == actor.Id))
                    imported.Actors.Add(new JsonObject { ["id"] = actor.Id, ["name"] = actor.Id, ["model"] = actor.Model, ["fromSource"] = true });
            document = imported; path = null; directory = baseDirectory; savedText = ""; selectedActor = selectedClip = null; playhead = 0;
            undo.Clear(); redo.Clear(); previewDirty = true; Refresh(); SaveDraft(); notice.IsOpen = false;
        }
        finally { snapshot.Dispose(); }
        await ReloadAsync();
    }
    internal async Task SaveAsync(bool saveAs = false)
    {
        string? destination = path;
        if (saveAs || destination is null) destination = await window.PickFileAsync(true, path ?? "scene.json", [".json"]);
        if (destination is null) return;
        // Resource paths are resolved before changing the project's base directory.
        foreach (var clip in document.Clips.OfType<JsonObject>())
            if (clip["data"]?["AudioPath"] is JsonValue file) clip["data"]!["AudioPath"] = Path.GetFullPath(file.ToString(), directory);
        DesktopSettings.AtomicWrite(destination, document.ToJson()); path = destination; directory = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        savedText = document.ToJson(); Refresh();
    }
    async Task AddActorAsync()
    {
        if (!Directory.Exists(window.Settings.ArchivePath)) { window.ShowResourceManager(); return; }
        var selected = await ResourcePicker.PickAsync(window, new("modelId", "角色", CommandFieldKind.Text, "") { LookupCommandId = "model.list" },
            new Dictionary<string, string?> { ["site"] = document.Site });
        if (selected is null) return;
        await EditAsync(() => { selectedActor = document.AddActor(selected.Value, selected.RelatedValues.GetValueOrDefault("displayName") ?? selected.Value); selectedClip = null; });
        viewport.SelectedTargetId = selectedActor; await LoadAssetsAsync();
    }
    async Task ImportVoiceAsync()
    {
        string? file = await window.PickFileAsync(extensions: [".wav", ".ogg", ".mp3", ".m4a", ".flac", ".aac"]);
        if (file is not null) await InsertVoiceFileAsync(file, Path.GetFileNameWithoutExtension(file), playhead);
    }
    async Task InsertVoiceFileAsync(string file, string label, double at)
    {
        file = Path.GetFullPath(file); string ffmpeg = Ffmpeg();
        var cue = await Task.Run(() => VoiceFile.Decode(file, -1, ffmpeg));
        var peaks = VoiceFile.Peaks(cue); string actor = selectedActor ?? "";
        await EditAsync(() =>
        {
            selectedClip = document.AddClip(actor, "voice", at, cue.Duration, new JsonObject { ["cmd"] = "Voice", ["AudioPath"] = file,
                ["AudioIn"] = 0, ["AudioDuration"] = cue.Duration, ["IgnoreLipSync"] = actor.Length == 0 }, label);
            var clip = document.Clip(selectedClip); clip["sourceDuration"] = cue.Duration;
            clip["waveform"] = new JsonArray(peaks.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray());
        });
    }
    async Task InsertAssetAsync(ResultRow row, string kind, double at)
    {
        if (kind == "voice")
        {
            var selection = row.Selection("voiceId");
            string origin = selection.RelatedValues.GetValueOrDefault("adv") ?? "";
            string key = Archive.Hash(System.Text.Encoding.UTF8.GetBytes(row.Data.ToJsonString() + origin + document.Site));
            string folder = Path.Combine(DesktopSettings.DataDirectory, "creative-voice-cache"); Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, key + ".audio");
            if (!File.Exists(file))
            {
                var result = await window.QueryAsync("voice.extract", new Dictionary<string, string?> { ["voiceId"] = selection.Value, ["adv"] = origin, ["site"] = document.Site, ["out"] = file });
                if (!result.Success || !File.Exists(file)) throw new IOException(result.Error ?? "语音提取未完成。");
            }
            await InsertVoiceFileAsync(file, row.Title, at); return;
        }
        if (string.IsNullOrEmpty(selectedActor)) throw new InvalidOperationException("先选择要使用这个素材的角色。");
        string actor = selectedActor; double duration = row.Data is JsonObject d ? N(d, "duration", kind == "expression" ? 1 : 3) : 3;
        await EditAsync(() =>
        {
            var data = new JsonObject { ["cmd"] = kind == "expression" ? "Expression" : "Motion", ["TargetName"] = actor,
                [kind == "expression" ? "ExpressionName" : "MotionName"] = row.Id, ["MotionFadeIn"] = .15 };
            selectedClip = document.AddClip(actor, kind, at, Math.Max(1d / 30, duration), data, row.Title);
        });
    }
    async Task DropAssetAsync(string json, double at)
    {
        var data = JsonNode.Parse(json)!.AsObject(); selectedActor = data["actor"]?.ToString();
        var row = new ResultRow(S(data, "resource"), S(data, "label"), "", data["data"]!.DeepClone());
        await InsertAssetAsync(row, S(data, "kind"), at);
    }
    async Task AddToTrackAsync(string actor, string kind, double at)
    {
        selectedActor = actor.Length == 0 ? null : actor; selectedClip = null; playhead = at;
        if (kind is "motion" or "expression" or "voice")
        { assetKind.SelectedIndex = kind == "motion" ? 0 : kind == "expression" ? 1 : 2; RefreshActors(); BuildInspector(); await LoadAssetsAsync(); return; }
        if (kind == "transform")
        {
            var target = viewport.LastFrame?.Targets.FirstOrDefault(t => t.Id == actor);
            await AddTransformAsync(actor, target is null ? .5 : target.X + target.Width / 2, target is null ? .5 : target.Y + target.Height / 2, target?.Height ?? .95); return;
        }
        await EditAsync(() =>
        {
            var values = kind == "camera" ? new JsonObject { ["framing"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["zoom"] = 1, ["roll"] = 0 } }
                : new JsonObject { ["grade"] = new JsonObject { ["exposure"] = 0, ["contrast"] = 1, ["saturation"] = 1, ["vignette"] = 0 } };
            selectedClip = document.AddClip("", kind, at, 1, new JsonObject { ["cmd"] = kind == "camera" ? "Director.Camera" : "Director.Scene", ["Values"] = values, ["Duration"] = 1 }, ClipTimeline.KindName(kind));
        });
    }
    static (int Width, int Height) PreviewSize(int width, int height)
    { double scale = Math.Min(1, Math.Min(960d / width, 720d / height)); return (Math.Max(16, (int)Math.Round(width * scale)), Math.Max(16, (int)Math.Round(height * scale))); }
    static string? Empty(string text) => string.IsNullOrWhiteSpace(text) ? null : text;
    string Ffmpeg() => NativeDependencies.MediaTool("ffmpeg", Empty(window.Settings.FFmpegPath));
    ScenePreviewSource Source(string file, int width, int height, double duration, PreviewDurationMode mode = PreviewDurationMode.Fixed) => new(window.Settings.ArchivePath, file,
        NativeDependencies.CoreLibrary(Empty(window.Settings.NativeLibraryPath), Empty(window.Settings.CubismSdkPath)), Ffmpeg(), width, height, duration, mode);
    JsonObject Compiled()
    {
        var compiled = document.Compile();
        foreach (var row in compiled["commands"]!.AsArray().OfType<JsonObject>())
            if (row["AudioPath"] is JsonValue file) row["AudioPath"] = Path.GetFullPath(file.ToString(), directory);
        return compiled;
    }
    async Task ReloadAsync()
    {
        if (disposed) return; long version = ++loadVersion;
        await loadGate.WaitAsync();
        try
        {
            if (version != loadVersion || disposed) return;
            if (document.Actors.Count == 0 && document.Clips.Count == 0)
            { viewport.SetEmptyCanvas((int)N(document.Canvas, "width"), (int)N(document.Canvas, "height")); previewDirty = false; return; }
            if (!Directory.Exists(window.Settings.ArchivePath)) { ShowError("请在素材库中选择归档；场景已保留，不需要选择原剧情。 "); return; }
            double restore = playhead;
            var parsed = SceneDocument.Parse(Compiled().ToJsonString(), baseDirectory: directory);
            var input = parsed.CreateSnapshot(Path.Combine(DesktopSettings.DataDirectory, "creative-preview"));
            try
            {
                var size = PreviewSize(parsed.Output.Width, parsed.Output.Height);
                await viewport.LoadSceneAsync(Source(input.FilePath, size.Width, size.Height, document.Duration + 1d / 30));
                previewInput?.Dispose(); previewInput = input; previewDirty = false;
                if (restore > 0) await viewport.SeekAsync(Math.Min(restore, document.Duration));
                viewport.SelectedTargetId = selectedActor;
            }
            catch { if (!ReferenceEquals(previewInput, input)) input.Dispose(); throw; }
        }
        finally { loadGate.Release(); }
    }
    async Task SeekAsync(double seconds)
    {
        viewport.Pause(); if (previewDirty) await ReloadAsync();
        playhead = Math.Clamp(seconds, 0, document.Duration); timeline.Position = playhead; timeline.IsPlaying = false;
        if (viewport.HasCurrentSource) await viewport.SeekAsync(playhead);
    }
    async Task TogglePlayAsync()
    {
        if (viewport.IsPlaying) { viewport.Pause(); timeline.IsPlaying = false; return; }
        if (previewDirty || !viewport.HasCurrentSource) await ReloadAsync();
        if (!viewport.HasCurrentSource) return;
        if (playhead >= document.Duration - 1d / 30) await SeekAsync(0);
        viewport.Play(); timeline.IsPlaying = true;
    }
    async Task ExportAsync(string kind, bool actorOnly = false)
    {
        viewport.Pause();
        var native = SceneDocument.Parse(Compiled().ToJsonString(), baseDirectory: directory);
        var editor = StudioSceneDocument.FromSceneDocument(native);
        string? actor = actorOnly ? selectedActor : null; JsonNode? metadata = null;
        if (actor is not null)
        {
            string? model = viewport.LastFrame?.ActorAnimations?.FirstOrDefault(a => a.TargetId == actor)?.Model
                ?? document.Actors.OfType<JsonObject>().FirstOrDefault(a => S(a, "id") == actor)?["model"]?.ToString();
            if (model is null) throw new InvalidOperationException("请先载入所选角色。");
            metadata = await Task.Run(() => new Archive(window.Settings.ArchivePath).Model(document.Site, model));
        }
        await StudioExportDialog.ShowSceneAsync(window, editor, playhead, initialKind: kind, actorId: actor, actorMetadata: metadata);
    }
}
