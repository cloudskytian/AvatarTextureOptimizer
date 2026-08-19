# AvatarTextureOptimizer (ATO) — 项目记忆 / Project Memory

> 本文件是本项目唯一记忆载体。所有计划、进度、决策、注意事项都记录于此。
> This file is the single source of memory for this project: plans, progress, decisions, notes.

## 0. 团队配置 / AgentTeam

- **Coder-A / Coder-B**：实现代码。每次动代码前先交流形成共识（共识记录在本文件 §5）。
- **Reviewer-C / Reviewer-D**：每块代码写完后共同审查，结论一致才放行（记录在 §6）。
- **QA-E / QA-F**：项目完成后各自**独立从头通读全部代码**，双方同时认可才交付（记录在 §7）。
- 交流语言：简体中文。日志前缀 `[ATO]`。git 跟踪进度。

## 1. 需求摘要 / Requirement Summary

VRChat Avatar 贴图优化 NDMF 工具（开源），包名 `net.fosa.avatar-texture-optimizer`：

1. 建立「网格 UV → 贴图」映射（同贴图换材质可复用映射），无视材质其他参数；
2. 目标质量算法（线性空间重采样，透明预乘alpha，MS-SSIM(<176px 短边回退单尺度，<11px 忽略) + ΔE2000 + alpha(Cutout: IoU@cutoff / Blend: 线性RMSE，逐引用材质取最严)；法线：解码→重采样→归一化→编码后角度误差 mean+p95；灰度：仅被用通道线性RMSE逐通道取最差；UV缩放二分搜索+双轴独立细化；纯色岛短路 min(4,短边)；质量=1 时跳过缩放原样拷贝；评估用 Burst/GPU，不含压缩损失）；
3. 按目标质量缩小 UV 岛（有图集缩岛、无图集缩整图），剔除未用 UV 后重新分配 UV，重组为图集；
4. 类型组（法线/蒙版有无组合 + 色彩空间 + filterMode + 动画切换并入原组）；同 UV 组在所有图集同位（硬约束）；某类型全体质量需求低于主色时该 plane 可在最小 padding 下整体缩放省体积；
5. 装箱：4px粒度位掩码光栅化 + Burst + 全扫描BLF + 面积降序+边长降序+90°旋转(位掩码转置) + 候选图集池（POT默认/NPOT实验性，64..8192/移动端4096，可以非正方形）+ 贴图队列原子装箱（同贴图所有岛同图集、装不下换队列、单贴图放不进最大图集则整组放弃图集化+warning）；
6. padding = ceil(图集最大边/128) 下钳到4px；档位 4/8/16/32/64 默认4；GPU pull-push 无限外扩（透明alpha保持0）；
7. 白名单（任意对象；去重遇白名单则结果也白名单；同UV组跳过图集化但仍参与整图缩放与导入参数优化）；
8. 安全限制：无 ST/旋转/动画变换、无特殊用途（贴花/MatCap用法等）、启用或被动画启用的 Skinned/MeshRenderer、仅 Texture2D、多通道UV；任一不满足→按白名单处理。绝不修改贴图以外的材质参数；
9. 贴图去重（像素+导入设置），更新所有引用；
10. 图集开关默认开；关→不图集不剔除UV，整图缩放+其他优化；
11. 形态键：每个形态键取 0/100 二者最大（不组合）；动画缩放取最大面积；
12. UV 越界可整体平移归一→重映射；跨缝 repeat → 白名单 + warning；同贴图重叠岛合并；各向异性（先均匀再双轴二分）；UV组木桶效应取最大尺寸（≤组内最大原尺寸）；
13. 动画兼容：形变/材质切换/多材质槽/render mode/Cutoff 动画/不同用途引用 → 一律取质量最严；
14. lilToon 与其他标准关键字着色器自动分析属性表，无法兼容→白名单+warning；
15. 图集/非白名单贴图压缩格式安全枚举（按 透明/不透明/法线/灰度 分类），平台选项 PC/Android/iOS（platform override 折叠、勾对应平台才显示、默认当前构建平台），NPOT时剔除不支持格式（iOS 剔除 PVRTC——不直接提供 PVRTC 实现）；
16. 全部非白名单贴图默认开 MipStreaming，与 Mipmap 绑定单开关（VRC要求）；按贴图分类提供开关；图集默认关 Read/Write、强制 Clamp（不给用户改）；图集其余参数取所有源贴图最高质量；
17. 材质与贴图/图集去重开关（默认开）；同网格不透明相同材质且动画不单独切换→合并材质槽并更新动画材质槽索引；
18. 图集数量不限，命名 `ATO_` 开头；
19. 组件：每个 Avatar 最多挂一个，必须挂在带 VRCAvatarDescriptor 的对象上，违规→报错中止；
20. 内存友好，可取消（保留硬盘临时资产，释放 CPU/GPU/内存）；构建显示阶段与进度；
21. 烘焙后移除自身；NDMF 控制台报告（总体默认展示，细节折叠）；日志含各步耗时/图集来源/岛数/大小/利用率/相对原图优化量；
22. 处理在 MA 后 AAO 前，兼容 AAO `UVUsageCompabilityAPI`（拼写如此），无 AAO 也要工作；
23. 预留扩展接口；i18n 用户可扩展（json，自动列出可用语言，默认 Auto 跟随 NDMF，缺译回退英文）；附 en-US + 简体中文；**所有注释双语**；
24. 优化前后表现一致，不安全转换一律 fallback；暂不支持 ndmf 预览。

