# Windows 桌面工作台

OurNotes Director 的主界面只有 **场景工作台** 和 **主页工作台**。两者采用同一布局：左侧对象、中间实时画布、右侧属性、下方时间轴。素材、设置和任务从顶栏打开，输出格式在完成编辑后通过“导出”选择。

实时预览直接调用应用进程内的原生渲染器，并把 RGBA 帧显示在 WinUI 窗口中。拖动、调色和播放无需先导出视频。最终导出使用相同的渲染实现，在后台队列中按项目的正式尺寸与画质运行。

## 启动与准备

完整解压 `ournotes-director-win-x64.zip`，双击 **`OurNotes.Director.exe`**。保留整个解压目录；包内已带 .NET、Windows App SDK、原生模型库与 FFmpeg，无需另装 SDK、Node.js、Python 或浏览器。命令行入口仍是同目录的 `advkit.exe`。

1. 点击顶栏 **素材库**，选择已有素材目录，或打开下载与更新工具。
2. 点击设置图标，选择默认输出位置、站点和语言。
3. 进入场景工作台新建场景，或进入主页工作台选择主页。

目标平台为 Windows x64，最低 API 级别为 Windows 10 1809（build 17763）。实际支持范围还受 Windows App SDK 和系统图形驱动影响，最低 API 级别不等于旧版 Windows 的实机验收。微软依据见 [Windows App SDK 发布渠道说明](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-channels)。

素材检索与渲染需要已经下载的归档；下载需要联网。已有项目和归档的渲染在本机执行。

## 场景工作台

### 布置场景

使用文件菜单中的“新建场景”，选择实际素材来源后开始。这里的素材来源供原生执行器找到模型、动作及配套资源；新场景的编排由自己的项目决定。

- **添加角色**：从素材库选择模型。左侧列表或画布点击选择角色。
- **直接摆放**：在画布中拖动角色，滚轮调整大小。选中对象显示包围框。屏幕空间角色支持拖动；导入的世界空间角色使用属性中的坐标编辑。
- **调整属性**：右侧修改位置、高度、旋转、透明度、显示状态、动作和表情。
- **相机**：在左侧选“相机”，调整平移、缩放与旋转。
- **画面**：选“画面与后处理”，设置透明背景或本地背景图片，调整曝光、对比度、饱和度和暗角。透明区域显示棋盘格。

属性修改会先显示在当前画面中；拖动结束后将最终位置写入项目。撤销、重做、保存和另存为位于工作台工具栏。

### 用时间轴编排

点击时间尺移动播放头，播放按钮在窗口内连续播放；可选择循环。时间轴按 30 fps 对齐，支持前后逐帧、缩放时间尺、调整总时长，以及拖动或删除关键帧。

开启 **自动关键帧** 时，当前位置、相机和调色的修改记录在播放头处；动作和表情在当前时间点开始。关闭时调整对象的基础状态。数字属性在关键帧之间插值；动作和显示状态按事件切换。

时间轴与导出使用同一项目。场景编辑器把可编辑关键帧和确定的逐帧指令写入统一项目，因此保存后重新打开、通过 CLI 导出和桌面预览可以恢复同一编排。播放头和对象选择本身不会产生项目修改或撤销记录。

原游戏剧情可能包含对白、等待、视频或继承的指令段，实际时长依赖资源与交互。编辑器不会猜测这些时间、清除原指令或强行重写为可编辑轨道；此类项目保留原始 JSON，并提示时间轴限制。自由编排请从新场景开始，原剧情的高级编辑仍可通过 JSON 与素材工具完成。

## 主页工作台

点击 **选择主页**，从实际归档中选择主页。房间和角色会直接显示在中央画布中。左侧保留所有角色，右侧只显示当前对象的属性。

- 选角色后直接拖动、缩放，或输入位置与缩放比例；可隐藏角色。
- 在动作列表中选择动画，设置循环、速度和起点，再在当前时间开始动作。
- 用位置关键帧安排角色运动，用动作关键帧安排动作切换。
- 选“房间与画面”，控制房间背景、原始后期效果，并编排曝光、对比度、饱和度和暗角。
- **仅预览此角色** 用于检查所选角色；选中角色本身不会自动改变预览范围。导出范围在导出面板单独选择。

主页项目保存为 `ournotes-home-project/1` JSON，包含素材来源、对象变换、动作片段、调色轨道、时长和输出默认值。CLI 的 `home frame/clip/sequence/export --project <文件>` 可以读取同一项目；显式传入的 CLI 输出参数覆盖项目默认值，已编排的内容仍然生效。

## JSON 与 agent 配合

两块工作台都提供 **JSON** 按钮，可复制当前项目、粘贴修改结果并应用。场景还支持直接拖入 JSON 文件或文本。接受纯 JSON 或单个 JSON 代码围栏；无效输入会保留在对话框中并显示错误，修正后再应用。

空白主页也可以直接粘贴完整项目；尚未选择素材目录时，项目会保留，并提供选择目录后继续预览的入口。场景项目修改了可编辑关键帧后，应用或重新打开时会同步更新派生的动画帧，避免画布与导出使用不同曲线。若打开的文件因此更新，请保存后再交给 CLI 使用。

建议先用素材库选定真实资源，再复制当前项目给 agent：说明要调整的角色、动作、时间与构图，让 agent 返回完整项目。场景 JSON 面板中的“给 agent 的说明”可以复制格式与当前内容。不要让 agent 编造素材编号或动作名。

