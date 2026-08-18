# CLAUDE.md — AvatarTextureOptimizer 项目记忆

> 本文件是本项目的唯一记忆载体。每次修改后必须更新。

## 项目定位

包名 `net.fosa.avatar-texture-optimizer`，目标是「全世界最好的 VRChat 贴图优化工具」。
开源 NDMF 工具：分析 Avatar 网格 → 建立网格 UV→贴图映射 → 按质量挡位缩放 UV 岛 →
剔除未用 UV → 重排合并成图集，最大化贴图利用率。

## 整体进度

**全部核心功能已实现并通过 stub 工程编译验证 + 数值验证。等待用户在 Unity 中实机验证。**

代码量约 8300 行（Runtime + Editor）。

| 层 | 文件 | 状态 |
| --- | --- | --- |
| Runtime 配置 | `Runtime/Config/{Enums,QualityPreset,ATOSettings}.cs` | ✅ |
| Runtime 组件 | `Runtime/Components/AvatarTextureOptimizer.cs` | ✅ |
| 工具 | `Editor/Core/Util/{ATOLogger,TextureCache}.cs` | ✅ |
| 数据模型 | `Editor/Core/Model/Model.cs` | ✅ |
| 分析 | `Editor/Core/Analysis/{ShaderAnalyzer,MaterialAnalyzer,WhitelistResolver,TextureDeduplicator,AnimationAnalyzer,TextureCollector}.cs` | ✅ |
| 质量 | `Editor/Core/Quality/{ImageMetrics,Resampler,QualityEvaluator}.cs` | ✅ |
| 网格 | `Editor/Core/Mesh/{UVIslandExtractor,MeshAreaAnalyzer,MeshUVRewriter}.cs` | ✅ |
| 装箱 | `Editor/Core/Packing/{IslandRasterizer,AtlasPacker,BurstPackKernel}.cs` | ✅ |
| 合成 | `Editor/Core/Atlas/AtlasCompositor.cs` + `Editor/Shaders/ATOPullPush.shader` | ✅ |
| 输出 | `Editor/Core/Output/{TextureOutput,MaterialRemapper}.cs` | ✅ |
| 兼容 | `Editor/Core/Compat/AAOCompat.cs` | ✅ |
| 编排 | `Editor/Core/OptimizationPipeline.cs` | ✅ |
| 插件 | `Editor/Plugin/{ATOPlugin,ATOPass}.cs` | ✅ |
| UI | `Editor/UI/AvatarTextureOptimizerEditor.cs` | ✅ |
| i18n | `Editor/Localization/ATOLocalization.cs` + `Editor/Resources/i18n/{en,zh-Hans}.json` | ✅ |
| 文档 | `README.md`、`docs/QualityPresets.md` | ✅ |

## 管线 6 阶段（OptimizationPipeline.Run）

1. **收集**：白名单展开 → 动画扫描 → 贴图去重 → 按 UV 流分组（TypeSignature 隔离分类/色彩空间/filterMode）
2. **网格分析**：提取 UV 岛 → 重叠合并 → 越界归一化（跨缝则排除）→ 世界面积计算
3. **质量搜索**：逐岛二分（先均匀 8 次，再逐轴 6 次），组内取各贴图所需的最大尺寸
4. **装箱**：光栅化真实轮廓 → Burst 并行 BLF → 候选池择优
5. **合成**：逐贴图 blit + pull-push 外扩 → 压缩格式定型
6. **应用**：AAO UV 撤离 → 重写网格 UV → 克隆材质换贴图引用

## 已发现并修复的真实缺陷（重要）

1. **`System.Numerics.BitOperations` 在 netstandard2.1 不可用** → 自实现 de Bruijn TZCNT。
2. **`Ciede2000` float 精度不足**：在 `|h1'-h2'|` 与 180° 分支比较处选错分支 → 全程改 double。
3. **预乘 alpha 下采样除零**：全透明区域除以 0 alpha 使 RGB 归零，导致**所有透明贴图在任何尺寸都无法通过质量检查**（永远不会缩小）→ alpha≈0 时回退未加权均值。
4. **合成器旋转压扁长宽比**：旋转岛被直接重采样到交换后的尺寸，且占用footprint 与装箱器预留的方向相反 → 改为重采样到未旋转 PackedSize，旋转以精确索引转置施加。
5. **Pull-push 用 alpha 判覆盖**：会把岛**内部**本就透明的 texel 误判为未写入并覆盖其颜色 → 改用独立 coverage 缓冲。
6. **管线未接线（最严重）**：AtlasCompositor / MeshUVRewriter / MaterialRemapper / AAOCompat 从未被调用，构建会「成功」但 Avatar 完全没有变化 → 补齐阶段 5、6。
7. **`island.SourceRect` 陈旧**：在质量循环中逐贴图写入，只保留最后一张的像素空间，但合成器对组内所有贴图复用它；混分辨率组（如 2048 albedo + 512 mask）会采样错误区域 → 合成器按每张贴图自行推导。
8. 猜错的 API：`requestedMipmapLevel` → `SetStreamingMipMapSettings`；`UnityEditor.ShaderUtil` → `Shader.GetPropertyCount`；`ReportException(e, msg)` 第二参数其实是 `additionalStackTrace`；`component.settings`（私有）→ `component.Settings`。

