# CLAUDE.md — AvatarTextureOptimizer 项目记忆

> 本文件是 AgentTeam 关于本项目的唯一记忆载体。每次变更后必须更新本文件并 git 提交。
> 防止因上下文过长或网络中断导致丢失主要工作。

## 项目概要

- **名称**：AvatarTextureOptimizer（包名 `net.fosa.avatar-texture-optimizer`）
- **目标**：VRChat Avatar 的 NDMF 开源贴图优化工具。分析网格 UV→贴图映射，按目标质量缩放 UV 岛，装箱生成图集，重映射网格/材质/动画引用，最大程度提高贴图利用率。
- **运行时机**：MA 执行后、AAO 执行前（Optimizing 阶段，`.BeforePlugin("com.anatawa12.avatar-optimizer")`）。
- **语言**：与用户使用简体中文交流；代码注释中英双语；交付 i18n 英文+简体中文。

## AgentTeam 分工（本项目的执行方式）

- **Coder-A / Coder-B**：写代码前先交流得出共识结论再落实代码。
- **Reviewer-A / Reviewer-B**：Coder 写完任何代码后共同审查，共识后决定是否打回。
- **QA-A / QA-B**：整体完成后各自独立从头完整阅读全部代码，共识通过才交付；有缺陷同时通知 Reviewer 和 Coder 打回。
- 每次修改后 git 提交 + 更新本文件。

## 已研读的依赖库（关键 API 事实，禁止猜测）

### NDMF 1.14.4（已通读核心源码）
- 注册：`[assembly: ExportsPlugin(typeof(MyPlugin))]`（`nadena.dev.ndmf.ExportsPlugin`）
- 插件：`class MyPlugin : Plugin<MyPlugin>`，`Configure()` 中用 `InPhase(BuildPhase.Optimizing).Run(...)` 注册 pass；`.Run("name", ctx => {...})` 匿名 pass；`.Run(PassClass.Instance)` 类 pass；`.BeforePlugin("qualified-name")` 弱顺序约束。
- Pass：`class MyPass : Pass<MyPass>`，`protected override void Execute(BuildContext ctx)`。
- `BuildContext`：`AvatarRootObject`、`GetState<T>(init?)`、`ErrorReport`、`OpenSerializationScope()`、`IsTemporaryAsset()`、`AssetSaver`、`AssetContainer`。
- `nadena.dev.ndmf.vrchat.VRChatContextExtensions.VRChatAvatarDescriptor(this BuildContext)` 扩展方法（该程序集 defineConstraints 为 `VRCHAT_AVATARS_PRESENT`）。
- `RemoveEditorOnlyPass` 在 `nadena.dev.ndmf.builtin` 命名空间。
- `RunsOnAllPlatforms` / `RunsOnPlatforms` 属性；`WellKnownPlatforms.VRChatAvatar30`。
- NDMF 自身 asmdef 用 `versionDefines` 定义符号（如 `NDMF_VRCSDK3_AVATARS`）。
- 语言：`nadena.dev.ndmf.localization.LanguagePrefs.Language`（"en-us"/"zh-hans"...），`RegisterLanguage(code)`。

### AAO 1.9.17（UVUsageCompabilityAPI 已通读）
- 命名空间 `Anatawa12.AvatarOptimizer.API`；静态类 `UVUsageCompabilityAPI`。
- `bool IsTexCoordUsed(SkinnedMeshRenderer renderer, int channel)`（0..7）。
- `void RegisterTexCoordEvacuation(SkinnedMeshRenderer renderer, int originalChannel, int savedChannel)`。
- `Impl` 静态字段构建期注入；构建外调用抛 `InvalidOperationException`。
- 协议：重写 UV 前检查 AAO 是否使用该通道 → 若使用，将原始 UV 拷到备用通道 → `RegisterTexCoordEvacuation(original, saved)`。
- 可选依赖做法：主 editor asmdef `versionDefines` 定义 `ATO_AAO`，独立 compat asmdef `defineConstraints: ["ATO_AAO"]` 引用 `Anatawa12.AvatarOptimizer.API`。

