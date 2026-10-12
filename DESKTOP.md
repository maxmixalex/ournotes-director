# Windows 桌面工作台

OurNotes Director 的 Windows 前端使用 WinUI 3 原生控件，提供场景 JSON 编辑、资源检索、主页与剧情导出、媒体处理及任务管理。界面调用同包的 `advkit.exe`，所有渲染、归档和编码仍使用 CLI 的执行器与规则。CLI 参数的完整含义见 [USER_GUIDE.md](USER_GUIDE.md)，项目格式见 [UNIFIED_INTERFACE.md](UNIFIED_INTERFACE.md)。

## 下载与启动

下载 Windows 构建产物 `ournotes-director-win-x64`，完整解压其中的 `ournotes-director-win-x64.zip`，双击 **`OurNotes.Director.exe`**。命令行入口仍是同目录的 `advkit.exe`。

发布包同时带有 .NET 运行时、Windows App SDK / WinUI 运行时、原生渲染库以及 FFmpeg / FFprobe。普通用户不需要另装 .NET、Windows App Runtime、开发 SDK、Node.js、Python 或浏览器环境。请保留整个解压目录，包括 `native/`、`tools/`、`licenses/`、运行时 DLL、`OurNotes.Director.pri` 和清单；单独复制主程序不足以运行。

目标平台为 Windows x64，最低 API 级别设为 Windows 10 1809（build 17763），支持 Windows 11。微软对 Windows App SDK 的正式支持限于仍在支持期的 Windows 版本；项目的最低 API 级别不等同于对所有旧版 Windows 的实机验收。兼容与支持范围的依据见微软的 [Windows App SDK 发布渠道说明](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-channels)。

首次打开后：

1. 在“设置”中选择已有素材归档，或进入“下载与归档”下载需要的乐队、站点和语言。
2. 设置默认输出目录。默认位于用户“视频”文件夹中的 `OurNotes Director`。
3. 打开“诊断检查 → 检查运行环境”，查看原生模型库和音视频工具的实际路径与版本。
4. 进入“主页动画”导出已有主页，或进入“场景工作台”打开、粘贴或创建场景 JSON。

检索游戏资源与渲染需要已经下载的归档。JSON 编辑、本地图片检查、媒体探测与转换、运行环境检查可在没有游戏归档时使用。下载素材需要联网；已有归档的检索和渲染在本机进行。

## 页面与 CLI 对应关系

导航按创作流程分组，页面内的“要做什么”选择器列出该组操作。“更多选项”保留高级参数，包括站点、语言、采样、质量、续渲和显式工具路径。表单空值保留命令或项目默认值；每个输入框旁会说明所用默认值和约束。

| 页面 | 能力 | 对应 CLI |
|---|---|---|
| 开始 | 打开常用工作流、最近项目和归档入口 | 桌面导航 |
| 场景工作台 | 粘贴 / 编辑项目，创建模板，校验、状态检查、抽帧、时间图集、视频、帧序列 | `shot init/validate`、`render validate/state/frame/contact/clip/sequence` |
| 主页动画 | 查主页与角色、动画列表、完整场景或透明角色、批量导出 | `home list/show/animations/frame/contact/clip/sequence/export` |
| 剧情与对白 | 查找剧情、原始指令、对白和声音，导出剧情、语音与对白素材包 | `story` 的检索与渲染操作、`commands list`、`dialogue search`、`voice search/extract` |
| 角色模型 | 模型、参数、动作、表情和预览图集 | `model`、`motion list`、`expression list/show` |
| 素材资源 | 舞台、后期、插图、边框、特效、剧情视频及文件检索 / 提取 | `stage/postfx/still/frame/effect/media list/show`、`asset` |
| 媒体工具 | 本地媒体探测、裁剪 / 转码、合并音轨与字幕、PNG 检查与比较 | `media probe/clip/mux`、`image probe/compare` |
| 下载与归档 | 下载、增量更新、乐队 / 站点 / 语言选择与并发设置 | `archive` |
| 任务与导出 | 查看进度、取消、日志、历史结果及输出文件 | 后台任务队列 |
| 诊断检查 | 能力说明、原生依赖、归档 / 模型 / 相机 / 声音检查、剧情程序与审计 | `help`、`probe`、`story native-program/native-check/audit` |
| 设置 | 归档、输出位置、站点、语言、主题、可选引擎与工具路径 | 桌面默认设置 |

可选择资源的输入框旁有“选择…”按钮，会查询当前归档中的真实编号和名称。结果列表支持本页筛选和后续操作，完整 JSON 与运行日志也可复制或保存。页面操作和 CLI 定义的对应关系由跨平台 `Desktop.Tests` 校验。

## 粘贴 agent 生成的场景 JSON

