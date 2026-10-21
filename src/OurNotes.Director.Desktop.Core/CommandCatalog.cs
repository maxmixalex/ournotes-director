namespace OurNotes.Director.Desktop.Core;

public enum CommandGroup { Archive, Stories, Home, Models, Assets, Media, Diagnostics, Scenes }

public enum CommandFieldKind
{
    Text, Integer, Number, Boolean, Choice, MultiChoice,
    InputFile, InputDirectory, OutputFile, OutputDirectory, NewDirectory
}

public enum CommandOutputKind { None, Json, Image, Video, Audio, File, Directory }

public sealed record CommandChoice(string Value, string Label);

/// <summary>A form field; values are separate process arguments, never shell fragments.</summary>
public sealed record CommandField(string Key, string Label, CommandFieldKind Kind, string Description)
{
    public bool Required { get; init; }
    public string? DefaultValue { get; init; }
    public double? Minimum { get; init; }
    public double? Maximum { get; init; }
    public bool MinimumExclusive { get; init; }
    public IReadOnlyList<CommandChoice> Choices { get; init; } = [];
    public bool Advanced { get; init; }
    public int? Position { get; init; }
    public string? Option { get; init; }
    public IReadOnlyList<string> FileExtensions { get; init; } = [];
    public bool AllowCustomValue { get; init; }
    public string? LookupCommandId { get; init; }
    public string? Placeholder { get; init; }
    public string? SuggestedFileName { get; init; }
    public IReadOnlyList<string> Aliases { get; init; } = [];
}

public sealed record CommandDefinition(
    string Id,
    CommandGroup Group,
    string Label,
    string Description,
    IReadOnlyList<string> CommandTokens,
    IReadOnlyList<CommandField> Fields,
    bool RequiresArchive,
    CommandOutputKind OutputKind);

public static class CommandCatalog
{
    private static readonly CommandChoice[] Sites = [new("intl", "国际服"), new("jp", "日服")];
    private static readonly CommandChoice[] Languages = [new("zh-Hans", "简体中文"), new("zh-Hant", "繁体中文"), new("ja", "日语"), new("en", "英语"), new("ko", "韩语")];
    private static readonly CommandChoice[] Codecs = [new("cineform", "CineForm 透明 MOV"), new("prores", "ProRes 4444 透明 MOV"), new("ffv1", "FFV1 无损 MKV"), new("h264", "H.264 不透明 MP4")];

    public static IReadOnlyList<CommandDefinition> All { get; } = Create();

    public static CommandDefinition Get(string id) => All.FirstOrDefault(command => command.Id == id)
        ?? throw new ArgumentException("未知操作：" + id, nameof(id));

    public static IReadOnlyList<CommandDefinition> ForGroup(CommandGroup group) =>
        All.Where(command => command.Group == group).ToArray();

    public static string GroupLabel(CommandGroup group) => group switch
    {
        CommandGroup.Archive => "素材归档", CommandGroup.Stories => "剧情与对白",
        CommandGroup.Home => "主页动画", CommandGroup.Models => "角色模型",
        CommandGroup.Assets => "素材资源", CommandGroup.Media => "媒体工具",
        CommandGroup.Diagnostics => "诊断检查", CommandGroup.Scenes => "场景项目",
        _ => throw new ArgumentOutOfRangeException(nameof(group))
    };

