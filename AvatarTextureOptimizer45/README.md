# Avatar Texture Optimizer (ATO)

**面向 VRChat Avatar 的开源 NDMF 贴图优化工具** · An open-source NDMF texture optimizer for VRChat avatars

[English](#english) · [简体中文](#简体中文)

---

<a name="english"></a>
## English

### What it does

ATO analyzes every mesh on your avatar, builds a mapping from mesh UVs to textures, and — driven by a perceptual
quality algorithm — shrinks UV islands, crops unused texture regions, and repacks islands into one or more atlases.
It only ever modifies **meshes (UVs) and texture references**; no other material property is touched.

Highlights:

- **Perceptual quality-driven scaling**: linear-space resampling with premultiplied-alpha downsampling;
  evaluated with **MS-SSIM** (+SSIM fallback for small islands) + **ΔE (CIEDE2000)** + alpha
  (**clipped-outline IoU** for cutout / **linear RMSE** for blend) for color textures; **angle error + p95** for
  normal maps; **per-used-channel linear RMSE** for grayscale. Binary search finds the smallest size that passes
  all thresholds; per-axis refinement handles anisotropy. The resized island is upsampled back to the original
  size and compared against the original.
- **Texel-density aware**: min/max pixel density in px/m (default 2048–4096, presets 512/1024/2048/4096/8192),
  clamped by the island's real physical size; blendshape (weight 0 vs 100, max) and animated scale are accounted for.
- **Texture type groups**: color / normal / mask / grayscale textures form separate atlas groups
  (split by color space and filter mode too), so a lone normal map no longer wastes 9/10 of an atlas.
  Textures used both with and without a normal map join the "with normal" group.
- **UV groups**: every texture sharing one UV (including textures switched by animation) forms a UV group;
  all of them keep the **same normalized rect across atlases** (weakest-link shared scaling), so UVs stay correct
  when a texture is sampled from several atlases.
- **Atlas packing**: 4px-granularity raster bitmasks, full-scan Bottom-Left-Fill, 90° rotation steps
  (mask transpose — normal-map tangent data is never recomputed), candidate atlas pool
  (POT powers of two 64→8192, 4096 on mobile; experimental NPOT in 64px steps, verified with MipStreaming/Crunch,
  PVRTC filtered out automatically). Padding = max(user setting 4/8/16/32/64, ⌈max side/128⌉).
  Island-edge colors are dilated (GPU pull-push equivalent; alpha stays 0 for transparent atlases).
- **Animation-aware**: material slot switches, texture property animations, render-mode/cutoff animations,
  object enable/disable, scale and blendshape animations are all analyzed; references are rewritten
  non-destructively through NDMF's animator services; identical opaque material slots are merged
  (submeshes + animation bindings remapped).
- **Whitelist**: any object type (GameObject, Material, Texture, Animation, Mesh). Textures referenced by
  whitelisted objects skip everything; textures sharing UV with them skip atlasing but still get whole-texture
  scaling and import-parameter optimization. Out-of-bounds UVs that can be shift-normalized into [0,1] are
  normalized; wrap-seam-crossing islands are whitelisted with a warning. Textures with animated ST transforms
  or special usages (matcap, light-memory maps, decals...) are whitelisted automatically.
- **Deduplication**: pre-pass texture dedup by actual pixels + import settings (whitelist contaminates the group);
  post-pass material / texture / atlas dedup with full reference remapping.
- **Import optimization**: mipmaps + MipStreaming bound together (VRChat requirement), compression format per
  texture category (opaque/transparent/normal/grayscale) with safe enums, per-platform override
  (Windows/Android/iOS), platform capability validation at build time, Read/Write off + Clamp forced for atlases.
- **Safety first**: if any conversion might be unsafe, ATO falls back (keeps the original reference) and reports.
  A progress bar with cancellation is shown during builds; cancelling keeps temporary assets on disk but frees
  CPU/GPU/memory. The component removes itself from the final avatar, and a summary report is printed to the
  NDMF console.
- **i18n**: ships with English + 简体中文; every JSON file under `Editor/i18n/` becomes a selectable language.
  Defaults to Auto (follows NDMF's language), falls back to English.
- **Extensible**: public extension points for pre/post processors and custom shader texture-category resolvers.

### Quality presets

Thresholds are based on published research (Wang et al. MS-SSIM; CIEDE2000 JND ≈ 1–2.3; SSIM ≥ 0.95 as
high-quality convention):

| Preset | MS-SSIM | ΔE2000 (mean) | Alpha IoU | Alpha RMSE | Normal mean/p95 | Gray RMSE |
|---|---|---|---|---|---|---|
| Lossless | skip resize | — | — | — | — | — |
| High (default) | 0.99 | 1.5 | 0.98 | 0.01 | 1.0° / 2.0° | 0.004 |
| Medium | 0.97 | 3.0 | 0.95 | 0.02 | 2.0° / 4.0° | 0.008 |
| Low | 0.94 | 6.0 | 0.90 | 0.04 | 4.0° / 8.0° | 0.016 |
| Custom | user-defined, defaults = strictest (near-lossless) | | | | | |

Custom preset parameters default to the strictest values (equivalently near-lossless, i.e. "quality = 1"),
and are never overwritten by other presets.

### Requirements

- Unity 2022.3 LTS
- VRChat Avatars SDK 3.10.4+ (`com.vrchat.avatars`)
- NDMF 1.14.4+ (`nadena.dev.ndmf`)
- Optional but recommended: Modular Avatar 1.18.2+, Avatar Optimizer 1.9.17+, lilToon 2.3.4+
  (ATO runs after MA and before AAO; AAO compatibility uses its `UVUsageCompabilityAPI` via reflection,
  so AAO is not a hard dependency).

### Quick start

1. Add the package to your project (VCC/VPM repository or copy into `Packages/`).
2. Add the **Avatar Texture Optimizer** component to the avatar root (the GameObject with the
   `VRCAvatarDescriptor`). Only one instance is allowed per avatar.
3. That's it — defaults are the recommended values. Build & upload as usual; details appear in the Console
   (prefixed `[ATO]`) and the NDMF console.

### For third-party developers

See `Editor/ATOExtensions.cs`:

```csharp
// 自定义着色器贴图类别解析 / custom shader texture-category resolver
ATOMExtensionRegistry.Register(new MyResolver()); // IATOTextureCategoryResolver
ATOExtensionRegistry.Register(new MyPreProcessor()); // IATOPreProcessor
ATOExtensionRegistry.Register(new MyPostProcessor()); // IATOPostProcessor
```

Atlases are named `ATO_*`; generated assets are saved through NDMF's asset saver (temp assets, cleaned up by NDMF).

### Current status & roadmap

v0.1.0-alpha.2 is a feature-complete implementation: Burst row-parallel packing jobs (4px raster bitmasks,
BLF, padding dilation), a Burst row-parallel quality pipeline (resample → Gaussian moments → metric
reduction → p95), a batch bisection scheduler that evaluates all active islands in parallel per round,
GPU (RenderTexture) full-resolution resampling for large islands, GPU pull-push edge dilation (Jump Flooding)
with a CPU multi-source BFS fallback, queue-per-texture packing with two-phase trial/commit, and whole-atlas
size reduction based on per-island quality headroom. Remaining: in-Unity build verification, automated tests,
and NDMF preview support (currently **not supported**).
See `CLAUDE.md` for the full development log.

---

<a name="简体中文"></a>
## 简体中文

### 它能做什么

ATO 分析 Avatar 上每个网格，建立网格 UV 到贴图的映射关系，以感知质量算法为基准缩小 UV 岛、剔除未使用
贴图区域、把岛重排合并成一个或多个图集。它**只修改网格(UV)与贴图引用**，材质其余任何属性一律不动。

核心特性：

- **感知质量缩放**：线性空间重采样、透明贴图预乘 alpha 下采样；主色用 **MS-SSIM**(小岛回退 SSIM)＋
  **ΔE(CIEDE2000)**＋alpha(**Cutout 用 clip 后轮廓 IoU / Blend 用线性 RMSE**，按引用材质逐一评估取最严苛)；
  法线贴图解码-重采样-重归一化后比**角度误差均值+p95**；灰度贴图在被使用通道上比线性 RMSE(逐通道取最差)。
  二分搜索取全部达标的最小尺寸，再双轴独立细化处理各向异性；缩小后的覆盖区双线性上采样回原尺寸与原图比较。
- **像素密度**：px/m 最小/最大密度(默认 2048–4096，挡位 512/1024/2048/4096/8192)，受岛在原贴图上的真实
  物理尺寸钳制；形态键(0 与 100 取最大)与动画缩放的面积影响全部纳入计算。
- **贴图类型组**：主色/法线/蒙版/灰度各自成组(色彩空间与 filterMode 不同也分组)，一张法线贴图不再浪费
  9/10 的图集；同一贴图既用于有法线材质又用于无法线材质时归入"有法线"组。
- **UV 组**：同一 UV 对应的全部贴图(含动画切换出的)构成 UV 组，**跨图集保持同一归一化矩形**(木桶效应共享
  缩放)，保证同一 UV 在不同图集上位置一致。
- **图集装箱**：4px 粒度光栅位掩码＋全扫描 BLF＋90° 步进旋转(位掩码转置，法线切线数据绝不重算)＋候选图集池
  (POT 2 的 n 次幂 64→8192，移动端 4096；实验性 NPOT 64 步进，已验证支持 MipStreaming/Crunch，自动剔除
  PVRTC)。padding = max(用户设置 4/8/16/32/64, ⌈最大边长/128⌉)；岛边缘颜色外扩填充(透明图集 alpha 保持 0)。
- **动画感知**：材质槽切换、贴图属性动画、渲染模式/Cutoff 动画、物体启用、缩放、形态键动画全部分析；
  引用经 NDMF 动画服务非破坏改写；相同的不透明材质槽自动合并(子网格与动画槽索引同步重映射)。
- **白名单**：不限对象类型(GameObject/材质/贴图/动画/网格)。白名单引用的贴图跳过全部优化；同 UV 的其他贴图
  跳过图集化但仍参与整图缩放与导入参数优化。越界可平移归一的 UV 自动归一；跨 wrap 缝、ST 被动画、特殊用途
  (matcap/灯光记忆图/贴花)等一律白名单＋warning。
- **去重**：前置贴图去重(实际像素＋导入设置，白名单污染整组)；后置材质/贴图/图集去重并更新全部引用。
- **导入优化**：Mipmap 与 MipStreaming 绑定一个开关(VRChat 要求)；压缩格式按贴图类别(不透明/透明/法线/灰度)
  提供安全枚举；平台覆盖(Windows/Android/iOS)；构建期按平台能力校验并安全回退；图集强制关闭 Read/Write、
  强制 Clamp。
- **安全兜底**：任何可能不安全的转换都会回退并报告。烘焙显示进度并可取消(取消保留硬盘临时资产、释放
  CPU/GPU/内存)。烘焙完成后组件自动从成品移除，NDMF 控制台输出报告。
- **i18n**：内置英文与简体中文；`Editor/i18n/` 下有几个 json 就支持几种语言；默认 Auto 跟随 NDMF 语言，
  缺翻译回退英文。
- **可扩展**：提供前置/后置处理器与自定义着色器贴图类别解析器等公开接口。

### 质量挡位

阈值依据学术/业内研究设定(Wang et al. 的 MS-SSIM；CIEDE2000 恰可察觉差 JND≈1–2.3；SSIM≥0.95 高质惯例)：

| 挡位 | MS-SSIM | ΔE2000(均值) | Alpha IoU | Alpha RMSE | 法线均值/p95 | 灰度 RMSE |
|---|---|---|---|---|---|---|
| 无损 | 跳过缩放 | — | — | — | — | — |
| 高(默认) | 0.99 | 1.5 | 0.98 | 0.01 | 1.0° / 2.0° | 0.004 |
| 中 | 0.97 | 3.0 | 0.95 | 0.02 | 2.0° / 4.0° | 0.008 |
| 低 | 0.94 | 6.0 | 0.90 | 0.04 | 4.0° / 8.0° | 0.016 |
| 自定义 | 用户自定，默认全部为最严苛(等效近无损) | | | | | |

自定义挡位参数默认取最严苛值(即"质量=1"近无损)，不会被其他挡位覆盖。

### 环境要求

- Unity 2022.3 LTS
- VRChat Avatars SDK 3.10.4+ (`com.vrchat.avatars`)
- NDMF 1.14.4+ (`nadena.dev.ndmf`)
- 建议安装: Modular Avatar 1.18.2+、Avatar Optimizer 1.9.17+、lilToon 2.3.4+
  (ATO 在 MA 之后、AAO 之前执行；AAO 兼容经反射调用其 `UVUsageCompabilityAPI`，未安装 AAO 也能正常运行)

### 快速开始

1. 把本包加入工程(VCC/VPM 仓库或直接放入 `Packages/`)。
2. 在 Avatar 根对象(带 `VRCAvatarDescriptor` 的对象)上添加 **Avatar Texture Optimizer** 组件。
   整个 Avatar 只允许挂载一个。
3. 完成。默认值即推荐配置，正常构建上传即可；详细信息见 Console 中 `[ATO]` 开头的日志与 NDMF 控制台报告。

### 第三方开发者

见 `Editor/ATOExtensions.cs`：

```csharp
// 自定义着色器贴图类别解析 / custom shader texture-category resolver
ATOMExtensionRegistry.Register(new MyResolver()); // IATOTextureCategoryResolver
ATOExtensionRegistry.Register(new MyPreProcessor()); // IATOPreProcessor
ATOExtensionRegistry.Register(new MyPostProcessor()); // IATOPostProcessor
```

图集以 `ATO_` 命名；生成的资产经 NDMF 资产保存器写入临时目录(由 NDMF 统一清理)。

### 当前状态与路线图

v0.1.0-alpha.2 为功能完整实现：Burst 行并行装箱作业(4px 光栅位掩码/BLF/padding 膨胀)、Burst 行并行质量
流水线(重采样→高斯矩→指标汇总→p95)、批量二分搜索调度器(每个调度回合并行评估全部活跃岛)、GPU(RenderTexture)
全分辨率重采样(大岛)、GPU pull-push 边缘外扩(跳跃洪泛 JFA, CPU 多源 BFS 回退)、按贴图队列装箱(试装两阶段
原子提交)、基于个体质量余量的图集整体收缩。剩余：Unity 实测验证、自动化测试、NDMF 预览(目前**暂不支持**)。
完整开发记录见 `CLAUDE.md`。

---

MIT License — see LICENSE.
