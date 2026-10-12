using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.WinUI;

/// <summary>One final-output workflow, shared by both editors and independent of preview resolution.</summary>
internal static class StudioExportDialog
{
    internal static async Task ShowSceneAsync(MainWindow window, StudioSceneDocument scene, double playhead)
    {
        var document = scene.ToSceneDocument(); var output = document.Output;
        var settings = await ChooseAsync(window, "导出场景", output.Width, output.Height, 30, output.Codec,
            output.CineformQuality, scene.Duration, playhead, false, false);
        if (settings is null) return;
        using var snapshot = document.CreateSnapshot(Path.Combine(DesktopSettings.DataDirectory, "export-inputs"));
        var values = Values(settings); values["project"] = snapshot.FilePath;
        string id = "render." + settings.Kind;
        Enqueue(window, id, "导出场景", values, window.Settings.Context(document.BaseDirectory));
    }

    internal static async Task ShowHomeAsync(MainWindow window, HomeStudioDocument document, double playhead)
    {
        var project = document.Project; var output = project.Output;
        var settings = await ChooseAsync(window, "导出主页", output.Width, output.Height, output.Fps, output.Codec,
            output.CineformQuality, project.Duration, playhead, true, !string.IsNullOrEmpty(document.SelectedCharacterKey));
        if (settings is null) return;
        using var snapshot = document.CreateSnapshot(Path.Combine(DesktopSettings.DataDirectory, "export-inputs"));
        var values = Values(settings); values["project"] = snapshot.FilePath;
        values["spotId"] = project.Source.SpotId.ToString(CultureInfo.InvariantCulture);
        values["site"] = project.Source.Site; values["adv"] = project.Source.AdvId?.ToString(CultureInfo.InvariantCulture);
        values["fps"] = settings.Fps.ToString(CultureInfo.InvariantCulture);
        string kind = settings.Scope is "all" or "both" ? "export" : settings.Kind;
        values["character"] = settings.Scope == "selected" ? document.SelectedCharacterKey : settings.Scope == "all" ? "all" : null;
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
        if (settings.Kind == "clip") { values["codec"] = settings.Codec; values["cineform-quality"] = settings.Quality; }
        if (settings.Overwrite && settings.Kind != "sequence" && settings.Scope is not ("all" or "both")) values["overwrite"] = "true";
        return values;
    }

    private static void Enqueue(MainWindow window, string id, string title, Dictionary<string, string?> values, CommandContext context)
    {
        var build = CommandLineBuilder.Build(id, values, context);
        if (!build.IsValid) throw new ArgumentException(string.Join(Environment.NewLine, build.Errors.Select(e => e.Message)));
        window.Jobs.Enqueue(new(id, title, build.Arguments, context.WorkingDirectory ?? Environment.CurrentDirectory, build.OutputPath, build.OutputKind));
        window.SetStatus(title + "已加入队列，可以继续编辑。"); window.Navigate("tasks");
    }

    private sealed record ExportSettings(string Kind, string Scope, int Width, int Height, int Fps, int Supersample,
        string Codec, string Quality, double Start, double Duration, string Path, bool Overwrite);

