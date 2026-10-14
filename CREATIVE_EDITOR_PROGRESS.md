# Windows 创作编辑器重构：实际状态

状态：开发中，不能作为可运行新版交付。主分支尚未替换。用户明确要求 Windows，不扩展或交付 Linux 产品。

## 交互方向

主要工作流是空白画布 → 添加角色 → 在常驻素材栏选择动作、表情或声音 → 拖入角色轨道编排 → 导出。角色、动作、表情、语音与位置片段是编辑模型；原生指令是编译结果和高级兼容入口，不能反过来成为默认创作 UI。

具体参考：

- Live2D Cubism 官方音频工作流：音频进入时间轴、指定模型、由音频生成场景时长。https://docs.live2d.com/en/cubism-editor-manual/generating-scene-from-audio-file/
- Live2D 时间轴与音轨/模型关联：https://docs.live2d.com/en/cubism-editor-manual/timelinepalatte/
- Godot Animation track types：动画、属性与音频轨道各有自己的操作语义。https://docs.godotengine.org/en/stable/tutorials/animation/animation_track_types.html
- Godot 官方音轨编辑设计：波形、音频入出点、定位时从正确声音位置开始。https://godotengine.org/article/godot-gets-brand-new-animation-editor-cinematic-support/

不声称已经完整复制这些产品的功能或完成 UX 验收。

## 已保存的代码

- `CreativeScene.cs`：独立的 `ournotes-director/scene` 文档，角色和有明确开始时间的片段；编译成原生项目，保留原始项目副本。
- `ClipTimeline.cs` 与 `CreativeScenePage*.cs`：独立角色/素材栏、片段拖放、语音波形、选择关联检查器、JSON 入口、当前角色导出。尚未替换 MainWindow 的默认 SceneStudioPage。
- `PreviewSoundtrack.cs`：使用同一原生执行器生成场景 PCM，独立于绘图线程，提供可定位的音频缓存。
- `WindowsPreviewAudio.cs` / `NativeViewport.Audio.cs`：Windows waveOut 设备输出、暂停、定位、音量及已消费样本时钟；绘图跟随声音时间，音频错误明确显示。
- `Actor.LipSync.cs` / `NativeMotionSync.cs`：取消统一强加的最低开口量和额外平滑，保留模型映射；足够完整音频块立即分析；新对白重建原生分析状态。
- `AdvUi.ClipVoice.cs`：独立角色声音所有权的实现草稿，尚未接入现有 Voice 分发。

## 已知未完成事项，继续前必须处理

1. Windows 集成运行 38580372158 在核心编译阶段失败：CreativeScene 的三处 `JsonArray.Add(JsonObject)` 选择了不支持 AOT 的泛型重载。没有通过编译，不得标记为完成。
2. 新 UI 文件保存于提交 `db1b4a4b92098d35ba030dc68b18019a62bcab22`，尚未编译或接入主窗口。Inspector 的 `Easing` 字段应按引擎实际 `Ease` 枚举修正；页面再次 Loaded 不应清除已加载画布。
3. 原剧情导入仍保留了 minimumDuration；应替换为可见可编辑的结束停留，不能重新引入用户反对的隐藏固定时长。
4. 动作片段的结束/保持语义必须落实到渲染，不允许只缩短色块但动作继续播放；表情保持到下一表情也必须在时间轴中明确呈现。
5. 声音片段重叠时的独立所有权分发、裁剪边界、文件波形入点、暂停恢复的实际音频时钟仍需完成。
6. 主构建工作流还未完成 Windows-only 改动；本轮没有启动 Linux 构建。保留旧源码不等于继续开发 Linux 产品。
7. 实际 Windows 音频设备播放、声音与嘴形的观看/听音验收、完整创作流程和新 UI 截图均未完成。不能用参数变化或编译通过代替这些验收。
8. 主页工作台目前仍是旧实现。不能将本轮场景编辑器代码称为整个应用重新设计完成。

## 工具状态

一组后续集成脚本的 create_blob 请求被工具安全层拦截，未写入。没有执行或换途径执行该脚本。随后仅保存了此前已经获准创建的独立源码 blob 和本说明，主分支没有改动。临时 creative-integration.yml 工作流仍在开发分支，旧集成脚本已删除；不要直接重跑该旧工作流。
