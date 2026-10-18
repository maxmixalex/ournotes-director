using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.WinUI;

/// <summary>One final-output workflow, shared by both editors and independent of preview resolution.</summary>
internal static class StudioExportDialog
{
    internal static async Task ShowLive2DAsync(MainWindow window, Live2DDocument live2d, string initialKind = "clip")
    {
        var document = live2d.ToSceneDocument(); var output = document.Output;
        var full = TimelineRange.Full(live2d.Duration, 30);
        using var analysisInput = document.CreateSnapshot(Path.Combine(DesktopSettings.DataDirectory, "export-canvas"));
        var source = new ScenePreviewSource(window.Settings.ArchivePath, analysisInput.FilePath,
            NativeDependencies.CoreLibrary(Empty(window.Settings.NativeLibraryPath), Empty(window.Settings.CubismSdkPath)),
            NativeDependencies.MediaTool("ffmpeg", Empty(window.Settings.FFmpegPath)), 64, 64, full.End);
        var settings = await ChooseAsync(window, "导出 Live2D 角色", output.Width, output.Height, 30,
            output.Codec == "h264" ? "cineform" : output.Codec, output.CineformQuality, full.Duration, live2d.Time,
            false, true, output.Supersample, initialKind: initialKind, isolated: true, transparencyRequired: true,
            initialRange: live2d.Range, actorCanvas: (progress, cancellation) => ActorCanvasAnalysis.AnalyzeAsync(
                source, full.StartFrame, full.EndFrameExclusive, progress, cancellation), canvasMultiplier: live2d.CanvasMultiplier,
            initialActorCanvas: live2d.Canvas is not null);
        if (settings is null) return;
        if (settings.CanvasFit is { } fit) document = SceneAssetExport.ApplyCanvasFit(document, fit);
        using var snapshot = document.CreateSnapshot(Path.Combine(DesktopSettings.DataDirectory, "export-inputs"));
        var values = Values(settings); values["project"] = snapshot.FilePath;
        Enqueue(window, "render." + settings.Kind, "导出 Live2D 角色", values, window.Settings.Context(document.BaseDirectory));
    }

    internal static async Task ShowSceneAsync(MainWindow window, StudioSceneDocument scene, double playhead, bool singleActor = false, string initialKind = "clip", string? actorId = null, JsonNode? actorMetadata = null, bool preserveTransparency = false, TimelineRange? initialRange = null)
    {
        var document = scene.ToSceneDocument(); var output = document.Output;
        bool transparencyRequired = preserveTransparency || actorId is not null || SceneContent.Read(document.Root["output"]?["content"]) == "characters";
        Func<string, int, Task<(int Width, int Height)>>? canvas = actorMetadata is null ? null
            : (codec, _) => Task.FromResult(SceneAssetExport.Canvas(actorMetadata, codec));
        var settings = await ChooseAsync(window, actorId is not null ? "导出角色素材" : singleActor ? "导出单人透明镜头" : "导出场景", output.Width, output.Height, 30, transparencyRequired && output.Codec == "h264" || actorId is not null ? "cineform" : output.Codec,
            output.CineformQuality, scene.ResolvedDuration ?? scene.Duration, playhead, false, actorId is not null, actorId is not null ? 1 : output.Supersample,
            scene.DurationMode == StudioDurationMode.Automatic, nativeCanvas: canvas, initialKind: initialKind,
            isolated: actorId is not null, transparencyRequired: transparencyRequired, initialRange:initialRange);
        if (settings is null) return;
        if (actorId is not null) document = SceneAssetExport.Isolate(document, actorId, settings.Width, settings.Height, settings.Supersample);
        using var snapshot = document.CreateSnapshot(Path.Combine(DesktopSettings.DataDirectory, "export-inputs"));
        var values = Values(settings); values["project"] = snapshot.FilePath;
        string id = "render." + settings.Kind;
        Enqueue(window, id, "导出场景", values, window.Settings.Context(document.BaseDirectory));
    }

