# CLAUDE.md — 项目记忆（AvatarTextureOptimizer）

> 本文件是本项目唯一的长期记忆载体。每次里程碑、每次修改、每个关键决策都记录于此。
> 任何会话中断后，先从本文件恢复上下文。

## 1. 项目概述
- **项目名**：AvatarTextureOptimizer（ATO）｜**包名**：`net.fosa.avatar-texture-optimizer`（UPM/VPM）
- **目标**：VRChat Avatar 贴图优化 NDMF 工具（分析网格→UV↔贴图映射→按质量算法缩小 UV 岛→重新打包图集→安全导入参数优化）。
- **运行时机**：NDMF `BuildPhase.Optimizing`，MA 之后、AAO 之前（`.BeforePlugin("com.anatawa12.avatar-optimizer")`，缺 AAO 自动忽略，已验证 PluginResolver 源码）。

## 2. 团队工作流（单 Agent 内模拟）
- Coder×3（先共识后写码）→ Reviewer×3（每批代码共同审查）→ QA×3（全部完成后独立从头全量阅读，全票通过才交付）。
- 原则：先取证再下结论、不猜 API、每次修改 git 提交、记忆只记录于本文件。
- 已交付：完整代码库 + dotnet 单测 45/45 + 设计文档 + i18n + README + 打包 zip。

## 3. 已取证的关键第三方 API（2026-08-21 验证，源码在 ~/.arena/refs/）
- **NDMF 1.14.4**：`Plugin<T>` + `[assembly: ExportsPlugin]`；`InPhase(Optimizing).Run(displayName, InlinePass).BeforePlugin(...)`；跨阶段约束抛异常（勿对 MA 用 AfterPlugin）；BuildContext 在克隆体上运行；`ctx.AssetSaver.SaveAsset` 保存非持久资产（网格/材质/剪辑）；Finish 时对临时网格自动 RecalculateUVDistributionMetrics；`nadena.dev.ndmf.localization.LanguagePrefs.Language`。
- **AAO 1.9.17**：插件名 `com.anatawa12.avatar-optimizer`；`Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI`（AAO 原文拼写）；`[InitializeOnLoadMethod]` 注册、随时可调；`IsTexCoordUsed(SkinnedMeshRenderer, ch)` / `RegisterTexCoordEvacuation(r, orig, saved)`（saved 被 AAO 用则抛异常）；仅 SkinnedMeshRenderer；API asmdef `com.anatawa12.avatar-optimizer.api.editor`。
- **MA 1.18.2**：插件名 `nadena.dev.modular-avatar`（Transforming 阶段，天然早于我们）。
- **lilToon 2.3.4**：`[MainTexture] _MainTex`、`[Normal] _BumpMap`、`<Prop>_UVMode`(0..3=UV0..UV3, 4=MatCap)、`<Prop>_ScrollRotate`、`[lilToggle] _UseXxx`。
- **可选依赖装配**：asmdef `versionDefines`+`defineConstraints`（AAO 桥 `ATO_AAO_API_AVAILABLE`；Burst `ATO_BURST_AVAILABLE`）。

## 4. 最终架构（定稿）
```
package root
├─ Runtime/   ATOSettings(组件, 要求 VRCAvatarDescriptor) + ATOSettingsData + 数据模型 + ATOLog/ATOCancellation/ATOBuildReport + i18n json
├─ Editor/
│  ├─ NDMF/        ATONdmfPlugin + ATORunner（13 阶段主流程）
│  ├─ Analysis/    ShaderPropertyTable / TextureUseCollector / AnimationAnalyzer / WhiteListEvaluator /
│  │               TextureDeduper / TextureClassifier / MeshUVAnalyzer / IslandCore(纯C#)
│  ├─ Optimization/ IslandScaler / DensityPlanner / QualityEvaluator / QualityMath(纯C#) / AtlasBuilder /
│  │               Packing/PackingCore(纯C#) + BurstPacking
│  ├─ Baking/      TextureDecodeCache / TextureOps / AtlasTextureBaker / PullPush(JFA) / RenderTexturePool
│  ├─ Post/        ReferenceUpdater(资产克隆器) / MeshReplacer / MaterialDeduper / ImportSettingsApplier / TextureReencoder
│  ├─ AaoCompat/   AAOCompatBridge（独立 asmdef 条件编译）
│  ├─ UI/          ATOSettingsEditor（i18n 全标签）
│  ├─ Validation/  ATOSettingsValidator
│  └─ Shaders/     ATOBlit(5 pass) / ATOJFA(3 pass)
├─ Tools/AtoCoreTests/  dotnet 8 单测（45/45 通过）
└─ Documentation~/DESIGN.md + README.md + CLAUDE.md + i18n(en-US/zh-CN)
```

