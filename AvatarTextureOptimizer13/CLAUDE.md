# CLAUDE.md — AvatarTextureOptimizer (ATO) 项目记忆

> 本文件是 ATO 项目的**唯一权威记忆**。任何上下文丢失/中断后，先读本文件再继续。
> 所有代码注释必须 **英文 + 中文双语**。日志统一以 `[ATO]` 前缀输出。

## 0. 项目定位

- **项目名**：AvatarTextureOptimizer（ATO）
- **包名**：`net.fosa.avatar-texture-optimizer`
- **目标**：全世界最好的 VRChat 贴图优化工具 —— 一个开源 NDMF 插件。
- **核心能力**：分析 Avatar 网格 → 建立「网格 UV ↔ 贴图」映射 → 按目标质量算法缩放 UV 岛/贴图 →
  剔除未用 UV 区域 → 重排 UV → 尽可能合并为图集 → 在保证质量前提下最大化贴图利用率。
- **性质**：非 Unity 工程（用户手动同步进工程验证）。本仓库只含包源码。

## 1. 关键结论（已研读依赖源码，勿再猜测）

### NDMF 1.14.4（fluent API）
- 插件：`Plugin<T>`，`[assembly: ExportsPlugin(typeof(T))]`，实现 `QualifiedName` / `DisplayName` / `Configure()`。
- 阶段 `BuildPhase`：FirstChance → InternalPrePlatformInit → PlatformInit → Resolving → Generating → Transforming → Optimizing → PlatformFinish。
- `InPhase(BuildPhase.Optimizing)` 返回 `Sequence`；`Sequence.Run("名", ctx=>{})` 返回 `DeclaringPass`；
  `DeclaringPass.BeforePlugin(qname)` / `BeforePass(qname)`；`Sequence.AfterPlugin(qname)`（在 Constraints.cs）。
- `BuildContext`：`AvatarRootObject` / `AvatarRootTransform` / `AssetContainer`（UnityObject，存生成资产）/
  `AssetSaver` / `GetState<T>()` / `ErrorReport` / `IsTemporaryAsset` / `OpenSerializationScope()`。
- 报告：`ErrorReport.ReportError(IError)`（静态，作用于 CurrentReport）；`IError` 接口
  `{Severity, CreateVisualElement(ErrorReport), ToMessage(), AddReference(ObjectReference)}`。
  `ErrorSeverity`：Information / NonFatal / Error / InternalError。
- 平台名：`WellKnownPlatforms.VRChatAvatar30 = "nadena.dev.ndmf.vrchat.avatar3"`。

### 执行顺序（MA 后、AAO 前）
- MA：`QualifiedName = "nadena.dev.modular-avatar"`（主逻辑 Transforming；Optimizing 内仅有 GCGameObjectsPluginPass）。
- MA 后期：`"nadena.dev.modular-avatar.late-transform-stages"`（Transforming）。
- AAO：`QualifiedName = "com.anatawa12.avatar-optimizer"`（主逻辑 Optimizing；另有少量 Resolving 前置 pass）。
- **ATO 定位**：`BuildPhase.Optimizing`，`AfterPlugin("nadena.dev.modular-avatar")` + `BeforePlugin("com.anatawa12.avatar-optimizer")`。
  即：MA 全部完成 → ATO → AAO。

### AAO 兼容（可选依赖，反射检测，勿硬引用）
- 类型 `Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI`（注意拼写 **Compability**，AAO 原文如此），
  程序集 `com.anatawa12.avatar-optimizer.api.editor`。
- API：`IsTexCoordUsed(SkinnedMeshRenderer, int channel)`（channel 0~7）、
  `RegisterTexCoordEvacuation(renderer, originalChannel, savedChannel)`。
- AAO 未安装时其 `Impl` 未初始化 → 调用会抛 InvalidOperationException。**必须先反射检测 AAO 存在**。
- 用法：把 ATO 要改的 UV 通道原数据疏散到备用通道，再告诉 AAO 用备用通道。