“场景工作台”直接接受统一项目 JSON，也兼容旧版 Shot JSON。可粘贴纯 JSON，或只有一个 JSON 代码块的 Markdown；无需先手动创建临时文件。代码块外的解释性文字不是 JSON，请只复制 agent 给出的项目对象或完整 JSON 代码块。

建议按下面的顺序工作：

1. **选好来源与素材。** 在剧情、模型和素材页面找到实际 `adv`、模型编号、动作、表情及资源名称，并把这些数据提供给 agent。也可从内置“自由透明人物”“双人纯色背景”“原剧情补丁”或“旧 Shot”模板开始。
2. **粘贴并解析。** 使用“粘贴 JSON”按钮或直接粘贴到编辑区，也可拖入 JSON 文件。语法错误会报告行与列，重复字段也会报错；错误文本保留在编辑区便于修正，不会送交渲染。替换尚未保存的场景前会提示。
3. **调整项目。** 在“JSON 编辑”与“参数编辑”间切换；工具栏提供格式化、复制、保存与另存为。JSON 是完整项目的来源。编辑器保留未知字段，并在结构校验中指出问题；不会为了让表单可编辑而悄悄删除数据。来源、输出、角色、相机和指令变更仍保存为同一项目格式。
4. **先结构校验，再原生验证。** 结构校验检查 JSON、数值和已知字段；原生验证才会加载归档、模型及资源。结构正确并不说明资源存在、动作名称正确或整段动画符合预期。
5. **预览时间点与图集，再导出。** 右侧“输出与检查”选择具体操作，再点“运行所选操作”；“预览一帧”提供快捷预览。检查代表性的开始、中间和结束画面，结果在“预览与结果”标签中显示；视频或帧序列的完整结果以对应任务为准。需要播放到剧情结束时，显式选择该选项并设置合理的时长上限。

例如下面的统一项目继承已归档剧情 `10868` 的原始指令，并指定 CineForm 输出。请将 `source` 改为当前归档中实际选择的剧情、站点和语言：

```json
{
  "format": "ournotes-director/project",
  "version": 1,
  "source": {
    "adv": "10868",
    "site": "intl",
    "language": "zh-Hans"
  },
  "output": {
    "width": 1920,
    "height": 1080,
    "supersample": 2,
    "codec": "cineform",
    "cineformQuality": "film1",
    "clean": false,
    "letterbox": "game"
  },
  "text": {}
}
```

在统一项目中，省略 `initialize`、`commands`、`finalize` 表示继承原剧情对应段；填写 `[]` 表示明确清空该段。给 agent 修改原剧情时，应保留这个区别。完整语义、支持的原生指令和 `Director.*` 扩展见 [UNIFIED_INTERFACE.md](UNIFIED_INTERFACE.md)。

已有项目的相对图片路径以项目所在目录为基准。新粘贴项目含有本地图片时，建议使用文件选择器或绝对路径，并尽早另存为项目文件。另存到其他目录时，编辑器会调整已知的相对图片引用；发送给后台任务的快照会固定这些引用，避免临时目录改变图片来源。未知自定义字段中的路径不会擅自重写。

工具栏更多菜单中的“复制给 agent 的场景说明”提供可粘贴到对话中的格式和能力说明。工作台不会自行请求 agent 服务、上传素材或调用 AI API。粘贴的是用户提供的项目文本，执行的仍是本地渲染器。不要让 agent 编造资源编号；模型、动作、场景资源应来自当前归档的查询结果。

快捷键：`Ctrl+S` 保存，`Ctrl+O` 打开，`Ctrl+Enter` 预览一帧。自动保存的草稿可从工具栏更多菜单恢复；草稿不会代替用户选择位置保存的项目文件。

## 主页动画与透明 MOV

在“主页动画”中先查看主页列表，再查看“主页详情与角色”和“主页动画列表”。单角色选择器使用实际导出 `key`；主页角色不需要 Cubism SDK。

“批量导出主页与角色”默认输出完整 `scene.mov`、`characters/` 下的各角色 MOV 与 `export.json`。角色留空表示场景和角色一起导出，选“全部角色”只导出角色，选择单个角色则只导出该角色。主页可选择一个编号或 `all`。新导出目录必须不存在。

默认参数为 CineForm `film1`、1920×1080、2 倍超采样和 30 fps。时长留空使用自然动画长度；批量素材共用场景起点和时长，便于剪辑对齐。单角色默认采用整个时间区间内的固定取景；需要按原场景位置叠加多层时，选择“原场景相机”。“透明整组动画”移除房间与全局后处理，仍保留 Spine 自带背景和道具。

