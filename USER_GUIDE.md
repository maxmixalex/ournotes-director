# OurNotes Director 使用指南

本工具用于下载和更新 Our Notes 素材归档，并制作 MAD 镜头：检索素材、回放原剧情、用游戏指令重新编排演出并导出视频。Windows 入口是 `advkit.exe`，Linux 入口是 `advkit`；两者来自同一套 C# 源码，发布程序不需要安装 .NET。

实现边界是 **ournotes-player 的普通剧情演出及 MAD 导出**，不是通用 Unity 引擎。运行时为 C# + 原生 DLL + 离屏 OpenGL；不启动浏览器，不执行 JavaScript，不使用 Python。

目前可用，但尚未证明所有原剧情、所有参数组合都与网页画面一致。下文的“支持”表示已接入功能，不表示完整复刻了对应 Unity 组件。

## 统一制作入口

新项目统一使用 `render <操作> project.json`。原游戏指令与自由摆放、模型参数、透明输出在同一个原生剧情执行器里运行。`story` 保留为直接打开原剧情 ID 的兼容入口。

```powershell
./advkit.exe render validate samples/unified/millsage-demo.json
./advkit.exe render frame samples/unified/millsage-demo.json --time 4 -o output/preview.png
./advkit.exe render clip samples/unified/millsage-demo.json --until-end --duration 60 -o output/mad.mp4
./advkit.exe render clip samples/unified/free-character.json --until-end --duration 10 -o output/character.mov
./advkit.exe render sequence samples/unified/free-character.json --duration 3 -o output/frames
./advkit.exe render sequence samples/unified/free-character.json --duration 3 -o output/frames --resume
```

格式与四组扩展字段见 [统一接口](UNIFIED_INTERFACE.md)。可直接复制 [原剧情](samples/unified/original.json)、[透明角色](samples/unified/free-character.json)、[混合镜头](samples/unified/mixed.json) 或 [39 秒样片](samples/unified/millsage-demo.json) 修改。

`Director.Actor` 控制自由位置、旋转、大小、透明度和外观；`Director.Camera` 控制游戏镜头或独立二维构图；`Director.Parameter` 覆盖指定模型参数，`release` 交还动作／表情；`Director.Scene` 控制背景和调色。动作、表情、口型、舞台等继续使用游戏原语。六角色同时显示已验证，不受五槽位限制。

帧序列输出 PNG 和 `render.json`；续渲校验输入、资源及已有帧散列，损坏帧会重写。续渲仍从头模拟并绘制，仅节省已有 PNG 的编码和写盘，不是模拟状态快照。帧序列固定 30 fps，不包含声音；有声音的交付使用 `clip`。

透明项目设置 `Director.Scene.Values.background=[0,0,0,0]`、`stageVisible=false` 及 `output.letterbox="none"`，导出 ProRes 4444 或 FFV1。为保留 alpha，透明场景跳过原游戏场景／最终后处理；导演调色仍执行。MP4 不接受透明场景。

独立角色目前按 `order` 绘制在游戏场景之后；world 坐标能使用游戏相机，但不提供与舞台网格交错遮挡的完整深度排序。暂停角色动画时，参数覆盖也暂停求值；需要参数动画时保持角色更新。

## 1. 从哪里运行

发布包默认从 GitHub Actions artifacts 下载。若 artifact 存储额度不足，`main` 或 `v*` 标签构建会将各平台测试通过的包分别保存为独立草稿 Release，链接见对应 job 的 Summary；草稿仅对具有仓库写权限的人可见。

以下命令均在解压后的发布目录执行，示例采用 Windows 文件名；Linux 把 `./advkit.exe` 换成 `./advkit`。开发构建的程序位于 `artifacts/win-x64` 或 `artifacts/linux-x64`：

```powershell
./advkit.exe help
./advkit.exe archive
./advkit.exe probe archive
```

Native AOT 发布支持 Windows x64 和 Linux x64，归档、检索和素材解包不需要 .NET / Node.js / Python。Linux 包以 Ubuntu 24.04 为基线，需要兼容或更新的 glibc 系统。渲染需要 Cubism Native Core 与 OpenGL；Linux 通过 EGL 创建无窗口上下文，可以使用 Mesa 软件渲染。视频及音频解码需要 FFmpeg，媒体检查需要 FFprobe。游戏素材和 SDK 单独保存，发布包不包含这些资源。

默认向上查找已有 `archive`，也兼容旧 `legacy/millsage-story-archive/archive`。查询站点默认 `intl`、剧情渲染语言默认 `zh-Hans`。也可显式指定 `--archive PATH --site intl --lang zh-Hans`。跨目录运行时，输入脚本和输出路径仍相对于当前工作目录。

### 下载与增量更新

