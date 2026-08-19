# Avatar Texture Optimizer (ATO)

> Non-destructive UV & texture atlas optimizer for VRChat avatars, built on NDMF.
> 基于 NDMF 的 VRChat Avatar 非破坏式 UV 与贴图图集优化工具。

**Package name / 包名**: `net.fosa.avatar-texture-optimizer`
**Status / 状态**: pre-release, under active development. 开发中，尚未经过 Unity 编译与实机验证。

---

## What it does / 它做什么

ATO builds a mapping from **mesh UVs to textures** rather than from materials to textures. Because the
mapping ignores every non-texture shader parameter, two different materials that reference the same
texture reuse the same mapping. On top of that mapping ATO:

ATO 建立的是**网格 UV → 贴图**的映射，而不是材质 → 贴图。由于该映射完全无视贴图以外的着色器参数，
引用同一张贴图的不同材质可以复用同一份映射。在此基础上，ATO 会：

1. Deduplicate textures by pixel content **and** import settings.
   按像素内容**与**导入设置对贴图去重。
2. Split each UV island out, shrink it to the smallest size that still meets your quality target, and drop
   the parts of the texture no UV ever touches.
   拆出每个 UV 岛，缩小到仍满足目标质量的最小尺寸，并剔除从未被 UV 采样到的贴图区域。
3. Repack the islands into one or more atlases using **shape-aware** (not rectangle) packing.
   使用**形状感知**（而非矩形）装箱把这些岛重新组合成一张或多张图集。
4. Rewrite meshes, materials and animation clips to point at the new atlases — **and nothing else**.
   重写网格、材质与动画剪辑指向新图集——**且仅此而已**。

ATO never modifies any shader parameter other than texture references.
ATO 绝不修改贴图引用以外的任何着色器参数。

## Quick start / 快速上手

1. Install NDMF ≥ 1.14 and the VRChat Avatars SDK.
2. Add **FOSA → Avatar Texture Optimizer** to the GameObject that carries your `VRCAvatarDescriptor`.
   Exactly one component per avatar.
3. Upload. That is all — the defaults are the recommended settings.

1. 安装 NDMF ≥ 1.14 与 VRChat Avatars SDK。
2. 在带有 `VRCAvatarDescriptor` 的物体上添加 **FOSA → Avatar Texture Optimizer**。一个 Avatar 只能有一个。
3. 直接上传。就这样——默认值就是推荐设置。

## Quality presets / 质量挡位

| Preset | MS-SSIM | ΔE00 mean / p95 | Normal mean / p95 | Notes |
|---|---|---|---|---|
| Draft | 0.900 | 4.0 / 8.0 | 8° / 18° | Smallest, visible on close inspection |
| Performance | 0.950 | 2.5 / 5.0 | 4° / 9° | Quest-friendly |
| **Balanced** (default) | 0.985 | 1.5 / 3.0 | 1.5° / 4° | Recommended |
| High | 0.995 | 0.8 / 1.6 | 0.7° / 1.8° | Near-transparent to the eye |
| Lossless | — | — | — | Island rescaling skipped entirely |
| Custom | user | user | user | Never overwritten by preset switching |

Rationale: MS-SSIM ≈ 0.99+ is commonly reported as visually lossless; CIEDE2000 ΔE00 ≤ 1 is one JND and
≤ 3.5 is the classic print-acceptance limit. MS-SSIM's five scales need 11 × 2⁴ = 176 px, which is exactly
why islands below 176 px fall back to single-scale SSIM and islands below 11 px ignore the metric.

依据：MS-SSIM ≈ 0.99 以上通常被认为视觉无损；CIEDE2000 ΔE00 ≤ 1 为 1 个 JND，≤ 3.5 是经典印刷可接受上限。
MS-SSIM 的五个尺度需要 11 × 2⁴ = 176px，这正是「短边 < 176px 回退单尺度 SSIM、< 11px 忽略」的由来。

## Target-quality algorithm / 目标质量算法

Everything is evaluated in **linear space**, on the reconstruction (downsample → bilinear upsample back to
the original size) versus the original. Transparent colour textures are downsampled **premultiplied**.
The worst metric decides; all of them must pass.

所有评估都在**线性空间**中，用「下采样 → 双线性上采样回原尺寸」的重建图与原图比较。
透明彩色贴图以**预乘 alpha** 下采样。取所有指标中最差的一项，全部达标才算通过。

| Texture kind | Metrics |
|---|---|
| Opaque colour | MS-SSIM + CIEDE2000 (mean & p95) |
| Cutout colour | + silhouette IoU after `clip()`, evaluated at **every** cutoff any referencing material uses |
| Blend colour | + linear alpha RMSE |
| Normal map | angular error (mean & p95) after decode → resample → renormalise → encode |
| Grayscale / data | per-channel linear RMSE on **sampled channels only**, worst channel wins |