### lilToon 贴图属性（来自 AAO ShaderInformation.Liltoon.cs，权威）
- 主色：`_MainTex`（`_MainTex_ST` / `_MainTex_ScrollRotate` / `_ShiftBackfaceUV`）；假属性 `_BaseMap`/`_BaseColorMap` 也指向主色。
- 法线：`_BumpMap`、`_Bump2ndMap`（用主 UV）；金属光滑：`_MetallicGlossMap`、`_SmoothnessTex`。
- 蒙版类：`_MainColorAdjustMask`、`_Main2ndBlendMask`、`_Main3rdBlendMask`、`_EmissionBlendMask`、
  `_ShadowBorderMask`、`_ShadowBlurMask`、`_ShadowStrengthMask`、`_RimShadeMask`、`_OutlineWidthMask`、
  `_FurMask`、`_FurLengthMask`、`_MatCapBlendMask`、`_AnisotropyScaleMask`、`_AnisotropyShiftNoiseMask` 等。
- 自发光：`_EmissionMap`、`_Emission2ndMap`、`_EmissionGradTex`、`_Emission2ndGradTex`。
- 其他：`_Main2ndTex`、`_Main3rdTex`、`_OutlineTex`、`_OutlineVectorTex`、`_FurVectorTex`、`_FurNoiseMask`、
  `_MatCapTex`、`_MatCapBumpMap`、`_ParallaxMap`、`_GlitterColorTex`、`_GlitterShapeTex`、`_AnisotropyTangentMap`、
  `_ReflectionColorTex`、`_RimColorTex`、`_ShadowColorTex`、`_Shadow2ndColorTex`、`_Shadow3rdColorTex`、
  `_BacklightColorTex`、`_AudioLinkMask`、`_AudioLinkLocalMap`、`_DitherTex`、`_MainGradationTex`、`_IDMask1..8`。
- 开关关键字：`_UseMain2ndTex`、`_UseMain3rdTex`、`_UseBumpMap`、`_UseBump2ndMap`、`_UseEmission`、`_UseEmission2nd`、
  `_UseMatCap`、`_UseMatCap2nd`、`_UseShadow`、`_UseRim`、`_UseOutline`(由 shader 决定)、`_UseParallax`、`_UseAnisotropy`、`_UseGlitter`、`_UseAudioLink`。
- UV 模式属性：`_Main2ndTex_UVMode`、`_EmissionMap_UVMode` 等（0=UV0,1=UV1,2=UV2,3=UV3,4=NonMesh）。

### 标准 shader 关键字（Builtin）
- `_MainTex`、`_MetallicGlossMap`、`_BumpMap`、`_OcclusionMap`、`_EmissionMap`、`_DetailMask`、
  `_DetailAlbedoMap`、`_DetailNormalMap`、`_ParallaxMap`（均 UV0；细节图可用 UV1）。

### VRC SDK 程序集
- `VRC.SDK3A`（runtime，含 `VRC.SDK3.Avatars.Components.VRCAvatarDescriptor`）、`VRC.SDK3A.Editor`、
  `VRC.SDKBase`、`VRC.SDKBase.Editor`。

### 依赖程序集名（asmdef 引用用）
- ndmf: `nadena.dev.ndmf`（Editor）、`nadena.dev.ndmf.runtime`（Runtime）
- AAO: `com.anatawa12.avatar-optimizer.runtime` / `.editor` / `.api.editor`
- MA: `nadena.dev.modular-avatar.editor` / `.runtime`
- VRC: `VRC.SDK3A`、`VRC.SDK3A.Editor`

## 2. 总体计划（AgentTeam 流程）

**角色**：2 Coder（先互相对齐设计再写码）→ 2 Reviewer（每份代码共同审查，打回或通过）→ 2 QA（最终整体验收，独立完整通读，双 QA 同时通过才交付）。

