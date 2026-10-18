using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.WinUI;

internal sealed partial class Live2DStudioPage
{
    Task readyTask = Task.CompletedTask;
    JsonObject? handoffSource;

    MenuFlyout FileMenu()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(StudioMenus.Async("选择模型 / 新建…", () => SafeAsync(NewAsync), "\uE77B"));
        menu.Items.Add(StudioMenus.Async("打开 Live2D 项目…", () => SafeAsync(() => OpenAsync()), "\uE8E5", true, "Ctrl+O"));
        menu.Items.Add(StudioMenus.Async("保存", () => SafeAsync(() => SaveAsync()), "\uE74E", document is not null, "Ctrl+S"));
        menu.Items.Add(StudioMenus.Async("另存为…", () => SafeAsync(() => SaveAsync(true)), "\uE792", document is not null, "Ctrl+Shift+S"));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(StudioMenus.Async("恢复草稿", () => SafeAsync(RestoreDraftAsync), "\uE777", File.Exists(DraftPath)));
        return menu;
    }
    MenuFlyout EditMenu()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(StudioMenus.Item("撤销", () => Undo(false), "\uE7A7", document?.CanUndo == true, "Ctrl+Z"));
        menu.Items.Add(StudioMenus.Item("重做", () => Undo(true), "\uE7A6", document?.CanRedo == true, "Ctrl+Y"));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(StudioMenus.Item("所有参数恢复自动", () => EditLive(current => current.ResetParameters()), "\uE777", document is not null));
        menu.Items.Add(StudioMenus.Item("重置位置与缩放", ResetTransform, "\uE9A6", document is not null));
        return menu;
    }
    MenuFlyout ViewMenu()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(StudioMenus.Item("适应画布", viewport.FitCanvas, "\uE9A6", true, "F"));
        menu.Items.Add(StudioMenus.Item("标记入点", () => MarkRange(true), enabled: document is not null, shortcut: "I"));
        menu.Items.Add(StudioMenus.Item("标记出点", () => MarkRange(false), enabled: document is not null, shortcut: "O"));
        menu.Items.Add(StudioMenus.Item("清除区间", ClearRange, enabled: document?.Range is not null));
        return menu;
    }

    internal async Task NewAsync()
    {
        if (!Directory.Exists(window.Settings.ArchivePath)) { window.ShowResourceManager(); return; }
        var selection = await ResourcePicker.PickAsync(window,
            new("modelId", "模型", CommandFieldKind.Text, "") { LookupCommandId = "model.list" },
            new Dictionary<string, string?> { ["site"] = window.Settings.Site, ["lang"] = window.Settings.Language });
        if (selection is null) return;
        string label = selection.RelatedValues.TryGetValue("displayName", out var name) ? name ?? selection.Value : selection.Value;
        await OpenModelAsync(selection.Value, label);
    }
    internal Task OpenModelAsync(string model, string label, string? motion = null, string? expression = null,
        string? site = null, string? language = null)
    {
        readyTask = OpenModelCoreAsync(model, label, motion, expression, site, language); return readyTask;
    }
    async Task OpenModelCoreAsync(string model, string label, string? motion, string? expression, string? site, string? language)
    {
        if (!await ConfirmReplaceAsync()) return;
        string selectedSite = site ?? window.Settings.Site;
        var metadata = await ModelMetadataAsync(model, selectedSite);
        var next = Live2DDocument.Create(model, selectedSite, language ?? window.Settings.Language, metadata);
        if (motion is not null) next.SetMotion(motion);
        if (expression is not null) next.SetExpression(expression);
        await SetDocumentAsync(next);
        ToolTipService.SetToolTip(modelName, label);
    }
    internal Task OpenSceneActorAsync(SceneDocument source, string actor, JsonNode metadata, double playhead,
        TimelineRange? range, double? duration = null)
    {
        readyTask = OpenSceneActorCoreAsync(source, actor, metadata, playhead, range, duration); return readyTask;
    }
    async Task OpenSceneActorCoreAsync(SceneDocument source, string actor, JsonNode metadata, double playhead,
        TimelineRange? range, double? duration)
    {
        if (!await ConfirmReplaceAsync()) return;
        string model = metadata["id"]?.ToString() ?? throw new ArgumentException("所选人物缺少模型信息。");
        var original = source.Root;
        double lastFrame = duration ?? Math.Max(playhead, Math.Max(range?.LastFrameTime ?? 0,
            original["commands"]?.AsArray().OfType<JsonObject>().Select(row =>
                (double.TryParse(row["AtFrame"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double frame) ? frame / 30 : 0)
                + (double.TryParse(row["Duration"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) ? seconds : 0)).DefaultIfEmpty(3).Max() ?? 3));
        var next = Live2DDocument.FromSceneActor(source, actor, model, metadata, playhead, lastFrame, range);
        await SetDocumentAsync(next);
        handoffSource = original;
        Notice("已带入这个人物的完整演出；当前姿态、历史动作和所选区间均已保留。", false);
    }
    internal Task OpenAsync(string? path = null)
    {
        readyTask = OpenCoreAsync(path); return readyTask;
    }
    async Task OpenCoreAsync(string? path)
    {
        path ??= await window.PickFileAsync(extensions: [".json"]); if (path is null) return;
        var next = Live2DDocument.Open(path);
        if (!await ConfirmReplaceAsync()) return;
        await SetDocumentAsync(next);
        window.Settings.RememberProject(path); window.SaveSettings();
    }
    async Task SetDocumentAsync(Live2DDocument next)
    {
        SaveDraft(); scrubber.Cancel(); Pause(); previewTimer.Stop();
        viewport.InvalidateSource(); loadVersion++; editVersion++;
        document = next; presentedDocument = null; previewSessionId = 0;
        handoffSource = null; nativeDescription = null; loadedSourceKey = null; previewDirty = replayRequired = true; transformPending = false;
        dragOrigin = null; dragDocument = null; notice.IsOpen = false;
        RefreshControls();
        if (!Directory.Exists(window.Settings.ArchivePath))
        { Notice("项目已打开。请在素材库中选择对应归档以载入模型。", false); return; }
        await LoadParameterMetadataAsync(next);
        if (!ReferenceEquals(document, next)) return;
        RefreshControls(); await RefreshPreviewAsync(); SaveDraft();
    }
    async Task<JsonNode> ModelMetadataAsync(string model, string site)
    {
        var result = await window.QueryAsync("model.show", new Dictionary<string, string?> { ["modelId"] = model, ["site"] = site }, lifetime.Token);
        return result.Success && result.Result is { } metadata ? metadata.DeepClone()
            : throw new InvalidOperationException(result.Error ?? "无法读取所选模型。");
    }
    async Task LoadParameterMetadataAsync(Live2DDocument current)
    {
        var result = await window.QueryAsync("model.parameters", new Dictionary<string, string?>
            { ["modelId"] = current.ModelId, ["site"] = current.Site }, lifetime.Token);
        if (!ReferenceEquals(document, current)) return;
        if (!result.Success || result.Result is null) throw new InvalidOperationException(result.Error ?? "无法读取模型参数。");
        current.SetParameterMetadata(result.Result); nativeDescription = result.Result.DeepClone();
    }
    async Task<bool> ConfirmReplaceAsync()
    {
        if (!HasUnsavedChanges) return true;
        var dialog = new ContentDialog
        {
            XamlRoot = window.RootXaml, Title = "保存当前 Live2D 项目？", Content = "打开另一个模型或项目之前，可以先保存当前修改。",
            PrimaryButtonText = "保存", SecondaryButtonText = "不保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary) { await SaveAsync(); return !HasUnsavedChanges; }
        if (result == ContentDialogResult.Secondary) { SaveDraft(); return true; }
        return false;
    }
    internal async Task SaveAsync(bool saveAs = false)
    {
        if (document is null) return;
        await scrubber.FlushAsync();
        string? destination = !saveAs ? document.FilePath : null;
        destination ??= await window.PickFileAsync(true, document.FilePath ?? document.ModelId + ".live2d.json", [".json"]);
        if (destination is null) return; SaveTo(destination);
    }
    void SaveTo(string destination)
    {
        if (document is null) return;
        document.Save(destination); window.Settings.RememberProject(destination); window.SaveSettings();
        RefreshHeader(); SaveDraft(); window.SetStatus("已保存 Live2D 项目：" + Path.GetFileName(destination));
    }
    internal void SaveDraft()
    {
        if (document is null) return;
        draftTimer.Stop();
        try
        {
            DesktopSettings.AtomicWrite(DraftPath, document.ToDraft().ToJsonString(DesktopSettings.JsonOptions));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { window.SetStatus("Live2D 草稿保存失败：" + error.Message); }
    }
    async Task RestoreDraftAsync()
    {
        if (!File.Exists(DraftPath)) return;
        var draft = JsonNode.Parse(await File.ReadAllTextAsync(DraftPath, lifetime.Token)) as JsonObject ?? throw new InvalidDataException("Live2D 草稿无效。");
        var next = Live2DDocument.FromDraft(draft);
        if (!await ConfirmReplaceAsync()) return;
        await SetDocumentAsync(next);
    }

    static string? Empty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    ScenePreviewSource PreviewSource(SceneSnapshot input, Live2DDocument current)
    {
        double ratio = Math.Min(1, Math.Min(960d / current.Width, 720d / current.Height));
        return new(window.Settings.ArchivePath, input.FilePath,
            NativeDependencies.CoreLibrary(Empty(window.Settings.NativeLibraryPath), Empty(window.Settings.CubismSdkPath)),
            NativeDependencies.MediaTool("ffmpeg", Empty(window.Settings.FFmpegPath)),
            Math.Max(16, (int)Math.Round(current.Width * ratio)), Math.Max(16, (int)Math.Round(current.Height * ratio)),
            TimelineRange.Full(current.Duration, 30).End);
    }
    async Task RefreshPreviewAsync()
    {
        if (disposed || document is null) return;
        previewTimer.Stop(); long version = ++loadVersion; await previewGate.WaitAsync(lifetime.Token);
        try
        {
            if (version != loadVersion || disposed || document is null) return;
            var current = document; long edited = editVersion; double time = current.Time;
            if (!Directory.Exists(window.Settings.ArchivePath)) { Notice("请先选择项目使用的素材库。", false); return; }
            await viewport.PauseForEditingAsync();
            var input = current.CreateSnapshot(Path.Combine(DesktopSettings.DataDirectory, "live2d-preview"));
            string key = current.ModelId + "\n" + current.Site + "\n" + current.Width + "x" + current.Height + "\n"
                + current.ToSceneDocument().Root["source"]?.ToJsonString() + "\n" + window.Settings.ArchivePath;
            try
            {
                var source = PreviewSource(input, current);
                if (viewport.HasCurrentSource && loadedSourceKey == key) await viewport.CommitSceneProjectAsync(source);
                else { presentedDocument = null; await viewport.LoadSceneAsync(source); }
                previewInput?.Dispose(); previewInput = input; loadedSourceKey = key;
                if (!ReferenceEquals(document, current) || disposed) return;
                presentedDocument = current; previewSessionId = viewport.LastFrame?.SessionId ?? 0;
                await viewport.SeekAsync(Math.Clamp(time, 0, current.Duration), lifetime.Token);
                viewport.SelectedTargetId = current.ActorId; previewDirty = edited != editVersion;
                if (!previewDirty) replayRequired = transformPending = false;
                await ReadParametersAsync(); RefreshHeader();
                if (previewDirty && active) { previewTimer.Stop(); previewTimer.Start(); }
            }
            catch { if (!ReferenceEquals(previewInput, input)) input.Dispose(); throw; }
        }
        finally { previewGate.Release(); }
    }
    async Task ApplyPreviewEditsAsync()
    {
        if (disposed || document is null || !previewDirty) return;
        if (replayRequired || !viewport.HasCurrentSource) { await RefreshPreviewAsync(); return; }
        previewTimer.Stop(); await previewGate.WaitAsync(lifetime.Token);
        try
        {
            if (disposed || document is null || !previewDirty) return;
            if (replayRequired || !viewport.HasCurrentSource)
            { previewTimer.Start(); return; }
            var current = document; long edited = editVersion;
            var transform = current.Transform;
            var rows = current.CurrentEditRows();
            await viewport.PauseForEditingAsync();
            if (transformPending)
                await viewport.SetIsolatedTransformAsync(new(transform.X, transform.Y, transform.Scale, transform.Rotation, transform.Opacity));
            if (rows.Count > 0) await viewport.ApplySceneEditsAsync(rows);
            // Keep the displayed native pose. The committed history is replayed only
            // by a later seek/play, so a slider never rebuilds the model or advances time.
            var input = current.CreateSnapshot(Path.Combine(DesktopSettings.DataDirectory, "live2d-preview"));
            try
            {
                await viewport.CommitSceneProjectAsync(PreviewSource(input, current));
                previewInput?.Dispose(); previewInput = input;
                previewDirty = edited != editVersion; transformPending = current.Transform != transform;
                await ReadParametersAsync(); RefreshHeader();
            }
            catch { if (!ReferenceEquals(previewInput, input)) input.Dispose(); throw; }
            if (previewDirty && active) { previewTimer.Stop(); previewTimer.Start(); }
        }
        finally { previewGate.Release(); }
    }
    async Task FlushEditsAsync()
    {
        previewTimer.Stop(); await scrubber.FlushAsync();
        if (document is not null && (previewDirty || !viewport.HasCurrentSource))
        { if (!viewport.HasCurrentSource) await RefreshPreviewAsync(); else await ApplyPreviewEditsAsync(); }
        await previewGate.WaitAsync(lifetime.Token); previewGate.Release();
        if (previewDirty) await ApplyPreviewEditsAsync();
    }
    async Task SeekAsync(double time, CancellationToken cancellationToken = default)
    {
        if (document is null) return;
        Pause(); document.Time = Math.Clamp(time, 0, document.Duration); RefreshHeader();
        if (previewDirty) replayRequired = true;
        if (previewDirty || !viewport.HasCurrentSource) await RefreshPreviewAsync();
        cancellationToken.ThrowIfCancellationRequested();
        if (viewport.HasCurrentSource) await viewport.SeekAsync(document.Time, cancellationToken);
        await ReadParametersAsync();
    }
    async Task StepAsync(int direction)
    {
        if (document is null) return;
        await scrubber.FlushAsync(); await SeekAsync((Math.Round(document.Time * 30) + direction) / 30);
    }
    async Task TogglePlayAsync()
    {
        if (document is null) return;
        if (viewport.IsPlaying || playbackRequested) { Pause(); return; }
        await FlushEditsAsync(); if (!viewport.HasCurrentSource) return;
        var range = PlaybackRange;
        if (document.Time < range.Start || document.Time >= range.LastFrameTime - 1d / 60) await SeekAsync(range.Start);
        playbackRequested = true; ((FontIcon)playButton.Content).Glyph = "\uE769";
        viewport.Play(range.End);
    }
    void OnFrame(PreviewFrame frame)
    {
        if (disposed || document is null || !ReferenceEquals(presentedDocument, document) || frame.SessionId != previewSessionId) return;
        document.Time = Math.Clamp(frame.Time, 0, document.Duration);
        RefreshTransport(); RefreshTimeDependentControls();
        ((FontIcon)playButton.Content).Glyph = frame.IsPlaying || playbackRequested ? "\uE769" : "\uE768";
        if (activeTab == "parameters" && DateTime.UtcNow - lastParameterRead > TimeSpan.FromMilliseconds(200)) _ = SafeAsync(ReadParametersAsync);
        if (!active || !playbackRequested || frame.IsPlaying || frame.Time < PlaybackRange.LastFrameTime - 1d / 60) return;
        if (!document.Loop) { Pause(); return; }
        if (restartingLoop) return; restartingLoop = true;
        _ = SafeAsync(async () =>
        {
            try
            {
                Task rewind = SeekAsync(PlaybackRange.Start); long resumeVersion = pauseVersion;
                await rewind;
                if (!active || document is null || resumeVersion != pauseVersion) return;
                playbackRequested = true; viewport.Play(PlaybackRange.End);
            }
            finally { restartingLoop = false; }
        });
    }
    Task parameterReadTask = Task.CompletedTask;
    Task ReadParametersAsync()
    {
        if (readingParameters) return parameterReadTask;
        if (document is null || !ReferenceEquals(presentedDocument, document) || !viewport.HasCurrentSource || disposed || previewDirty) return Task.CompletedTask;
        parameterReadTask = ReadParametersCoreAsync(document); return parameterReadTask;
    }
    async Task ReadParametersCoreAsync(Live2DDocument current)
    {
        long edited = editVersion; readingParameters = true; lastParameterRead = DateTime.UtcNow;
        try
        {
            var values = await viewport.ReadActorParametersAsync(current.ActorId, lifetime.Token);
            if (ReferenceEquals(document, current) && edited == editVersion && !previewDirty)
            { current.UpdateParameterValues(values); RefreshParameterValues(); }
        }
        finally { readingParameters = false; }
    }
    void MoveModel(PreviewDrag drag)
    {
        if (document is null || drag.TargetId != document.ActorId) return;
        if (!ReferenceEquals(dragDocument, document) || dragOrigin is null) { dragDocument = document; dragOrigin = document.Transform; }
        var origin = dragOrigin!;
        EditTransform(current => current.SetTransform(origin with { X = origin.X + drag.DeltaX, Y = origin.Y + drag.DeltaY }));
        RefreshTransformControls();
        if (drag.IsCompleted) { dragOrigin = null; dragDocument = null; }
    }
    void ScaleModel(PreviewScale scale)
    {
        if (document is null || scale.TargetId != document.ActorId) return;
        EditTransform(current => current.SetTransform(current.Transform with { Scale = Math.Clamp(current.Transform.Scale * scale.Factor, .01, 10) }));
        RefreshTransformControls();
    }
    async Task ExportAsync(string kind)
    {
        if (document is null) return;
        Pause(); await FlushEditsAsync();
        var current = document;
        var scene = StudioSceneDocument.CreateExportView(current.ToSceneDocument(), TimelineRange.Full(current.Duration, 30).Duration);
        await StudioExportDialog.ShowSceneAsync(window, scene, current.Time, singleActor: true, initialKind: kind,
            preserveTransparency: true, initialRange: current.Range);
    }
    internal async Task WaitForReadyAsync()
    {
        Task pending;
        do { pending = readyTask; await pending; } while (!ReferenceEquals(pending, readyTask));
        await FlushEditsAsync(); await ReadParametersAsync();
    }
    internal void Deactivate()
    {
        active = false; scrubber.Cancel(); Pause(); previewTimer.Stop(); draftTimer.Stop(); SaveDraft();
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        Deactivate(); disposed = true; loadVersion++; lifetime.Cancel();
        await previewGate.WaitAsync();
        try { await viewport.DisposeAsync(); previewInput?.Dispose(); }
        finally { previewGate.Release(); lifetime.Dispose(); }
    }
}
