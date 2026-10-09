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

部分特效样例依赖原开发工作区中另行准备的 `references/effect-samples` 归档，例如剧情 10020；默认 millsage 归档不保证包含它们。

## 社区实现

- [Moenotes](https://github.com/StarMoe-org/moenotes)：资料站与播放器集成。附件记录的移植参考快照是 `6b661881cf71bd63ecdd09fb544956120ca9ef0c`，入口包括 `src/components/tools/StoryStage.tsx`、`src/lib/story/player-data.ts`、`patches/ournotes-player@0.1.6.patch`。
- [nnnotes](https://github.com/MetaSekaiLab/nnnotes)：数据导出工具。附件记录的参考快照是 `1bdbd640755e4f49e0ab302c74c1ed871aef7f70`。`src/nnnotes/advcommand.py` 解释指令编号，`advscene.py`、`story.py`、`storyvoices.py`、`crilips.py` 解释资源组织。
- [ournotes-player](https://github.com/empty-sekai/ournotes-player)：原生移植基线为 0.1.6，主要参考 `src/story/` 和 `src/live2d/`。其 JavaScript 不参与本项目构建或运行。

原 `legacy/millsage-story-archive` 的发现、下载、索引和 Director Pack 流程已由 `src/OurNotes.Director.Core/Archiving` 接管。仓库无需保留 Node 项目及其 node_modules；旧归档数据可以独立保留。

## 供应商文档

- [Native AOT](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/) 与[跨平台编译限制](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/cross-compile)。
- [Live2D Cubism Core API](https://docs.live2d.com/en/cubism-sdk-manual/cubism-core-api-reference/)。
- [Live2D MotionSync 平台支持](https://docs.live2d.com/en/cubism-sdk-manual/motionsync-plugin-platform/)；Linux 的 CRI Lips 与 Windows 的 MotionSync 不保证产生一致的音素嘴型。
- [FFmpeg 命令行](https://ffmpeg.org/ffmpeg.html)。

这些是理解行为的参考，不是额外的运行后端。当前功能范围见 [使用指南](USER_GUIDE.md) 与实际运行结果；首帧成功或社区播放器的能力不能单独证明本原生实现已经完整复刻。