## 数值验证结论（`_verify/mathtest`，非交付物）

- **CIEDE2000**：Sharma/Wu/Dalal(2005) 全 34 组吻合（tol≈3e-3）。
- **装箱**：de Bruijn TZCNT 对全 64 单 bit + 20000 随机值 + 0 全部正确；**Burst 内核与标量参考在 60/60 随机用例（5 种网格尺寸 × 4 种非矩形形状）结果完全一致**；40 次放置均无重叠；BLF 性质成立。
- **重采样**：同尺寸恒等；纯色保持；预乘 alpha 阻止绿色渗透而直通 alpha 会渗透（对照验证）；法线重采样保持单位长度。
- **质量搜索**：无损档绝不重采样（含纯色）；纯色短路到 4×4；挡位越松结果面积单调不增；噪声图保留面积 ≥ 渐变图。
- **合成/UV 一致性**：blit footprint 的面积/朝向/原点与装箱器预留一致，且**每个源 texel 都能在网格实际采样的 UV 处被正确读回**——覆盖 upright/rotated × square/16×4/4×16 × 非方形图集。
- **外扩**：核心像素不被改动；填充区 alpha 恒为 0；已覆盖但透明的 texel 颜色被保留；法线填充重新归一化。
- **i18n**：en/zh-Hans 各 75 键完全对齐、占位符一致；CJK 与全角标点、含冒号的键、全部转义（含 `\uXXXX`）正确；8 种畸形输入不抛异常不死循环。
- **混分辨率**：32px 与 128px 源经图集化后结果完全一致（diff 0.0000）。

## 取证确认的关键事实（不可再猜）

- NDMF：`Plugin<T>.Configure()` → `InPhase(BuildPhase.Optimizing).AfterPlugin("nadena.dev.modular-avatar").BeforePlugin("com.anatawa12.avatar-optimizer").Run(Pass.Instance)`；`Pass<T>` 提供静态 `Instance`，需 override `Execute(BuildContext)`；`[assembly: ExportsPlugin(typeof(T))]`。
- `BuildContext`：`AvatarRootObject`、`AssetSaver.SaveAsset()`（**生成资产必须显式保存**）、`GetState<T>()`、`Extension<T>()`。
- `ErrorReport.ReportError(Localizer, ErrorSeverity, string key, params object[])`；`ReportException(Exception, string additionalStackTrace = null)`。
- i18n：`new Localizer(defaultLang, Func<List<(string, Func<string,string>)>>)`；`GetLocalizedString(key)`。
- AAO `UVUsageCompabilityAPI`（拼写如此）：`IsTexCoordUsed(SkinnedMeshRenderer, int 0-7)`、`RegisterTexCoordEvacuation(smr, orig, saved)`，saved 被占用抛 `InvalidOperationException`。**仅 SkinnedMeshRenderer**。全程反射调用。
- lilToon：TBN 由顶点 `input.tangentOS` 构建（`lil_common_vert.hlsl:108`），不用 UV 求导 → **UV+texel 同步 90° 旋转、切线不重算是逐像素等价的**。
- lilToon 危险属性：`*_ST`、`*_ScrollRotate`、`*_UVMode`、`*IsDecal`、`*DecalAnimation`、`*DecalSubParam`、`_UDIMDiscard*`；`_TransparentMode`: 0=Opaque,1=Cutout,2=Transparent,3=Refraction,4=Fur,5=FurCutout,6=Gem。
- AAO MaxTextureSizeProcessor 经验：Crunch 不可处理需 warning；构造 Texture2D 用 **TextureFormat + linear 布尔**重载（GraphicsFormat 重载在 Windows 编辑器下误判 ASTC 失败）；需手工复制 wrapModeU/V、filterMode、anisoLevel、mipMapBias。
- `MS-SSIM` 权重 {0.0448,0.2856,0.3001,0.2363,0.1333}；`MsSsimMinShortSide=176`、`StructuralMetricIgnoreShortSide=11`。

## 4 项硬约束

