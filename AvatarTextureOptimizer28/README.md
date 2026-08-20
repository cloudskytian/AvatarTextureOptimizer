# Avatar Texture Optimizer (ATO)

> **EN** — A non-destructive NDMF texture and atlas optimizer for VRChat avatars.
> **ZH** — 面向 VRChat Avatar 的非破坏性 NDMF 贴图 / 图集优化工具。

`net.fosa.avatar-texture-optimizer` · Unity 2022.3 · NDMF ≥ 1.14.4

---

## What it does / 它做什么

**EN** — ATO builds a mapping from *mesh UVs* to *textures*, rather than from materials to textures.
Because the mapping is anchored on the UV layout, swapping a material for another one that uses the
same texture changes nothing, so the work can be reused. On top of that mapping it:

1. Shrinks every UV island to the smallest size that still meets a **perceptual quality target**.
2. Discards the UV space no triangle actually uses.
3. Repacks the surviving islands into one or more **shape-aware atlases**.
4. Rewrites mesh UVs and material *texture references only* — never any other shader parameter.

**ZH** — ATO 建立的是 **网格 UV → 贴图** 的映射，而不是材质 → 贴图的映射。
由于映射锚定在 UV 布局上，把材质换成另一个使用相同贴图的材质不会造成任何变化，因此成果可以复用。
在此映射之上，它会：

1. 把每个 UV 岛缩小到仍能满足**感知质量目标**的最小尺寸。
2. 剔除没有任何三角形实际使用的 UV 空间。
3. 把留存的岛重新打包成一张或多张**形状感知图集**。
4. 只重写网格 UV 与材质的**贴图引用**——绝不改动任何其他着色器参数。

---

## Install / 安装

Add the package to your VPM project, or drop the folder into `Packages/`.
NDMF is a hard dependency; VRChat SDK Avatars is required at runtime.

Then add **Avatar Texture Optimizer** to your avatar root (the object with the `VRCAvatarDescriptor`).
Exactly one component per avatar is allowed; anything else aborts the build with a clear error.

把包加入你的 VPM 工程，或直接把文件夹放进 `Packages/`。
NDMF 是硬依赖，运行时需要 VRChat SDK Avatars。
随后把 **Avatar Texture Optimizer** 加到 Avatar 根节点（带 `VRCAvatarDescriptor` 的对象）。
每个 Avatar 只允许挂载一个组件，否则会以明确的错误中止构建。

---

## Quality tiers / 质量挡位

| Tier | targetQuality | MS-SSIM ≥ | ΔE00 mean ≤ | ΔE00 p95 ≤ | Cutout IoU ≥ | Blend αRMSE ≤ | Normal mean ≤ | Normal p95 ≤ | Data RMSE ≤ |
|---|---|---|---|---|---|---|---|---|---|
| Lossless | 1.00 | *rescaling skipped entirely* | | | | | | | |
| Very High | 0.95 | 0.995 | 1.0 | 2.0 | 0.999 | 0.004 | 1.0° | 2.0° | 0.005 |
| **High** *(default)* | 0.85 | 0.99 | 2.0 | 4.0 | 0.997 | 0.008 | 2.0° | 4.0° | 0.010 |
| Medium | 0.70 | 0.98 | 3.0 | 6.0 | 0.99 | 0.016 | 3.5° | 7.0° | 0.020 |
| Low | 0.50 | 0.96 | 5.0 | 10.0 | 0.98 | 0.030 | 5.0° | 10.0° | 0.040 |
| Custom | user-defined, defaults to Lossless, never overwritten by tier changes | | | | | | | | |

Sources for the thresholds:
MS-SSIM — Wang, Simoncelli & Bovik, *Multi-scale structural similarity for image quality assessment*,
Asilomar 2003. CIEDE2000 — Luo, Cui & Rigg 2001; ISO/CIE 11664-6; graphic-arts tolerance per ISO 12647.
Normal maps use angular error rather than RGB error. Data textures use a linear RMSE scaled from the
8-bit quantisation step (1/255 ≈ 0.0039).

阈值依据见上：MS-SSIM 出自 Wang 等 2003；CIEDE2000 出自 Luo/Cui/Rigg 2001 与 ISO/CIE 11664-6，
印刷容差参考 ISO 12647；法线贴图使用角度误差而非 RGB 误差；数据贴图使用以 8 位量化步长
（1/255 ≈ 0.0039）为基准放大的线性 RMSE。

