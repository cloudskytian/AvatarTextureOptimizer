# Avatar Texture Optimizer

面向 VRChat Avatar 的安全优先 NDMF 贴图与 UV 岛优化器。ATO 在 Modular Avatar 之后、Avatar Optimizer 之前分析 Avatar，并在 NDMF 构建副本上按质量门槛缩放、裁剪、去重和打包贴图；遇到无法证明安全的输入时保留原状并报告原因。

> **当前验证状态（0.1.0，Unreleased）**：64 个 C# 已用 Unity 2022.3.22f1 随附 Roslyn 和实际引用直接编译，Runtime、Editor、Tests 程序集均无诊断；三轮独立 QA 已通过源码与直接编译边界。Unity Test Runner 仍为 **0/263**，Compute/Burst、真实依赖工程、目标 GPU 和 Avatar 烘焙尚未执行。此归档不是已完成动态验证的正式发布；发布者必须自行完成下方门禁。

## 功能概览

- 分析 `MeshRenderer` / `SkinnedMeshRenderer`、材质槽、动画材质与贴图绑定、UV0–UV7、纹理 ST、透明模式及已知 shader 属性。
- 按贴图用途、色彩空间、过滤方式、UV 通道、动画闭包和共享 UV 建组；可对像素与导入语义相同的材质/贴图/图集去重。
- 按 UV 岛质量搜索目标尺寸，使用 SSIM / MS-SSIM、CIEDE2000、透明轮廓 IoU、Alpha RMSE、法线角误差和灰度通道 RMSE 复核。
- 以 Burst 位掩码光栅化岛形状，支持旋转、重叠岛、BLF 形状装箱、不限页数的 POT 图集，以及可选实验性 NPOT 图集。
- GPU 线性空间重采样、透明色预乘处理及 pull-push padding；按用途选择安全压缩、Mipmap 与 Mip Streaming。
- 重排构建副本中的网格 UV，仅替换贴图引用；不会主动改写其他材质参数。仅在已证明安全时合并不透明材质槽并同步动画索引。
- 可关闭图集，仅执行整图质量缩放与安全去重。
- PC、Android、iOS 独立覆盖；对象/材质/贴图白名单；英文、简体中文和自动语言回退。
- 所有消息使用 `[ATO]` 前缀，并向 NDMF 报告 warning/error；提供分阶段耗时和可开关调试分类。
- 构建结束移除 ATO 组件；**不注册 NDMF 实时预览**，场景中的源 Avatar 和源资产保持不变。

## 兼容性

| 项目 | 版本 / 说明 |
|---|---|
| Unity | 2022.3 LTS |
| VRChat SDK - Base | 3.10.4，由 Avatars 3.10.4 精确依赖 |
| VRChat SDK - Avatars | 3.10.4，必需 |
| NDMF | 1.14.4，必需 |
| Burst | 1.8.12 |
| Collections | 2.1.4 |
| Mathematics | 1.2.6 |
| Modular Avatar | 1.18.2，可选；ATO 排在其后 |
| Avatar Optimizer | 1.9.17，可选；ATO 排在其前，并按精确包/程序集门禁调用 UV Usage Compatibility API |
| lilToon | 2.3.4，为已知属性分析目标；无运行时依赖 |
| avatar-compressor | 0.9.0，源码审计参照；无直接 API 依赖，不构成兼容性承诺 |
| Light Limit Changer | 2.13.0，源码审计参照；无直接 API 依赖，不构成兼容性承诺 |

`package.json` 声明必需的 UPM/VPM 依赖。可选工具不存在时 ATO 仍应工作；ATO 不通过反射猜测未知 AAO API。

## 安装

正式发布后优先使用发布页提供的 VPM/UPM 安装方式。直接使用源码时：

1. 将本 README 所在的完整包目录复制到 Unity 项目的 `Packages/net.fosa.avatar-texture-optimizer/`（在源码仓库中它已经位于该路径）。
2. 用 VCC 或 Package Manager 安装上表中的必需依赖。
3. 等待 Unity 导入完成，确认 Console 没有编译错误。
4. 在 Test Runner 的 **EditMode** 中运行程序集 `net.fosa.avatar-texture-optimizer.editor-tests` 后再用于生产项目。

不要只复制 `Runtime` 或 `Editor` 子目录；Compute Shader、i18n JSON、程序集定义和 `.meta` 都是包的一部分。

## 快速开始

1. 在含 `VRCAvatarDescriptor` 的 **Avatar 根对象**上添加 **Avatar Texture Optimizer**。
2. 同一 Avatar 整个层级只能有一个 ATO 组件。Inspector 中出现红色放置错误时，构建会拒绝执行。
3. 第一次使用保留默认 **Balanced**，按需把绝不能改动的对象、材质或贴图放入白名单。
4. 正常执行 NDMF/VRChat Avatar 构建。ATO 只修改构建副本并将生成资产交给 NDMF 保存。
5. 在 NDMF Console / Unity Console 查看 `[ATO]` 的来源、岛数、页面尺寸、利用率、节省量、fallback 和阶段耗时。
6. 检查构建出的 Avatar，重点查看透明边缘、法线、高光、动画换装/换贴图、BlendShape、远距离 Mipmap 和各目标平台。

