# AvatarTextureOptimizer (ATO)

> 全世界最好的 VRChat Avatar 贴图优化 NDMF 工具。
> The world-class VRChat avatar texture optimization NDMF tool.

[English](#english) | [中文](#中文)

---

<a name="english"></a>
## English

**AvatarTextureOptimizer (ATO)** is an open-source [NDMF](https://github.com/bdunderscore/ndmf) plugin that
analyzes the meshes on your VRChat avatar and generates UV-island-aware texture atlases, so you can keep
visual quality while dramatically reducing texture memory.

### What it does

1. **Collects** every `SkinnedMeshRenderer` / `MeshRenderer` material slot (skipping `EditorOnly`),
   and finds the main-color / normal / mask / grayscale textures each one samples — via keyword-driven
   shader analysis that adapts to lilToon and standard shaders.
2. **Analyzes animations** for material-slot switches, texture switches, ST / scroll / rotate transforms,
   render-mode / cutoff changes, enable toggles and scale animations.
3. **Deduplicates** textures by import settings + pixel content.
4. **Extracts UV islands** (union-find over shared vertices), normalizes out-of-bounds UV, merges
   overlapping islands, and builds **UV groups** (same UV → same atlas position) and **texture type
   groups** (normal/mask presence + color space + filter mode).
5. **Quality-gated scaling** per island using **MS-SSIM (SSIM fallback for small islands) + CIEDE2000 ΔE +
   alpha (Cutout IoU / Blend RMSE) / normal-map angle error (p95) / grayscale linear RMSE**, linear-space
   bilinear resampling with premultiplied alpha, binary search (uniform then per-axis for anisotropy),
   pixel-density clamping, and pure-color short-circuiting.
6. **Packs** islands into atlases via 4px-granularity bitmask rasterization + Bottom-Left-Fill with 90°
   rotation + a candidate atlas pool (POT by default, experimental NPOT).
7. **Applies** results: persists atlases with tuned import settings (compression per class,
   MipStreaming bound to mipmaps, clamp, sRGB, filter), remaps mesh UVs, and rewrites material and
   animation references — all through NDMF's `ObjectRegistry` so nothing breaks.
8. **Deduplicates** identical materials/textures after optimization and merges material slots.

### Installation

Add the package via VCC (package name `net.fosa.avatar-texture-optimizer`) or copy the folder into
`Assets/`. Requires NDMF and the VRChat Avatars SDK.

### Usage

1. Attach one **Avatar Texture Optimizer** component to the object that has the `VRCAvatarDescriptor`.
   (Exactly one per avatar is allowed.)
2. Optionally configure quality preset, atlas, compression, dedup, whitelist, and platform overrides.
3. Upload your avatar — ATO runs automatically (after Modular Avatar, before Avatar Optimizer) and prints
   a report to the NDMF console.

### Key options

| Option | Description |
| --- | --- |
| Quality preset | Lossless / Ultra / High (default) / Balanced / Aggressive / Custom |
| Generate atlas | On by default; off = whole-texture scaling only, no UV repack |
| Padding | 4 / 8 / 16 / 32 / 64 px between islands |
| Allow NPOT | Experimental NPOT atlas sizes |
| Compression | Per texture class (opaque / transparent / normal / grayscale) |
| Whitelist | Objects whose referenced textures skip all optimization |
| Platform override | Per-platform (PC / Android / iOS) parameter overrides |
| Language | Auto (follows NDMF) / English / 简体中文 |

### Extending ATO

- **Custom material analyzers**: implement `ATOShaderAnalysis.IMaterialAnalyzer` and call
  `ATOShaderAnalysis.RegisterAnalyzer(...)` to teach ATO about third-party shaders.
- **Custom i18n**: drop JSON files into `Assets/ATO/Localization/` (filename = language code, e.g.
  `fr.json`). ATO picks them up automatically.
- **Burst**: the hottest loops (rasterization, SSIM) ship CPU reference implementations plus Burst jobs.

### Safety

ATO is conservative by design. Any texture that has an ST transform, is used for a special purpose
(decal/parallax), is animated with a scroll/rotate, or is referenced by a whitelisted object is skipped
(and its UV-sharing textures skip atlasing but still get import-parameter tuning). Unsupported shaders
are treated as whitelist with a warning.

---

<a name="中文"></a>
## 中文

**AvatarTextureOptimizer (ATO)** 是一个开源的 [NDMF](https://github.com/bdunderscore/ndmf) 插件，
分析你 VRChat Avatar 上的网格，生成基于 UV 岛的贴图图集，在保持画质的同时大幅降低贴图内存。

### 功能

1. **收集** 每个 `SkinnedMeshRenderer` / `MeshRenderer` 的材质槽（跳过 `EditorOnly`），通过关键字驱动的
   着色器分析（兼容 lilToon 与标准着色器）找出其采样的主色/法线/蒙版/灰度贴图。
2. **分析动画**：材质槽切换、贴图切换、ST/滚动/旋转变换、渲染模式/Cutoff 变化、启用开关、缩放动画。
3. **贴图去重**（按导入设置 + 像素内容）。
4. **提取 UV 岛**（共享顶点并查集）、越界 UV 归一化、重叠岛合并，构建 **UV 组**（同 UV → 同图集位置）与
   **贴图类型组**（法线/蒙版存在 + 色彩空间 + filterMode）。
5. **质量门控缩放**：MS-SSIM（小岛回退 SSIM）+ CIEDE2000 ΔE + alpha（Cutout IoU / Blend RMSE）/ 法线角度误差
   （p95）/ 灰度线性 RMSE，线性空间双线性重采样 + 预乘 alpha，二分搜索（均匀后逐轴各向异性），像素密度钳制，
   纯色短路。
6. **装箱**：4px 粒度位掩码光栅化 + Bottom-Left-Fill（90° 旋转）+ 候选图集池（默认 POT，实验性 NPOT）。
7. **应用**：持久化图集并调优导入设置（按分类压缩、MipStreaming 与 mipmap 绑定、Clamp、sRGB、filter），
   重映射网格 UV，重写材质与动画引用——全部经 NDMF `ObjectRegistry`，不破坏任何引用。
8. **优化后去重**：合并相同材质/贴图，合并材质槽。

### 安装

通过 VCC 添加包（包名 `net.fosa.avatar-texture-optimizer`），或将文件夹拷入 `Assets/`。依赖 NDMF 与
VRChat Avatars SDK。

### 使用

1. 在拥有 `VRCAvatarDescriptor` 的对象上挂载 **Avatar Texture Optimizer** 组件（每个 Avatar 只允许一个）。
2. 可选配置质量挡位、图集、压缩、去重、白名单、平台覆盖。
3. 上传 Avatar——ATO 自动运行（在 Modular Avatar 之后、Avatar Optimizer 之前），并在 NDMF 控制台打印报告。

### 关键选项

| 选项 | 说明 |
| --- | --- |
| 质量挡位 | 近无损 / 超高 / 高（默认）/ 平衡 / 激进 / 自定义 |
| 生成图集 | 默认勾选；不勾选则只整图缩放，不重排 UV |
| Padding | 岛间距 4 / 8 / 16 / 32 / 64 px |
| 允许 NPOT | 实验性 NPOT 图集边长 |
| 压缩 | 按贴图分类（不透明 / 透明 / 法线 / 灰度） |
| 白名单 | 其引用贴图跳过所有优化的对象 |
| 平台覆盖 | 按平台（PC / Android / iOS）覆盖参数 |
| 语言 | 自动（跟随 NDMF）/ English / 简体中文 |

### 扩展

- **自定义材质分析器**：实现 `ATOShaderAnalysis.IMaterialAnalyzer` 并调用 `ATOShaderAnalysis.RegisterAnalyzer(...)`。
- **自定义 i18n**：将 JSON 文件放入 `Assets/ATO/Localization/`（文件名为语言代码，如 `fr.json`）。
- **Burst**：最热循环（光栅化、SSIM）提供 CPU 参考实现 + Burst 作业。

### 安全

ATO 保守设计。存在 ST 变换、特殊用途（贴花/视差）、滚动/旋转动画、或被白名单对象引用的贴图一律跳过
（其同 UV 贴图跳过图集化但仍做导入参数调优）。不支持的着色器按白名单处理并报 warning。

---

## License

MIT. See [LICENSE](LICENSE).
