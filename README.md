# OurNotes Director / advkit

**用户入口：[CLI 使用指南](USER_GUIDE.md)**。统一 `render` 接受原游戏指令及自由编排扩展，旧 Shot 自动适配到同一原生执行器。包含原剧情导出、自编剧本、常用参数、支持范围和已知限制。本文保留较详细的素材与 Shot 格式说明。

面向素材归档、检索和 MAD 镜头渲染的原生 CLI。Windows 和 Linux 共用 `src/` 下的同一套 C# 源码，由 `win-x64` / `linux-x64` 编译目标决定发布平台。正式发布使用 **.NET 10 Native AOT**，解压后直接运行 `advkit.exe` / `advkit`，用户机器不需要安装 .NET、Node.js 或 Python。

已能生成真实模型的透明角色、动作/表情动画、多角色构图、舞台背景镜头、PNG 帧序列、ProRes 4444、FFV1 和 H.264。`archive` 联网更新素材，检索和渲染使用本地归档。固定 30 Hz 逐帧模拟，渲染速度不改变片长。

## 下载和运行

GitHub Actions 的 **Build native executables** 工作流在 GitHub 托管的 `ubuntu-24.04` 与 `windows-2022` 上分别构建、测试和发布。完成后下载 `ournotes-director-linux-x64` / `ournotes-director-win-x64` artifact，解开其中的平台压缩包；包内包含原生程序、使用指南、许可证和示例剧本。

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

归档、检索、校验和解包只需要程序本身及操作系统网络组件。渲染仍需要本机 Cubism Native Core 和 OpenGL；音视频解码／编码需要 FFmpeg/FFprobe。Linux 发布以 Ubuntu 24.04 为构建基线，需要兼容或更新的 glibc 系统，不适用于 Alpine/musl；图形路径使用 EGL surfaceless OpenGL，可配 Mesa 软件渲染。AOT 消除了 .NET 安装要求，没有替代这些图形和媒体组件；CI 包不包含游戏素材或用户提供的 Cubism SDK。

## 一个命令归档和增量更新

```powershell
./advkit.exe archive
```

它会读取当前 intl / JP MasterData，动态识别 millsage 成员和关联剧情，默认继续扫描 story/model manifest 以发现客串；随后下载所有语言、公共文件和模型依赖，校验素材，重建检索索引，并为有普通剧情的站点生成 Director Pack。不需要运行旧 Node 项目或拆成多个命令。

**更新或中断后续跑，仍执行同一条 `archive`。** 已有素材按解码后 SHA-256、decoded size 和 stored size 校验，通过后复用；缺失或损坏的文件会补齐。共享存储与站点素材优先使用 hardlink，不支持时自动复制。可变的目录、MasterData 和 manifest 会按 ETag / Last-Modified 重新确认；变化后只补下载所需的新内容。下载写入临时文件，校验成功后才替换；索引和 Director Pack 也先在临时目录构建，失败保留上一份完整结果。同一归档有进程锁，防止并发更新相互覆盖。

默认查找已有 `archive`，也兼容旧 `legacy/millsage-story-archive/archive`；没有现成归档时创建当前目录的 `archive/`。`--out` 或 `OURNOTES_ARCHIVE` 可以固定位置。只保留一种调用方式，需要时加选项即可：

```powershell
./advkit.exe archive --out D:/OurNotes/archive
./advkit.exe archive --out D:/OurNotes/archive --languages zh-Hans,ja
./advkit.exe archive --out D:/OurNotes/archive --sites intl --concurrency 8
```

默认所有语言、两个站点、并发 8、开启客串深扫；`--no-deep` 可明确限定到 MasterData / 目录关联范围。`--retries` 控制单次请求的最大尝试次数（默认 4）。筛选不会删除已经保存的旧素材；索引依据本次选择生成。归档器按完整文件复用，尚未下载完的单个文件会重新下载，不是 HTTP 字节范围续传。

