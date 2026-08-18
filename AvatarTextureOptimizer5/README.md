# Avatar Texture Optimizer

**English** | [简体中文](#简体中文)

A non-destructive [NDMF](https://github.com/bdunderscore/ndmf) tool for VRChat avatars. It
analyses your avatar's meshes, works out exactly which parts of every texture are actually
used, shrinks each UV island to the smallest size that still looks identical, throws away the
unused space, and repacks everything into shared atlases.

Nothing is modified in your project. All work happens during the build.

---

## What it does

| Stage | What happens |
| --- | --- |
| **Analyse** | Finds every texture on the avatar and how each material samples it (colour space, alpha mode, used channels, UV channel). |
| **Map** | Groups textures by UV stream, so a normal map and its albedo always land in the same place. |
| **Measure** | Extracts UV islands, measures their real-world surface area, and computes the texel density they actually need. |
| **Shrink** | Binary-searches each island's size against perceptual quality metrics, first uniformly, then per-axis for anisotropic islands. |
| **Pack** | Rasterizes each island's true outline into a 4 px bitmask and packs the *shapes*, not their bounding boxes, with 90° rotation allowed. |
| **Composite** | Renders the atlases with pull-push dilation so filtering and mipmaps never bleed across island edges. |
| **Apply** | Rewrites mesh UVs, clones materials with only their texture references changed, and repoints everything. |

### Quality is measured, not guessed

Rather than a blunt "half resolution" slider, each candidate size is downsampled, upsampled back,
and compared against the original with the metric appropriate to the texture's role:

- **Colour** — MS-SSIM (falls back to single-scale SSIM below 176 px, ignored below 11 px) plus
  CIEDE2000 colour difference (mean and 95th percentile).
- **Normal maps** — angular error in degrees, decoded to vectors and re-normalised so encoding
  differences are never mistaken for detail loss.
- **Grayscale / masks** — linear RMSE, evaluated only on channels the shader actually reads,
  taking the worst channel.
- **Alpha** — clipped-shape IoU for Cutout materials (tested at every cutoff the material is
  animated through), linear RMSE for Blend.

Resampling is done in linear space with premultiplied alpha, so transparent pixels can never
bleed their colour into visible ones.

### Quality presets

| Preset | MS-SSIM | ΔE00 mean | Normal mean | Pixel density (px/m) |
| --- | --- | --- | --- | --- |
| Maximum | *lossless* | — | — | 4096–8192 |
| High | 0.995 | 0.8 | 0.75° | 2048–4096 |
| **Balanced** (default) | 0.985 | 1.5 | 1.5° | 2048–4096 |
| Performance | 0.970 | 2.5 | 3.0° | 1024–2048 |
| Extreme | 0.945 | 4.0 | 5.0° | 512–1024 |

Full table: [`docs/QualityPresets.md`](docs/QualityPresets.md).

**Maximum** never resamples anything — textures keep their exact original resolution, but still
benefit from atlasing, deduplication and better compression settings.

---

## Installation

Requires Unity 2022.3, the VRChat Avatars SDK, and NDMF ≥ 1.14.4.

Add the package to your project via VCC, or clone into `Packages/`.

Dependencies (`com.unity.burst`, `com.unity.collections`, `com.unity.mathematics`) are declared
in `package.json` and resolve automatically.

## Usage

1. Add the **Avatar Texture Optimizer** component to your avatar root (next to the
   `VRCAvatarDescriptor`).
2. Pick a quality preset. **Balanced** is a good default.
3. Upload as usual.

That's the whole workflow. The component removes itself during the build.

### If something looks wrong

Add the offending object to **Exclusions**. It accepts anything — a texture, a material, a
renderer, a mesh, an animation clip, or a whole GameObject — and everything it touches is left
completely alone.

Enable **Verbose Logging** to see per-stage timings, island counts, atlas sizes and utilisation
in the console.

---

## Compatibility

- **Modular Avatar** — ATO runs after MA, so everything MA assembles is included.
- **Avatar Optimizer** — ATO runs before AAO and uses its `UVUsageCompabilityAPI` to preserve
  original UVs where AAO needs them. Works whether or not AAO is installed.
- **lilToon** and other shaders — materials using UV scrolling, decals, UDIM discard or animated
  UV properties are detected and excluded automatically.

**Shader parameters other than texture references are never modified.** Materials are cloned;
only their texture slots change.

---

## Safety

The tool refuses to touch anything it cannot prove is safe:

- Textures with a scale/offset (`_ST`) other than the identity transform.
- Textures swapped in by animation (the sampling UV layout is unknowable at build time).
- Materials with animated UV transform properties.
- UV islands that cross a tile boundary.
- Meshes that are not readable, or Crunch-compressed textures.

Each exclusion is reported in the console with a reason.

---

## Limitations

- No ndmf preview support yet; results are visible after a build or a manual bake.
- UV evacuation for Avatar Optimizer only works on `SkinnedMeshRenderer` (an AAO API
  restriction). `MeshRenderer`s that AAO needs UVs from are excluded with a warning.
- Non-power-of-two atlases are experimental and disabled by default.

## Contributing

Translations are plain JSON files in `Editor/Resources/i18n/`. Copy `en.json`, translate the
values, name it after the language code, and it is picked up automatically.

## License

MIT

---

<a name="简体中文"></a>

# Avatar Texture Optimizer（简体中文）

面向 VRChat 模型的非破坏式 [NDMF](https://github.com/bdunderscore/ndmf) 贴图优化工具。
它会分析模型网格，找出每张贴图中真正被使用的区域，把每个 UV 岛缩小到「看起来仍然一致」的最小尺寸，
丢弃未使用的空白，并将所有内容重新打包为共享图集。

工程中的资产不会被修改，全部处理都在构建期完成。

## 功能概览

| 阶段 | 内容 |
| --- | --- |
| **分析** | 找出模型上的所有贴图，以及各材质的采样方式（色彩空间、透明模式、使用通道、UV 通道）。 |
| **映射** | 按 UV 流对贴图分组，确保法线与其对应的固有色始终落在同一位置。 |
| **测量** | 提取 UV 岛，计算其真实世界表面积，推导实际所需的像素密度。 |
| **缩放** | 以感知质量指标为准对每个岛的尺寸做二分搜索：先均匀缩放，再针对各向异性岛逐轴细化。 |
| **装箱** | 将每个岛的真实轮廓光栅化为 4px 位掩码，按**形状**而非包围盒装箱，并允许 90° 旋转。 |
| **合成** | 以 pull-push 外扩填充渲染图集，使过滤与 mipmap 不会跨越岛边缘渗色。 |
| **应用** | 重写网格 UV，克隆材质并**仅**替换其贴图引用。 |

### 质量是测量出来的，不是猜的

每个候选尺寸都会经过「下采样 → 上采样还原 → 与原图比较」，并按贴图用途选用对应指标：

- **颜色**：MS-SSIM（短边低于 176px 回退单尺度 SSIM，低于 11px 忽略）+ CIEDE2000 色差（平均值与 95 分位）。
- **法线**：解码为向量后计算角度误差并重新归一化，避免把编码差异误判为细节损失。
- **灰度 / 蒙版**：线性 RMSE，只在着色器实际读取的通道上计算，取最差通道。
- **Alpha**：Cutout 使用裁剪后形状 IoU（覆盖该材质被动画化经过的每一个 cutoff）；Blend 使用线性 RMSE。

重采样在线性空间下配合预乘 alpha 进行，因此透明像素的颜色绝不会渗入可见像素。

### 质量挡位

| 挡位 | MS-SSIM | ΔE00 平均 | 法线平均 | 像素密度 (px/m) |
| --- | --- | --- | --- | --- |
| 最高 | *无损* | — | — | 4096–8192 |
| 高 | 0.995 | 0.8 | 0.75° | 2048–4096 |
| **均衡**（默认） | 0.985 | 1.5 | 1.5° | 2048–4096 |
| 性能 | 0.970 | 2.5 | 3.0° | 1024–2048 |
| 极限 | 0.945 | 4.0 | 5.0° | 512–1024 |

完整参数表见 [`docs/QualityPresets.md`](docs/QualityPresets.md)。

**最高**挡位不会进行任何重采样——贴图保持原始分辨率，但仍会受益于图集化、去重与更优的压缩设置。

## 安装

需要 Unity 2022.3、VRChat Avatars SDK 与 NDMF ≥ 1.14.4。

通过 VCC 添加本包，或克隆到 `Packages/` 目录。
依赖项（`com.unity.burst`、`com.unity.collections`、`com.unity.mathematics`）已在 `package.json` 中声明，会自动解析。

## 使用方法

1. 将 **Avatar Texture Optimizer** 组件添加到模型根节点（与 `VRCAvatarDescriptor` 同级）。
2. 选择质量挡位，推荐 **均衡**。
3. 照常上传。

整个流程就这么简单。该组件会在构建过程中自动移除。

### 如果发现显示异常

把出问题的对象加入 **排除项**。它接受任意类型——贴图、材质、渲染器、网格、动画文件或整个游戏对象，
其涉及的所有内容都会被完全跳过。

开启 **详细日志** 可在控制台查看各阶段耗时、岛数量、图集尺寸与利用率。

## 兼容性

- **Modular Avatar**：ATO 在 MA 之后运行，因此 MA 生成的内容都会被纳入处理。
- **Avatar Optimizer**：ATO 在 AAO 之前运行，并通过其 `UVUsageCompabilityAPI` 保留 AAO 所需的原始 UV。无论是否安装 AAO 都能正常工作。
- **lilToon** 等着色器：使用 UV 滚动、贴花、UDIM 丢弃或被动画化 UV 属性的材质会被自动识别并排除。

**除贴图引用外，绝不修改材质的任何着色器参数。** 材质会被克隆，只有贴图槽发生变化。

## 安全策略

无法证明安全的内容一律不动：

- 存在非单位 `_ST` 缩放/偏移的贴图。
- 由动画切换的贴图（构建期无法得知其采样 UV 布局）。
- 存在被动画化 UV 变换属性的材质。
- 跨越平铺边界的 UV 岛。
- 不可读的网格，以及 Crunch 压缩的贴图。

每一项排除都会连同原因输出到控制台。

## 已知限制

- 暂不支持 ndmf 预览；需构建或手动烘焙后查看结果。
- 面向 Avatar Optimizer 的 UV 撤离仅支持 `SkinnedMeshRenderer`（AAO API 的限制）。AAO 需要 UV 的 `MeshRenderer` 会被排除并给出警告。
- 非 2 的幂图集为实验性功能，默认关闭。

## 参与贡献

翻译文件是 `Editor/Resources/i18n/` 下的纯 JSON。复制 `en.json`，翻译其中的值，
按语言代码命名即可被自动识别。

## 许可

MIT
