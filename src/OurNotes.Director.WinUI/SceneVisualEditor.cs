using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.WinUI;

internal sealed partial class ScenePage
{
    private void RefreshVisualEditor()
    {
        try
        {
            var document = ParseCurrent(); var content = Ui.Stack(16);
            content.Children.Add(Ui.Text("修改会同步回 JSON。未修改的字段和其他指令会完整保留；每个角色条目对应时间线中的具体一条指令。", 12, true));
            content.Children.Add(BuildSourceEditor(document)); content.Children.Add(BuildOutputEditor(document));
            content.Children.Add(BuildActorEditor(document)); content.Children.Add(BuildCameraEditor(document));
            content.Children.Add(BuildCommandEditor(document)); _visualHost.Content = content;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        { _visualHost.Content = Ui.Stack(12, Ui.Text("请先修正 JSON，再使用参数编辑。", 16), Ui.Text(error.Message, 13, true), Ui.Button("返回 JSON", () => _tabs.SelectedIndex = 0)); }
    }

    private FrameworkElement BuildSourceEditor(SceneDocument document)
    {
        var adv = Ui.Input("来源剧情", document.Source.Adv);
        var site = Ui.Choice("来源站点", [("intl", "国际服"), ("jp", "日服")], document.Source.Site);
        var language = Ui.Input("文本语言", document.Source.Language, "zh-Hans / ja");
        var choose = Ui.AsyncButton("从剧情列表选择…", async () =>
        {
            var selected = await ResourcePicker.PickAsync(_window, CommandCatalog.Get("story.show").Fields.First(f => f.Key == "advId"),
                new Dictionary<string, string?> { ["site"] = Ui.Selected(site) });
            if (selected is null) return; adv.Text = selected.Value;
            if (selected.RelatedValues.GetValueOrDefault("site") is { } selectedSite)
                site.SelectedItem = site.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag?.ToString() == selectedSite);
        });
        var body = Ui.Stack(12, adv, choose, Ui.Columns(site, language), Ui.Button("应用来源", () =>
        {
            try { var current = ParseCurrent(); current.SetSource(adv.Text, Ui.Selected(site), language.Text); SetDocumentText(current); ValidateCurrent(); }
            catch (Exception error) { _ = _window.ShowErrorAsync(error.Message); }
        }));
        return new Expander { Header = "素材来源", Content = body, HorizontalAlignment = HorizontalAlignment.Stretch, IsExpanded = true };
    }

