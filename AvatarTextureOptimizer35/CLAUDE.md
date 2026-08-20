# CLAUDE.md — AvatarTextureOptimizer 项目记忆

> 本文件是项目的唯一记忆中枢。每次修改后必须更新并 git 提交。
> 约定：所有日志以 `[ATO]` 开头；代码注释中英双语；i18n 中英双语。

## 项目基本信息

- 项目名：AvatarTextureOptimizer（ATO）
- 包名：`net.fosa.avatar-texture-optimizer`
- 目标：全世界最好的 VRChat 贴图优化工具 —— 开源 NDMF 工具：分析 Avatar 网格，为满足条件的材质建立 UV→贴图映射，按目标质量算法缩放 UV 岛，剔除未使用区域，重组生成图集，在保证质量的同时最大化贴图利用率。
- 不是完整 Unity 工程：用户手动同步到 Unity 工程验证。交付以 zip 打包。
- 交付规则：一次性完成全部功能，不交付半成品。

## AgentTeam 工作流（必须遵守）

1. **Coder ×3**：每次写代码前互相交流 → 共识写入 `docs/architecture.md` → 再落实代码。
2. **Reviewer ×3**：Coder 每批代码完成后共同审查 → 共识后决定是否打回。
3. **QA ×3**：项目整体完成、Reviewer 验收后，三个 QA 各自独立从头完整阅读全部代码 → 全部认为合格才交付。
4. 每个阶段结论、进度记录到本文件并 git 提交（防上下文丢失）。

## 环境与取证结果（2026-08-20，已核实源码，禁止凭猜测）

- 沙箱：无 dotnet SDK。已下载全部 9 个依赖 zip 到 `/home/user/vendor/x/`（NDMF 1.14.4、MA 1.18.2、AAO 1.9.17、lilToon 2.3.4、VRC SDK 3.10.4、avatar-compressor 0.9.0、LLC 2.13.0 均带源码；VRC SDK 为二进制 DLL，用 dnfile 解析过元数据）。
- vpm shader util zip（lilxyzw vpm-packages）404，不影响开发（liltoon 需要 lilUtils，由用户工程 VPM 安装）。

### 关键 API 取证（真实源码/元数据）

- NDMF 插件：`[assembly: ExportsPlugin(typeof(T))]`；`Plugin<T>` 覆写 `QualifiedName`；`InPhase(BuildPhase.Optimizing)`；`Sequence.BeforePlugin/AfterPlugin(string)`（WeakOrder 约束，目标插件缺失安全，会创建幽灵 innate phase）。`Pass<T>.Execute(BuildContext)`。
- NDMF 相位顺序：FirstChance → PlatformInit → Resolving → Generating → Transforming → Optimizing → PlatformFinish。
- NDMF 编辑器程序集真名 `nadena.dev.ndmf`（运行时 `nadena.dev.ndmf.runtime`）。
- BuildContext：`AvatarRootObject/AvatarRootTransform/ObjectRegistry/ErrorReport/AssetContainer/IsTemporaryAsset/GetState<T>/Successful`；构建结束自动对临时资产 Mesh 调 `RecalculateUVDistributionMetrics`。
- ObjectRegistry：`GetReference(obj)`、`RegisterReplacedObject(old,new)`（须在 GetReference(new) 之前调用）。**NDMF 不会自动重映射动画曲线中的对象引用**，动画曲线重映射自实现（参照 AAO ObjectMapping 机制）。
- AAO QualifiedName = `"com.anatawa12.avatar-optimizer"`；MA = `"nadena.dev.modular-avatar"`；TTT = `"net.rs64.tex-trans-tool"`。
- `UVUsageCompabilityAPI`（`Anatawa12.AvatarOptimizer.API`，程序集 `com.anatawa12.avatar-optimizer.api.editor`）：
  - `IsTexCoordUsed(SkinnedMeshRenderer, int channel)` 0..7；仅 SMR。
  - `RegisterTexCoordEvacuation(renderer, originalChannel, savedChannel)`；savedChannel 被 AAO 使用会抛 InvalidOperationException。
  - 疏散契约（已读 AAO `EvacuateProcessors.cs`）：我方把原始 UV 拷到 saved 通道（写入新网格）→ 注册 → AAO 的 EvacuateProcessor 交换两通道 → AAO 处理用原始 UV → RevertEvacuateProcessor 最后写回新 UV 并删除 saved 通道。RemoveMeshByMask 用 UV0 + 组件自带 mask 贴图；RemoveMeshByUVTile 只用 UV。
  - **引用方式：反射隔离集成**（`AtoAaoIntegration`）。取证结论：asmdef `versionDefines` 只加宏、不加程序集引用（MA 对必装 SDK 也是硬引用），可选依赖必须用反射。
