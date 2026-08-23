# CLAUDE.md — AvatarTextureOptimizer (ATO) 项目记忆

包名: `net.fosa.avatar-texture-optimizer` · 目标: 全球最好的 VRChat Avatar 贴图优化工具 (NDMF 插件)
交付形式: Unity Package (VPM 兼容, package.json), 用户手动拷入 Unity 工程验证。**这不是一个完整 Unity 工程。**

## 0. 角色 (AgentTeam 运作方式)
- 3×Coder: 每个模块写码前先"三方共识"(设计要点记录于 docs/TeamLog.md 的 Coder 共识条目), 再落码。
- 3×Reviewer: 每个模块提交后全量审查, 共识结论决定是否打回; 打回项修复后才能继续。
- 3×QA: 项目全部完成 + Reviewer 验收后, 3 个 QA **各自独立从头通读全部代码**(在 TeamLog 记录三份独立报告), 全部通过才允许交付。
- 修改/排查前必须先读代码与第三方源码, 先取证再下结论。

## 1. 已读通的第三方库 (源码已逐个取证, 位于 /home/user/refs, 不入 git/zip)
| 库 | 版本 | 关键取证结论 |
|---|---|---|
| nadena.dev.ndmf | 1.14.4 | `Plugin<T>`: `InPhase(BuildPhase.X).Run(pass).BeforePlugin(qn)`; Pass: `Pass<T>` + `Execute(BuildContext)`; `BuildContext`: `AvatarRootObject/AssetSaver(IAssetSaver.SaveAsset/IsTemporaryAsset)/GetState<T>/Extension<T>()/PlatformProvider`; `ErrorReport.ReportError(Localizer, ErrorSeverity, key, args)` (public); `Localizer(defaultLang, Func<List<(string,Func<string,string>)>>)`; `LanguagePrefs.Language`; 动画服务: `WithRequiredExtension(typeof(AnimatorServicesContext), seq=>…)` 内 `Extension<AnimatorServicesContext>()` → `.AnimationIndex`(全动画 VirtualClip, `RewriteObjectCurves(Func<EditorCurveBinding,Object,Object>)`, `GetPPtrReferencedObjectsWithBinding`), `.ControllerContext`。Pass 抛异常→整个构建中止(rethrow)。无内置进度/取消 API → 自建。`WellKnownPlatforms.VRChatAvatar30 = "nadena.dev.ndmf.vrchat.avatar3"`。BuildPhase: FirstChance/PlatformInit/Resolving/Generating/Transforming/Optimizing。 |
| Modular Avatar | 1.18.2 | QualifiedName `nadena.dev.modular-avatar`; 主要在 Resolving/Transforming。i18n 模式: JSON per-language + Localizer loader (我们照此实现但自动扫描 json)。 |
| AvatarOptimizer(AAO) | 1.9.17 | QualifiedName `com.anatawa12.avatar-optimizer`, 主序列在 **Optimizing**。`Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI`(拼写确认无误): `IsTexCoordUsed(SkinnedMeshRenderer, ch)` / `RegisterTexCoordEvacuation(smr, origCh, savedCh)`; asmdef `com.anatawa12.avatar-optimizer.api.editor` 且 autoReferenced=false → 我们用**反射适配**, 不硬引用, 兼容未安装 AAO。其 `ShaderInformation.Liltoon.cs` 是 lilToon 属性语义权威参照(已通读, ATO 的 lilToon 表据此实现)。 |
| lilToon | 2.3.4 | 特性开关 = `_UseBumpMap` 等 float 属性(shader_feature 由材质属性驱动); UV 选择 = `_XXX_UVMode`(0..3=uv0..3, 4=MatCap/Rim 等非网格UV, 其他值=未知); ST/滚动/旋转 = `_XXX_ST` + `_XXX_ScrollRotate` + `_XXXAngle`; 贴花/翻转/复制 = `_XXXIsDecal/IsLeftOnly/IsRightOnly/ShouldCopy/ShouldFlipMirror/ShouldFlipCopy/IsMSDF/DecalAnimation/DecalSubParam`; `_ShiftBackfaceUV`≠0 → MainTex 矩阵不可信; 已知非网格UV贴图: `_MatCapTex/_MatCap2ndTex/_GlitterShapeTex/_MainGradationTex/_Shadow*ColorTex(_ShadowColorType==1)/_EmissionGradTex/_DitherTex` 等 → 这些一律白名单。 |
| VRCSDK base/avatars | 3.10.4 | `VRC.SDKBase.IEditorOnly`(runtime 组件实现它); `VRC.SDK3.Avatars.Components.VRCAvatarDescriptor.baseAnimationLayers / specialAnimationLayers`(CustomAnimLayer{type,isDefault,animatorController,…}, DLL 内已 strings 验证)。ndmf 在 Resolving 前移除 tag==EditorOnly 物体 → 我们运行时它们已不存在(仍留防御代码)。 |
| LightLimitChanger | 2.13.0 | 会动画修改材质颜色属性(不动贴图引用), 佐证"动画可改材质属性"需保守处理。 |

