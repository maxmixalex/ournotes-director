# 统一剧本接口 v1

状态：统一原生执行路径已接通。original.json、free-character.json、mixed.json、millsage-demo.json 均可运行；四组 Director 指令、无留边、透明输出、旧 Shot 适配、sequence/resume 已实现。旧 Shot 经 ShotProject 转换后也进入 AdvStoryRenderer，不再通过旧 Renderer 输出。旧渲染类暂保留给内部回归和素材图集。

## 统一项目入口

命令：

```text
advkit render validate project.json
advkit render state project.json --time 4
advkit render frame project.json --time 4 -o frame.png
advkit render contact project.json --times 0,2,4 -o contact.png
advkit render clip project.json --until-end --duration 60 -o clip.mp4
advkit render clip project.json --duration 5 --codec cineform --cineform-quality film1 -o clip.mov
advkit render sequence project.json -o frames --resume
```

`render` 名称沿用，但新格式统一进入原生剧情执行器。旧 Shot 输入通过适配器转换，同样进入该执行器。不能只把两种旧后端藏在同一命令下面。

validate 检查项目结构并执行资源初始化／首帧，不是全时间线验证。Director 字段和参数名会预检，资源仍可能在对应命令执行时报告缺失。

原游戏指令不变。已有 `story` 命令保持兼容，与统一入口使用同一 AdvStoryRenderer。

主页 Spine 的原 prefab、房间和角色导出使用 `home` 命令，见 [HOME_SPINE.md](HOME_SPINE.md)。它与 `render`／`story` 共用视频编码配置，但不使用本节的剧情 project 容器，不把 Spine 动画改写成 Live2D 剧情指令。

## 项目容器

`format: "ournotes-director/project"` 与 `version: 1` 明确区分旧 Shot。

- `source.adv`：可选的原剧情来源；省略时使用素材库，不绑定任何原剧情。自由项目需要显式提供 `initialize`、`commands`、`finalize` 三个数组，空数组表示该段没有指令；只有指定真实原剧情时，省略段落才表示继承。编辑、预览和导出共用这条规则。旧 Shot 兼容格式省略来源时仍默认归档 10060，新建的自由 Shot 模板显式使用 `library`。Character 可用 Model 直接引用归档模型 ID。
- `source.site` 默认 intl，`source.language` 默认 zh-Hans。归档根目录沿用 CLI/environment 解析，不写死在项目内。
- `initialize`、`commands`、`finalize`：省略时使用原剧情相应段；明确 `[]` 清空。原有游戏行的 cmd 和参数字段不改名。
- `text`：合并原文本表，沿用现有多语言格式。
- `output`：width、height、supersample、codec、cineformQuality、clean、letterbox。命令行显式选项优先，其次项目配置，再其次默认值。
- `output.codec`：`h264`、`cineform`（别名 `cfhd`）、`prores` 或 `ffv1`。CineForm 输出为 RGB + alpha MOV；对应命令行 `--codec cineform`。
- `output.cineformQuality`：CineForm 的命名质量档位，默认 `film1`，更重视体积可用 `high`；对应命令行 `--cineform-quality`，完整档位见 [CINEFORM.md](CINEFORM.md)。
- `output.letterbox`：`game` 保留原比例；`none` 使用整个输出画布，透明角色项目不强制加黑边。
- `output.isolateActor`：要提取的角色标识。整个原生演出仍从开头执行，仅绘制此刻该角色的原始模型画布，输出透明背景。原剧情的动作历史、表情、语音口型、物理、淡入淡出与换装仍由同一执行器求值。
- `output.playbackViewport`：`{width,height,letterbox}` 保留源播放器的开场等待条件。隔离输出可更换画布与黑边显示，但不能因此提前原演出。由场景或 Live2D 工作台导出时自动写入。
- `output.isolateTransform`：仅与 `isolateActor` 一起使用，形如 `{x:0.5,y:0.5,scale:1,rotation:0,opacity:1}`。x/y 是模型画布中心相对于输出宽高的位置，y 向下；scale 为相对于原始模型画布适配尺寸的倍率，rotation 为绕画布中心的角度，opacity 为 0–1。默认值保留原来的独立角色画面；这些字段直接影响原生几何、合成透明度与预览命中范围。它们不修改源舞台位置、摄影机或动作时间。
- `output.isolateCanvas`：仅与 `isolateActor` 一起使用，固定画布形如 `{referenceWidth:2048,referenceHeight:3072,x:0.1,y:0.05,width:0.8,height:0.9}`。referenceWidth/referenceHeight 保存应用画布前的原输出尺寸；x/y/width/height 是该原画布的归一化取景矩形，可以延伸到 0–1 以外。先在原画布求值 isolateTransform，再使用这一个固定矩形绘制所有帧，输出尺寸变化时保持人物比例。工作台逐帧合并完整演出中可见角色的真实几何范围，增加边缘采样余量，再按倍率扩展宽高；倍率 1 为完整动作最小范围，倍率大于 1 只增加透明留白。视频尺寸对齐只向外增加透明边缘。再次分析仍使用 referenceWidth/referenceHeight，不在已有裁剪上继续累积变换。PNG、帧序列与 MOV 直接共用相同字段；源指令、声音、开场等待及导出时间不改变。
- 项目文件中的本地素材路径相对项目目录解析，输出命令路径相对当前工作目录解析。