    private static IReadOnlyList<CommandDefinition> Create()
    {
        var commands = new List<CommandDefinition>();
        void Add(string id, CommandGroup group, string label, string description, CommandOutputKind output,
            IEnumerable<CommandField>? fields = null, bool archive = true, bool site = true,
            bool language = false, bool native = false, bool ffmpeg = false, bool ffprobe = false)
        {
            var items = (fields ?? []).ToList();
            if (archive) items.Add(ArchiveField());
            if (site && archive) items.Add(SiteField(id.StartsWith("home.", StringComparison.Ordinal)));
            if (language) items.Add(id is "story.dialogue-plan" or "story.export-dialogue"
                ? LanguageField() with { DefaultValue = "zh-Hans", AllowCustomValue = false } : LanguageField());
            if (native) items.AddRange(NativeFields());
            if (ffmpeg) items.Add(Tool("ffmpeg", "视频编码器", "可选；默认使用发布包中的 FFmpeg。"));
            if (ffprobe) items.Add(Tool("ffprobe", "媒体探测器", "可选；默认使用发布包中的 FFprobe。"));
            items.Add(Flag("debug", "详细错误信息", "失败时在任务日志中显示详细诊断。", true));
            items.Add(Flag("json", "结构化结果", "命令始终返回结构化结果；此选项兼容命令行调用。", true));
            commands.Add(new(id, group, label, description, id.Split('.'), items.AsReadOnly(), archive, output));
        }

        Add("archive", CommandGroup.Archive, "下载或更新归档", "发现、下载并校验一个乐队的素材，更新索引与导演资源；再次运行会自动续传和复用已有文件。", CommandOutputKind.Directory,
        [
            Choice("band", "乐队", "提供已记录名称的快捷选择，也支持其他上游乐队名称、原始乐队编号或 Band_Name_* 文本编号。", "millsage",
                [new("millsage", "Millsage 乐队"), new("mygo", "MyGO!!!!! 乐队"), new("mujica", "Ave Mujica 乐队"),
                 new("yumemita", "Yumemita 乐队"), new("kadan", "Kadan 乐队")], allowCustom: true),
            Output("out", "归档目录", "可以选择已有归档以增量更新，也可以指定新目录。", CommandFieldKind.OutputDirectory, true, "archive") with { Aliases = ["archive"] },
            Choice("sites", "归档站点", "留空同时下载国际服和日服；此范围独立于全局浏览站点。", "intl,jp", Sites, multi: true) with { Aliases = ["site"] },
            Choice("languages", "下载语言", "留空下载所有可用语言；通用素材始终保留。", null, Languages, multi: true, allowCustom: true),
            Integer("concurrency", "并发下载数", "同时处理的下载数。", "8", 1, 64, true),
            Integer("retries", "最大尝试次数", "每个暂时失败的下载最多尝试多少次。", "4", 1, 10, true),
            Flag("no-deep", "跳过深度发现", "只使用常规发现结果，可能减少发现到的主页或其他引用素材。", true)
        ], archive: false, site: false);

        foreach (string verb in new[] { "list", "search" })
            Add("story." + verb, CommandGroup.Stories, verb == "list" ? "剧情列表" : "搜索剧情", "按当前站点查看已归档剧情，可按标题或元数据搜索。", CommandOutputKind.Json,
                [Pos("query", "搜索内容", "留空列出全部剧情。", required: false), .. Page()]);
        Add("story.show", CommandGroup.Stories, "剧情详情", "查看剧情标题、来源和原始元数据。", CommandOutputKind.Json, [StoryId()]);
        Add("story.commands", CommandGroup.Stories, "原始剧情指令", "读取保留原始数值的剧情指令，可选择分页或保存 JSON。", CommandOutputKind.Json,
            [StoryId(), .. Page(), .. OptionalJson("commands.json")]);
        Add("commands.list", CommandGroup.Stories, "剧情指令索引", "查看指定剧情的可检索指令索引。", CommandOutputKind.Json,
            [OptionId("adv", "剧情", "选择剧情编号。", "story.list", true), .. Page()]);
        foreach (string name in new[] { "dialogue", "voice" })
            Add(name + ".search", CommandGroup.Stories, name == "dialogue" ? "搜索对白" : "搜索语音", "检索对白文本，并可按角色、剧情和语言筛选。", CommandOutputKind.Json,
            [
                Pos("query", "搜索文本", "输入需要查找的对白内容。"),
                OptionText("speaker", "说话角色", "使用原始角色名称筛选；留空不限。") with { Aliases = ["actor"] },
                OptionId("adv", "限定剧情", "只搜索这一剧情；留空不限。", "story.list"),
                Flag("with-voice", "仅含语音", "只显示包含语音资源的对白。"), .. Page()
            ], language: true);
        Add("voice.extract", CommandGroup.Stories, "提取原始语音", "校验并提取原始音频文件，保留来源格式。", CommandOutputKind.Audio,
        [
            Pos("voiceId", "语音编号", "从语音搜索结果中选择原始语音编号。", lookup: "voice.search"),
            OptionId("adv", "来源剧情", "同一编号出现多个素材时，用剧情消除歧义。", "story.list"),
            Output("out", "音频文件", "按原始音频格式保存，不进行转码。", CommandFieldKind.OutputFile, true, "voice.m4a"), Overwrite()
        ]);
        foreach (string verb in new[] { "dialogue-plan", "export-dialogue" })
            Add("story." + verb, CommandGroup.Stories, verb == "dialogue-plan" ? "编排对白时间线" : "导出对白素材包",
                "按对白顺序编排配音与字幕；时间线采用语音长度和人工间隔，不代替原剧情演出。",
                verb == "dialogue-plan" ? CommandOutputKind.Json : CommandOutputKind.Directory,
                [StoryId(), .. Page(), Number("gap", "对白间隔（秒）", "每句内容结束后的额外间隔。", "0.4", 0, 30),
                    Number("silent-seconds", "无语音对白时长（秒）", "没有配音的对白采用此时长。", "2", 0, 60) with { MinimumExclusive = true },
                    .. (verb == "dialogue-plan" ? OptionalJson("dialogue-plan.json") : [Output("out", "新素材包目录", "生成 WAV、字幕、原语音和时间线记录；目录必须不存在。", CommandFieldKind.NewDirectory, true, "dialogue-package")])],
                language: true, ffmpeg: verb == "export-dialogue");

        foreach (string verb in new[] { "frame", "contact", "clip", "state", "sequence" })
            Add("story." + verb, CommandGroup.Stories, RenderLabel(verb, "剧情"),
                verb == "sequence" ? "使用指定项目或指令文件输出 PNG 序列；可校验已有任务并续渲。" : "使用原生剧情执行器求值；渲染与抽帧按原始 30 帧时序进行。",
                RenderOutput(verb), [StoryId(), Script(verb == "sequence"), .. StoryRenderFields(verb)],
                language: true, native: true, ffmpeg: true);
        foreach (string verb in new[] { "native-program", "native-check", "audit" })
            Add("story." + verb, CommandGroup.Diagnostics,
                verb == "native-program" ? "编译剧情程序" : verb == "native-check" ? "检查剧情资源与首帧" : "审计剧情归档",
                verb == "native-program" ? "编译初始化、主体与结束指令，不渲染画面。" : verb == "native-check" ? "初始化真实资源并检查第 0 帧；不代表整段动画已经验收。" : "检查剧情指令和资源存在性；不执行渲染。",
                CommandOutputKind.Json, [StoryId(verb != "native-program"), .. (verb == "native-check" ? new[] { Script() } : []), .. OptionalJson(verb + ".json")],
                language: verb == "native-check", native: verb == "native-check", ffmpeg: verb == "native-check");

        Add("home.list", CommandGroup.Home, "主页列表", "发现已有归档中的主页，无需重新下载或迁移索引。", CommandOutputKind.Json, Page());
        Add("home.show", CommandGroup.Home, "主页详情与角色", "查看来源、角色、共享骨架的拆分依据和独立导出支持状态。", CommandOutputKind.Json,
            [HomeId(), HomeSource()]);
        Add("home.animations", CommandGroup.Home, "主页动画列表", "查询实际动画、循环设置、时长及每个骨架的可用动画。", CommandOutputKind.Json,
            [HomeId(), HomeSource(), HomeCharacter(), Animation(), Loop()]);
        foreach (string verb in new[] { "frame", "contact", "clip", "sequence", "export" })
            Add("home." + verb, CommandGroup.Home, verb == "export" ? "批量导出主页与角色" : RenderLabel(verb, "主页"),
                verb == "export" ? "输出完整场景和每个独立角色，统一起点与时长；也可选择全部主页或仅角色。" : "渲染完整房间与 Spine，或导出透明角色；不需要 Cubism SDK。",
                verb == "export" ? CommandOutputKind.Directory : RenderOutput(verb),
                [HomeId(verb == "export"), HomeSource(), HomeCharacter(verb == "export"), Animation(), Loop(), .. HomeRenderFields(verb)],
                ffmpeg: verb is "clip" or "export");

        Add("model.list", CommandGroup.Models, "模型列表", "查看已下载模型，可按角色名称筛选。", CommandOutputKind.Json,
            [OptionText("character", "角色名称", "模型编号中包含的角色名称；留空不限。"), .. Page()]);
        Add("model.show", CommandGroup.Models, "模型详情", "查看模型资源、动作和表情索引。", CommandOutputKind.Json, [ModelId()]);
        Add("model.parameters", CommandGroup.Models, "模型参数", "加载模型，查看参数、部件与原生取值范围。", CommandOutputKind.Json, [ModelId()], native: true);
        foreach (string name in new[] { "motion", "expression" })
            Add(name + ".list", CommandGroup.Models, name == "motion" ? "模型动作列表" : "模型表情列表", "列出所选模型的资源名称。", CommandOutputKind.Json,
                [ModelId(), .. Page()]);
        Add("expression.show", CommandGroup.Models, "表情参数详情", "查看原始表情参数、数值、混合方式和淡入淡出设置。", CommandOutputKind.Json,
            [ModelId(), Pos("expressionId", "表情", "从该模型的表情列表中选择。", 1, lookup: "expression.list")], native: true);
        Add("model.gallery", CommandGroup.Models, "模型动作或表情图集", "生成模型的动作或表情预览图集。", CommandOutputKind.Image,
        [
            ModelId(), Choice("kind", "图集内容", "选择动作或表情。", "expression", [new("expression", "表情"), new("motion", "动作")]),
            Number("time", "预览时间（秒）", "每个动作或表情的采样时间。", "1", 0, 60), .. Page(48, 48),
            PngOutput("gallery.png"), Overwrite()
        ], native: true);

        foreach (var (name, label) in new[] { ("stage", "舞台"), ("postfx", "后期效果"), ("still", "静态插图"), ("frame", "画面边框"), ("effect", "特效"), ("media", "剧情视频") })
        {
            Add(name + ".list", CommandGroup.Assets, label + "列表", "查看导演资源库中的" + label + "。", CommandOutputKind.Json, Page());
            Add(name + ".show", CommandGroup.Assets, label + "详情", "查看原始资源定义与来源文件。", CommandOutputKind.Json,
                [Pos("resourceId", label, "从对应资源列表中选择。", lookup: name + ".list")]);
        }
        Add("asset.search", CommandGroup.Assets, "搜索素材文件", "按逻辑路径、资源类型、剧情或模型搜索素材索引。", CommandOutputKind.Json,
        [
            Pos("query", "路径关键词", "留空搜索全部文件。", required: false),
            Choice("kind", "素材类型", "留空不限；也可以输入归档中的其他类型名称。", null,
                [new("image", "图像"), new("audio", "音频"), new("video", "视频"), new("json", "结构化数据"),
                    new("font-atlas", "字体图集"), new("moc3", "模型数据"), new("shader", "着色器"), new("other", "其他文件")], allowCustom: true),
            OptionId("adv", "限定剧情", "按来源剧情筛选。", "story.list"),
            OptionId("model", "限定模型", "按来源模型筛选，与剧情筛选择一使用。", "model.list"), .. Page()
        ]);
        foreach (string verb in new[] { "show", "verify", "extract" })
            Add("asset." + verb, CommandGroup.Assets, verb == "show" ? "查看素材数据" : verb == "verify" ? "校验素材" : "提取素材文件",
                verb == "show" ? "读取并显示 JSON 素材；必须指定来源剧情或模型。" : "按原始清单解码并校验素材；剧情和模型必须选择一个。",
                verb == "extract" ? CommandOutputKind.File : CommandOutputKind.Json,
                [Pos("logicalPath", "逻辑路径", "从素材搜索结果选择归档内路径；语音提取也支持 voice:编号。", lookup: "asset.search"),
                    OptionId("adv", "来源剧情", "剧情和模型必须选择一个；语音编号可选。", "story.list"),
                    OptionId("model", "来源模型", "剧情和模型必须选择一个。", "model.list"),
                    .. (verb == "extract" ? new[] { Output("out", "输出文件", "保留原始格式和字节内容。", CommandFieldKind.OutputFile, true, "asset.bin"), Overwrite() } : [])], language: true);
        Add("asset.gallery", CommandGroup.Assets, "素材图集", "将匹配的 PNG 素材排列为预览图集。", CommandOutputKind.Image,
            [Pos("query", "路径关键词", "留空使用当前站点的所有 PNG 素材。", required: false), .. Page(48, 48), PngOutput("assets.png"), Overwrite()]);

        Add("media.probe", CommandGroup.Media, "检查本地媒体", "查看视频、音频、编码、时长与帧率；不需要素材归档。", CommandOutputKind.Json,
            [Input("input", "媒体文件", "选择本地视频或音频。")], archive: false, site: false, ffprobe: true);
        Add("media.clip", CommandGroup.Media, "裁剪或转换视频", "提取指定时间段，保留源帧率，不包含音频；透明 CineForm 可直接用于后期。", CommandOutputKind.Video,
        [
            Input("input", "源视频", "选择本地视频。"),
            Number("start", "开始时间（秒）", "从源视频的此位置开始。", "0", 0, null),
            Number("duration", "片段长度（秒）", "输出片段长度，不能超出源视频。", "5", 0, null) with { MinimumExclusive = true },
            Choice("codec", "视频编码", "默认 ProRes HQ；选择 CineForm 可保留透明通道。", "prores",
                [new("prores", "ProRes HQ 视频"), new("cineform", "CineForm 透明视频")]), Quality(),
            Output("out", "输出视频", "输出为 MOV 文件。", CommandFieldKind.OutputFile, true, "clip.mov", [".mov"]), Overwrite()
        ], archive: false, site: false, ffmpeg: true, ffprobe: true);
        Add("media.mux", CommandGroup.Media, "合并视频、音频与字幕", "复制原视频流并替换音轨；可添加独立字幕轨，保留视频原有透明通道。", CommandOutputKind.Video,
        [
            Input("input", "源视频", "选择需要保留画面的视频。"),
            InputOption("audio", "音频文件", "短音轨补静音，长音轨按视频时长裁切。", true),
            InputOption("subtitles", "字幕文件", "可选 SRT 文件，作为可开关字幕轨保存。", false, [".srt"]),
            Output("out", "合并后视频", "MP4 支持 H.264/HEVC；MOV 支持 CineForm、ProRes、H.264/HEVC；其他编码可使用 MKV。", CommandFieldKind.OutputFile, true, "merged.mov", [".mov", ".mkv", ".mp4"]), Overwrite()
        ], archive: false, site: false, ffmpeg: true, ffprobe: true);
        Add("image.probe", CommandGroup.Media, "检查 PNG 图像", "查看尺寸、透明度、可见边界和像素哈希。", CommandOutputKind.Json,
            [Input("input", "PNG 图像", "选择需要检查的图像。", extensions: [".png"])], archive: false, site: false);
        Add("image.compare", CommandGroup.Media, "比较两张 PNG", "比较像素和透明度误差；任务成功只代表完成比较，不代表两张图像相同。", CommandOutputKind.Json,
            [Input("first", "第一张图像", "选择作为参考的图像。", extensions: [".png"]), Input("second", "第二张图像", "选择需要比较的图像。", 1, [".png"])], archive: false, site: false);

        Add("help", CommandGroup.Diagnostics, "命令与能力说明", "显示当前程序支持的命令、参数与能力边界。", CommandOutputKind.Json, archive: false, site: false);
        Add("probe.runtime", CommandGroup.Diagnostics, "检查运行环境", "检查随包提供的模型运行库、口型引擎和媒体工具；不需要素材归档。", CommandOutputKind.Json,
            archive: false, site: false, native: true, ffmpeg: true, ffprobe: true);
        Add("probe.archive", CommandGroup.Diagnostics, "检查归档概况", "查看归档路径、索引统计与索引指纹；不等同于完整素材校验。", CommandOutputKind.Json, site: false);
        Add("probe.model", CommandGroup.Diagnostics, "检查模型索引", "读取所选模型的归档索引与资源信息。", CommandOutputKind.Json, [ModelId()]);
        Add("probe.native", CommandGroup.Diagnostics, "检查模型原生加载", "加载模型原始数据并检查原生模型运行库。", CommandOutputKind.Json, [ModelId()], native: true);
        Add("probe.sound", CommandGroup.Diagnostics, "检查声音解码与循环", "原生解码指定剧情的声音，查看长度、声道、峰值和循环信息。", CommandOutputKind.Json,
            [Pos("soundId", "声音编号", "选择原始声音编号。", lookup: "voice.search"), OptionId("adv", "来源剧情", "声音来自的剧情编号。", "story.list", true)], ffmpeg: true);
        Add("probe.camera", CommandGroup.Diagnostics, "检查剧情相机", "查看原生主相机的投影、变换和裁剪范围。", CommandOutputKind.Json, [StoryId()]);

        Add("shot.init", CommandGroup.Scenes, "新建旧版镜头模板", "从当前归档创建兼容旧版 Shot 格式的镜头模板。", CommandOutputKind.File,
        [
            Choice("template", "构图模板", "选择初始角色构图。", "fullbody", [new("fullbody", "全身"), new("portrait", "半身肖像"), new("duet", "双角色")]),
            OptionId("model", "初始模型", "留空从当前站点已下载模型中选择。", "model.list"),
            Output("out", "镜头文件", "保存为 JSON 文件，之后可在场景页编辑或渲染。", CommandFieldKind.OutputFile, true, "shot.json", [".json"]), Overwrite()
        ], language: true);
        Add("shot.validate", CommandGroup.Scenes, "校验旧版镜头", "加载旧版 Shot 模型并校验镜头与透明输出设置。", CommandOutputKind.Json,
            [Input("project", "镜头文件", "选择旧版 Shot JSON 文件。", extensions: [".json"])], native: true);
        foreach (string verb in new[] { "validate", "state", "frame", "contact", "clip", "sequence" })
            Add("render." + verb, CommandGroup.Scenes, verb == "validate" ? "校验统一项目" : RenderLabel(verb, "项目"),
                "统一项目和旧版镜头均使用原生剧情执行器；未覆盖的参数保留项目自身设置。",
                RenderOutput(verb), [Input("project", "项目文件", "选择统一项目或兼容的旧版 Shot JSON 文件。", extensions: [".json"]), .. StoryRenderFields(verb, true)],
                language: true, native: true, ffmpeg: true);

        return commands.AsReadOnly();
    }