### lilToon 2.3.4（属性结构已通读）
- 属性带 `[lilToon]`/`[lilUVAnim]`/`[NoScaleOffset]` 等属性；`_UseXXX` 开关；`_Main2ndTex_UVMode` 等 UV 模式属性（0=UV0..3=UV3, 其他=非网格UV）。
- 关键贴图属性：`_MainTex`（主色）、`_ShadeTexture`、`_NormalMap`、`_MatCapTex`（屏幕空间，非网格UV！）、`_Main2ndTex`/`_Main3rdTex`、蒙版类 `_Main2ndBlendMask` 等。
- 通过 `Shader.GetPropertyCount()/GetPropertyName()/GetPropertyType()/GetPropertyFlags()/GetPropertyTextureDimension()` 运行时解析（标准关键字方案，兼容未来版本）。

## 架构与决策（Coder 共识记录）

### 程序集布局
- `Runtime/net.fosa.avatar-texture-optimizer`：组件 + 纯数据设置（不依赖 VRCSDK/NDMF）。
- `Editor/net.fosa.avatar-texture-optimizer.editor`：主逻辑；versionDefines：`NDMF_VRCSDK3_AVATARS`、`ATO_AAO`、`ATO_LILTOON`。
- `Editor/Compat/AAOCompat.asmdef`：defineConstraints `ATO_AAO`，仅装 AAO 时编译。
- 已写：package.json、4 个 asmdef、README 占位未写（任务完成后写）、Localization/en-US.json、zh-CN.json。

### 已实现（本阶段完成的文件）
- **Phase 4-6 新增（应用/图集/收尾）**：`Atlas/Shaders/ATO_PullPush.compute`（GPU pull-push 无限外扩）、`Atlas/AtlasBuilder.cs`（岛 GPU 重采样绘制+旋转、pull-push 填充、ATO_ 命名、类别导入参数、去重、NDMF 容器持久化）、`Import/TextureImportConfig.cs`（压缩格式安全映射+平台规则+alpha 兜底+Mipmap/MipStreaming 绑定）、`Apply/Applier.cs`（网格重映射+顶点拆分+形态键/骨骼权重重建+AAO 反射疏散+材质重赋+整图副本）、`Apply/AnimationUpdater.cs`（动画对象引用更新+槽索引重命名）、`Apply/Deduplicator.cs`（贴图/材质去重+材质槽合并+Read/Write 关闭+持久化+报告）、`Analysis/DensityAnalyzer.cs`（形态键 0/100 最大面积+动画最大缩放面积）、`README.md`。
- **AAO 兼容改为反射方案**：删除 AAOCompat.asmdef；AAOUVUsageCompat 用反射调 UVUsageCompabilityAPI（已通读源码），无编译期依赖。
- **Phase 3 新增（装箱器）**：`Atlas/RasterMask.cs`（4px 位掩码 + 边函数三角形光栅化 + Burst 全扫描 BLF 放置搜索，含非字节对齐位偏移重叠测试）、`Atlas/Packer.cs`（候选池生成 [面积升序→长宽比升序，NPOT 64px 步进，aspect≤2 过滤，MaxCandidates 上限]、类型组队列 [面积降序]、原子操作=贴图+UV组、首个装下全部即成品、拆分路径、装不下→SkippedAtlas+警告、法线组禁止旋转、padding=max(min,ceil(max/128))、回滚）。
- **Phase 2 新增（质量引擎）**：`Quality/Shaders/ATO_SSIM.compute`、`Quality/Shaders/ATO_ImageOps.compute`、`Quality/GPUImageOps.cs`、`Quality/MS_SSIM.cs`（GPU 5 级 MS-SSIM，短边<176 单尺度、<11 忽略）、`Quality/CIEDE2000.cs`（Burst）、`Quality/Metrics.cs`（Burst：alpha RMSE/Cutout IoU/法线角度 p95/灰度通道 RMSE）、`Quality/QualityEvaluator.cs`（线性空间+预乘 alpha，按引用取最严苛）、`Quality/IslandScaler.cs`（二分搜索最小通过缩放 + 密度钳制 + 纯色短路 + 各向异性逐轴二分 + 整图路径 + UV 归一化）。
- Runtime：`AvatarTextureOptimizer.cs`（组件）、`ATOSettings.cs`（QualitySettings/QualityThresholds/AtlasSettings/ImportSettings/PlatformSettings/WhitelistSettings + 枚举 QualityTier/ATOCompressionFormat/ATOTargetPlatform/ATOImportCategory）。
- Editor/Localization：`ATOI18n.cs`（用户可扩展 JSON i18n + MiniJson 解析器，Auto 跟随 NDMF 语言，回退英文）。
- Editor/Logging：`ATOLog.cs`（[ATO] 前缀分级日志 + 计时）、`BuildReport.cs`（结构化报告）。
- Editor/Progress：`ATOBuildProgress.cs`（进度 + 协作式取消，`ATOBuildCancelledException`）。
- Editor/Model：`TextureUsage.cs`、`UVIsland.cs`（含 UVGroup）、`TextureTypeGroup.cs`（含 AtlasEntry）、`ATOBuildState.cs`。
- Editor/Analysis：`ShaderAnalyzer.cs`、`TextureCollector.cs`、`AnimationScanner.cs`、`WhitelistResolver.cs`、`TextureDeduplicator.cs`。
- Editor/Processing：`IslandExtractor.cs`（并查集连通+重叠合并+空间哈希+可归一判定+纯色占位+像素密度）。
- Editor/NDMF：`ATOPlugin.cs`、`ATOPasses.cs`（Validate/Collect/Group/ExtractIslands + Scale/Pack/BuildAtlases/Apply/Finalize）。
- Editor/UI：`ATOComponentEditor.cs`（IMGUI，语言下拉、折叠高级选项、平台覆写）。
- Editor/Compat：`ExtensionRegistry.cs`、`AAOUVUsageCompat.cs`（AAO 疏散计划+包装）。