## 5. 关键设计决策（详见 DESIGN.md）
- **D1 UV组跨图集同位**：全局一次布局（D_max=8192/4096，组宏观矩形 BLF）→ 固定归一化矩形；每个 (桶,贴图) 图集只选最小满足像素需求的边长 D 并实例化同一归一化矩形。
- **D2 装箱**：4px 位掩码光栅化（Burst/托管双实现，算法一致）+ 全扫描 BLF（边界跳过扫描，模糊测试验证）+ 0°/90°旋转 + padding=max(ceil(maxSide/128), 最小4/8/16/32/64)；图集数量随 (桶,贴图) 自然增长；装不下→整图缩放回退+warning。
- **D3 质量挡位**：Ultra(1.0 跳过)/High(0.95 默认)/Medium(0.9)/Low(0.85)/Minimum(0.8) + Custom(默认全≈1 近无损)；阈值联动（SSIM 0.98/0.96/0.94/0.91，ΔE00 1.0/2.3/3.5/5.0，αRMSE 0.005/0.012/0.02/0.032，IoU 0.999/0.996/0.99/0.98，法线 0.5/1/2/3°，灰度同 αRMSE）；依据 Wang2004/Sharma2005/JND。
- **D4 质量评估**：GPU 重采样 + CPU 双线性放大回原尺寸比较；MS-SSIM(<176px 回退 SSIM，<11px 忽略)+ΔE00+alpha(Cutout IoU/Blend RMSE，多材质取最严苛)+法线 p95+灰度逐通道最差；全部达标才通过；纯色短路 min(4,短边)；目标=1 原样拷贝；透明贴图评估用预乘 alpha 下采样；永不上采样。
- **D5 pull-push**：GPU JFA（种子→步长减半传播→汇聚）；>4096 用半分辨率工作区；透明图集保持 alpha 0（跳过填充）。
- **D6 资产安全**：材质 `new Material`、网格/剪辑/控制器 `Object.Instantiate` 克隆后修改；渲染器/Animator 接入克隆；NDMF Serialize 保存；绝不改用户资产。
- **D7 白名单/回退**：对象级白名单；含白名单引用的 UV 组整体禁止图集化（共享 UV 不重排，其余贴图整图缩放+导入参数）；ST 变换/MatCap/跨缝/无法分析 shader/未知属性 → 自动白名单+warning。
- **D8 可选依赖**：AAO 桥独立 asmdef；Burst 守卫回退。

## 6. 进度状态（最终）
- [x] 环境搭建、第三方库取证、可行性论证（Coder 共识）
- [x] Runtime 数据模型/组件/i18n
- [x] 纯 C# 核心 + dotnet 单测（45/45：CIEDE2000 官方数据、BLF 间距、岛提取、布局装配、模糊测试）
- [x] Analysis / Optimization / Baking / Post / NDMF 集成 / AAO 桥 / Editor UI / Validator
- [x] DESIGN.md / README.md / CLAUDE.md / CHANGELOG / i18n 全量
- [x] Reviewer×3 联合审查 + QA×3 全量验收（见 §8）
- [x] git 提交（4 个里程碑）+ zip 打包交付

## 7. 注意事项（后续会话必读）
- 本环境无 Unity：Unity 侧代码靠静态审查 + 与已取证 API 对齐；纯核心用 dotnet 8 单测（~/.arena/dotnet/dotnet run --project Tools/AtoCoreTests）。
- 用户在真实 Unity 工程手动同步验证；README/DESIGN.md 已列出已知取舍与迭代点（指标核 Burst 化、JFA 半分辨率、AnimatorOverrideController 展开、剪辑直接贴图引用等）。
- 交付 zip = Packages/net.fosa.avatar-texture-optimizer 目录内容。
- 日志统一 `[ATO]` 前缀；`PlayerPrefs ATO.Verbose=1` 开详细日志。
- 生成资产路径：`Assets/AvatarTextureOptimizer_Generated/`（PNG 图集与整图缩放结果）。

## 8. QA 全量验收结论（QA-1/2/3 独立从头阅读全部代码后共识）
- **QA-1 规格符合性**：全部规格点均有对应实现（逐项映射见 DESIGN.md §4 表格）；发现并修复：①含白名单引用组整体禁止图集化（防共享 UV 被重排破坏白名单贴图）；②越界岛内容矩形取模（防采样错位）；③透明贴图预乘 alpha 下采样评估。
- **QA-2 正确性**：修复 CIEDE2000 ΔH′ 定义（对齐 Sharma 官方数据 2.0425/2.8615/3.4412）；BLF padding 语义（内容间距=padding）；图集烘焙矩形受限 blit（原 Graphics.Blit 全幅覆盖 bug）；槽位合并需子网格数匹配+无动画；材质/剪辑/控制器克隆防用户资产污染；整图缩放不覆盖已赋图集的 (材质,属性)。
- **QA-3 集成**：JSON/shader/括号/using/类型重复全部静态校验通过；45/45 单测通过；asmdef 可选依赖装配正确（AAO/Burst 缺装安全）。
- **残留风险（交付时声明）**：Unity 实机烘焙行为未在本环境验证；GPU 路径（JFA/采样 shader）需实机确认；动画剪辑直接贴图引用的整图替换保留原引用（安全性优先）；OverrideController 未展开。

## 9. 命令速查
- 单测：`~/.arena/dotnet/dotnet run --project Packages/net.fosa.avatar-texture-optimizer/Tools/AtoCoreTests -c Release`
- 静态检查：`python3 ~/.arena/static_check.py`（括号/using/类型查重/JSON）
- git：`cd /home/user/AvatarTextureOptimizer && git add -A && git commit -m "..."`（本环境每次提交后需手动同步）