---

## The target quality algorithm / 目标质量算法

Everything is measured in **linear space**. Textures with a meaningful alpha channel are downsampled
with **premultiplied alpha**. A candidate island scale is accepted only when *every applicable* metric
passes; the search then binary-searches for the smallest accepted scale.

* **Colour** — MS-SSIM + CIEDE2000 (mean and p95). Islands whose original bounding-box short side is
  below 176 px fall back to single-scale SSIM; below 11 px the SSIM term is dropped entirely.
* **Alpha** — Cutout materials are judged on the **silhouette IoU after applying the cutoff**; Blend
  materials on the **linear RMSE of the alpha ramp**. When a texture is referenced by several materials,
  every referencing material's mode and cutoff is evaluated and the strictest one wins.
* **Normal maps** — decoded, resampled, **renormalised**, re-encoded, then compared by angular error
  (mean and p95).
* **Data / grayscale** — linear RMSE on the channels that actually carry information, worst channel wins.

Comparison is always made by upsampling the reduced island back to its original size and diffing it
against the original. Compression loss is deliberately excluded from the metric.

Short circuits: `targetQuality == 1` skips rescaling and resampling entirely; a solid-colour island
collapses to `min(4, short side)` immediately.

Anisotropy: the search first finds a uniform scale that passes, then refines U and V independently, so a
long thin island is not padded out to a square.

Density clamp: island scale is additionally bounded by a **texel density** window derived from the
island's real-world surface area (default 2048–4096 px/m, presets 512 / 1024 / 2048 / 4096 / 8192).
Blend shapes are evaluated at weight 0 and 100 and the larger area wins; animated object scale is taken
at its maximum.

全部度量在**线性空间**进行；含有效 alpha 的贴图使用**预乘 alpha** 降采样。
只有当**所有适用**度量都通过时，候选缩放才被接受，随后二分搜索最小的可接受缩放。
细节同上（彩色 / alpha / 法线 / 数据四类度量、上采样回原尺寸后比较、不含压缩损失、
纯色与无损短路、先均匀后双轴细化、按真实世界面积推导的像素密度钳制、
形态键取 0 与 100 的较大者、动画缩放取最大值）。

---

## Atlas packing / 图集装箱

* Islands are rasterised into **4 px-granularity coverage bitmasks** and packed by **full-scan
  bottom-left-fill** on the real shape, not a bounding rectangle.
* Sorting: rasterised coverage descending, then longest side descending.
* 90° rotation is obtained by **transposing the mask** and swapping U/V in the mesh —
  **vertex tangents are never recomputed**.
* Candidate atlas pool: powers of two from 64 to 8192 (4096 on mobile) by default, or an
  **experimental NPOT** mode stepping by 64 px. Candidates smaller than the queue's total covered area
  are discarded, and the rest are tried in ascending area then ascending aspect ratio, so the smallest
  and most square atlas that fits wins.
* A **UV group is atomic**: every island of a group lands in the same atlas. If a group cannot fit the
  largest candidate even alone, it drops out of atlasing, falls back to whole-texture scaling, and a
  warning is reported.
* **Texture type groups**: UV groups are partitioned by their occupied slot set plus colour space,
  filter mode and wrap mode. Ten colour textures of which only one has a normal map therefore never
  produce a 90 %-empty normal atlas.

* 岛被光栅化为 **4 像素粒度的覆盖位掩码**，按**全扫描 BLF** 依真实形状装箱，而非包围矩形。
* 排序：光栅化覆盖面积降序，再按最长边降序。
* 90° 旋转通过**转置掩码** + 交换网格 U/V 实现，**绝不重算顶点切线**。
* 候选池默认为 64 到 8192（移动端 4096）的 2 次幂，或以 64 像素步进的**实验性 NPOT** 模式。
  小于队列覆盖总面积的候选被丢弃，其余按面积升序、长宽比升序尝试，最小且最接近正方形者胜出。
* **UV 组是原子的**；单独也装不下最大候选图集的组会退出图集化、回退到整图缩放，并报出警告。
* **贴图类型组**按槽位集合 + 色彩空间 + 过滤模式 + 循环模式划分，避免生成 90% 空白的法线图集。