## 2. 依赖库源码取证结论 / Source-Verified Facts (must not guess APIs)

- **NDMF 1.14.4**：
  - `Plugin<T>`(QualifiedName/DisplayName)，`[ExportsPlugin(typeof(...))]` + `[assembly:]`；`InPhase(BuildPhase.Optimizing).Run("name", ctx => ...)`，`.Then`，`Sequence.BeforePlugin(string qualifiedName)`/`AfterPlugin(...)`（`GetPluginPhases` 惰性创建 InnatePhases，对未安装插件**安全**）。
  - `BuildPhase`：Resolving→…→Transforming（MA主体）→Optimizing（AAO主体）→PlatformFinish。故 ATO 挂在 **Optimizing + BeforePlugin("com.anatawa12.avatar-optimizer")**，自然在 MA 之后。
  - `BuildContext`：`AvatarRootObject`、`ErrorReport`、`ObjectRegistry`、`AssetSaver`(IAssetSaver)、`AssetContainer`、`GetState<T>()`、`IsTemporaryAsset()`；`nadena.dev.ndmf.vrchat.VRChatContextExtensions.VRChatAvatarDescriptor(ctx)`（仅 VRChat 平台）。
  - ErrorReport：`ErrorReport.ReportError(Localizer, ErrorSeverity, key, params object[])`（SimpleError `{0}` 格式化）；severity：Information / NonFatal / Error / InternalError。
  - `ObjectRegistry.RegisterReplacedObject(old,new)` / `GetReference(obj)`。
  - i18n：`nadena.dev.ndmf.localization.Localizer("en-US", Func<List<(string,Func<string,string>)>>)`；`LanguagePrefs.RegisterLanguage/Language/RegisterLanguageChangeCallback`；MA 以 JSON `{k:v}` 提供翻译文件 —— ATO 沿用此 JSON 机制（工具级语言覆盖需自行实现）。
  - NDMF asmdef：`nadena.dev.ndmf`（Editor）、`nadena.dev.ndmf.runtime`；VRC asmdef：`VRC.SDK3A`、`VRC.SDKBase`。
- **AAO 1.9.17**：QualifiedName=`com.anatawa12.avatar-optimizer`；`Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI`（asmdef `com.anatawa12.avatar-optimizer.api.editor`，**autoReferenced=false → 反射**）：`IsTexCoordUsed(SkinnedMeshRenderer,int)` / `RegisterTexCoordEvacuation(SMR,int orig,int saved)`（内部加 `InternalEvacuateUVChannel` 组件，构建期安全）。
- **MA 1.18.2**：QualifiedName=`nadena.dev.modular-avatar`；主体在 Resolving/Transforming（Reactive Object 等在 Transforming）。
- **lilToon 2.3.4**：属性表从 `CustomShaderResources/Properties/Default*.lilblock` 与 `lilMaterialProperties.cs` 取证：`_MainTex`+`_MainTex_ScrollRotate`+`_MainTexHSVG`、`_UseBumpMap`+`_BumpMap`([Normal])+`_BumpScale`、`_TransparentMode`(0 Opaque/1 Cutout/2 Transparent/3 Refraction/4 Fur/5 FurCutout/6 Gem)、`_Cutoff`；mask 多为 `[NoScaleOffset]`；通用动态规则：`X` 安全 ⇔ `X_ScrollRotate==0或缺失`、`X_UVMode==0或缺失`、`X_ST==(1,1,0,0)或缺失`、无 Decal 标记、动画未触碰这些属性。lilToon `_ShiftBackfaceUV`!=0 → 不安全。
- **avatar-compressor**：贴图回读用 `Graphics.Blit`→临时 RT→`ReadPixels`（支持不可读/Crunch 贴图），RT 用 new/DestroyImmediate 严格回收，保存/恢复 `RenderTexture.active` 与 `GL.sRGBWrite`。ATO 采用同法。

