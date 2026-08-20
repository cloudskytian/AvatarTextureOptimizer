# Avatar Texture Optimizer (ATO)

[English](#english) | [简体中文](#简体中文)

Non-destructive NDMF texture/atlas optimiser for VRChat avatars.
面向 VRChat Avatar 的非破坏式 NDMF 贴图/图集优化工具。

`net.fosa.avatar-texture-optimizer` · Unity 2022.3 · MIT

---

## English

### What it does

ATO looks at your avatar the way the GPU does: it builds the mapping from **mesh UVs to textures**, throws
away the parts of every texture nothing samples, shrinks each UV island only as far as a **perceptual
quality target** allows, and repacks everything into as few atlases as possible.

* Materials are matched by the **texture** they use, not by their parameters — swapping to a different
  material that uses the same texture reuses the same mapping.
* Only **textures, UVs and meshes** are ever modified. No other shader parameter is touched, ever.
* Everything runs inside the NDMF build, after Modular Avatar and before Avatar Optimizer (AAO).

### Installation

1. Install [NDMF](https://github.com/bdunderscore/ndmf) 1.14.0+ and the VRChat Avatars SDK 3.7+.
2. Add this package (VCC / VPM, or copy into `Packages/`).
3. Add the **Avatar Texture Optimizer** component to the GameObject that carries your
   `VRCAvatarDescriptor`. Exactly one component per avatar is allowed.
4. Press Play / build / upload as usual — the optimisation happens automatically.

### Quality tiers

| Tier | MS-SSIM | mean ΔE2000 | p95 ΔE2000 | Normal (mean/p95) | Typical use |
|---|---|---|---|---|---|
| Lossless | 1.0 | — | — | — | verbatim copy, import settings only |
| Very high | 0.995 | 1.0 | 2.0 | 1.0° / 2.5° | visually lossless |
| **High (default)** | 0.99 | 2.0 | 4.0 | 2.0° / 5.0° | recommended |
| Balanced | 0.98 | 3.0 | 6.0 | 3.0° / 7.0° | size/quality balance |
| Performance | 0.96 | 5.0 | 10.0 | 5.0° / 12.0° | crowded worlds |
| Aggressive | 0.93 | 8.0 | 16.0 | 8.0° / 18.0° | Quest / mobile |
| Custom | your values | | | | never overwritten by tier changes |

The thresholds follow the usual reading of the literature: a CIEDE2000 difference around **1.0 is the just
noticeable difference**, and **MS-SSIM ≥ 0.99** is commonly described as visually lossless
(Wang, Simoncelli & Bovik 2003; Sharma, Wu & Dalal 2005).

### The target quality algorithm

1. Everything is resampled in **linear space**; textures with meaningful alpha are downsampled with
   **premultiplied alpha**.
2. The downscaled island is bilinearly upsampled back to its original size and compared to the source,
   restricted to the texels the island actually covers.
3. Metrics per texture role:
   * colour: MS-SSIM + CIEDE2000 (mean and p95). Islands whose short side is below 176 px fall back to
     single scale SSIM; below 11 px the structural term is skipped entirely.
   * transparent colour: additionally alpha silhouette **IoU** at every cutoff the material(s) can use
     (Cutout) or linear alpha **RMSE** (Blend). The strictest requirement across all referencing
     materials wins.
   * normal maps: decoded, resampled, renormalised, re-encoded, then compared by **angular error**
     (mean + p95).
   * masks / grayscale: linear **RMSE per used channel**, worst channel decides.
4. A **binary search** finds the smallest uniform scale that still passes; afterwards each axis is refined
   independently so anisotropic islands are not over- or under-sampled.
5. Flat colour islands short-circuit to `min(4, short side)`. Texel density is clamped between the
   configured minimum and maximum (default 2048 – 4096 px/m).

### Atlasing

* 4 px granularity **raster bit masks** (real island shapes, not bounding rectangles).
* Ordering: rasterised area descending, then longest edge descending, with optional 90° rotation
  (bit mask transpose — **tangents are never recomputed**).
* Height-map guided **bottom-left-fill full scan** with exact bit mask collision tests.
* **Candidate atlas pool**: powers of two by default (64 – 8192, 4096 on mobile), or 64 px steps when the
  experimental NPOT option is enabled. Non-square candidates are allowed, closest-to-square first.
* Textures that share a UV stream form a **UV group** and always land at the same normalised position, so
  colour / normal / mask atlases stay perfectly aligned.
* Groups with the same set of texture classes form a **queue**, which prevents the classic
  "one normal map in a ten texture atlas wastes 90 % of the normal atlas" problem. A class whose quality
  requirements are lower gets its own smaller atlas with the same layout.
* Padding = `ceil(atlas longest edge / 128)`, clamped up to your minimum (4 / 8 / 16 / 32 / 64, default 4),
  and the empty space is filled by an infinite **pull-push bleed** on the GPU (alpha stays 0).

### Safety rules

A texture is skipped (treated as whitelisted) whenever ATO cannot prove the transformation is safe:

* tiling/offset, `_ScrollRotate`, decal, angle, MatCap/Rim UV modes, `_ShiftBackfaceUV`, UDIM discard,
* any animation that modifies one of those properties,
* UVs that cross a wrap seam and cannot be normalised into `[0,1]`,
* shaders whose property table cannot be understood.

Each case reports a warning to the NDMF console. Whitelisting is contagious in the safe direction: the
other textures sampled through the same UV stream keep their UVs (they are still rescaled and their import
settings are still optimised).

### Extending

```csharp
using Net.Fosa.AvatarTextureOptimizer.Editor.API;

ATOExtensions.RegisterShaderAdapter(new MyShaderAdapter()); // teach ATO about your shader
ATOExtensions.RegisterHook(new MyHook());                   // observe the pipeline
```

Translations: drop a `<language>.json` file (flat `{"key": "value"}`) into any folder named
`ATO-Localization` under `Assets/`. Every file found becomes a selectable language; missing keys fall back
to English.

---

## 简体中文

### 它做什么

ATO 用 GPU 的视角来看你的模型：建立**网格 UV 到贴图**的映射，丢掉贴图中没有被任何 UV 采样到的部分，
在**目标感知质量**允许的范围内尽可能缩小每个 UV 岛，然后重排合并成尽量少的图集。

* 材质按**贴图**匹配，与材质其他参数无关——切换到使用同一贴图的另一个材质，映射关系照样复用。
* 只会修改**贴图、UV 和网格**，绝不改动材质的任何其他着色器参数。
* 全程在 NDMF 构建中进行，位于 Modular Avatar 之后、Avatar Optimizer (AAO) 之前。

### 安装

1. 安装 [NDMF](https://github.com/bdunderscore/ndmf) 1.14.0+ 与 VRChat Avatars SDK 3.7+。
2. 通过 VCC / VPM 添加本包，或直接复制到 `Packages/`。
3. 把 **Avatar Texture Optimizer** 组件挂到带 `VRCAvatarDescriptor` 的对象上（每个 Avatar 只能有一个）。
4. 正常进入 Play / 构建 / 上传即可，优化会自动执行。

### 质量挡位

见上表。默认 **High**。阈值参考学界常用结论：CIEDE2000 约 **1.0 即恰可察觉差异 (JND)**，
**MS-SSIM ≥ 0.99** 通常被认为视觉无损。自定义挡位的参数永远不会被切换挡位覆盖。

### 目标质量算法

1. 全部在**线性空间**重采样；alpha 有意义的贴图使用**预乘 alpha**下采样。
2. 把缩小后的岛双线性上采样回原尺寸，只在岛**实际覆盖的纹素**上与原图比较。
3. 按贴图角色评估：
   * 颜色：MS-SSIM + CIEDE2000（均值与 p95）。包围盒短边 < 176px 回退单尺度 SSIM，< 11px 直接忽略该项。
   * 透明颜色：额外评估 alpha —— Cutout 用 clip 后**轮廓 IoU**（对每个可能的 Cutoff 逐一评估），
     Blend 用线性 **RMSE**；被多材质引用时取最严苛的要求。
   * 法线：解码 → 重采样 → 重归一化 → 编码，再用**角度误差**（均值 + p95）比较。
   * 灰度/蒙版：只在**被使用的通道**上做线性 RMSE，逐通道取最差。
4. 先**二分搜索**求出仍然达标的最小均匀缩放，再对两个轴分别细化，处理各向异性。
5. 纯色岛直接短路到 `min(4, 短边)`；像素密度被钳制在最小/最大之间（默认 2048 – 4096 px/m）。

### 图集化

* **4px 粒度光栅位掩码**，按真实岛形状装箱，不使用矩形装箱。
* 排序：光栅面积降序 → 最长边降序；可选 90° 旋转（位掩码转置，**绝不重算切线**）。
* 高度图引导的 **BLF 全扫描** + 精确位掩码碰撞检测。
* **候选图集池**：默认 2 的幂（64 – 8192，移动端 4096）；勾选实验性 NPOT 后按 64px 步进。
  允许非正方形，越接近正方形越优先。
* 共享同一路 UV 的贴图构成 **UV 组**，在各自图集上的归一化位置完全一致，颜色/法线/蒙版图集严格对齐。
* 拥有相同贴图类别集合的组构成一个**队列**（贴图类型组），避免"十张贴图只有一张有法线导致法线图集浪费 9/10"。
  质量需求更低的类别会生成同布局、更小尺寸的图集。
* padding = `ceil(图集最大边 / 128)`，向上钳制到你设置的最小值（4/8/16/32/64，默认 4）；
  空白区域用 GPU **pull-push 无限外扩**填充（透明贴图 alpha 保持 0）。

### 安全规则

只要无法证明变换安全，贴图就会被跳过（按白名单处理）并在 NDMF 控制台报 warning：
tiling/offset、`_ScrollRotate`、贴花、角度、MatCap/Rim 采样、`_ShiftBackfaceUV`、UDIM 丢弃；
动画修改上述属性；UV 跨 wrap 缝无法归一；着色器属性表无法理解。
白名单会沿"安全方向"传播：采样同一路 UV 的其他贴图会保留 UV（但仍会做整图缩放与导入参数优化）。

### 扩展

```csharp
using Net.Fosa.AvatarTextureOptimizer.Editor.API;

ATOExtensions.RegisterShaderAdapter(new MyShaderAdapter()); // 为自定义着色器补充信息
ATOExtensions.RegisterHook(new MyHook());                   // 观察管线各阶段
```

本地化：把 `<语言代码>.json`（扁平的 `{"key": "value"}`）放进 `Assets/` 下任意名为 `ATO-Localization`
的文件夹即可。有几个文件就有几种语言可选，缺失的条目回退到英文。

### 调试

在组件的 **Debug** 折叠里打开 `Verbose [ATO] logging`，所有日志以 `[ATO]` 开头，包含每一步耗时、
图集来源、岛数量、图集尺寸与利用率、相对原贴图的优化量；构建结束后会在 NDMF 控制台输出总览报告，
细节默认折叠。

---

## License

MIT. See `LICENSE`.