- VRC SDK（VRCSDK3A.dll 元数据）：`VRCAvatarDescriptor.baseAnimationLayers/specialAnimationLayers`（嵌套 public 类型 `CustomAnimLayer{type, animatorController, mask, isDefault, eyeMovement}`），`AnimLayerType{Base=0..FX=5..IKPose=8}`。**反射隔离集成**（`AtoVrcSdkIntegration`，程序集名 `VRCSDK3A`）。
- lilToon 2.3.4 属性表（Shader/lts.shader 实读）：`_MainTex`[MainTexture]、`_BumpMap`[Normal]、`_Main2ndTex`、`_Main3rdTex`、`_AlphaMask`、`_Bump2ndMap`[Normal]、`_AnisotropyTangentMap`[Normal]（切线数据，绝不重算/不旋转）、`_EmissionMap`、`_MatCapTex`、`_OutlineTex`、`_ParallaxMap`（视作不安全）、`_Ramp`（排除）、mask 系列。运行时用 `ShaderUtil.GetPropertyType` + 反射 `GetPropertyAttributes`（[Normal]/[NoScaleOffset]/[MainTexture]）+ material keywords 动态分析。
- NDMF 报告：`ErrorReport.ReportError(IError)`；`IError` 需 `CreateVisualElement`（UIElements Foldout 折叠细节）；`ErrorSeverity.Information/Warning/Error`。
- NDMF 语言：`nadena.dev.ndmf.ui.LanguagePrefs.Language`（Auto 模式）；`LanguagePrefs.RegisterLanguage(code)`；Localizer 惯用法已照 MA 实现。

## 总体架构（Coder 共识 v1，详见 docs/architecture.md；勿违背）

1. 双 asmdef：Runtime（组件/设置）+ Editor（全部逻辑，refs: nadena.dev.ndmf, Unity.Burst/Collections/Jobs/Mathematics）。
2. 组件：`AtoAvatarRoot`（Avatar 根，含全部设置）。规则：一个 Avatar 及其子级只允许 1 个；挂载对象必须有 `VRCAvatarDescriptor`（构建期校验，违规报错中止）。
3. 流水线（单 pass，Optimizing，AfterPlugin(MA).AfterPlugin(TTT).BeforePlugin(AAO)，可取消）：
   Scan → Animations → DedupeTextures → Islands → Quality → Packing → Compose → Meshes → DedupeAssets → References → Import → RemoveSelf → Report。