1. 材质必须克隆，克隆只改贴图引用。
2. AAO UV 撤离仅支持 SkinnedMeshRenderer；MeshRenderer 或 8 通道占满 → 排除 + warning。
3. 无损档 + 图集化须整数 texel 偏移 + 仅 90° 整数倍旋转（4px 光栅粒度天然满足）。
4. 重叠岛合并 / UV 重排若拆分顶点，须同步复制 blendshape delta、骨骼权重、切线（MeshUVRewriter 已全部处理）。

## 未完成 / 待确认

- **Burst 实机验证**：用户已选择「硬依赖 Burst」。`FindPositionJob` 的 job body 已用功能性 stub 验证逻辑正确，但 **Burst 编译本身无法在本环境验证**，需在 Unity 中确认能通过 Burst 编译（无托管对象引用、无异常抛出）。
- **`ATOPullPush.shader` 已接线但未在 GPU 上验证**。`AtlasCompositor.DilateGpu` 走 pull-push 三 pass（Pull=0/Push=1/Resolve=2），失败自动回退 CPU `Dilate`；stub 环境 `Shader.Find` 恒为 null，故本地始终走 CPU 路径，**GPU 分支的实际像素输出必须在 Unity 中验证**（重点看外扩区颜色是否连续、padding alpha 是否恒 0、法线是否已重归一化）。
- 暂不支持 ndmf 预览（需求已明确暂不支持）。

## 本轮补齐（GPU 外扩 / 材质去重 / 动画重定向 / 平台 UI / lilToon 守卫）

1. **GPU pull-push 外扩**：`AtlasCompositor.DilateGpu(buffer, coverage, w, h, isNormalMap)`。pull 建覆盖度加权 mip 金字塔，push 由粗到细回填，resolve 用 `_OriginalTex`/`_CoverageTex` 还原原 texel 并把填充区 alpha 置 0；法线走 `RenormalizeUncovered`（仅 coverage==0）。中间 RT 全部 ARGBFloat+Linear+HideAndDontSave，`finally` 统一释放并还原 `RenderTexture.active`。调用点为 `if (!DilateGpu(...)) Dilate(...)`。
2. **材质去重**：`OptimizationPipeline.DeduplicateMaterials`，受 `settings.deduplicateMaterials` 控制，在材质记录前执行；映射存入 `OptimizationResult.MaterialDeduplication`，日志 `Material dedup: N slots merged`。
3. **动画引用重定向**：`ATOPass.RepointAnimationReferences` 用 `context.Extension<AnimatorServicesContext>().AnimationIndex.RewriteObjectCurves(Func<Object,Object>)` 把被合并材质重定向到代表材质；整段 try/catch，失败只 warning 不中断构建。
4. **平台 override UI 补全**：新增 Custom 档展开 `customQuality`、`minPadding`、`allowNpot`、`deduplicateTextures`、`deduplicateMaterials` 及四类输出（opaque/transparent/normal/grayscale）。12 个字段名已与 `PlatformSettings` 逐一核对一致。
5. **lilToon 危险属性守卫**（`ShaderAnalyzer.IsMaterialUsageSafe` 新增）：`*IsDecal`/`*IsLeftOnly`/`*IsRightOnly`/`*ShouldCopy`/`*ShouldFlipMirror`/`*ShouldFlipCopy`/`*IsMSDF`/`*Angle≠0`/`*DecalAnimation`+`*DecalSubParam` 真实网格/`_UDIMDiscardCompile`。
   - **取证**：`lilCalcDecalUV`（`lil_common_functions.hlsl:473`）会镜像、以 `u=-1` 隐藏一侧、按 Angle 旋转，并经 `lilIsIn0to1` 在 0-1 外淡出；`lilCalcAtlasAnimationAtAnimTime`(:533) 按 `decalAnimation.xy` 网格偏移。
   - **关键**：lilToon 默认值 `DecalAnimation=(1,1,1,30)`、`DecalSubParam=(1,1,0,1)` 会退化为恒等变换，**必须只在真实网格/缩放时报警**，否则所有材质都会被误判为不可优化。已用测试锁定该行为。
6. `MaterialRemapper.BuildMaterialKey` 修正：排序 keywords 前先复制，不再原地修改引擎返回的数组。

## 注意事项

- `_verify/` 是编译验证工程与数值测试，**非交付物**，不在 git 仓库内。
- stub 工程不等于 Unity，`Resources.LoadAll`、`Shader.Find`、`EditorUtility.CompressTexture` 等在 stub 中均为空实现，其真实行为必须在 Unity 中验证。
- 验证命令：
  - 编译：`cd /home/user/_verify && PATH=/home/user/.dotnet:$PATH dotnet build -v q --nologo`
  - 数值：`cd /home/user/_verify/mathtest && PATH=/home/user/.dotnet:$PATH dotnet run -v q --nologo`
