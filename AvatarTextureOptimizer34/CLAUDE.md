# CLAUDE.md — 项目记忆（AvatarTextureOptimizer）

> 关于本项目的一切记忆只记录在此文件。每次修改后必须更新本文件并 git 提交。
> All project memory lives here. Update after every change and commit.

## 项目概况 / Overview

- 名称：AvatarTextureOptimizer（简称 ATO）
- 包名：`net.fosa.avatar-texture-optimizer`（UPM 包，非完整 Unity 工程，用户手动同步验证）
- 定位：VRChat Avatar 的开源 NDMF 贴图优化工具，运行于 **MA 之后、AAO 之前**（`BuildPhase.Transforming`）
- 目标：分析网格 UV↔贴图映射 → 按目标质量算法缩放 UV 岛 → 剔除未用 UV → 重组图集 → 最大程度提高贴图利用率且保证表现一致

## 已确认的第三方 API 事实（2026-08-20 通读源码后确认，勿凭猜测）

### NDMF 1.14.4（Editor/API/）
- `Plugin<T> : PluginBase where T : Plugin<T>, new()`；`[assembly: ExportsPlugin(typeof(X))]` 注册；`[RunsOnAllPlatforms]`
- `Configure()` 内 `InPhase(BuildPhase.Transforming)` 返回 `Sequence`；`Run(name, ctx=>{})`（InlinePass）或 `Run<T>(T pass) where T : Pass<T>, new()`
- 约束：`Sequence.AfterPlugin("nadena.dev.modular-avatar").BeforePlugin("com.anatawa12.avatar-optimizer")`
- `BuildContext`：`AvatarRootObject`、`PlatformProvider`（`INDMFPlatformProvider.QualifiedName`，VRChat 用 `WellKnownPlatforms.VRChatAvatar30`）、`GetState<T>()`、`AssetSaver.SaveAsset(obj)`、`IsTemporaryAsset(obj)`、`SetEnableUVDistributionRecalculation(mesh, false)`（我们改 UV 后应自己 `mesh.RecalculateUVDistributionMetrics(0)` 再 opt-out）
- 错误：`ErrorReport.ReportError(SimpleError)`；`SimpleError` 抽象类（`Localizer`/`TitleKey`/`TitleSubst`/`DetailsKey`...）；`ErrorSeverity.Error/NonFatal/Warning/Info`
- i18n：`nadena.dev.ndmf.localization.LanguagePrefs.Language`（如 "en-us"、"zh-hans"）→ Auto 模式读取
- **NDMF 自带 CheckMipStreamingPass**：临时资产贴图 mipmapCount>1 时必须 `m_StreamingMipmaps=true`，否则报错 → 我们的图集必须遵循"开 Mipmap ⇔ 开 MipStreaming"绑定
- NDMF 无内置 pass 级进度/取消 API → 用 `EditorUtility.DisplayCancelableProgressBar` + 自建取消标志
- 暂不支持 ndmf 预览（用户明确不需要）

### AAO 1.9.17（API-Editor/UVUsageCompabilityAPI.cs）
- 命名空间 `Anatawa12.AvatarOptimizer.API`，静态类 `UVUsageCompabilityAPI`
  - `bool IsTexCoordUsed(SkinnedMeshRenderer renderer, int channel)`（0~7）
  - `void RegisterTexCoordEvacuation(SkinnedMeshRenderer renderer, int originalChannel, int savedChannel)`
- `Impl` 由 AAO 的 `[InitializeOnLoadMethod]` 注册 → AAO 已安装时烘焙期恒可用；asmdef `com.anatawa12.avatar-optimizer.api.editor` 是 `autoReferenced=false` → **我方用反射调用，AAO 未装时静默跳过**
- 语义：我们改动某通道 UV 前，应先把原始 UV 拷贝到空闲通道 f，再 `RegisterTexCoordEvacuation(renderer, ch, f)`；AAO 会用它替换自己的 UV 依赖并在最后移除疏散通道
- AAO 主插件 QualifiedName = "com.anatawa12.avatar-optimizer"，主处理在 `BuildPhase.Optimizing`