    private static async Task<ExportSettings?> ChooseAsync(MainWindow window, string title, int width, int height,
        int fps, string codec, string quality, double duration, double playhead, bool home, bool selected)
    {
        var kind = Ui.Choice("导出内容", [("clip", "视频"), ("sequence", "PNG 帧序列"), ("frame", "当前画面 PNG")], "clip");
        var scope = Ui.Choice("导出范围", [("scene", "完整主页"), ("selected", "选中的角色"), ("all", "每个角色分别导出"), ("both", "完整主页与全部角色")], "scene");
        if (!selected) ((ComboBoxItem)scope.Items[1]).IsEnabled = false;
        scope.Visibility = home ? Visibility.Visible : Visibility.Collapsed;
        var format = Ui.Choice("视频格式", [("cineform", "透明 MOV · CineForm（推荐）"), ("prores", "透明 MOV · ProRes 4444"),
            ("ffv1", "无损 MKV · FFV1"), ("h264", "MP4 · H.264")], codec);
        var qualityBox = Ui.Choice("CineForm 质量", [("film1", "均衡 · Film 1"), ("high", "较小体积 · High"), ("film3", "更高画质 · Film 3")], quality);
        var resolution = Ui.Choice("画面尺寸", [("current", $"项目尺寸 · {width} × {height}"), ("1080", "横屏 · 1920 × 1080"),
            ("portrait", "竖屏 · 1088 × 1920"), ("1440", "横屏 · 2560 × 1440"), ("custom", "自定义")]);
        var w = Number("宽度", width, 16, 7680, 16); var h = Number("高度", height, 16, 4320, 8);
        var dimensions = Ui.Columns(w, h); dimensions.Visibility = Visibility.Collapsed;
        var frameRate = Number("帧率", fps, 1, 120, 1); frameRate.Visibility = home ? Visibility.Visible : Visibility.Collapsed;
        var start = Number("开始（秒）", 0, 0, 36000, .1);
        var length = Number("时长（秒）", Math.Max(duration, 1d / 30), 1d / 30, 36000, .1);
        var range = Ui.Columns(start, length);
        var sampling = Ui.Choice("边缘质量", [("1", "快速"), ("2", "精细（推荐）"), ("3", "更精细"), ("4", "最高")], "2");
        var overwrite = new CheckBox { Content = "替换同名文件" };
        var location = Ui.Input("保存到");
        string folder = window.Settings.OutputDirectory;
        string stem = (home ? "home-" : "scene-") + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
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
            format.Visibility = video ? Visibility.Visible : Visibility.Collapsed;
            qualityBox.Visibility = video && Ui.Selected(format) == "cineform" ? Visibility.Visible : Visibility.Collapsed;
            range.Visibility = Ui.Selected(kind) == "frame" ? Visibility.Collapsed : Visibility.Visible;
            string next = Suggested(); if (location.Text == previousSuggested) location.Text = next; previousSuggested = next;
        }
        kind.SelectionChanged += (_, _) => Update(); format.SelectionChanged += (_, _) => Update(); scope.SelectionChanged += (_, _) => Update();
        resolution.SelectionChanged += (_, _) =>
        {
            (int newWidth, int newHeight) = Ui.Selected(resolution) switch { "1080" => (1920, 1080), "portrait" => (1088, 1920), "1440" => (2560, 1440), _ => (width, height) };
            if (Ui.Selected(resolution) != "custom") { w.Value = newWidth; h.Value = newHeight; }
            dimensions.Visibility = Ui.Selected(resolution) == "custom" ? Visibility.Visible : Visibility.Collapsed;
        };
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
        var advanced = new Expander { Header = "画质与时间范围", HorizontalAlignment = HorizontalAlignment.Stretch, Content = Ui.Stack(12, sampling, frameRate, range, overwrite) };
        var body = Ui.Stack(12, kind, scope, format, qualityBox, resolution, dimensions, advanced, pathRow, error);
        var dialog = new ContentDialog { XamlRoot = window.RootXaml, Title = title, PrimaryButtonText = "开始导出", CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary, Content = new ScrollViewer { Content = body, MaxHeight = 570, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        ExportSettings? result = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                string mode = Ui.Selected(kind), selection = home ? Ui.Selected(scope) : "scene";
                if (!double.IsFinite(w.Value) || !double.IsFinite(h.Value) || w.Value != Math.Truncate(w.Value) || h.Value != Math.Truncate(h.Value)) throw new ArgumentException("宽度和高度需要填写整数像素。");
                if (!double.IsFinite(frameRate.Value) || frameRate.Value != Math.Truncate(frameRate.Value)) throw new ArgumentException("帧率需要填写整数。");
                if (selection is "all" or "both" && mode != "clip") throw new ArgumentException("分别导出角色时，请选择视频。");
                string encoding = Ui.Selected(format);
                if (mode == "clip") VideoEncoding.Validate(encoding, checked((int)w.Value), checked((int)h.Value), Ui.Selected(qualityBox));
                if (mode == "clip" && encoding == "h264" && home && selection != "scene") throw new ArgumentException("透明角色请选择透明 MOV 或无损 MKV。");
                if (!double.IsFinite(start.Value) || !double.IsFinite(length.Value) || length.Value <= 0 || start.Value + length.Value > 36000) throw new ArgumentException("请检查导出的时间范围。");
                string target = Path.GetFullPath(location.Text.Trim());
                bool directory = mode == "sequence" || selection is "all" or "both";
                if (Directory.Exists(target) || File.Exists(target) && (directory || overwrite.IsChecked != true)) throw new ArgumentException("这里已有同名文件或目录，请改一个名称。");
                if (mode == "clip" && !directory) VideoEncoding.ValidateOutputPath(encoding, target);
                if (mode == "frame" && !target.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("当前画面需要保存为 .png。");
                result = new(mode, selection, checked((int)w.Value), checked((int)h.Value), checked((int)frameRate.Value), int.Parse(Ui.Selected(sampling), CultureInfo.InvariantCulture),
                    encoding, Ui.Selected(qualityBox), mode == "frame" ? playhead : start.Value, length.Value, target, overwrite.IsChecked == true);
            }
            catch (Exception failure) when (failure is ArgumentException or IOException or OverflowException)
            { args.Cancel = true; error.Title = "请调整导出设置"; error.Message = failure.Message; error.IsOpen = true; }
        };
        Update(); return await dialog.ShowAsync() == ContentDialogResult.Primary ? result : null;
    }

    private static NumberBox Number(string label, double value, double min, double max, double step) => new()
    { Header = label, Value = value, Minimum = min, Maximum = max, SmallChange = step, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
}