CineForm、ProRes 4444 和 FFV1 可以保存透明通道，H.264 使用不透明画面。CineForm 的最终宽度必须为 16 的倍数，高度为 8 的倍数且至少 32；1920×1080 可用。默认 `film1` 是画质与体积的平衡档，`high` 通常更小。编码档位、实测条件、alpha 验证和 Premiere Pro 2015 的官方支持依据见 [CINEFORM.md](CINEFORM.md)。主页共享骨架拆分、混合模式和完整渲染边界见 [HOME_SPINE.md](HOME_SPINE.md)。

本地“媒体工具 → 合并视频、音频与字幕”复制原视频流。给 CineForm / ProRes 透明片段添加音轨时，使用 MOV 可以保留原画面和 alpha；字幕是可开关字幕轨，不会烧录到图像。

## 任务、预览与本地数据

导出任务进入后台队列，可以切换页面继续编辑和查询。在“任务与导出”查看当前阶段、进度、耗时、输出路径、完整结果和日志，取消排队或正在执行的任务。程序关闭前会提示仍在进行的任务，未保存场景可保留草稿。重新打开后，历史中尚未完成的任务显示为中断，不会擅自重启大型下载或渲染。

单个输出文件和普通批量导出先在独立临时位置生成，成功后再提交成品；失败或取消不会替换已有成品。归档下载以及支持续渲的剧情 / 项目帧序列会保留已完成的部分和 `render.json`。在历史任务中选择“查看原设置”，检查输出目录、勾选“续渲已有任务”后即可按原项目继续；续渲仍会验证素材、项目、引擎和 GPU 是否匹配。每次任务使用独立的场景输入副本，导出过程中继续编辑不会改变已经排队的内容。

预览 PNG 保留图像透明度。CineForm、ProRes 或 FFV1 等剪辑格式可通过随包 FFmpeg 生成播放代理，代理用于在 Windows 播放器中查看运动和构图；代理是缩小的 H.264 文件，不保留原始透明通道，不能替代最终输出或 alpha 检查。原导出文件不因生成代理而改变。可使用“媒体工具”检查编码，或抽取 PNG 后查看 / 比较透明度。

个人设置、最近项目、任务历史、草稿和预览缓存位于 `%LOCALAPPDATA%\OurNotes\Director\`，与归档和导出目录分开。`settings.json` 保存主题、默认路径和表单设置，`tasks/` 保存任务记录，`previews/` 保存媒体代理。应用启动错误的附加日志位于 `%LOCALAPPDATA%\OurNotes\Director\logs\startup.log`。

## 从源码构建

GUI 构建必须在 Windows 上进行，使用 .NET 10 SDK 和 Windows 构建工具。仓库固定 Microsoft Windows App SDK **2.5.1** 与 `Microsoft.Windows.SDK.BuildTools` **10.0.26100.9169**；WinUI 等传递组件按 NuGet 实际解析版本发布。目标框架是 `net10.0-windows10.0.26100.0`，最低 API 级别是 17763。

先按 [README.md](README.md) 的流程发布并检查 Native AOT `advkit.exe`、原生库和媒体工具，再运行：

```powershell
# 在仓库根目录，使用 PowerShell 7
./scripts/build-desktop.ps1 -OutputDirectory artifacts/win-x64

# 检查已发布目录，并保存真实 WinUI 截图与报告
./tests/desktop-smoke.ps1 -Directory artifacts/win-x64 `
  -ReportDirectory artifacts/desktop-smoke

# 或从最终 ZIP 重新解压、启动并检查
./tests/desktop-smoke.ps1 -Package packages/ournotes-director-win-x64.zip `
  -ReportDirectory artifacts/desktop-smoke-package