### 质量挡位设计（学术参考）
- Ultra：MS-SSIM 0.995 / ΔE 1.0 / alphaRMSE 0.004 / CutoutIoU 0.999 / 法线 1° / 灰RMSE 0.004
- High（默认）：0.99 / 2.0 / 0.008 / 0.998 / 2° / 0.008
- Medium：0.98 / 3.0 / 0.016 / 0.995 / 4° / 0.016
- Low：0.96 / 5.0 / 0.03 / 0.99 / 8° / 0.03
- Custom：全部 1（近无损），用户自改，永不被覆盖。
- 依据：CIEDE2000 JND≈2.3 (Sharma 2005)；MS-SSIM≥0.99 视觉无损共识；法线角度 <2° 不可感知。

## 整体进度

- [x] 依赖研读（NDMF/AAO/lilToon）
- [x] 工程骨架（package.json/asmdef/组件/设置/i18n/日志/进度/模型/分析/UV岛提取/NDMF集成/UI/扩展点）
- [x] **质量引擎**（MS-SSIM GPU / CIEDE2000 Burst / alpha / 法线 / 灰度；线性空间；预乘alpha；GPU RenderTexture + Burst）
- [x] **UV 缩放**（二分搜索最小通过缩放；纯色短路 min(4,短边)；质量=1 跳过；密度钳制 [minPPM,maxPPM]+不超原尺寸；各向异性先均匀再逐轴二分；UV 归一化记录）
- [x] **装箱**（Burst 光栅位掩码 4px + 全扫描 BLF + 面积降序 + 边长降序 + 90°旋转步进 + 候选图集池 NPOT 选项 + 类型组队列 + 岛形状装箱非矩形 + 装不下逻辑）
- [x] **图集构建**（GPU 重采样 + pull-push 无限外扩填充；ATO_ 前缀；压缩格式安全枚举按类别；Mipmap↔MipStreaming 绑定（SerializedObject 写 m_StreamingMipmaps，AAO 技术）；图集 Clamp/关闭ReadWrite 强制；NDMF 容器持久化）
- [x] **应用**（网格 UV 重写 + 逐子网格顶点拆分 + 骨骼权重/形态键重建；AAO UV 疏散（反射调用）；材质贴图重赋；动画引用更新；材质槽合并）
- [x] **平台覆写落地**（PC=BC7/DXT、Android/iOS=ETC2/ASTC；NPOT 剔除 PVRTC；alpha 安全兜底）
- [x] **形态键 0/100 取最大面积 / 动画最大缩放面积**（DensityAnalyzer：AABB 面积因子，O(shapes*verts) 保守实现）
- [x] **ndmf 预览暂不支持**（需求确认：不做）
- [x] **README.md**（已编写）
- [x] **打包 zip 交付**（本轮）

