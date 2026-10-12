using System.Text.Json.Nodes;
using OurNotes.Director.Core;
using OurNotes.Director.Desktop.Core;

namespace OurNotes.Director.Desktop.Tests;

public static class HomeStudioDocumentTests
{
    public static void Run(Action<bool, string> check)
    {
        var source = new HomeProjectSource(77, "jp", 42);
        var model = new HomeStudioDocument(new HomeProject(source) { Duration = 4, Output = new(320, 192, 60, 1) });
        check(model.Project.Source == source && model.HasUnsavedChanges, "主页项目保留明确的站点、主页和归档来源");
        model.SetTransform("alice", 0, new(.1, -.1, 1));
        model.SetTransform("alice", 2, new(.5, .3, 2, false));
        var middle = model.TransformAt("alice", 1);
        check(Math.Abs(middle.X - .3) < 1e-10 && Math.Abs(middle.Y - .1) < 1e-10 && middle.Scale == 1.5,
            "主页角色位置和缩放在关键帧间线性插值");
        check(middle.Visible && !model.TransformAt("alice", 2).Visible, "主页角色显示状态在精确关键帧时刻切换");
        model.SetTransform("alice", 1, middle with { X = .4, Scale = 3 }, false);
        check(Math.Abs(model.TransformAt("alice", 1).X - .4) < 1e-10 && model.TransformAt("alice", 1).Scale == 3
            && model.Track("alice").Keys.Count == 2 && !model.TransformAt("alice", 2).Visible,
            "关闭自动关键帧时整体变换轨道，保留原有运动和显示切换");
        model.Undo();
        model.SetAnimation("alice", 0, "home_start", false);
        model.SetAnimation("alice", 1.5, "home_idle", true, 2, .25);
        check(model.Track("alice").AnimationAt(1)!.Name == "home_start" && model.Track("alice").AnimationAt(1.5)!.Name == "home_idle",
            "主页动作片段按起点选择，不因位置关键帧而重启");
        check(model.Track("alice").AnimationAt(2) is { Speed: 2, SourceTime: .25, Loop: true }, "主页动作速度、裁切起点和循环保留在时间轴");
        model.SetGrade(0, new(0, 1, 1, 0)); model.SetGrade(2, new(2, 2, 0, .5));
        var grade = model.Project.EditState.GradeAt(1);
        check(grade == new HomeGrade(1, 1.5, .5, .25), "主页后期参数在保存的调色关键帧间插值");
        model.SetScene(background: false, postProcessing: false);
        string serialized = model.ToJson();
        var restored = HomeStudioDocument.Parse(serialized);
        check(restored.ToJson() == serialized && !restored.Project.EditState.Background && !restored.Project.EditState.PostProcessing,
            "主页项目往返保留场景、角色、输出和整条时间轴");
        model.ResetCharacter("alice"); model.Undo(); check(model.ToJson() == serialized, "主页撤销恢复完整角色与动作编排");
        model.Redo(); check(!model.Project.EditState.Characters.ContainsKey("alice"), "主页重做恢复角色重置操作"); model.Undo();
        var key = model.TimelineKeys().Single(k => k.CharacterKey == "alice" && k.Kind == "key" && k.Time == 2);
        model.MoveKey(key.Id, 3); check(model.Track("alice").Keys.Last().Time == 3, "主页时间轴拖动更新真实关键帧时间");
        model.Undo(); Reject(() => model.MoveKey(key.Id, 0), check, "主页拒绝将同一轨道的两个关键帧拖到同一时刻");
        check(model.Track("alice").Keys.Last().Time == 2, "无效时间轴编辑不改变项目");
        Reject(() => model.SetDuration(1), check, "主页不能无提示截掉项目结束时间之后的关键帧");
        var animationKey = model.TimelineKeys().First(k => k.Kind == "clip" && k.Time == 1.5);
        model.DeleteKey(animationKey.Id); check(model.Track("alice").Animations.Count == 1, "主页可从时间轴移除动作片段"); model.Undo();
        model.SetTransform("alice", .508, new(.2, .2));
        check(model.Track("alice").Keys.Any(k => Math.Abs(k.Time - .5) < 1e-10), "主页关键帧对齐项目保存的每秒帧数");
        model.Undo();
        model.Playhead = 1.25; model.SelectedCharacterKey = "alice";
        var draft = HomeStudioDocument.FromDraft(model.ToDraft());
        check(draft.Playhead == 1.25 && draft.SelectedCharacterKey == "alice" && draft.ToJson() == model.ToJson() && draft.HasUnsavedChanges,
            "主页草稿恢复游标、选择、修改状态和完整项目");

        var valid = model.Project.ToJson();
        void Invalid(Action<JsonObject> mutate, string label)
        { var bad = (JsonObject)valid.DeepClone(); mutate(bad); Reject(() => HomeProject.FromJson(bad), check, label); }
        Invalid(json => json["unknown"] = true, "主页项目拒绝未知顶层选项，避免保存成功但导出遗漏");
        Invalid(json => json["scene"]!["grade"]!["blur"] = 1, "主页项目拒绝不支持的调色字段");
        Invalid(json => json["duration"] = "4", "主页项目拒绝以字符串冒充时间数值");
        Invalid(json => json["output"]!["fps"] = 60.5, "主页项目要求整数帧率");
        Invalid(json => json["source"]!["spotId"] = -1, "主页项目拒绝无效来源编号");
        Invalid(json => json["source"]!["site"] = "unknown", "主页项目拒绝未知来源站点");
        Invalid(json => json["characters"]!["alice"]!["transform"]!["scale"] = 0, "主页项目拒绝零缩放");
        Invalid(json => json["characters"]!["alice"]!["animations"]![0]!["speed"] = 0, "主页项目拒绝零动作速度");
        Invalid(json => json["characters"]!["alice"]!["keys"]![1]!["time"] = 0, "主页项目拒绝重复或未排序关键帧");
        Invalid(json => json["characters"]!["alice"]!["keys"]![1]!["time"] = 10, "主页项目拒绝项目范围之外的关键帧");
        Invalid(json => json["scene"]!["background"] = 0, "主页项目要求真正布尔值来表达背景开关");

        string root = Path.Combine(Path.GetTempPath(), "ournotes-home-document-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string file = Path.Combine(root, "home.json"); model.Save(file);
            check(File.Exists(file) && !model.HasUnsavedChanges && HomeStudioDocument.Open(file).ToJson() == model.ToJson(), "主页保存采用可重新打开的完整项目格式");
            using var snapshot = model.CreateSnapshot(Path.Combine(root, "inputs")); string prior = File.ReadAllText(snapshot.FilePath);
            model.SetTransform("alice", 0, new(.7, .1));
            check(model.HasUnsavedChanges && File.ReadAllText(snapshot.FilePath) == prior, "继续编辑不会改变已创建的主页导出快照");
            model.Undo(); check(!model.HasUnsavedChanges, "主页撤销到已保存版本后不再显示未保存");
            string renamed = Path.Combine(root, "different", "composition.json"); model.Save(renamed);
            check(model.FilePath == renamed && HomeProject.Read(renamed).Source == source, "主页另存为保留来源，不依赖临时快照目录");

            CommandBuildResult Build(string command, Dictionary<string, string?> values) => CommandLineBuilder.Build(command, values,
                new() { ArchivePath = root, Site = "intl", OutputDirectory = root, WorkingDirectory = root });
            var values = new Dictionary<string, string?> { ["project"] = file, ["out"] = Path.Combine(root, "result.mov") };
            var command = Build("home.clip", values);
            check(command.IsValid && command.Arguments.Contains("--project") && command.Arguments.Contains(file)
                && command.Arguments.Contains("jp") && command.Arguments[2] == "77", "主页命令从项目继承来源，覆盖未触碰的全局国际服默认值："
                    + string.Join("；", command.Errors.Select(e => e.Message)) + " [" + string.Join(" ", command.Arguments) + "]");
            check(command.Arguments.Contains("--adv") && command.Arguments.Contains("42"), "主页命令固定保存项目中的来源归档");
            var wrong = new Dictionary<string, string?>(values) { ["spotId"] = "78" };
            check(!Build("home.clip", wrong).IsValid, "主页命令拒绝与项目不符的主页编号");
            wrong = new(values) { ["site"] = "intl" }; check(!Build("home.clip", wrong).IsValid, "主页命令拒绝显式选择其他站点");
            wrong = new(values) { ["adv"] = "43" }; check(!Build("home.clip", wrong).IsValid, "主页命令拒绝与项目不符的来源剧情");
            wrong = new(values) { ["codec"] = "h264", ["out"] = Path.Combine(root, "opaque.mp4") }; check(!Build("home.clip", wrong).IsValid, "主页命令识别项目中的透明背景并拒绝 H.264");
            wrong = new(values) { ["time"] = "4" }; check(!Build("home.clip", wrong).IsValid, "主页默认导出拒绝从项目结束之后开始");
            wrong = new(values) { ["width"] = "319" }; check(!Build("home.clip", wrong).IsValid, "主页项目导出的显式尺寸仍满足 CineForm 对齐要求");
            var frame = Build("home.frame", new(values) { ["out"] = Path.Combine(root, "frame.png"), ["width"] = "319" });
            check(frame.IsValid, "主页 PNG 项目抽帧不受视频编码对齐限制");
            foreach (var id in new[] { "home.frame", "home.contact", "home.clip", "home.sequence", "home.export" })
                check(CommandCatalog.Get(id).Fields.Single(f => f.Key == "project") is { Kind: CommandFieldKind.InputFile, Required: false, Option: "--project" }, id + "包含独立可选择的主页项目文件");
            File.WriteAllText(file, "{\"format\":\"other\"}"); check(!Build("home.clip", values).IsValid, "主页导出表单拒绝其他格式的项目");
        }
        finally { Directory.Delete(root, true); }
    }
    static void Reject(Action action, Action<bool, string> check, string label)
    {
        bool rejected = false;
        try { action(); }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or InvalidOperationException or NotSupportedException) { rejected = true; }
        check(rejected, label);
    }
}
