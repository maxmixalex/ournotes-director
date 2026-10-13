using System.Text.Json.Nodes;
using OurNotes.Director.Core;

namespace OurNotes.Director.Desktop.Core;

/// <summary>Uses the native command names and numeric IDs. Field hints never replace the complete source row.</summary>
public static class StudioCommandCatalog
{
    public static IReadOnlyList<StudioCommandDescriptor> All { get; } = Build();
    public static StudioCommandDescriptor Get(string command) => All.FirstOrDefault(item => item.Command == command)
        ?? new(command, null, command, "此指令按原文保留。可编辑完整字段；是否可执行以原生验证为准。", Array.Empty<CommandField>());

    /// <summary>
    /// Creates real native syntax. Resource-dependent drafts without a selected resource are disabled,
    /// so an inserted placeholder cannot accidentally load a made-up asset. Fill the fields and enable it.
    /// </summary>
    public static JsonObject Create(string command, string? target = null)
    {
        if (!AdvProgram.KnownCommand(command)) throw new ArgumentException("无法为未知指令生成模板；仍可粘贴和编辑其原始 JSON。", nameof(command));
        var row = new JsonObject { ["cmd"] = command };
        bool hasTarget = !string.IsNullOrWhiteSpace(target);
        void Actor() { row["TargetName"] = target ?? ""; if (!hasTarget) row["IgnoreData"] = 1; }
        void Resource(string field) { row[field] = ""; row["IgnoreData"] = 1; }
        void Tween() { row["Duration"] = 0; row["IsNoWait"] = 0; }
        switch (command)
        {
            case "Delay": row["Frames"] = 30; break;
            case "Director.Actor":
                Actor(); row["Space"] = "screen";
                row["Values"] = new JsonObject { ["position"] = new JsonArray(.5, .5), ["height"] = 1,
                    ["rotation"] = new JsonArray(0, 0, 0), ["scale"] = new JsonArray(1, 1, 1),
                    ["opacity"] = 1, ["visible"] = true, ["animation"] = "director", ["loop"] = true,
                    ["physics"] = true, ["breath"] = true, ["blink"] = true };
                Tween(); break;
            case "Director.Camera":
                row["Values"] = new JsonObject { ["framing"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["zoom"] = 1, ["roll"] = 0 } };
                Tween(); break;
            case "Director.Scene":
                row["Values"] = new JsonObject { ["stageVisible"] = false, ["background"] = new JsonArray(0, 0, 0, 0),
                    ["grade"] = new JsonObject { ["exposure"] = 0, ["contrast"] = 1, ["saturation"] = 1, ["vignette"] = 0 } };
                Tween(); break;
            case "Director.Parameter":
                Actor(); Resource("Parameter"); row["Operation"] = "set"; row["Value"] = 0; Tween(); break;
            case "Character":
                row["TargetName"] = hasTarget ? target : "actor"; Resource("Model"); row["TargetAssetIndex"] = 0; break;
            case "Motion":
                Actor(); Resource("MotionName"); row["MotionFadeIn"] = 0; row["MotionWait"] = 0; break;
            case "MotionLoop":
                Actor(); Resource("MotionName"); row["MotionFadeIn"] = 0; row["Parameter1"] = ""; break;
            case "Expression":
                Actor(); row["ExpressionName"] = ""; row["MotionFadeIn"] = 0; break;
            case "Talk":
                row["TargetName"] = target ?? ""; row["AdvTextID"] = ""; row["TargetTextIDs"] = new JsonArray();
                row["VoiceIDs"] = new JsonArray(); row["IgnoreLipSync"] = false; row["IsNoWait"] = 0; break;
            case "Voice":
                row["TargetName"] = target ?? ""; row["VoiceIDs"] = new JsonArray(); row["IgnoreLipSync"] = false; row["IsNoWait"] = 0; break;
            case "Subtitles": case "Location":
                row["AdvTextID"] = ""; row["Duration"] = 0; break;
            case "In":
                Actor(); row["PositionType"] = 5; row["Duration"] = .3; row["MotionName"] = ""; row["ExpressionName"] = ""; row["MotionFadeIn"] = 0; break;
            case "Out":
                Actor(); row["PositionType"] = 0; row["Duration"] = .3; break;
            case "Costume": Actor(); row["TargetAssetIndex"] = 0; break;
            case "Pause": case "Resume": case "EyeBlink": Actor(); break;
            case "Stage": case "Still": case "Frame": case "TalkWindow": case "ChatWindow": case "PostEffect":
                Resource("TargetAssetName"); Tween(); break;
            case "Effect":
                Actor(); Resource("TargetAssetName"); row["PositionType"] = 5; row["Parameter1"] = ""; row["Parameter2"] = ""; break;
            case "Bgm": row["BgmID"] = 0; row["Parameter1"] = 0; row["IgnoreData"] = 1; break;
            case "Se": row["SeID"] = 0; row["Parameter1"] = ""; row["DelaySeconds"] = 0; row["IgnoreData"] = 1; break;
            case "Movie": case "Clip": row["VideoID"] = 0; row["Parameter1"] = 0; row["IgnoreData"] = 1; break;
            case "SoundVolume": row["Parameter1"] = "bgm"; row["Parameter2"] = 1; row["Duration"] = 0; break;
            case "GoTo": Resource("Parameter1"); break;
            case "Wait": case "CancelDelay": case "ForceAuto": case "ChoiceSet": break;
            case "ChatTalk": case "ChatStamp": case "ChatRead": case "ChatTyping":
                row["TargetName"] = target ?? ""; row["AdvTextID"] = ""; row["IgnoreData"] = 1; break;
            case "Tilt": case "Role": case "Timeline": case "ChoiceShow":
                // These names are present in the game enum, but not every native scene binds them.
                row["IgnoreData"] = 1; break;
            default:
                if (Get(command).Fields.Any(field => field.Key == "TargetName")) Actor();
                Tween();
                row["Parameter1"] = command is "Zoom" or "Brightness" or "Alpha" ? 1 : 0;
                if (Get(command).Fields.Any(field => field.Key == "PositionType")) row["PositionType"] = 5;
                break;
        }
        // A template passes syntax/value validation; actual assets still require native validation.
        var native = AdvProgram.Row(row, "new instruction"); DirectorValidation.Check(native);
        if (!native.Ignored) _ = AdvPlayback.FixedDelayFrames(native);
        return row;
    }