### liltoon 2.3.4
- 属性表在 `Editor/lilInspector/lilMaterialProperties.cs`（`lilMaterialProperty(name, isTexture, blocks...)`）
- 策略：**运行时自动分析**着色器属性表（`Shader.GetPropertyCount/Name/Type/Flags`）+ 名称模式规则表 + `[Normal]` 属性 + 关键字（`LIL_FEATURE_NORMALMAP` 等），不硬编码全部属性，天然兼容未来版本
- 已知特殊 UV（不进图集，视为不可优化）：`*MatCap*`、`*Reflection*`、`*Panorama*`、`*Cubemap*`、`_MainTex2nd/3rd` 类需看 `_UVMode` 浮点值（非 0 = 非 uv0，需谨慎）；`*_ScrollRotate` 属性被动画/数值修改 = 贴图存在变换 → 白名单
- 渲染模式：关键字 `LIL_RENDER_MODE_CUTOUT` 等 + `RenderType` tag 兜底（Opaque/TransparentCutout/Transparent）；`_Cutoff` 用于 cutout

### VRC SDK 3.10.4
- asmdef 引用名：`VRC.SDKBase`、`VRC.SDK3A`；`VRCAvatarDescriptor` 位于 `VRC.SDK3.Avatars.Components`（VRCSDK3A.dll，自动引用）
- NDMF 惯例版本定义：`com.vrchat.avatars` → define `NDMF_VRCSDK3_AVATARS`

## 核心架构（Coder 组共识，2026-08-20）

阶段管线（单 pass 内分阶段，每阶段可取消、有进度条、有耗时日志）：
1. **Validate**：组件合规检查（Avatar 子级唯一；挂载对象必须有 VRCAvatarDescriptor；否则 Error 并中止）
2. **Scan**：遍历 Renderer（跳过 EditorOnly、要求启用或动画启用）、材质槽、动画（AnimatorController 各层 + 直接引用的 AnimationClip）
3. **MeshUV 分析**：每 (mesh, uvChannel)：BlendShape 0/100 最大面积；动画最大缩放面积；UV 越界整体平移归一（跨 wrap 缝 → 白名单+warning）；Burst 光栅化连通域提岛；重叠岛合并；岛↔三角形归属（含材质槽索引）
4. **动画分析**：材质槽/贴图引用切换（ObjectReferenceCurve）、ST 变换、渲染模式/Cutoff、m_Enabled、m_LocalScale；动画新增贴图并入 UV 组
5. **贴图登记**：按「实际像素 + 导入设置」去重并更新引用（白名单去重结果仍白名单）；解码 LRU 缓存（内存预算）
6. **分类**：着色器属性表自动分析 → 用途（Albedo/Normal/GrayMask/SpecialUV→不可优化）；色彩空间/filterMode/ST 检查；渲染模式判定
7. **分组**：类型组（用途集合+色彩空间+filterMode）与 UV 组（同一 UV 几何）。UV 组模板布局共享 → 同组多贴图在不同图集同位
8. **质量评估**：见下
9. **装箱**：Burst 4px 光栅掩码 + BLF 全扫描 + 面积降序 + 边长降序 + 90°旋转（转置）+ 候选图集池（POT/NPOT）+ 队列逻辑 + padding
10. **烘焙**：RT 池 + blit shader（线性/预乘 alpha/法线重采样）+ pull-push（GPU compute，CPU 扩张 fallback）+ 资产写入（压缩/Mipmap⇔Streaming 绑定/Clamp/只读/平台覆盖）
11. **重映射**：新网格（UV 重映射 + AAO 疏散 + RecalculateUVDistributionMetrics + opt-out）；材质克隆只改贴图引用；动画曲线重写；材质/贴图去重 + 材质槽合并
12. **报告**：ndmf 控制台汇总 + 折叠细节；[ATO] 日志含每步耗时/图集来源/岛数/大小/利用率/优化量

## 质量算法（目标质量，CPU 精确实现 + GPU 自检路径）