未来版本字段不静默接受；扩展行严格校验。原游戏行保留原数据字段，不将原始引擎未消费的字段误报为新接口错误。

透明 CineForm 项目的输出部分可以写为：

```json
{
  "output": {
    "width": 1920,
    "height": 1080,
    "supersample": 2,
    "codec": "cineform",
    "cineformQuality": "film1",
    "letterbox": "none"
  }
}
```

场景仍需使用 `Director.Scene` 设置透明背景并隐藏舞台；选择编码不会自动抠图。CineForm 要求宽度为 16 的倍数、高度为 8 的倍数且至少 32，无效尺寸明确拒绝，不自动改变取景。画面以 RGBA8 straight alpha 送入 12-bit 编码格式；CineForm 是有损中间文件，保留精确像素可使用 PNG 或 FFV1。Premiere Pro 2015 的格式支持依据和测试界限见 [CINEFORM.md](CINEFORM.md)。

## 必要扩展：四组指令

命名空间 `Director.` 表示工具扩展，不占用原游戏命令编号。无表达式、变量、循环语法或插件脚本。复用原序列、Duration、IsNoWait 和缓动调度。

| cmd | 作用 | 核心字段 |
|---|---|---|
| Director.Actor | 脱离槽位的角色显示、变换、外观 | TargetName、Space、Values、Duration、Ease、IsNoWait |
| Director.Camera | 显式镜头位置、旋转、视角 | Values、Duration、Ease、IsNoWait |
| Director.Parameter | 已有模型参数的数值覆盖及释放 | TargetName、Parameter、Value、Operation、Duration、Ease、IsNoWait |
| Director.Scene | 背景、舞台可见性及导演调色 | Values、Duration、Ease、IsNoWait |

Actor.Values 使用 position、rotation、scale、opacity、visible、order 等明确字段；同时支持 tint、brightness、speed、loop、blink、breath、physics、warmupFrames（0–300）、animation（game/director）。不设计一个可以修改任意 C# 对象属性的通用指令。

### 坐标

- Actor.Space=`world`：位置使用游戏场景单位，rotation 为角度，scale 为倍率；相机影响此角色。
- Actor.Space=`screen`：位置 x/y 为画面归一化坐标，原点左上，height 为模型 canvas 相对画面高度；独立二维素材构图不随游戏相机移动。
- 原 In/Focus/Move 等指令继续使用原槽位语义。Director.Actor 将角色切到独立摆放；重新使用 In 可回到游戏槽位。
- Camera 使用原世界坐标与角度；支持显式 position、rotation、fieldOfView，以及旧二维 camera 构图的独立 framing 配置，不混用两类单位。

