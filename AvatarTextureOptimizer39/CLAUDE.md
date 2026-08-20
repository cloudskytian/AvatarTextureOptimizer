# Avatar Texture Optimizer (ATO)

> 本文件是本项目的唯一记忆载体。所有关于本项目的工作计划、已做工作、整体进度、
> 未完成工作、注意事项均记录于此，并随每次提交更新。防止上下文过长/中断导致遗忘。

## 项目定位

- 项目名：`AvatarTextureOptimizer`
- 包名：`net.fosa.avatar-texture-optimizer`
- 目标：全世界最好的 VRChat 贴图优化工具（开源 NDMF 工具）
- 语言：所有代码注释、日志、i18n 均使用「英文 + 简体中文」双语

## 核心机制（一句话）

分析 Avatar 上满足条件的材质，建立「网格 UV → 贴图」映射；以导入后的有效贴图为基准，
按目标质量算法缩放 UV 岛；剔除未用 UV；将碎片重组合并为图集（或整图缩放），
在保证质量的前提下最大化贴图利用率。

## 依赖库（已下载并通读关键源码）

| 库 | 版本 | 路径（沙箱） | 已读关键 API |
|---|---|---|---|
| NDMF | 1.14.4 | deps/ndmf | Plugin/Pass/Sequence、BuildPhase、BuildContext、AnimatorServicesContext、AnimationIndex、ObjectRegistry |
| VRChat Base | 3.10.4 | deps/vrchat-base | VRCAvatarDescriptor |
| VRChat Avatars | 3.10.4 | deps/vrchat-avatars | SDK3A |
| Modular Avatar | 1.18.2 | deps/modular-avatar | （MA 在 Transforming 阶段执行） |
| AAO | 1.9.17 | deps/aao | UVUsageCompabilityAPI、ShaderInformation 注册模式、OptimizerPlugin |
| lilToon | 2.3.4 | deps/liltoon | 属性命名 _MainTex/_BumpMap/_MainTex_ScrollRotate、[lilUVAnim] 标签 |
| avatar-compressor | 0.9.0 | deps/avatar-compressor | 贴图压缩/Resize 参考 |
| LLC | 2.13.0 | deps/llc | TextureBaker 参考 |

## 关键 API 结论（已验证，勿再猜测）

- **NDMF 插件**：继承 `nadena.dev.ndmf.Plugin<T>`，重写 `QualifiedName`/`DisplayName`，
  在 `Configure()` 内用 `InPhase(BuildPhase.X).Run(...)` 注册 pass。
- **Pass 顺序约束**：`DeclaringPass.BeforePlugin("com.anatawa12.avatar-optimizer")`
  可保证在 AAO 之前运行。MA 在 Transforming 阶段，AAO 在 Optimizing 阶段。
  → **ATO 应注册在 Optimizing 阶段，并 BeforePlugin("com.anatawa12.avatar-optimizer")**。
- **BuildPhase 顺序**：FirstChance → PlatformInit → Resolving → Generating → Transforming → Optimizing → PlatformFinish。
- **BuildContext 关键成员**：`AvatarRootObject`、`ObjectRegistry`、
  `GetState<T>()`、`ActivateExtensionContext<T>()`、`AssetContainer`。
- **动画分析**：`context.ActivateExtensionContext<AnimatorServicesContext>()` 后，
  用 `.AnimationIndex` 的 `ClipsWithObjectCurves` / `GetPPtrReferencedObjectsWithBinding`
  / `RewriteObjectCurves(mapping)` / `RewritePaths` 枚举与重写动画中的材质/贴图引用。
- **AAO UVUsageCompabilityAPI**（命名空间 `Anatawa12.AvatarOptimizer.API`）：
  `IsTexCoordUsed(SkinnedMeshRenderer, channel)` / `RegisterTexCoordEvacuation(renderer, orig, saved)`。
  注意原文拼写就是 `Compability`（非笔误）。
- **lilToon 主色 `_MainTex`，法线 `_BumpMap`**；`_MainTex_ScrollRotate`(Vector4) 等
  属 ST 变换，需检测并视为白名单（不可优化）。

## 架构设计

```
Runtime/   —— 可序列化组件与配置（主组件 ATOAvatarTextureOptimizer、枚举、质量参数等）
Editor/
  ATOPlugin.cs            —— NDMF 插件入口（Optimizing 阶段，BeforePlugin AAO）
  Passes/                 —— 各处理 Pass（按流水线拆分）
  Analysis/               —— 材质/动画/贴图收集、去重、白名单判定、UV↔贴图映射
  Texture/                —— 贴图解码、线性空间重采样、预乘 alpha、图集生成、压缩
  Quality/                —— MS-SSIM、ΔE(CIEDE2000)、alpha IoU/RMSE、法线角度误差、灰度 RMSE
  UVIsland/               —— UV 岛提取、各向异性缩放（二分）、像素密度钳制
  Atlas/                  —— 光栅位掩码、BLF、候选图集池、装箱
  Packing/                —— 装箱（原子操作=贴图及其 UV 组）
  ShaderAnalysis/         —— lilToon 等 shader 属性表/关键字分析
  Platform/               —— 平台 override（PC/Android/iOS）
  Localization/           —— i18n（json，en + zh-CN）
  UI/                     —— 自定义 Inspector
  Burst/                  —— Burst/GPU 并行评估
```

