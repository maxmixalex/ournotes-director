using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;
using Windows.ApplicationModel.DataTransfer;

namespace OurNotes.Director.WinUI;

internal sealed partial class CreativeScenePage
{
    long inspectorVersion;
    readonly CheckBox recordTransform = new() { Content = "记录位置变化", IsChecked = true };

    void BuildInspector()
    {
        long version = ++inspectorVersion;
        inspector.Children.Clear();
        var current = document;
        bool Current() => !disposed && version == inspectorVersion && ReferenceEquals(current, document);
        void Heading(string text) => inspector.Children.Add(new TextBlock { Text = text, FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new(0, 2, 0, 4) });
        void Help(string text) => inspector.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 11, Opacity = .7 });
        NumberBox Field(string label, double value, double min, double max, Func<double, Task> changed)
        {
            var input = new NumberBox { Header = label, Value = value, Minimum = min, Maximum = max,
                SmallChange = .1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
            double original = value;
            input.LostFocus += (_, _) =>
            {
                if (!Current() || !double.IsFinite(input.Value) || Math.Abs(input.Value - original) < .00001) return;
                double next = input.Value; original = next; _ = Safe(() => changed(next));
            };
            input.KeyDown += (_, args) =>
            {
                if (args.Key != Windows.System.VirtualKey.Enter || !Current() || !double.IsFinite(input.Value) || input.Value == original) return;
                original = input.Value; args.Handled = true; _ = Safe(() => changed(input.Value));
            };
            inspector.Children.Add(input); return input;
        }
        var clip = document.Clips.OfType<JsonObject>().FirstOrDefault(c => S(c, "id") == selectedClip);
        if (clip is not null)
        {
            string id = S(clip, "id"), kind = S(clip, "kind");
            var data = clip["data"]!.AsObject();
            Heading(ClipTimeline.KindName(kind));
            var label = new TextBox { Header = "名称", Text = CreativeScene.Text(clip, "label", ClipTimeline.KindName(kind)) };
            label.LostFocus += (_, _) => { if (Current() && label.Text != S(clip, "label")) _ = Safe(() => EditAsync(() => document.Clip(id)["label"] = label.Text, false)); };
            inspector.Children.Add(label);
            var start = Field("开始（秒）", N(clip, "start"), 0, 36000, value => EditAsync(() => document.MoveClip(id, value)));
            if (document.CanResize(clip))
            {
                var duration = Field("持续（秒）", N(clip, "duration"), 1d / 30, 36000, value => EditAsync(() => document.ResizeClip(id, value)));
                StudioLayout.PairFields(inspector, start, duration);
            }
            else Help($"素材区间 {N(clip, "duration"):0.###} 秒。拖动片段安排开始时间。");
            if (kind == "voice")
            {
                var binding = new ComboBox { Header = "说话角色", HorizontalAlignment = HorizontalAlignment.Stretch };
                binding.Items.Add(new ComboBoxItem { Content = "旁白（不驱动角色）", Tag = "" });
                foreach (var a in document.Actors.OfType<JsonObject>())
                    binding.Items.Add(new ComboBoxItem { Content = CreativeScene.Text(a, "name", S(a, "id")), Tag = S(a, "id") });
                binding.SelectedItem = binding.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == S(clip, "actor")) ?? binding.Items[0];
                binding.SelectionChanged += (_, _) =>
                {
                    if (!Current()) return;
                    string actor = (binding.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
                    _ = Safe(() => EditAsync(() => { var changed = document.Clip(id); changed["actor"] = actor; changed["data"]!["IgnoreLipSync"] = actor.Length == 0; }));
                };
                inspector.Children.Add(binding);
                var lip = new CheckBox { Content = "跟随语音驱动口型", IsChecked = data["IgnoreLipSync"]?.GetValue<bool>() != true && S(clip, "actor").Length > 0 };
                lip.Click += (_, _) => { if (Current()) _ = Safe(() => EditAsync(() => document.Clip(id)["data"]!["IgnoreLipSync"] = lip.IsChecked != true)); };
                inspector.Children.Add(lip);
                if (data["AudioPath"] is not null)
                {
                    Field("文件入点（秒）", N(data, "AudioIn"), 0, N(clip, "sourceDuration", 36000), value => EditAsync(() => document.Clip(id)["data"]!["AudioIn"] = value));
                    Help(Path.GetFileName(data["AudioPath"]!.ToString()));
                }
                Help("音量控制位于画布下方；预览静音不会关闭口型或改变导出的声音。");
            }
            else if (kind is "motion" or "expression")
            {
                string field = kind == "motion" ? "MotionName" : "ExpressionName";
                Help("素材：" + data[field]);
                inspector.Children.Add(Button("选择其他" + ClipTimeline.KindName(kind) + "…", async () =>
                {
                    selectedActor = S(clip, "actor"); assetKind.SelectedIndex = kind == "motion" ? 0 : 1;
                    await LoadAssetsAsync(); assetHint.Text = "先删除或移动当前片段，再双击添加需要的素材。";
                }, "\uE8D2"));
            }
            else if (kind == "transform" && data["Values"] is JsonObject values && values["position"] is JsonArray point && point.Count >= 2)
            {
                double width = N(document.Canvas, "width"), height = N(document.Canvas, "height");
                var x = Field("水平位置（像素）", double.Parse(point[0]!.ToString(), CultureInfo.InvariantCulture) * width, -100000, 100000,
                    value => EditAsync(() => document.Clip(id)["data"]!["Values"]!["position"]![0] = value / width));
                var y = Field("垂直位置（像素）", double.Parse(point[1]!.ToString(), CultureInfo.InvariantCulture) * height, -100000, 100000,
                    value => EditAsync(() => document.Clip(id)["data"]!["Values"]!["position"]![1] = value / height));
                StudioLayout.PairFields(inspector, x, y);
                Field("角色高度（画布百分比）", N(values, "height", .95) * 100, 1, 2000, value => EditAsync(() => document.Clip(id)["data"]!["Values"]!["height"] = value / 100));
            }
            else if (kind is "camera" or "grade" && data["Values"] is JsonObject visual)
            {
                string group = kind == "camera" ? "framing" : "grade";
                if (visual[group] is JsonObject settings)
                {
                    var fields = kind == "camera" ? new[] { ("x", "水平位移", 0d), ("y", "垂直位移", 0d), ("zoom", "缩放", 1d), ("roll", "旋转（度）", 0d) }
                        : new[] { ("exposure", "曝光", 0d), ("contrast", "对比度", 1d), ("saturation", "饱和度", 1d), ("vignette", "暗角", 0d) };
                    foreach (var (key, name, fallback) in fields)
                        Field(name, N(settings, key, fallback), key == "zoom" ? .01 : key is "contrast" or "saturation" or "vignette" ? 0 : -100,
                            key == "vignette" ? 1 : 100, value => EditAsync(() => document.Clip(id)["data"]!["Values"]![group]![key] = value));
                }
            }
            var easing = new ComboBox { Header = "变化方式", HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var (key, name) in new[] { ("linear", "匀速"), ("smoothstep", "柔和进出"), ("step", "直接切换") }) easing.Items.Add(new ComboBoxItem { Tag = key, Content = name });
            if (S(data, "cmd").StartsWith("Director.", StringComparison.Ordinal))
            {
                easing.SelectedItem = easing.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == data["Easing"]?.ToString()) ?? easing.Items[0];
                easing.SelectionChanged += (_, _) => { if (Current()) _ = Safe(() => EditAsync(() => document.Clip(id)["data"]!["Easing"] = (easing.SelectedItem as ComboBoxItem)!.Tag!.ToString())); };
                inspector.Children.Add(easing);
            }
            inspector.Children.Add(new Expander { Header = "高级", HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = Button("编辑这个片段的 JSON…", () => EditClipJsonAsync(id), "\uE943") });
            return;
        }
        var actor = document.Actors.OfType<JsonObject>().FirstOrDefault(a => S(a, "id") == selectedActor);
        if (actor is not null)
        {
            string id = S(actor, "id"); Heading(CreativeScene.Text(actor, "name", id));
            var transform = ActorTransform(id);
            var width = N(document.Canvas, "width"); var height = N(document.Canvas, "height");
            var x = Field("水平位置（像素）", transform.X * width, -100000, 100000, value => AddTransformAsync(id, value / width, transform.Y, transform.Height));
            var y = Field("垂直位置（像素）", transform.Y * height, -100000, 100000, value => AddTransformAsync(id, transform.X, value / height, transform.Height));
            StudioLayout.PairFields(inspector, x, y);
            Field("角色高度（画布百分比）", transform.Height * 100, 1, 2000, value => AddTransformAsync(id, transform.X, transform.Y, value / 100));
            inspector.Children.Add(recordTransform);
            Help("在画布中直接拖动摆放。开启记录时，位置变化保存在当前播放头；关闭时调整角色初始摆放。");
            inspector.Children.Add(Ui.Row(6, Button("当前姿态图片", () => ExportAsync("frame", true), "\uEB9F"), Button("角色动画", () => ExportAsync("clip", true), "\uE714")));
            inspector.Children.Add(new Expander { Header = "素材信息", Content = new TextBlock { Text = S(actor, "model"), IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap } });
            return;
        }
        Heading("场景");
        Help("添加角色，然后把动作、表情或语音拖到时间轴。场景长度跟随内容，不需要预先填写固定时长。");
        var canvasWidth = Field("画布宽度", N(document.Canvas, "width", 1920), 16, 32768, value => EditAsync(() => document.Canvas["width"] = (int)Math.Round(value)));
        var canvasHeight = Field("画布高度", N(document.Canvas, "height", 1080), 16, 32768, value => EditAsync(() => document.Canvas["height"] = (int)Math.Round(value)));
        StudioLayout.PairFields(inspector, canvasWidth, canvasHeight);
        var presets = new ComboBox { PlaceholderText = "常用画布", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (name, width, height) in new[] { ("横屏 1920 × 1080", 1920, 1080), ("竖屏 1080 × 1920", 1080, 1920), ("正方形 1080 × 1080", 1080, 1080) })
            presets.Items.Add(new ComboBoxItem { Content = name, Tag = (width, height) });
        presets.SelectionChanged += (_, _) => { if (Current() && (presets.SelectedItem as ComboBoxItem)?.Tag is ValueTuple<int, int> size) _ = Safe(() => EditAsync(() => { document.Canvas["width"] = size.Item1; document.Canvas["height"] = size.Item2; })); };
        inspector.Children.Add(presets);
        var transparent = new CheckBox { Content = "透明背景", IsChecked = document.Canvas["transparent"]?.GetValue<bool>() != false };
        transparent.Click += (_, _) => { if (Current()) _ = Safe(() => EditAsync(() => document.Canvas["transparent"] = transparent.IsChecked == true)); };
        inspector.Children.Add(transparent);
        inspector.Children.Add(Button("添加镜头变化", () => AddToTrackAsync("", "camera", playhead), "\uE722"));
        inspector.Children.Add(Button("添加画面调整", () => AddToTrackAsync("", "grade", playhead), "\uE790"));
        inspector.Children.Add(Button("导出当前画面", () => ExportAsync("frame"), "\uEB9F"));
    }

    (double X, double Y, double Height) ActorTransform(string id)
    {
        var actor = document.Actors.OfType<JsonObject>().FirstOrDefault(a => S(a, "id") == id);
        double x = actor is null ? .5 : N(actor, "x", .5), y = actor is null ? .5 : N(actor, "y", .5), height = actor is null ? .95 : N(actor, "height", .95);
        if (actor?["fromSource"]?.GetValue<bool>() == true && viewport.LastFrame?.Targets.FirstOrDefault(t => t.Id == id) is { } target)
        { x = target.X + target.Width / 2; y = target.Y + target.Height / 2; height = target.Height; }
        foreach (var clip in document.Clips.OfType<JsonObject>().Where(c => S(c, "actor") == id && S(c, "kind") == "transform" && N(c, "start") <= playhead).OrderBy(c => N(c, "start")))
        {
            if (clip["data"]?["Values"] is not JsonObject values) continue;
            double duration = N(clip["data"]!.AsObject(), "Duration"), blend = duration <= 0 ? 1 : Math.Clamp((playhead - N(clip, "start")) / duration, 0, 1);
            if (values["position"] is JsonArray point && point.Count == 2)
            { x += (double.Parse(point[0]!.ToString(), CultureInfo.InvariantCulture) - x) * blend; y += (double.Parse(point[1]!.ToString(), CultureInfo.InvariantCulture) - y) * blend; }
            height += (N(values, "height", height) - height) * blend;
        }
        return (x, y, height);
    }
    JsonObject TransformRow(string actor, double x, double y, double height) => new()
    {
        ["cmd"] = "Director.Actor", ["TargetName"] = actor, ["Space"] = "screen",
        ["Values"] = new JsonObject { ["position"] = new JsonArray(x, y), ["height"] = height }
    };
    async Task AddTransformAsync(string actor, double x, double y, double height)
    {
        await EditAsync(() =>
        {
            var initial = document.Actors.OfType<JsonObject>().FirstOrDefault(a => S(a, "id") == actor);
            if (initial is not null && initial["fromSource"]?.GetValue<bool>() != true && (playhead < 1d / 30 || recordTransform.IsChecked != true))
            { initial["x"] = x; initial["y"] = y; initial["height"] = height; return; }
            selectedClip = document.AddClip(actor, "transform", playhead, 1d / 30, TransformRow(actor, x, y, height), "位置");
        });
    }
    async Task DragAsync(PreviewDrag drag)
    {
        if (dragActor != drag.TargetId)
        { dragActor = drag.TargetId; var state = ActorTransform(drag.TargetId); dragX = state.X; dragY = state.Y; dragHeight = state.Height; }
        double x = dragX + drag.DeltaX, y = dragY + drag.DeltaY;
        if (drag.IsCompleted)
        { dragActor = null; await AddTransformAsync(drag.TargetId, x, y, dragHeight); }
        else if (viewport.HasCurrentSource) await viewport.ApplySceneEditsAsync([TransformRow(drag.TargetId, x, y, dragHeight)]);
    }
    async Task ScaleAsync(PreviewScale scale)
    { var state = ActorTransform(scale.TargetId); await AddTransformAsync(scale.TargetId, state.X, state.Y, Math.Clamp(state.Height * scale.Factor, .01, 20)); }

    async Task<string?> EditJsonDialogAsync(string heading, string text, Action<string> validate)
    {
        var input = new TextBox { Text = text, AcceptsReturn = true, FontFamily = new FontFamily("Consolas"), FontSize = 12,
            MinWidth = 460, Height = 400, TextWrapping = TextWrapping.NoWrap };
        ScrollViewer.SetVerticalScrollBarVisibility(input, ScrollBarVisibility.Auto); ScrollViewer.SetHorizontalScrollBarVisibility(input, ScrollBarVisibility.Auto);
        var error = new InfoBar { IsOpen = false, Severity = InfoBarSeverity.Error };
        var dialog = new ContentDialog { XamlRoot = window.RootXaml, Title = heading, PrimaryButtonText = "应用", CloseButtonText = "取消",
            Content = Ui.Stack(8, input, error) };
        dialog.PrimaryButtonClick += (_, args) =>
        { try { validate(input.Text); } catch (Exception failure) { error.Message = failure.Message; error.IsOpen = true; args.Cancel = true; } };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? input.Text : null;
    }
    async Task ShowJsonAsync()
    {
        var result = await EditJsonDialogAsync("场景 JSON", document.ToJson(), text => CreativeScene.Parse(text));
        if (result is not null) await EditAsync(() => { document = CreativeScene.Parse(result); selectedActor = selectedClip = null; });
    }
    async Task PasteAsync()
    {
        var clipboard = Clipboard.GetContent();
        if (!clipboard.Contains(StandardDataFormats.Text)) throw new ArgumentException("剪贴板中没有 JSON 文本。");
        string text = await clipboard.GetTextAsync();
        var candidate = text.Trim();
        if (candidate.StartsWith("```", StringComparison.Ordinal)) { int first = candidate.IndexOf('\n'), last = candidate.LastIndexOf("```", StringComparison.Ordinal); if (first >= 0 && last > first) candidate = candidate[(first + 1)..last]; }
        if (JsonNode.Parse(candidate)?["format"]?.ToString() == CreativeScene.Format)
        {
            var result = await EditJsonDialogAsync("粘贴场景", candidate, value => CreativeScene.Parse(value));
            if (result is not null) await EditAsync(() => { document = CreativeScene.Parse(result); selectedActor = selectedClip = null; });
        }
        else
        {
            var result = await EditJsonDialogAsync("导入原生项目", candidate, value => SceneDocument.Parse(value, baseDirectory: directory));
            if (result is not null) await ImportNativeAsync(result, directory);
        }
    }
    async Task EditClipJsonAsync(string id)
    {
        string? result = await EditJsonDialogAsync("片段高级参数", document.Clip(id)["data"]!.ToJsonString(DesktopSettings.JsonOptions), text =>
        {
            var copy = CreativeScene.Parse(document.ToJson()); copy.Clip(id)["data"] = JsonNode.Parse(text)!.AsObject(); copy.Validate();
        });
        if (result is not null) await EditAsync(() => document.Clip(id)["data"] = JsonNode.Parse(result)!.AsObject());
    }
    async Task ShowNativeAsync()
    {
        var text = new TextBox { Text = Compiled().ToJsonString(DesktopSettings.JsonOptions), AcceptsReturn = true, IsReadOnly = true,
            Height = 420, MinWidth = 460, FontFamily = new FontFamily("Consolas"), FontSize = 11, TextWrapping = TextWrapping.NoWrap };
        ScrollViewer.SetVerticalScrollBarVisibility(text, ScrollBarVisibility.Auto);
        await new ContentDialog { XamlRoot = window.RootXaml, Title = "原生指令（由当前片段编译）", Content = text, CloseButtonText = "关闭" }.ShowAsync();
    }
    internal Task RunSmokeTestAsync(Action<bool, string> check)
    {
        check(document.Source["adv"] is null && viewport.LastFrame is null, "空白创作场景不依赖原剧情或渲染文件");
        var sample = CreativeScene.Blank("intl", "zh-Hans");
        string id = sample.AddClip("", "grade", 1, 1, new JsonObject { ["cmd"] = "Director.Scene", ["Values"] = new JsonObject { ["grade"] = new JsonObject { ["exposure"] = 0 } }, ["Duration"] = 1 }, "画面变化");
        sample.MoveClip(id, 2);
        check(sample.Duration == 3 && sample.Compile()["commands"]!.AsArray().OfType<JsonObject>().Any(row => row["AtFrame"]?.ToString() == "60"), "片段移动会改变实际播放时序，不只移动一个色块");
        check(timeline.ActualWidth > 0 && actors.IsLoaded && inspector.IsLoaded, "角色、素材、画布和片段时间轴已在原生窗口布局");
        return Task.CompletedTask;
    }
    internal async Task RunRealSmokeTestAsync(Action<bool, string> check)
    {
        string? fixture = Environment.GetEnvironmentVariable("OURNOTES_REAL_SCENE_FIXTURE");
        if (string.IsNullOrEmpty(fixture)) return;
        window.Settings.ArchivePath = fixture;
        var source = SceneTemplates.Create("story-patch", new SceneSource("10462", "intl", "zh-Hans"));
        await ImportNativeAsync(source.Text, fixture); await SeekAsync(3.1);
        check(document.Clips.OfType<JsonObject>().Any(c => S(c, "kind") == "voice"), "原剧情对白在主时间轴中成为可见的语音片段");
        check(viewport.LastFrame is { Frame: 93 }, "原剧情可在片段编辑器中定位实际第93帧");
    }
}
