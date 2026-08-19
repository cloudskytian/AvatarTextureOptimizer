# CLAUDE.md — AvatarTextureOptimizer 项目记忆

> 本文件是项目的唯一持久记忆。每次完成工作后更新「进度」与「决策」章节。
> (EN) This file is the single source of project memory. Update it after every work session.

## 1. 项目概览

- **名称**：AvatarTextureOptimizer（简称 ATO）
- **包名**：`net.fosa.avatar-texture-optimizer`
- **目标**：做全世界最好的 VRChat Avatar 贴图优化 NDMF 工具（开源）。
- **定位**：基于 UV 岛的图集生成 + 质量门控重采样 + 压缩调优。
- **运行时机**：MA（Modular Avatar，Transforming 阶段）之后、AAO（Avatar Optimizer，Optimizing 阶段）之前。
- **工作流**：AgentTeam 协作（2 Coder 共识 → 2 Reviewer 共识审查 → 2 QA 独立全量验收）。
- **交付**：全部功能完成、通过 QA 后打包 zip。不做半成品交付。

## 2. 关键技术事实（已从源码取证，勿臆测）

### NDMF (nadena.dev.ndmf 1.14.4)
- 插件：`Plugin<T>`，`Configure()` 内用 `InPhase(BuildPhase.X).Run(...)` 注册 pass。
- `Pass<T>`：`protected override void Execute(BuildContext context)`。
- 排序：插件按 `FullName` ordinal 排序；AAO 用 `\uFFDC` 命名空间排到最后。
- 我通过 `BeforePlugin("com.anatawa12.avatar-optimizer")` 保证在 AAO 前。
- `BuildPhase`：FirstChance / PlatformInit / Resolving / Generating / Transforming / Optimizing / PlatformFinish。
- `BuildContext`：`AvatarRootObject`、`ObjectRegistry`、`GetState<T>()`、`Extension<T>()`、`IsTemporaryAsset(obj)`。
- `ObjectRegistry`：`GetReference(obj, create)`、`RegisterReplacedObject(old, new)`。换网格/材质/贴图靠它。
- 报错：`ErrorReport.ReportError(Localizer, ErrorSeverity, key, args)`；`ErrorSeverity.Error` 会阻断上传。
- 本地化：`nadena.dev.ndmf.localization.Localizer` + `LanguagePrefs.Language`（返回如 "en-us"）。
- 编辑器程序集名：`nadena.dev.ndmf`（另有 `nadena.dev.ndmf.runtime`）。

### AAO (com.anatawa12.avatar-optimizer 1.9.17)
- **UVUsageCompabilityAPI**（拼写就是 Compability，非 Compatibility）在 `Anatawa12.AvatarOptimizer.API`：
  - `IsTexCoordUsed(SkinnedMeshRenderer, int channel)` — 判断 AAO 是否用某 UV 通道。
  - `RegisterTexCoordEvacuation(SkinnedMeshRenderer, int originalChannel, int savedChannel)` — 让 AAO 用保存的通道。
- 我改 UV 前：若 AAO 会用该通道，先把原始 UV 疏散到空闲通道并注册，防止 AAO 的 RemoveMeshByMask/ByUVTile 出错。
- 引入方式：`#if ATO_AAO` 版本定义（versionDefines expression "1.8.0"），未装 AAO 时编译剔除。
- AAO 主程序集名：`com.anatawa12.avatar-optimizer`。
- AAO 报告用 `BuildLog.LogInfo`（AAO 自带的类，非 NDMF）。

### lilToon (jp.lilxyzw.liltoon 2.3.4)
- 标准属性名：`_MainTex`、`_MainColor`、`_BumpMap`（法线）、`_MainColorAdjustMask`、`_AlphaMask`、`_FurMask`、`_FurNoiseMask`、`_Bump2ndScaleMask`、`_ParallaxMap` 等。
- 关键字分析可基于这些标准名 + 属性表反射实现，兼容未来版本。

### 参考实现 (avatar-compressor v0.9.0, dev.limitex.avatar-compressor)
- 有 GPU 分析后端（GpuAnalysisBackend / CpuAnalysisBackend）、NormalMapAnalyzer、LruCache、TextureReadback、AlphaExtractor 等可参考。

## 3. 总体设计决策（已与需求对齐）

