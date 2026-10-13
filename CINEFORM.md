# CineForm 透明 MOV 导出

`--codec cineform` 使用 GoPro CineForm 的 RGB + alpha 编码，将画面和透明通道一起写入 `.mov`。发布包自带所需 FFmpeg 编码器，导出时不需要另装 Adobe 软件或系统视频编码器。统一项目、剧情、主页渲染及 `media clip` 共用这一编码配置。

默认质量为 `film1`，用于兼顾后期画质与文件体积；更重视体积可选 `high`。输出是有损中间文件，颜色和 alpha 都可能有小幅量化误差。需要精确保存每个 RGBA8 像素时，可使用 PNG 序列或 FFV1。

## 导出示例

以下使用 Windows 可执行文件名；Linux 将 `./advkit.exe` 换成 `./advkit`。

```powershell
# 透明角色项目，默认 film1 质量
./advkit.exe render clip samples/unified/free-character.json --duration 5 --codec cineform -o output/character.mov

# 同一项目使用体积更小的 high 档位
./advkit.exe render clip samples/unified/free-character.json --duration 5 --codec cineform --cineform-quality high -o output/character-small.mov

# 剧情画面也可以输出 CineForm MOV
./advkit.exe story clip 10060 --duration 10 --codec cineform -o output/story.mov

# 将已有透明视频的指定片段转为 CineForm，保留源帧率，不带音频
./advkit.exe media clip output/transparent.mkv --start 0 --duration 5 --codec cineform -o output/plate.mov

# 给 CineForm 添加声音和可选字幕，直接复制视频流
./advkit.exe media mux output/character.mov --audio dialogue.wav --subtitles dialogue.srt -o output/character-with-audio.mov
```

透明程度由场景决定。统一项目的透明角色设置包括 `Director.Scene.Values.background=[0,0,0,0]`、`stageVisible=false` 和 `output.letterbox="none"`。主页使用 `home clip|export`，默认采用相同的 CineForm film1 配置；单角色自动透明，整组 Spine 可加 `--no-background`。完整主页、每个角色的选择和导出操作见 [HOME_SPINE.md](HOME_SPINE.md)。

`media mux` 的 MOV 输出使用 PCM 24-bit 音频；音频过短补静音，过长按视频时长裁切，字幕写为可开关的 `mov_text` 轨道。视频不重新编码，透明通道随原 CineForm 视频流保留。`media clip` 的默认编码仍为 ProRes HQ，需要显式选择 `--codec cineform` 才使用此格式。

### 在项目中保存设置

将下列字段合并进统一项目的 `output` 对象：

```json
{
  "width": 1920,
  "height": 1080,
  "supersample": 2,
  "codec": "cineform",
  "cineformQuality": "film1",
  "letterbox": "none"
}
```

命令行显式选项优先于项目字段，项目字段优先于默认值。`cfhd` 可作为 `cineform` 的别名；`--cineform-quality` 与项目字段 `cineformQuality` 使用相同的档位名称。

## 质量与尺寸

| 设置 | 用途 |
|---|---|
| `film1` | 默认；后期剪辑、透明人物边缘和一般动画素材 |
| `high` | 更重视体积；正式使用前检查发丝、线条、柔和阴影和半透明边缘 |
| `film2` | 更重视细节；文件通常更大 |