### 冲突与释放

- 原游戏模式未使用 Director 指令时，维持当前相位和行为。
- 同一目标／属性的新动画替换该属性的旧动画，不取消其他属性。
- Parameter.Operation=`set` 的值在动作、表情、口型与物理求值后作为最终覆盖，持续到 `release`；release 回到当帧原生求值，而非写回过时的旧数值。
- 只覆盖明确指定的参数。手动头部参数不应关掉嘴型；手动嘴部参数则按明确覆盖语义优先。
- 标量及向量关键帧通过同一类指令的连续段表达。旧 Shot 的 linear/smoothstep/step 轨道由适配器转换，不再要求用户学习第二套轨道系统。

## 旧 render 能力迁移表

| 旧能力 | 统一路径 | 阶段 |
|---|---|---|
| 原动作、表情、动作切换 | 原 Character/Motion/Expression，加模型 ID 资源适配 | 2–3 |
| 自由摆放、旋转、尺寸、透明度、多角色 | Director.Actor；独立于槽位的角色集合 | 2 |
| 模型参数、参数轨道 | Director.Parameter；覆盖／释放 | 2–3 |
| camera x/y/zoom/roll | Director.Camera 的 framing 配置 | 2–3 |
| 透明背景及完整画布 | Director.Scene + output.letterbox | 2 |
| stage、backgroundImage | 原 Stage 或 Director.Scene；后者支持本地图片 | 3 |
| exposure/contrast/saturation/vignette | Director.Scene 的 grade；与游戏 PostEffect 分清叠加次序 | 3 |
| tint、brightness、speed、loop、blink、breath、physics | Director.Actor 明确选项，原指令可表达者优先沿用 | 3 |
| seed/warmup、重复输出 | 统一模拟设置；游戏默认行为与旧 Shot 适配值分别明确 | 3 |
| PNG、图集、视频、帧序列、resume | 同一输出层 | 3 |
| 来源／输出元数据 | 统一元数据，保留依赖和可复现信息 | 3 |

## 实施顺序

四组扩展、旧 Shot 适配与帧序列已接通统一执行器。使用方式及边界见 USER_GUIDE.md。原游戏指令保留归档语义，扩展字段严格校验；不要把可识别指令等同于所有素材变体均已验证。

## 可直接编写的字段

所有数值动画使用秒为单位的 `Duration`；默认 0（立即设置）。`IsNoWait:1` 并行执行，默认等待动画结束。`Ease` 默认 `Linear`，常用 `InOutSine`、`InOutQuad`。布尔、资源选择和排序立即生效，不做渐变。

需要精确对齐场景工作台的 30 fps 时间轴时，`Delay` 还接受整数 `Frames`：`{"cmd":"Delay","Frames":30}` 等待 30 帧，`Frames:0` 不消耗一帧。不能将 `Frames` 与非零 `Duration` 混用；负数、小数和空值都会报错。已有 `Delay.Duration` 的执行语义不变。

工作台保存的项目仍直接由原生执行器读取。编辑器会在允许的 `Key` 字段中保留可编辑关键帧标记，并在需要的位置生成逐帧即时指令；重新打开时恢复精简的时间轴。请保留这些标记与生成指令的对应关系。高级 JSON 修改了生成的中间帧时，编辑器会保留修改，并将无法安全还原的视觉轨道显示为只读。

对 `Key` 以 `ournotes-studio/1/key/` 开头的合法稀疏关键帧，可以修改其 `Values`，或动作 / 表情对应的名称字段。桌面工作台应用、重新打开或替换 JSON 时，会在确认时序与标记完整后重新生成派生中间帧；未修改的项目保留原始文本。重新生成过的项目需要保存，CLI 才会读到新的原生指令。关键帧时刻与插值优先通过视觉时间轴调整；不要只改 `Key` 中的帧数字，也不要手动删除生成的等待、时长或原始时序标记。

