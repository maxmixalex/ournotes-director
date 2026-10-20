# 2026-10-20 — 独立 Live2D 与暂停 GPU 呈现续接

基线：PR #5 / feat/windows-creative-editor，24b63b6。

## 已定位的问题

- 上一轮 ActorCanvasTests 的失败来自 SetMotion 将 Time 归零，不是 FromSceneActor。动作替换保留播放头，原画布回归断言不放宽。
- 首次预览使用 selectedMotionOnly，但实时参数提交及完整动作画布分析没有使用它。提交后重新播放会重放不同的项目。
- 原先“单动作”编译仅删除原剧情 In/Motion/Expression，可能连人物的唯一显示指令一起删除，仍留下 Out、换装与其他人物指令。
- 清除表情没有更新 Motion 中内嵌的 ExpressionName；旧表情可能在重新播放时回来。
- 暂停 GPU 缩放仅 SetMatrixTransform，没有后续 Present；此前加入的 Seek 同一时间补刷新又会和用户定位、实时编辑竞争。

## 修改

独立工作台统一生成一个可见的 Character/Director.Actor，只有一条 Motion 和一条 Expression。保留原归档作为素材来源，但不执行原剧情的初始化、退场、换装和其他人物轨道。完整原项目仍保存于 performance，恢复路径 ToSceneDocument(false) 保留原指令与数值 token。没有精确 AtFrame 的旧顺序演出仍按原路径只读打开，不猜测时间。

首次加载、实时提交、画布分析和导出使用同一个单动作编译实现。选项是全局属性，不是新关键帧；选择和恢复默认不移动播放头，表情与 Motion 内嵌字段一起更新。旧项目中的多条动作选择按最后一次编辑投影为单一选择。

GPU 桥 ABI 更新为 3。显示尺寸变化时在现有 surface gate 内更新矩阵、从保留纹理复制到交换链并 Present。该操作只涉及 D3D，不调用 WGL、不推进模型、不请求剧情 seek。移除全屏回调中额外的 seek。

## 验证设计与边界

新增管理层回归覆盖唯一选择、默认表情、回退播放头、参数编辑帧、撤销重做、旧项目投影、原始演出不变和真实显示指令。ActorCanvasTests 保持原有断言。

新增实窗检查：选择非默认动作和表情，实时参数提交后重新求值同一帧，比较完整像素哈希；往返定位后比较画面及项目内容。证据文件为 live2d-selection-consistency.json。

WARP 检查同时读取真实 Present 计数和保留像素，要求暂停缩放连续三次都有实际提交。它验证交换链操作，不证明物理双显卡互操作。

剧情分析增加完整渲染器与 timingOnly 的逐指令时序对照，避免串行与并行两条 timingOnly 路径共同出错却互相通过。既有耗时对照与取消检查不改门槛。原剧情完整导入耗时仍以新的 story-import.json 为准，不能把单阶段或历史测量当作本版总加载提速。

本记录写入时仅完成代码审查和 git diff --check；构建、原生与 Windows 实窗结果以该次实际 CI 为准。
