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
    long openVersion;
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
        long version = ++openVersion;
        if (!Directory.Exists(window.Settings.ArchivePath)) { window.ShowResourceManager(); return; }
        var selection = await ResourcePicker.PickAsync(window,
            new("modelId", "模型", CommandFieldKind.Text, "") { LookupCommandId = "model.list" },
            new Dictionary<string, string?> { ["site"] = window.Settings.Site, ["lang"] = window.Settings.Language });
        if (selection is null || version != openVersion) return;
        string label = selection.RelatedValues.TryGetValue("displayName", out var name) ? name ?? selection.Value : selection.Value;
        await OpenModelAsync(selection.Value, label);
    }
    internal Task OpenModelAsync(string model, string label, string? motion = null, string? expression = null,
        string? site = null, string? language = null)
    {
        readyTask = OpenModelCoreAsync(model, label, motion, expression, site, language, ++openVersion); return readyTask;
    }
    async Task OpenModelCoreAsync(string model, string label, string? motion, string? expression, string? site, string? language, long version)
    {
        if (!await ConfirmReplaceAsync() || version != openVersion) return;
        string selectedSite = site ?? window.Settings.Site;
        var metadata = await ModelMetadataAsync(model, selectedSite);
        if (version != openVersion) return;
        var next = Live2DDocument.Create(model, selectedSite, language ?? window.Settings.Language, metadata);
        if (motion is not null) next.SetMotion(motion);
        if (expression is not null) next.SetExpression(expression);
        await SetDocumentAsync(next);
        if (version != openVersion || !ReferenceEquals(document, next)) return;
        ToolTipService.SetToolTip(modelName, label);
    }
    internal Task OpenSceneActorAsync(SceneDocument source, string actor, JsonNode metadata, double playhead,
        TimelineRange? range, double? duration = null)
    {
        readyTask = OpenSceneActorCoreAsync(source, actor, metadata, playhead, range, duration, ++openVersion); return readyTask;
    }
    async Task OpenSceneActorCoreAsync(SceneDocument source, string actor, JsonNode metadata, double playhead,
        TimelineRange? range, double? duration, long version)
    {
        if (!await ConfirmReplaceAsync() || version != openVersion) return;
        string model = metadata["id"]?.ToString() ?? throw new ArgumentException("所选人物缺少模型信息。");
        var original = source.Root;
        double lastFrame = duration ?? Math.Max(playhead, Math.Max(range?.LastFrameTime ?? 0,
            original["commands"]?.AsArray().OfType<JsonObject>().Select(row =>
                (double.TryParse(row["AtFrame"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double frame) ? frame / 30 : 0)
                + (double.TryParse(row["Duration"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) ? seconds : 0)).DefaultIfEmpty(3).Max() ?? 3));
        var next = Live2DDocument.FromSceneActor(source, actor, model, metadata, playhead, lastFrame, range);
        await SetDocumentAsync(next);
        if (version != openVersion || !ReferenceEquals(document, next)) return;
        handoffSource = original;
        Notice("已带入这个人物的完整演出；当前姿态、历史动作和所选区间均已保留。", false);
    }
    internal Task OpenAsync(string? path = null)
    {
        readyTask = OpenCoreAsync(path, ++openVersion); return readyTask;
    }
    async Task OpenCoreAsync(string? path, long version)
    {
        path ??= await window.PickFileAsync(extensions: [".json"]); if (path is null || version != openVersion) return;
        var next = Live2DDocument.Open(path);
        if (!await ConfirmReplaceAsync() || version != openVersion) return;
        await SetDocumentAsync(next);
        if (version != openVersion || !ReferenceEquals(document, next)) return;
        window.Settings.RememberProject(path); window.SaveSettings();
    }
    async Task SetDocumentAsync(Live2DDocument next)
    {
        actorCanvasCancellation?.Cancel(); resolutionTimer?.Stop(); previewSizeOverride = null;
        SaveDraft(); scrubber.Cancel(); Pause(); previewTimer.Stop();
        viewport.InvalidateSource(); loadVersion++; editVersion++; seekVersion++; requestedSeek = null;
        document = next; presentedDocument = null; previewSessionId = 0;
        handoffSource = null; nativeDescription = null; loadedSourceKey = null; previewDirty = replayRequired = true; transformPending = false;
        modelDrag = null; notice.IsOpen = false;
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
        draftTimer.Stop(); draftPending = false;
        viewport.RecordPreviewOperation("draft.begin");
        try
        {
            DesktopSettings.AtomicWrite(DraftPath, document.ToDraft().ToJsonString(DesktopSettings.JsonOptions));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { window.SetStatus("Live2D 草稿保存失败：" + error.Message); }
        finally { viewport.RecordPreviewOperation("draft.end"); }
    }
    Task RestoreDraftAsync()
    {
        readyTask = RestoreDraftCoreAsync(++openVersion); return readyTask;
    }
    async Task RestoreDraftCoreAsync(long version)
    {
        if (!File.Exists(DraftPath)) return;
        var draft = JsonNode.Parse(await File.ReadAllTextAsync(DraftPath, lifetime.Token)) as JsonObject ?? throw new InvalidDataException("Live2D 草稿无效。");
        if (version != openVersion) return;
        var next = Live2DDocument.FromDraft(draft);
        if (!await ConfirmReplaceAsync() || version != openVersion) return;
        await SetDocumentAsync(next);
    }

    static string? Empty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    ScenePreviewSource PreviewSource(SceneSnapshot input, Live2DDocument current)
    {
        viewport.SetCanvasDimensions(current.Width, current.Height);
        var size = DesiredPreviewSize(current);
        return new(window.Settings.ArchivePath, input.FilePath,
            NativeDependencies.CoreLibrary(Empty(window.Settings.NativeLibraryPath), Empty(window.Settings.CubismSdkPath)),
            NativeDependencies.MediaTool("ffmpeg", Empty(window.Settings.FFmpegPath)),
            size.Width, size.Height, TimelineRange.Full(current.Duration, 30).End,
            Supersample: size.Supersample, SegmentedCpuFrames: true);
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
            var input = current.CreateSnapshot(Path.Combine(DesktopSettings.DataDirectory, "live2d-preview"), selectedMotionOnly: true);
            var source = PreviewSource(input, current); var transform = current.Transform;
            string key = current.ModelId + "\n" + current.Site + "\n" + current.Width + "x" + current.Height + "\n"
                + current.Canvas + "\n"
                + current.ToSceneDocument().Root["source"]?.ToJsonString() + "\n" + window.Settings.ArchivePath;
            try
            {
                await viewport.PauseForEditingAsync();
                if (!ReferenceEquals(document, current) || version != loadVersion || disposed) return;
                if (viewport.HasCurrentSource && loadedSourceKey == key)
                {
                    // Undo and an immediate seek after a transform edit also come
                    // through this replay path; Commit requires the same presentation.
                    await viewport.SetIsolatedTransformAsync(new(transform.X, transform.Y, transform.Scale, transform.Rotation, transform.Opacity));
                    await viewport.CommitSceneProjectAsync(source);
                }
                else { presentedDocument = null; await viewport.LoadSceneAsync(source); }
                previewInput?.Dispose(); previewInput = input; loadedSourceKey = key;
                if (!ReferenceEquals(document, current) || disposed) return;
                presentedDocument = current; previewSessionId = viewport.LastFrame?.SessionId ?? 0;
                await viewport.SeekAsync(Math.Clamp(time, 0, current.Duration), lifetime.Token);
                previewSessionId = viewport.LastFrame?.SessionId ?? previewSessionId;
                viewport.SelectedTargetId = current.ActorId; previewDirty = edited != editVersion;
                if (!previewDirty) replayRequired = transformPending = false;
                await ReadParametersAsync(); RefreshHeader();
                if (previewDirty && active) { previewTimer.Stop(); previewTimer.Start(); }
            }
            finally { if (!ReferenceEquals(previewInput, input)) input.Dispose(); }
        }
        finally { previewGate.Release(); }
    }
    Task previewEditTask = Task.CompletedTask;
    Task ApplyPreviewEditsAsync()
    {
        if (applyingPreviewEdits) return previewEditTask;
        if (disposed || document is null || !previewDirty) return Task.CompletedTask;
        applyingPreviewEdits = true;
        return previewEditTask = ApplyPreviewEditsCoreAsync();
    }
    async Task ApplyPreviewEditsCoreAsync()
    {
        try
        {
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
            bool move = transformPending;
            // Keep the displayed native pose. The committed history is replayed only
            // by a later seek/play, so a slider never rebuilds the model or advances time.
            // Freeze both rows and their matching project before yielding: another
            // pointer event may author the next value while native rendering awaits.
            var input = current.CreateSnapshot(Path.Combine(DesktopSettings.DataDirectory, "live2d-preview"));
            var source = PreviewSource(input, current);
            try
            {
                await viewport.PauseForEditingAsync();
                if (!ReferenceEquals(document, current) || disposed) return;
                if (move || rows.Count > 0) await viewport.ApplySceneEditsAsync(rows, move
                    ? new IsolatedActorTransform(transform.X, transform.Y, transform.Scale, transform.Rotation, transform.Opacity) : null);
                await viewport.CommitSceneProjectAsync(source);
                previewInput?.Dispose(); previewInput = input;
                previewDirty = edited != editVersion; transformPending = current.Transform != transform;
                await ReadParametersAsync(); RefreshHeader();
            }
            finally { if (!ReferenceEquals(previewInput, input)) input.Dispose(); }
        }
        finally { previewGate.Release(); }
        }
        finally
        {
            applyingPreviewEdits = false;
            if (previewDirty && active && !presentingModelDrag && !previewTimer.IsRunning) previewTimer.Start();
        }
    }
    async Task FlushEditsAsync()
    {
        previewTimer.Stop(); await AwaitModelDragAsync(); await scrubber.FlushAsync();
        if (document is not null && (previewDirty || !viewport.HasCurrentSource))
        { if (!viewport.HasCurrentSource) await RefreshPreviewAsync(); else await ApplyPreviewEditsAsync(); }
        await previewGate.WaitAsync(lifetime.Token); previewGate.Release();
        if (previewDirty) await ApplyPreviewEditsAsync();
    }
    void RequestSeek(double time)
    {
        if (document is null) return;
        Pause(); document.Time = Math.Clamp(time, 0, document.Duration);
        requestedSeek = document.Time; seekVersion++; scrubber.Request(document.Time);
    }
    async Task SeekAsync(double time, CancellationToken cancellationToken = default)
    {
        if (document is null) return;
        var current = document;
        Pause(); current.Time = Math.Clamp(time, 0, current.Duration);
        double target = current.Time; long version = ++seekVersion; requestedSeek = target; RefreshHeader();
        try
        {
            if (previewDirty) replayRequired = true;
            if (previewDirty || !viewport.HasCurrentSource) await RefreshPreviewAsync();
            cancellationToken.ThrowIfCancellationRequested();
            await previewGate.WaitAsync(cancellationToken);
            try
            {
                if (!ReferenceEquals(document, current) || version != seekVersion) return;
                // Serialize seek with live transform updates; the native coalescer
                // deliberately rejects an isolation edit and seek in the same batch.
                if (viewport.HasCurrentSource) await viewport.SeekAsync(target, cancellationToken);
                if (!ReferenceEquals(document, current) || version != seekVersion) return;
                previewSessionId = viewport.LastFrame?.SessionId ?? previewSessionId;
                current.Time = Math.Clamp(viewport.LastFrame?.Time ?? target, 0, current.Duration);
                requestedSeek = null; RefreshTransport(); RefreshTimeDependentControls();
                await ReadParametersAsync();
            }
            finally { previewGate.Release(); }
        }
        finally
        {
            if (ReferenceEquals(document, current) && version == seekVersion)
            {
                requestedSeek = null;
                if (viewport.HasCurrentSource && viewport.LastFrame is { } frame) current.Time = Math.Clamp(frame.Time, 0, current.Duration);
                RefreshTransport();
            }
        }
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
        playbackRequested = true; DeferDraft(); ((FontIcon)playButton.Content).Glyph = "\uE769";
        viewport.Play(range.End);
    }
    void OnFrame(PreviewFrame frame)
    {
        if (disposed || document is null || !ReferenceEquals(presentedDocument, document) || frame.SessionId < previewSessionId) return;
        // A real backward seek/history replay creates a newer native session.
        // Reject only older frames, and keep a pending user cursor until seek finishes.
        previewSessionId = frame.SessionId;
        if (requestedSeek is not null) return;
        document.Time = Math.Clamp(frame.Time, 0, document.Duration);
        RefreshTransport(); RefreshPlayingControls(frame.IsPlaying);
        string glyph = frame.IsPlaying || playbackRequested ? "\uE769" : "\uE768";
        if (((FontIcon)playButton.Content).Glyph != glyph) ((FontIcon)playButton.Content).Glyph = glyph;
        if (activeTab == "parameters" && !readingParameters && DateTime.UtcNow - lastParameterRead > TimeSpan.FromMilliseconds(200)) _ = SafeAsync(ReadParametersAsync);
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
                playbackRequested = true; DeferDraft(); viewport.Play(PlaybackRange.End);
            }
            finally { restartingLoop = false; }
        });
    }
    Task parameterReadTask = Task.CompletedTask;
    async Task ReadParametersAsync()
    {
        if (readingParameters) await parameterReadTask;
        if (readingParameters) { await parameterReadTask; return; }
        if (document is null || !ReferenceEquals(presentedDocument, document) || !viewport.HasCurrentSource || disposed || previewDirty || requestedSeek is not null) return;
        parameterReadTask = ReadParametersCoreAsync(document); await parameterReadTask;
    }
    async Task ReadParametersCoreAsync(Live2DDocument current)
    {
        long edited = editVersion, seek = seekVersion; readingParameters = true; lastParameterRead = DateTime.UtcNow;
        try
        {
            var values = await viewport.ReadActorParametersAsync(current.ActorId, lifetime.Token);
            if (ReferenceEquals(document, current) && edited == editVersion && seek == seekVersion && !previewDirty && requestedSeek is null)
            { current.UpdateParameterValues(values); RefreshParameterValues(); }
        }
        finally { readingParameters = false; }
    }
    void MoveModel(PreviewDrag drag)
        => QueueModelDrag(drag);
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
        if (current is not null) await StudioExportDialog.ShowLive2DAsync(window, current, kind);
    }
    internal async Task WaitForReadyAsync()
    {
        Task pending;
        do { pending = readyTask; await pending; } while (!ReferenceEquals(pending, readyTask));
        await FlushEditsAsync(); await ReadParametersAsync();
    }
    internal void Deactivate()
    {
        active = false; scrubber.Cancel(); Pause(); previewTimer.Stop(); resolutionTimer?.Stop(); draftTimer.Stop(); SaveDraft();
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
