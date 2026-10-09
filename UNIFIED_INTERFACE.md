# 统一剧本接口 v1

状态：统一原生执行路径已接通。original.json、free-character.json、mixed.json、millsage-demo.json 均可运行；四组 Director 指令、无留边、透明输出、旧 Shot 适配、sequence/resume 已实现。旧 Shot 经 ShotProject 转换后也进入 AdvStoryRenderer，不再通过旧 Renderer 输出。旧渲染类暂保留给内部回归和素材图集。

## 唯一用户入口

命令：

```text
advkit render validate project.json
advkit render state project.json --time 4
advkit render frame project.json --time 4 -o frame.png
advkit render contact project.json --times 0,2,4 -o contact.png
advkit render clip project.json --until-end --duration 60 -o clip.mp4
advkit render sequence project.json -o frames --resume
```

`render` 名称沿用，但新格式统一进入原生剧情执行器。旧 Shot 输入通过适配器转换，同样进入该执行器。不能只把两种旧后端藏在同一命令下面。

validate 检查项目结构并执行资源初始化／首帧，不是全时间线验证。Director 字段和参数名会预检，资源仍可能在对应命令执行时报告缺失。

原游戏指令不变。已有 `story` 命令保持兼容，与统一入口使用同一 AdvStoryRenderer。

## 项目容器

`format: "ournotes-director/project"` 与 `version: 1` 明确区分旧 Shot。

- `source.adv`：基础场景／资源来源，不表示只能回放这集剧情。统一项目目前必填；旧 Shot 适配默认以归档 10060 提供基础场景。Character 可用 Model 直接引用归档模型 ID，解除原剧情角色列表限制。
- `source.site` 默认 intl，`source.language` 默认 zh-Hans。归档根目录沿用 CLI/environment 解析，不写死在项目内。
- `initialize`、`commands`、`finalize`：省略时使用原剧情相应段；明确 `[]` 清空。原有游戏行的 cmd 和参数字段不改名。
- `text`：合并原文本表，沿用现有多语言格式。
- `output`：width、height、supersample、codec、clean、letterbox。命令行显式选项优先，其次项目配置，再其次默认值。
- `output.letterbox`：`game` 保留原比例；`none` 使用整个输出画布，透明角色项目不强制加黑边。
- 项目文件中的本地素材路径相对项目目录解析，输出命令路径相对当前工作目录解析。

未来版本字段不静默接受；扩展行严格校验。原游戏行保留原数据字段，不将原始引擎未消费的字段误报为新接口错误。

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

| 指令 | Values 字段及单位 |
|---|---|
| Director.Actor | screen 的 position 为 [x,y]，world 为 [x,y,z]；rotation 为 [x,y,z] 度；scale 为三轴倍率；height 为模型画布高度／输出高度（默认 .95）；opacity 为 0–1；tint 为 RGBA 四数；brightness 为非负倍率；visible 为布尔；order 为整数，越大越晚绘制 |
| Director.Camera | position 为世界 XYZ，rotation 为角度 XYZ，fieldOfView 为 (0,180) 度；framing 为 {x,y,zoom,roll}，默认 {x:0,y:0,zoom:1,roll:0}，x/y 为相对画面宽高的平移量，仅控制独立 screen 角色和背景图片 |
| Director.Scene | background 为 [r,g,b,a]（0–1）；stageVisible:false 隐藏舞台，恢复用原 Stage；backgroundImage 为 {path:"relative.png"} 或 {adv:"10060",logicalPath:"归档路径"}，加载后隐藏舞台；grade 为 {exposure,contrast,saturation,vignette}，默认 {exposure:0,contrast:1,saturation:1,vignette:0} |

`Director.Actor` 的 `animation:"game"` 为默认游戏动作时序；`animation:"director"` 使用简单动作循环／末帧保持，`loop` 仅在此模式生效。`speed` 控制动作播放倍率；`physics`、`breath`、`blink` 控制自动行为。原游戏参数循环仍用 `MotionLoop`。布尔不写则保留当前设置。角色从自由摆放重新 `In` 时恢复游戏基础变换和游戏动画时序，显式参数覆盖持续到 release。

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