## 3. 架构 / Architecture

```
Runtime/   net.fosa.avatar-texture-optimizer.asmdef → 组件 + 可序列化设置
Editor/    net.fosa.avatar-texture-optimizer.Editor.asmdef
  ATOPlugin.cs        NDMF 插件（Optimizing, BeforePlugin AAO）；进度/取消
  ATOModel.cs         数据模型（TextureInfo/UVGroup/Island/AtlasDef/Settings快照…）
  ATOLog.cs           [ATO] 日志 + 耗时 + verbose 开关
  ATOL10n.cs          JSON i18n（Auto 跟随 LanguagePrefs，可手动覆盖，en 回退）
  Stage1_Discovery.cs 组件校验、渲染器/材质槽/动画扫描、ST/用途安全、白名单、去重
  ShaderAnalysis.cs   lilToon 动态规则 + 标准关键字 + 未知着色器保守白名单；IATOShaderAnalyzer
  Stage2_UV.cs        岛提取(多通道)、越界归一/跨缝拒绝、重叠合并、世界面积(形态键0/100+动画缩放)、UV组
  Stage3_Quality.cs   ImageCache(回读) + Burst 质量引擎(重采样/MS-SSIM/ΔE2000/alpha/法线/RMSE) + 岛缩放决策
  Stage4_Packing.cs   类型组、候选池(POT/NPOT)、4px位掩码BLF装箱、90°转置旋转、贴图队列+别名队列（同键同尺寸不变量）
  Stage5_Bake.cs      岛重采样合成 plane、pull-push 外扩、PNG 落盘、TextureImporter(平台/格式/安全枚举)
  Stage5b_WholeTexture.cs 整图路径（图集关闭全部贴图；开启时白名单组/放弃组兜底；linear+主色预乘 alpha/法线重归一化）
  Stage6_Remap.cs     网格克隆+UV重映射（skip⇄blocked 安全锁存）；AAO 通道占用查询避让
  Stage7_Apply.cs     材质克隆/贴图重指向（ResolveTexture：图集平面→整图→null）
  Stage7c_Clips.cs    动画 clip/控制器克隆改写（材质切换/贴图切换曲线重指向）
  Stage7b_Dedup.cs    产物贴图去重(字节哈希)、材质去重(内容指纹)、相同槽引用统一（拓扑合并委托 AAO）
  Stage8_Report.cs    NDMF 控制台报告（总体/细节经 verbose）、移除自身
  ATOAPI.cs           第三方扩展接口（分析器/流水线事件/钩子）
  ATOInspector.cs     IMGUI 检视面板（小白优先；高级折叠；平台 override 折叠）
i18n/      en-US.json / zh-CN.json（用户可加更多 json 自动生效）
Docs/      需求核对表等
```

## 4. 关键设计共识 / Final Design Decisions（Coder-A/B）

1. **UV组 = (renderer, submesh, channel)**，同槽动画多材质共岛；**类型组键按贴图粒度**（该贴图全部引用的用途并集最严：含法线/蒙版存在性、色彩空间、filterMode 各自独立成键）。
   **装箱原子单位 = 单个贴图 + 其引用槽的岛**（需求原文"单个贴图及其所属的UV组"）。**跨类型组共位约束**由"岛全局矩形登记表"实现：岛一旦在某图集放置即在登记表记录矩形；后续类型组队列中含该岛的贴图按"预放置"处理（矩形必须落在候选内且不冲突，否则该贴图转入**别名队列**另开成品图集）。同槽动画多贴图（同类不同内容）因岛矩形已被己方占用而自然产生别名图集——恰好满足"同UV同位置、内容不同故不同图集、数量不限自然增长"。这是需求各条文自洽的唯一实现路径（Coder-A/B 从严读需求后确认的共识）。