    internal static async Task ShowHomeAsync(MainWindow window, HomeStudioDocument document, double playhead, string initialKind = "clip", bool isolated = false, TimelineRange? initialRange = null)
    {
        var project = document.Project; var output = project.Output;
        async Task<(int Width, int Height)> NativeCanvas(string selectedCodec, int selectedFps)
        {
            var canvas = await Task.Run(() => document.MeasureNativeCanvas(window.Settings.ArchivePath, fps: selectedFps, codec: selectedCodec));
            return (canvas.Width, canvas.Height);
        }
        var settings = await ChooseAsync(window, "导出主页", output.Width, output.Height, output.Fps, output.Codec,
            output.CineformQuality, TimelineRange.Full(project.Duration, output.Fps).Duration, playhead, true, !string.IsNullOrEmpty(document.SelectedCharacterKey), output.Supersample,
            nativeCanvas: NativeCanvas, initialKind: initialKind, isolated: isolated, initialRange:initialRange);
        if (settings is null) return;
        using var snapshot = document.CreateSnapshot(Path.Combine(DesktopSettings.DataDirectory, "export-inputs"));
        var values = Values(settings); values["project"] = snapshot.FilePath;
        values["spotId"] = project.Source.SpotId.ToString(CultureInfo.InvariantCulture);
        values["site"] = project.Source.Site; values["adv"] = project.Source.AdvId?.ToString(CultureInfo.InvariantCulture);
        values["fps"] = settings.Fps.ToString(CultureInfo.InvariantCulture);
        string kind = settings.Scope is "all" or "both" ? "export" : settings.Kind;
        values["character"] = settings.Scope == "selected" ? document.SelectedCharacterKey : settings.Scope == "all" ? "all" : null;
        if (settings.Scope == "selected") { values["framing"] = "fit"; values["no-background"] = "true"; }
        Enqueue(window, "home." + kind, "导出主页", values, window.Settings.Context());
    }

    private static Dictionary<string, string?> Values(ExportSettings settings)
    {
        var values = new Dictionary<string, string?>
        {
            ["out"] = settings.Path, ["width"] = settings.Width.ToString(CultureInfo.InvariantCulture),
            ["height"] = settings.Height.ToString(CultureInfo.InvariantCulture), ["supersample"] = settings.Supersample.ToString(CultureInfo.InvariantCulture),
            ["time"] = settings.Start.ToString("R", CultureInfo.InvariantCulture)
        };
        if (settings.Kind != "frame") values["duration"] = settings.Duration.ToString("R", CultureInfo.InvariantCulture);
        if (settings.UntilEnd && settings.Kind != "frame") { values["until-end"] = "true"; values["duration"] = "36000"; }
        if (settings.Kind == "clip") { values["codec"] = settings.Codec; values["cineform-quality"] = settings.Quality; }
        if (settings.Overwrite && settings.Kind != "sequence" && settings.Scope is not ("all" or "both")) values["overwrite"] = "true";
        return values;
    }

    private static void Enqueue(MainWindow window, string id, string title, Dictionary<string, string?> values, CommandContext context)
    {
        var build = CommandLineBuilder.Build(id, values, context);
        if (!build.IsValid) throw new ArgumentException(string.Join(Environment.NewLine, build.Errors.Select(e => e.Message)));
        window.Jobs.Enqueue(new(id, title, build.Arguments, context.WorkingDirectory ?? Environment.CurrentDirectory, build.OutputPath, build.OutputKind));
        window.SetStatus(title + "已加入队列，可从右上角任务查看进度。");
    }

    private sealed record ExportSettings(string Kind, string Scope, int Width, int Height, int Fps, int Supersample,
        string Codec, string Quality, double Start, double Duration, string Path, bool Overwrite, bool UntilEnd,
        ActorCanvasFit? CanvasFit = null);

