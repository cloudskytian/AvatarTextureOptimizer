# CLAUDE.md — AvatarTextureOptimizer 项目记忆（唯一记忆来源）

> 本文件是该项目唯一的记忆来源。所有计划、已做工作、进度、未完成事项、注意事项均记录于此。
> 修改任何代码前必须先读本文件与本仓库代码，先取证再下结论。

## 一、项目概要

- **项目名称**：AvatarTextureOptimizer（以下简称 ATO）
- **包名**：`net.fosa.avatar-texture-optimizer`
- **目标**：VRChat Avatar 的开源 NDMF 工具。分析 Avatar 网格上材质引用的贴图，建立"网格 UV ↔ 贴图"映射，按目标质量算法缩放 UV 岛、剔除未使用贴图区域、重新分配 UV，并尽可能重组合并为一个或多个图集，在保证质量的同时最大化贴图利用率。
- **运行时机**：NDMF `Optimizing` 阶段（MA 执行后、AAO 执行前）。
- **交付方式**：整个项目打包为 zip 一次性交付（不交付半成品）。

## 二、AgentTeam 分工与流程（本项目采用）

- **Coder ×2**：写代码前必须先交流、读依赖源码取证，得出共识后再落实代码。
- **Reviewer ×2**：Coder 每完成任何代码，Reviewer 共同审查，达成共识后再决定是否打回。
- **QA ×2**：Coder 完成整个项目且通过 Reviewer 验收后，QA 两人各自独立从头完整通读全部代码、找隐患/Bug、对照需求。任一发现缺陷 → 同时通知 Reviewer 与 Coder 打回。仅当两个 QA 同时认为符合要求才可交付。
- 所有结论/分歧/共识记录在本文件。

### 团队讨论记录（简要）
- **Coder-A/B 共识（源码取证结论）**：
  1. NDMF 插件注册：`[assembly: ExportsPlugin(typeof(ATOModule))]`；`Plugin<T>`；`InPhase(BuildPhase.Optimizing)`，用 `.AfterPlugin("nadena.dev.modular-avatar")` + `.BeforePlugin("com.anatawa12.avatar-optimizer")` 实现"MA 后 AAO 前"（源码见 nadena.dev.ndmf-1.14.4/Editor/API/Fluent/Plugin.cs、Sequence.cs、AAO OptimizerPlugin.cs）。
  2. AAO 兼容 API：`Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI`（静态类），`IsTexCoordUsed(SkinnedMeshRenderer,int)` 与 `RegisterTexCoordEvacuation(SkinnedMeshRenderer,int,int)`；Impl 由 AAO 的 `[InitializeOnLoadMethod]` 注入（见 API-Editor/UVUsageCompabilityAPI.cs 与 Editor/APIInternal/UVUsageCompabilityAPIImpl.cs）。**必须用反射调用**以支持"未安装 AAO"的场景。
  3. lilToon 2.3.4 贴图属性：主色 `_MainTex`；法线 `_BumpMap`/`_Bump2ndMap`（2.3.4 已不用 `_NormalMap`）；蒙版 `_Main2ndBlendMask`/`_Main3rdBlendMask`/`_MaskTex` 等；UV 模式选择属性形如 `_Xxx_UVMode`（enum: UV0..UV3/MatCap/Rim），非 UV0 的贴图不算纯 UV 采样（应白名单跳过）；另有 `_MainTex_ScrollRotate` 等动画属性（见 CustomShaderResources/Properties/Default.lilblock 与 Editor/lilPropertyNameChecker.cs）。运行时统一走 Unity Shader API（GetPropertyCount/Name/Type/Flags/Attributes + ShaderUtil.GetShaderGlobalKeywords/LocalKeywords）做通用分析，并对 lilToon 提供已知表兜底。
  4. VRC SDK：VRC_AvatarDescriptor 位于 VRCSDK3A.dll（命名空间 VRC.SDKBase）；编辑器 asmdef 引用 'VRC.SDKBase'、'VRC.SDK3A'（与 ndmf 一致）。
  5. NDMF 临时资产目录：`AvatarProcessor.TemporaryAssetRoot`（默认 Packages/nadena.dev.ndmf/__Generated），构建成功后由 CleanTemporaryAssets 清理；取消/出错时保留磁盘临时资产（符合需求）。生成资产统一放该目录、命名 ATO_ 开头。
  6. NDMF 语言：`nadena.dev.ndmf.localization.LanguagePrefs.Language`（如 "en-us"/"zh-hans"），i18n Auto 模式读取之。