只执行一个命令：`./advkit.exe archive`。没有归档时下载到当前目录的 `archive/`；已有归档时重新确认上游版本，校验后复用已有文件，补齐新增或损坏内容，更新检索索引和 Director Pack。中断后重复同一命令即可，单个未完成文件会重下。旧 Node 归档可以直接指定路径复用，不需要转换，也不需要继续保留 Node 项目。

默认下载两个站点的 millsage 关联及模型客串剧情、全部语言和公共模型资源。`--out PATH` 指定目录；`--languages zh-Hans,ja` 选择语言；`--sites intl` 只更新一个站点；`--no-deep` 关闭额外客串扫描。后续每次沿用所需的相同选项；省略语言／站点选项会回到全部语言／两个站点。筛选限定本次下载、索引和 Director Pack 的范围，不主动删除旧素材。

```powershell
./advkit.exe archive --out D:/OurNotes/archive
# 今后更新仍使用这条命令
./advkit.exe archive --out D:/OurNotes/archive
```

下载阶段在 stderr 显示进度，结束在 stdout 输出 JSON；Ctrl+C 取消返回 130。资源目录、manifest 和素材经过校验后才提交；失败的临时索引不会替换上一份完整索引。

## 2. 导出原剧情

先找剧情，再抽帧确认内容：

```powershell
./advkit.exe story list --limit 20
./advkit.exe story show 10060
./advkit.exe story frame 10060 --time 10 --width 640 --height 360 -o output/story-10060-10s.png
./advkit.exe story contact 10060 --times 5,10,15,20 --columns 2 --width 640 --height 360 -o output/story-10060-contact.png
```

导出从第 10 秒开始的 6 秒片段：

```powershell
./advkit.exe story clip 10060 --time 10 --duration 6 --width 640 --height 360 --supersample 2 -o output/story-10060-cut.mp4
```

导出到剧情结束：

```powershell
./advkit.exe story clip 10060 --until-end --duration 1800 --width 1920 --height 1080 --supersample 2 -o output/story-10060-full.mp4
```

`--until-end` 下的 `--duration` 是从起点算起的安全上限；超过上限仍未结束会报错，不发布截断的成片。省略上限时为 3600 秒。没有 `--until-end` 时按指定长度输出，默认 10 秒；`finished` 字段说明导出终点是否已到剧情结束。

任意时间起点都需要从头模拟剧情状态，因此跳到很晚的时间也有准备开销。`story clip` 没有断点续渲功能。

### 常用输出参数

| 参数 | 作用 |
|---|---|
| `--time 10` | 从原剧情第 10 秒开始，默认 0 |
| `--duration 6` | 原剧情时间长度；使用 `--until-end` 时改为上限 |
| `--width 1920 --height 1080` | 输出尺寸；frame/clip 默认 1920×1080 |
| `--supersample 2` | 默认 2 倍超采样抗锯齿，允许 1–4；数值越高越慢、越占显存 |
| `--clean` | 隐藏常规对话框、地点文字、字幕等 UI；不会清除所有剧情视觉层，手机、边框、插画等仍可能存在 |
| `--codec h264` | 默认；MP4 预览，有损、无透明通道 |
| `--codec prores` | MOV / ProRes 4444，适合后期中间文件 |
| `--codec ffv1` | MKV / FFV1，无损，文件较大 |
| `--speed 0.5` | 统一变速，范围 0.25–4，音频保持音高；时间参数仍按原剧情时间填写 |
| `--overwrite` | 允许覆盖已有输出；默认拒绝覆盖 |

模拟固定 30 Hz。变速保留源帧，输出帧率为 `30 × speed`，例如半速为 15 fps；这不是补帧。剧情按原画布比例合成，16:9 输出可能保留上下留边。

ProRes 不会自动去掉原剧情背景；透明镜头参照上面的统一项目。

先用 640×360、2 倍超采样检查编排，再导出正式分辨率。CLI 的 JSON 会返回 GPU 名称、初始化时间和导出耗时；当前渲染速度依赖显卡和剧情复杂度，不保证实时。

## 3. 用游戏原语编写剧本

使用 `story ... --script FILE`。脚本是游戏指令的 JSON 列表，没有另造脚本语言。剧情 ID 仍然必须提供，它决定使用哪套场景、字体、角色、语音和特效资源。

先运行已有示例：

```powershell
./advkit.exe story frame 10060 --script samples/game-primitives.json --time 4 --width 640 --height 360 -o output/custom-frame.png
./advkit.exe story clip 10060 --script samples/game-primitives.json --until-end --duration 60 --width 640 --height 360 -o output/custom.mp4
```

复制 [game-primitives.json](samples/game-primitives.json) 修改即可。文件结构如下：

