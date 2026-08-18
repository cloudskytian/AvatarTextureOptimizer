# Avatar Texture Optimizer (ATO)

开源 NDMF 工具：分析 VRChat Avatar 网格 UV，按目标质量缩放 UV 岛，剔除未使用纹素，并在安全前提下重组成图集。

Package: `net.fosa.avatar-texture-optimizer`

## 给使用者

1. 用 VCC / VPM 安装本包，依赖 NDMF ≥ 1.8 与 VRChat Avatars SDK。
2. 在带 **VRCAvatarDescriptor** 的根物体上添加 `FOSA / Avatar Texture Optimizer`。整个 Avatar 只能有一个。
3. 选择质量挡位（默认 High）与是否生成图集。高级参数折叠在 Inspector 里。
4. 白名单可拖入网格、材质、贴图、动画等任意对象：其引用的贴图跳过优化。
5. 用 NDMF 手动烘焙或直接上传。构建时显示阶段进度，可取消（临时资产保留，内存会释放）。
6. 成品上的本组件会被移除。报告出现在 NDMF 控制台与 `[ATO]` 日志。

**不会改材质里除贴图引用以外的任何着色器参数。**

### 质量挡位

| 挡位 | 含义 |
| --- | --- |
| NearLossless / Custom 默认 | 目标质量 1，不缩放岛，原样拷贝 |
| Ultra | SSIM 0.99，ΔE≤1.0 |
| High（默认） | SSIM 0.97，ΔE≤2.3（CIEDE2000 恰可辨差） |
| Medium / Low | 更激进 |

像素密度默认 2048–4096 px/m，可改。平台覆盖（PC / Android / iOS）勾选后才显示。

### 安全回退

- 有 ST 变换、动画改 ST、UV 跨 wrap 缝、非 Texture2D、无法分析的着色器 → 视作白名单并 Warning
- 单张贴图的全部岛装不进最大图集 → 放弃该 UV 组图集化
- 透明图不会落到无 alpha 格式；多通道灰度不会被压成单通道

## 给第三方开发者

扩展点见 `Net.Fosa.AvatarTextureOptimizer.Editor.AtoExtensionPoints`：

- `AfterScan` / `AfterIslands` / `AfterAtlas`
- `OverrideShaderAnalysis`
- `OverrideWhitelist`

i18n：在 `Runtime/Localization/` 增加 `xx.json` 即可出现新语言。`Auto` 读取 NDMF `LanguagePrefs`。

处理顺序：Modular Avatar 之后、Avatar Optimizer 之前。若安装了 AAO，会在改 UV 前调用 `UVUsageCompabilityAPI`。

处理顺序：Modular Avatar 之后、Avatar Optimizer 之前。安装 AAO 时会在改 UV 前调用 `UVUsageCompabilityAPI`。

lilToon：自动读 `_Xxx_UVMode`（0–3 为 UV0–3，MatCap/Rim 跳过）、`_ScrollRotate`、透明模式与 Cutoff。无法分析的着色器会白名单并 Warning。

### 程序集

- Runtime: `net.fosa.avatar-texture-optimizer.runtime`
- Editor: `net.fosa.avatar-texture-optimizer.editor`

Compute：`Editor/Shaders/AtoQuality.compute`（GPU 辅助；失败回退 CPU）。Burst Job：`Editor/Burst/AtoRasterJobs.cs`。

## 许可

MIT