```

`build-desktop.ps1` 使用微软支持的 unpackaged、自包含目录部署：`WindowsPackageType=None`、`WindowsAppSDKSelfContained=true`、`.NET SelfContained=true`。无需 MSIX 签名证书或安装全局 Windows App Runtime。GUI 保留自身 .NET 运行时，发布时关闭 Native AOT 与 trimming，并启用 Release ReadyToRun；原有 `advkit.exe` 继续使用 Native AOT，两者是独立进程。

应用资源索引使用此项目实际生成的 `OurNotes.Director.pri`。项目在发布目标中明确加入构建产出的 PRI，缺少源文件时立即失败；构建脚本也要求发布目录包含该文件，随后纳入哈希清单。此处理覆盖 [Windows App SDK 已报告的 unpackaged PRI 发布遗漏](https://github.com/microsoft/WindowsAppSDK/issues/6720)，不生成替代索引，也不依赖 MSIX 项目的 `resources.pri` 文件名。

脚本先发布到自己的临时目录，确认运行文件完整，再合并到已有 Windows 包。它记录并验证后台程序和已有原生 / 媒体依赖的 SHA-256，复制 NuGet 许可证、通知及元数据到 `licenses/desktop/`，生成 `desktop-dependencies.json`。清单记录各 NuGet / framework / runtime 包的实际版本、声明的许可证和随包通知路径，包括 Windows SDK 的 WinRT 支持程序集；若 NuGet 只声明许可 URL，则保留该 URL 和原 `.nuspec`。许可文件使用包声明的真实名称，例如 `sdk_license.txt`，不依赖统一文件名。不要在合并 GUI 之后用“整个目录不得包含 .NET 运行时”的规则判断 CLI 是否为 AOT；工作流在合并前完成独立 CLI 检查，合并后验证同一 `advkit.exe` 的校验和与行为。

Linux 构建继续发布原生 CLI。桌面命令与 JSON 模型在 `OurNotes.Director.Desktop.Core` 中，可在 Windows / Linux 运行契约测试；WinUI 控件、XAML 编译和实际窗口验证只在 Windows 执行。

## 自动检查与证据

发布工作流对最终 Windows ZIP 执行 `tests/desktop-smoke.ps1`。测试从带空格的临时目录解压包，从包外工作目录启动 `OurNotes.Director.exe --smoke-test <绝对报告路径>`，移除外部 SDK / FFmpeg 路径并把外部 .NET 路径指向不存在的位置。`OURNOTES_DIRECTOR_DATA` 指向本次测试的独立临时配置目录，避免已有个人设置影响后台程序选择，也不会改写用户设置。

测试要求：

- 真正收到 WinUI 根元素的 `Loaded`，窗口句柄非零，XAML 布局尺寸大于零。
- 所有导航页面实际创建并布局，并通过真实导航与操作选项逐一创建全部 71 个操作表单，检查每个表单的 `Loaded` 状态与布局尺寸。这一步仅创建控件，不运行资源查询、下载或导出任务。
- 把带 `json` 代码围栏的场景模板写入 Windows 剪贴板，调用界面原有粘贴流程，确认围栏已移除、JSON 内容完整、结构检查通过，参数编辑控件中的宽高与 JSON 一致，CineForm film1 设置保留。
- 真正启动随包 `advkit.exe` 获取命令说明，再生成本地透明 PNG，通过任务队列运行 `image.probe`，检查成功结果及持久保存的任务记录。此项无需游戏素材归档。
- 进程实际加载的 .NET 和 WinUI 原生模块位于当前解压包内，发布文件的长度与 SHA-256 符合清单，后台程序未在 GUI 合并时被替换。
- 通过运行中 WinUI 的 `RenderTargetBitmap` 生成启动和完成两个 PNG；校验 PNG 签名与尺寸。截图来自真实 XAML 客户区，不包含系统标题栏和桌面。

报告为 `report.json`，包含页面列表、逐项检查，以及每个操作表单的 ID、页面和实际宽高。截图为 `report.startup.png` 和 `report.ready.png`，并保留 `stdout.log` / `stderr.log`。GitHub Actions 将这些文件上传为 `ournotes-director-winui-evidence` artifact；测试失败会使 Windows job 失败，不能仅凭进程启动或退出码零判定 UI 验收通过。

这些桌面检查验证窗口、控件、JSON 流程和后台连接。实际游戏动画、GPU 行为、音视频编码、完整素材与 Premiere 导入分别依据渲染、媒体、发布包回归和目标软件实机检查；桌面启动成功不替代这些验收。具体通过的数量、截图和日志以对应 commit 的 CI 产物为准。

## 微软官方依据

- [当前稳定版本、Windows 兼容与维护范围](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-channels)
- [.NET 10 WinUI 官方模板](https://github.com/microsoft/WindowsAppSDK/blob/main/dev/Templates/Dotnet/README.md)
- [Windows App SDK 自包含部署](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps)
- [WinUI unpackaged 项目配置与部署](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/unpackage-winui-app)
- [WinUI 3 持续集成](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/ci-for-winui3)
- [Windows 应用测试与 XAML UI 线程](https://learn.microsoft.com/en-us/windows/apps/develop/testing/)
- [Windows App SDK 文件保存选择器](https://learn.microsoft.com/en-us/windows/apps/develop/files/pickers-save-file)与[官方 API 规范](https://github.com/microsoft/WindowsAppSDK/blob/main/specs/Storage.Pickers/Microsoft.Windows.Storage.Pickers.md)：本项目使用 2.x 的路径返回接口，选择输出位置时不提前创建空文件。
- [CsWinRT 的 Native AOT 与 trimming 要求](https://github.com/microsoft/CsWinRT/blob/master/docs/aot-trimming.md)

桌面前端沿用仓库许可证。Windows App SDK、WinUI、.NET 和各 NuGet 依赖仍遵循自身许可证；构建脚本把实际解析版本的许可材料随 Windows 包发布，其他原生与媒体组件见 [THIRD-PARTY.md](THIRD-PARTY.md)。