```json
{
  "initialize": [],
  "finalize": [],
  "commands": [
    {"cmd":"Character","TargetName":"hotaru","TargetAssetName":"016_adv/adv_live2d_hotaru_016_casual_spring_01/model/adv_live2d_hotaru_016_casual_spring_01"},
    {"cmd":"Stage","TargetAssetName":"adv_bkg_stage_000248/adv_bkg_stage_000248"},
    {"cmd":"In","TargetName":"hotaru","PositionType":5},
    {"cmd":"Focus","PositionType":5,"CameraDistance":0},
    {"cmd":"Delay","Duration":1},
    {"cmd":"Zoom","Parameter1":"1.2","Duration":2},
    {"cmd":"Delay","Duration":2}
  ]
}
```

- `commands` 替换原剧情正文；不会把自定义指令追加到原正文后面。
- `initialize`、`finalize` 替换原场景的初始化和收尾。明确写 `[]` 表示清空；省略则继承原场景。
- `TargetName` 是角色绑定名；`TargetAssetName` 是该指令使用的原始资源名，不能随意改成文件名或角色显示名。
- `PositionType` 常用角色槽位为 1、3、5、7、9，5 是中间。`CameraDistance` 使用原场景的 Focus 预设；它不是米数。
- `Duration` 是指令动作时长，`Delay` 用于停留；需要并行动作时，使用原字段 `IsNoWait: 1`，然后用等待指令控制后续时间。
- `Parameter1/2/3` 的含义因指令而异。不要把一种指令的参数直接套到另一种上；优先复制原剧情行。

查看原始指令与执行状态：

```powershell
./advkit.exe story commands 10060 --offset 0 --limit 30
./advkit.exe story native-program 10060 -o output/original-program.json
./advkit.exe story native-check 10060 --script samples/game-primitives.json
./advkit.exe story state 10060 --script samples/game-primitives.json --time 4
```

`native-program` 导出原剧情的初始化、正文、收尾，不应用 `--script`。`native-check` 会加载资源并渲染第 0 帧，不能代替全片验证。`state` 返回指定时刻的角色、相机、对话及音频状态。

### 台词与口型

`Talk` 的 `AdvTextID` 指向文本条目，`TargetTextIDs` 是说话人名称条目。自定义文本通过顶层 `text` 合并到原剧情文本表，例如：

```json
"text": {
  "mad_line_01": {"simplifiedChinese":"这里填写台词"}
}
```

对应指令使用 `"AdvTextID":"mad_line_01"`。字体必须包含这些字形；不能保证任意新文字都可显示。Talk/Subtitles 已做文本预检，缺字会给出指令行及字体信息。更完整例子见 [game-rich-text.json](samples/game-rich-text.json)。

表情名不是情绪描述：例如 hotaru 的 `exp_smile07` 会设置 `ParamEyeaHL=1`（隐藏瞳孔高光），普通微笑可用 `exp_smile01`。需要保持表情但改变某个细节，可用 Director.Parameter 覆盖模型已有参数。

有原声的 Talk/Voice 已接入口型：使用模型 MotionSync 或游戏 CRI Lips 数据。仅新增文本不会合成配音，也不会自动生成语音口型；`story state` 的 `lipSyncMissing` 可用于检查缺失分析数据。

| 示例 | 搭配剧情 | 内容 |
|---|---|---|
| [game-primitives.json](samples/game-primitives.json) | 10060 | 角色、舞台、相机移动与推近 |
| [game-lipsync.json](samples/game-lipsync.json) | 10060 | 原声与口型 |
| [game-rich-text.json](samples/game-rich-text.json) | 10060 | 粗体、颜色、字号 |
| [game-ruby.json](samples/game-ruby.json) | 10077 | 注音文字 |
| [game-still-shake.json](samples/game-still-shake.json) | 10079 | 插画与抖动 |
| [game-chat-lock.json](samples/game-chat-lock.json) | 10061 | 剧情中的手机画面 |

`game-effect-character.json` 等部分特效样例使用单独的 `references/effect-samples` 归档。例如：

```powershell
./advkit.exe story clip 10020 --archive references/effect-samples --script samples/game-effect-character.json --duration 6 --width 640 --height 360 -o output/effect.mp4
```

## 4. 支持范围与限制

以下以统一原生剧情执行器为准，按现有实现整理；处理器存在不代表每种素材和参数变体都已验证。

