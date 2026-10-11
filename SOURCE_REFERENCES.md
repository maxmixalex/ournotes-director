# 演出与实现参考

## 原始演出数据

`advkit archive` 从 MoeNotes 站点下载剧情、模型、动作、表情、语音及材质数据。归档是本工具的直接演出参考，发布程序不附带完整素材库；已有的旧归档仍可复用。工具与数据网站均不属于游戏官方。

通过逻辑文件名和原始指令检索，避免手动逐个解压哈希素材：

```sh
./advkit story show 10060
./advkit story commands 10060 --offset 0 --limit 50
./advkit story native-program 10060 -o output/original-program.json
./advkit asset show story.json --adv 10060
```

Windows 使用 `./advkit.exe`。`story commands` 保留原字段与行号；`native-program` 导出初始化、正文和收尾，不是可以直接当作统一 project 容器执行的文件。`samples/` 中是本工具的演示编排，不是原游戏逐字导出的剧本。

| 附件基线中的参考剧情 | 参考内容 |
|---|---|
| 10060 | millsage 角色、原声、普通对话、相机；统一 demo 的资源来源 |
| 10079 | 插画／Still，搭配 `samples/game-still-shake.json` |
| 10070 | 视频资源，搭配 `samples/game-video.json` |
| 10061 | 手机画面，搭配 `samples/game-chat.json` |
| 10062 | CRI Lips 分析数据 |

部分特效样例依赖原开发工作区中另行准备的 `references/effect-samples` 归档，例如剧情 10020；默认 millsage 归档不保证包含它们。现在可以用同一个 `archive --band <ID|token>` 命令为其他乐队建立归档，普通剧情及相关舞台、特效、模型会随该选择生成索引和 Director Pack。

## 社区实现

- [Moenotes](https://github.com/StarMoe-org/moenotes)：资料站与播放器集成。附件记录的移植参考快照是 `6b661881cf71bd63ecdd09fb544956120ca9ef0c`，入口包括 `src/components/tools/StoryStage.tsx`、`src/lib/story/player-data.ts`、`patches/ournotes-player@0.1.6.patch`。
- [nnnotes](https://github.com/MetaSekaiLab/nnnotes)：数据导出工具。附件记录的参考快照是 `1bdbd640755e4f49e0ab302c74c1ed871aef7f70`。`src/nnnotes/advcommand.py` 解释指令编号，`advscene.py`、`story.py`、`storyvoices.py`、`crilips.py` 解释资源组织。
- [ournotes-player](https://github.com/empty-sekai/ournotes-player)：原生移植基线为 0.1.6，主要参考 `src/story/` 和 `src/live2d/`。其 JavaScript 不参与本项目构建或运行。

原 `legacy/millsage-story-archive` 的发现、下载、索引和 Director Pack 流程已由 `src/OurNotes.Director.Core/Archiving` 接管。仓库无需保留 Node 项目及其 node_modules；旧归档数据可以独立保留。

## 主页 Spine 参考

本次主页移植另行固定了下列源码快照，不改写上面的历史剧情基线：

- [ournotes-player `9c1033a060c5dc5c169cedab6627b1b6b932c830`](https://github.com/empty-sekai/ournotes-player/tree/9c1033a060c5dc5c169cedab6627b1b6b932c830/src/story/simple/home)：`src/story/simple/home/` 的相机、Spine、房间、材质与启用的 HomeView volume 行为。JavaScript 仅作为移植参考，不在 CLI 中执行。
- [nnnotes `d34b9c7fae623b4528169f53cdfe373009f76e32`](https://github.com/MetaSekaiLab/nnnotes/tree/d34b9c7fae623b4528169f53cdfe373009f76e32/src/nnnotes)：`spot.py`、`room.py` 和 `storyhost.py` 的资源组织、prefab 层级与原始组件参数。
- [Spine C# runtime `e7dc1435fa4a0083ab431f1b28e083c14a1f5c68`](https://github.com/EsotericSoftware/spine-runtimes/tree/e7dc1435fa4a0083ab431f1b28e083c14a1f5c68/spine-csharp/src)：支持 Spine 4.2 数据，作为单独项目编译进 Native AOT 程序；本地适配及独立许可证见 [ORIGIN.md](third_party/Spine.Runtime/ORIGIN.md)。

直接演出输入仍是归档 story manifest 中的 `host/host.json`、`host/spot/spot.json`、Spine atlas／骨架／纹理和 room 资源。`home show` 返回具体站点、spot ID、来源剧情和 manifest，并显示共享骨架的角色拆分信息。此路径支持主页场景与动画，不表示已经实现 SimpleADV 的点击对话、结算或相应 UI 指令；也不保证任意 Unity 组件与特殊混合层能逐像素复现。使用方式见 [HOME_SPINE.md](HOME_SPINE.md)。

## 供应商文档

- [Native AOT](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/) 与[跨平台编译限制](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/cross-compile)。
- [Live2D Cubism Core API](https://docs.live2d.com/en/cubism-sdk-manual/cubism-core-api-reference/)。
- [Live2D MotionSync 平台支持](https://docs.live2d.com/en/cubism-sdk-manual/motionsync-plugin-platform/)；Linux 的 CRI Lips 与 Windows 的 MotionSync 不保证产生一致的音素嘴型。
- [FFmpeg 命令行](https://ffmpeg.org/ffmpeg.html)。
- [FFmpeg 8.0 CineForm 编码器源码](https://ffmpeg.org/doxygen/8.0/cfhdenc_8c_source.html)：`cfhd` 支持 GBRAP 12-bit 和 `film1`、`high` 等专用质量名称；本项目媒体源码固定为 8.0.3，见 `scripts/media-sources.lock`。
- [Adobe CC 2014.1 原生 CineForm 说明](https://blog.adobe.com/en/publish/2014/09/07/after-effects-cc-2014-1-13-1)：Premiere Pro、After Effects、Media Encoder 的 Windows／macOS 原生 CineForm MOV 编解码和 alpha 支持，早于 PR 2015.0。
- [Adobe Media Encoder CC 2015 手册](https://helpx.adobe.com/archive/en/media-encoder/cc/2015/mediaencoder_reference.pdf)：文档第 32–35 页说明 RGBA 4:4:4:4、12-bit、RGB + alpha 预设及宽 16／高 8 的尺寸要求。它们是本项目选择 CineForm 透明 MOV 的依据；FFmpeg 往返和发布包测试不等同于 PR 2015.0 9.0.0 (247) 实机导入验证，详见 [CINEFORM.md](CINEFORM.md)。

这些是理解行为的参考，不是额外的运行后端。当前功能范围见 [使用指南](USER_GUIDE.md) 与实际运行结果；首帧成功或社区播放器的能力不能单独证明本原生实现已经完整复刻。