### Padding and mip bleeding / padding 与 mip 渗色

Padding is `max(userMinimum, ceil(maxSide / 128))`, with the user minimum selectable from
4 / 8 / 16 / 32 / 64 px. Empty atlas space is filled by **pull-push extrapolation** (Gortler et al. 1996)
so the colour extends infinitely outwards; for transparent atlases the alpha of filled texels is forced
back to 0.

Mip levels are **capped at `log2(padding) + 1`**. A mip level *N* texel averages 2^*N* base texels, so
once 2^*N* exceeds the padding the mip starts mixing neighbouring islands. ATO stops the chain where it
is still correct instead of shipping visible colour bleeding at distance. Mip chains are generated by
ATO itself with an alpha-aware box filter, not by Unity's automatic generation.

padding 为 `max(用户最小值, ceil(最大边长 / 128))`，用户最小值可选 4 / 8 / 16 / 32 / 64 像素。
图集空白区域由 **pull-push 外推**填满，颜色无限外扩；透明图集中被填充纹素的 alpha 强制归零。
mip 层数**上限为 `log2(padding) + 1`**：mip 第 *N* 层的一个纹素平均了 2^*N* 个基础纹素，
一旦 2^*N* 超过 padding 就会混合相邻的岛。ATO 在仍然正确的地方截断 mip 链，
而不是把远处可见的串色交付出去。mip 链由 ATO 自己用感知 alpha 的盒式滤波生成。

---

## Safety rules / 安全规则

A texture is treated as **whitelisted** (skips every optimisation) whenever ATO cannot *prove* it is a
plain mesh-UV lookup:

* non-identity `_ST`, including values written by animation;
* lilToon-style `<Tex>_ScrollRotate` (in-shader UV animation);
* lilToon-style `<Tex>_UVMode` selecting MatCap / Rim or any procedural coordinate;
* decal, parallax, matcap, refraction, screen-space and similar samplers;
* a UV island that spans a wrap seam and therefore genuinely relies on repeat sampling;
* a shader whose property table cannot be read.

All of these are read from the **real shader property table** via `ShaderUtil`, never guessed from a
name list, so future lilToon versions and third-party shaders using the standard keywords keep working.
Properties flagged `[NoScaleOffset]` have no `_ST` by construction and are safe on that axis.

Output format safety is enforced and cannot be overridden:
a texture with meaningful alpha is never written to a format without an alpha channel, and a data
texture that actually uses more than one channel is never written to a single-channel format even if
BC4 was selected. Both downgrades are reported in the NDMF console.

Atlases are always **Clamp** and always **non-readable**. Mipmap and Mip Streaming are a single bound
toggle, because VRChat requires Streaming Mip Maps whenever mip maps exist.

只要 ATO 无法**证明**某贴图是普通的网格 UV 查表，它就按**白名单**处理（跳过全部优化）：
非单位 `_ST`（含动画写入）、lilToon 式 `<Tex>_ScrollRotate`、
`<Tex>_UVMode` 选中 MatCap / Rim 等程序化坐标、贴花 / 视差 / matcap / 折射 / 屏幕空间等采样器、
跨 wrap 缝而真正依赖 repeat 采样的 UV 岛、属性表无法读取的着色器。
以上全部通过 `ShaderUtil` 从真实着色器属性表读取，绝不靠名字列表猜测。
输出格式安全规则强制生效且无法覆盖；图集恒为 Clamp 且不可读；
Mipmap 与 Mip Streaming 合并为一个开关（VRChat 要求二者绑定）。

---

## Build order / 执行顺序

ATO runs in `BuildPhase.Optimizing` and declares `BeforePlugin("com.anatawa12.avatar-optimizer")`.
Modular Avatar does its work in `BuildPhase.Transforming`, which NDMF always schedules first, so
**after MA, before AAO** holds without a fragile dependency on MA's internal pass names.

When Avatar Optimizer is installed, ATO calls its `UVUsageCompabilityAPI` (spelling as in AAO) through
**reflection** to evacuate the original UVs of any channel AAO consumes. Reflection is mandatory here:
AAO's API assembly is `autoReferenced: false`, so naming it in our asmdef would break compilation for
everyone who has not installed AAO. Note the API only accepts `SkinnedMeshRenderer`.

