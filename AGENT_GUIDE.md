# 项目入口

Windows 和 Linux 共用 `src/OurNotes.Director.Core` 与 `src/OurNotes.Director.Cli`。不要创建另一份平台源码或运行时 Node 项目。编译／测试使用 .NET 10；正式产物用 Native AOT 在对应平台发布，见 [README](README.md#构建与验收) 和 `.github/workflows/build.yml`。

归档由一个 `archive` 命令完成，内部包括发现、下载校验、索引与 Director Pack。新增可序列化输出使用 `JsonObject` / `JsonNode`，或显式 `JsonOutput.Object` 和已注册的 DTO；避免匿名 CLR 对象的反射序列化。原始 manifest 与数值 token 必须无损保留。修改归档时保持已有目录、共享素材和中断后的复用行为。

完整使用方式见 [USER_GUIDE.md](USER_GUIDE.md)，剧本格式见 [UNIFIED_INTERFACE.md](UNIFIED_INTERFACE.md)，来源见 [SOURCE_REFERENCES.md](SOURCE_REFERENCES.md)。新项目统一使用 `render`，原游戏指令与四组 Director 扩展共用原生执行器。

默认核心测试和 HTTP fixture 回归不需要真实游戏素材或 Cubism SDK。真实素材验收脚本依赖用户单独提供的归档、SDK 和 FFmpeg，其中部分数量断言绑定旧快照；不要把它们误当作每日最新上游的固定数量。编译、初始化或首帧成功不代表全片视觉一致。

只清理本次创建的临时产物，保留源素材、用户 SDK 和最终输出。