- **Reviewer-A/B 结论**：见"已知风险与对策"节。

## 三、整体架构（已定稿）

```
AvatarTextureOptimizer/
├─ package.json / README.md / CHANGELOG.md / LICENSE.md / CLAUDE.md
├─ Runtime/          组件与设置数据（无 VRC 强依赖，验证用反射）
│   ├─ AvatarTextureOptimizer.cs   组件（设置、白名单、平台覆盖、质量预设…）
│   ├─ ATOEnums.cs / QualityPresets.cs / PlatformOverrides.cs
│   └─ net.fosa.avatar-texture-optimizer.runtime.asmdef
└─ Editor/
    ├─ ATOPlugin.cs                  NDMF 插件+Pass（Optimizing；MA后AAO前）
    ├─ UI/AvatarTextureOptimizerEditor.cs   检查器（含折叠、平台覆盖、i18n）
    ├─ i18n/Localization.cs + Translations/{en-US,zh-CN}.json（可扩展）
    ├─ Core/
    │   ├─ ATOContext.cs             构建上下文（设置解析+平台覆盖+缓存+报告+取消）
    │   ├─ BuildPipeline.cs          主流程编排（分阶段+进度+取消）
    │   ├─ Analysis/                 AvatarScanner / AnimationAnalyzer / TextureMappingBuilder
    │   │                            / TextureDeduplicator / WhitelistResolver / ShaderAnalyzer
    │   ├─ UV/                       UvIsland / UvIslandExtractor / UvIslandPost
    │   ├─ Quality/                  QualityConfigResolver / QualityEvaluator / UvScaler
    │   │        / Metrics/{ImageOps,MsSsim,Ssim,Ciede2000,AlphaMetrics,NormalMetrics,GrayMetrics}
    │   ├─ Atlas/                    RasterMask / BinPacker / CandidatePool / AtlasBuilder / PullPushFiller
    │   ├─ Processing/               AtlasWriter / MeshUvRewriter / AnimationPatcher / MaterialAssigner
    │   │        / PostDeduplicator / SlotMerger / AssetSaver / CompressionApplier
    │   ├─ Platform/PlatformResolver.cs
    │   ├─ AAO/UVUsageCompat.cs      反射包装 UVUsageCompabilityAPI
    │   ├─ Logging/{ATOLogger,BuildReport}.cs
    │   └─ Utils/{TextureCache,RenderTexturePool,ProgressScope,ColorSpace,NativeArrayPool}.cs
    └─ Shaders/  ATO_Resample.shader / ATO_PullPush.shader / ATO_BlitRect.shader
```

## 四、核心设计决策（Coder 共识）