ATO 没有实时预览。这是刻意的安全边界：只有实际构建才能提供完整 Animator Services、资产保存和事务提交环境。

## 设置说明

### 质量预设

| 预设 | 用途 | `targetQuality` | 特点 |
|---|---|---:|---|
| Performance | 更积极节省 | 0.65 | 允许较宽的感知、Alpha、法线与通道误差 |
| Balanced | 默认 | 0.82 | 质量与资源占用折中 |
| High | 质量优先 | 0.92 | 更严格的全部指标 |
| Ultra | 很高质量 | 0.98 | 接近原图的严格阈值 |
| Near Lossless | 精确旁路 | 1.0 | 整条贴图优化旁路，不分析、不重采样、不改 UV/引用 |
| Custom | 高级用户 | 默认 0.999 | 默认接近无损但仍真实求解；可独立调整各指标 |

预设会联动高级阈值。非 Custom 预设中的阈值为只读展示；切换预设时由运行时再次修复非法、NaN 或越界序列化值。

**研究依据与边界：** ATO 选择结构相似度、颜色差和用途特定误差，是参考了 SSIM、MS-SSIM 与 CIEDE2000 的公开研究；具体预设数值是面向 Avatar 的保守工程策略，并非论文直接给出的通用“不可见阈值”，也不能替代作者对目标设备和 Avatar 的目视 QA。