**里程碑**（git 提交点）：
- [x] M0 依赖研读 + 计划（本文件）
- [ ] M1 包骨架（package.json / asmdef / i18n JSON / 目录）
- [ ] M2 Runtime 层（组件 + 全部设置模型 + 白名单 + 质量挡位 + 平台覆盖 + 扩展接口）
- [ ] M3 Editor 基础设施（ATOPlugin、ATOLog、ATOI18n、Model、Inspector）
- [ ] M4 分析层（ShaderPropertyAnalyzer / AnimationAnalyzer / WhitelistResolver / UVIslandExtractor / Deduplicator）
- [ ] M5 质量度量（SSIM/MSSSIM/CIEDE2000/角度误差/alpha IoU+RMSE + 求值编排）
- [ ] M6 缩放（二分搜索 + 各向异性 + 纯色短路 + 密度钳制）
- [ ] M7 装箱（位掩码光栅化 + BLF + 候选池 + pull-push padding）
- [ ] M8 烘焙与回写（RenderTexture 烘焙 + 贴图导入参数 + Mesh UV 回写 + AAO 疏散）
- [ ] M9 Pass 串联（Validate→Analyze→Optimize→Atlas→Reassign→Dedup→Report/Cleanup）
- [ ] M10 QA 终验 + README + 打包 zip

## 3. 需求要点速查（实现时对照）

1. **白名单**：不限制对象类型（网格/材质/贴图/动画皆可）。白名单内对象引用的全部贴图跳过所有优化（含参数优化）；同 UV 的其他贴图跳过图集化，但参与整图缩放与导入参数优化。
2. **仅处理**：只在「被启用或有动画启用」的 SkinnedMeshRenderer/MeshRenderer 上、经网格 UV 采样、
   无 ST 平移缩放旋转（含动画）的 Texture2D。任一不符 → 按白名单处理。
