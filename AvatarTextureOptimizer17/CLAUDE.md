# CLAUDE.md — AvatarTextureOptimizer (ATO) 项目记忆

> 本文件是本项目唯一的长期记忆。所有计划、决策、进度、注意事项都记录在这里。
> This file is the single source of long-term memory for this project.

## 0. 项目概况 / Project Overview

- **项目名**: AvatarTextureOptimizer (ATO)
- **包名**: `net.fosa.avatar-texture-optimizer`
- **目标**: 全世界最好的 VRChat Avatar 贴图优化工具 —— 一个开源的 NDMF 工具。
- **核心理念**: 分析 Avatar 网格，建立「网格 UV → 贴图」映射（无视材质其他参数，贴图不变即可复用）；按目标质量算法缩放 UV 岛/整贴图；剔除未使用 UV 部分；将岛重排并合并为图集；最大化贴图利用率，同时保证视觉质量。
- **平台**: VRChat Avatar 3.0 (NDMF 默认平台)。
- **不交付半成品**: 所有功能一次性实现，最终 zip 打包交付。
- **验证方式**: 用户拿到代码后手动同步进 Unity 工程验证（本环境无 Unity，代码用 stub 编译验证 + 人工 Review）。

## 1. 团队分工模拟 / Simulated Team Roles

- **Coder1** (主架构/管线/打包/网格/材质): 负责整体管线、数据模型、岛提取、网格重写、材质/动画打补丁。
- **Coder2** (主算法/贴图/质量/Burst/图集): 负责质量指标、UV 缩放、光栅化装箱、图集构建、导入设置、i18n。
- **Reviewer1/Reviewer2**: 每个模块写完后共同审查（安全、引用一致性、Unity API 正确性、内存释放），达成共识后放行或打回。
- **QA1/QA2**: 全部完成后各自独立从头通读全部代码，查隐患/Bug/需求符合度，两个 QA 都通过才交付。

## 2. 关键取证结论（读源码确认，禁止猜测） / Verified API Facts

### NDMF 1.14.4 (`nadena.dev.ndmf`)
- 注册: `[assembly: ExportsPlugin(typeof(ATOPlugin))]`；插件类 `ATOPlugin : Plugin<ATOPlugin>`，`Configure()` 内 `InPhase(BuildPhase.Transforming)`。
- 排序: `Sequence.AfterPlugin("nadena.dev.modular-avatar")` / `.BeforePlugin("com.anatawa12.avatar-optimizer")`（字符串 QualifiedName 为官方推荐跨插件写法）。
- MA 插件 QualifiedName = `nadena.dev.modular-avatar`，主工作在 `Transforming`；AAO = `com.anatawa12.avatar-optimizer`，主工作在 `Optimizing`。→ 我们放 `Transforming` 且 After MA / Before AAO 即可满足「MA 执行后、AAO 执行前」。
- Pass: `public sealed class XPass : Pass<XPass>`，实现 `protected override void Execute(BuildContext ctx)`。
- `BuildContext`: `AvatarRootObject`, `AssetSaver.SaveAsset(obj)`（生成资产入容器,纹理自动作为 subasset）, `ObjectRegistry.GetReference(obj)`, `ObjectRegistry.RegisterReplacedObject(old,new)`, `ErrorReport`, `GetState<T>()`。
- 错误: 继承 `SimpleError`（`Localizer`/`TitleKey`/`Severity`/`References`），`ErrorReport.ReportError(err)`；`ErrorSeverity.Error` 阻断构建。
- i18n: `nadena.dev.ndmf.localization.LanguagePrefs.Language`（如 "zh-hans"/"en-us"）; `Localizer` 构造 `(defaultLanguage, List<(lang, Func<string,string>)>)`。NDMF 自身用 `LocalizationAsset`(csv)。**我们自研 JSON i18n**（用户要求 json 配置文件），Auto 模式读 `LanguagePrefs.Language`，回退英文。
- NDMF 在 Resolving 阶段已移除 EditorOnly（`RemoveEditorOnlyPass`）；BuildContext 构造时已拒绝 prefab 实例/资产。