- Z. Wang et al., *Image Quality Assessment: From Error Visibility to Structural Similarity*, IEEE TIP 13(4), 2004, DOI: [10.1109/TIP.2003.819861](https://doi.org/10.1109/TIP.2003.819861)
- Z. Wang, E. P. Simoncelli, A. C. Bovik, *Multi-scale Structural Similarity for Image Quality Assessment*, 2003, DOI: [10.1109/ACSSC.2003.1292216](https://doi.org/10.1109/ACSSC.2003.1292216)
- G. Sharma, W. Wu, E. N. Dalal, *The CIEDE2000 Color-Difference Formula: Implementation Notes, Supplementary Test Data, and Mathematical Observations*, 2005, DOI: [10.1002/col.20070](https://doi.org/10.1002/col.20070)

### 图集与像素密度

- **Generate Atlases**：关闭时不改写 UV/图集，只走整图优化和安全去重。
- **Maximum Atlas Size**：256–8192。规划器可生成任意数量的页面，不把所有内容强塞进单页。
- **Minimum Padding**：4/8/16/32/64 px。图集使用 pull-push 填充未覆盖像素，并对 Mipmap 安全作额外门禁。
- **Experimental NPOT**：允许非 2 次幂候选。目标平台或 LOD 无法证明安全时整页回退。
- **Minimum / Maximum Pixel Density**：约束单位几何面积的纹素密度；分析出的动画最大缩放会进入保守下界。

### 输出与压缩

可分别设置不透明颜色、Alpha 颜色、法线、灰度数据的压缩及 Mipmap/Mip Streaming。`Auto` 根据用途、Alpha 和平台选择格式；显式格式不兼容时使用安全 fallback，而不是强制产生错误数据。生成贴图以 `ATO_` 命名，关闭 Read/Write，wrap mode 为 Clamp。

三个输出开关彼此独立：**Deduplicate generated materials** 对完整序列化状态相同的生成材质执行全局去重；**Deduplicate textures and atlases** 控制贴图/图集 canonicalization；**Merge proven-safe opaque material slots** 只在同一 Renderer 内通过 shader、动画、PropertyBlock、拓扑及严格分离 bounds 等全部证明后合并槽。关闭全局材质去重不会关闭单独启用的安全槽合并；被合并的等价槽只保留一个材质引用，其他 Renderer/槽不会因此被全局去重。

支持的显式选项：RGBA32、RGB24、BC7、BC5、DXT1、DXT5、ETC2 RGB、ETC2 RGBA8、ASTC 4×4、ASTC 6×6。实际可用性仍取决于构建平台与 Unity 2022.3。

### 平台覆盖

PC、Android、iOS 覆盖彼此独立。未启用时使用 Common；启用后使用该平台的完整设置副本。流水线根据 NDMF 构建平台解析一次，并再次执行设置清理。

### 白名单

白名单接受 Texture2D、Material、GameObject 或其他 Unity 对象。ATO 通过依赖收集扩展为贴图集合；GameObject 会包含其子层级组件。白名单贴图及与其像素去重的等价贴图不会进入图集或质量缩放，避免通过别名绕过保护。

### 调试日志

开启 Verbose 后可分别打开：Analysis、UV Islands、Quality、Packing、Generated Assets、Animation Rewrite、Resource Lifetime。常规阶段摘要和 warning/error 同样带 `[ATO]` 前缀。调试日志可能很多，提交问题时请附上完整构建日志。

## 安全与 fallback 规则

ATO 的原则是“无法证明就不转换”。常见保留原因包括但不限于：

- 不支持的 shader 复合语义、非法/非有限 ST、贴花、repeat/mirror 或 UV 越界无法安全归一；
- 动画材质/贴图/透明模式/Cutoff 无法形成闭包，或动画 binding 无法唯一映射；
- additive scale、骨骼相对形变、Scale Constraint、未知动画权重域；
- BlendShape 名称不唯一、frame/当前/动画权重不在有限 `[0,100]`、缺少显式 100% frame、存在 additive binding；
- 各向异性或非 2 次幂的源→图集 LOD 比例无法保持 Mipmap 导数语义；
- 编辑器后端缺少 Compute、Async GPU Readback 或 RGBA16F/R8 必需用途，或输出超过设备纹理轴上限/保守 4096² 总像素预算；
- 页面最终质量复核、压缩支持、AAO 注册或事务提交失败。

BlendShape 可缩小门禁会检查 0%、100% 与所有中间 authored frame，并按每个三角形建立形变包络；否则保留原岛分辨率。动画、网格、材质、AAO 注册和资源所有权都在提交事务中处理；回滚不完整时 ATO 保留可能仍被 Avatar 引用的生成对象，避免悬空引用，并使构建失败。

## Shader 与材质约束

ATO 会自分析 shader 的 Texture 属性，并对标准属性与 lilToon 已知语义进行分类。扩展可以覆盖纹理用途、UV 通道或拒绝未知属性。ATO 只改网格/UV/贴图引用（以及已证明安全的材质槽/动画索引）；不会主动调整颜色、强度、混合参数、Cutoff 或其他非贴图材质参数。

“识别到属性名”不等于可以安全打图集。任何贴图合成语义、动态关键字或未知坐标变换仍可能让整组 fallback。

## 扩展 API

Editor-only 扩展实现 `Fosa.AvatarTextureOptimizer.Editor.API.IATOExtension`，并注册到 `ATOExtensionRegistry`：

```csharp
using Fosa.AvatarTextureOptimizer.Editor.API;
using UnityEditor;

[InitializeOnLoad]
internal static class MyATOExtensionRegistration
{
    private static readonly IATOExtension Extension = new MyATOExtension();

    static MyATOExtensionRegistration()
    {
        ATOExtensionRegistry.Register(Extension);
    }
}
```

扩展按 `Priority` 升序执行；相同优先级保持注册顺序。`BeforeAnalysis` 可以调整设置，随后 ATO 会清理设置；`ClassifyTexture` 可以设置 Kind、Alpha 用途、UV 通道或 `RejectAsUnsafe`；`BeforeCommit` 收到的是只读语义快照，对其中设置的修改不会进入提交。扩展必须确定、可重入，并在任何不确定输入上拒绝优化。

## 开发与验证

主要目录：

- `Runtime/`：组件、设置和公开枚举。
- `Editor/Analysis/`：Avatar、动画、shader、UV 与纹理分析。
- `Editor/Quality/`：GPU 重采样、Burst mask、质量指标和尺寸求解。
- `Editor/Atlas/`：布局、打包、图集、网格/材质/动画提交及 AAO 桥。
- `Editor/Pipeline/`：NDMF 编排、进度、取消、报告、事务和资源生命周期。
- `Tests/Editor/`：EditMode 回归。

发布前最低门禁：Unity 2022.3 全量编译、全部 EditMode 测试、Compute Shader 实际 dispatch、PC/Android/iOS 格式检查、MA→ATO→AAO 排序、取消/异常回滚、真实 Avatar 烘焙前后截图和内存/资产审计。仅静态扫描不能替代这些门禁。

## 已知边界

- 只支持 Unity 2022.3 与声明的依赖版本；其他版本未经承诺。
- NPOT 是实验选项，应在每个目标平台单独验证。
- NDMF `IAssetSaver` 没有删除 API；若资产保存已开始后发生异常，构建缓存中可能留下未引用的持久化资产，需要清理构建输出/缓存后重试。
- 极端顶点数、超大图集和大量动画变体会增加 GPU/Native 临时内存；ATO 会分阶段释放资源，但仍应监控 Editor 峰值。
- 客观指标无法覆盖所有 shader、显示器、VR 采样和内容语义，最终发布者仍负责目视检查。

## 许可证

MIT，见 [`LICENSE.md`](LICENSE.md)。

---

## English summary

Avatar Texture Optimizer is a safety-first NDMF build plugin for VRChat avatars. It analyzes renderer/material/animation/UV relationships, searches quality-bounded island sizes, optionally packs irregular multi-page atlases, rewrites build-copy meshes and texture references, and falls back whenever safety cannot be demonstrated. Place exactly one component on the `VRCAvatarDescriptor` root. It runs after Modular Avatar and before Avatar Optimizer, has no NDMF live preview, and never edits source assets. See the Chinese sections above for complete settings, safety boundaries, extension API, and validation requirements.