完整可用名称为 `film3+`、`film3`、`film2+`、`film2`、`film1.5`、`film1+`、`film1`、`high+`、`high`、`medium+`、`medium`、`low+`、`low`，按质量从高到低排列。FFmpeg 的 cfhd 专用 `-quality` 数字值由 0 到 12，数字越大质量越低；Adobe 的质量滑块采用不同的刻度。命令使用名称，避免把它当作 CRF、通用 `-q:v` 或 Adobe 滑块数值。[FFmpeg 编码器源码](https://ffmpeg.org/doxygen/8.0/cfhdenc_8c_source.html)给出了这些名称和像素格式。

CineForm 按质量压缩，不承诺固定码率。片长、尺寸、帧率、画面纹理和透明边缘都会影响体积。12-bit RGB + alpha 是编码格式；当前 RGBA8 渲染源不会因此增加原本没有的颜色细节。

### 本次完整动画的体积与误差

本地 Linux 检查使用发布配置的 **FFmpeg 8.0.3**，将同一份渲染器直接输出的 RGBA8 PNG 序列分别编码再完整解码。素材为本次归档的 spot `40005`、来源剧情 `10868`、角色 `hotaru`、Spine `4.2.43` 的 `home_start`：4 秒非循环动画加结束帧，共 **121 帧、30 fps、1920×1080、2 倍超采样、fit 构图、透明背景**，不套全局后处理。

下表的平均绝对误差仅统计**源 alpha 大于 0** 的像素，单位为 0–255 的一个 8-bit 级。预乘 RGB 逐通道按 `RGB × alpha / 255` 计算后再比较，用来反映画面合成时的颜色误差；大片透明背景不计入这些均值。

| 格式／档位 | 文件体积 | alpha 平均误差 | 预乘 RGB 平均误差 |
|---|---:|---:|---:|
| CineForm `film1` | 73.01 MiB | 0.163 | 0.432 |
| CineForm `high` | 67.66 MiB | 0.195 | 0.540 |
| ProRes 4444 | 69.39 MiB | 0.170 | 0.122 |

本样本中，`high` 比 `film1` 小 **7.33%**，代价是更高的颜色和 alpha 误差。`film1` 的预乘 RGB 平均误差低于半个 8-bit 级，因此保留为默认平衡档位。CineForm 的体积不一定小于 ProRes：本例 film1 稍大，high 稍小；其他角色、背景和动画的结果会不同。

半透明区域（源 alpha 为 1–254）的 alpha 平均误差分别为 film1 **0.475**、high **0.567**、ProRes **0.495**；最大 alpha 误差分别为 **6、8、1**。另外单独检查了全片 **216,127,003 个源 alpha=0 像素**，两档 CineForm 解码后均保持为 0；其余 alpha 值仍可能有量化误差。

可用以下命令准备同样条件的序列；需要归档中包含对应素材：

```powershell
./advkit.exe home sequence 40005 --adv 10868 --character hotaru --fps 30 --width 1920 --height 1080 --supersample 2 -o output/quality-frames
./tools/ffmpeg.exe -framerate 30 -i output/quality-frames/frame-%06d.png -vf format=rgba64le -c:v cfhd -pix_fmt gbrap12le -quality film1 -tag:v CFHD -threads 1 output/quality-film1.mov
```

`high` 只替换 `-quality`；本次 ProRes 对照采用 `prores_ks`、`-profile:v 4`、`-pix_fmt yuva444p10le`、`-alpha_bits 16`、单编码线程。Linux 使用无 `.exe` 后缀的工具。CineForm 在编码前经 packed RGBA64 扩展位深，以保留全透明／全不透明端点；`advkit` 已自动使用这项转换，无需额外设置。

### 尺寸要求

为遵守旧版 Adobe 的 CineForm 尺寸要求，输出宽度必须是 **16 的倍数**，高度必须是 **8 的倍数且至少为 32**。显式输入的不支持尺寸会在导出前报错，并给出就近尺寸建议。工作台的「单人透明镜头／模型原始尺寸」会提前计算符合所选格式的画布，只增加必要的透明边距，保留模型原有像素尺寸。

原始模型画布可能达到 6000×9000。此类大画布按原始分辨率渲染，默认采用 1 倍采样；编码器会按画布像素数限制并发，降低同时处理多个巨大帧的内存占用。分辨率与质量档位不因此降低，导出速度会受可用内存和机器性能影响。

| 尺寸 | 是否可用 |
|---|---|
| 1920 × 1080 | 可用 |
| 640 × 360 | 可用 |
| 1440 × 2160 | 可用 |
| 1080 × 1920 | 宽度不符合要求，可显式改用 1088 × 1920 |

上述宽 16／高 8 的限制见 [Adobe Media Encoder CC 2015 官方手册](https://helpx.adobe.com/archive/en/media-encoder/cc/2015/mediaencoder_reference.pdf)，文档第 35 页；FFmpeg 编码器还要求高度至少为 32。

## Premiere Pro 2015.0 的支持依据

Adobe 在 [CC 2014.1 官方更新说明](https://blog.adobe.com/en/publish/2014/09/07/after-effects-cc-2014-1-13-1)中说明，Premiere Pro、After Effects 和 Media Encoder 可在 Windows／macOS 上原生编解码 CineForm MOV，并支持 alpha。该支持早于 Premiere Pro 2015.0。

[2015 官方手册](https://helpx.adobe.com/archive/en/media-encoder/cc/2015/mediaencoder_reference.pdf)的文档第 32–35 页进一步说明 CineForm 的 RGBA 4:4:4:4、12-bit 输出、RGB + alpha 预设及尺寸限制。这些资料是选择此格式的依据；本项目的自动化测试不包含 Premiere Pro 2015.0 9.0.0 (247) 本体，不能代替该版本的实际导入验证。

原始素材尺寸和 Premiere 序列尺寸应分别看待。[Premiere Pro CC 2015 手册](https://helpx.adobe.com/archive/en/premiere-pro/cc/2015/premiere_pro_reference.pdf)列出的素材导入上限是 256 百万像素、任一边不超过 32,768 像素，而序列上限是 10,240×8,192。6000×9000 原始角色素材处于前者的尺寸范围内；可以作为较小序列里的缩放、移动素材，但不能据此假定该版本能创建同样高度的序列。具体 CineForm 文件的导入仍须在目标 Premiere 版本确认。

本程序渲染的透明画面以 **straight alpha（非预乘）**送入编码器。验收时，将 MOV 放在有颜色或有纹理的图层上，检查透明区域、半透明区域和细线边缘；同时确认素材的 alpha 解释与 straight alpha 一致。普通播放器显示黑色背景不代表透明通道不存在。`media clip` 转换已有素材时保留源像素，源素材自身的 alpha 解释也需一致。

## 导出结构与自动化验收

```powershell
./advkit.exe media probe output/character.mov
```

CineForm 视频流应报告 `codec_name=cfhd`、`codec_tag_string=CFHD`、`pix_fmt=gbrap12le`，尺寸、帧数和时长应与渲染结果一致。`gbrap12le` 包含四个颜色／透明平面；只有这些元数据仍不足以证明透明像素正确。

`tests/package-smoke.py` 对最终解压的发布包执行真实编解码：

- 用透明和半透明 RGBA 图案编码六帧 CineForm，再完整解码，校验帧数、颜色与 alpha 误差。
- 检查半透明像素仍保留 straight RGB，避免引入黑色底色。
- 分别用 film1／high 检查整块全透明和全不透明画面精确保留端点，半透明画面保留 straight RGB；覆盖直接编码和 `media clip` 共用的像素转换。
- 通过 `media clip` 验证默认及指定质量的透明视频裁剪。
- 通过 `media mux` 写入 PCM24 和中文／日文字幕，确认混流前后解码得到的 RGBA 像素完全相同。

这些测试验证编码器、命令行和发布包的媒体链路；实际 Spine 画面的完整性及旧版 Premiere 导入效果须分别验收。编码器构建版本和文件校验值记录在发布包的 `media-dependencies.json`，源码版本锁定见 `scripts/media-sources.lock`，许可证及重建说明见 [THIRD-PARTY.md](THIRD-PARTY.md)。