1. **程序集**：Runtime（`net.fosa.avatar-texture-optimizer`，纯数据+组件）＋ Editor（`.editor`，全部逻辑）。
2. **组件**：`AvatarTextureOptimizer`（MonoBehaviour，DisallowMultipleComponent），挂在有 VRCAvatarDescriptor 的对象上。
3. **i18n**：自写极简平铺 JSON 解析（无外部依赖），文件放 `Editor/Localization/*.json`，语言名=文件名；用户可在 `Assets/ATO/Localization` 扩展。
4. **日志**：`[ATO]` 前缀，`ATOLog`，含计时 Scope 与 Verbose 开关。
5. **报告**：`ATOReport`，构建结束打印到控制台 + 摘要。
6. **设置模型**：`ATOSettings.cs`（质量挡位/阈值/像素密度/压缩/图集/平台/去重/白名单/语言）。

## 4. 目录结构

```
AvatarTextureOptimizer/
├── package.json
├── Runtime/  (组件 + 设置)
├── Editor/
│   ├── ATOPlugin.cs  ATOPasses.cs  ATOBuildContext.cs  ATOReport.cs
│   ├── ATOLog.cs  ATOLocalization.cs
│   ├── Core/ATOPipeline.cs
│   └── Localization/en.json, zh-CN.json
```

## 5. 当前进度（更新：2026-08-19，功能全部完成，待用户在 Unity 验证）

> **状态：全部需求功能已实现，已打包交付 v0.1.0。**
> 剩余风险：代码未经 Unity 编译/烘焙验证（本环境无 Unity），需用户同步到工程后反馈编译/运行错误。

**全部已实现**（含此前骨架）：
1. 阶段1 Collect、2 Animations、3 Dedup、4 Islands、5 Quality、6 Pack、7 Apply、8 Report。
2. pull-push 图集边缘外扩填充（BFS）。
3. 无图集模式（整图缩放）。
4. 动画引用重写（克隆 clip + AnimatorController，重写材质/贴图对象引用，处理嵌套状态机与 BlendTree）。
5. 优化后材质/贴图去重 + 材质槽合并（动画无材质切换时才合并，避免索引错位）。
6. AAO UV 疏散（反射调用 UVUsageCompabilityAPI，无硬依赖，未装 AAO 自动跳过）。
7. 进度条 + 取消（EditorUtility.DisplayCancelableProgressBar，取消抛 OperationCanceledException 由 Pass 静默处理）。
8. Burst 加速（光栅化 RasterizeJob + SSIM SSIMJob，CPU 参考实现回退）。
9. 关键正确性修复：
   - 共享 Mesh 岛去重（不重复提取/装箱/重映射）。
   - 同贴图多用途引用 → 白名单 + warning。
   - 被不安全贴图（白名单/ST/特殊）引用的 UV 岛 → 其贴图所有岛跳过图集化、走整图缩放（传播逻辑）。
   - AAO 疏散针对克隆网格且每 (renderer,channel) 只注册一次。
10. README（中英双语，面向用户与第三方开发者）。
11. 扩展接口：IMaterialAnalyzer、i18n JSON、Burst 开关预留。

**已知限制/待用户反馈**：
- 质量算法当前 CPU 参考实现（Burst 光栅化/SSIM 已接入，ΔE2000/法线角度仍是 CPU）。
- legacy Animation 组件的 clip 重写尽力而为（VRChat 用 Animator，此场景罕见）。
- 压缩格式枚举映射为常见格式（BC7/BC5/BC1/BC3/ASTC/ETC2/R8 等），Auto 用引擎默认。

## 5b. 当前进度（历史快照）

**架构决策（已定）**：
- 岛按 **Mesh 去重**（共享网格只提取/装箱/重映射一次），UV 组持有 Mesh + 渲染器列表。
- **聚类** = 共享贴图的 UV 组并查集合并，作为装箱原子单位，保证「同一贴图的所有岛在同一图集」且不产生「部分图集化」的分裂。
- **类型组** = 按档案（法线/蒙版存在 + sRGB + filterMode）分组；严格性传播（贴图同时出现于有法线/无法线组 → 全归有法线组）。
- 质量缩放：逐岛「逐贴图二分取木桶最大」，再各向异性双轴二分，像素密度钳制（含动画缩放 + 形态键 0/100 最大面积）。
- 贴图读取用**区域读取 + 单条目缓存**（避免 4K 贴图爆内存）；质量评估线性空间 + 预乘 alpha。