3. **绝不动**：材质里贴图以外的任何着色器参数。
4. **去重**：按实际像素 + 导入设置（不同即不同）去重并更新引用；白名单污染则结果也白名单。
5. **图集开关**：默认开；关 → 不生成图集、不剔除未用 UV、不重排 UV，仅缩放贴图 + 其他优化。
6. **形态键**：每个形态键仅取 0/100 二者最大值；不考虑组合/负数/>100。
7. **缩放动画**：按最大缩放面积。
8. **多通道 UV**：支持；逐通道独立处理。
9. **UV 越界**：可整体平移归一[0,1]（不跨 wrap 缝）则归一重映射；跨缝依赖 repeat 或无法处理 → 白名单+warning。
10. **重叠岛合并**：同贴图内重叠岛合并。
11. **各向异性**：先均匀缩放至全部达标，再双轴独立二分细化。
12. **动画兼容**：形变、材质切换、多材质槽、VRC 组件；材质槽可能动画切换多贴图；材质属性（渲染模式/Cutoff）可能被动画改 → 取最严苛。
13. **贴图用途**：同贴图被不同材质以不同用途引用 → 取最严苛。
14. **shader 分析**：自动分析 liltoon + 标准关键字 shader 属性表/关键字，兼容未来版本；不兼容 → 白名单+warning。
15. **装箱**：Burst 位掩码（4px 粒度）+ 全扫描 BLF + 面积降序 + 边长降序 + 90°旋转步进（位掩码转置；法线切线不动）。候选池：默认 POT（64 起，8192/移动 4096 止），可选 NPOT（64 步进）；NPOT 已兼容 MipStreaming/Crunch，勾选时剔除不支持格式（如 iOS PVRTC）。
16. **装箱步骤**：贴图按类型组形成队列，按光栅化总面积降序；先算队列总 UV 面积、丢弃更小候选、按面积升序/接近正方形优先排序；每个队列以「单张贴图+其 UV 组」为原子；装不下最大图集余量则另开队列（复用同类）；单张贴图都装不下 → 放弃该 UV 组图集化 + warning。用岛形状光栅化装箱（非矩形）。
17. **缓存**：合理缓存避免重复解码/光栅化。
18. **padding**：ceil(maxEdge/128) 下钳 4，档位 4/8/16/32/64 默认 4；岛边缘 pull-push 无限外扩填满空白（透明 alpha 保持 0）。
19. **压缩格式**：安全枚举；按透明/不透明（按图集实际是否有 alpha）/法线/灰度 分设。先读 liltoon 关键字再按像素兜底。
20. **MipStreaming**：非白名单默认开；Mip 开→Streaming 强制开，Mip 关→Streaming 强制关（单开关绑定）。
21. **平台选项**：PC/Android/iOS override（参考 unity platform override）；默认读当前构建平台。
22. **图集参数**：默认关闭 Read/Write、强制 Clamp（不可改）；其余取各贴图最高。
23. **安全 fallback**：任何选项组合不得破坏材质（透明贴图不提供无 alpha 格式；灰度设单通道但存在多通道 → 按多通道保存+warning）。
24. **图集数量**：不限，自然增长。名称以 `ATO_` 开头。
25. **去重**：材质/贴图各设开关（默认开）；优化后内容+参数相同且多材质槽网格内可判定相同且动画不单独切换 → 去重更新引用；不透明材质合并时合并材质槽 + 更新动画与槽索引。
26. **组件约束**：一个 Avatar 子树只允许一个组件；挂载对象必须有 VRCAvatarDescriptor；不合规 → 报错中止。
27. **内存**：控制内存、防泄漏。
28. **不支持 ndmf 预览**。
29. **进度/取消**：显示阶段+进度、可取消（终止、保留临时资产、释放资源）。
30. **烘焙后移除自身**（组件）。
31. **控制台报告**：默认展示总体，细节折叠；含每步耗时/图集来源/岛数/图集大小/利用率/相对原贴图优化量。
32. **预留接口**：高级用户 + 第三方扩展接口（IATOPostProcessor 等）。
33. **i18n**：JSON 配置（有几个语言显示几个），手动切换，默认 Auto 读 ndmf 语言，缺失回退英文；提供 en + zh-Hans 两套。
34. **质量算法**：线性空间重采样；透明贴图预乘 alpha 下采样；MS-SSIM（包围盒短边<176px 回退单尺度 SSIM；<11px 忽略；不透明同理）+ ΔE(CIEDE2000) + alpha（Cutout 用 clip 后轮廓 IoU / Blend 用线性 RMSE；多材质引用逐一评估取最严苛）；不透明贴图 MS-SSIM+ΔE；法线贴图正确解码重采样重归一编码后角度误差+p95；灰度贴图仅被用通道、线性 RMSE 逐通道取最差。缩放后的岛实际覆盖区双线性上采样回原尺寸比较。二分搜索取最差阈值，全部达标才过；UV 组内木桶效应取最大尺寸（≤组内最大原尺寸）。
35. **质量=1**：近无损，跳过 UV 缩放（含纯色），原样拷贝。
36. **密度挡位**：默认最小 2048px/m、最大 4096px/m；挡位 512/1024/2048/4096/8192；受岛在贴图物理文件真实大小钳制。
37. **质量挡位**（预设，折叠在高级选项，可改）：见 `ATOQualityPreset`。自定义挡位默认全 1。
38. **处理时机**：MA 后、AAO 前；兼容 AAO UVUsageCompabilityAPI；未装 AAO 也正常。

## 4. 质量挡位参数（学术/业内依据）

| 挡位 | MS-SSIM | ΔE2000 | 法线角度° | p95角度° | alpha RMSE | Cutout IoU |
|---|---|---|---|---|---|---|
| Balanced(默认) | 0.980 | 2.5 | 2.5 | 5.0 | 0.02 | 0.985 |
| High | 0.990 | 1.5 | 1.5 | 3.0 | 0.012 | 0.992 |
| Low | 0.960 | 4.0 | 4.5 | 8.0 | 0.035 | 0.970 |
| Lossless(质量1) | 1.0 | 0.0 | 0.0 | 0.0 | 0.0 | 1.0 |
| Custom | 全 1（近无损） | | | | | |

依据：SSIM/MS-SSIM 文献（Wang et al.）；ΔE2000 感知阈值（Sharma et al.，ΔE≤2 不可感知、≤3 几乎不可感知）；
法线贴图角度误差（法线方向偏差 ~2° 肉眼难辨）；alpha 线性 RMSE。

## 5. 当前状态 / 进度

