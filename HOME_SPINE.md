# 主页 Spine 动画导出

`home` 从现有归档读取主页场景及 Spine 4.2 动画，可以导出完整场景、透明的整组 Spine 动画，以及每个角色的独立动画。默认视频格式为 **CineForm RGB + alpha MOV，film1 质量**；默认 1920 × 1080、30 fps、2 倍超采样。编码参数、尺寸要求和 Premiere Pro 2015 的支持依据见 [CINEFORM.md](CINEFORM.md)。

主页使用编译进程序的 Spine C# runtime 和离屏 OpenGL，不需要 Cubism SDK、Unity、浏览器、Node.js 或 Python 运行环境。游戏素材仍需先用现有 `archive` 命令取得；主页所需的 `host/host.json`、`host/spot/spot.json`、Spine atlas／骨架／贴图和房间资源已经属于 story manifest，不需要迁移归档或新增下载命令。

## 查找和快速导出

以下采用 Windows 文件名，Linux 将 `./advkit.exe` 换成 `./advkit`。先用列表确认当前归档包含哪些主页；示例使用包含 Hotaru 的 spot ID `40005`；若当前归档没有该主页，应替换为列表中的 ID。

```powershell
./advkit.exe home list
./advkit.exe home show 40005
./advkit.exe home animations 40005

# 从第 0 帧到原动画结束，完整场景和每个角色各写一个 MOV
./advkit.exe home export 40005 -o output/home-40005

# 导出当前归档中的全部主页；每个站点／spot 使用自己的子目录
./advkit.exe home export all -o output/all-homes
```

`home show` 返回角色 `key`、名称、原 prefab 的角色 ID、资源部件与来源剧情。优先把该命令返回的 `key` 传给 `--character`；名称、明确的原 prefab ID 和 target path 也可选择角色，含糊的匹配会报错。主页角色 ID 不等同于 Live2D 模型 ID。

同一主页可能被多个剧情引用。`--site intl|jp` 限定站点，`--adv <剧情ID>` 为一个主页指定资源来源；不指定站点时，按 ID 打开优先选择 intl。`home export all` 不接受 `--adv`，只处理当前归档中已发现的主页。

## 完整场景、透明整组与单角色

| 目标 | 命令 |
|---|---|
| 房间和全部 Spine 的完整画面 | `home clip 40005 -o output/scene.mov` |
| 去掉房间的整组透明 Spine | `home clip 40005 --no-background -o output/spine.mov` |
| 一个角色的透明动画 | `home clip 40005 --character Hotaru -o output/hotaru.mov` |
| 一次输出完整场景和所有独立角色 | `home export 40005 -o output/home-40005` |
| 一次输出透明整组和所有独立角色 | `home export 40005 --no-background -o output/home-40005-alpha` |
| 只输出所有独立角色 | `home export 40005 --character all -o output/home-40005-characters` |
| 批量目录中只输出指定角色 | `home export 40005 --character Hotaru -o output/home-40005-hotaru` |

表中的 `Hotaru` 是角色选择示例；应以所选主页 `home show` 的结果为准。`--character all` 仅用于 `export`，其他操作省略 `--character` 表示完整场景。

单角色默认就是透明画面。`--no-background` 对完整场景移除 **room 网格及全局 volume 后处理**，保留全部 Spine 部件，包括 Spine 自身的 `Bg`、家具、道具和未分配给角色的部分。因此，整组透明导出仍可能有由 Spine 附件绘制的实心区域；它不是按图像颜色自动抠背景。

带房间的完整场景使用原主页相机、房间材质和已启用的 HomeView volume。Bloom／色彩处理由实际启用状态决定，不因为归档里存在配置就强行开启。单角色和 `--no-background` 输出不套全局后处理，以保留透明合成所需的画面。

### 固定构图与后期对齐