## Reviewer 已修复的 bug 记录（Phase 2-6）
1. `Validate()` 恒返回错误（stub 逻辑）→ 改为由 ATOValidatePass 做真实 VRCSDK 校验。
2. `MS_SSIM` SetTexture 绑定到错误内核 → 改为逐内核索引绑定。
3. `TextureDeduplicator.UpdateReferences` 先改 usage.Texture 再查 remap → 材质引用更新不到 → 重排为先更新材质再改写 usage。
4. `IslandExtractor` 世界面积用了错误子网格索引 → 岛记录 SubmeshIndex，面积用该子网格索引缓冲。
5. 进度/取消未接入 → ATOBuildProgress 接入 Collect，全 pass 增加 `state.Cancelled` 优雅退出。
6. UI `managedReferenceValue` 用在普通序列化类上 → 移除，直接 WriteThresholds。
7. Python 脚本贪婪正则把取消检查重复堆到 Finalize → 清理为每 pass 恰一处。
8. **UV 重写未按子网格区分**（跨材质槽共享顶点 UV 冲突）→ MeshRemapper 改为 per-(submesh, channel) 数组 + 顶点拆分。
9. **AAO 直接引用导致程序集依赖错误**（AAOCompat.asmdef 只在装 AAO 时编译，但文件在主程序集）→ 改为反射调用，删除 asmdef。
10. **图集绘制未按布局过滤**（每张图集画了所有组的岛）→ 只画 atlas.LayoutIndex 布局内且引用本类型组贴图的组。
11. **动画启用渲染器在 Scan 阶段被跳过**（Scan 早于动画扫描）→ Scan 收集全部，GroupPass 按 AnimationFacts.IsRendererEffectivelyEnabled 过滤。
12. **白名单渲染器未排除** → GroupPass RemoveAll WhitelistedRenderers 的组。
13. `group.SubmeshIndex` 引用不存在字段 → 删除（island 已存）。
14. AtlasEntry.Name 同类型组重名 → 加入 Index。
15. 生成的贴图保持可读违反"图集关闭 Read/Write" → 去重后 Apply(false, true) 关闭。
16. Applier/TextureImportConfig 缺 using（TextureCollector/ATOLog）→ 补齐。

## 待办/注意事项（踩坑记录）

1. `IslandExtractor.ComputePixelDensity` 目前是估算（只用了变换缩放，UV 面积计算有 TODO）；形态键 0/100 取最大面积、动画最大缩放面积需在后续实现。
2. `DetectSolidColor` 目前占位返回 false，需在 IslandScaler 阶段用可读贴图实现（质心+内部点采样）。
3. `AnimationScanner` 的直接材质绑定（binding.type==Material）目前保守取第一个材质，可能轻微扩大白名单（安全方向）。
4. 动画 ST 的白名单目前基于材质级 `AnimatedSTMaterials`；渲染器级 "material._X_ST" 曲线（binding 挂在渲染器上）尚未显式收集——需要在 Collect 阶段把 `material.*_ST` 曲线也归入 AnimatedSTMaterials（见 AnimationScanner.ParseClip 的 renderer 分支 TODO）。
5. 材质槽动画切换（m_Materials.Array.data[N] 对象引用）已在 facts.AnimatedMaterialSlots 记录，但"合并材质槽后更新动画索引"的逻辑在 Deduplicator 阶段实现。
6. 组件在 Finalize 阶段 DestroyImmediate 移除自身。
7. i18n 文件夹：Package 布局 `Packages/net.fosa.avatar-texture-optimizer/Localization`；Assets 布局回退 `Assets/AvatarTextureOptimizer/Localization`。
8. `ATOSettings.Quality.ApplyTier` 与 Editor 中 `WriteThresholds` 两条路径都要保持与 `DefaultThresholds` 一致。
9. 尚未生成 .meta 文件（Unity 会自动生成）；git 提交时注意 .gitignore 排除 ThirdParty 下载的 zip/解压产物（仅作研读参考，不随包分发）。
10. 用户会在 Unity 工程内手动同步验证烘焙；每次修改后必须确保可编译（本阶段未做编译验证，因为沙箱无 Unity；靠仔细的代码评审兜底）。

## git 记录要求
- 每次修改后：`git add -A && git commit -m "阶段描述"`，同步更新本文件。
