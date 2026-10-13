using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.WinUI;

internal sealed partial class SceneStudioPage
{
    private void BuildNativeObjectProperties()
    {
        if (_document is null) return;
        _autoKey.Visibility = Visibility.Collapsed;
        var actor = _document.Actors.FirstOrDefault(a => a.Id == _document.SelectedId);
        string title = actor is not null ? DisplayNames.Model(actor.Model) : _document.SelectedId == StudioSceneDocument.CameraId ? "相机" : "场景";
        _properties.Children.Add(StudioLayout.Section(title));
        _properties.Children.Add(Ui.Text("在下方选择一条指令，可直接修改内容；也可以为当前对象插入指令。", 12, true));
        if (actor is not null)
        {
            _properties.Children.Add(StudioLayout.Tool("插入位置与大小", () => InsertNativeCommand("Director.Actor"), "\uE710"));
            _properties.Children.Add(StudioLayout.Tool("插入动作", () => InsertNativeCommand("Motion"), "\uE768"));
            _properties.Children.Add(StudioLayout.Tool("插入表情", () => InsertNativeCommand("Expression"), "\uE76E"));
            _properties.Children.Add(StudioLayout.Tool("插入对白", () => InsertNativeCommand("Talk"), "\uE8BD"));
            _properties.Children.Add(StudioLayout.AsyncTool("单人透明导出…", () => CreateSingleActorShotAsync(true), "\uE898"));
            var related = _document.Commands.Where(command => !command.IsGenerated && command.Target.Split(',').Contains(actor.Id)).Take(12).ToArray();
            if (related.Length > 0) _properties.Children.Add(StudioLayout.Section("这个角色的指令"));
            foreach (var command in related)
                _properties.Children.Add(StudioLayout.Tool(StudioCommandCatalog.Get(command.Command).Label + " · " + (command.Index + 1), () => SelectCommand(command.Id)));
        }
        else if (_document.SelectedId == StudioSceneDocument.CameraId)
            _properties.Children.Add(StudioLayout.Tool("插入镜头控制", () => InsertNativeCommand("Director.Camera"), "\uE710"));
        else
        {
            _properties.Children.Add(StudioLayout.Tool("插入背景与后处理", () => InsertNativeCommand("Director.Scene"), "\uE710"));
            _properties.Children.Add(StudioLayout.AsyncTool("调整画布尺寸", CanvasSettingsAsync, "\uE740"));
        }
        _hint.Text = "对白、等待与语音保留原生执行顺序。选择指令后可修改、复制、排序或插入。";
    }