角色默认 `--framing fit`：程序检查整个输出时间段每一帧的几何边界，使用一个带留白的固定构图。角色伸手、道具摆动等会计入取景范围；不会每帧重新缩放或自动追踪主体。准备取景需要预先遍历动画，因此长片段在写出首帧前也有计算开销。

如果要把多名角色按原位置叠回同一画布，使用 `--framing scene`：

```powershell
./advkit.exe home export 40005 --character all --framing scene -o output/home-40005-layers
```

这些文件使用同一场景相机、尺寸、起点和帧率，便于在后期时间线上对齐。Spine 的 add／multiply／screen 等混合模式依赖下方画面；将单角色渲染成独立 RGBA 层后，再放到不同背景上，不保证与原完整场景逐像素相同。需要保留原场景中这些混合关系时，使用完整场景输出。

## 动画起点、末帧和循环

省略 `--animation` 时使用 prefab 的原动画名称、循环设置、timeScale 和初始 skin；`home animations` 列出每个部件的骨架版本、实际动画、循环状态、有效时长与可用动画名称。

省略 `--time` 和 `--duration` 会从 **真正的第 0 帧**开始，按最长部件的有效时长输出。非循环动画包含结束姿态：例如 4 秒动画以 30 fps 自然导出，包含 0 到 4 秒的 121 帧，视频时长为 121 / 30 秒。全部部件循环时不额外追加重复的周期终点。

同一次 `home export` 中，完整场景和所有角色共用完整场景的最长时长；较短的非循环部件保持末帧。单独 `home clip --character` 则默认采用该角色自身部件的最长时长。各文件的 `sourceDuration`、`firstFrame`、`frameCount`、`duration` 和 `includesSourceEnd` 可用于检查输出范围。

```powershell
# 指定片段：起点向下取整到帧，长度向上取整为完整帧
./advkit.exe home clip 40005 --character Hotaru --time 1 --duration 3 -o output/hotaru-cut.mov

# 按原动画循环 12 秒
./advkit.exe home clip 40005 --character Hotaru --loop --duration 12 -o output/hotaru-loop.mov

# 先查询可用名称；覆盖名称必须存在于所有选中的骨架中
./advkit.exe home animations 40005 --character Hotaru
```

`--animation <名称>` 替换所选部件的动画，未知名称会明确报错。`--loop` 强制循环；省略时保留各部件的原始设置。`--fps` 接受 1–120，默认 30；这是主页动画的固定模拟和输出帧率。`home` 不使用剧情的 `--speed` 或 `--until-end` 语义。

## 抽帧、图集和 PNG 序列

```powershell
./advkit.exe home frame 40005 --time 2 -o output/home.png
./advkit.exe home frame 40005 --character Hotaru --time 2 -o output/hotaru.png
./advkit.exe home contact 40005 --character Hotaru --times 0,1,2,4 --columns 2 -o output/hotaru-contact.png
./advkit.exe home sequence 40005 --character Hotaru -o output/hotaru-frames
```

`frame` 输出单张 PNG；`contact` 按指定时间生成图集，并在 stdout JSON 的 `cells` 中返回每格的帧号、实际时间和位置。图集默认每格 640 × 360、3 列、采样时间 `0,1,2,3`；其他渲染操作默认 1920 × 1080。帧与图集预览的角色 fit 使用整个自然动画区间的固定构图。

`sequence` 输出 `frame-000000.png` 起的 PNG 文件及 `animation.json`，使用与视频相同的时序和 straight alpha。它需要新的输出目录，目前不支持续渲。PNG 输出不调用 FFmpeg；主页导出不包含对话或声音，后期加音轨可使用 `media mux`。

## 参数和输出目录