| 指令 | Values 字段及单位 |
|---|---|
| Director.Actor | screen 的 position 为 [x,y]，world 为 [x,y,z]；rotation 为 [x,y,z] 度；scale 为三轴倍率；height 为模型画布高度／输出高度（默认 .95）；opacity 为 0–1；tint 为 RGBA 四数；brightness 为非负倍率；visible 为布尔；order 为整数，越大越晚绘制 |
| Director.Camera | position 为世界 XYZ，rotation 为角度 XYZ，fieldOfView 为 (0,180) 度；framing 为 {x,y,zoom,roll}，默认 {x:0,y:0,zoom:1,roll:0}，x/y 为相对画面宽高的平移量，仅控制独立 screen 角色和背景图片 |
| Director.Scene | background 为 [r,g,b,a]（0–1）；stageVisible:false 隐藏舞台，恢复用原 Stage；backgroundImage 为 {path:"relative.png"} 或 {adv:"10060",logicalPath:"归档路径"}，加载后隐藏舞台；grade 为 {exposure,contrast,saturation,vignette}，默认 {exposure:0,contrast:1,saturation:1,vignette:0} |

`Director.Actor` 的 `animation:"game"` 为默认游戏动作时序；`animation:"director"` 使用简单动作循环／末帧保持，`loop` 仅在此模式生效。`speed` 控制动作播放倍率；`physics`、`breath`、`blink` 控制自动行为。原游戏参数循环仍用 `MotionLoop`。布尔不写则保留当前设置。角色从自由摆放重新 `In` 时恢复游戏基础变换和游戏动画时序，显式参数覆盖持续到 release。

要在原剧情角色上调整自动控制器而保留其舞台槽位、当前动作和原生混合历史，使用独立的 `controls` 对象：

```json
{"cmd":"Director.Actor","TargetName":"hotaru","Values":{"controls":{"physics":true,"breath":false,"blink":true,"loop":false}},"Duration":0}
```

`controls` 仅允许 physics、breath、blink、loop 四个布尔；省略项保留当前值。不能与其他 `Values` 字段或 `Space` 混用，`Duration` 必须为 0。显式 `controls.loop` 在游戏动画模式中通过原生动作混合器开启或停止当前动作的自动重启；未使用此扩展时，仍保持原作仅自动重启默认动作的行为。它不会切换动画采样器，`MotionLoop` 的原游戏参数循环仍独立存在。

```json
{"cmd":"Director.Parameter","TargetName":"hotaru","Parameter":"ParamEyeaHL","Value":0}
```

以上保留当前表情并恢复此模型的瞳孔高光。需要交还表情控制时：

```json
{"cmd":"Director.Parameter","TargetName":"hotaru","Parameter":"ParamEyeaHL","Operation":"release"}
```

参数属于模型本身，先查询再使用，不同模型不保证同名参数：

```powershell
./advkit.exe model parameters adv_live2d_hotaru_016_casual_spring_01
./advkit.exe expression show adv_live2d_hotaru_016_casual_spring_01 exp_smile07
```

返回原始参数名、数值范围及表情的 add/multiply/overwrite 操作。名称保留素材原文，不自动猜测情绪。需要预览可用 `model gallery`，最终效果以统一项目抽帧为准。

Windows Live2D 工作台保存 `ournotes-director/live2d` 文档，原演出与编辑指令保持独立，并在预览及加入普通导出队列前编译成上述统一项目。动作、表情和参数编辑写入当前 30 fps 帧号；参数“恢复自动”生成 `Operation:release` 并保留之前的编辑历史。播放头保存在文档中，但仅移动播放头不将项目标记为已修改。原场景的本地图片与音频在送入独立文档时解析为完整路径，保存到其他目录或排队执行仍使用同一资源。

预览参数读数来自已显示模型的 Cubism Core 参数缓冲及真实最小值、最大值、默认值。暂停时的参数设置／释放与隔离变换使用现有原生渲染线程重绘，保持当前时间与模型实例；保存后的统一项目重新播放同一原生命令。读数本身不会自动变成参数覆盖或关键帧。