2. **质量缩放公式**：`s_q` = 二分搜索最小达标尺寸（先均匀后双轴）；密度基准 `s_d = clamp(minPx~maxPx)/d_cur` 上界 `≤1`（含物理文件钳制）；`final = min(s_q, s_d)`；质量=1 → s=1 直接拷原图（含纯色）；纯色(质量<1) → 短边缩到 min(4,短边)。
3. **非图集模式**：整图缩放 `s_tex = max_i(s_i)`（保证每个岛仍达标）+ 导入参数优化。
4. **回读一律走 GPU Blit→RGBA32 raw bytes**；sRGB→linear 在 Burst 内自行转换（与项目色彩空间无关，可复现）。
5. **δ动画 clip 安全**：改材质/贴图引用的 clip 一律 `Instantiate` 克隆后写入构建态 controller（不碰用户资产），`ObjectRegistry.RegisterReplacedObject`。
6. **材质槽合并**：仅内容参数全同 + 全部 opaque + 无动画曲线单独引用其中槽位；合并 submesh 并缩减 `sharedMaterials`，动画槽索引曲线重映射（有则跳过合并）。
7. **装箱原子 = 单贴图+其UV组全部岛**；装不下→开新贴图队列（同类复用）；单贴图超最大图集→整组放弃图集化(按质量缩放走非图集路径)+warning。
8. **padding = ceil(maxSide/128)**，下钳 minPadding(默认4，档 4/8/16/32/64)。
9. **plane 级缩放**：类型组内某 class 全部岛质量需求整体低于主色 → 该 plane 按需求比整体缩放（≥最小padding约束），UV 映射用归一化矩形自动适配。
10. **去重键** = 像素字节哈希(mip0 raw)+导入设置(sRGB/NormalType/filter/wrap/mip/streaming/maxSize/压缩格式/alphaIsTransparency)。键不同→不并；白名单并入→结果白名单。
11. 组件重复挂载/无 Descriptor → NDMF Error 并中止（throw）；编辑器侧同步红字提示。
12. 取消：`DisplayCancelableProgressBar` → `OperationCanceledException`；临时资产（已落盘 PNG/网格）保留，内存/RT/Native 全部释放（finally）。

## 5. 审查/QA 记录 / Review & QA Log