    private static CommandField Pos(string key, string label, string description, int position = 0, bool required = true, string? lookup = null) =>
        new(key, label, CommandFieldKind.Text, description) { Position = position, Required = required, LookupCommandId = lookup };
    private static CommandField OptionText(string key, string label, string description, bool advanced = false) =>
        new(key, label, CommandFieldKind.Text, description) { Option = "--" + key, Advanced = advanced };
    private static CommandField OptionId(string key, string label, string description, string lookup, bool required = false) =>
        OptionText(key, label, description) with { LookupCommandId = lookup, Required = required };
    private static CommandField StoryId(bool all = false) => Pos("advId", "剧情", all ? "选择剧情，或使用“全部”检查整个站点。" : "从剧情列表选择编号。", lookup: "story.list") with
        { Kind = all ? CommandFieldKind.Choice : CommandFieldKind.Text, AllowCustomValue = all, Choices = all ? [new("all", "全部剧情")] : [] };
    private static CommandField ModelId() => Pos("modelId", "角色模型", "从当前站点的模型列表选择。", lookup: "model.list");
    private static CommandField HomeId(bool all = false) => Pos("spotId", "主页", all ? "选择一个主页，或导出当前归档中的全部主页。" : "从主页列表选择场景编号。", lookup: "home.list") with
        { Kind = all ? CommandFieldKind.Choice : CommandFieldKind.Text, AllowCustomValue = all, Choices = all ? [new("all", "全部主页")] : [] };
    private static CommandField HomeSource() => OptionId("adv", "资源来源剧情", "可选；用主页详情中的来源剧情指定版本，全部主页导出不能使用此选项。", "home.show") with { Advanced = true };
    private static CommandField HomeCharacter(bool all = false) => OptionText("character", "角色", all ? "留空输出场景和所有角色；选择“全部角色”只导出角色；也可选择一个角色。" : "留空渲染完整场景；选择角色后自动输出透明画面。") with
        { Kind = CommandFieldKind.Choice, AllowCustomValue = true, LookupCommandId = "home.show", Choices = all ? [new("all", "全部角色（不含场景）")] : [] };
    private static CommandField Animation() => OptionText("animation", "动画名称", "留空保留原始动画；覆盖名称必须存在于相关骨架中。", true) with { LookupCommandId = "home.animations" };
    private static CommandField Loop() => Flag("loop", "强制循环", "启用后循环播放；关闭时保留各部件的原始循环设置。", true);
    private static CommandField ArchiveField() => new("archive", "素材归档", CommandFieldKind.InputDirectory, "留空使用全局归档设置或命令行自动发现。") { Option = "--archive", Advanced = true };
    private static CommandField SiteField(bool home = false) => Choice("site", "来源站点", home ? "留空时列表包括全部站点；按编号打开优先国际服。" : "留空使用全局站点；未配置时使用国际服。", home ? null : "intl", Sites, advanced: true);
    private static CommandField LanguageField() => Choice("lang", "语言", "留空保留项目或操作的默认语言；对白查询可搜索全部语言。", null, Languages, allowCustom: true, advanced: true);
    private static CommandField Input(string key, string label, string description, int position = 0, IReadOnlyList<string>? extensions = null) =>
        new(key, label, CommandFieldKind.InputFile, description) { Position = position, Required = true, FileExtensions = extensions ?? [] };
    private static CommandField InputOption(string key, string label, string description, bool required = false, IReadOnlyList<string>? extensions = null) =>
        new(key, label, CommandFieldKind.InputFile, description) { Option = "--" + key, Required = required, FileExtensions = extensions ?? [] };
    private static CommandField Script(bool required = false) => InputOption("script", "替代指令文件", required
        ? "帧序列需要统一项目或包含 commands 数组的 JSON 对象；项目来源必须与剧情匹配。"
        : "支持统一项目、包含 commands 的对象或原始指令数组；项目来源必须与剧情匹配。", required, [".json"]) with { Advanced = !required };
    private static CommandField Tool(string key, string label, string description) => InputOption(key, label, description) with { Advanced = true };
    private static CommandField[] NativeFields() =>
    [
        new("cubism-sdk", "Cubism SDK 目录", CommandFieldKind.InputDirectory, "可选；从 SDK 中查找原生模型运行库。显式 DLL 与 SDK 目录择一使用。") { Option = "--cubism-sdk", Advanced = true },
        Tool("dll", "原生模型运行库", "可选；直接指定对应平台的模型运行库。与 SDK 目录择一使用。")
    ];
    private static CommandField Output(string key, string label, string description, CommandFieldKind kind, bool required, string suggested, IReadOnlyList<string>? extensions = null) =>
        new(key, label, kind, description) { Option = "--" + key, Required = required, SuggestedFileName = suggested, FileExtensions = extensions ?? [] };
    private static CommandField PngOutput(string name) => Output("out", "输出图像", "保存 PNG 图像，保留可用透明通道。", CommandFieldKind.OutputFile, true, name, [".png"]);
    private static CommandField Overwrite() => Flag("overwrite", "替换已有文件", "显式允许替换目标文件；不用于覆盖输出目录。", true);
    private static CommandField[] OptionalJson(string suggested) =>
        [Output("out", "保存结果文件", "可选；留空在界面查看结果，选择文件则另存 JSON。", CommandFieldKind.OutputFile, false, suggested, [".json"]), Overwrite()];
    private static CommandField Flag(string key, string label, string description, bool advanced = false) =>
        new(key, label, CommandFieldKind.Boolean, description) { Option = "--" + key, DefaultValue = "false", Advanced = advanced };
    private static CommandField Integer(string key, string label, string description, string? value, double? minimum, double? maximum, bool advanced = false) =>
        new(key, label, CommandFieldKind.Integer, description) { Option = "--" + key, DefaultValue = value, Minimum = minimum, Maximum = maximum, Advanced = advanced };
    private static CommandField Number(string key, string label, string description, string? value, double? minimum, double? maximum, bool advanced = false) =>
        new(key, label, CommandFieldKind.Number, description) { Option = "--" + key, DefaultValue = value, Minimum = minimum, Maximum = maximum, Advanced = advanced };
    private static CommandField Choice(string key, string label, string description, string? value, IReadOnlyList<CommandChoice> choices, bool multi = false, bool allowCustom = false, bool advanced = false) =>
        new(key, label, multi ? CommandFieldKind.MultiChoice : CommandFieldKind.Choice, description) { Option = "--" + key, DefaultValue = value, Choices = choices, AllowCustomValue = allowCustom, Advanced = advanced };
    private static CommandField[] Page(int defaultLimit = 50, int maximum = 10000) =>
        [Integer("limit", "每页数量", "本次最多返回或处理的条目数。", defaultLimit.ToString(System.Globalization.CultureInfo.InvariantCulture), 1, maximum, true), Integer("offset", "跳过条目数", "从零开始的分页位置。", "0", 0, int.MaxValue, true)];
    private static CommandField Quality() => Choice("cineform-quality", "CineForm 质量", "默认 film1 平衡画质和体积，high 更重视体积；仅对 CineForm 生效。", "film1",
        ExportQuality.CineFormChoices.Select(choice => new CommandChoice(choice.Value, choice.Label)).ToArray(), advanced: true);
    private static CommandField[] Dimensions(bool contact, bool home, bool project = false) =>
    [
        Integer("width", "画面宽度", project ? "留空使用项目设置；没有设置时为操作默认尺寸。" : "最终输出宽度；CineForm 要求为 16 的倍数。", project ? null : contact ? "640" : "1920", 16, OurNotes.Director.Core.RenderDimensions.MaxSide),
        Integer("height", "画面高度", project ? "留空使用项目设置；没有设置时为操作默认尺寸。" : "最终输出高度；CineForm 要求为 8 的倍数且至少 32。", project ? null : contact ? "360" : "1080", 16, OurNotes.Director.Core.RenderDimensions.MaxSide),
        Integer("supersample", "超采样倍数", "更高倍数改善边缘，同时增加渲染时间和显存占用。", project ? null : "2", 1, 4, true)
    ];
    private static CommandField[] StoryRenderFields(string verb, bool project = false)
    {
        var fields = new List<CommandField>();
        if (verb == "validate") { fields.AddRange(OptionalJson("validation.json")); return fields.ToArray(); }
        if (verb == "state") return [Number("time", "查询时间（秒）", "查看这一原始时间点的剧情状态。", "0", 0, 36000)];
        fields.AddRange(Dimensions(verb == "contact", false, project));
        fields.Add(Flag("clean", "隐藏对白界面", "隐藏剧情对白界面；关闭时保留项目原本设置。", true));
        if (verb == "contact")
        {
            fields.Add(OptionText("times", "采样时间（秒）", "输入 1 至 64 个以逗号分隔的时间，范围为 0 至 36000 秒。") with { DefaultValue = "0,1,2" });
            fields.Add(Integer("columns", "图集列数", "图集的最大列数。", "3", 1, 16));
        }
        else fields.Add(Number("time", "开始时间（秒）", "按原始 30 帧时序向下取整到帧。", "0", 0, 36000));
        if (verb is "clip" or "sequence")
        {
            fields.Add(Number("duration", "时长上限（秒）", "普通输出默认 10 秒；启用播放到结束时默认上限 3600 秒。旧版 Shot 默认自身时长。", null, 0, 36000) with { MinimumExclusive = true });
            fields.Add(Flag("until-end", "播放到剧情结束", "提前结束时停止；到达时长上限仍未结束则报告失败。"));
        }
        if (verb == "clip")
        {
            fields.Add(Number("speed", "输出速度", "以恒定源帧率求值后改变输出速度；1 为原速。", "1", .25, 4, true));
            fields.Add(Choice("codec", "视频编码", project ? "留空保留项目编码；项目未设置时默认 H.264。" : "透明画面需使用 CineForm、ProRes 或 FFV1。", project ? null : "h264", Codecs));
            fields.Add(project ? Quality() with { DefaultValue = null } : Quality());
            fields.Add(Output("out", "输出视频", "CineForm / ProRes 使用 MOV，FFV1 使用 MKV，H.264 使用 MP4。", CommandFieldKind.OutputFile, true, "clip.mov", [".mov", ".mkv", ".mp4"]));
        }
        else if (verb == "sequence")
        {
            fields.Add(Output("out", "帧序列目录", "新目录从头渲染；已有任务只可使用续渲，必须保留 render.json。", CommandFieldKind.OutputDirectory, true, "frames"));
            fields.Add(Flag("resume", "续渲已有任务", "从头模拟状态，复用已验证的帧；项目、资源或 GPU 改变会拒绝续渲。", true));
        }
        else fields.Add(PngOutput(verb == "contact" ? "contact.png" : "frame.png"));
        if (verb != "sequence") fields.Add(Overwrite());
        return fields.ToArray();
    }
    private static CommandField[] HomeRenderFields(string verb)
    {
        var fields = new List<CommandField>(Dimensions(verb == "contact", true));
        fields.Add(new("project", "主页项目", CommandFieldKind.InputFile, "可选；应用主页工作台保存的位置、缩放、动作编排、调色和输出默认设置。")
        { Option = "--project", Advanced = true, FileExtensions = [".json"] });
        fields.Add(Integer("fps", "每秒帧数", "主页动画的固定模拟和输出帧率。", "30", 1, 120));
        fields.Add(Choice("framing", "构图方式", "留空时完整场景用原相机，独立角色用整段固定取景。", null,
            [new("scene", "原场景相机（便于多层对齐）"), new("fit", "整段固定取景（适合独立角色）")]));
        fields.Add(Flag("no-background", "透明整组动画", "移除房间和全局后处理，保留 Spine 自身的背景及道具；单角色始终透明。"));
        fields.Add(Number("time", "开始时间（秒）", "向下取整到输出帧；默认从真正的第 0 帧开始。", "0", 0, 36000));
        if (verb is "clip" or "sequence" or "export")
            fields.Add(Number("duration", "片段长度（秒）", "留空输出到自然结束，非循环动画包含结束姿态；批量导出共用完整场景时长。", null, 0, 36000) with { MinimumExclusive = true });
        if (verb == "contact")
        {
            fields.Add(OptionText("times", "采样时间（秒）", "输入 1 至 100 个以逗号分隔的时间，范围为 0 至 36000 秒。") with { DefaultValue = "0,1,2,3" });
            fields.Add(Integer("columns", "图集列数", "图集的最大列数。", "3", 1, 100));
        }
        if (verb is "clip" or "export")
        {
            fields.Add(Choice("codec", "视频编码", verb == "export" ? "批量导出包含透明角色，不支持 H.264。" : "透明画面需 CineForm、ProRes 或 FFV1；完整背景可使用 H.264。", "cineform",
                verb == "export" ? Codecs.Where(choice => choice.Value != "h264").ToArray() : Codecs));
            fields.Add(Quality());
        }
        if (verb is "export" or "sequence")
            fields.Add(Output("out", verb == "export" ? "新导出目录" : "新帧序列目录", "目录必须不存在；任务成功后整体发布，不覆盖旧目录。", CommandFieldKind.NewDirectory, true, verb == "export" ? "home-export" : "home-frames"));
        else if (verb == "clip")
            fields.Add(Output("out", "输出视频", "默认 CineForm MOV；选择其他编码时使用对应扩展名。", CommandFieldKind.OutputFile, true, "home.mov", [".mov", ".mkv", ".mp4"]));
        else fields.Add(PngOutput(verb == "contact" ? "home-contact.png" : "home.png"));
        if (verb is not ("export" or "sequence")) fields.Add(Overwrite());
        return fields.ToArray();
    }
    private static CommandOutputKind RenderOutput(string verb) => verb switch
    {
        "frame" or "contact" => CommandOutputKind.Image, "clip" => CommandOutputKind.Video,
        "sequence" => CommandOutputKind.Directory, _ => CommandOutputKind.Json
    };
    private static string RenderLabel(string verb, string subject) => verb switch
    {
        "frame" => subject + "抽帧", "contact" => subject + "时间图集", "clip" => "导出" + subject + "视频",
        "sequence" => "导出" + subject + "帧序列", "state" => subject + "状态检查", _ => subject + "校验"
    };
}
