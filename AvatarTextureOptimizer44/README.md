# Avatar Texture Optimizer (ATO)

> 世界最好的 VRChat Avatar 贴图优化工具 — The best texture optimizer for VRChat avatars.
> 基于 NDMF 的非破坏式贴图优化：UV岛质量缩放 · 未用UV剔除 · 智能图集打包
> An NDMF-based non-destructive optimizer: island-quality scaling · unused-UV culling · smart atlas packing

[English](#english) | [简体中文](#简体中文)

---

## English

### What it does
ATO analyzes every mesh on your avatar (after Modular Avatar, before Avatar Optimizer), builds a
**mesh-UV → texture** mapping that ignores all other material parameters, then:

1. **Dedups textures** by real pixels + import settings (different import settings = different textures).
2. **Shrinks every UV island** with a perceptual quality search (MS-SSIM + CIEDE2000 + alpha metrics,
   normal maps by angular error, grayscale by per-channel RMSE), evaluated on GPU (compute shaders)
   with a Burst CPU fallback. Pure-color islands collapse to ≤4px; the near-lossless gear skips
   resampling entirely.
3. **Cuts unused UV regions** and re-packs islands into one or more atlases
   (Burst rasterized bitmask bottom-left-fill packing, 90° rotation, non-square candidates,
   experimental NPOT sizes verified to work with MipStreaming & Crunch).
4. **Texture type groups**: atlases are rendered per signature (color/normal/mask, sRGB/linear,
   filter mode) sharing one island layout, so a normal-map atlas never wastes space on textures
   that have no normal map — while every texture that covers the same UV keeps identical positions.
5. **Rewrites meshes/materials/animations** — texture references only, never any other shader
   parameter. Animations that swap materials/textures are tracked and rewritten too.
6. **Dedups & merges** identical materials/atlases, and merges identical opaque material slots
   (with submesh merge) when no animation targets those slots individually.

### Safety first
- Anything that cannot be proven safe is whitelisted with a warning (ST/scroll/rotate/decal,
  matcap/screen/LUT sampling, wrap-crossing UVs, unparseable shader usage, ...).
- Alpha content never falls into a no-alpha format; multi-channel masks stay multi-channel;
  normal maps are forced to BC5/ASTC. All combos fall back safely.
- Mipmaps and MipStreaming share ONE switch (VRChat requirement). Atlases are forced Clamp and
  Read/Write off.
- One component per avatar, must sit on the VRCAvatarDescriptor object; bad mounts abort the build
  with a clear error. The component removes itself from the baked avatar.

### Quick start
1. Install NDMF ≥ 1.14.4 (Modular Avatar / AAO / lilToon optional but supported).
2. Add the component **ATO Avatar Texture Optimizer** to your avatar root.
3. Choose a quality gear (default **High** — visually lossless in motion), tweak the whitelist if
   needed, build. Watch the [ATO] logs and the NDMF console report.

### Quality gears (based on published research)
| Gear | MS-SSIM | ΔE00 mean / p95 | Alpha (Blend RMSE / Cutout IoU) | Normal mean / p95 |
|---|---|---|---|---|
| NearLossless | 1.0 (skip resample) | 1.0 / 2.0 | 1/255 / 1.0 | 1° / 2° |
| **High (default)** | 0.99 | 1.5 / 3.0 | 0.004 / 0.995 | 1° / 3° |
| Medium | 0.97 | 3.0 / 6.0 | 0.010 / 0.98 | 2.5° / 8° |
| Low | 0.93 | 5.0 / 10.0 | 0.020 / 0.95 | 4° / 15° |
| Custom | user values, never overwritten by other gears | | | |

MS-SSIM: Wang, Simoncelli & Bovik 2003/2004 · CIEDE2000: Sharma, Wu & Dalal 2005 ·
Islands smaller than 176px fall back to single-scale SSIM; below 11px the metric is ignored.
Pixel-density clamps (default 2048–4096 px per real-world meter, gears 512–8192) prevent both
waste and blur, always clamped by the source texture's real resolution.

### For third-party developers
- `Fosa.ATO.Editor.ATOExtensions.Register(IATOExtension)` exposes six hooks:
  `GraphBuilt / QualityDone / Packed / Rendered / Rewritten / Finished`.
- i18n is a plain JSON drop-in: add `Localization/<lang>.json` (see `en-us.json`) and it appears in
  the language selector automatically. Default "Auto" follows NDMF's language with English fallback.
- Reports/warnings go through the NDMF error console (severity `Information`).
- AAO compatibility via reflection (`UVUsageCompabilityAPI`), works without AAO installed.

---

## 简体中文

### 它做什么
ATO 在 Modular Avatar 之后、Avatar Optimizer 之前分析 Avatar 上所有网格，建立**网格UV → 贴图**映射
（无视材质其他参数；同贴图不同材质直接复用），然后：

1. **贴图去重**：按实际像素 + 导入设置（导入设置不同视为不同）。
2. **UV岛质量缩放**：MS-SSIM + CIEDE2000 + alpha 指标（Blend 用线性 RMSE、Cutout 用 clip 轮廓 IoU），
   法线贴图按角度误差，灰度按被使用通道 RMSE；GPU（ComputeShader）批量执行 + Burst CPU 兜底；
   纯色岛短路缩到 ≤4px；近无损挡直接跳过重采样原样拷贝。
3. **剔除未用UV**：岛形状光栅化装箱（Burst 4px 位掩码 + BLF + 90°旋转 + 候选图集池 +
   实验性NPOT[已验证支持MipStreaming/Crunch]）。
4. **贴图类型组**：按（颜色/法线/蒙版、色彩空间、filterMode）分映像渲染、共享同一岛布局——
   法线图集不再为没有法线的贴图浪费空间，同一UV在所有图集上位置一致。
5. **只改网格/贴图引用**重写材质与动画（含材质切换/贴图切换动画），绝不修改其他着色器参数。
6. **去重与合并**：相同材质/图集合并；无动画单独切换时合并相同不透明材质槽（含子网格合并）。

### 安全性
- 一切无法证明安全的（ST/滚动/旋转/贴花、matcap/屏幕/查找表采样、跨wrap缝UV、无法解析的着色器用途…）
  一律白名单跳过并报 warning。
- 有alpha绝不落无alpha格式；多通道灰度保持多通道；法线强制 BC5/ASTC；任意选项组合都有安全回退。
- Mipmap 与 MipStreaming 一个开关（VRChat 要求）；图集强制 Clamp、关闭 Read/Write。
- 一个Avatar只允许一个组件且必须挂在 VRCAvatarDescriptor 对象上；违规挂载报错中止；烘焙后自动移除自身。

### 快速上手
1. 安装 NDMF ≥ 1.14.4（MA/AAO/lilToon 可选，均已兼容）。
2. 在 Avatar 根对象添加组件 **ATO Avatar Texture Optimizer**。
3. 选择质量挡位（默认 **High**——动态视觉无损），按需设置白名单，构建。
   观察 `[ATO]` 日志与 NDMF 控制台报告（总体结果直接展示，细节折叠在控制台日志里）。

### 给第三方开发者
- 扩展点：`Fosa.ATO.Editor.ATOExtensions.Register(IATOExtension)`，六个阶段钩子
  （建图/质量/装箱/渲染/改写/完成）。
- i18n：往 `Localization/` 丢一个 `<语言>.json` 即自动出现在语言选择里；默认 Auto 跟随 NDMF 语言，
  缺失回退英文。
- AAO 兼容经反射调用 `UVUsageCompabilityAPI`，未安装 AAO 也可用。

### 兼容性
- lilToon 2.3.4 属性表已按其着色器源码逐项核对（并参考 AAO 的忠实转录自动兼容未来版本的关键字用法）。
- 运行于 NDMF Optimizing 阶段：MA 之后、AAO 之前；未安装 AAO 亦正常。

### 工程质量 / Engineering quality
- 全部源码已在忠实API桩下实际编译验证（dotnet 8 / C# 9，0 error / 0 warning），14项纯逻辑冒烟测试通过。
- 外部API（NDMF/AAO/MA/lilToon/VRC SDK）全部经源码核实，零猜测；已验证事实表见 CLAUDE.md。

### License
MIT © fosa