### AAO 1.9.17 (`com.anatawa12.avatar-optimizer`)
- `API-Editor/com.anatawa12.avatar-optimizer.api.editor` asmdef 暴露 `Anatawa12.AvatarOptimizer.API`：
  - `UVUsageCompabilityAPI.IsTexCoordUsed(SkinnedMeshRenderer r, int channel)`（0..7）
  - `UVUsageCompabilityAPI.RegisterTexCoordEvacuation(renderer, originalChannel, savedChannel)`（savedChannel 若被 AAO 使用则抛异常）
  - 语义: 把「原始 UV」转移到另一通道，AAO 处理完会移除 saved channel。用于兼容 RemoveMeshByMask(uv0)/RemoveMeshByUVTile 等。
- AAO 无可选依赖机制 → 我们通过**反射**调用（`Type.GetType("Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI, com.anatawa12.avatar-optimizer.api.editor")`），未装 AAO 时静默跳过。
- AAO 自身对 liltoon 用反射读取 `lilToon.lilConstants` 版本 → 借鉴该模式。

### liltoon 2.3.4 (`jp.lilxyzw.liltoon`)
- 贴图属性（来自 AAO ShaderInformation.Liltoon + liltoon 源码 lilMaterialProperties）:
  - 主色: `_MainTex`(ST: `_MainTex_ST`, 滚动: `_MainTex_ScrollRotate`), `_BaseMap`, `_BaseColorMap`(dummy), `_MainColorAdjustMask`(同主UV)
  - 2nd/3rd: `_Main2ndTex`/`_Main3rdTex`（UV 通道由 `_Main2ndTex_UVMode`/`_Main3rdTex_UVMode` 决定: 0=uv0..3=uv3, 4=NonMesh(MatCap, 白名单), default=多通道=白名单）；blend: `_Main2ndBlendMask`/`_Main3rdBlendMask`(主UV)
  - 法线: `_BumpMap`(主UV), `_Bump2ndMap`(`_Bump2ndMap_UVMode` 同 2nd 逻辑), `_MatCapBumpMap`(主UV, 可选)
  - 蒙版/阴影: `_ShadingGradeTex`(见 liltoon 源码, 主UV), `_ShadowColorTex`/`_Shadow2ndColorTex`/`_Shadow3rdColorTex`(主UV), `_ShadowStrengthMask`/`_ShadowBorderMask`/`_ShadowBlurMask`, `_RimShadeMask`
  - 自发光: `_EmissionMap`/`_Emission2ndMap`(`_EmissionMap_UVMode` 类似), `_EmissionBlendMask`/`_Emission2ndBlendMask`(主UV)
  - 非网格UV(必须白名单): `_MatCapTex`/`_MatCap2ndTex`(屏幕/法线空间), `_DitherTex`(屏幕), `_MainGradationTex`(颜色空间), `_MainGradationTex`, `_TriMask`(liltoon 内部?), `_ParallaxMap`(POM 特殊采样→白名单)
  - 其他网格UV: `_OutlineTex`(主UV), `_OutlineWidthMask`, `_OutlineVectorTex`, `_FurVectorTex`/`_FurLengthMask`/`_FurMask`/`_FurNoiseMask`, `_MetallicGlossMap`/`_SmoothnessTex`/`_AnisotropyTangentMap`/`_AnisotropyScaleMask`/`_AnisotropyShiftNoiseMask`, `_DissolveMask`/`_DissolveNoiseMask`, `_GlitterColorTex`/`_GlitterShapeTex`, `_RimColorTex`/`_ReflectionColorTex`/`_BacklightColorTex`, `_AlphaMask`, `_AudioLinkMask`(主UV)
- 关键字: liltoon 用 `lilToonSetting`/`lilConstants`；我们不硬依赖，运行时枚举 shader 属性 + 关键字启发式。

### VRChat SDK 3.10.4
- `VRCAvatarDescriptor` = `VRC.SDK3.Avatars.Components.VRCAvatarDescriptor`（VRC.SDK3A 程序集，autoReferenced）。运行时 asmdef 引用 `VRC.SDK3A`。
- 动画层枚举 `VRCAvatarDescriptor.AnimLayerType`（Base/Additive/Gesture/Action/FX）。