Search: uniform binary search first, then an independent binary refinement per axis (anisotropy).
Solid-colour islands short-circuit to `min(4, short side)` unless the preset is lossless.
Texel density is clamped between the configured min/max (default 2048–4096 px/m) and by the real
imported texture size.

搜索：先均匀二分，再对两个轴分别二分细化（各向异性）。
非近无损挡位下，纯色岛直接短路到 `min(4, 包围盒短边)`。
像素密度会被配置的上下限（默认 2048–4096 px/m）以及贴图导入后的真实尺寸共同钳制。

## Atlas packing / 图集装箱

- 4 px granularity conservative rasterisation (Burst) → bit-packed masks.
- Full-scan bottom-left-fill, area-descending then longest-edge-descending, with a 90° rotation step
  (implemented as a mask transpose — tangent data is **never** recomputed).
- Candidate pool: powers of two 64…8192 (4096 on mobile) by default; 64 px steps with the experimental
  NPOT option. Non-square candidates are allowed, closest-to-square first among equal areas.
- One texture **plus its whole UV group** is the atomic unit, so all islands from a texture always land in
  the same atlas.
- Padding = `ceil(longest edge / 128)`, clamped up to the configured minimum (4/8/16/32/64, default 4).
- Empty space is filled by GPU-style pull-push edge extension; alpha stays 0 outside islands.

- 4 像素粒度保守光栅化（Burst）→ 位压缩掩码。
- 全扫描 BLF，按面积降序 + 最长边降序，含 90° 旋转步进（以掩码转置实现——**绝不**重算切线数据）。
- 候选池：默认 2 的幂 64…8192（移动端 4096）；勾选实验性 NPOT 时以 64px 步进。允许非正方形，面积相同时越接近正方形越优先。
- 「一张贴图 + 其所属 UV 组」为原子单位，因此同一贴图的所有岛必定在同一图集内。
- padding = `向上取整(最长边 / 128)`，并向上钳制到配置的最小值（4/8/16/32/64，默认 4）。
- 空白区域用 pull-push 边缘外扩填满；岛外 alpha 保持 0。

## Safety rules / 安全规则

A texture is treated **exactly like a whitelisted texture** (skipped entirely, with a warning) whenever
ATO cannot prove the transformation is safe:

只要 ATO 无法证明变换是安全的，该贴图就会被**完全按白名单处理**（跳过并 warning）：

- non-identity tiling/offset, shader-side scroll/rotate/angle, decal or MSDF usage;
- animation driving any of those properties;
- UV channel that the mesh does not provide, or a slot that does not sample mesh UVs at all
  (matcap, dither, gradation LUT, screen-space …);
- UV islands that cross a wrap seam (out-of-range UVs that *can* be integer-translated into `[0,1]`
  are normalised correctly);
- textures that do not fit into the largest candidate atlas.

Compression choices are validated at build time as well: a format without an alpha channel is never used
for a texture that needs alpha, a single-channel format is never used for a multi-channel texture, crunch
is dropped for non-power-of-two atlases, and PVRTC is not offered on iOS at all.

压缩格式在构建时也会被校验：需要 alpha 的贴图绝不会使用无 alpha 的格式；多通道贴图绝不会被压成单通道；
NPOT 图集自动关闭 Crunch；iOS 完全不提供 PVRTC。

## Compatibility / 兼容性

- Runs in `BuildPhase.Optimizing`, **after Modular Avatar**, and is explicitly ordered
  `BeforePlugin("com.anatawa12.avatar-optimizer")`.
- Uses AAO's `UVUsageCompabilityAPI` (upstream spelling) **through reflection**, so the package works with
  or without AAO installed. When AAO reads a UV channel we rewrite, the original UVs are copied to a free
  channel and registered for evacuation.
- Shader analysis is generic (`Shader.GetPropertyCount/Type/Flags`) plus the well-known lilToon
  conventions (`_X_UVMode`, `_X_ScrollRotate`, `_XAngle`, `_XIsDecal`, …), so future shader versions
  degrade gracefully instead of breaking.

## Localization / 本地化

Drop any `ato-lang.<code>.json` (e.g. `ato-lang.fr.json`) anywhere in your project and it appears in the
language dropdown. `Auto` follows NDMF's language. Missing keys fall back to English. Nested objects are
flattened with `:` and `//` line comments are tolerated.

在工程任意位置放入 `ato-lang.<语言代码>.json`（如 `ato-lang.fr.json`）即可出现在语言下拉框中。
`Auto` 跟随 NDMF 语言设置。缺失的键回退到英文。嵌套对象用 `:` 扁平化，允许 `//` 行注释。