    static IReadOnlyList<StudioCommandDescriptor> Build()
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["In"]="角色入场", ["Out"]="角色退场", ["Talk"]="对白与语音", ["Delay"]="等待时长", ["Shake"]="角色与画面震动",
            ["FadeOut"]="画面淡出", ["FadeIn"]="画面淡入", ["Focus"]="游戏镜头对焦", ["Forward"]="槽位移到前景", ["Back"]="槽位移到后景",
            ["Flash"]="画面闪光", ["Brightness"]="亮度", ["MoveToRight"]="槽位向右移动", ["MoveToLeft"]="槽位向左移动",
            ["Bgm"]="背景音乐", ["SoundVolume"]="音量", ["Expression"]="切换表情", ["Pause"]="暂停角色动画", ["Resume"]="恢复角色动画",
            ["Location"]="地点标题", ["Motion"]="播放角色动作", ["Character"]="加载角色模型", ["Costume"]="切换服装索引",
            ["Stage"]="舞台背景", ["Movie"]="完整视频播放", ["Clip"]="视频片段", ["Subtitles"]="字幕", ["Wait"]="原剧情自动等待",
            ["Still"]="插图", ["Se"]="音效", ["Angle"]="角色角度", ["Pan"]="游戏镜头水平转动", ["Tilt"]="游戏垂直转动指令",
            ["TalkWindow"]="对白窗口", ["ChatWindow"]="聊天窗口", ["ChatTalk"]="聊天消息", ["ChatStamp"]="聊天贴图",
            ["ChatRead"]="聊天已读", ["ChoiceSet"]="设置分支", ["ChoiceShow"]="显示分支指令", ["GoTo"]="跳转到指令标记",
            ["PostEffect"]="后处理资源", ["Frame"]="画面框架", ["Timeline"]="游戏时间轴指令", ["Look"]="角色视线",
            ["Pedestal"]="游戏镜头上下移动", ["Track"]="游戏镜头左右移动", ["DoF"]="景深", ["Role"]="游戏角色职责指令",
            ["CameraShake"]="相机震动", ["Voice"]="语音与口型", ["Zoom"]="游戏镜头缩放", ["Effect"]="场景特效", ["Alpha"]="透明度",
            ["ForceAuto"]="切换强制自动播放", ["StageEnv"]="舞台环境", ["RimLight"]="角色轮廓光", ["CancelDelay"]="取消延迟",
            ["MoveToUp"]="槽位向上移动", ["MoveToDown"]="槽位向下移动", ["MoveToForward"]="槽位向前移动", ["MoveToBack"]="槽位向后移动",
            ["MoveToDirection"]="槽位三维移动", ["ChatTyping"]="聊天输入状态", ["LookTarget"]="注视另一角色", ["PanV2"]="游戏镜头转动与平移",
            ["MotionLoop"]="参数循环动作", ["EyeBlink"]="眨眼状态", ["Director.Actor"]="自由角色变换", ["Director.Camera"]="导演相机",
            ["Director.Parameter"]="模型参数覆盖", ["Director.Scene"]="背景与导演调色"
        };
        return AdvProgram.Names.Where(name => name is not null).Cast<string>().Concat(AdvProgram.DirectorCommands)
            .Select(command =>
            {
                int id = Array.IndexOf(AdvProgram.Names, command);
                return new StudioCommandDescriptor(command, id < 0 ? null : id, labels.GetValueOrDefault(command, command),
                    Description(command), Fields(command));
            }).ToArray();
    }

    static string Description(string command) => command switch
    {
        "Delay" => "Frames 是精确的 30 fps 帧数；也可仅使用原生 Duration 秒数。不能把 Frames 与非零 Duration 混用。",
        "Wait" => "沿用实际原剧情的自动播放等待设置，实际等待时间由原生执行返回。",
        "Talk" => "AdvTextID 引用项目 text；VoiceIDs 按说话角色顺序绑定语音与口型。可在对白编辑栏直接写文字。",
        "Voice" => "使用真实 VoiceIDs 播放语音并驱动所选角色口型；完整行为沿用原生执行器。",
        "Character" => "加载已归档的 Model，并以 TargetName 引用。新空模板先禁用，选择真实模型后启用。",
        "Motion" => "MotionName 必须存在于该角色模型；切换动作沿用原生动画管理器及淡入设置。空模板先禁用。",
        "Expression" => "选择模型中的实际表情；空 ExpressionName 恢复模型默认表情。",
        "Director.Actor" => "screen 坐标从画布左上角归一化，height 是完整模型画布占输出高度的比例；world 使用世界单位。",
        "Director.Camera" => "framing 编辑完整画布的平移、缩放与旋转；position、rotation、fieldOfView 编辑世界相机。",
        "Director.Scene" => "设置透明或纯色背景、实际背景图片和曝光/对比度/饱和度/暗角。舞台资源由 Stage 指令选择。",
        "Director.Parameter" => "使用实际模型参数名设置数值或 release。未选择参数的模板先禁用，不会假设模型有某个参数。",
        "GoTo" => "Parameter1 指向正文中已有指令的 Key。循环与跳转的实际执行时序由原生预览记录。",
        "ForceAuto" => "切换原生强制自动播放状态；重复执行会再次切换。",
        "Tilt" or "Role" or "Timeline" or "ChoiceShow" => "保留原始游戏枚举名称；当前场景是否连接对应执行器由原生验证确认，新模板先禁用。",
        "Stage" or "Still" or "Frame" or "PostEffect" or "Effect" or "Movie" or "Clip" or "Bgm" or "Se" or "TalkWindow" or "ChatWindow"
            => "使用原剧情或归档中的真实资源。未选择资源的模板先禁用，填写后可启用；所有原有参数保持可编辑。",
        _ => "保留原始游戏指令名称、编号和参数；按照指令列表顺序由同一个原生执行器播放。"
    };

    static IReadOnlyList<CommandField> Fields(string command)
    {
        var fields = new List<CommandField>();
        void Text(string key, string label, string description = "", bool required = false)
            => fields.Add(new(key, label, CommandFieldKind.Text, description) { Required = required });
        void Number(string key, string label, string description = "", bool integer = false)
            => fields.Add(new(key, label, integer ? CommandFieldKind.Integer : CommandFieldKind.Number, description));
        void Flag(string key, string label, string description = "") => fields.Add(new(key, label, CommandFieldKind.Boolean, description));
        bool director = command.StartsWith("Director.", StringComparison.Ordinal);
        bool actor = command is "Character" or "Costume" or "In" or "Out" or "Motion" or "MotionLoop" or "Expression" or "Pause" or "Resume"
            or "Angle" or "Look" or "LookTarget" or "Brightness" or "EyeBlink" or "RimLight" or "Effect" or "Talk" or "Voice"
            or "Director.Actor" or "Director.Parameter";
        if (actor) Text("TargetName", "角色或目标名称", "引用加载角色时使用的 TargetName；不是展示名称。", command is not ("Talk" or "Voice"));
        if (command == "Character")
        { Text("Model", "归档模型", "选择实际模型编号，不填写虚构路径。", true); Text("TargetAssetName", "原剧情模型资源名"); Number("TargetAssetIndex", "服装索引", integer: true); }
        if (command is "Motion" or "MotionLoop" or "In") { Text("MotionName", "动作名称", "来自所选模型的动作目录。", command != "In"); Number("MotionFadeIn", "动作淡入秒数"); Number("MotionWait", "动作等待秒数"); }
        if (command is "Expression" or "Motion" or "In") { Text("ExpressionName", "表情名称", "空值使用模型默认表情。"); if (command == "Expression") Number("MotionFadeIn", "表情淡入秒数"); }
        if (command is "Talk" or "Subtitles" or "Location" or "ChatTalk") Text("AdvTextID", "对白文本 ID", "引用 text 对象，编辑对白栏可自动创建独立文本。", true);
        if (command is "Talk" or "Voice")
        { Text("VoiceIDs", "语音 ID 数组", "按角色顺序排列的真实语音编号。"); Flag("IgnoreLipSync", "关闭口型"); }
        if (command == "Talk")
        { Text("TargetTextIDs", "说话者名称文本 ID 数组"); Number("TargetStatus", "说话者显示模式", "0=正常名称，1=未知名称，2=隐藏名称。", true); }
        if (command is "Stage" or "Still" or "Frame" or "Effect" or "PostEffect" or "TalkWindow" or "ChatWindow")
            Text("TargetAssetName", "实际资源名称", "来自所选归档或原剧情。", true);
        if (command is "Movie" or "Clip") Text("VideoID", "实际视频 ID", "保留原始整数 ID，不转换为浮点数。", true);
        if (command == "Bgm") Text("BgmID", "实际背景音乐 ID", "0 表示停止背景音乐。", true);
        if (command == "Se") Text("SeID", "实际音效 ID", "保留原始整数 ID。", true);
        if (command == "Costume") Number("TargetAssetIndex", "服装索引", integer: true);
        if (command == "Director.Actor")
        {
            fields.Add(new("Space", "坐标空间", CommandFieldKind.Choice, "screen 使用画布坐标，world 使用世界坐标。")
                { Choices = [new("screen", "画布"), new("world", "世界")] });
            Text("Values.position", "位置", "screen 为 [x,y]，world 为 [x,y,z]。"); Number("Values.height", "画布高度比例");
            Text("Values.rotation", "旋转 XYZ（度）"); Text("Values.scale", "缩放 XYZ");
            Number("Values.opacity", "不透明度"); Flag("Values.visible", "显示角色");
            Flag("Values.physics", "物理模拟"); Flag("Values.breath", "呼吸"); Flag("Values.blink", "自动眨眼"); Flag("Values.loop", "动作循环");
            Text("Values.animation", "动画管理模式", "director 或 game。"); Number("Values.order", "绘制顺序", integer: true);
        }
        if (command == "Director.Camera")
        {
            Number("Values.framing.x", "画布横向偏移"); Number("Values.framing.y", "画布纵向偏移");
            Number("Values.framing.zoom", "画布缩放"); Number("Values.framing.roll", "画布旋转（度）");
            Text("Values.position", "世界相机位置 XYZ"); Text("Values.rotation", "世界相机旋转 XYZ"); Number("Values.fieldOfView", "世界相机视场角");
        }
        if (command == "Director.Scene")
        {
            Text("Values.background", "背景 RGBA", "[0,0,0,0] 表示原生透明背景。");
            Number("Values.grade.exposure", "曝光"); Number("Values.grade.contrast", "对比度");
            Number("Values.grade.saturation", "饱和度"); Number("Values.grade.vignette", "暗角");
            Text("Values.backgroundImage.path", "本地背景图片", "相对路径以项目文件目录为基准。");
        }
        if (command == "Director.Parameter")
        { Text("Parameter", "实际模型参数名", required: true); Text("Operation", "操作", "set 或 release。"); Number("Value", "参数值"); }
        if (command == "Delay") Number("Frames", "精确等待帧数", "30 fps；与非零 Duration 互斥。", true);
        if (command is "In" or "Out" or "Focus" or "Forward" or "Back" or "LookTarget" or "DoF" or "Effect"
            || command.StartsWith("MoveTo", StringComparison.Ordinal)) Number("PositionType", "游戏槽位", "常规角色槽位使用 1、3、5、7、9。", true);
        if (command == "Focus") Number("CameraDistance", "原生对焦距离枚举", integer: true);
        if (command is not ("Wait" or "CancelDelay" or "ForceAuto" or "GoTo" or "ChoiceSet" or "Character" or "Costume"))
        { Number("Duration", "持续秒数"); Flag("IsNoWait", "并行执行", "是否支持并行及具体等待行为由该原生指令决定。"); }
        if (director || command is "Focus" or "Pan" or "PanV2" or "Track" or "Pedestal" or "Zoom") Text("Ease", "缓动", "使用原生 AdvEase 名称，例如 Linear、InOutSine。");
        if (!director)
        {
            Number("DelaySeconds", "执行前延迟秒数", "仅对支持此字段的原生指令生效。");
            for (int index = 1; index <= 4; index++) Text("Parameter" + index, command == "GoTo" && index == 1 ? "目标指令 Key" : "原生参数 " + index,
                "保留原指令约定的字符串或数值内容。", command == "GoTo" && index == 1);
        }
        return fields.AsReadOnly();
    }
}
