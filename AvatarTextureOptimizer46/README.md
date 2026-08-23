# Avatar Texture Optimizer (ATO)

**Package name:** `net.fosa.avatar-texture-optimizer`
**Status:** pre-release / 开发阶段

An open source [NDMF](https://github.com/bdunderscore/ndmf) tool for VRChat avatars. It analyses the
meshes on an avatar, works out which parts of every texture are actually sampled, shrinks each UV
island to the smallest size that still meets a perceptual quality target, and repacks the surviving
islands into shared atlases.

一个面向 VRChat Avatar 的开源 NDMF 工具。它会分析 Avatar 上的网格，找出每张贴图中真正被采样的
部分，把每个 UV 岛缩小到仍能满足感知质量目标的最小尺寸，再把留下来的岛重新打包进共享图集。

---

## Design guarantees / 设计保证

* **Only UVs and texture references are ever written.** No other shader parameter is modified, ever.
  **只会写入 UV 与贴图引用。** 绝不修改任何其他着色器参数。
* **Anything ATO cannot prove safe is skipped** and reported as a warning in the NDMF console.
  **凡是 ATO 无法证明安全的内容都会被跳过**，并在 NDMF 控制台中报告警告。
* **UV groups** guarantee that a normal map lands at exactly the same atlas coordinates as the colour
  map it belongs to. **UV 组**保证法线贴图与其对应的主色贴图落在完全相同的图集坐标上。
* **Texture type groups** prevent an atlas of normal maps from being 90% empty when only one member of
  the atlas actually has a normal map. **贴图类型组**避免在只有一个成员拥有法线贴图时，
  法线图集有 90% 的面积被浪费。

## Target quality algorithm / 目标质量算法

All resampling happens in linear space, with premultiplied alpha for transparent textures. Each
candidate downscale is bilinearly upsampled back to the original size and compared against the
original with:

所有重采样都在线性空间进行，透明贴图使用预乘 alpha。每个候选缩放都会被双线性上采样回原尺寸，
再与原图按以下方式比较：

| Texture kind / 贴图类型 | Metrics / 度量 |
|---|---|
| Colour / 颜色 | MS-SSIM + CIEDE2000 (p95) |
| Colour with alpha / 带 alpha 的颜色 | above + silhouette IoU (Cutout) or linear alpha RMSE (Blend) |
| Normal / 法线 | angular deviation after decode + renormalize, p95 |
| Grayscale / 灰度 | per used channel linear RMSE, worst channel wins |

* Islands whose bounding box short side is below 176 px fall back to single scale SSIM.
  包围盒短边低于 176 px 的岛回退到单尺度 SSIM。
* Islands below 11 px ignore structural similarity entirely.
  低于 11 px 的岛完全忽略结构相似度。
* A flat coloured island short circuits to `min(4, short side)`.
  纯色岛直接短路到 `min(4, 短边)`。
* The `Lossless` tier skips resampling completely.
  `Lossless` 挡位完全跳过重采样。

Default tier thresholds are derived from Wang/Simoncelli/Bovik (2003) for MS-SSIM and
Sharma/Wu/Dalal (2005) for CIEDE2000. See `Runtime/AtoQuality.cs` for the exact numbers and the
rationale.

默认挡位阈值取自 Wang/Simoncelli/Bovik（2003）的 MS-SSIM 与 Sharma/Wu/Dalal（2005）的 CIEDE2000。
具体数值与依据见 `Runtime/AtoQuality.cs`。

## Packing / 装箱

Islands are conservatively rasterized into a **4 texel granularity bit mask** with Burst, then packed
with a full-scan bottom-left-first search over the actual shape (not a bounding rectangle), sorted by
rasterized area descending then longest edge descending, with a 90 degree rotation retry implemented
as a mask transpose. Candidate atlas sizes come from a pool that is either powers of two (default) or
64 texel steps (experimental NPOT).

岛会用 Burst 保守光栅化成 **4 像素粒度的位掩码**，然后按实际形状（而非包围矩形）做全扫描
左下优先搜索装箱；排序规则为光栅化面积降序、其次最长边降序，并以掩码转置实现 90 度旋转重试。
候选图集尺寸来自二次幂池（默认）或 64 像素步进池（实验性 NPOT）。

Padding is `ceil(max edge / 128)` texels, clamped up to the configured minimum (4/8/16/32/64).
Empty space is filled by a GPU pull-push dilation that runs all the way down to 1x1 and back.

Padding 为 `ceil(最大边长 / 128)` 像素，并向上钳制到配置的最小值（4/8/16/32/64）。
空白区域由一路降到 1x1 再升回来的 GPU pull-push 外扩填满。

## Safety rules learned from reading shader source / 从阅读着色器源码得出的安全规则

These were derived by reading lilToon 2.3.4, not guessed:

以下规则来自阅读 lilToon 2.3.4 源码，而非猜测：

| Evidence / 证据 | Rule / 规则 |
|---|---|
| `lil_common_functions.hlsl` `lilCalcDoubleSideUV` | `_ShiftBackfaceUV != 0` shifts backface UV by +1.0 and relies on wrapping -> whole material whitelisted |
| `lil_common_macro.hlsl:272` `LIL_SAMPLE_2D_ST` | sub textures have their **own** `_ST`, all of them must be identity |
| `lil_common_frag.hlsl:746` `_Main2ndTex_UVMode == 4 -> fd.uvMat` | UV mode 4 is a projected space -> not atlasable |
| `lil_common_frag.hlsl:1825` `_EmissionMap_UVMode == 4 -> fd.uvRim` | same |
| `lilParallax` / `lilPOM` | parallax displaces the sampling UV -> whole material whitelisted |
| `_MatCapTex`, `_DitherTex`, `_MainGradationTex`, `_EmissionGradTex`, `_AudioLink*`, `_GlitterColorTex` | not sampled with a mesh UV -> skipped |

Anything sampled through a shader ATO does not recognise is treated exactly like a whitelisted
texture, with a warning.

任何通过 ATO 不认识的着色器采样的内容，都会被完全当作白名单贴图处理，并报出警告。

## Avatar Optimizer integration / 与 Avatar Optimizer 的集成

ATO runs in NDMF's `Optimizing` phase, ordered `BeforePlugin("com.anatawa12.avatar-optimizer")`, so
Modular Avatar has already finished. When AAO is installed, ATO calls
`UVUsageCompabilityAPI.IsTexCoordUsed` / `RegisterTexCoordEvacuation` through reflection - so the
package compiles and runs with or without AAO. Note that AAO's API only accepts
`SkinnedMeshRenderer`; plain `MeshRenderer`s are not evacuated, which is safe because AAO's UV
consuming components only attach to skinned renderers.

ATO 在 NDMF 的 `Optimizing` 阶段运行，并以 `BeforePlugin("com.anatawa12.avatar-optimizer")` 排序，
此时 Modular Avatar 已经执行完毕。安装了 AAO 时，ATO 会通过反射调用
`UVUsageCompabilityAPI.IsTexCoordUsed` / `RegisterTexCoordEvacuation`——因此无论是否安装 AAO，
本包都能编译并运行。注意 AAO 的 API 只接受 `SkinnedMeshRenderer`；普通 `MeshRenderer` 不做转移，
这是安全的，因为 AAO 中消费 UV 的组件只会挂在蒙皮渲染器上。

## Extending ATO / 扩展 ATO

Implement `IAtoShaderAnalyzer` and register it:

实现 `IAtoShaderAnalyzer` 并注册：

```csharp
[InitializeOnLoad]
public static class MyShaderSupport
{
    static MyShaderSupport()
        => AtoShaderAnalyzerRegistry.Register(new MyAnalyzer());
}
```

Add a translation by dropping `<culture>.json` into any folder registered with
`AtoLocalizer.AddSearchPath`. A language appears in the dropdown as soon as its file exists.

把 `<语言代码>.json` 放进任意通过 `AtoLocalizer.AddSearchPath` 注册的目录即可添加翻译。
只要文件存在，该语言就会出现在下拉框中。

## Logging / 日志

Every message is prefixed `[ATO]` and carries its stage and elapsed time. Verbose and per island trace
levels are switchable in the Debug foldout. The final summary is published to the NDMF console with
headline figures and a collapsible detail block listing atlas sizes, utilization, source textures,
island counts and skip reasons.

所有消息都以 `[ATO]` 开头，并带有阶段与耗时。详细日志与逐岛跟踪日志可在“调试”折叠项中开关。
最终摘要会发布到 NDMF 控制台，包含总体数据与可折叠的细节块，列出图集尺寸、利用率、
来源贴图、岛数量与跳过原因。

## Deduplication / 去重

Two separate passes:

分为两个独立阶段：

1. **Before optimization** - source textures whose decoded pixels *and* import settings match are
   collapsed, so the same art is never atlased twice. If any member of a duplicate set is whitelisted,
   the survivor is whitelisted too.
   **优化前** —— 解码后像素**且**导入设置都相同的源贴图会被合并，避免同一份素材被打进图集两次。
   若重复集合中任一成员在白名单内，保留下来的那一份也视为白名单。
2. **After optimization** - generated atlases that came out byte identical are collapsed, then
   materials are compared by a full content signature (shader, every declared property, keyword set,
   render queue, GI flags, tags) and collapsed. This matters because optimization frequently makes
   previously different materials identical: two materials that only differed by which texture they
   pointed at become equal once both textures land in the same atlas.
   **优化后** —— 逐字节相同的生成图集会被合并；随后按完整内容签名（着色器、每一个声明的属性、
   关键字集合、渲染队列、GI 标志、标签）比较并合并材质。这一步很重要，因为优化经常让原本不同的
   材质变得相同：两个仅仅贴图不同的材质，在两张贴图落进同一图集后就完全一致了。

When a mesh ends up with several slots holding the very same **opaque** material, the sub meshes are
merged and the duplicate slots removed. Animated `m_Materials.Array.data[N]` bindings are reindexed
so animations keep addressing the right slot. Merging is skipped entirely when any animation swaps
those slots, and it is never applied to transparent materials because merging changes draw order.

当一个网格最终有多个槽持有完全相同的**不透明**材质时，子网格会被合并、多余的槽被删除。
动画中的 `m_Materials.Array.data[N]` 绑定会被重新索引，使动画仍能定位到正确的槽。
只要有动画切换这些槽就完全跳过合并；也绝不对透明材质应用，因为合并会改变绘制顺序。

## Memory behaviour / 内存行为

Decoded textures live on the GPU behind a budgeted LRU cache (`LinearSourceCache`), pinned only while
the group that needs them is being solved or composed. Peak GPU memory is therefore bounded by the
largest single UV group rather than by the whole avatar - decoding everything up front would need
many gigabytes on an avatar with dozens of 4K textures. The budget defaults to a quarter of the
reported graphics memory with a 256 MB floor, and the report prints the peak and the number of
re-decodes so the setting can be judged.

解码后的贴图通过有预算的 LRU 缓存（`LinearSourceCache`）驻留在 GPU 上，只在需要它们的组正在求解或
合成时才被固定。因此 GPU 显存峰值由最大的单个 UV 组决定，而不是整个 Avatar——
对于拥有几十张 4K 贴图的 Avatar，一次性全部解码会需要数 GB。预算默认取报告显存的四分之一、
下限 256 MB；报告中会打印峰值与重新解码次数，便于判断该设置是否合适。

## Verification / 校验

`Tools~/OfflineVerify/verify.sh` compiles the entire package against **real Unity reference
assemblies and the real NDMF sources**, then runs the algorithms that can execute outside Unity:

`Tools~/OfflineVerify/verify.sh` 用**真实的 Unity 参考程序集与真实的 NDMF 源码**编译整个包，
然后运行可以脱离 Unity 执行的算法：

* CIEDE2000 against the official Sharma / Wu / Dalal (2005) verification data - all 21 pairs match to
  four decimals. / CIEDE2000 对照官方 Sharma / Wu / Dalal（2005）验证数据 —— 21 组全部吻合到 4 位小数。
* The bit mask shape packer: concave nesting, 90 degree rotation, padding separation, snapshot
  rollback and overlap refusal. / 位掩码形状装箱器：凹槽嵌套、90 度旋转、padding 分隔、快照回滚、拒绝重叠。
* The reference-space to atlas-space UV mapping, including the rotation convention that must agree
  exactly with `Hidden/ATO/IslandBlit`. / 参考空间到图集空间的 UV 映射，
  含必须与 `Hidden/ATO/IslandBlit` 完全一致的旋转约定。
* The candidate atlas pool ordering and constraints. / 候选图集池的排序与约束。

What this **cannot** cover is anything that needs a live Unity: GPU blits, mesh surgery, the NDMF
pass itself. Those still have to be validated by baking a real avatar.

它**无法**覆盖的是一切需要活的 Unity 的部分：GPU blit、网格手术、NDMF pass 本身。
这些仍然必须通过烘焙真实 Avatar 来验证。

## Licence

MIT.