4. **核心不变式（Coder 共识）**：每个岛只有一个（质量缩放后的）UV 矩形，在所有包含它的图集中完全一致。类型组节省 = 为该贴图选择更小的图集分辨率（S_t ≥ T_t×s_i^t/s_i），而非逐岛独立缩放。
5. 装箱：UV 组共享放置注册表（`ctx.PlacedIslands`）——第一个装箱某岛的类型组固定其原点+旋转，后续组复用；主色组优先。队列=类型组内贴图（面积降序）；候选池 POT/NPOT（64 步进，移动端 4096）；4px 位掩码 BLF 全扫描；90° 旋转（切线组禁用）；交换伙伴（共享岛的贴图）不得共图集；padding = max(ceil(候选最大边长/128), 用户最小值) px。
6. 旋转一致性（三处同一映射）：装箱掩码转置 / 合成写入 WriteRotated / 网格 UV Rotate()：r∈{(a,b), (b,a), (W−a,H−b), (H−b,W−a)}。内容与 UV 成对旋转，采样外观不变；切线组禁用旋转。
7. 质量评估（Burst，线性空间，预乘 alpha）：MS-SSIM（短边<176px 单尺度 SSIM；<11px 跳过）+ΔE00+alpha（Cutout IoU 逐材质逐阈值 / Blend 线性 RMSE）+法线（解码→重采样→重归一化→编码，角度误差均值+p95）+灰度（使用通道 RMSE 取最差）；回放大比较；便宜指标先行快速失败；扩展指标 provider 最后。
8. 像素密度带 [Dmin,Dmax] px/m（默认 2048/4096，挡位 512..8192）：Dmin 硬下钳（防糊），Dmax 仅告警（质量优先）——**解读选择，已向用户说明**。目标质量=1（自定义全 1）跳过缩放原样拷贝。纯色岛 min(4,短边)。
9. 类型组 key = 类型签名（整个 UV 组的 kinds 排序去重）× sRGB × filterMode；签名相同才能共享图集（9/10 浪费问题的解决）。
10. 白名单：不限类型；引用贴图跳过一切优化；同 UV 其他贴图跳过图集化、参与整图缩放与导入参数；不安全用法视作白名单+warning。**直接动画目标材质不能克隆 → 其贴图白名单**；**只读剪辑（用户资产未克隆）→ 其引用白名单**。
11. 网格重写：克隆网格（绝不改原资产）→ 重写 UV（平移→缩放→旋转→放置）→ AAO 疏散 → 换网格。顶点/骨骼/权重/形态键不动。
12. 材质：绝不改贴图以外的参数；需要换贴图引用时克隆材质（NDMF 自动保存非持久资产）；直接动画目标材质不克隆。
13. 导入参数：图集强制 Clamp + Read/Write 关（不可改）；Mipmap+MipStreaming 单开关；压缩按 透明/不透明/法线/灰度 × 平台 override（安全枚举：透明必带 alpha、法线≥2通道、灰度单通道需像素兜底、NPOT 剔 PVRTC）；sRGB 按组键/原贴图。
14. 整图缩放（无图集模式/白名单组/放弃图集化）：s_tex = 该贴图全部岛的 s_i^t 逐轴取最小；分带双通道预乘重采样（内存友好）。
15. 动画曲线重映射：对象引用曲线值 Resolve；槽合并后 `m_Materials.Array.data[i]` 索引重映射（旧绑定曲线先移除）；只读剪辑跳过。
16. 材质槽合并：同网格不透明相同材质、无独立动画（材质切换/属性动画）→ 子网格并入保留槽 + 槽索引映射。
17. 取消：阶段间检查 + DisplayCancelableProgressBar；取消抛 OperationCanceledException → 保留磁盘资产、释放 CPU/GPU/内存（AtoRuntimeCache.ReleaseAll）。
18. 扩展 API：`AtoTextureUsageProvider`、`AtoQualityMetricProvider`（自动发现+手动注册）。i18n：JSON（en/zh-cn），用户可加，Auto 读 NDMF 语言，回退英文。

## 质量预设（docs/quality.md 详述，含文献依据）

| 挡位 | MS-SSIM | ΔE00均值 | Cutout IoU | Blend αRMSE | 法线 均值/p95 | 灰度 RMSE |
|---|---|---|---|---|---|---|
| 极高 | 0.9995 | 0.5 | 0.9999 | 0.002 | 0.15°/0.5° | 0.002 |
| 高（默认） | 0.999 | 1.0 | 0.9995 | 0.005 | 0.25°/1.0° | 0.005 |
| 中 | 0.997 | 2.0 | 0.998 | 0.01 | 0.5°/2.0° | 0.01 |
| 低 | 0.995 | 3.0 | 0.995 | 0.02 | 1.0°/4.0° | 0.02 |
| 自定义 | 全 1（近无损） | | | | | |