## For third-party developers / 面向第三方开发者

```csharp
using Net.Fosa.AvatarTextureOptimizer.Editor.API;

[InitializeOnLoad]
static class MyIntegration
{
    static MyIntegration()
    {
        ATOExtensionRegistry.Register(new MyShaderProvider());  // IATOShaderProvider
        ATOExtensionRegistry.Register(new MyHook());            // IATOBuildHook
        // ATOExtensionRegistry.PackingStrategyOverride = new MyPacker(); // IATOPackingStrategy
    }
}
```

All three extension points are live in the pipeline: shader providers get first refusal in
`ShaderAnalysis.Analyse`, build hooks run in `ATOMainPass` (graph observation, per-texture veto,
post-planning inspection), and a packing strategy override replaces `AtlasPacker.PackAll` wholesale. A hook
that throws is logged and skipped, never fatal.

三个扩展点都已接入主流程：着色器提供者在 `ShaderAnalysis.Analyse` 中拥有优先权；
构建钩子在 `ATOMainPass` 中运行（观察关系图、逐贴图否决、规划后检查）；
装箱策略覆盖会整体替换 `AtlasPacker.PackAll`。钩子抛出的异常只会被记录并跳过，绝不会导致构建失败。

## Diagnostics / 调试

Every log line starts with `[ATO]`. Verbose logging and per-island metric traces are toggles on the
component, so advanced users never need to recompile. The NDMF console shows a one-line summary
(atlases, textures, estimated VRAM before/after, saved %, total time) with a collapsed foldout containing
the per-atlas breakdown (size, format, utilisation, island count, source textures) and the full stage
timing table.

所有日志都以 `[ATO]` 开头。详细日志与逐岛指标追踪都是组件上的开关，高级用户无需重新编译。
NDMF 控制台会显示一行总览（图集数、贴图数、优化前后预计显存、节省比例、总耗时），
并折叠展示逐图集明细（尺寸、格式、利用率、岛数量、来源贴图）与完整的阶段耗时表。

## GPU acceleration / GPU 加速

Resampling, SSIM/MS-SSIM, CIEDE2000, alpha metrics, normal angular error and the pull-push edge extension
all run as compute shaders (`Editor/Shaders/ATOImageOps.compute`, `ATOPullPush.compute`) over flat
structured buffers. Every one of them has a CPU reference implementation that is used automatically when
compute is unavailable, when the image is smaller than 64×64, or if any dispatch throws — in which case the
whole remaining build falls back to CPU so results stay self-consistent. `GpuImageOps.ForceCpu` pins the
CPU path for debugging. Island rasterisation and the packing masks use Burst.

重采样、SSIM/MS-SSIM、CIEDE2000、alpha 指标、法线角度误差与 pull-push 边缘外扩全部以 compute shader
（`Editor/Shaders/ATOImageOps.compute`、`ATOPullPush.compute`）在扁平 structured buffer 上执行。
每一项都有 CPU 参考实现，在 compute 不可用、图像小于 64×64、或任一 dispatch 抛异常时自动启用——
后一种情况下本次构建的剩余部分会整体降级到 CPU，以保证结果自洽。
`GpuImageOps.ForceCpu` 可在排查问题时强制走 CPU。岛的光栅化与装箱掩码使用 Burst。

## Atlas families / 图集族

A UV group is the atomic unit of packing, not a texture. Packing a group once produces an **atlas family**:
one atlas per texture role (colour / normal / mask / …) and per animation variant, all sharing identical
dimensions and identical island placement. That is what makes a single rewritten UV set valid for a colour
atlas and its companion normal and mask atlases simultaneously, and it is also why a normal-map atlas never
ends up 90% empty — companion roles are grouped, not padded out.

装箱的原子单位是 UV 组而不是单张贴图。一个组装箱一次产生一个**图集族**：
按贴图角色（彩色 / 法线 / 蒙版 / …）与动画变体各输出一张图集，尺寸与岛的位置完全一致。
这正是「一份重写后的 UV 能同时对彩色图集及其配套法线、蒙版图集有效」的原因，
也是法线图集不会出现 90% 浪费的原因——配套角色是被分组的，而不是被空白填充的。

## Known limitations / 已知限制

Every feature on the roadmap is implemented (see `CLAUDE.md` §8). The one outstanding item is verification:
this package has **not yet been compiled inside a Unity project**, because it was authored in an environment
without Unity or a C# toolchain. Please report the first compile log and the first bake result.

路线图上的功能已全部实现（见 `CLAUDE.md` 第 8 节）。唯一遗留项是验证：
本包**尚未在 Unity 工程内编译过**，因为它是在没有 Unity、也没有 C# 工具链的环境中编写的。
请把首次编译日志与首次烘焙结果反馈给我。

## License

MIT.