### avatar-compressor (参考)
- GPU 读回模式参考: `TextureReadback.BlitToReadable`（RenderTexture + Graphics.Blit + ReadPixels，锁 + GL.sRGBWrite 保存/恢复 + try/finally 释放）→ 我们照此模式实现 GPU 重采样与 pull-push。

## 3. 总体设计 / Architecture

### 3.1 目录结构 (Unity 包)
```
Packages/net.fosa.avatar-texture-optimizer/
  package.json
  Runtime/
    net.fosa.avatar-texture-optimizer.runtime.asmdef
    ATOComponent.cs            (组件 + 序列化配置)
    QualityConfig.cs           (质量挡位/自定义参数)
  Editor/
    net.fosa.avatar-texture-optimizer.editor.asmdef
    ATOPlugin.cs               (NDMF 入口/管线/进度/取消)
    Log.cs / I18n.cs / Cancel.cs / Report.cs
    Core/Model.cs              (数据模型)
    Analysis/ShaderAnalyzer.cs / AvatarAnalyzer.cs / AnimationAnalyzer.cs / WhitelistResolver.cs
    Textures/TextureDecodeCache.cs / TextureDeduper.cs / QualityMetrics.cs / IslandScaler.cs
    UV/IslandExtractor.cs
    Packing/BitmaskRasterizer.cs / AtlasPacker.cs / AtlasBuilder.cs
    Output/MeshRewriter.cs / MaterialPatcher.cs / AnimationPatcher.cs / ImportSettingsApplier.cs / FinalDeduper.cs
    UI/ATOComponentEditor.cs
    API/PublicAPI.cs           (扩展接口)
  i18n/en.json, i18n/zh-CN.json
  Documentation~/README.md (包内), 仓库根 README.md
```

### 3.2 管线 (单 Pass 内分阶段, 每阶段计时/进度/可取消)
0. Validate: ATOComponent 合规（挂载对象必须含 VRCAvatarDescriptor；一个 Avatar 上唯一）→ 不合规报 Error 中止。
1. Collect: 遍历渲染器（跳过 EditorOnly）→ 材质槽 → ShaderAnalyzer 分类贴图(角色/UV通道/ST) → 过滤条件(无ST变换、非贴花、动画无变换) → 不满足视作白名单。
2. Animations: 收集 Avatar 上全部 AnimatorController（Animator + VRC Descriptor 各层）的 clip；分析材质属性绑定（贴图切换/ST/渲染模式/Cutoff/颜色）、物体 Active、网格切换、材质槽索引。
3. Whitelist: 用户白名单（对象不限类型：网格/材质/贴图/动画）+ 自动白名单（上述不合规项）；白名单内引用贴图全部跳过优化（同 UV 其他贴图跳过图集化、参与整图缩放+导入参数优化）。
4. Dedup textures: 按像素内容 + 导入设置去重，更新引用；白名单联动。
5. Build UV↔texture 映射 → UV 组（同 UV 的所有贴图,含动画切换与贴图类型组）→ 贴图类型组（主色/法线/蒙版/其他 按色彩空间+filterMode+特殊贴图存在性分类）。
6. Island extraction: 三角形→岛（拓扑连通+UV匹配）→ 重叠合并 → 越界处理（可平移归一化/跨缝→白名单+warning）→ 面积(形态键 0/100 取最大、动画缩放取最大) → 像素密度(px/m)。
7. Scaling: 质量算法(线性空间重采样/预乘alpha/多材质最严苛) + 密度钳制 + 纯色短路 + quality=1 跳过；均匀→双轴独立二分细化。
8. Atlas 或整图: (a) 图集模式: Burst 光栅化(4px) + 候选池(2^n 默认/64步进 NPOT 实验) + 队列(面积降序/类型组) + 全扫描BLF + 旋转90° + padding + pull-push 外扩; (b) 整图模式: 直接缩放贴图。
9. Rewrite: 网格新UV(多通道) + AAO UV 撤离(反射, 仅 SkinnedMeshRenderer 且通道被 AAO 使用)。
10. Patch materials: 贴图引用替换(不碰其他参数), 材质按需复制/去重, 不透明材质槽合并。
11. Patch animations: 贴图属性绑定/材质槽索引更新。
12. Import settings: 压缩格式(分类+平台override) + Mipmap/MipStreaming 绑定 + Clamp 强制 + Read/Write 关 + 安全 fallback。
13. Final dedup: 内容/参数完全相同的材质、贴图/图集去重并更新引用。
14. Remove ATOComponent from build output; Report 输出到 NDMF 控制台（总体+折叠细节）。