## 2. 核心架构共识 (Coder 三方共识, Reviewer 已确认)
处理时机: ndmf **Optimizing** 阶段, `AfterPlugin("nadena.dev.modular-avatar").BeforePlugin("com.anatawa12.avatar-optimizer")`。

管线 (全部在一个 Pass 内, 分阶段显示进度+可取消):
1. **Validate**: 全 Avatar(含子级)只允许 1 个组件且必须挂在有 VRCAvatarDescriptor 的物体上, 否则 ErrorReport(Error)+抛异常中止。
2. **Scan**: 渲染器(SMR/MR, 启用或被动画启用) → 材质槽 → ShaderAnalyzer(lilToon 表 + 标准属性/关键字通用分析 + [Normal]/[NoScaleOffset]/[MainTexture] 属性 + 扩展接口) → 每个 (材质,贴图属性) 得到: 贴图/UV通道/类别(Color,Normal,Mask,Grayscale,LinearColor)/sRGB/filterMode/是否可安全处理。不可分析或特殊用途 → 白名单+warning。
3. **Animation scan** (AnimatorServicesContext): 材质切换/贴图切换 pptr → 并入 UV 映射; `_XXX_ST`/`_ScrollRotate`/UVMode/Cutoff/渲染模式 float 动画 → 记录或白名单; scale 动画取最大值; m_IsActive/m_Enabled → 动画启用渲染器; blendshape 动画不特殊处理(面积评估固定取 0/100 两者最大)。
4. **Dedup(前置)**: 按 (像素哈希 + 完整导入设置) 去重并更新引用; 命中白名单 → 结果视为白名单。
5. **UVGroup/TypeGroup**: UVGroup = (Mesh 实例, UV 通道) —— **类型组以 UVGroup 为粒度**(取其全部用例的特性并集: 含法线与否/蒙版与否/sRGB/filterMode), 同一 UVGroup 的所有贴图(含动画切换)在所有图集**共享同一套岛矩形**(归一化坐标), 镜像图集(法线/蒙版)与主图集同尺寸、同布局, 若镜像图集内全部岛质量余量允许则整图 2^k 均匀缩小(保最小 padding)。
6. **Islands**: 按 UV 三角形连通分量提岛; 量化形状哈希合并重叠岛; 岛 bbox 可整体平移归一到 [0,1] 则归一, 跨 wrap 缝 → 白名单+warning; blendshape 面积 = max(0,100), 缩放面积 = max scale²。
7. **Quality scaling**: 二分 UV 缩放; GPU(RT) 双线性缩放/回放(法线: 解码→重采样→重归一化→重编码; 透明: 预乘 alpha), CPU Burst 度量: MS-SSIM(短边<176→单尺度, <11→忽略该指标)+ΔE2000+alpha(Cutout→clip 轮廓 IoU / Blend→线性 RMSE, 逐引用材质取最严)+法线角度误差 mean+p95+灰度按使用通道线性 RMSE。木桶效应: 岛最终尺寸 = 各贴图最小达标尺寸的最大值(≤原尺寸)。纯色岛短路至 min(4,短边)。像素密度钳制 [minPx/m, maxPx/m](挡位 512..8192) 且受原物理尺寸钳制。目标质量==1 → 跳过缩放原样拷贝。均匀缩放达标后再双轴独立二分细化(各向异性)。
8. **Packing**: 每类型组: 岛 4px 粒度 bitmask 光栅化(缓存), 队列按光栅面积降序, 原子 = 贴图+UVGroup; 候选图集池 POT(默认, 64..8192/移动4096)或 64 步进 NPOT(实验), 丢弃面积<总面积的候选, 按(面积升序, 长宽比接近1优先)逐个尝试, 全扫描 BLF + 面积降序 + 边长降序 + 90°旋转(bitmask 转置, 法线绝不重算切线), 第一个能装下全部岛的候选即成品; 装不下→溢出贴图另起队列(复用同类队列); 单贴图连最大图集都装不下→放弃该 UVGroup 图集化(仅缩放)+warning。**形状光栅化装箱, 非矩形装箱**。padding = max(4, ceil(候选最大边/128)) 可选挡位 4..64。
9. **Atlas build**: GPU 合成岛像素 + pull-push 无限外扩渗色(透明图集 alpha 保持 0); ATO_ 前缀; Read/Write off, Clamp 强制; mip+MipStreaming 绑定单开关; 压缩格式按 (透明/不透明/法线/灰度)×(PC/Android/iOS) 安全枚举, 不安全组合构建时 fallback+控制台警告; NPOT 勾选时剔除 PVRTC 等不支持格式。
10. **Rewrite**: 克隆网格重写 UV(岛矩形+旋转, 跨岛共享顶点拆分), 保留原切线/蒙皮/形态键; AAO `UVUsageCompabilityAPI`(反射) 做 UV 通道疏散; 按 (renderer,slot) 克隆材质赋图集引用(不改任何其他参数), 动画内材质经 AnimationIndex.RewriteObjectCurves 按 binding 精确替换; 材质槽合并且动画不单独切换时合并相同不透明材质并更新索引。
11. **Post dedup**: 材质/贴图(内容+参数)去重开关(默认开)。
12. **Report**: [ATO] 日志(每阶段耗时/岛数/图集来源/利用率/优化量, 默认总览+细节折叠) → ndmf 控制台; 成功后移除自身组件; 取消→保留临时资产, finally 释放 RT/NativeArray/GPU。