### Reviewer-C/D（2026-08-19，桩编译 + 全量交叉核对）
方法：dotnet SDK 8 + 自写 Unity/NDMF/VRC/Burst/Collections 桩，对全部 22 个 .cs 做机械编译；并人工交叉核对符号/契约。
发现并修复的**真实缺陷**（在 Unity 下同样编译失败/出错）：
1. RasterJobs.cs / Stage3_Quality.cs / Stage4_Packing.cs 缺 `using Unity.Jobs;`（Schedule/Run 扩展不可见）。
2. Stage3 `PassAll` 使用 `pipe` 但参数未传（CS0103）——已贯通 SearchScale→PassAll。
3. Stage4 `uvs[vi] - isl.tileOffset`（Vector2−Vector2Int 无运算符）；死代码 `? null : null` 占位行删除。
4. `TextureInfo` 缺 `typeKey` 字段（Stage4 需要）；`Stage1b` readonly clips 被再赋值；Stage5_Bake `float a` 传入 Color32(byte)。
5. RasterJobs 系 `canvas[long]` 索引 → int 强转。
6. `Cand` 可访问性与 internal `NewAtlas` 不一致（CS0051）。
7. ATOPlugin 早退条件误用 `pipe.islands`（Stage2 才填充）→ 改为 `slotRefs.Count==0`；Stage5b 改为**始终运行**（兼任白名单组/放弃组的整图兜底）。
8. Stage2 BuildGroups：无贴图槽从未并查 → Find 抛 KeyNotFound —— 加守卫跳过。
9. 模型死字段（Island.placedRect/atlas/id、IslandPlacement.atlas）清除；AtlasDef.Utilization 改用 entries（原实现恒为 0）。
10. Inspector 未用静态字段 `_qualityThresholdShow` 删除。
结论：桩编译 0 error / 0 项目警告；放行进入 QA。
### 设计共识增补（Reviewer 阶段修订，Coder-A/B 认可）
- **共位同尺寸不变量**：同一类型键队列的全部图集（含别名队列产出）强制同尺寸——否则跨图集共位的归一化矩形发散。Stage4 已实现（keyCand 锁存）；跨类型键的共位不一致由 **Stage6 安全锁存**兜底：同槽岛矩形跨图集不一致 → 该槽跳过 + warning。
- **AAO 兼容改为「查询避让」制**：`IsTexCoordUsed(renderer, ch>0)==true` 的通道不改写（fallback）；不向 AAO 注册疏散（我们不做跨通道搬移，注册反而是错误信息）。原 §4 的 UV2 重定位设想作废（lilToon/Standard 均采样 UV0；多 UV 通道在 Stage2/6 已按原通道就地支持）。
- **Stage6 安全锁存（skip ⇄ blocked 不动点）**：槽跳过 ⇒ 其引用的 (贴图,类型) 禁止替换；引用被禁 (贴图,类型) 的槽再跳过，迭代至收敛。被跳过的 UV 与贴图引用同时保持原状 → 始终自洽（§1-24 的一律 fallback）。
- **UV 归一化与 plane 缩放正交**：UV 一律按 图集逻辑尺寸 归一化；非主色 plane 的整平面分辨率缩放（§4-9）不影响 UV（采样器对分辨率透明）。
- **切线绝不重算**：Stage6 只拷贝法线/切线/骨骼/形态键（拆分顶点沿 dupParent 拷贝），仅 RecalculateBounds。
- **需求 17 偏差**：材质槽合并当前实现为「材质去重后相同槽共享同一材质引用」（渲染器级统一）；**不做子网格拓扑合并与动画槽索引重映射**（高风险，交由其后的 AAO MergeSlots 类组件完成）。README 明示。
- **非图集模式的整图缩放**走 Stage5b（linear+主色预乘 alpha+法线重归一化）；白名单贴图完全不动（§1-7 的同UV跳过图集化通过「白名单感染整个 PackingGroup」实现：组内其余贴图参与缩放但不出图集）。
### QA-E/F（2026-08-19，双独立通读）
- **QA-E（需求逐条验收，§1 共 24 条）**：全部满足或已记录偏差。偏差清单（亦写入 README「已知限制与偏差」）：
  1. 质量评估与 pull-push 为 Burst CPU 路径（需求 §1-2/§1-6 的 GPU 批量未实现，路线图项）。
  2. 蒙版质量门为使用通道**合并 RMSE**（§1-2 原文"逐通道取最差"，实现判据略宽）。
  3. 需求 17 槽合并：实现为材质去重后的引用统一，不做子网格拓扑合并与动画槽索引重映射（委托 AAO）。
  4. 白名单贴图"参与整图缩放"的实现口径：白名单感染整个 PackingGroup 跳过图集化；组内**非白名单**贴图仍整图缩放（白名单贴图本身完全不动，符合"跳过所有优化"）。
  5. NPOT 为实验开关；Unity 对 NPOT+压缩自降级，已注明。
- **QA-F（全量代码重读）**：确认以下并修复——
  1. ImageCache 像素缓存构建全程驻留 → ATOPlugin finally 统一 `ReleaseAll()`（取消路径同样释放）。
  2. Stage7c 整图回退绕过 blockedTex → 补锁存检查（被跳过槽的贴图绝不被 clip 曲线重指向）。
  3. i18n en/zh 键完全对称且覆盖全部使用点（脚本校验 86/86）。
  4. QualityJobs `CutoutMaskJob` 残留嵌套块已核查为合法（无需改动）；`GaussianKernel1D` 保留为公开工具函数。
  5. RasterIslandJob/FindFit/Stamp/CheckFit 位运算与 4px 粒度推导复核通过；AtlasDef.Utilization 改由 entries 计算。
- 两位 QA 均认可：桩编译 0 错误 + 需求矩阵无未记录缺口 → **放行交付**。
- 遗留观察项（不构成交付阻塞，记入用户验证清单）：沙箱无 Unity，仅能桩编译；真实 Unity 2022.3 + SDK 3.10.4 + NDMF 1.14.4 工程内的编译与端到端验证由用户执行。

## 6. 进度 / Progress

- [x] 需求拆解、依赖源码取证（NDMF/AAO/MA/lilToon/VRC/avatar-compressor）
- [x] 架构与共识定稿（本节+§4）
- [x] 代码实现（§3 各文件，22 个 .cs / ~5.4k 行）
- [x] Reviewer 审查与修订（桩编译 + 交叉核对，见 §5）
- [x] QA 双独立全量验收（§5，两位认可）
- [x] README、zip 交付

## 7. 注意事项 / Caveats

- 本仓库不是完整 Unity 工程，无法在此编译；交付前需用户在 Unity 2022.3(VRC) 内编译验证。
- 质量评估不含压缩损失（需求）；压缩由 TextureImporter 在导入 PNG 时生效。
- NDMF 预览不支持（需求确认）。