### 3.3 质量算法
- 线性空间重采样（sRGB 纹理解码到线性, 缩放后用 GPU 双线性, 回传原尺寸比较）。
- 透明: 预乘 alpha 下采样; Cutout→裁剪后轮廓 IoU(在 clip 阈值), Blend→线性 RMSE(alpha 通道); 多材质引用→每个引用材质的透明模式/Cutoff 逐一评估取最严苛。
- 不透明: MS-SSIM + ΔE(CIEDE2000)。岛包围盒短边<176px→单尺度SSIM；<11px→忽略这两个参数(只做密度/面积约束)。
- 法线: 正确解码(XY*2-1, Z 重建)→重采样→重归一化编码→角度误差 + p95 对比。
- 灰度: 仅被使用的通道、线性空间 RMSE, 逐通道取最差。
- 比较方式: 缩小岛覆盖区双线性上采样回原尺寸后与原图比较。
- UV 缩放: 二分搜索（均匀），最差阈值全达标才算通过；UV 组内木桶效应取最大尺寸（≤组内最大原尺寸）。
- 评估: Burst 并行 + GPU(RenderTexture) 批量执行; 不含最终压缩格式损失。
- 目标质量!=1: 纯色岛短路缩到 min(4, 原岛包围盒短边)。目标质量==1: 对应贴图类型跳过 UV 缩放(不重采样原样拷贝)。
- 密度: 默认最小 2048px/m、最大 4096px/m；挡位 512/1024/2048/4096/8192；受原贴图物理文件大小钳制。
- 质量挡位: 预设(如 0.9/0.95/0.98/0.999?) + 自定义(默认全 1=近无损)，折叠在高级选项；挡位改变时具体参数值同步改变。

### 3.4 图集
- 岛间距 padding = max(ceil(候选图集最大边长/128), 4px)；用户可选最小 padding 4/8/16/32/64（默认4）。图集边缘 GPU pull-push 无限外扩填充（透明 alpha 保持 0）。
- 候选池: 默认 2^n 边长, min 64, max 8192(移动端 4096)；NPOT 实验选项: 64 步进, 剔除不支持格式(如 iOS PVRTC)。
- 装箱: 队列=贴图类型组, 按光栅化总面积降序; 原子单位=单张贴图及其 UV 组; 先算队列所需总 UV 面积, 丢弃过小候选, 按面积升序+长宽比最接近方形优先; 首个能装下全部的作为成品; 装不下→另开队列(复用同类); 单贴图装不进最大图集→放弃该 UV 组图集化, 质量缩放后继续+warning。
- 岛形状光栅化装箱（非矩形）。
- 法线图集: 切线数据原样拷贝、绝不重算。
- 图集名 `ATO_` 开头; 图集数量不限。
- 图集默认关 Read/Write、强制 Clamp（不可改）；其余参数取所有来源贴图中质量最高者。

### 3.5 其他关键点
- Mipmap 与 MipStreaming 绑定（一个开关同时控制；开 Mipmap 强制开 MipStreaming）。
- 平台选项: PC/Android/iOS override（参考 Unity platform override），默认读取当前构建平台；影响图集格式等受平台限制参数；勾选才显示对应平台折叠区。
- 压缩格式安全枚举: 按透明贴图/不透明贴图(按图集是否有 alpha)/法线/灰度 分类；灰度设单通道但存在多通道灰度图时构建回退多通道+warning；带透明度贴图不提供无 alpha 选项。
- 导出顺序: MA 之后、AAO 之前; 兼容 AAO UVUsageCompabilityAPI(未装 AAO 不报错)。
- 取消: DisplayCancelableProgressBar; 取消→保留临时资产, 释放 CPU/GPU/内存。
- 日志: `[ATO]` 前缀, 含耗时/贴图来源/岛数/图集大小/利用率/优化量; 开关控制详细日志; 默认总体结果, 细节折叠。
- 渲染一致性: 只改网格 UV 与贴图引用, 绝不改材质其他着色器参数。
- 接口: API/PublicAPI.cs 预留扩展点（第三方面向对象分析器、图集策略、质量指标等）。