原始 manifest 保留上游字节，gzip / Brotli / split JSON 仍可由已有 C# 解包逻辑读取，包括 `1e999` 这样的合法 JSON 数值。主要输出保持兼容：

| 路径 | 内容 |
|---|---|
| `discovery.json` | 本次成员、剧情来源、站点和筛选范围 |
| `store/` | 跨剧情、跨站点共享的哈希素材 |
| `sites/intl`、`sites/jp` | 筛选后的剧情目录、原始 story/model manifest、素材和 Director Pack |
| `index/catalog.json`、`models.json`、`stats.json` | 剧情、模型和统计目录 |
| `index/*.jsonl` | commands、texts、dialogue、voices、sounds、files 检索索引 |

高级镜像／测试可使用 `OURNOTES_METADATA_BASE`、`OURNOTES_STORY_SITE_INTL`、`OURNOTES_STORY_SITE_JP` 覆盖源地址，普通使用不需要设置。所有进度写 stderr，stdout 始终返回一个 JSON 结果；Ctrl+C 返回 130，下次执行同一命令恢复。

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

## 剧情检查与原声剪辑包

`story frame|clip` 直接执行游戏指令，支持舞台、透视相机、角色动作、对话、48 kHz 混音、语音口型、原始材质与后处理。剧情 10060 已完成整段视频导出；含手机聊天的 10061、10066、10078 已完成整段时间线运行。ChatWindow、ChatTalk、ChatStamp、ChatRead、ChatTyping 已接入原生手机画布，含来电、锁屏、emoji 与历史记录；输入动画和非等待模式切换已对照网页帧状态校验。SimpleADV（结算、主页短对话及其宿主场景）按需求排除；正常剧情的完整画面一致性尚未达成。使用 `story native-check` 检查资源初始化，`story clip --until-end` 验证整段播放；初始化成功不代表全部演出已验证。

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

`media mux` 直接复制第一路视频，不重编码画面；用指定音频替换原音轨，过短补静音、过长按视频时长裁切。字幕为可开关轨道，不烧入画面。MP4 支持 H.264/HEVC + AAC；MKV 支持现有 ProRes/FFV1/H.264 等视频 + PCM，可保留透明视频编码。输出旁边 `.source.json` 记录来源哈希和处理策略。视频镜头与台词内容仍需自行编排，封装不代表已复现原剧情画面。

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
- ProRes 为 4444 / yuva444p10le；H.264 为 yuv420p / CRF 16，无 alpha。10-bit 编码容器不代表内部渲染具有 10-bit 色深。
- 统一帧序列的 `render.json` 记录输入／依赖散列、GPU、尺寸和各帧散列；frame/clip 的信息返回到 stdout JSON，旧版逐帧几何 `.render.json` 不再生成。
- sequence 从 000000.png 开始。`--resume` 校验 job 与每帧 hash，从头模拟状态，只跳过已验证的像素输出，补画损坏/缺失帧。输入、renderer、DLL 或 GPU 改变会拒绝续渲。
- frame 的 time 取 floor(time×30)，必须小于 duration。GPU 在极少透明边缘可能出现舍入差异；当前同机验收容差为预乘 RGBA 每通道 1 个 8-bit 级，不声称跨进程/跨 GPU 字节完全一致。
- 默认拒绝覆盖，显式 `--overwrite` 才替换；较短任务不能覆盖含有尾部旧帧的 sequence 目录。PNG/视频写临时文件再移动。clip 中断需重跑，只有 sequence 支持续渲。

```powershell
./advkit.exe image probe output/frame.png
./advkit.exe image compare output/frame.png output/another.png
./advkit.exe media probe output/shot.mov
./advkit.exe media clip "../素材/source.mp4" --start 12 --duration 3 -o output/plate.mov
```

image probe 返回 alpha 统计、可见边界、像素 hash；compare 返回误差指标，调用方应检查 withinOneLevel，退出码 0 仅表示完成比较。media clip 解码时间段为 ProRes HQ，保留源帧率，不带音频，用于提取 MV 镜头素材。

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