| 功能 | 当前状态 |
|---|---|
| 原剧情、自定义指令 JSON、按时间抽帧／图集／视频 | 支持；整集仍需实际导出验证 |
| 角色装载、换装、进出场、动作、表情、循环、暂停、眨眼、视线、位置 | 已接入；依赖所选剧情内资源 |
| 原舞台、Focus、Zoom、Pan/PanV2、Track/Pedestal、Shake/CameraShake | 已接入；按原场景预设和指令语义工作 |
| BGM、SE、Voice、音量、48 kHz 混音、语音口型 | 已接入；缺资源或缺口型分析数据仍需处理 |
| 对话、字幕、地点、淡入淡出、闪光、富文本 | 已接入；受归档字体和已实现文字标签限制 |
| PostEffect、DoF、StageEnv、RimLight | 已接入原始材质及相应渲染路径；不是全部 Unity 后处理功能 |
| Effect 粒子、SpriteRenderer、网格与动画 | 已接入多个原始特效路径；不是通用 ParticleSystem／Animator 实现 |
| Still、Frame、Clip/Movie、Alpha | 已接入插画、边框、视频和对应动画／透明度；特殊组件与参数仍可能报不支持 |
| 剧情手机对话、贴图、输入、已读、来电、锁屏 | 已接入普通剧情需要的画面；不以交互式手机应用为目标 |
| 画布渐变、平铺、软遮罩 | 已有原生实现和部分组合验证；自定义图像材质目前仅接入两种 Mobile/Particles 材质，不能视为任意材质支持 |
| 分支与跳转 | `GoTo` 可跳转；`ChoiceSet` 当前为空处理器，`ChoiceShow` 未接入，不能自动遍历／选择全部分支 |
| `Tilt`、`Role` | 已接入原游戏相机旋转 |
| `Timeline` | 未接入；有效指令会拒绝 |
| SimpleADV、主页／结算对话宿主 | 明确不在当前目标内 |
| 任意 Unity 场景、脚本、材质、Shader Graph、组件导入 | 不支持，也不是建设目标 |
| 整曲节奏剪辑、完整 NLE 时间线、任意音频自动嘴型 | 没有一站式实现；镜头素材可导出给后期软件 |

部分原剧情已完成整段导出，部分功能已与上游状态或参考帧对照；尚未完成所有归档剧情的全时段逐帧对比。`native-check` 成功、命令名称能识别、素材能列出，都不能单独证明对应演出完全一致。

最近添加的某些组合支持（例如渐变软遮罩）是原生端扩展验证，不等于上游已经使用了这个组合。后续工作优先补齐上游普通剧情实际使用的能力。

## 5. 素材检索与独立角色镜头

```powershell
./advkit.exe dialogue search 幸福 --lang zh-Hans --speaker hotaru --with-voice --limit 10
./advkit.exe model list --character hotaru
./advkit.exe motion list adv_live2d_hotaru_016_casual_spring_01 --limit 100
./advkit.exe expression list adv_live2d_hotaru_016_casual_spring_01
./advkit.exe expression show adv_live2d_hotaru_016_casual_spring_01 exp_smile07
./advkit.exe stage list --limit 20
./advkit.exe effect list --limit 20
./advkit.exe asset search .webm --kind video
```

`stage/postfx/still/frame/effect/media list|show` 是素材目录，条目存在不等于任意剧情都能引用。跨剧情拼素材目前不能仅靠换一个 ID 自动补齐依赖。

旧 Shot 文件仍可输入 `render`，内部转换为统一游戏指令与 Director 扩展，不再分流到旧渲染器。适配器以归档剧情 10060 为资源基础；动画和材质已采用统一实现，不保证与旧版逐像素相同：

```powershell
./advkit.exe shot init --template portrait -o output/shot.json
./advkit.exe shot validate output/shot.json
./advkit.exe render frame output/shot.json --time 1 -o output/character.png
./advkit.exe render clip output/shot.json --codec prores -o output/character.mov
```

Shot 参数详见 [README 的 Shot JSON 部分](README.md#shot-json)。新编排优先使用统一项目，不必学习旧 Shot 数值轨道格式。

`story export-dialogue` 可以输出 WAV、SRT 和原声包，但它按台词重新排时，不包含原剧情全部等待、转场和音效；不能用它代替完整剧情回放。

## 6. 报错、输出和清理

- 命令输出为 JSON，进度在 stderr。列表通常默认 50 项，用 `--limit/--offset` 翻页。
- 退出码：`0` 成功、`2` 输入错误、`3` 文件／数据／依赖错误、`4` 尚不支持、`130` 归档取消。必要时加 `--debug` 查看调用栈。
- 缺模型、字体、语音或材质时，先确认剧情 ID、`--archive`、语言与脚本中的资源名是否匹配。
- `Story did not finish within --duration`：增加上限，或用 `story state` 检查等待／跳转；不是把未完成片段当整集输出。
- 视频导出会临时写 `.adv-*.video/.f32/.mux`，正常退出及捕获到的失败会清理；解码缓存使用程序目录下的 `.ournotes-temp`。强制杀进程后可能残留，确认没有导出进程在运行再清理对应任务文件。
- 用户生成的 PNG、MP4、MOV、MKV、台词包需要自行保留或删除。不要把 `archive`、`references`、`samples`、SDK、`bin` 当测试缓存删除。

完整命令列表随 EXE 提供：`./advkit.exe help`。本指南不把内部检查数量当作功能完成率。