    private async Task CreateSingleActorShotAsync(bool export)
    {
        bool commandChanged = HasCommandDraft;
        if (!TryFlushCommandDraft(reload: false)) return;
        FlushProperty(); await _pendingPreviewCommit;
        if (commandChanged && !_suppressReload) await ReloadAsync();
        SceneSource? source = _document?.Document.Source ?? await ChooseSourceAsync();
        if (source is null) return;
        StudioActor? actor = _document?.Sample(_document.Time).Actors.FirstOrDefault(a => a.Id == _document.SelectedId)
            ?? _document?.Actors.FirstOrDefault(a => a.Id == _document.SelectedId);
        var native = _viewport.HasCurrentSource && ReferenceEquals(_previewDocument, _document)
            ? _viewport.LastFrame?.ActorAnimations?.FirstOrDefault(state => state.TargetId == _document?.SelectedId) : null;
        string? model = native?.Model ?? actor?.Model;
        if (string.IsNullOrWhiteSpace(model))
        {
            var picked = await ResourcePicker.PickAsync(_window, CommandCatalog.Get("model.show").Fields.First(f => f.Key == "modelId"),
                new Dictionary<string, string?> { ["site"] = source.Site });
            if (picked is null) return; model = picked.Value;
        }
        string selectedModel = model;
        var info = await Task.Run(() => new Archive(_window.Settings.ArchivePath).Model(source.Site, selectedModel).AsObject());
        var length = new NumberBox { Header = "镜头时长（秒）", Value = Math.Clamp(_document?.DurationMode == StudioDurationMode.Fixed ? _document.Duration : 5, .1, 600),
            Minimum = 1d / 30, Maximum = 36000, SmallChange = 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        string motion = native?.Motion ?? (_document?.CanEditTimeline == true ? actor?.Motion : null) ?? info["defaultMotion"]?.ToString() ?? "";
        string expression = native?.Expression ?? (_document?.CanEditTimeline == true ? actor?.Expression : null) ?? info["defaultExpression"]?.ToString() ?? "";
        var motions = info["motions"]?.AsArray().Select(value => (value!.ToString(), DisplayNames.Animation(value.ToString()))).Prepend(("", "模型默认动作")).ToList() ?? [("", "模型默认动作")];
        var expressions = info["expressions"]?.AsArray().Select(value => (value!.ToString(), DisplayNames.Animation(value.ToString()))).Prepend(("", "模型默认表情")).ToList() ?? [("", "模型默认表情")];
        if (motion.Length > 0 && motions.All(item => item.Item1 != motion)) motions.Add((motion, "当前动作 · " + DisplayNames.Animation(motion)));
        if (expression.Length > 0 && expressions.All(item => item.Item1 != expression)) expressions.Add((expression, "当前表情 · " + DisplayNames.Animation(expression)));
        var motionBox = Ui.Choice("动作", motions, motion); var expressionBox = Ui.Choice("表情", expressions, expression);
        int width = (int)Math.Ceiling(Number(info["canvas"]?["width"], 1920) / 16) * 16;
        int height = (int)Math.Ceiling(Number(info["canvas"]?["height"], 1080) / 8) * 8;
        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false };
        var dialog = new ContentDialog { XamlRoot = _window.RootXaml, Title = export ? "单人透明导出" : "单人透明镜头",
            PrimaryButtonText = export ? "选择导出设置" : "创建镜头", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary,
            Content = Ui.Stack(12, Ui.Text(DisplayNames.Model(selectedModel), 16),
                Ui.Text($"原始画布 {width} × {height} · 完整角色 · 透明背景", 12, true),
                Ui.Text(native is not null ? "已选用原生播放器当前的动作和表情；新镜头从所选动作起点播放。"
                    : _document?.CanEditTimeline == false ? "当前原剧情动作尚未取得，请从列表选择；新镜头从所选动作起点播放。" : "新镜头从所选动作起点播放。", 11, true),
                motionBox, expressionBox, length, error) };
        StudioSceneDocument? shot = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                shot = StudioSceneDocument.CreateSingleActor(source, selectedModel, info, Math.Round(length.Value * 30) / 30,
                    Empty(Ui.Selected(motionBox)), Empty(Ui.Selected(expressionBox)));
                RenderDimensions.Validate(shot.Document.Output.Width, shot.Document.Output.Height, shot.Document.Output.Supersample);
            }
            catch (Exception failure) when (failure is ArgumentException or InvalidOperationException or OverflowException)
            { args.Cancel = true; error.Title = "请检查镜头设置"; error.Message = failure.Message; error.IsOpen = true; }
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || shot is null) return;
        if (export) await StudioExportDialog.ShowSceneAsync(_window, shot, 0, singleActor: true);
        else
        {
            SaveDraft(); SetDocument(shot); SelectObject(shot.Actors.Single().Id); await ReloadAsync();
            Notice($"已按模型原始画布创建单人透明镜头：{width} × {height}。动作和表情可在指令区继续编排。", false);
        }
    }

    private async Task CanvasSettingsAsync()
    {
        if (!TryFlushCommandDraft()) return;
        if (_document is null) return;
        var output = _document.Document.Output;
        var preset = Ui.Choice("画布比例", [("current", "当前画布"), ("landscape", "横屏 16:9"), ("portrait", "竖屏 9:16"), ("square", "正方形"), ("native", "所选角色原始尺寸"), ("custom", "自定义")], "current");
        var width = new NumberBox { Header = "宽度（像素）", Value = output.Width, Minimum = 16, Maximum = RenderDimensions.MaxSide, SmallChange = 16, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var height = new NumberBox { Header = "高度（像素）", Value = output.Height, Minimum = 16, Maximum = RenderDimensions.MaxSide, SmallChange = 8, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var sampling = Ui.Choice("渲染精度", [("1", "原像素 · 1 倍"), ("2", "抗锯齿 · 2 倍"), ("3", "抗锯齿 · 3 倍"), ("4", "抗锯齿 · 4 倍")], output.Supersample.ToString(CultureInfo.InvariantCulture));
        var letterbox = new CheckBox { Content = "保留原剧情上下黑边", IsChecked = output.Letterbox != "none" };
        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false };
        var dialog = new ContentDialog { XamlRoot = _window.RootXaml, Title = "调整场景画布", PrimaryButtonText = "应用画布", CloseButtonText = "取消",
            Content = Ui.Stack(12, preset, Ui.Columns(width, height), sampling, letterbox, Ui.Text("实时预览会适配窗口；最终输出使用这里的实际像素。", 12, true), error) };
        int sizingVersion = 0; bool measuring = false, invalidNativeSize = false;
        preset.SelectionChanged += async (_, _) =>
        {
            int version = ++sizingVersion; measuring = false; invalidNativeSize = false; error.IsOpen = false;
            switch (Ui.Selected(preset))
            {
                case "landscape": width.Value = 1920; height.Value = 1080; break;
                case "portrait": width.Value = 1088; height.Value = 1920; break;
                case "square": width.Value = height.Value = 1920; break;
                case "current": width.Value = output.Width; height.Value = output.Height; break;
                case "native":
                    if (_document.Actors.FirstOrDefault(a => a.Id == _document.SelectedId) is not { } actor)
                    { invalidNativeSize = true; error.Message = "请先在场景对象中选择一个角色。"; error.IsOpen = true; break; }
                    measuring = true;
                    try
                    {
                        var info = await Task.Run(() => new Archive(_window.Settings.ArchivePath).Model(_document.Document.Source.Site, actor.Model));
                        if (version != sizingVersion) return;
                        width.Value = Math.Ceiling(Number(info["canvas"]?["width"], output.Width) / 16) * 16;
                        height.Value = Math.Ceiling(Number(info["canvas"]?["height"], output.Height) / 8) * 8;
                        sampling.SelectedIndex = 0; letterbox.IsChecked = false;
                    }
                    catch (Exception failure) { if (version == sizingVersion) { invalidNativeSize = true; error.Message = failure.Message; error.IsOpen = true; } }
                    finally { if (version == sizingVersion) measuring = false; }
                    break;
            }
        };
        int newWidth = 0, newHeight = 0, supersample = 1;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                if (measuring) throw new ArgumentException("正在读取原始画布，请稍候。");
                if (invalidNativeSize) throw new ArgumentException("未能取得所选角色的原始尺寸，请选择其他画布或重新载入该角色。");
                if (!double.IsFinite(width.Value) || !double.IsFinite(height.Value) || width.Value != Math.Truncate(width.Value) || height.Value != Math.Truncate(height.Value)) throw new ArgumentException("画布宽高需要是整数像素。");
                newWidth = checked((int)width.Value); newHeight = checked((int)height.Value); supersample = int.Parse(Ui.Selected(sampling), CultureInfo.InvariantCulture);
                RenderDimensions.Validate(newWidth, newHeight, supersample);
                VideoEncoding.Validate(output.Codec, newWidth, newHeight, output.CineformQuality);
            }
            catch (Exception failure) when (failure is ArgumentException or OverflowException)
            { args.Cancel = true; error.Message = failure.Message; error.IsOpen = true; }
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            Edit(() => _document.SetOutput(newWidth, newHeight, supersample, letterbox: letterbox.IsChecked == true ? "game" : "none"));
    }
}
