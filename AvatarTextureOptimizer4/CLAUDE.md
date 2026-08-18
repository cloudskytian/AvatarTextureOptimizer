# CLAUDE.md — 项目记忆（仅记录在本项目根目录）

> 本文件是 AvatarTextureOptimizer 项目的唯一持久记忆。任何中途上下文丢失/网络中断后，先读本文件恢复状态。

## 0. 项目定位

- 项目名：**AvatarTextureOptimizer**；包名 `net.fosa.avatar-texture-optimizer`。
- 目标：全世界最好的 VRChat 贴图优化工具——开源 NDMF 工具，分析网格、建立 UV↔贴图映射、按目标质量算法缩小 UV 岛、剔除未用区域、按类型组重组图集。
- 交流语言：简体中文；代码注释双语（英+中）；交付物为**源码包**（非完整 Unity 工程），由用户手动同步到工程验证。

## 1. 已完成工作（截至 2026-08-18）

### 1.1 取证（已读源码，禁止猜测 API）
- 已下载并阅读 8 个依赖包：VRChat base/avatars 3.10.4、NDMF 1.14.4、Modular Avatar 1.18.2、AAO 1.9.17、lilToon 2.3.4、avatar-compressor 0.9.0、LLC 2.13.0。
- 关键结论（已核对源码）：
  - NDMF 阶段：`Resolving→Generating→Transforming→Optimizing→PlatformFinish`；MA 主逻辑在 `Transforming`，AAO 主序列在 `Optimizing`（插件标识 `com.anatawa12.avatar-optimizer`，用 `\uFFDC` 命名空间排序到最末）。→ ATO 用 `InPhase(Optimizing).BeforePlugin("com.anatawa12.avatar-optimizer")`，AAO 缺失时 `BeforePlugin` 安全（`GetPluginPhases` 对未知插件建占位 phase）。
  - AAO `UVUsageCompabilityAPI`（原文拼写 Compability）：`IsTexCoordUsed(SMR,ch)` / `RegisterTexCoordEvacuation(SMR,orig,saved)`；仅构建期调用、仅 SMR、saved 通道若被 AAO 占用会抛异常。程序集名 `com.anatawa12.avatar-optimizer.api.editor`（namespace `Anatawa12.AvatarOptimizer.API`），版本 1.8.0 引入。
  - NDMF API 面：`Plugin<T>`（`QualifiedName`/`Configure`/`InPhase`）、`Sequence.Run`/`BeforePlugin`/`AfterPlugin`、`Pass<T>.Execute(BuildContext)`、`BuildContext`（`AvatarRootObject`/`ObjectRegistry`/`ErrorReport`/`AssetContainer`/`AssetSaver`/`GetState<T>`/`SetEnableUVDistributionRecalculation`）、`ErrorReport.AddError`/`ReportError`、`SimpleError`（`Localizer`/`TitleKey`/`Severity`/`AddReference`）、`ObjectRegistry.GetReference`、`IAssetSaver.SaveAsset`（立即持久化并给路径）、`LanguagePrefs.Language`（如 `zh-hans`）。
  - lilToon 属性名：主色 `_MainTex`、法线 `_BumpMap`、遮罩 `_AlphaMask` 等；matcap/env/grad/AudioLink 等属 `Other`（不图集化）。
  - asmdef 名：NDMF 编辑器 `nadena.dev.ndmf`、AAO API `com.anatawa12.avatar-optimizer.api.editor`、VRC `com.vrchat.avatars`。

### 1.2 已实现（全部为真实代码，非占位）
- 包结构 + 双 asmdef（Runtime 纯净；Editor 引用 NDMF/Burst/Collections/Jobs/Mathematics，`versionDefines` 定义 `ATO_VRCSDK3_AVATARS` 与 `ATO_AAO`）。
- NDMF 插件 `ATOPlugin` + `ATOPass`（组件校验：唯一性 + VRCAvatarDescriptor）+ `ATOPipeline` 九阶段编排。
- 数据模型 `ATOModel`（RendererRef/TextureRef/Usage/Island/UvSpace/Atlas/AnimationRemap/…）+ `ATOBuildContext`（平台覆写解析、白名单、baseMaterialClone、pathRemap）。
- 分析：`ATOAvatarScanner`（克隆材质）、`ATOAnimationAnalyzer`（材质/贴图切换、启停、缩放、渲染模式/cutoff、ST）、`ATOPropertyTable` + `ATOShaderPropertyAnalyzer`（关键字兜底）、`ATOEligibility`（白名单/ST/越界规则）、`ATOTextureDeduplicator`。
- UV：`ATOMeshUvAccessor`（多通道）、`ATOIslandBuilder`（邻接洪泛、越界归一、wrap 缝、同子网格重叠合并）、`ATOPixelDensity`（形态键 0/100 取最大、动画缩放、密度）。
- 质量：`ATOColorMath`（sRGB/线性、预乘、CIEDE2000、SSIM/MS-SSIM、RMSE、法线解码/角度误差、p95）、`ATOQualityModel`（挡位预设 + Custom）、`ATOQualityEvaluator`（多指标木桶门控）、`ATOQualityJobs`（Burst 高斯/RMSE + 托管兜底）、`ATOTextureSampler`（岛光栅化 + 预乘 + 双线性）。
- 缩放/装箱：`ATOIslandScaler`（二分 + 各向异性 + 纯色短路 + 密度钳制 + 原图缓存）、`ATORasterizer`（4px 位掩码 + 90° 转置）、`ATOAtlasPacker`（类型组 + 全局 BLF 归一化布局 + POT/NPOT 池 + 兜底）、`ATOAtlasBuilder`（绘制 + 法线重归一 + pull-push 填充）、`ATOUVRemapper`（写 UV + AAO 疏散 + 共享网格/多图集克隆材质）、`ATODirectResizer`（整图缩放、克隆）。
- 收尾：`ATOMaterialDeduplicator`、`ATOMaterialSlotMerger`、`ATOAnimationRewriter`（对象引用/路径/槽索引重映射，克隆片段与控制器）、`ATOTextureSettingsApplier`（压缩/流式 PNG 重导入 + 兜底）、`ATOSelfRemoval`、`ATOBuildReportWriter`、`ATOI18n`、`ATOGpu`、`ATOProgress`（取消）、`ATOAvatarInspector`（UI）。