ATO 运行在 `BuildPhase.Optimizing` 并声明 `BeforePlugin("com.anatawa12.avatar-optimizer")`。
MA 的主体工作在 `BuildPhase.Transforming`，NDMF 总是先调度它，
因此"MA 之后、AAO 之前"无需依赖 MA 的内部 Pass 名即可成立。
安装了 AAO 时，ATO 通过**反射**调用其 `UVUsageCompabilityAPI` 疏散原始 UV；
必须用反射，因为 AAO 的 API 程序集是 `autoReferenced: false`。该 API 只接受 `SkinnedMeshRenderer`。

---

## Localization / 本地化

Drop a `<bcp47>.json` file into `Packages/net.fosa.avatar-texture-optimizer/Localization/` or into
`Assets/ATO_Localization/`. The file name is the language code; the content is a flat
`{"key": "value"}` object. However many files exist, that many languages appear in the dropdown.
`Auto` follows NDMF's language preference and falls back to English for missing keys.
User files in `Assets/ATO_Localization/` override the bundled ones key by key.

把 `<bcp47>.json` 放进 `Packages/net.fosa.avatar-texture-optimizer/Localization/`
或 `Assets/ATO_Localization/` 即可。文件名即语言代码，内容为扁平的 `{"键": "值"}` 对象。
有几个文件就显示几个语言。`Auto` 跟随 NDMF 的语言设置，缺失的键回退到英文。
`Assets/ATO_Localization/` 中的用户文件会逐键覆盖内置文件。

---

## Extension API / 扩展接口

```csharp
using net.fosa.ato.editor.api;

[InitializeOnLoad]
static class MyShaderSupport
{
    static MyShaderSupport() => ATOExtensionRegistry.Register(new Provider());

    class Provider : IShaderSupportProvider
    {
        public bool Handles(Shader s) => s.name.StartsWith("MyCompany/");

        public bool Describe(Material m, string property,
            out bool safe, out int uvChannel, out TextureSlot slot)
        {
            safe = property == "_MainTex";
            uvChannel = 0;
            slot = TextureSlot.Color;
            return true;   // EN: false falls back to ATO's own analysis. ZH: 返回 false 则回退到 ATO 自己的分析。
        }
    }
}
```

`IATOBuildObserver` additionally exposes `OnGroupsBuilt`, `OnAtlasesBaked` and `OnRemapReady`.

---

## Diagnostics / 诊断

Every log line is prefixed with `[ATO]`. Verbose and Trace levels are toggles on the component; the
instrumentation is permanent, not added after a bug appears. The final NDMF console report shows a
one-line summary by default and expands to per-atlas details: source textures, island counts, atlas
size, utilisation, byte savings, deduplication counts, a full timing tree and every warning.

Baking shows a cancellable progress bar. Cancelling releases all CPU/GPU memory and stops the build,
but intentionally leaves temporary assets on disk so a partially written asset container is not
corrupted.

所有日志以 `[ATO]` 开头。Verbose 与 Trace 由组件上的开关控制；埋点是永久的，
不是等出了 bug 才临时添加。最终的 NDMF 控制台报告默认展示单行总览，展开后包含逐图集明细：
源贴图、岛数量、图集尺寸、利用率、体积节省、去重数量、完整耗时树以及所有警告。
烘焙时显示可取消的进度条；取消会释放全部 CPU/GPU 内存并终止构建，
但刻意保留硬盘上的临时资产，以免部分写入的资产容器被破坏。

---

## Known limitations / 已知限制

* NDMF **preview is not supported** yet; the optimisation only runs on bake / upload.
* Cross-island bleeding within a single mip level is bounded by the padding but not eliminated.
* The experimental NPOT mode removes compression formats that the target platform cannot pair with a
  non-power-of-two size.
* Blend shapes are evaluated at 0 and 100 only. Combinations, negative weights and weights above 100
  are not explored — the combinatorial blow-up would dominate bake time for a negligible accuracy gain.

* 暂不支持 NDMF **预览**，优化只在烘焙 / 上传时执行。
* 单个 mip 层内的跨岛渗色由 padding 限制但未被完全消除。
* 实验性 NPOT 模式会剔除目标平台无法与非 2 次幂尺寸配合的压缩格式。
* 形态键只在 0 与 100 求值，不枚举组合、负权重与超过 100 的权重。

---

## License

MIT.