**已完成（核心管线端到端）**：
- [x] 工程脚手架、asmdef（已确认 VRChat 程序集名为 VRC.SDK3A/VRC.SDKBase；Burst/Collections/Mathematics 由 com.vrchat.base 强制依赖）。
- [x] 阶段1 Collect：遍历渲染器（跳 EditorOnly）、着色器关键字分析（主色/法线/蒙版/灰度分类、UV 通道启发式、ST/滚动/旋转检测）、贴图去重登记、白名单。
- [x] 阶段2 Animations：材质槽切换(m_Materials.Array.data[i])、贴图切换(material.X 对象曲线)、ST/ScrollRotate/Angle→白名单、RenderMode/Cutoff→最严苛、m_IsActive、m_LocalScale。
- [x] 阶段3 Dedup：按导入设置签名 + 像素 FNV 哈希去重（引用更新在 Apply 前统一）。
- [x] 阶段4 Islands：岛提取（并查集）、UV 越界归一（跨缝白名单）、重叠岛合并、UV 组、类型组、形态键/动画缩放面积。
- [x] 阶段5 Quality：双线性线性空间重采样、MS-SSIM/SSIM、CIEDE2000、alpha IoU/RMSE、法线角度+p95、灰度 RMSE、二分缩放、密度钳制、纯色短路、近无损跳过。
- [x] 阶段6 Pack：4px 粒度位掩码光栅化（三角形填充）、BLF 装箱（90° 旋转转置）、候选图集池、按类型组生成各类型图集贴图。
- [x] 阶段7 Apply：图集持久化（PNG + TextureImporter 压缩/Clamp/MipStreaming/sRGB/filter）、网格克隆重映射 UV、材质克隆指向图集、ObjectRegistry 登记。
- [x] 阶段8 Report：报告摘要（贴图数/图集数/尺寸/利用率/来源）。
- [x] UI：组件 Inspector（本地化、质量挡位、图集、压缩、去重、白名单、语言）。

**未完成（优先级降序）**：
1. **pull-push**：图集岛边缘颜色外扩填充（防渗色），透明 alpha 保持 0。
2. **无图集模式**：整图缩放（ScaleWholeTexture 目前是空壳）。
3. **动画引用更新**：动画 clip 中切换的材质/贴图引用需经 ObjectRegistry 传播到新图集/新材质（部分可由 NDMF 自动处理，需验证）。
4. **优化后材质/贴图去重 + 材质槽合并**（阶段8）。
5. **AAO UV 疏散**（UVUsageCompabilityAPI，反射调用，无硬依赖）。
6. **进度显示 + 取消**、报告细节折叠。
7. **Burst/GPU 加速**（当前 CPU 参考实现）。
8. **扩展接口**（自定义材质分析器已留 IMaterialAnalyzer，其余待补）。
9. QA 全量验收 → README.md → 打包 zip。

## 6. 待向用户确认/已识别的风险

- **无 Unity 编译环境**：代码由用户手动同步到工程验证；我会尽量写稳、少用冷门 API。**强烈建议用户现在先同步编译一次**，暴露集成错误（比写完再调便宜百倍）。
- **形态键面积**：只取 0/100 各形态键最大值，忽略组合——用户已明确接受。
- **动画缩放面积**：按最大缩放面积算，可能过度分配（保守）。
- **质量算法性能**：MS-SSIM + ΔE2000 + 法线角度是全项目最难最吃性能部分，当前 CPU 实现，需 Burst/GPU 加速。
- **装箱聚类**：聚类（共享贴图）比用户描述的「按贴图队列」更粗粒度，可能导致过保守放弃（安全但略低效），后续可细化。
- **压缩格式**：图集存为独立 PNG 资产以支持 TextureImporter 压缩/MipStreaming；NDMF 默认子资产方式不支持 per-texture 导入设置。
- 许可证默认 MIT，用户未指定——如需换请告知。

## 7. AgentTeam 协作记录

- Coder A/B、Reviewer A/B、QA A/B 的角色与共识结论随每次模块实现写入本文件「协作记录」。