1. **UV 岛提取**：按 UV 空间邻接（三角形共享 UV 顶点，容差 1e-5）做并查集合并，得到独立 UV 岛；每岛记录 mesh/channel/三角形列表/AABB/是否越界需平移归一；多通道 UV 各自独立处理。
2. **UV 组（UvGroup）**：同一 (renderer, uvChannel, 材质槽) 上同一 UV 区域的全部贴图（主色+法线+蒙版+动画切换贴图）构成一个 UV 组。**组内所有贴图在各自图集里必须使用完全相同的 rect（位置+尺寸）**，防止 UV 被多图集引用时错位。
3. **贴图类型组（TextureTypeGroup）**：key = (种类{主色/法线/灰度蒙版/其他}, 色彩空间{sRGB/Linear}, filterMode{点/双线性/三线性}, 伴随标志{有法线伴生/有蒙版伴生})。用于解决"十张贴图合一张大图集只有一张有法线"的浪费。某贴图同时被有/无法线材质引用 → 归入"有法线"组。
4. **质量算法**：线性空间重采样；透明预乘 alpha 下采样；指标 = MS-SSIM(短边<176px 回退单尺度 SSIM，<11px 忽略) + ΔE2000 + alpha(Cutout 用 clip 后 IoU / Blend 用线性 RMSE，逐引用材质取最严) / 不透明主色 = MS-SSIM+ΔE2000；法线 = 解码-重采样-重归一化-编码后用角度误差 p95；灰度 = 仅被使用通道、线性 RMSE 逐通道取最差。缩小后的覆盖区双线性上采样回原尺寸与原图比较。**Burst 并行 + GPU(RenderTexture) 批量执行**。UV 缩放 = 二分搜索（先均匀，全部指标达标后双轴独立二分细化），组内取最大尺寸（≤组内最大原尺寸）。目标质量==1 → 跳过该类型岛缩放、原样拷贝；纯色岛(质量≠1) 短路缩到 min(4, 原包围盒短边)。
5. **像素密度**：min/max 像素密度（默认 2048/4096 px/m，挡位 512..8192），结合形态键(仅取 0/100 两态取大)与动画最大缩放求世界面积，作为缩放上下界（受原贴图真实尺寸钳制）。
6. **装箱**：按贴图类型组形成贴图队列（光栅化总面积降序）；候选图集池（默认 POT 边长 64..8192(移动端4096)，实验性 NPOT 勾选后 64 步进同上限，NPOT 时剔除 PVRTC）；每队列先算所需 UV 总面积，丢弃面积不足的候选，按面积升序、长宽比升序（最接近正方形优先）尝试，第一个能装下全部岛的即成品图集。原子操作 = 单个贴图及其 UV 组（组内各贴图 rect 必须一致）。Burst 光栅位掩码 4px 粒度 + 全扫描 BLF + 光栅化面积降序 + 边长降序 + 90° 步进旋转（位掩码转置，法线不重算切线）。单贴图无法装入最大图集 → 放弃该贴图整个 UV 组图集化、质量缩放后走整图缩放并报 warning。
7. **图集填充**：padding = max(用户最小padding(4/8/16/32/64 默认4), ceil(图集最大边/128))；岛边缘 GPU pull-push 无限外扩填充空白（透明贴图 alpha 保持 0）。
8. **安全规则**：仅处理"仅在启用或有动画启用的 Renderer 上、经网格 UV 采样、无任何 ST/UV 变换(含动画)、非贴花等特殊用途"的 Texture2D；任一不满足 → 视作白名单（跳过所有优化）。白名单对象引用到的全部贴图跳过所有优化；同 UV 的其他贴图跳过图集化但仍参与整图缩放与导入参数优化。绝不动材质内非贴图参数。
9. **平台**：PC/Android/iOS 三平台 override（默认读取当前构建平台），勾选后显示并覆盖所有优化参数；图集格式等受平台限制的选项在对应平台下受限。
10. **压缩/Mip**：图集与 fallback 贴图按 透明/不透明(按是否含 alpha)/法线/灰度 分类提供安全压缩格式枚举；Mipmap 与 MipStreaming 绑定为单一开关（VRC 要求）；默认开启 MipStreaming。剔除导致问题的选项（如含 alpha 不提供无 alpha 格式；灰度贴图被设单通道格式但实际多通道 → 构建时仍多通道保存并警告）。
11. **AAO 兼容**：AAO 安装时（反射检测 Impl），对 AAO 使用的 UV 通道先"撤离"（原 UV 拷贝到空闲通道并 RegisterTexCoordEvacuation），再重排原通道；未安装 AAO 时跳过。
12. **取消/进度**：`EditorUtility.DisplayCancelableProgressBar` + `EditorApplication.update` 轮询取消标记；取消时中止、释放 GPU/CPU/内存、保留磁盘临时资产。烘焙完成移除自身组件、输出报告（[ATO] 前缀、每步耗时、图集来源、岛数、图集大小/利用率、相对原贴图优化量；默认总体、细节折叠）。
13. **去重**：处理前贴图按实际像素+导入设置去重并更新引用（白名单传染）；处理后材质/贴图按内容+参数去重；同网格相同不透明材质（且动画不单独切换其一）合并材质槽并更新动画引用与槽索引。
14. **i18n**：读取 `Assets/**/ATO_i18n/*.json` 与内置 en-US/zh-CN；有多少语言文件显示多少语言选项；Auto 读 NDMF LanguagePrefs，缺翻译回退英文。