## 处理流水线（Pass 划分，规划）

1. **Pass: ValidateComponent** —— 校验组件挂载合法性（≤1 个、必须有 VRCAvatarDescriptor，否则报错中止）
2. **Pass: CollectMaterialsAndTextures** —— 遍历材质槽（跳过 EditorOnly），收集主色/法线等贴图，建立 UV↔贴图映射
3. **Pass: CollectAnimationReferences** —— 分析动画中的材质/贴图切换、启用禁用，并入映射
4. **Pass: DeduplicateTextures** —— 按实际像素+导入设置去重，更新引用（白名单传播）
5. **Pass: ExtractUVIslands** —— 提取 UV 岛（含多通道 UV、越界归一、重叠合并、形态键/缩放面积）
6. **Pass: ScaleUVIslands** —— 目标质量算法 + 二分搜索 + 各向异性细化 + 像素密度钳制
7. **Pass: PackAtlases** —— 图集装箱（BLF+光栅化，候选图集池，类型组）
8. **Pass: RegenerateTextures** —— 生成图集、fallback 贴图、压缩格式、MipStreaming、pull-push
9. **Pass: RewriteReferences** —— 重写材质/动画引用、材质槽合并、去重、AAO 兼容
10. **Pass: Report** —— 输出报告

## 目标质量挡位（初版参数，基于 MS-SSIM/ΔE 学术与业内经验）

见 `Runtime/ATOQualityLevel.cs`。默认挡位 **High**。自定义挡位默认全 1（近无损）。

## 进度

- [x] 依赖库下载与关键源码通读
- [x] 项目骨架 + git + package.json + CLAUDE.md
- [x] Runtime 数据模型（组件、枚举、质量/压缩/平台配置、像素密度挡位）
- [x] NDMF 插件入口 + 10 个 Pass（Optimizing 阶段，BeforePlugin AAO）
- [x] 分析流水线（白名单展开、贴图读取、材质/着色器分析、动画查询、收集 Pass）
- [x] 质量算法（ΔE2000、SSIM/MS-SSIM、alpha IoU/RMSE、法线角度、灰度 RMSE + 重采样）
- [x] UV 岛提取（拓扑连通分量 + 三角光栅化 + 归一化/跨缝检测 + 重叠岛合并）
- [x] UV 岛缩放（二分 + 各向异性 + 密度钳制 + 纯色短路 + 近无损跳过 + 动画 scale 面积 + 动画 renderMode/Cutoff）
- [x] 图集装箱（BLF + 4px 位掩码 + 候选池 + 贴图原子装箱 + 法线岛禁旋转 + 动态 padding）
- [x] 贴图再生（图集构建 + 旋转写入 + pull-push + 法线重归一化 + 压缩安全过滤 + 整图缩放 + MipStreaming/ReadWrite/Clamp）
- [x] 引用重写（UV 重映射 + 材质赋值 + 动画引用更新 + 材质去重 + 材质槽合并 + AAO 反射兼容 + 移除组件）
- [x] 白名单同 UV 跳过图集化 + 整图缩放
- [x] 多通道 UV（shader 源码 texcoord 语义解析）
- [x] Burst 加速（重采样 job）+ GPU 预留接口
- [x] 内存释放策略（raw/linear 像素缓存按阶段释放）
- [x] 进度显示 + 取消（ATOProgress 集成到各 pass）
- [x] i18n（en/zh-CN JSON + 语言切换）+ 自定义 Inspector + 资产后处理
- [x] README
- [ ] 用户在 Unity 内实机验证（沙箱无法编译/运行 Unity）

## 已知说明（诚实标注）

1. 沙箱无 Unity/dotnet，**代码未经编译验证**，需用户在 Unity 内同步后修复编译错误。
2. Burst 加速已集成（ATOCompute 调度 CPU/Burst）；GPU 双线性重采样为预留接口
   （ATOGpuResampler，未强制接入核心路径，因双线性与面积平均语义不同会影响质量评估一致性）。
3. "类型组内次要贴图整体缩放"由"每岛独立缩放 + 各自质量指标"覆盖（次要贴图可用更宽松指标缩得更小）。
4. MipStreaming 的 import 设置通过 AssetPostprocessor（ATOTexturePostprocessor）在资产保存后应用。

## 注意事项

- 沙箱无 Unity/dotnet，只能产出源码，由用户手动同步验证，**无法自测编译**。
- 所有日志前缀 `[ATO]`，预留开关。
- 每步日志含耗时、来源、岛数、图集尺寸、利用率、优化量。
- 未确认的 Unity API 一律标注，避免凭空猜测。