    private FrameworkElement BuildOutputEditor(SceneDocument document)
    {
        var output = document.Output; var touched = new HashSet<string>();
        NumberBox Number(string key, string label, double value, double min, double max)
        {
            var box = new NumberBox { Header = label, Value = value, Minimum = min, Maximum = max, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
            box.ValueChanged += (_, _) => touched.Add(key); return box;
        }
        var width = Number("width", "宽度", output.Width, 16, RenderDimensions.MaxSide); var height = Number("height", "高度", output.Height, 16, RenderDimensions.MaxSide);
        var supersample = Number("supersample", "超采样倍数", output.Supersample, 1, 4);
        var codec = Ui.Choice("视频编码", [("cineform", "CineForm · 透明 MOV"), ("prores", "ProRes 4444 · 透明 MOV"),
            ("ffv1", "FFV1 · 无损 MKV"), ("h264", "H.264 · MP4")], output.Codec);
        var quality = Ui.Choice("CineForm 质量", [("film1", "film1 · 平衡画质与体积"), ("high", "high · 体积优先"), ("film2", "film2 · 细节优先")], output.CineformQuality);
        var clean = new CheckBox { Content = "隐藏对白和游戏界面", IsChecked = output.Clean };
        var letterbox = Ui.Choice("画布边框", [("none", "完整画布"), ("game", "保留原游戏边框")], output.Letterbox);
        codec.SelectionChanged += (_, _) => touched.Add("codec"); quality.SelectionChanged += (_, _) => touched.Add("quality");
        clean.Checked += (_, _) => touched.Add("clean"); clean.Unchecked += (_, _) => touched.Add("clean"); letterbox.SelectionChanged += (_, _) => touched.Add("letterbox");
        if (document.Kind == SceneDocumentKind.LegacyShot) codec.IsEnabled = quality.IsEnabled = clean.IsEnabled = letterbox.IsEnabled = false;
        var presets = Ui.Choice("常用尺寸", [("", "自定义"), ("1920,1080", "横屏 1920 × 1080"), ("1440,2160", "竖幅 1440 × 2160"),
            ("1088,1920", "竖屏 1088 × 1920（CineForm 对齐）"), ("1280,720", "横屏 1280 × 720")]);
        presets.SelectionChanged += (_, _) =>
        { var parts = Ui.Selected(presets).Split(','); if (parts.Length == 2) { width.Value = int.Parse(parts[0], CultureInfo.InvariantCulture); height.Value = int.Parse(parts[1], CultureInfo.InvariantCulture); } };
        var body = Ui.Stack(12, presets, Ui.Columns(width, height), supersample, codec, quality, clean, letterbox,
            Ui.Text(document.Kind == SceneDocumentKind.LegacyShot ? "旧 Shot 的编码选项在右侧导出面板设置，原项目格式保持兼容。" : "CineForm 的宽度必须为 16 的倍数，高度必须为 8 的倍数且至少 32。", 12, true),
            Ui.AsyncButton("应用输出设置", () =>
            {
                var current = ParseCurrent();
                current.SetOutput(touched.Contains("width") ? (int)width.Value : null, touched.Contains("height") ? (int)height.Value : null,
                    touched.Contains("supersample") ? (int)supersample.Value : null, touched.Contains("codec") ? Ui.Selected(codec) : null,
                    touched.Contains("quality") ? Ui.Selected(quality) : null, touched.Contains("clean") ? clean.IsChecked : null,
                    touched.Contains("letterbox") ? Ui.Selected(letterbox) : null);
                SetDocumentText(current); touched.Clear(); ValidateCurrent(); return Task.CompletedTask;
            }, "\uE73E"));
        return new Expander { Header = "画布与输出", Content = body, HorizontalAlignment = HorizontalAlignment.Stretch, IsExpanded = true };
    }

    private FrameworkElement BuildActorEditor(SceneDocument document)
    {
        var actors = document.GetActors(); var container = Ui.Stack(12);
        if (actors.Count == 0)
            container.Children.Add(Ui.Text("当前 JSON 没有显式的角色构图指令。继承原剧情时，角色由原指令控制；可通过 JSON 或下方指令编辑器添加 Director.Actor。", 13, true));
        else
        {
            var choose = Ui.Choice("角色指令", actors.Select((a, i) => (i.ToString(CultureInfo.InvariantCulture),
                a.TargetName + " · " + a.Location.Section + "[" + a.Location.Index + "]")));
            var host = new ContentControl();
            void Fill()
            {
                if (!int.TryParse(Ui.Selected(choose), out int index) || index >= actors.Count) return;
                var actor = actors[index]; var values = actor.Values; var edited = new HashSet<string>();
                var layout = Ui.Stack(12, Ui.Text("模型：" + (actor.Model ?? "沿用当前模型"), 12, true));
                var space = Ui.Choice("坐标方式", [("", "沿用当前"), ("screen", "画布比例坐标"), ("world", "原场景世界坐标")], actor.Space);
                if (document.Kind == SceneDocumentKind.LegacyShot) space.IsEnabled = false;
                bool spaceChanged = false; space.SelectionChanged += (_, _) => spaceChanged = true; layout.Children.Add(space);
                var fields = new Dictionary<string, NumberBox>();
                void Number(string key, string label, JsonNode? value, double min = double.MinValue, double max = double.MaxValue)
                {
                    var box = new NumberBox { Header = label, Value = ReadNumber(value), Minimum = min, Maximum = max, PlaceholderText = "本条指令未设置", SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
                    box.ValueChanged += (_, _) => edited.Add(key); fields[key] = box; layout.Children.Add(box);
                }
                Number("positionX", "横向位置", values["positionX"] ?? (values["position"] as JsonArray)?.ElementAtOrDefault(0));
                Number("positionY", "纵向位置", values["positionY"] ?? (values["position"] as JsonArray)?.ElementAtOrDefault(1));
                Number("height", "角色高度（画布比例）", values["height"], .0001, 20); Number("opacity", "不透明度", values["opacity"], 0, 1);
                Number("rotationZ", "平面旋转（度）", values["rotationZ"] ?? (values["rotation"] as JsonArray)?.ElementAtOrDefault(2));
                Number("speed", "动画速度", values["speed"], 0, 100);
                var booleans = new Dictionary<string, CheckBox>();
                foreach (var (key, label) in new[] { ("visible", "显示角色"), ("physics", "物理摆动"), ("breath", "呼吸"), ("blink", "自动眨眼"), ("loop", "循环动作") })
                {
                    var box = new CheckBox { Content = label + (values[key] is null ? "（本条未设置）" : ""), IsChecked = values[key]?.ToString() == "true" };
                    if (document.Kind == SceneDocumentKind.LegacyShot && key == "visible") continue;
                    box.Checked += (_, _) => edited.Add(key); box.Unchecked += (_, _) => edited.Add(key); booleans[key] = box; layout.Children.Add(box);
                }
                layout.Children.Add(Ui.AsyncButton("应用这条角色指令", () =>
                {
                    var patch = new JsonObject();
                    foreach (var pair in fields) if (edited.Contains(pair.Key) && double.IsFinite(pair.Value.Value)) patch[pair.Key] = pair.Value.Value;
                    foreach (var pair in booleans) if (edited.Contains(pair.Key)) patch[pair.Key] = pair.Value.IsChecked == true;
                    var current = ParseCurrent(); current.PatchActor(actor.Location, patch, spaceChanged && Ui.Selected(space).Length > 0 ? Ui.Selected(space) : null);
                    SetDocumentText(current); ValidateCurrent(); RefreshVisualEditor(); return Task.CompletedTask;
                }, "\uE73E"));
                layout.Children.Add(Ui.Text("画布比例坐标中，0.5 表示居中。高级的旋转、缩放、色调与插值可在下方编辑原始 Values。", 12, true));
                var raw = new TextBox { Text = values.ToJsonString(DesktopSettings.JsonOptions), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 260, FontSize = 12 };
                layout.Children.Add(new Expander { Header = "完整 Values", Content = Ui.Stack(10, raw, Ui.AsyncButton("合并 Values", () =>
                {
                    var patch = JsonNode.Parse(raw.Text) as JsonObject ?? throw new ArgumentException("Values 必须为 JSON 对象。");
                    var current = ParseCurrent(); current.PatchActor(actor.Location, patch); SetDocumentText(current); ValidateCurrent(); RefreshVisualEditor(); return Task.CompletedTask;
                })), HorizontalAlignment = HorizontalAlignment.Stretch }); host.Content = layout;
            }
            choose.SelectionChanged += (_, _) => Fill(); container.Children.Add(choose); container.Children.Add(host); Fill();
        }
        return new Expander { Header = "角色构图", Content = container, HorizontalAlignment = HorizontalAlignment.Stretch };
    }

    private FrameworkElement BuildCameraEditor(SceneDocument document)
    {
        var cameras = document.GetCameras(); var body = Ui.Stack(12);
        if (cameras.Count == 0) body.Children.Add(Ui.Text("相机沿用原剧情。可在指令编辑器添加 Director.Camera，或由 agent 生成相机动画。", 13, true));
        else
        {
            var choose = Ui.Choice("相机指令", cameras.Select((camera, i) => (i.ToString(CultureInfo.InvariantCulture), camera.Location.Section + "[" + camera.Location.Index + "]")));
            var host = new ContentControl();
            void Fill()
            {
                var camera = cameras[int.Parse(Ui.Selected(choose), CultureInfo.InvariantCulture)]; var fields = new Dictionary<string, NumberBox>(); var edited = new HashSet<string>();
                var content = Ui.Stack(12);
                foreach (var (key, label) in new[] { ("x", "横向偏移"), ("y", "纵向偏移"), ("zoom", "画布缩放"), ("roll", "画布旋转（度）") })
                {
                    var box = new NumberBox { Header = label, Value = ReadNumber(camera.Values["framing"]?[key]), PlaceholderText = "沿用当前", SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                        Minimum = key == "zoom" ? .0001 : double.MinValue };
                    box.ValueChanged += (_, _) => edited.Add(key); fields[key] = box; content.Children.Add(box);
                }
                content.Children.Add(Ui.AsyncButton("应用相机取景", () =>
                {
                    var framing = new JsonObject(); foreach (var field in fields) if (edited.Contains(field.Key) && double.IsFinite(field.Value.Value)) framing[field.Key] = field.Value.Value;
                    var current = ParseCurrent(); current.PatchCamera(camera.Location, new JsonObject { ["framing"] = framing });
                    SetDocumentText(current); ValidateCurrent(); RefreshVisualEditor(); return Task.CompletedTask;
                }, "\uE73E"));
                var raw = new TextBox { Text = camera.Values.ToJsonString(DesktopSettings.JsonOptions), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 260, FontSize = 12 };
                content.Children.Add(new Expander { Header = "相机完整 Values", HorizontalAlignment = HorizontalAlignment.Stretch, Content = Ui.Stack(10, raw,
                    Ui.AsyncButton("合并相机 Values", () =>
                    {
                        var patch = JsonNode.Parse(raw.Text) as JsonObject ?? throw new ArgumentException("Values 必须为 JSON 对象。");
                        var current = ParseCurrent(); current.PatchCamera(camera.Location, patch); SetDocumentText(current); ValidateCurrent(); RefreshVisualEditor(); return Task.CompletedTask;
                    })) }); host.Content = content;
            }
            choose.SelectionChanged += (_, _) => Fill(); body.Children.Add(choose); body.Children.Add(host); Fill();
        }
        return new Expander { Header = "相机取景", Content = body, HorizontalAlignment = HorizontalAlignment.Stretch };
    }

    private FrameworkElement BuildCommandEditor(SceneDocument document)
    {
        if (document.Kind == SceneDocumentKind.LegacyShot)
            return new Expander { Header = "旧版时间线", HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = Ui.Text("原 tracks 和 events 完整保留，可在 JSON 页编辑。渲染仍由同一个原生引擎执行。", 13, true) };
        var section = Ui.Choice("指令段", [("initialize", "初始化"), ("commands", "正文"), ("finalize", "收尾")], "commands");
        var list = new ListView { MaxHeight = 260, SelectionMode = ListViewSelectionMode.Single };
        var state = Ui.Text("", 12, true);
        var rowEditor = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 140, MaxHeight = 300, FontSize = 12, PlaceholderText = "选择一条指令，或在这里输入要追加的 JSON 对象。" };
        void Fill()
        {
            list.Items.Clear(); var array = ParseCurrent().Root[Ui.Selected(section)] as JsonArray;
            state.Text = array is null ? "此段继承原剧情。编辑时可先从归档载入完整指令。" : $"显式定义了 {array.Count} 条指令，按顺序执行。";
            if (array is not null) for (int i = 0; i < array.Count; i++)
            {
                var row = array[i]; list.Items.Add(new ListViewItem { Tag = i, Content = $"{i + 1}. {row?["cmd"] ?? row?["Command"]}   {row?["TargetName"]}" });
            }
        }
        section.SelectionChanged += (_, _) => Fill();
        list.SelectionChanged += (_, _) =>
        { if ((list.SelectedItem as ListViewItem)?.Tag is int index) rowEditor.Text = ParseCurrent().Root[Ui.Selected(section)]![index]!.ToJsonString(DesktopSettings.JsonOptions); };
        void Store(bool append)
        {
            var current = ParseCurrent(); string key = Ui.Selected(section);
            var rows = current.Root[key] as JsonArray ?? throw new ArgumentException("这一段仍继承原剧情。请先载入原指令，或明确建立空段。");
            var row = JsonNode.Parse(rowEditor.Text) as JsonObject ?? throw new ArgumentException("一条指令必须为 JSON 对象。");
            if (append) rows.Add((JsonNode)row); else if ((list.SelectedItem as ListViewItem)?.Tag is int index) rows[index] = row;
            else throw new ArgumentException("请先选中要修改的指令。");
            current.ReplaceSection(key, rows); SetDocumentText(current); ValidateCurrent(); Fill();
        }
        var body = Ui.Stack(12, section, state, list, rowEditor,
            Ui.Row(8, Ui.AsyncButton("保存这条", () => { Store(false); return Task.CompletedTask; }), Ui.AsyncButton("追加一条", () => { Store(true); return Task.CompletedTask; })),
            Ui.Row(8, Ui.AsyncButton("删除所选", async () =>
            {
                if ((list.SelectedItem as ListViewItem)?.Tag is not int index) return;
                if (!await _window.ConfirmAsync("删除这条指令？", "只删除选中的一条，其余顺序保留。", "删除")) return;
                var current = ParseCurrent(); var rows = current.Root[Ui.Selected(section)]!.AsArray(); rows.RemoveAt(index);
                current.ReplaceSection(Ui.Selected(section), rows); SetDocumentText(current); ValidateCurrent(); Fill();
            }), Ui.AsyncButton("新建空段", async () =>
            {
                if (!await _window.ConfirmAsync("明确清空这段指令？", "空数组表示不执行原剧情的这一段。其他指令段不受影响。", "建立空段")) return;
                var current = ParseCurrent(); current.ReplaceSection(Ui.Selected(section), new JsonArray()); SetDocumentText(current); Fill();
            })),
            Ui.AsyncButton("从归档载入原剧情指令", async () =>
            {
                var current = ParseCurrent();
                string revision = _editor.Text; string? identity = _path; string selected = Ui.Selected(section);
                bool Unchanged() => _editor.Text == revision && _path == identity && Ui.Selected(section) == selected;
                var result = await _window.QueryAsync("story.native-program", new Dictionary<string, string?> { ["advId"] = current.Source.Adv, ["site"] = current.Source.Site });
                if (!result.Success) throw new ArgumentException(result.Error);
                if (!Unchanged()) { await _window.ShowMessageAsync("场景已改变", "查询期间场景或所选指令段已经改变。请再次载入，当前编辑内容已保留。"); return; }
                var program = result.Result;
                string[] candidates = selected switch { "initialize" => ["initialize", "Initialize"], "finalize" => ["finalize", "Finalize"], _ => ["rows", "Rows", "commands"] };
                var rows = candidates.Select(key => program?[key]).OfType<JsonArray>().FirstOrDefault()
                    ?? throw new ArgumentException("引擎没有返回这一段的原始指令，请在剧情指令页查看完整结果。");
                var original = new JsonArray(rows.Select(row => (row is JsonObject obj && (obj["data"] ?? obj["Data"]) is JsonObject data ? data : row)?.DeepClone()).ToArray());
                if (!await _window.ConfirmAsync("载入原始指令？", "将用归档中的这一段替换当前段，之后可以逐条编辑。", "载入")) return;
                if (!Unchanged()) { await _window.ShowMessageAsync("场景已改变", "当前场景已有更新，请再次载入指令。"); return; }
                current.ReplaceSection(selected, original); SetDocumentText(current); ValidateCurrent(); Fill();
            }));
        Fill(); return new Expander { Header = "完整指令时间线", Content = body, HorizontalAlignment = HorizontalAlignment.Stretch };
    }

    private static double ReadNumber(JsonNode? value) => value is not null && double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : double.NaN;
}