## 五、质量挡位（Coder 依据学术研究定参）

| 挡位 | 整体质量 | MS-SSIM | ΔE2000 | alpha IoU | alpha RMSE | 法线 p95(°) | 灰度 RMSE | 说明依据 |
|---|---|---|---|---|---|---|---|---|
| NearLossless | 1.0 | 1.0 | 0 | 1.0 | 0 | 0 | 0 | 跳过缩放（几乎无损） |
| High | 0.98 | 0.995 | 1.5 | 0.995 | 0.005 | 1.5 | 0.010 | ΔE≈JND(Sharma2005)；SSIM>0.99 不可察觉 |
| Balanced(默认) | 0.95 | 0.98 | 3.0 | 0.98 | 0.015 | 3.0 | 0.020 | 默认挡位，肉眼难以察觉 |
| Performance | 0.90 | 0.95 | 6.0 | 0.95 | 0.040 | 6.0 | 0.040 | 明显但可接受 |
| Extreme | 0.85 | 0.90 | 10.0 | 0.90 | 0.080 | 10.0 | 0.080 | 极限压缩 |
| Custom | 默认1.0 | 全部参数用户自定，不会被其他挡位覆盖 | | | | | | 用户改参数后质量<1 才启用缩放 |

质量阈值折叠在"高级选项"中；切换挡位时参数随之变化；Custom 独立持久。

## 六、进度追踪

### 已完成
- [x] 依赖源码取证（NDMF/AAO/MA/lilToon/VRC SDK 关键 API 已读通，见上）
- [x] 架构与算法定稿（见三、四）
- [x] 项目脚手架（package.json/asmdefs/LICENSE/CHANGELOG/目录）
- [x] Runtime：Enums / 质量预设 / 组件(设置、白名单、平台覆盖、i18n 字符串)
- [x] Editor：日志/报告（[ATO] 前缀+耗时+图集报告）、i18n(+en-US/zh-CN JSON+Auto 跟随 NDMF)、
      上下文(ATOContext)、平台解析(EffectiveSettings)、组件校验（单一组件+VRCAvatarDescriptor 强校验）
- [x] 分析层：ShaderAnalyzer（lilToon 已知表+Unity Shader API 通用分析+UVMode/ST 伴侣探测）、
      AvatarScanner（EditorOnly/可见性）、AnimationAnalyzer（控制器/剪辑/材质槽切换/贴图切换/ST/渲染模式/
      Cutoff/缩放/启用）、TextureMappingBuilder(UV组/类型组/去重/白名单传染)
- [x] UV 层：提取（UV 空间并查集/多通道/越界归一/重叠合并）、WorldAreaCalculator（形态键 0/100+动画缩放）
- [x] 质量层：ImageOps(线性重采样/预乘 alpha)、SSIM/MS-SSIM、ΔE2000、alpha(IoU/RMSE)、法线角度 p95、
      灰度逐通道 RMSE、二分缩放器（均匀+双轴细化）、密度钳制、纯色短路、1024 上限防内存
- [x] 图集层：RasterMask(Burst 栅格化+位掩码)、BinPacker(队列/候选池/BLF/90°旋转/跨类型组固定 rect)、
      CandidatePool(POT/NPOT/移动端上限)、AtlasBuilder(类型组装箱/整图缩放兜底/复验)、PullPush shader
- [x] 处理层：AtlasWriter(GPU 组装+pull-push+CPU 兜底)、MeshUvRewriter(+AAO 撤离/顶点冲突检测)、
      AnimationPatcher(引用替换+槽索引重映射)、CompressionApplier(分类压缩/平台兜底/Mipmap⇔Streaming 绑定)、
      PostDeduplicator、SlotMerger、AssetSaver(NDMF 临时目录+AATO_ 前缀+反射读 TemporaryAssetRoot)