## 独立 Shot 渲染器的边界（`render`）

以下限制仅指较早的 `shot` / `render` 路径。`story` 已有原生剧情相机、景深、粒子、原始材质及后处理实现，其支持范围与未完成项见 [使用指南](USER_GUIDE.md#4-支持范围与限制)。两者都不以通用 Unity 引擎为目标。

- 真实 Cubism 网格、纹理、裁剪/反向遮罩、混合模式、prefab 动作/表情、眨眼/呼吸/物理已接通。
- stage 以 cover 方式使用真实背景 sprite；未复现游戏 3D field、Focus 预设、DoF、粒子、Lit-URP、rim light、shadow 或原版 PostEffect。grade 为独立调色。
- stage/postfx/still/frame/effect/media list/show 复用已有 Director Pack；能列出 postfx 不表示能应用它。media list 只含剧情视频，额外 MV 用本地路径 probe/clip。
- Windows x64 使用不可见窗口创建 WGL 上下文；Linux x64 使用 EGL surfaceless 上下文。像素在离屏 FBO 中渲染；完整剧情效果仍需结合实际 SDK、驱动和素材验证。
- 整曲剪辑、字幕/转场/混音交给独立后期流程。

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
# 直接使用 .NET 发布命令
dotnet publish src/OurNotes.Director.Cli -c Release -r linux-x64 -o artifacts/linux-x64
```

开发构建需要 .NET 10 SDK，Native AOT 另外需要目标平台的原生编译工具：Windows 的 Visual C++ / Windows SDK，Linux 的 clang 和 zlib 开发包。Native AOT 不支持跨操作系统发布，因此 `win-x64` 在 Windows runner 构建，`linux-x64` 在 Linux runner 构建。`global.json` 固定到 .NET 10 的稳定版本系列；业务代码不需要维护两个副本。没有第三方业务 NuGet 依赖，发布阶段由 .NET SDK 取得官方 AOT 编译器和运行时包。

默认 CI 执行底层测试和无需真实素材的 CLI 归档回归：首次下载、五类归档文件、默认客串扫描、重复运行零素材请求、共享文件损坏修复、上游新增资源、旧归档迁移、语言／站点筛选、失败后的恢复。随后检查发布产物是原生 ELF/PE，并直接运行发布程序。Python 仅用于 CI 测试，不是程序运行依赖。

`tests/*smoke.ps1` 是保留的真实素材验收脚本，部分断言针对 2026-10-07 的固定归档快照；需要该素材、Cubism SDK 和 FFmpeg 才能运行，不作为当前上游内容数量的断言，也不加入默认托管 CI。可设置 `OURNOTES_CLI` 指定待验收的原生程序。

Native SDK 默认使用本机已有 CubismSdkForNative-5-r.5，也支持 --dll、--cubism-sdk / CUBISM_NATIVE_SDK。缺失时报错，无 Web/静态图替代。Core 不打包进 bin。移植来源与许可证见 THIRD-PARTY.md。

附件保留的历史验收记录（使用当时的固定素材快照，并非本次托管 CI 结果）：119 项底层/镜头/PNG/剧情/原生内核检查（含阶段顺序、并发取消、分支、变速、缓动、PCM 混音与相机矩阵）；9 项原生归档检查；55 项归档集成；45 项渲染检查，包含全部 31 个模型真实输出、随机访问/连续对照、容差重复性、损坏帧修复、ProRes alpha、FFV1 精确像素往返、双角色+舞台背景。18 项剧情包/封装检查覆盖真实原声、字幕、帧时长、视频像素不变、长音轨裁切、短音轨补静音和失败清理。10 秒 1080p / 2× supersampling 经 ffprobe 核实 300 帧。报告见 output/reports/ 下各 smoke JSON。测试临时目录在 finally 中自动清理，失败时也保留已完成检查的报告。