## 4. 进度 / Progress
- [x] 0. 环境搭建、8 个参考包下载解压
- [x] 1. 关键 API 取证（NDMF/AAO/MA/liltoon/VRC/avatar-compressor）
- [x] 2. 设计共识（本节）写入 CLAUDE.md
- [x] 3. 包骨架: package.json / asmdef / i18n json / 组件与配置
- [x] 4. Core 模型 + 分析（Shader/Avatar/Animation/Whitelist）
- [x] 5. 贴图: 去重/解码缓存/质量指标(Burst)/岛缩放
- [x] 6. UV 岛提取
- [x] 7. 装箱(Burst) + 图集构建 + pull-push
- [x] 8. 输出: 网格/材质/动画/导入设置/最终去重
- [x] 9. UI(i18n 组件编辑器) + 报告 + 取消 + 日志
- [x] 10. Reviewer 审查 + QA 全量验收（编译验证 + 22 项算法测试全过）
- [x] 11. 编译验证(stub) + git 提交 + zip 打包 + README

## 4.1 最终交付清单 / Final deliverable
- 包源码: `Packages/net.fosa.avatar-texture-optimizer/`（Runtime + Editor + i18n + shader）
- 测试台: `Tests/AlgorithmHarness/`（22 项算法断言，dotnet 可直接跑）
- 文档: `README.md`（面向用户与第三方开发者）
- 本记忆: `CLAUDE.md`

## 4.2 QA 记录（双人独立验收） / QA notes
QA1 发现并修复:
  - Rasterize 用岛三角形索引当顶点索引 → 改用 mesh.triangles 正确映射（算法测试抓出）。
  - 试探性 64px 初始队列被遗弃为空槽 → PruneEmptySlots。
  - MaterialPatcher 图集查找用 TextureRef 引用相等（槽位/组内对象不同）→ 改用 (Texture2D,UVGroup) 键。
  - FinalDeduper 对 isReadable=false 资产调 GetPixels32 → 改用持久化前内存哈希。
  - MeshRewriter AAO 撤离读到了重写后的新网格 UV → 记录 rendererSourceMesh。
QA2 发现并修复:
  - `importer.mipmapStreaming` 属性名不确定 → 照 AAO 实测改用 SerializedObject("m_StreamingMipmaps")。
  - 白名单组（非跨缝）内非白名单贴图未走整图缩放 → 补管线路径。
  - 槽合并原地修改共享原网格 → 按渲染器复制。
  - zh-CN i18n 键名笔误（maxAlphaCutoutIoU→minAlphaCutoutIoU）。
验证:
  - dotnet stub 编译验证: 通过（全部 30 文件）。
  - Tests/AlgorithmHarness: 22/22 通过（岛提取/光栅化/装箱/镜像/旋转/利用率/无重叠）。

## 4.3 已知限制（如实记录） / Known limitations
- 未在真实 Unity 工程中运行验证（用户手动同步验证）；GPU pull-push 与导入设置路径
  依赖 Unity 运行时行为，若 shader 加载失败有安全 fallback（透明留白 + warning）。
- MipStreaming 用 SerializedObject("m_StreamingMipmaps")（AAO 同款，2021.2+）。
- 渲染器动画网格切换(m_Mesh) → 槽位白名单（保守）。
- 非 customizeAnimationLayers 的 VRC 默认层不在解析范围（默认层不引用 Avatar 材质）。

## 5. 注意事项 / Gotchas
- 不得猜测 API；修改前先读相关源码（ndmf/aao/liltoon 已在本仓库 ref/ 目录）。
- 只允许修改网格 UV 与贴图引用，禁止改材质其他着色器参数。
- 所有新生成资产走 `ctx.AssetSaver.SaveAsset`；GPU 资源 try/finally 释放防泄漏。
- 日志 [ATO] 前缀 + 耗时；代码注释中英双语。
- 用户手写同步代码进 Unity 工程验证，因此包结构必须可直接放入 Packages/。
- 版本兼容：开发期可随意改配置字段，不承诺序列化兼容。