- [x] 主流程 BuildPipeline + NDMF Pass 接线（Optimizing：MA 后 AAO 前）
- [x] 检查器 UI（折叠/平台覆盖/i18n 语言下拉/立即烘焙）
- [x] README.md、git 提交

### 已知取舍/风险（QA 记录，重要！）
1. **旋转规则**：网格 UV 是 UV 组内共用的 → 90° 旋转只允许在"组内无任何法线贴图"时使用，
   否则旋转会破坏法线采样方向。实现于 BinPacker.GroupContainsNormal。
2. **整图缩放系数** = max(各岛 scale)（保证每岛质量不劣于已验证；偏保守但安全）。
3. **跨类型组 rect 一致性**：由"首个装箱类型组分配位置、后续组按固定 UV 位置复放"实现；
   不同图集 padding 不同 → 极端情况 padding 间隙略小（pull-push 兜底渗色，已知够用）。
4. **图集分辨率 < 原贴图**时按实际分辨率复验质量，失败 → 该贴图整个转整图缩放并警告（一次收缩，无级联）。
5. **内存**：质量比较分辨率上限 1024（文档化）；区域缓存 per (texture, island)；RT/数组池化。
6. **AAO**：纯反射调用 UVUsageCompabilityAPI；AAO 未安装时安全跳过。
7. **动画路径**：以 Avatar 根为基准解析（VRC 标准）；非常规挂载可能需白名单。
8. **无法在无 Unity 环境编译验证**：代码按取证 API 编写，用户需手动同步验证；见"注意事项"。

### 待办（下一轮）
- [ ] **用户侧验证**：把包同步进 Unity 工程，编译并烘焙真实 Avatar，修复一切编译/运行期问题
      （QA 无法在无 Unity 环境编译，此项必须由用户实测回馈）
- [ ] 性能实测（大贴图/多岛 Avatar 的耗时与内存）
- [ ] 若需要：为指标添加 GPU 批量评估的正式路径（当前 GPU 用于组装/缩放，指标在 CPU 并行）

### QA 第二轮结论（已执行）
- 修复：旋转仅限"组内无法线贴图 + 正方形图集"（UV 旋转与像素旋转在非正方形下不一致）；
  掩码按内容尺寸栅格化（掩码=内容精确区域+padding 膨胀）；_DestOffset 全屏=0；shader vert 双重变换；
  项目色彩空间确定性（Gamma 工程 sRGB 源手动转线性）；动画切换贴图 UV 通道正确解析；
  整图缩放系数取 max；Custom 挡位默认近无损；UV 岛 id 全局唯一；装箱面积过滤不误杀。
- 遗留（已知）：不同图集 padding 可能不同（pull-push 兜底）；GAMMA 工程未实测；
  `MeshUvRewriter`/`BinPacker` 等复杂路径需真实模型验证。

## 七、已知风险与对策（Reviewer 共识）
1. 无法在无 Unity 环境编译验证 → 代码严格按取证 API 编写；统一 C# 9 语法；asmdef 引用与 NDMF 一致；用户手动同步验证。
2. GPU 批量评估依赖编辑器 RenderTexture：全部使用 Graphics.Blit 标准路径，避免 ComputeShader 兼容差异；所有 shader 提供 CPU 兜底（readback 失败回退 GetPixels）。
3. AAO 反射调用可能抛异常 → try/catch 全包，异常即视为"AAO 不可用"并记日志。
4. Burst 作业需纯托管/数学代码 → 指标计算用 NativeArray+IJobParallelFor；装箱掩码操作用普通 C# + 可选 Burst；均带非 Burst 路径。
5. 动画分析遗漏（Playable 动态、VRC 表达式切换）→ 白名单兜底 + warning，绝不破坏表现。
6. 内存：所有解码/光栅化结果进 TextureCache/NativeArrayPool，阶段结束释放；大图处理用 RenderTexture 池。

## 八、注意事项
- 日志一律以 `[ATO]` 开头，含每步耗时；可开关（verbose）。
- 生成资产命名 `ATO_` 前缀；放 NDMF 临时目录。
- 每次修改后 git commit（消息带 [ATO] 前缀）。
- 本文件是唯一记忆源，任何会话都必须先读它。