其余共识: i18n 自动扫描 Localization/*.json(用户可自行加语言文件), 语言选项 Auto(跟随 ndmf)+手动, 回退英文; 全部代码注释英+中; 日志 [ATO] 前缀+耗时, verbose 开关; 白名单对象不限类型(网格/材质/贴图/动画/任意 Object, 递归收集其中引用的全部贴图); "不生成图集"模式: 不剔岛/不重排, 仅整图缩放+其余优化; MS-SSIM 176px/11px 阈值源自标准 5 尺度实现(11×2⁴=176)。

质量挡位 (依文献: MS-SSIM Wang2004, ΔE2000 JND≈2.3 Sharma2005, 法线压缩研究 1-2.5°): NearLossless(全1=近无损,原样拷贝) / Ultra(0.995,ΔE1.0) / **High(默认 0.99,ΔE1.5)** / Balanced(0.98,ΔE2.3) / Aggressive(0.95,ΔE3.5) / Custom(用户自管,默认全1)。

## 3. 已知用户输入的勘误/风险反馈 (需在交付说明重申)
1. NPOT+Crunch: Unity Crunch 压缩历史上要求 POT; 已实现"构建失败自动 fallback + 控制台警告", 用户声称已验证可用的场景由 fallback 兜底。PVRTC 在 NPOT/非正方形下不可用 → iOS+NPOT 勾选时已剔除。
2. 用户原文 UVUsageCompabilityAPI 拼写属实, 已按 AAO 源码适配。
3. 材质槽合并只对"不透明+动画不单独切换"执行(动画按 binding 检查), 否则跳过——安全优先。
4. 同一贴图被不同 UV 使用时: 岛按 UVGroup 各自裁剪, 像素可能进多张图集(必要代价); 材质按 (renderer,slot) 克隆以指向正确图集。
5. lilToon 非网格 UV 贴图(MatCap/渐变/Dither 等)与特殊变换(贴花/翻转/MSDF)一律白名单——与 AAO 的保守结论一致。

## 4. 进度
- [x] 第三方源码通读 + API 取证
- [x] 架构共识 (本文件 §2)
- [x] 骨架/组件/设置/i18n/日志/进度/插件
- [x] Shader 分析 (lilToon 表 + 通用)
- [x] 扫描器 (渲染器/白名单/动画)
- [x] 贴图库 (readback+去重+导入设置)
- [x] 岛提取/归一/重叠合并
- [x] 质量算法 (GPU 重采样 + Burst 度量 + 二分缩放)
- [x] 类型组/UV组
- [x] 装箱 (bitmask 光栅 + BLF + 候选池)
- [x] 图集构建 (pull-push + 压缩/mip/streaming)
- [x] 网格/材质/动画重写 + AAO 兼容 + 槽合并
- [x] 后置去重 / 报告 / 控制台
- [x] Inspector UI / 平台 override / 预设
- [x] 扩展 API / README / i18n 文件 (en + zh-Hans)
- [x] Reviewer 轮 + QA 轮(3 独立通读) + zip 交付

## 5. 注意事项
- 用户手动同步到 Unity 工程验证; 无法在本环境编译 Unity 代码 → Reviewer/QA 轮静态审查 + 交付说明中列出需要在 Unity 中验证的点。
- 不修改第三方库; 不引用 AAO 程序集(纯反射); ndmf/VRCSDK 为硬依赖 (asmdef defineConstraints: NDMF_VRCSDK3_AVATARS)。
- 每次 git 提交; CLAUDE.md 随每次提交更新。
- 临时资产全部 `ctx.AssetSaver.SaveAssets`; RT 用 RenderTexture.GetTemporary/ReleaseTemporary; NativeArray 全 Dispose。
- .meta 已随包生成; Localization/en.json.meta 的 GUID (9f4f86d6fc8b45cfa22d3930a304ebca) 已写入 ATOL10n.MarkerGuids；若用户重新生成 metas 会自动走路径回退扫描。
- 代码结构: Runtime(组件+设置) / Editor(API, ATOPlugin, Core, Shader, Texture, UV, Quality, Packing, Atlas, Report, UI, Localization) / Localization(en+zh-hans) / docs(TeamLog)。
- 关键修复历史(防回退): GL.LoadPixelMatrix 方向、位掩码真旋转、合并岛 master 解析+自身 offset、多通道网格累积重写、m_Enabled 的 IsAssignableFrom、语言全局 EditorPrefs。
- 首次 Unity 实测关注点: Hidden/ATO/Gfx 加载、Burst 编译、ndmf 排序(MA后AAO前)、lilToon 真实材质、AAO 反射疏散、Crunch/NPOT 组合回退。