| 参数 | 含义 |
|---|---|
| `--archive PATH` | 指定现有素材归档 |
| `--site intl` | 限定主页来源站点 |
| `--adv 10868` | 为 spot 40005 指定已归档的来源剧情；其他主页按 home show 的 sources 选择 |
| `--character KEY` | 选择 `home show` 返回的角色 |
| `--animation NAME` | 覆盖所选骨架的原动画名称 |
| `--time 0 --duration 5` | 起点和长度，单位秒；省略长度采用自然结束时长 |
| `--loop` | 强制动画循环 |
| `--fps 30` | 固定模拟和输出帧率，1–120 |
| `--width 1920 --height 1080` | 输出画布尺寸 |
| `--supersample 2` | 超采样抗锯齿，1–4 |
| `--framing fit` / `scene` | 整段固定取景／原场景相机；角色默认 fit，场景默认 scene |
| `--no-background` | 移除 room 和全局后处理，保留整组 Spine |
| `--codec cineform` | 默认；RGB + alpha MOV，也可选 prores、ffv1 |
| `--cineform-quality film1` | 默认平衡档位；`high` 更重视体积 |
| `--overwrite` | 仅用于替换单个输出文件；不能覆盖 export／sequence 目录 |

CineForm 要求宽度为 16 的倍数、高度为 8 的倍数且至少 32。1920 × 1080 可用；1080 × 1920 需要显式改成例如 1088 × 1920，程序不自动变形。H.264 仅用于带背景的单个 `home clip`；透明输出及批量 `export` 需要 CineForm、ProRes 或 FFV1。

默认 `home export 40005` 生成：

| 路径 | 内容 |
|---|---|
| `scene.mov` | 完整场景，或使用 `--no-background` 时的整组透明 Spine |
| `characters/<key>.mov` | 每个角色独立透明动画 |
| `export.json` | 站点／spot／来源、各文件、尺寸、时序、编码、构图、后处理与耗时信息 |

`home export all` 在 `<site>-<spotId>/` 下分别放置上述视频，顶层 `export.json` 汇总全部文件。FFV1 对应 `.mkv`。程序先写本次临时目录，所有目标完成后才发布输出目录；遇到不支持的素材或编码失败会报错并清理本次临时内容。

## 角色归属和支持边界

一个角色可以由多个 SkeletonAnimation 组成，例如主体、背面、影子和手持物；多个角色也可能共享同一个骨架。独立导出按原 prefab 的 target 关系和可核实的 slot／骨骼／附件信息分组，保留完整骨架的约束和动画求值，仅筛选输出的图形部件。

没有点击交互的具名角色也可独立导出。当前已根据原骨架附件核实 `30002` 的 `Manager` 和 `30003` 的 `Marukun`，通过 spot ID、骨架文件和原组件名的精确匹配纳入角色列表；这两项的 `sourceCharacterId` 为 `null`、`targetPaths` 为空，`association` 为 `verifiedNonInteractive`。普通背景和道具继续保留在 `unassignedParts`，不会仅因使用 Spine 就被识别为角色。

`home show` 自动包含 `exportSupported`、共享骨架的 `slotFilters`、未分配场景部件，必要时给出 `unsupportedReason`。不能可靠确定共享 slot 归属时，独立角色导出明确拒绝；仍可尝试完整场景导出。列出角色不代表其所有分离条件均已通过实际渲染。

本功能支持当前归档格式和 Spine 4.2 数据，以及已实现的主页房间／材质路径；未知 Spine 版本、材质或不可靠的部件拆分会报告不支持。没有导入任意 Unity 场景或执行任意脚本的能力。SimpleADV 的主页点击对话、结算对话及其 UI 指令尚未接入；主页动画本身由 `home` 导出。

原场景视觉、完整动画末帧、透明合成和编码输出须分别检查；首帧成功不能代替全段验证。Premiere Pro 2015.0 的实际导入不属于当前自动化测试。Spine 来源和单独许可证见 [THIRD-PARTY.md](THIRD-PARTY.md)；代码仓库的来源记录为 `third_party/Spine.Runtime/ORIGIN.md`，发布包随附 `licenses/Spine-Runtimes.txt` 和 `licenses/Spine-Runtimes-Origin.md`。