    private static async Task<ExportSettings?> ChooseAsync(MainWindow window, string title, int width, int height,
        int fps, string codec, string quality, double duration, double playhead, bool home, bool selected,
        int supersample = 2, bool automaticDuration = false, Func<string, int, Task<(int Width, int Height)>>? nativeCanvas = null, string initialKind = "clip", bool isolated = false, bool transparencyRequired = false, TimelineRange? initialRange = null,
        Func<IProgress<ActorCanvasProgress>?, CancellationToken, Task<ActorCanvasBounds>>? actorCanvas = null, double canvasMultiplier = 1,
        bool initialActorCanvas = false)
    {
        var kind = Ui.Choice("导出内容", [("frame", "图片 · 当前姿态 PNG"), ("clip", "动画 · 视频"), ("sequence", "动画 · PNG 帧序列")], initialKind);
        var scope = Ui.Choice("导出范围", [("scene", "完整主页"), ("selected", "选中的角色"), ("all", "每个角色分别导出"), ("both", "完整主页与全部角色")], isolated && home ? "selected" : "scene");
        if (!selected) ((ComboBoxItem)scope.Items[1]).IsEnabled = false;
        scope.Visibility = home ? Visibility.Visible : Visibility.Collapsed;
        var format = Ui.Choice("视频格式", [("cineform", "透明 MOV · CineForm（推荐）"), ("prores", "透明 MOV · ProRes 4444"),
            ("ffv1", "无损 MKV · FFV1"), ("h264", "MP4 · H.264")], codec);
        var qualityBox = Ui.Choice("CineForm 质量", [("film1", "均衡 · Film 1"), ("high", "较小体积 · High"), ("film3", "更高画质 · Film 3")], quality);
        var resolution = Ui.Choice("画面尺寸", [("current", $"项目尺寸 · {width} × {height}"), ("1080", "横屏 · 1920 × 1080"),
            ("portrait", "竖屏 · 1088 × 1920"), ("1440", "横屏 · 2560 × 1440"), ("custom", "自定义")]);
        if (selected && nativeCanvas is not null) resolution.Items.Insert(1, new ComboBoxItem { Tag = "native", Content = "选中角色原始尺寸 · 自动适配完整动画" });
        if (actorCanvas is not null) resolution.Items.Insert(1, new ComboBoxItem { Tag = "actor", Content = "仅角色 · 完整动作的最小矩形 × 倍数" });
        var multiplier = Number("画布倍数（X）", canvasMultiplier, 1, 10, .05);
        var actorCanvasOptions = Ui.Stack(6, multiplier, Ui.Text("1 倍包含完整动作；宽高各乘所设倍数。图片、帧序列和视频均使用固定范围。", 11, true));
        actorCanvasOptions.Visibility = Visibility.Collapsed;
        var w = Number("宽度", width, 16, RenderDimensions.MaxSide, 16); var h = Number("高度", height, 16, RenderDimensions.MaxSide, 8);
        var dimensions = Ui.Columns(w, h); dimensions.Visibility = Visibility.Collapsed;
        var frameRate = Number("帧率", fps, 1, 120, 1); frameRate.Visibility = home ? Visibility.Visible : Visibility.Collapsed;
        var start = Number("开始（秒）", initialRange?.Start ?? 0, 0, 36000, .1);
        var length = Number("时长（秒）", initialRange?.Duration ?? Math.Max(duration, 1d / fps), 1d / fps, 36000, .1);
        var rangeMode = Ui.Choice("时间范围", [("complete", "完整动画"), ("work", "时间轴区间"), ("range", "自定义区间")], initialRange is null ? "complete" : "work");
        ((ComboBoxItem)rangeMode.Items[1]).IsEnabled = initialRange is not null;
        var rangeSummary = Ui.Text("", 12, true);
        var range = Ui.Columns(start, length);
        var sampling = Ui.Choice("边缘质量", [("1", "原始像素 · 1 倍采样"), ("2", "精细 · 2 倍采样"), ("3", "更精细 · 3 倍采样"), ("4", "最高 · 4 倍采样")], supersample.ToString(CultureInfo.InvariantCulture));
        var overwrite = new CheckBox { Content = "替换同名文件" };
        var location = Ui.Input("保存到");
        string folder = window.Settings.OutputDirectory;
        string stem = (home ? "home-" : actorCanvas is not null ? "live2d-" : "scene-") + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string Suggested()
        {
            bool directory = Ui.Selected(kind) == "sequence" || Ui.Selected(scope) is "all" or "both";
            string extension = directory ? "" : Ui.Selected(kind) == "frame" ? ".png" : VideoEncoding.NormalizeCodec(Ui.Selected(format)) switch { "ffv1" => ".mkv", "h264" => ".mp4", _ => ".mov" };
            return Path.Combine(folder, stem + (directory ? Ui.Selected(kind) == "sequence" ? "-frames" : "-exports" : "") + extension);
        }
        location.Text = Suggested(); string previousSuggested = location.Text;
        void Update()
        {
            bool video = Ui.Selected(kind) == "clip";
            // The batch encoder produces videos. Never silently substitute MOV for
            // the requested current PNG or image sequence.
            ((ComboBoxItem)scope.Items[2]).IsEnabled = video;
            ((ComboBoxItem)scope.Items[3]).IsEnabled = video;
            if (!video && (Ui.Selected(scope) is "all" or "both")) scope.SelectedIndex = isolated ? 1 : 0;
            bool alpha = transparencyRequired || isolated || home && Ui.Selected(scope) != "scene";
            var h264 = format.Items.OfType<ComboBoxItem>().First(item => item.Tag?.ToString() == "h264");
            h264.IsEnabled = !alpha;
            if (alpha && Ui.Selected(format) == "h264") format.SelectedIndex = 0;
            format.Visibility = video ? Visibility.Visible : Visibility.Collapsed;
            qualityBox.Visibility = video && Ui.Selected(format) == "cineform" ? Visibility.Visible : Visibility.Collapsed;
            range.Visibility = Ui.Selected(kind) != "frame" && Ui.Selected(rangeMode) == "range" ? Visibility.Visible : Visibility.Collapsed;
            rangeMode.Visibility = Ui.Selected(kind) != "frame" ? Visibility.Visible : Visibility.Collapsed;
            rangeSummary.Visibility = Ui.Selected(kind) != "frame" ? Visibility.Visible : Visibility.Collapsed;
            if (Ui.Selected(rangeMode) == "work" && initialRange is not null)
            { start.Value = initialRange.Start; length.Value = initialRange.Duration; }
            else if (Ui.Selected(rangeMode) == "complete")
            { start.Value = 0; length.Value = home && double.IsFinite(frameRate.Value) && frameRate.Value >= 1
                ? (Math.Floor((duration - 1d / fps) * frameRate.Value + 1e-8) + 1) / frameRate.Value : duration; }
            rangeSummary.Text = automaticDuration && Ui.Selected(rangeMode) == "complete" ? "包括原剧情的对白、语音与等待。"
                : $"{start.Value:0.###}–{start.Value + length.Value:0.###} 秒 · {Math.Ceiling(length.Value * frameRate.Value - 1e-8):0} 帧";
            string next = Suggested(); if (location.Text == previousSuggested) location.Text = next; previousSuggested = next;
        }
        void RangeSummary() { if (Ui.Selected(rangeMode) != "complete" || !automaticDuration) rangeSummary.Text = $"{start.Value:0.###}–{start.Value + length.Value:0.###} 秒 · {Math.Ceiling(length.Value * frameRate.Value - 1e-8):0} 帧"; }
        start.ValueChanged += (_, _) => RangeSummary(); length.ValueChanged += (_, _) => RangeSummary(); frameRate.ValueChanged += (_, _) => { if (Ui.Selected(rangeMode) == "complete") Update(); else RangeSummary(); };
        kind.SelectionChanged += (_, _) => Update(); format.SelectionChanged += (_, _) => Update(); rangeMode.SelectionChanged += (_, _) => Update();
        scope.SelectionChanged += (_, _) =>
        {
            if (Ui.Selected(scope) == "selected" && nativeCanvas is not null) resolution.SelectedItem = resolution.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == "native");
            else if (Ui.Selected(resolution) == "native") resolution.SelectedIndex = 0;
            Update();
        };
        var resolutionStatus = Ui.Text("", 11, true);
        var resolutionProgress = new ProgressBar { Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed };
        using var canvasCancellation = new CancellationTokenSource();
        Task<ActorCanvasBounds>? boundsTask = null;
        ActorCanvasFit? actorFit = null;
        ContentDialog? dialog = null;
        bool dialogClosed = false;
        int sizingVersion = 0;
        bool measuring = false;
        string? sizingError = null;
        void EnableExport() { if (dialog is not null) dialog.IsPrimaryButtonEnabled = !measuring && sizingError is null; }
        async Task UpdateResolutionAsync()
        {
            int version = ++sizingVersion;
            actorFit = null;
            bool actorOnly = Ui.Selected(resolution) == "actor";
            actorCanvasOptions.Visibility = actorOnly ? Visibility.Visible : Visibility.Collapsed;
            resolutionProgress.Visibility = Visibility.Collapsed;
            dimensions.Visibility = Ui.Selected(resolution) == "custom" ? Visibility.Visible : Visibility.Collapsed;
            if (actorOnly && actorCanvas is not null)
            {
                measuring = true; sizingError = null; EnableExport();
                resolutionStatus.Text = "正在计算完整动作的角色范围…";
                resolutionProgress.Visibility = Visibility.Visible;
                try
                {
                    if (!double.IsFinite(multiplier.Value) || multiplier.Value < 1) throw new ArgumentException("画布倍数需要大于或等于 1。");
                    if (boundsTask is null || boundsTask.IsFaulted || boundsTask.IsCanceled)
                    {
                        var progress = new Progress<ActorCanvasProgress>(value =>
                        {
                            if (dialogClosed || canvasCancellation.IsCancellationRequested || Ui.Selected(resolution) != "actor" || !measuring) return;
                            resolutionProgress.Value = value.Fraction * 100;
                            resolutionStatus.Text = $"完整动作 · {value.CompletedFrames} / {value.TotalFrames} 帧";
                        });
                        boundsTask = actorCanvas(progress, canvasCancellation.Token);
                    }
                    var bounds = await boundsTask;
                    if (version != sizingVersion || dialogClosed) return;
                    actorFit = bounds.Fit(multiplier.Value, Ui.Selected(kind) == "clip" ? Ui.Selected(format) : "ffv1");
                    w.Value = actorFit.Width; h.Value = actorFit.Height;
                    resolutionStatus.Text = $"仅角色 · {actorFit.Width} × {actorFit.Height} · {multiplier.Value:0.##} 倍 · 完整动画固定画布";
                }
                catch (OperationCanceledException) when (canvasCancellation.IsCancellationRequested) { }
                catch (Exception failure) { if (version == sizingVersion && !dialogClosed) { sizingError = failure.Message; resolutionStatus.Text = failure.Message; } }
                finally
                {
                    if (version == sizingVersion && !dialogClosed)
                    { measuring = false; resolutionProgress.Visibility = Visibility.Collapsed; EnableExport(); }
                }
                return;
            }
            if (Ui.Selected(resolution) == "native" && nativeCanvas is not null)
            {
                measuring = true; sizingError = null; EnableExport();
                resolutionStatus.Text = "正在测量完整动画的角色范围…";
                try
                {
                    if (!double.IsFinite(frameRate.Value) || frameRate.Value != Math.Truncate(frameRate.Value)) throw new ArgumentException("帧率需要填写整数。");
                    var measured = await nativeCanvas(Ui.Selected(kind) == "clip" ? Ui.Selected(format) : "ffv1", checked((int)frameRate.Value)); if (version != sizingVersion) return;
                    w.Value = measured.Width; h.Value = measured.Height; sampling.SelectedIndex = 0;
                    resolutionStatus.Text = $"原始角色画布 · {measured.Width} × {measured.Height} · 完整动画固定构图";
                }
                catch (Exception failure) { if (version == sizingVersion) { sizingError = failure.Message; resolutionStatus.Text = failure.Message; } }
                finally { if (version == sizingVersion) { measuring = false; EnableExport(); } }
                return;
            }
            measuring = false; sizingError = null; EnableExport();
            (int newWidth, int newHeight) = Ui.Selected(resolution) switch { "1080" => (1920, 1080), "portrait" => (1088, 1920), "1440" => (2560, 1440), _ => (width, height) };
            if (Ui.Selected(resolution) != "custom") { w.Value = newWidth; h.Value = newHeight; }
            dimensions.Visibility = Ui.Selected(resolution) == "custom" ? Visibility.Visible : Visibility.Collapsed;
            resolutionStatus.Text = "";
        }
        resolution.SelectionChanged += async (_, _) => await UpdateResolutionAsync();
        format.SelectionChanged += async (_, _) => { if (Ui.Selected(resolution) is "native" or "actor") await UpdateResolutionAsync(); };
        kind.SelectionChanged += async (_, _) => { if (Ui.Selected(resolution) is "native" or "actor") await UpdateResolutionAsync(); };
        frameRate.ValueChanged += async (_, _) => { if (Ui.Selected(resolution) == "native") await UpdateResolutionAsync(); };
        multiplier.ValueChanged += async (_, _) => { if (Ui.Selected(resolution) == "actor") await UpdateResolutionAsync(); };
        var browse = StudioLayout.AsyncTool("选择…", async () =>
        {
            bool directory = Ui.Selected(kind) == "sequence" || Ui.Selected(scope) is "all" or "both";
            if (directory)
            {
                if (await window.PickFolderAsync() is { } parent) location.Text = Path.Combine(parent, Path.GetFileName(location.Text));
            }
            else if (await window.PickFileAsync(true, Path.GetFileName(location.Text), [Path.GetExtension(Suggested())]) is { } path) location.Text = path;
        }, "\uE8B7");
        var pathRow = new Grid { ColumnSpacing = 8 }; pathRow.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); pathRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        pathRow.Children.Add(location); pathRow.Children.Add(browse); Grid.SetColumn(browse, 1); browse.VerticalAlignment = VerticalAlignment.Bottom;
        var error = new InfoBar { IsClosable = false, Severity = InfoBarSeverity.Error };
        var advanced = new Expander { Header = "高级画质与帧率", HorizontalAlignment = HorizontalAlignment.Stretch, Content = Ui.Stack(12, sampling, frameRate, overwrite) };
        var body = Ui.Stack(12, kind, scope, format, qualityBox, resolution, actorCanvasOptions, dimensions, resolutionProgress, resolutionStatus, rangeMode, range, rangeSummary, advanced, pathRow, error);
        dialog = new ContentDialog { XamlRoot = window.RootXaml, Title = title, PrimaryButtonText = "开始导出", CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary, Content = new ScrollViewer { Content = body, MaxHeight = 570, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        ExportSettings? result = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                if (measuring) throw new ArgumentException("角色完整动画范围仍在计算，请稍候。");
                if (sizingError is not null) throw new ArgumentException("无法取得角色原始尺寸：" + sizingError);
                if (Ui.Selected(resolution) == "actor" && actorFit is null) throw new ArgumentException("请等待完整动作画布计算完成。");
                string mode = Ui.Selected(kind), selection = home ? Ui.Selected(scope) : "scene";
                if (!double.IsFinite(w.Value) || !double.IsFinite(h.Value) || w.Value != Math.Truncate(w.Value) || h.Value != Math.Truncate(h.Value)) throw new ArgumentException("宽度和高度需要填写整数像素。");
                if (!double.IsFinite(frameRate.Value) || frameRate.Value != Math.Truncate(frameRate.Value)) throw new ArgumentException("帧率需要填写整数。");
                if (selection is "all" or "both" && mode != "clip") throw new ArgumentException("分别导出角色时，请选择视频。");
                string encoding = Ui.Selected(format);
                RenderDimensions.Validate(checked((int)w.Value), checked((int)h.Value), int.Parse(Ui.Selected(sampling), CultureInfo.InvariantCulture));
                if (mode == "clip") VideoEncoding.Validate(encoding, checked((int)w.Value), checked((int)h.Value), Ui.Selected(qualityBox));
                if (mode == "clip" && encoding == "h264" && (transparencyRequired || isolated || home && selection != "scene")) throw new ArgumentException("透明角色请选择透明 MOV 或无损 MKV。");
                bool explicitRange = mode != "frame" && !(automaticDuration && Ui.Selected(rangeMode) == "complete");
                if (explicitRange && (!double.IsFinite(start.Value) || start.Value < 0 || !double.IsFinite(length.Value)
                    || length.Value <= 0 || start.Value + length.Value > 36000)) throw new ArgumentException("请检查导出的时间范围。");
                if (actorCanvas is not null && explicitRange && start.Value + length.Value > duration + 1e-8)
                    throw new ArgumentException("导出区间超出当前动画时长。请先在工作台延长动画，再计算完整动作画布。");
                string target = Path.GetFullPath(location.Text.Trim());
                bool directory = mode == "sequence" || selection is "all" or "both";
                if (Directory.Exists(target) || File.Exists(target) && (directory || overwrite.IsChecked != true)) throw new ArgumentException("这里已有同名文件或目录，请改一个名称。");
                if (mode == "clip" && !directory) VideoEncoding.ValidateOutputPath(encoding, target);
                if (mode == "frame" && !target.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("当前画面需要保存为 .png。");
                result = new(mode, selection, checked((int)w.Value), checked((int)h.Value), checked((int)frameRate.Value), int.Parse(Ui.Selected(sampling), CultureInfo.InvariantCulture),
                    encoding, Ui.Selected(qualityBox), mode == "frame" ? playhead : automaticDuration && Ui.Selected(rangeMode) == "complete" ? 0 : start.Value,
                    explicitRange ? length.Value : Math.Max(duration, 1d / 30), target, overwrite.IsChecked == true, mode != "frame" && automaticDuration && Ui.Selected(rangeMode) == "complete",
                    Ui.Selected(resolution) == "actor" ? actorFit : null);
            }
            catch (Exception failure) when (failure is ArgumentException or IOException or OverflowException)
            { args.Cancel = true; error.Title = "请调整导出设置"; error.Message = failure.Message; error.IsOpen = true; }
        };
        if (isolated && selected && nativeCanvas is not null)
            resolution.SelectedItem = resolution.Items.OfType<ComboBoxItem>().First(item => item.Tag?.ToString() == "native");
        else if (initialActorCanvas && actorCanvas is not null)
            resolution.SelectedItem = resolution.Items.OfType<ComboBoxItem>().First(item => item.Tag?.ToString() == "actor");
        try { Update(); EnableExport(); return await dialog.ShowAsync() == ContentDialogResult.Primary ? result : null; }
        finally
        {
            dialogClosed = true; sizingVersion++; canvasCancellation.Cancel();
            if (boundsTask is not null) try { await boundsTask; } catch (Exception) { /* The dialog already reported a sizing failure or the user cancelled it. */ }
        }
    }

    private static string? Empty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static NumberBox Number(string label, double value, double min, double max, double step) => new()
    { Header = label, Value = value, Minimum = min, Maximum = max, SmallChange = step, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
}