- [x] M0 依赖研读 + 计划
- [x] M1 包骨架（package.json / asmdef / i18n JSON）
- [x] M2 Runtime（组件 + 设置模型 + 白名单 + 质量挡位 + 平台覆盖 + 扩展接口）
- [x] M3 Editor 基础设施（ATOPlugin / ATOLog / ATOI18n + 迷你JSON / Model / Inspector）
- [x] M4 分析层（ShaderPropertyAnalyzer / AnimationAnalyzer / WhitelistResolver / UVIslandExtractor / Deduplicator）
- [x] M5 质量度量（SSIM/MSSSIM/CIEDE2000/角度/alpha IoU+RMSE + 岛级评估）
- [x] M6 缩放（二分 + 各向异性 + 纯色/近无损捷径 + 密度钳制）
- [x] M7 装箱（位掩码 + 光栅化 + BLF + 候选池 + pull-push）
- [x] M8 烘焙与回写（图集烘焙 + 贴图导入参数 + Mesh UV 回写 + AAO 疏散 + 动画贴图重映射）
- [x] M9 Pass 串联（0 Validate → 1 Analyze → 2 Optimize → 3 Atlas → 4 Reassign → 5 Dedup → 6 Report/Cleanup）
- [x] M10 Reviewer 两轮审查 + QA 双审 + README + 打包 zip
- [x] M11 补全（第二轮）：Burst 度量后端（SSIM/MS-SSIM/alpha/角度/重采样，带 CPU 回退）、
      GPU(RenderTexture) 双线性上采样、原始贴图 MipStreaming/导入参数应用（按需克隆）、
      NPOT 格式剔除（iOS PVRTC→ASTC）、灰度多通道/alpha 安全回退、形态键 0/100 面积因子、
      IATOTextureKindProvider 第三方着色器扩展、报告字节统计、本地化挡位/语言下拉、
      白名单 Mesh 全局语义、_BaseMap/_BaseColorMap 去重跳过、网格克隆 UV 分布重算、
      NDMF 序列级 BeforePlugin/AfterPlugin 顺序修正。
- [ ] **待用户 Unity 内验证**（本沙箱无 Unity，无法编译）

## 6. 注意事项 / 风险

- **本沙箱无 Unity，无法编译验证**。代码严格按已读的 NDMF 1.14.4 / AAO 1.9.17 / MA 1.18.2 / lilToon 2.3.4 / VRC 3.10.4 源码 API 编写，交付后必须在 Unity 内编译验证。
- AAO 集成走反射（`Type.GetType("Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI, com.anatawa12.avatar-optimizer.api.editor")`），避免硬依赖。
- VRCAvatarDescriptor 校验在 Editor（`#if ATO_VRCSDK3`，由 asmdef versionDefines 定义），Runtime 组件零依赖。
- **双后端**：质量指标/重采样走 Burst（`BurstMetrics`，带 CPU 回退到 `QualityMath`）+ GPU 双线性上采样（`GpuResampler`，区域过小或 GPU 不可用时回退）。光栅化/BLF/pull-push 仍为 CPU 参考实现（Burst 化为后续优化）。
- **已实现**：原始贴图 MipStreaming/导入参数应用（`OriginalTextureSettingsApplier`，仅当设置不同时克隆源贴图）；NPOT 格式剔除；灰度多通道/alpha 安全回退；形态键 0/100 面积因子；第三方着色器扩展接口。
- **仍属已知边界**（非功能缺口，是保守策略）：
  1. 动画路径解析为"精确→后缀→名称"尽力匹配；嵌套 Animator 的复杂路径重映射依赖该策略。
  2. 不透明材质槽合并采用"任槽被动画单独切换则跳过合并"的保守回退（不会产生错误索引，但可能少合并）。
  3. 装箱全扫描 BLF 在超大图集上为参考实现性能；Burst 化是计划项。
- **关键设计决策**：图集布局跨类别一致（颜色/法线/蒙版图集共享同一 UV 布局）；法线图集空白填中性法线 (128,128,255)；透明图集空白 alpha 保持 0。
- 所有 Unity 专属操作必须 try/catch + fallback + `[ATO]` 日志。
- 打包交付：`zip -r AvatarTextureOptimizer.zip AvatarTextureOptimizer/`（不含 deps）。