场景使用 `ournotes-director/project`、`version: 1`。省略 `initialize`、`commands`、`finalize` 表示继承原剧情对应段，填写 `[]` 表示清空对应段；这两者含义不同。完整指令和格式见 [UNIFIED_INTERFACE.md](UNIFIED_INTERFACE.md)。编辑器会保留原始及未知字段，无法安全展示为控件的内容仍可在 JSON 中检查。

已有项目的相对图片路径以项目目录为基准。新粘贴项目引用本地图片时，使用文件选择器或绝对路径，并尽早另存为项目文件。保存到另一个目录、发送预览或导出时，已知图片引用会保持指向原文件。

工具栏文件菜单可恢复草稿。草稿保存当前项目及播放头，不能代替用户选择位置保存的项目文件。`Ctrl+S` 保存，`Ctrl+O` 打开，`Ctrl+Z` / `Ctrl+Y` 撤销和重做；文本框内保留文本编辑行为。

## 导出

在任一工作台右上角点击 **导出**。选择视频、PNG 帧序列或当前画面 PNG，再设置格式、尺寸、路径。采样质量和时间范围放在可展开的选项中。

主页还可选择 **完整主页、所选角色、每个角色分别导出、完整主页与全部角色**。每个角色单独导出的视频包含该角色已经编辑的动作和位置；完整主页与角色共享时间范围，便于在剪辑软件中对齐。

| 格式 | 用途 | 透明通道 |
|---|---|---|
| MOV / CineForm，默认 Film 1 | Premiere 剪辑与画质、体积平衡 | 支持 |
| MOV / ProRes 4444 | 需要 ProRes 的后期流程 | 支持 |
| MKV / FFV1 | 无损中间文件 | 支持 |
| MP4 / H.264 | 普通视频预览或发布 | 不支持 |
| PNG / PNG 序列 | 单帧、逐帧后期 | 支持 |

CineForm 的宽度必须是 16 的倍数，高度必须是 8 的倍数且至少 32。横屏预设为 1920 × 1080，竖屏预设为 1088 × 1920，均满足编码约束；Film 1 是默认平衡档，还可选择 High 或 Film 3。透明画面需要在场景中设置透明背景；选择支持 alpha 的格式本身不会自动移除房间背景。编码边界与 Premiere Pro 2015 的官方支持依据见 [CINEFORM.md](CINEFORM.md)。

点击“开始导出”后进入后台队列。可以继续在工作台中编辑，在顶栏任务图标查看进度、取消任务或打开结果。每个任务固定自己的项目副本，排队后的编辑不改变已提交的内容。导出失败或取消时，已有成品按原有安全提交规则保留；支持续渲的帧序列可从高级工具继续。

## 素材与其他工具

顶栏 **素材库** 管理目录、下载与更新。“素材检索与其他工具”折叠区域保留已有 CLI 的完整操作，包括剧情与对白、声音提取、模型与参数、素材检索、媒体转码与合轨、图像检查、诊断、旧版场景检查和续渲。它们按需从侧面打开，日常摆放与动画编排在两个工作台完成。

| 入口 | 内容 |
|---|---|
| 素材库 | 目录、下载、更新、真实资源列表 |
| 素材检索与其他工具 | 原有 CLI 操作与高级参数 |
| 任务图标 | 当前进度、取消、历史结果、日志 |
| 设置图标 | 默认路径、站点、语言、主题及高级工具路径 |

个人设置、最近项目、任务历史、草稿和缓存位于 `%LOCALAPPDATA%\OurNotes\Director\`。启动错误日志位于其中的 `logs\startup.log`。任务完成后的媒体结果仍可生成播放代理；工作台实时画布直接显示原生帧。

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

测试检查两个主工作台的实际布局、剪贴板 JSON 应用、关键帧插值与撤销、实时渲染和后台任务。提供 `-FixtureDirectory` 后，会加载合法的最小 ADV 归档与 Spine 动画素材，通过实际原生执行器检查：

- RGBA 帧与透明度确实写入 WinUI 画布。
- 改变位置或后处理后，在当前会话中产生不同像素。
- 前后拖动播放头可恢复同一帧，播放会连续产生新帧。
- 项目修改和快照切换后，预览状态仍对应同一时间。
- 同时保留场景与主页两个渲染会话，切换后状态不丢失。

Windows CI 没有可依赖的硬件图形驱动，使用 MSYS2 Mesa 的软件 OpenGL 驱动来运行上述真实渲染。驱动只在显式 `--smoke-test` 模式通过 `-OpenGLDriver` 指定，不打入发布包；正常用户启动使用系统驱动。测试素材是自动生成的最小合法归档，不包含商业游戏角色或模型。

测试还会启动随包 `advkit.exe` 获取能力信息，通过后台队列检查本地透明 PNG，验证任务结果落盘、包内运行时路径与文件 SHA-256。Linux 原生测试独立检查主页编辑项目的离线渲染与预览像素一致、共享骨架人物拆分、alpha、逐帧时间、并发会话与确定性回放。

`report.json` 记录逐项结果，`report.startup.png`、`report.ready.png`、`report.scene.png` 来自实际 WinUI `RenderTargetBitmap`，并验证 PNG 尺寸与不透明窗口背景。GitHub Actions 上传为 `ournotes-director-winui-evidence` artifact。检查失败会使 Windows job 失败。

最小归档检查不能替代用户全部游戏素材、各厂商 GPU 或 Premiere Pro 2015 的实机导入验收。以对应 commit 的测试报告、截图和目标环境实测为准。

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
