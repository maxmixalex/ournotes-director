# OurNotes Director / advkit

**用户入口：[CLI 使用指南](USER_GUIDE.md)**。统一 `render` 接受原游戏指令及自由编排扩展，旧 Shot 自动适配到同一原生执行器。[主页 Spine 导出](HOME_SPINE.md)使用 `home`，支持完整场景及每个角色的动画；[CineForm 透明 MOV](CINEFORM.md)说明质量参数和 Premiere Pro 2015 的支持依据。本文保留较详细的素材与 Shot 格式说明。

面向素材归档、检索和 MAD 镜头渲染的原生 CLI。Windows 和 Linux 共用 `src/` 下的同一套 C# 源码，由 `win-x64` / `linux-x64` 编译目标决定发布平台。正式发布使用 **.NET 10 Native AOT**，解压后直接运行 `advkit.exe` / `advkit`，用户机器不需要安装 .NET、Node.js 或 Python。

已能生成真实模型的透明角色、动作/表情动画、多角色构图、舞台背景镜头，以及主页 Spine 的完整动画和独立角色动画。输出包括 PNG 帧序列、CineForm RGB + alpha MOV、ProRes 4444、FFV1 和 H.264。`archive` 联网更新素材，检索和渲染使用本地归档。剧情固定 30 Hz 逐帧模拟；主页默认 30 fps，可显式指定 1–120 fps，渲染速度不改变片长。

## 下载和运行