## 2. 关键设计决策与修正（务必遵守）

1. **法线贴图禁止旋转**：切线不重算时，90° 旋转会破坏切线空间法线（R→T 映射被旋转）。装箱中"含法线的 UV 空间锁定 0° 旋转"。这是对原始需求"旋转 90° 步进（法线切线保持原样）"的必要修正，已向用户反馈。
2. **全局归一化布局**：所有 UV 空间在参考分辨率（maxAtlasSize）下一次 BLF 布局，得到归一化摆放+旋转；每个类型组图集复用同一摆放，保证"同一 UV 在不同图集上位置一致"。各类型组图集尺寸 = 不小于组内最大贴图边长的最小候选（保原生分辨率）。
3. **资产安全**：所有将被修改的材质/网格/贴图/动画先克隆（`baseMaterialClone`），`AssetSaver.SaveAsset` 立即持久化以拿到路径；动画材质属性曲线按 `materialPathRemap` 重映射绑定路径。
4. **白名单语义**：白名单贴图自身全跳过；其同 UV 的非白名单贴图跳过图集化但参与"整图缩放 + 导入参数"优化（`wholeTextureScale`）。
5. 质量挡位默认 High；Custom 默认近无损。

## 2.5 第二轮补齐（2026-08-18 第二次交付）

- **GPU 路径**：`Editor/GPU/ATODownsample.compute`（预乘 alpha 2x 下采样）+ `ATOGpu.PremultipliedDownsample2x`（RenderTexture+ComputeShader，托管兜底），接入整图缩放的精确减半场景。
- **候选图集池**：`ATOAtlasPacker.PickCandidate` 支持正方形+非正方形（w≥h）、POT（64×2..max）/NPOT（64 步进）、按（面积，长宽比）升序排序；`BuildAtlasForGroup` 按归一化摆放反推所需宽高（防截断），岛装不下最大图集时整组 fallback。
- **类型组整体缩放**：`ATOAtlasBuilder.CanHalve` 对灰度/遮罩图集做半尺寸评估（逐岛最差通道线性 RMSE ≤ 灰度阈值 且 padding≥8），通过则 `BuildScaled(0.5)` 整体减半。
- **灰度单通道兜底**：`ATOTextureSettingsApplier` 检测多通道灰度/遮罩图集，用户选了 BC4 时改存 BC7 并告警。
- **NDMF 报告窗口化**：`ATOBuildReportWriter` 用 `ErrorReport.ReportError(ATOReportError)`（Information 级、详情可折叠、`DetailsSubst` 承载全文）。
- **单开关**：`ATOCompressionChoice` 删除 `generateMipmaps`，仅 `mipStreaming` 同时控制 mipmap+流式（UI 单一开关）。
- **扩展点**：`Editor/Extensibility/IATOStage.cs`（`ATOStageRegistry`，`ATOPipeline` 末尾按优先级执行）。
- **可读性保障**：`ATOUtil.EnsureReadable`（GPU 读回不可读贴图）+ `ATOTextureSampler` 可读副本缓存（`ClearCache` 在管线 finally 释放）；`CloneTexture` 恒为 RGBA32（压缩格式也可 SetPixels）。
- **关键 API 修正**：NDMF 的 `ErrorReport.AddError` 是 internal，外部必须用 public static `ErrorReport.ReportError(...)`。

## 3. 未完成 / 待办（优先级从高到低）

- [ ] **在真实 Unity 工程编译并烘焙验证**（用户将手动进行；预期需要修复的首轮问题：Burst 与托管互操作、PNG 重导入路径、材质属性动画路径重映射的边界情形）。
- [ ] 质量评估的上采样（BilinearUpsample）GPU 路径（当前下采样已 GPU，上采样为 CPU）。
- [ ] NDMF 预览支持（需求明确"暂不支持"，保留）。
- [ ] 法线图集装箱时的"同 UV 多图集"像素级对齐测试（当前按归一化布局保证）。
- [ ] i18n 键补全（当前仅错误与报告键；UI 为硬编码双语，可用）。

## 4. 注意事项

- 日志全部以 `[ATO]` 开头；`ATOAdvancedSettings.debugLogging`（默认开）/`verboseLogging` 控制级别；每步含耗时、图集来源/岛数/尺寸/利用率/相对优化量（报告在 NDMF 控制台，默认汇总、细节用 debug 折叠）。
- 处理顺序：MA(Transforming) 之后 → ATO(Optimizing) → AAO(Optimizing)。仅处理"启用或被动画启用的 SMR/MR 上经网格 UV 采样、无 ST 变换（含动画）、无特殊用途的 Texture2D"。
- 图集名以 `ATO_` 开头；图集默认关 Read/Write、强制 Clamp。
- 每个阶段都通过 `ATOProgress` 支持取消（`ThrowIfCancelled`），取消时保留硬盘临时资产、释放 CPU/GPU/内存。
- 修改代码后：先读代码取证再改；改完 git 提交并更新本文件。

## 5. Git 提交记录约定

- 每次修改后 `git commit`，提交信息注明阶段与摘要。