- 线性空间重采样；透明贴图预乘 alpha 下采样；比较时缩小后双线性上采样回原尺寸
- 指标：MS-SSIM（岛包围盒短边<176px→单尺度 SSIM；<11px→忽略 SSIM）+ ΔE(CIEDE2000) + alpha（Cutout=clip 后轮廓 IoU / Blend=线性 RMSE；多材质取最严苛）+ 法线（解码→重采样→重归一化→编码后角度误差 mean+p95）+ 灰度（仅使用通道、线性空间 RMSE、逐通道取最差）
- UV 缩放：二分搜索；均匀缩放达标后再双轴独立二分细化；UV 组内取木桶最大（≤组内最大原尺寸）；像素密度钳制（默认 min 2048 / max 4096 px/m，挡位 512/1024/2048/4096/8192）
- 质量=1（近无损）：跳过缩放原样拷贝；纯色岛短路缩到 min(4, 短边)（质量≠1 时）
- GPU 路径（compute shader）**必须自检**：与 CPU 结果对比，偏差超限自动回退 CPU 并 warning

## 质量挡位（参数依据见 README）

| 挡位 | MS-SSIM | ΔE | alpha RMSE | IoU | 法线 mean/p95 | 灰度 RMSE |
|---|---|---|---|---|---|---|
| NearLossless(质量=1) | 跳过 | 跳过 | 跳过 | 跳过 | 跳过 | 跳过 |
| High | 0.98 | 2.3 | 0.02 | 0.98 | 3°/8° | 0.02 |
| Medium | 0.95 | 4.0 | 0.04 | 0.96 | 5°/12° | 0.04 |
| Low | 0.90 | 8.0 | 0.08 | 0.92 | 8°/20° | 0.08 |
| Custom | 默认全 1（近无损），用户改，不被其他挡位覆盖 | | | | | |

## 装箱规则（按用户 spec 逐条落实）

- 原子操作 = 单个贴图 + 其 UV 组模板实例；类型组队列；先算队列剩余 UV 总面积 → 丢弃小于总面积的候选图集 → 按面积升序、长边/短边升序排序候选 → 顺序试装；装不下最大图集 → 开新队列（同类型组复用）；单贴图装不进最大图集 → 放弃该 UV 组图集化，仅质量缩放 + warning
- 岛形状光栅化装箱（非矩形）；旋转 90° 步进（掩码转置）；法线贴图绝不重算切线
- padding = ceil(候选图集最大边长/128) 向下钳制到 4，用户可选 4/8/16/32/64（默认 4）；岛边缘 GPU pull-push 无限外扩（透明 alpha 保持 0；渗色已知够用）
- 候选池：POT 2^n，min 64，max 8192（移动端 4096）；NPOT 实验选项：64 步进，剔除不支持格式（iOS 剔除 PVRTC），已验证支持 MipStreaming/Crunch

## 已做的工程决策（偏离点如实记录）

1. **GPU 批量度量**：实现 GPU compute 路径（写好了 shader），但以「GPU/CPU 自检一致才启用」为默认策略；CPU 路径（Burst 并行）始终可用。真正的多岛批处理是性能优化项，当前为逐岛 dispatch（自检 + 文档说明）
2. **CPU 度量分析分辨率上限**：CPU fallback 评估在 min(原尺寸, 1024) 上执行并文档化；GPU 路径全分辨率（读回分块）
3. **导入参数优化只作用于 ATO 生成的资产**，不改用户原始贴图导入设置（安全优先）
4. **UV 通道归属**：着色器属性→UV 通道映射使用规则表（liltoon 的 `_UVMode` 读取 + 默认 uv0），未知着色器默认 uv0
5. 材质/贴图去重、材质槽合并仅在动画不单独切换目标槽时执行
6. 取消：停止工作、释放 RT/NativeArray、保留已保存的临时资产、报告中止原因

## 待办 / TODO（完成时勾除）