依据：SSIM/MS-SSIM（Wang et al. 2003）、CIEDE2000（Sharma et al. 2005）、JND ΔE<1（Mokrzycki & Tatol 2011）、3Dc/BC5 法线压缩误差研究、8bit alpha 量化 JND。

## 当前进度（2026-08-20 全部完成）

- [x] 环境搭建、依赖下载、第三方源码/元数据取证
- [x] 可行性分析：总体可行；两个设计修正（UV组级装箱解决跨图集同位置冲突；动画曲线重映射自实现）
- [x] CLAUDE.md、docs/architecture.md、docs/quality.md
- [x] 全部 12 个阶段实现（扫描/动画/去重/岛/质量/装箱/合成/网格/去重合并/引用/导入/自移除）
- [x] Burst 质量核（线性转换/预乘重采样/高斯/SSIM/ΔE00/IoU/RMSE/法线角度/跳跃洪泛外扩）
- [x] 装箱器（候选池/BLF/旋转/padding/固定岛边界检查）
- [x] 反射集成（AAO UVUsageCompabilityAPI、VRC SDK 描述符）
- [x] 扩展 API、Inspector UI、i18n（en/zh-cn + 生成器 + 键同步校验）
- [x] README.md（用户+开发者）
- [x] **QA×3 独立全量审查完成**：
  - QA-1（编译级）：缺失 using、Inspector 重复绘制 → 已修。
  - QA-2（逻辑级）：固定岛越界静默钳制（装箱）、旧动画曲线未移除（重绑定）、白名单组越界岛平移（评估裁剪）、多 tile 岛保守不缩放、原网格资产绝不原地修改 → 已修。
  - QA-3（需求追踪）：用户全部需求逐条核验通过（见下）；阶段名 i18n 接线 → 已修。
- [x] git 提交链完整（17+ commits）

### QA-3 需求追踪结论（全绿）

50+ 条需求全部实现。已记录的解读/限制（均已向用户或文档说明）：
1. 像素密度带语义：Dmin 硬下钳、Dmax 告警（质量优先）。
2. 装箱粒度修正为 UV 组级（原"类型组队列"与"跨图集同位置"约束冲突）。
3. 图集 padding 在 4px 粒度下为 ≥4px 的近似（安全方向）。
4. 指标核 CPU Burst；GPU 用于贴图读回与合成（性能意图达成）。
5. 骨骼动画形变不参与面积计算（绑定姿态 + 形态键 0/100 帧）。
6. 近无损图集在源贴图非候选尺寸时做双线性上采样（视觉等同，非严格逐像素拷贝）。
7. 共享 UV 布局中后续图集沿用首组 padding 布局（质量阈值兜底）。

## 注意事项（持续有效）

- Unity 版本目标 2022.3（VCC 支持）；无 dotnet SDK，沙箱不能编译 C#，只能人工静态审查 → 每次修改后必须重读变更文件。
- 用户在 Unity 中验证；每次修改后必须保证可完整烘焙。
- `ShaderUtil.GetPropertyAttributes` 走反射探测（ShaderUtil 与 Shader 双候选），缺失时降级为名称分类（安全）。
- 打包进 zip 前删除无关文件；zip 根目录即包目录（见交付脚本 tools/make_zip.sh）。
- i18n 键维护：改 tools/gen_i18n.py 后重跑 `python3 tools/gen_i18n.py`；键同步用 QA 脚本校验。
- 日志开关：AtoLog.Level（Summary/Normal/Verbose）；全部阶段输出耗时；报告折叠细节。