GitHub Actions 的 [**Build native executables** 工作流](https://github.com/maxmixalex/ournotes-director/actions/workflows/build.yml)在 GitHub 托管的 `ubuntu-24.04` 与 `windows-2022` 上分别构建、测试和发布。完成后下载 `ournotes-director-linux-x64` / `ournotes-director-win-x64` artifact，解开其中的平台压缩包；包内包含原生程序、原生运行库、音视频工具、使用指南、许可证和示例剧本。请保留整个解压目录，不能只复制 `advkit` 可执行文件。

默认交付位置是 Actions artifacts。若 artifact 存储额度不足，`main` 或 `v*` 标签构建会把各平台测试通过的包分别保存为独立的**草稿 Release**，下载链接见对应 job 的 Summary。草稿只有具有仓库写权限的人能查看；其他情况仍使用 artifacts。

```powershell
# Windows：在解压后的目录中运行
./advkit.exe archive
./advkit.exe story list
```

```sh
# Linux：命令完全相同，只是没有 .exe 后缀
./advkit archive
./advkit story list
```

CI 发布包自带以下运行依赖，默认按可执行文件所在目录加载，从其他工作目录启动也不需要配置 SDK 路径或 FFmpeg 的 `PATH`：

| 组件 | Windows x64 | Linux x64 |
|---|---|---|
| Cubism Core | `native/Live2DCubismCore.dll` | `native/libLive2DCubismCore.so` |
| 口型分析 | `native/Live2DCubismMotionSyncEngine_CRI.dll`，以及编译进程序的 C# CRI Lips | 编译进程序的 C# CRI Lips |
| 音视频工具 | `tools/ffmpeg.exe`、`tools/ffprobe.exe` | `tools/ffmpeg`、`tools/ffprobe` |

官方 MotionSync 没有 Linux 桌面运行库；Linux 使用已有 C# CRI Lips 与归档中的模型权重，不需要额外口型 DLL，但不保证与 Windows MotionSync 产生相同的音素嘴型。游戏素材仍由 `archive` 下载。图形驱动／OpenGL 是操作系统依赖：Linux 使用 EGL surfaceless OpenGL，可配 Mesa；Ubuntu 可安装 `libegl1 libgl1 libgl1-mesa-dri`。Linux 发布以 Ubuntu 24.04 为构建基线，需要兼容或更新的 glibc 系统，不适用于 Alpine/musl。

`./advkit.exe probe runtime`（Linux 使用 `./advkit`）不需要素材归档，实际加载 Core、检查口型引擎并调用包内 FFmpeg/FFprobe，返回路径和版本。高级用法仍可通过 `--dll`、`--cubism-sdk` / `CUBISM_NATIVE_SDK`、`CUBISM_MOTIONSYNC_DLL`、`--ffmpeg`、`--ffprobe` 显式选择其他安装。第三方运行库沿用各自许可证，见 `THIRD-PARTY.md` 和包内 `licenses/`。

## 一个命令归档和增量更新

```powershell
./advkit.exe archive
```

它会读取当前 intl / JP MasterData，动态识别所选乐队的成员和关联剧情，默认继续扫描 story/model manifest 以发现客串；随后下载所有语言、公共文件和模型依赖，校验素材，重建检索索引，并为有普通剧情的站点生成 Director Pack。不需要运行旧 Node 项目或拆成多个命令。未指定乐队时继续使用 millsage。

**更新或中断后续跑，仍执行同一条 `archive`。** 已有素材按解码后 SHA-256、decoded size 和 stored size 校验，通过后复用；缺失或损坏的文件会补齐。共享存储与站点素材优先使用 hardlink，不支持时自动复制。可变的目录、MasterData 和 manifest 会按 ETag / Last-Modified 重新确认；变化后只补下载所需的新内容。下载写入临时文件，校验成功后才替换；索引和 Director Pack 也先在临时目录构建，失败保留上一份完整结果。同一归档有进程锁，防止并发更新相互覆盖。

默认查找已有 `archive`，也兼容旧 `legacy/millsage-story-archive/archive`；没有现成归档时创建当前目录的 `archive/`。`--out` 或 `OURNOTES_ARCHIVE` 可以固定位置。只保留一种调用方式，需要时加选项即可：

```powershell
./advkit.exe archive --out D:/OurNotes/archive
./advkit.exe archive --out D:/OurNotes/archive --languages zh-Hans,ja
./advkit.exe archive --out D:/OurNotes/archive --sites intl --concurrency 8
# 选择其他乐队；名称 token、完整 text ID 和数值 ID 均可
./advkit.exe archive --band mygo --out D:/OurNotes/mygo --languages zh-Hans,ja
./advkit.exe archive --band mujica --out D:/OurNotes/mujica
./advkit.exe archive --band Band_Name_yumemita --out D:/OurNotes/yumemita
./advkit.exe archive --band 5 --out D:/OurNotes/kadan
```

`--band` 每次选择一个乐队，按上游 MasterBand 解析，不写死成员或乐队 ID。token 忽略大小写及常见标点，例如 `MyGO!!!!!` 与 `mygo` 等价；上游提供显示名时也接受该显示名。未知或含糊的名称会报错并列出可用 ID/token。换乐队后，剧情、角色、客串扫描、资源索引和 Director Pack 都按本次选择生成。已有素材保留并可增量复用；若需要同时保留多套可查询的乐队索引，请使用不同的 `--out` 目录。再次更新需沿用相同的 `--band`；省略它会回到 millsage。

默认所有语言、两个站点、并发 8、开启客串深扫；`--no-deep` 可明确限定到 MasterData / 目录关联范围。已选剧情和模型清单也按该并发数预取，验证后按顺序汇总；即使素材完全复用，更新仍需等待上游确认清单版本。`--retries` 控制单次请求的最大尝试次数（默认 4）。筛选不会删除已经保存的旧素材；索引依据本次选择生成。归档器按完整文件复用，尚未下载完的单个文件会重新下载，不是 HTTP 字节范围续传。

原始 manifest 保留上游字节，gzip / Brotli / split JSON 仍可由已有 C# 解包逻辑读取，包括 `1e999` 这样的合法 JSON 数值。主要输出保持兼容：

| 路径 | 内容 |
|---|---|
| `discovery.json` | 本次成员、剧情来源、站点和筛选范围 |
| `store/` | 跨剧情、跨站点共享的哈希素材 |
| `sites/intl`、`sites/jp` | 筛选后的剧情目录、原始 story/model manifest、素材和 Director Pack |
| `index/catalog.json`、`models.json`、`stats.json` | 剧情、模型和统计目录 |
| `index/*.jsonl` | commands、texts、dialogue、voices、sounds、files 检索索引 |

高级镜像／测试可使用 `OURNOTES_METADATA_BASE`、`OURNOTES_STORY_SITE_INTL`、`OURNOTES_STORY_SITE_JP` 覆盖源地址，普通使用不需要设置。所有进度写 stderr，stdout 始终返回一个 JSON 结果；Ctrl+C 返回 130，下次执行同一命令恢复。进度包含当前阶段、完成数／总数和耗时；大型文件或请求重试期间也会定期显示状态。再次运行时，会在取得归档锁后清理 `store/` 中符合下载器命名规则的未完成临时文件。

Director Pack 保留各剧情的聊天窗口、图标、贴图、文本和音效引用，并合并所选语言的字体子集。同名但内容不同的字体图片会分别保留，字形继续使用原始图片坐标。只下载日语等单一语言后，剧情与媒体渲染也使用该语言的 UI 资源。

`story list`、`model list`、`stage list` 等命令读取所指定归档的当前索引，无需重复传 `--band`。`shot init` 从该归档选择可用模型与普通剧情作为场景来源，`--template duet` 选择两个不同的可用模型；也可用 `--model` 指定主角。生成的场景记录来源剧情、站点和语言，可继续交给统一 `render`。已有 millsage 示例仍保持原来的角色与剧情 ID，使用其他乐队时请生成新模板或替换示例中的资源引用。

## 使用流程

以下示例从解压后的发布目录运行，采用 Windows 文件名；Linux 将 `./advkit.exe` 换成 `./advkit`。开发构建的程序在 `artifacts/<RID>/`：

```powershell
./advkit.exe help
./advkit.exe dialogue search 幸福 --speaker hotaru --lang zh-Hans --with-voice --limit 3
./advkit.exe voice extract 3101040101002 -o output/hotaru.m4a
./advkit.exe model list --character hotaru
./advkit.exe motion list adv_live2d_hotaru_016_casual_spring_01 --limit 100
./advkit.exe expression list adv_live2d_hotaru_016_casual_spring_01
./advkit.exe model parameters adv_live2d_hotaru_016_casual_spring_01
./advkit.exe model gallery adv_live2d_hotaru_016_casual_spring_01 --kind expression --limit 12 --time 1 -o output/expressions.png
./advkit.exe model gallery adv_live2d_hotaru_016_casual_spring_01 --kind motion --limit 12 --time 1 -o output/motions.png
./advkit.exe asset gallery adv_bkg_stage --limit 12 -o output/backgrounds.png
./advkit.exe shot init --template portrait -o output/shot.json
./advkit.exe shot validate output/shot.json
./advkit.exe render frame output/shot.json --time 1 -o output/frame.png
./advkit.exe render contact output/shot.json -o output/contact.png
./advkit.exe render sequence output/shot.json -o output/frames
./advkit.exe render sequence output/shot.json -o output/frames --resume
./advkit.exe render clip output/shot.json --codec prores -o output/shot.mov
```

gallery 为 4 列图集，旁边 `.json` 按 row/column 给出表演或素材 ID；`--offset` 翻页。motion 图集是指定秒数的姿态；观察运动用 contact/clip。contact 是最多 9 格、从首帧到末帧等间隔抽样，帧号在 sidecar 的 frameHashes 中。

可直接渲染 `samples/portrait.json`（10 秒近景、头部角度、镜头推近、表情转换）与 `samples/duet.json`（双角色、原始舞台背景、调色）。

## 主页 Spine 完整动画与角色导出

```powershell
./advkit.exe home list
./advkit.exe home show 40005
./advkit.exe home animations 40005
# ID 40005 是 Hotaru 主页示例，按 home list 选择已归档的 spot
./advkit.exe home export 40005 -o output/home-40005
./advkit.exe home export all -o output/all-homes
# 去掉房间，输出透明整组 Spine 和每个独立角色
./advkit.exe home export 40005 --no-background -o output/home-40005-alpha
```

批量默认生成 `scene.mov`、`characters/<key>.mov` 和 `export.json`，格式为 CineForm、film1 质量、1920×1080、30 fps、2 倍超采样。省略时长时从原动画第 0 帧输出到自然结束，非循环动画包含末帧；同一批次内场景和角色共用最长时长，便于对齐。`--character all` 只导出角色，`--character <key>` 只导出所选角色。

角色默认按整个时间段固定取景并输出透明画面；需要原位置图层时用 `--framing scene`。`--no-background` 移除 room 和全局 volume，仍保留 Spine 自身的背景及道具。单独 RGBA 层在不同背景上的 add／multiply／screen 合成不保证与原场景逐像素一致。主页资源直接来自已有 story manifest，不需要迁移归档；该路径不依赖 Cubism SDK。完整命令、动画和角色拆分边界见 [HOME_SPINE.md](HOME_SPINE.md)。

## 剧情检查与原声剪辑包

`story frame|clip` 直接执行游戏指令，支持舞台、透视相机、角色动作、对话、48 kHz 混音、语音口型、原始材质与后处理。剧情 10060 已完成整段视频导出；含手机聊天的 10061、10066、10078 已完成整段时间线运行。ChatWindow、ChatTalk、ChatStamp、ChatRead、ChatTyping 已接入原生手机画布，含来电、锁屏、emoji 与历史记录；输入动画和非等待模式切换已对照网页帧状态校验。主页场景及 Spine 动画已由 `home` 接入，SimpleADV 的主页点击对话、结算对话及其 UI 指令尚未支持；正常剧情的完整画面一致性尚未达成。使用 `story native-check` 检查资源初始化，`story clip --until-end` 验证整段播放；初始化成功不代表全部演出已验证。

手机编排示例见 `samples/game-chat*.json`，使用 `story frame 10061 --script samples/game-chat-lock.json --time 11 -o output/chat.png`。`story state` 返回手机模式、消息数量、滚动位置、已读状态和输入框状态。示例只使用游戏指令原语；所需头像、贴图和字体必须存在于选定剧情的归档资源中。

```powershell
./advkit.exe story clip 10060 --script samples/game-lipsync.json --duration 14 --width 640 --height 360 --supersample 2 -o output/lipsync.mp4
./advkit.exe story contact 10060 --script samples/game-lipsync.json --times 0.4,0.8,1.2,1.6,2,2.4 --columns 3 --clean -o output/contact.png
```

`story contact` 在一个原生会话中加载模型并采样指定时间，JSON 返回各格对应的帧号与位置；时间可乱序或重复。`story clip` 返回总耗时、帧率与各渲染阶段耗时，使用三缓冲 GPU 像素回读，按原帧序导出并排空尾帧。`--supersample 2` 默认保留抗锯齿，GPU 使用整数面积平均缩图。编排预览可先用 `--width 640 --height 360`，正式输出再提高分辨率。`--debug` 在错误时向 stderr 输出调用栈。

```powershell
./advkit.exe story native-program 10060 -o output/reports/native-program-10060.json
./advkit.exe story native-check all -o output/reports/native-engine.json
./advkit.exe probe sound 3101040101001 --adv 10060
```

`story clip --speed 0.5|1|2` 按导出时间统一变速（范围 0.25–4），画面、字幕、口型与混音同步，音频保持音高。保留全部源帧，因此输出帧率为 `30 × speed`；`--time`、`--duration` 仍按原剧情时间计。

正文支持原剧情的 `<size=150%>` 与 `<ruby=读音>正文</ruby>`，注音计入打字进度和自动等待时间。`story frame 10077 --script samples/game-ruby.json --time 4 -o output/ruby.png` 可查看实际剧情注音；字号和注音已用五种语言的原引擎字形位置校验。自定义文本仍须使用所选剧情字体中已有的字形。

自定义台词也支持 `<b>重点词</b>`、`<color=#ff80c0>彩色文字</color>`、八位 RGBA 十六进制颜色，以及原引擎的命名颜色。支持嵌套后恢复先前样式；示例 `samples/game-rich-text.json` 使用剧情 10060 的现有字形，可直接用于视频导出。

`Shake` 的 `CanvasLayers: [7]` 可单独摇动插画，省略层列表时也包含插画；使用原相机的世界坐标强度和透视投影。`samples/game-still-shake.json` 配合剧情 10079 可预览，抖动结束后恢复原位置。

`native-program` 合并解析原始 scene 设置中的初始化/收尾与 episode 正文，保留行号和原始参数；它导出程序，不执行渲染。`native-check` 报告已实现处理器和缺失项；未注册的命令会在原生执行前报错，绝不当作空操作。`probe sound` 验证原声哈希并解码实际 PCM，返回解码后的样本数（AAC 解码尾部填充可能使它不同于台词索引的原始采样数）。所有这些命令均不依赖浏览器、WebView、JavaScript 引擎或 Python。

```powershell
./advkit.exe story commands 10060 --offset 9 --limit 1
./advkit.exe story audit all -o output/reports/story-audit.json --overwrite
./advkit.exe story dialogue-plan 10060 --lang zh-Hans --offset 0 --limit 2
./advkit.exe story export-dialogue 10060 --lang zh-Hans --offset 0 --limit 2 --gap 0.4 -o output/dialogue-10060
```

`story commands` 从校验后的完整 episode.json 读取，保留原始字段；旧 `commands list` 是精简检索索引。`story audit` 按功能列出有效命令、原始行号、角色与基础 manifest 文件缺失情况，跳过 IgnoreData 行。它不把现有自定义 Shot 能力当作原剧情命令已实现；当前 `nativeOriginalPlaybackReady` 为 false。参考源码入口见 SOURCE_REFERENCES.md。

`dialogue-plan` / `export-dialogue` 是剪辑工作流，不是游戏回放。只采用有效 Talk 台词，排除隐藏对话框的空 Talk。时长为最长语音采样数/采样率向上取整到 30 fps 加句间停顿；无语音句使用 `--silent-seconds`（默认 2）。`--gap` 默认 0.4；`--offset` 和 `--limit` 按有效台词计数（默认最多 50；整集可用 `--limit 10000`），选段重新从零开始。不会计入原剧的 Delay、转场、BGM/SE、Chat 或独立 Voice；遇到活动 GoTo/ChoiceShow 拒绝生成，避免拼错分支。语言缺失也会报错。

剪辑包包含 `timeline.json`、`dialogue.srt`、`dialogue.wav`、`voices/` 原声和 SHA-256 清单 `manifest.json`。WAV 为 48 kHz 双声道 PCM，每视频帧恰好 1600 音频采样；多条同时发声采用 1/声部数增益，字幕覆盖句末停顿。输出目录必须不存在，失败自动清除本次临时目录，不接受覆盖整个目录。Python 不参与运行。

将音轨与自行渲染的镜头合成：

```powershell
./advkit.exe media mux output/shot.mp4 --audio output/dialogue-10060/dialogue.wav --subtitles output/dialogue-10060/dialogue.srt -o output/preview.mp4
```

`media mux` 直接复制第一路视频，不重编码画面；用指定音频替换原音轨，过短补静音、过长按视频时长裁切。字幕为可开关轨道，不烧入画面。MP4 支持 H.264/HEVC + AAC；MKV 支持现有 ProRes/FFV1/H.264 等视频 + PCM；MOV 支持 CineForm／ProRes 等视频 + PCM 24-bit 和 `mov_text` 字幕，复制 CineForm 视频流时保留 alpha。输出旁边 `.source.json` 记录来源哈希和处理策略。视频镜头与台词内容仍需自行编排，封装不代表已复现原剧情画面。

现有演示 `output/ready-dialogue-preview.mp4` 是 10 秒 1080p/300 帧自编萤镜头 + 剧情 10060 开头两句原声 + 可选中文字幕。对应 `output/dialogue-10060-opening/` 保留台词包。这是输出流程示例，不是原剧情镜头或口型复现。

## Shot JSON

init 提供 fullbody / portrait / duet 模板。完整类型见 `src/OurNotes.Director.Core/Shot.cs`；未知字段报错。

| 字段 | 含义 |
|---|---|
| duration / fps | duration 对应整数帧；fps 当前只接受 30 |
| seed / warmupFrames | 稳定随机种子；默认先预热 30 帧物理与自动控制，再从动作起点开始镜头 |
| output | width、height、renderScale；默认 1920×1080、2 倍超采样；scale 为整数 1–4 |
| background | RGBA 0..1，默认透明；H.264 要求 alpha=1 |
| stage | stage list 返回的 ID，使用原始背景 sprite 与颜色 |
| backgroundImage | `{path:"relative.png"}` 或 `{adv:"10060",logicalPath:"textures/…png"}`；与 stage 二选一 |
| actors | 从后到前绘制，最多 16 人，name 唯一 |
| camera | x、y、zoom、roll，二维构图相机，作用于背景与角色 |
| grade | exposure（档数）、contrast、saturation、vignette；独立导演调色 |
| tracks | 数值关键帧，linear / smoothstep / step |
| events | 按时间排序的 motion / expression 切换，fade 为秒数 |

角色支持 name/model/motion/expression/x/y/height/rotation/opacity/brightness/tint/speed/loop/blink/breath/physics/parameters。

x/y 是归一化画面坐标，(0,0) 左上、(.5,.5) 中心。height 是模型 canvas 相对画面高度的比例，并非可见像素边界。旋转用度。camera.x 增大使场景向左，camera.y 增大使场景向上。actors 顺序决定遮挡，不受游戏五槽位限制。

tracks 的 target 为角色 name 或 camera。相机可动画 x/y/zoom/roll；角色可动画 x/y/height/rotation/opacity/brightness，以及 `param:<真实ID>`：

```json
{"target":"hotaru","property":"param:ParamAngleX","interpolation":"smoothstep","keys":[{"t":0,"value":-15},{"t":3,"value":15}]}
```

静态参数写 `parameters:{"ParamAngleX":10}`。眼睛看向可用 ParamEyeBallX/Y，头部用 ParamAngleX/Y/Z，身体用 ParamBodyAngleX，手动嘴型用 ParamMouthOpenY。使用 model parameters 查该模型的真实参数及范围；未知 ID 报错，值在模型范围内截断。统一适配后的参数覆盖发生在物理、口型求值后；release 可交还原动作。自动语音口型通过 `story` 的 Talk/Voice 指令驱动：MotionSync 模型加载本地 5.0.4 原生 DLL，其余模型与共享语音使用剧情内的 CRI Lips 数据；缺失分析数据会在 `story state` 的 `lipSyncMissing` 中明确显示。Talk 支持原有的 AirLipSync、AirLipSync_HoldOpen、EveryoneLipSync 和 IgnoreLipSync 字段。

loop=true 是导演循环语义，false 在末尾保持。切换使用定长淡入混合，不承诺复现游戏 Animator/FadeController 的完整生命周期。

## 输出与检查

- PNG/FFV1 为 RGBA8 straight alpha；内部预乘合成、超采样降采样后转换回 straight。PNG/图集/帧序列不需要 FFmpeg。
- CineForm 为 `cfhd` / `gbrap12le` / MOV，原生携带 RGB + alpha。默认 `--cineform-quality film1` 平衡体积与画质，`high` 更重视体积。支持 `render clip`、`story clip`、`home clip|export` 和 `media clip`；统一项目可保存 `output.codec="cineform"` 与 `output.cineformQuality="film1"`。宽度须为 16 的倍数、高度须为 8 的倍数且至少 32，1920×1080 可用。不支持的尺寸明确拒绝，不自动缩放。详见 [CINEFORM.md](CINEFORM.md)。
- ProRes 为 4444 / yuva444p10le；H.264 为 yuv420p / CRF 16，无 alpha。10／12-bit 编码格式不代表内部 RGBA8 渲染具有更高的源色深。CineForm 和 ProRes 均是有损中间文件。
- 统一帧序列的 `render.json` 记录输入／依赖散列、GPU、尺寸和各帧散列；frame/clip 的信息返回到 stdout JSON，旧版逐帧几何 `.render.json` 不再生成。
- `render sequence` 从 000000.png 开始。`--resume` 校验 job 与每帧 hash，从头模拟状态，只跳过已验证的像素输出，补画损坏/缺失帧。输入、renderer、DLL 或 GPU 改变会拒绝续渲。`home sequence` 使用 `frame-000000.png` 和 `animation.json`，需要新目录，不支持 resume。
- 剧情／统一 frame 的 time 取 floor(time×30)；主页取 floor(time×fps)，详见主页指南。GPU 在极少透明边缘可能出现舍入差异；当前同机验收容差为预乘 RGBA 每通道 1 个 8-bit 级，不声称跨进程/跨 GPU 字节完全一致。
- 默认拒绝覆盖，显式 `--overwrite` 才替换单个文件；较短任务不能覆盖含有尾部旧帧的 sequence 目录。PNG/视频写临时文件再移动。clip 中断需重跑，`render sequence` 支持续渲；`home export` 始终发布新目录。

```powershell
./advkit.exe image probe output/frame.png
./advkit.exe image compare output/frame.png output/another.png
./advkit.exe media probe output/shot.mov
./advkit.exe media clip "../素材/source.mp4" --start 12 --duration 3 -o output/plate.mov
./advkit.exe media clip output/transparent.mkv --start 0 --duration 3 --codec cineform -o output/alpha-plate.mov
```

image probe 返回 alpha 统计、可见边界、像素 hash；compare 返回误差指标，调用方应检查 withinOneLevel，退出码 0 仅表示完成比较。media clip 默认解码时间段为 ProRes HQ；显式 `--codec cineform` 可转换为 RGB + alpha MOV，并支持 `--cineform-quality`。两者保留源帧率、不带音频。

## 归档与机器接口

读取归档的路径优先级：`--archive` > `OURNOTES_ARCHIVE` > 从 cwd/exe 目录向上查找有效的 `archive` 或旧 `legacy/millsage-story-archive/archive`。默认查询站点为 `intl`；归档命令默认更新 `intl` 和 `jp`。

音视频解包临时文件默认放在程序目录的 `.ournotes-temp`，使用后自动清理。可用 `OURNOTES_TEMP` 指定临时文件的存放根目录；不会更改系统临时目录设置。

stdout 为 UTF-8 JSON，进度在 stderr。列表默认 50 项，可用 limit/offset，返回 total/items/nextOffset；gallery 最多 48 项。正常 envelope 含 schema/ok/provenance/result，失败含 error/exitCode。退出码：0 成功、2 输入错误、3 文件/数据/依赖错误、4 不支持的能力、130 归档被取消。

台词保留全部语言、标题、command 行、voice ID、资源路径与时长。素材搜索保留剧情映射；asset gallery 按 asset 去重。压缩存储文件须先 extract 再消费。

```powershell
./advkit.exe asset search .webm --kind video
./advkit.exe asset extract videos/adv_movie_welcome_party_millsage_11.webm --adv 10070 -o output/welcome.webm
./advkit.exe asset extract episode.json --adv 10060 --lang zh-Hans -o output/episode.json
./advkit.exe asset verify scene.json --adv 10060
./advkit.exe commands list --adv 10060 --limit 20
```

支持 raw/gzip/brotli/split JSON；manifest 提供时检查 stored/decoded size，并校验 decoded SHA-256。split 保留原始数值含 1e999。语音快捷导出校验 SHA-256。probe archive 只读统计，不是全盘完整性扫描。

## 渲染范围（`render` / `story`）

`render` 将统一项目和旧 Shot 交给 `story` 使用的同一原生剧情执行器。Shot 输入保留自身的二维构图与数值轨道语义；原生指令的支持范围与未完成项见 [使用指南](USER_GUIDE.md#4-支持范围与限制)。当前实现不以通用 Unity 引擎为目标。

- 真实 Cubism 网格、纹理、裁剪/反向遮罩、混合模式、prefab 动作/表情、眨眼/呼吸/物理已接通。
- 舞台、剧情相机、景深、粒子、原始材质与后处理按原生剧情指令执行；Shot 的 `grade` 是独立导演调色。现有实现不保证与游戏逐帧一致。
- stage/postfx/still/frame/effect/media list/show 查询 Director Pack 素材目录；跨剧情引用仍需确保当前来源具备完整依赖。media list 只含剧情视频，额外 MV 用本地路径 probe/clip。
- Windows x64 使用不可见窗口创建 WGL 上下文；Linux x64 使用 EGL surfaceless 上下文。像素在离屏 FBO 中渲染；完整剧情效果仍需结合实际 SDK、驱动和素材验证。
- 原剧情音效、语音和转场由原生时间线处理；自定义整曲剪辑与字幕编排可继续衔接后期流程。

## 构建与验收

```powershell
# Windows：构建、运行离线测试、发布原生程序
./build.ps1 -Runtime win-x64
# 只编译和测试（不执行原生发布）
./build.ps1 -NoPublish
```

```sh
# Linux：同一项目，指定 Linux 目标
./build.sh linux-x64
# 仅发布 AOT 主程序（完整包还需下面的依赖暂存步骤）
dotnet publish src/OurNotes.Director.Cli -c Release -r linux-x64 -o artifacts/linux-x64
python3 scripts/stage-native.py --rid linux-x64 --output artifacts/linux-x64
bash scripts/build-media.sh linux-x64 artifacts/linux-x64
```

开发构建需要 .NET 10 SDK，Native AOT 另外需要目标平台的原生编译工具：Windows 的 Visual C++ / Windows SDK，Linux 的 clang 和 zlib 开发包。Native AOT 不支持跨操作系统发布，因此 `win-x64` 在 Windows runner 构建，`linux-x64` 在 Linux runner 构建。`global.json` 固定到 .NET 10 的稳定版本系列；业务代码不需要维护两个副本。没有第三方业务 NuGet 依赖，发布阶段由 .NET SDK 取得官方 AOT 编译器和运行时包。

`build.sh` / `build.ps1` 默认还会暂存原生 SDK 运行库、构建媒体工具，并执行 `probe runtime`。完整媒体构建需要 GCC、make、NASM、pkg-config、curl、tar 和 Python 3；Windows 使用 MSYS2 UCRT64，默认在 `C:/msys64`，可用 `MSYS2_ROOT` 指定其他安装。`--no-publish` / `-NoPublish` 只编译和测试 C#。CI 已配置这些构建工具和按源码锁文件区分的缓存；首次媒体编译较慢，后续校验缓存后复用。`OURNOTES_BUILD_JOBS` 可限制媒体编译并发；`OURNOTES_MEDIA_CACHE` 可指定不含空格的构建缓存目录。这些均为开发依赖，最终包运行不需要它们。

默认 CI 执行底层测试和无需真实素材的 CLI 归档回归：首次下载、五类归档文件、默认客串扫描、重复运行零素材请求、共享文件损坏修复、上游新增资源、旧归档迁移、语言／站点筛选、失败后的恢复。随后检查发布产物是原生 ELF/PE，并直接运行发布程序。Python 仅用于 CI 测试，不是程序运行依赖。

`tests/*smoke.ps1` 是保留的真实素材验收脚本，部分断言针对 2026-10-07 的固定归档快照；需要该素材、Cubism SDK 和 FFmpeg 才能运行，不作为当前上游内容数量的断言，也不加入默认托管 CI。可设置 `OURNOTES_CLI` 指定待验收的原生程序。

正式包在发布后按目标 RID 提取并校验 Cubism Core 和 Windows MotionSync，只附带运行所需的库与许可证。两个 SDK 均从 `scripts/native-sources.lock.json` 固定的官方版本 URL 下载，验证 ZIP 大小与 SHA-256 后再校验每个运行库和许可文件；干净检出无需预放 SDK ZIP。FFmpeg/FFprobe 从锁定版本的 FFmpeg、x264、zlib 源码静态构建，不依赖系统安装的媒体库；`media-sources/` 随包提供完整源码压缩包、SHA-256 和构建脚本。包内版本与文件清单见 `native-dependencies.json`、`media-dependencies.json`。CI 在陌生工作目录中运行依赖探测及媒体回归，再对最终压缩包解压后的副本做验收，避免只验证构建工作区里碰巧存在的 SDK。

附件保留的历史验收记录（使用当时的固定素材快照，并非本次托管 CI 结果）：119 项底层/镜头/PNG/剧情/原生内核检查（含阶段顺序、并发取消、分支、变速、缓动、PCM 混音与相机矩阵）；9 项原生归档检查；55 项归档集成；45 项渲染检查，包含全部 31 个模型真实输出、随机访问/连续对照、容差重复性、损坏帧修复、ProRes alpha、FFV1 精确像素往返、双角色+舞台背景。18 项剧情包/封装检查覆盖真实原声、字幕、帧时长、视频像素不变、长音轨裁切、短音轨补静音和失败清理。10 秒 1080p / 2× supersampling 经 ffprobe 核实 300 帧。报告见 output/reports/ 下各 smoke JSON。测试临时目录在 finally 中自动清理，失败时也保留已完成检查的报告。