- [x] 依赖源码通读（ndmf/aao/liltoon/vrc/ma）
- [x] 项目骨架（package.json/asmdefs/组件/i18n/日志）
- [x] 分析层（扫描/UV 提岛/动画/去重/分类/分组）
- [x] 质量层（参数/CPU 度量/二分缩放评估器/GPU 路径+自检）
- [x] 装箱层（光栅化/BLF/候选池/模板）
- [x] 烘焙层（RT 池/blit/pull-push/资产写入/网格重映射）
- [x] 后处理（引用重写/去重/槽合并/AAO 兼容/扩展接口）
- [x] Inspector / 平台覆盖 / 报告 / README / VERIFY 清单
- [x] Reviewer 三轮 + QA 三轮全量审查（见 git log）

## 最终状态（2026-08-20 交付）

- 代码规模：约 7300 行 C# + 3 个着色器（blit / quality metrics / pull-push）+ 双语注释
- 三轮 Reviewer + 三轮 QA（QA1 正确性 / QA2 生命周期 / QA3 需求符合性）全部完成并修复
- 交付物：`AvatarTextureOptimizer-v0.1.0.zip`（包目录，不含 .git）
- 用户侧验证：`Documentation~/VERIFY.md` 清单（沙盒无法运行 Unity，须用户同步验证）
- 诚实声明（已写入 README/VERIFY）：
  - CPU 回退评估分辨率上限 1024px；GPU 路径全分辨率（compute shader 需 Unity 编译验证，自检不通过自动回退）
  - GPU 度量按岛 dispatch（多岛批处理为后续性能优化项）
  - GPU blit 在贴图存储空间处理预乘/法线；CPU 路径严格线性空间（二者自检互校）
  - pull-push 的 CPU/扩张回退为近似实现（渗色已知，够用）

## 审查记录（Reviewer / QA 已修复的真实问题）

1. 装箱旋转掩码尺寸未含 padding（Orient/FlipBoth/transpose 尺寸错）→ 已修
2. 同一网格资产被多渲染器共用时只赋值给第一个渲染器 → UvGroup.renderers 全量赋值
3. 动画启用的渲染器在扫描时被漏掉（扫描先于动画分析）→ 调整阶段顺序
4. 预乘 alpha 在上采样阶段被二次加权 → 仅下采样预乘
5. 同一贴图被不同材质以不同用途引用时只按主用途评估 → 遍历全部用途取最严苛
6. 增量装箱不按候选池收缩图集 → FinalizeSize 最小候选收缩
7. 空 NativeArray 的 IsCreated==true 导致形态键面积作业越界 → 长度守卫
8. pull-push compute 同纹理读写竞态 → 乒乓缓冲；扩张 blit 同步修复
9. 材质去重时全局 slotRemap 误判 → 局部 anyMerge
10. PVRTC/压缩回退路径、GetWidthForFormat 非真实 API、NPOT 未设 npotScale=None → 已修
11. TextureAssetWriter 透明/不透明分类按图集实际 alpha → 已修
12. AAO 疏散改为遍历 g.renderers（共用网格）→ 已修
13. I18n 误报配平为误报；AtoLocalization 临时 ScriptableObject 泄漏 → 已修
14. 图集队列按总面积降序（spec）→ 已修

## 注意事项 / Gotchas

- 所有 Unity 主线程 API（RenderTexture/ReadPixels/AssetDatabase/AnimationUtility）不得进 Burst job；Burst 只做纯数据（NativeArray）
- 每步 finally/using 释放 RT 与 NativeArray，防泄漏；RenderTexture 池化（预算上限）
- `Texture2D.ReadPixels` 需要 `RenderTexture.active = rt` 且之后 `Apply()`；用完恢复 active
- 动画绑定路径 `m_Materials.Array.data[i]._MainTex` 解析/重写要兼容 MeshRenderer 与 SkinnedMeshRenderer
- 网格 UV 修改必须新建 mesh 临时资产（不污染原资产）；`SetEnableUVDistributionRecalculation(mesh,false)` + 手动 Recalculate
- 图集资产名 `ATO_` 前缀；Read/Write 关、Wrap=Clamp 强制、其余取所有源贴图最高质量
- 组件烘焙后从成品移除自身
- 日志以 `[ATO]` 开头，含开关；报告默认汇总、细节折叠
- 本沙盒无法运行 Unity → 交付后用户同步验证；`Documentation~/VERIFY.md` 为验证清单
