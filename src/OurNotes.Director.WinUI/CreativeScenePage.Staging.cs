using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.WinUI;

internal sealed partial class CreativeScenePage
{
    static readonly string[] ShotNames = ["全景", "远景", "中远景", "中景", "近景", "特写", "大特写"];
    IReadOnlyList<NativeStageOption> stageOptions = [];
    string? stageOptionsKey;
    async Task EnsureStageOptionsAsync()
    {
        string source = DirectorProject.SourceId(document.Source);
        string key = window.Settings.ArchivePath + "\n" + document.Site + "\n" + source;
        if (key == stageOptionsKey) return;
        var owner = document; string archivePath = window.Settings.ArchivePath, site = document.Site;
        var result = await Task.Run(() => NativeStageCatalog.Read(new Archive(archivePath), site, source));
        if (ReferenceEquals(owner, document) && archivePath == window.Settings.ArchivePath)
        { stageOptions = result; stageOptionsKey = key; }
    }
    async Task ChooseStageAsync()
    {
        viewport.Pause();
        if (!Directory.Exists(window.Settings.ArchivePath)) { window.ShowResourceManager(); return; }
        await EnsureStageOptionsAsync();
        var owner = document;
        await using var picker = new StagePickerDialog(window.RootXaml, new Archive(window.Settings.ArchivePath),
            document.Site, DirectorProject.SourceId(document.Source), document.Language, stageOptions,
            CreativeFocusEditing.StageAt(document, playhead));
        var chosen = await picker.ShowAsync();
        if (chosen is null || !ReferenceEquals(owner, document)) return;
        await UseStageAsync(chosen);
    }
    async Task UseStageAsync(NativeStageOption stage)
    {
        await EditAsync(() =>
        {
            if (document.Root["imported"]?.GetValue<bool>() == true)
            {
                // Replace the selected/active stage row, not every stage in the story.
                var clip = document.Clips.OfType<JsonObject>().Where(c => S(c["data"]!.AsObject(), "cmd") == "Stage" && N(c, "start") <= playhead)
                    .OrderBy(c => N(c, "start")).LastOrDefault();
                if (clip is null) selectedClip = document.AddClip("", "stage", playhead, 1d / 30,
                    new JsonObject { ["cmd"] = "Stage", ["TargetAssetName"] = stage.Key, ["Duration"] = 0 }, stage.Label);
                else { clip["data"]!["TargetAssetName"] = stage.Key; clip["label"] = stage.Label; selectedClip = S(clip, "id"); }
            }
            else { CreativeStaging.SetStage(document, stage.Key, stage.Label); selectedActor = selectedClip = null; }
        });
    }
    async Task FocusActorAsync(string actor)
    {
        await EditAsync(() => { selectedActor = actor; selectedClip = CreativeFocusEditing.FocusHere(document, actor, playhead); });
        timeline.RevealSelection();
    }
    void AddStageMenuItems(MenuFlyout menu, string actor)
    {
        if (!CreativeStaging.Slots.Contains(CreativeStaging.SlotAt(document, actor, playhead))) return;
        menu.Items.Insert(0, StudioMenus.Async("对焦此人", () => Safe(() => FocusActorAsync(actor)), "\uE722"));
        menu.Items.Insert(1, StudioMenus.Async("添加前后移动", () => Safe(() => EditAsync(() => selectedClip = CreativeStaging.AddDepth(document, actor, playhead, -.5, .5))), "\uE7C2"));
        menu.Items.Insert(2, StudioMenus.Async("同深度置于前面", () => Safe(() => EditAsync(() => selectedClip = CreativeFocusEditing.OrderHere(document, actor, playhead, true))), "\uE8B0"));
        menu.Items.Insert(3, StudioMenus.Async("同深度置于后面", () => Safe(() => EditAsync(() => selectedClip = CreativeFocusEditing.OrderHere(document, actor, playhead, false))), "\uE8B1"));
    }
    void StageNumber(string label, double value, double min, double max, Func<double, Task> edit)
    {
        long version = inspectorVersion; double initial = value;
        var input = new NumberBox { Header = label, Value = value, Minimum = min, Maximum = max, SmallChange = .1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        void Apply()
        {
            if (version != inspectorVersion || !double.IsFinite(input.Value) || Math.Abs(initial - input.Value) < .00001) return;
            initial = input.Value; _ = Safe(() => edit(initial));
        }
        input.LostFocus += (_, _) => Apply(); input.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { Apply(); e.Handled = true; } };
        inspector.Children.Add(input);
    }
    void StageChoice(string label, IEnumerable<(string Key, string Label)> choices, string selected, Func<string, Task> edit)
    {
        long version = inspectorVersion;
        var select = new ComboBox { Header = label, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (key, name) in choices) select.Items.Add(new ComboBoxItem { Tag = key, Content = name });
        select.SelectedItem = select.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == selected);
        select.SelectionChanged += (_, _) =>
        {
            if (version == inspectorVersion && select.SelectedItem is ComboBoxItem { Tag: string key } && key != selected)
                _ = Safe(() => edit(key));
        };
        inspector.Children.Add(select);
    }
    void BuildMaterialFadeInspector(JsonObject clip)
    {
        string id = S(clip, "id"); long version = inspectorVersion;
        double fade = N(clip["data"]!.AsObject(), "MotionFadeIn");
        var authored = new CheckBox { Content = "沿用素材过渡", IsChecked = fade < 0 };
        authored.Click += (_, _) =>
        {
            if (version == inspectorVersion)
                _ = Safe(() => EditAsync(() => document.Clip(id)["data"]!["MotionFadeIn"] = authored.IsChecked == true ? -1 : .2));
        };
        inspector.Children.Add(authored);
        if (fade >= 0) StageNumber("切换时长（秒）", fade, 0, 10,
            value => EditAsync(() => document.Clip(id)["data"]!["MotionFadeIn"] = value));
    }
    bool BuildStagingActorInspector(JsonObject actor)
    {
        if (actor["stageSlot"] is null) return false;
        string id = S(actor, "id");
        StageChoice("初始站位", CreativeStaging.Slots.Select(p => (p.ToString(), CreativeStaging.SlotName(p))), actor["stageSlot"]!.ToString(),
            key => EditAsync(() => CreativeStaging.AssignSlot(document, id, int.Parse(key))));
        inspector.Children.Add(Ui.Row(6, Button("靠近镜头", () => MoveStageDepthAsync(id, -.25f), "\uE74A"),
            Button("远离镜头", () => MoveStageDepthAsync(id, .25f), "\uE74B")));
        ToolTipService.SetToolTip(inspector.Children[^1], "在播放头记录原作空间位移；不改变遮挡顺序。Ctrl+滚轮可连续调整。 ");
        inspector.Children.Add(Button("对焦此人", () => FocusActorAsync(id), "\uE722", true));
        inspector.Children.Add(Button("添加前后移动", () => EditAsync(() => selectedClip = CreativeStaging.AddDepth(document, id, playhead, -.5, .5)), "\uE7C2"));
        StageChoice("同深度遮挡顺序", new[] { ("", "沿用原作"), ("Forward", "置于前面"), ("Back", "置于后面") }, S(actor, "layer"),
            value => EditAsync(() => document.Actor(id)["layer"] = value));
        inspector.Children.Add(new TextBlock { Text = "负值靠近镜头，正值远离。直接拖动可微调构图，不改变原作站位。", TextWrapping = TextWrapping.Wrap, FontSize = 11, Opacity = .7 });
        inspector.Children.Add(Ui.Row(6, Button("姿态图片", () => ExportAsync("frame", true), "\uEB9F"), Button("角色动画", () => ExportAsync("clip", true), "\uE714")));
        return true;
    }
    bool BuildStagingClipInspector(JsonObject clip)
    {
        string id = S(clip, "id"), kind = S(clip, "kind"); var row = clip["data"]!.AsObject();
        if (S(row, "cmd") is "Forward" or "Back")
        {
            StageChoice("同深度遮挡顺序", new[] { ("Forward", "置于前面"), ("Back", "置于后面") }, S(row, "cmd"),
                value => EditAsync(() =>
                {
                    var c = document.Clip(id); c["data"]!["cmd"] = value;
                    c["label"] = value == "Forward" ? "置于前面" : "置于后面";
                }));
            return true;
        }
        if (kind == "placement")
        {
            int start = inspector.Children.Count;
            StageNumber("水平位移", N(row, "Parameter1"), -1000, 1000, v => EditAsync(() => document.Clip(id)["data"]!["Parameter1"] = v));
            StageNumber("垂直位移", N(row, "Parameter2"), -1000, 1000, v => EditAsync(() => document.Clip(id)["data"]!["Parameter2"] = v));
            StageNumber("前后位移", N(row, "Parameter3"), -1000, 1000, v => EditAsync(() => document.Clip(id)["data"]!["Parameter3"] = v));
            FoldStagingFields(start, "精确位移", false);
            inspector.Children.Add(new TextBlock { Text = "在画布上拖动人物继续摆放；Ctrl+滚轮调整前后距离。", FontSize = 12, TextWrapping = TextWrapping.Wrap });
            return true;
        }
        if (kind == "depth")
        {
            StageNumber("前后位移", N(row, "Parameter3"), -20, 20, value => EditAsync(() => document.Clip(id)["data"]!["Parameter3"] = value));
            return true;
        }
        if (kind == "stage") { inspector.Children.Add(Button("更换舞台…", ChooseStageAsync, "\uE91B")); return true; }
        if (kind != "focus") return false;
        var available = document.Actors.OfType<JsonObject>().Where(a => CreativeStaging.Slots.Contains(CreativeStaging.SlotAt(document, S(a, "id"), N(clip, "start"))))
            .Select(a => (Key: S(a, "id"), Label: DisplayNames.Character(CreativeScene.Text(a, "name", S(a, "id"))))).ToArray();
        string mode = CreativeFocusEditing.Mode(clip);
        if (mode == "layer")
        {
            string actor = CreativeFocusEditing.LayerActor(document, clip);
            var scope = CreativeFocusEditing.LayerScope(clip);
            string current = scope.Background ? scope.Character ? "both" : "background" : "character";
            StageChoice("作用范围", new[] { ("character", "人物"), ("background", "背景"), ("both", "背景与人物") }, current,
                choice => EditAsync(() => CreativeFocusEditing.SetLayerScope(document, id,
                    actor.Length > 0 ? actor : available.FirstOrDefault().Key ?? "", choice != "character", choice != "background")));
            if (scope.Character) StageChoice("虚化人物", available, actor,
                target => EditAsync(() => CreativeFocusEditing.SetLayerScope(document, id, target, scope.Background, true)));
            StageNumber("虚化程度（%）", N(row, "Parameter1") * 100, 0, 100,
                value => EditAsync(() => document.Clip(id)["data"]!["Parameter1"] = value / 100));
        }
        else StageChoice("对焦人物", available, S(clip, "actor"), target => EditAsync(() =>
        { var c = document.Clip(id); c["actor"] = target; c["actorSlot"] = true; c["data"]!["TargetName"] = target; }));
        StageChoice("镜头方式", new[] { ("rack", "保持构图，只转移焦点"), ("native", "原作镜头对焦"), ("layer", "单独调整虚化层") }, mode,
            value => ChangeFocusModeAsync(id, value));
        int advancedStart = inspector.Children.Count;
        if (mode == "rack")
        {
            StageNumber("其他人物虚化（%）", N(clip["rackFocus"]!.AsObject(), "others") * 100, 0, 100,
                value => EditAsync(() => document.Clip(id)["rackFocus"]!["others"] = value / 100));
            StageNumber("背景虚化（%）", N(clip["rackFocus"]!.AsObject(), "background") * 100, 0, 100,
                value => EditAsync(() => document.Clip(id)["rackFocus"]!["background"] = value / 100));
        }
        else if (mode == "native")
        {
            string stageKey = CreativeFocusEditing.StageAt(document, N(clip, "start"));
            var options = stageOptions.FirstOrDefault(s => s.Key == stageKey)?.FocusDistances;
            if (options is null)
            {
                long version = inspectorVersion;
                inspector.Children.Add(new TextBlock { Text = stageOptions.Count == 0 ? "正在读取舞台景别…" : "当前舞台未提供可用景别", FontSize = 12 });
                _ = Safe(async () => { await EnsureStageOptionsAsync(); if (version == inspectorVersion && stageOptions.Any(s => s.Key == stageKey)) BuildInspector(); });
            }
            else StageChoice("原作景别", options.Select(i => (i.ToString(), i >= 0 && i < ShotNames.Length ? ShotNames[i] : $"景别 {i}")), N(row, "CameraDistance").ToString(),
                value => EditAsync(() => document.Clip(id)["data"]!["CameraDistance"] = int.Parse(value)));
        }
        StageChoice("过渡", new[] { ("Linear", "匀速"), ("InOutQuad", "柔和进出"), ("OutQuad", "逐渐减速") }, CreativeScene.Text(row, "Parameter2", "InOutQuad"),
            value => EditAsync(() => document.Clip(id)["data"]!["Parameter2"] = value));
        if (mode == "rack") FoldStagingFields(advancedStart, "虚化与过渡", false);
        return true;
    }
    void FoldStagingFields(int start, string title, bool expanded)
    {
        var fields = Ui.Stack(10);
        while (inspector.Children.Count > start)
        { var child = inspector.Children[start]; inspector.Children.RemoveAt(start); fields.Children.Add(child); }
        inspector.Children.Add(new Expander { Header = title, Content = fields, IsExpanded = expanded, HorizontalAlignment = HorizontalAlignment.Stretch });
    }

    async Task ChangeFocusModeAsync(string id, string mode)
    {
        // Awaiting resource discovery must not apply to a different project after Open/New.
        var owner = document;
        int distance = 3;
        if (mode == "native")
        {
            await EnsureStageOptionsAsync();
            if (!ReferenceEquals(owner, document)) return;
            string stage = CreativeFocusEditing.StageAt(document, N(document.Clip(id), "start"));
            var options = stageOptions.FirstOrDefault(s => s.Key == stage)?.FocusDistances;
            if (options is null || options.Count == 0) throw new InvalidOperationException("当前舞台没有可用的原作景别。");
            distance = options.Contains(3) ? 3 : options[0];
        }
        if (ReferenceEquals(owner, document)) await EditAsync(() => CreativeFocusEditing.SetMode(document, id, mode, distance));
    }

    internal async Task RunStagingSmokeAsync(Action<bool,string> check,string fixture)
    {
        // Exercise the normal resource-selection and insertion handlers. No scene JSON is
        // pasted/imported here; the only fixture inputs are real asset choices.
        savedText=document.ToJson();await NewAsync();
        _ = new Archive(fixture).Manifest("intl", "stories", DirectorProject.LibrarySource);
        await EnsureStageOptionsAsync();
        string emptyBefore = document.ToJson();
        await using (var picker = new StagePickerDialog(window.RootXaml, new Archive(fixture), document.Site,
            DirectorProject.LibrarySource, document.Language, stageOptions, "")) await picker.VerifyAndCancelAsync(check);
        check(document.ToJson() == emptyBefore, "浏览和取消舞台选择不创建场景指令，也不改写空白项目");
        await UseStageAsync(stageOptions.First());
        var archive=new Archive(fixture);var manifest=archive.Manifest("intl","stories","10462");
        var definition=JsonNode.Parse(archive.Logical("intl",manifest,"story.json"))!;
        var modelIds=definition["models"]!.AsObject().Select(p=>p.Value!.ToString()).Distinct().Take(2).ToArray();
        await AddActorResourceAsync(modelIds[0],"前景人物");string first=selectedActor!;
        await AddActorResourceAsync(modelIds[1],"后景人物");string second=selectedActor!;
        await EditAsync(()=>{document.Actor(first)["depth"]=-.6;document.Actor(second)["depth"]=.8;});
        selectedActor=first;var metadata=archive.Model("intl",modelIds[0]);
        string motion=metadata["motions"]!.AsArray().Select(m=>m!.ToString()).First(m=>m!=metadata["defaultMotion"]?.ToString());
        string expression=metadata["expressions"]!.AsArray().Select(m=>m!.ToString()).First(m=>m!=metadata["defaultExpression"]?.ToString());
        await InsertAssetAsync(new ResultRow(motion,motion,"",new JsonObject{["duration"]=3}),"motion",0);
        await InsertAssetAsync(new ResultRow(expression,expression,"",new JsonObject{["duration"]=3}),"expression",.2);
        string voice=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(fixture))!,"staging-evidence","original-voice.wav");
        if(!File.Exists(voice))throw new FileNotFoundException("Staging audio fixture was not produced.",voice);
        await InsertVoiceFileAsync(voice,"原语音",0);
        await SeekAsync(0);await FocusActorAsync(first);await SeekAsync(.8);
        check(document.Source["adv"] is null&&document.Root["staging"] is not null,"从空白界面建立原作舞台，不导入原剧情 JSON");
        check(viewport.LastFrame?.Targets.Count(t=>t.Id==first||t.Id==second)==2,"双人原作站位在实际画布中同时可见");
        await App.CaptureCheckpointAsync("authored-stage-front-focus");
        await SeekAsync(1.8);await FocusActorAsync(second);await SeekAsync(2.5);BuildInspector();
        check(document.Clips.OfType<JsonObject>().Count(c=>S(c,"kind")=="focus")==2,"界面处理函数建立两个可编辑焦点片段");
        check(viewport.LastFrame?.ActorAnimations?.Any(a=>a.TargetId==first&&a.Motion==motion&&a.Expression==expression)==true,
            "原作站位上的动作和表情经过素材栏插入后仍实际生效");
        timeline.RevealSelection();
        await Task.Delay(40);
        check(timeline.IsSelectedLaneVisible,"新增焦点片段的轨道进入可见区域，不留在屏幕下方");
        await App.CaptureCheckpointAsync("authored-stage-rear-focus");
        check(inspector.Children.OfType<ComboBox>().Any(b=>b.Header?.ToString()=="镜头方式"),"焦点人物、镜头方式、虚化程度均直接在检查器编辑，无需手写 JSON");
        string focused = selectedClip!;
        async Task ChooseControlAsync(string header, string key)
        {
            var control = inspector.Children.OfType<ComboBox>().Single(box => box.Header?.ToString() == header);
            control.SelectedItem = control.Items.OfType<ComboBoxItem>().Single(item => item.Tag?.ToString() == key);
            // Production SelectionChanged handlers own the edit and request the new frame.
            await loadGate.WaitAsync(); loadGate.Release(); await Task.Delay(30);
        }
        await ChooseControlAsync("镜头方式", "layer");
        await ChooseControlAsync("作用范围", "both");
        check(CreativeFocusEditing.Mode(document.Clip(focused)) == "layer"
            && CreativeFocusEditing.LayerScope(document.Clip(focused)) == (true, true),
            "实际下拉控件能切换为背景与人物分层虚化，不把 DoF 改成镜头运动");
        await ChooseControlAsync("镜头方式", "native");
        check(inspector.Children.OfType<ComboBox>().Single(box => box.Header?.ToString() == "原作景别").Items.Count
            == stageOptions.First().FocusDistances.Count, "原作景别下拉框只列出该舞台实际支持的项目");
        await ChooseControlAsync("镜头方式", "rack");
        check(CreativeFocusEditing.Mode(document.Clip(focused)) == "rack" && !notice.IsOpen,
            "镜头方式经实际控件切换后可返回焦点片段，原生预览没有报错");
        var originalPlane = viewport.LastFrame!.Targets.Single(t => t.Id == first).StagePlacement!;
        var sourceFrame = viewport.LastFrame.Frame;
        await viewport.SendDragSmokeAsync(window, first, .04, -.015);
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while ((stageGesture is not null || selectedClip is null || document.Clip(selectedClip)["placementGesture"]?.GetValue<bool>() != true)
            && DateTime.UtcNow < deadline) await Task.Delay(30);
        await loadGate.WaitAsync(); loadGate.Release();
        check(document.Clips.OfType<JsonObject>().Any(c => c["placementGesture"]?.GetValue<bool>() == true)
            && !document.Compile()["commands"]!.AsArray().Any(c => c?["cmd"]?.ToString() == "Director.Actor"),
            "系统鼠标拖动实际画布，写入原作 MoveToDirection，不写入画面偏移扩展");
        var dragged = viewport.LastFrame!.Targets.Single(t => t.Id == first).StagePlacement!;
        check(dragged.Position != originalPlane.Position && dragged.Position.Z == originalPlane.Position.Z
            && viewport.LastFrame.Frame == sourceFrame, "拖动不改变源深度或声音与动作时间");
        var savedPixels = viewport.LastFrame.Pixels.Rgba.ToArray();
        await SeekAsync(sourceFrame / 30d);
        check(viewport.LastFrame!.Pixels.Rgba.SequenceEqual(savedPixels), "鼠标拖动后的图像与保存指令重放一致");
        await App.CaptureCheckpointAsync("native-space-drag");

    }

}
