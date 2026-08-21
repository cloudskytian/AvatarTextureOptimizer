# CLAUDE.md — AvatarTextureOptimizer 项目记忆 / Project Memory

> 本文件是本项目的唯一记忆载体（用户要求）。任何计划变更、进度、结论都必须同步写回这里。
> This file is the single memory store for this project (user requirement). Update it with every change.

## 项目 / Project
- 名称 / Name: `net.fosa.avatar-texture-optimizer` (ATO)
- 目标 / Goal: 世界最好的 VRChat Avatar 贴图优化工具（NDMF 插件，MA 后 AAO 前执行）
- 交付形态 / Delivery: 非 Unity 工程，仅 VPM 包目录；用户手动同步进 Unity 工程验证。

## AgentTeam 运作 / Team Operation
- Coder×3（A:管线/NDMF、B:几何/UV/质量、C:装箱/渲染/UI/i18n）——每模块先出共识决议再落码。
- Reviewer×3——每模块交叉审查；已打回并修复的问题记录在下方"Review 记录"。
- QA×3——最终各自完整读全部代码；结论见"QA 记录"。

## 已验证的外部 API 事实（禁止凭猜测改动！）/ Verified External API Facts (never guess!)
| 事实 | 来源 |
|---|---|
| NDMF 1.14.4: `Plugin<T>`, `InPhase(BuildPhase.Optimizing)`, `Sequence.AfterPlugin(string)/BeforePlugin(string)`（partial Sequence 直接成员）, `WithRequiredExtension(Type, Action<Sequence>)`, `Run(string, InlinePass)` | ndmf 源码 Editor/API/Fluent |
| NDMF: `ErrorReport.ReportError(Localizer, ErrorSeverity, string key, params object[])`; `ErrorSeverity.Information/NonFatal/Error` | Editor/ErrorReporting |
| NDMF: `Localizer(string defaultLang, Func<List<LocalizationAsset>>)`; `LanguagePrefs.Language`; `LocalizationAsset.AddEntry` | Editor/UI/Localization |
| NDMF: `ctx.AssetSaver.SaveAsset/SaveAssets/IsTemporaryAsset`; `ctx.AvatarRootObject/AvatarRootTransform` | BuildContext / IAssetSaver |
| NDMF AnimatorServices: `ctx.Extension<AnimatorServicesContext>()`, `AnimationIndex.RewriteObjectCurves(Func<Object,Object>)` | Editor/API/AnimatorServices |
| AAO 1.9.17 插件名 `com.anatawa12.avatar-optimizer`; API 命名空间 `Anatawa12.AvatarOptimizer.API`; 类名 `UVUsageCompabilityAPI`（AAO 原文拼写，无 "ti"）；方法 `IsTexCoordUsed(SMR,int)` / `RegisterTexCoordEvacuation(SMR,int,int)`；经反射调用以兼容未安装 AAO 的用户 | AAO API-Editor 源码 |
| MA 1.18.2 插件名 `nadena.dev.modular-avatar`（Optimizing 阶段内再 AfterPlugin 兜底） | MA PluginDefinition |
| lilToon 2.3.4: `uvMain = lilCalcUV(uv0, _MainTex_ST, _MainTex_ScrollRotate)`，`_ShiftBackfaceUV`、各 `*_UVMode`、贴花族属性、`_UseBumpMap` 等开关——属性表已转录进 `LiltoonTables.cs`（与 AAO 的 LiltoonShaderInformation 交叉核对） | lilToon 源码 + AAO 转录 |
| VRC SDK 3.10.4: `VRCAvatarDescriptor.baseAnimationLayers/specialAnimationLayers: CustomAnimLayer[]{type,animatorController,isDefault,isEnabled}`；`VRC.SDKBase.IEditorOnly`（VRCSDKBase.dll） | SDK dll strings 核实 |
| `UnityEngine.Rendering.VertexAttribute.TexCoord0 == 4`（不是 2） | Unity 文档枚举序 |
| 质量阈值依据: MS-SSIM (Wang 2003/2004, 权重 .0448/.2856/.3001/.2363/.1333)；CIEDE2000 (Sharma 2005)；JND ΔE00≈1.0–2.3 | 学术文献 |

## 架构共识决议（Coder×3）/ Architecture Consensus (Coders)
1. **UV组不变式**：UvGroup=(mesh,channel) 的岛在所有图集映像上位置一致（同一布局多张映像：主色/法线/蒙版按 (isNormal,sRGB,filterMode,category) 拆分映像）——满足"同一UV在不同图集上位置相同"。
2. **簇与分片（替代规格中的类型组队列）**：共享贴图的 UV组 连通成簇；簇内按主类型键+面积分片装箱。类型组分离的本质（避免法线图集 9/10 浪费）通过"每布局按需渲染映像"实现：无法线贴图的布局不渲染法线映像。
3. **原子性**：装箱原子单位=贴图及其全部 UV组 的未放置岛（规格原文）；跨簇约束通过 UV组→单一图集 的不变式保证。
4. **装不下最大图集**：放弃该 UV组图集化→整图缩放+warning（规格原文）。
5. **wrapped 岛**（跨 wrap 缝）：整组跳过图集化+warning（规格原文）。
6. **质量二分**：先均匀二分（指数探测+二分），再 X、Y 轴独立二分细化（各向异性）；密度钳制 [minD,maxD] px/m + 不超过源贴图像素密度；无损挡跳过一切重采样（含纯色）；纯色岛短路至 min(4,短边)。
7. **MS-SSIM 回退**：区域短边<176px→单尺度SSIM；<11px→忽略该指标（窗口下限）。
8. **指标在区域偏移处采样**：ReduceMetrics 以 `_RegionOffset` 采样完整RT内的岛区域（Review 修复）。
9. **GPU 内存纪律**：逐次评估的RT用 GetTemporary/ReleaseTemporary 即用即还；线性源缓存有界(12)；资产级RT由 GPUContext.Dispose 统一释放（finally 块，取消也不泄漏）。
10. **白名单传播**：对象展开（材质→贴图、网格→渲染器材质、GameObject→子渲染器、动画/控制器→曲线对象）；去重组任一成员白名单→整组白名单；不合格用途→整张贴图白名单+warning。
11. **透明最严**：跨材质/动画取最严 alpha 模式；Cutout 对每个候选 cutoff 逐一评估 IoU。
12. **槽合并**：仅不透明、且该渲染器无任何材质槽动画时合并相同材质槽（合并子网格）；材质/贴图去重开关独立。
13. **平台Override**：PC/Android/iOS 各一份完整 ATOSettings，勾选生效；默认读取当前构建平台；移动端 ASTC-only（不提供 PVRTC），PC 提供BC族。
14. **AoS 安全回退**：有alpha内容→强制带alpha格式+警告；多通道灰度→强制多通道格式+警告；法线→BC5/ASTC。Mipmap 与 MipStreaming 一个开关（VRChat 要求），SerializedObject 开启 streaming，Clamp+Read/Write关闭不给改。

## 进度 / Progress
- [x] 包骨架/asmdef/Runtime组件/设置模型
- [x] 日志/计时、i18n(JSON,auto跟随ndmf)、进度+取消、GPU上下文
- [x] Avatar/动画扫描、lilToon属性表、通用着色器分析
- [x] Burst 岛提取（焊接/并查集/归一化/重叠合并/形态键+缩放面积）
- [x] 使用图（内容+导入设置去重、白名单展开、覆盖边）
- [x] 质量引擎（compute: 降采样/MS-SSIM/CIEDE2000/alpha/法线/灰度 + Burst CPU兜底 + 二分）
- [x] 光栅化(4px)、BLF装箱（候选池/方形优先/90°旋转/边距膨胀）
- [x] 图集渲染（类型映像、pull-push外扩、透明alpha保持0）
- [x] TextureWriter（安全格式枚举/回退、Mipmap+流式绑定、ATO_前缀）
- [x] 网格UV重映射（共享旋转映射）、材质克隆改写、动画对象曲线重写(AnimatorServices)
- [x] 去重与不透明槽合并、AAO反射兼容、扩展点(IATOExtension六阶段)
- [x] NDMF 插件（Optimizing, After MA, Before AAO, self-remove）、ndmf 控制台报告
- [x] UI（i18n Inspector、平台Override、高级折叠、语言切换）、en/zh JSON
- [x] 语法验证：tree-sitter c_sharp 全 28 文件通过（预处理器分支除外，属解析器限制，两分支均合法）
- [x] README.md、CLAUDE.md、zip 交付

## 未完成/已知简化（诚实清单）/ TODO & Known Simplifications
- [x] **编译验证已完成**：沙箱内搭建 dotnet 8 + 忠实API桩（UnityEngine/UnityEditor/Burst生态/NDMF/VRC 全部按已核实签名），对全部源码实际编译：**0 error / 0 warning（自有代码）**；另有14项纯逻辑冒烟测试全部通过（Docs/SmokeTests.cs.txt）。Unity 内实际运行/烘焙仍需用户验证（运行时行为、GPU平台差异无法在沙箱复现）。
- [ ] 动画中 `material.<命名材质>.<属性>` 的具名纹理切换按"并入该渲染器全部槽"保守处理（安全但可能多优化）。
- [x] CPU兜底指标已升级为真实公式（单尺度SSIM+CIEDE2000+IoU+法线角度，Burst），编译验证通过。
- [ ] Crunch 压缩选项未提供（格式枚举已验证 NPOT+流式可用，Crunch 预留）。
- [ ] ndmf 预览不支持（规格允许）。
- [ ] UVUsageCompabilityAPI 预留反射封装（当前就地改写UV值无需登记迁移）。
- [ ] 1px 级装箱对齐为 4px 粒度（规格即如此）；padding 膨胀为 Chebyshev 距离。

## Review 记录（Reviewer×3 打回清单）/ Review Log
1. Partials 结构体步长 55→73 floats、alpha 误差改平方和、MS-SSIM 金字塔先降后评。
2. 指标归约缺区域偏移（读错区域）→ shader 增加 `_RegionOffset`。
3. GPU 临时RT累积 → GetTemporary/ReleaseTemporary 纪律 + 有界线性缓存。
4. `VertexAttribute.TexCoord0` 误用 2 → 4。
5. Burst 作业结构体值返回不生效 → NativeArray 出参。
6. IslandRasterizer 单 ulong 行(≤256px) → 多字行位图。
7. 包根定位用相对路径 GetFullPath（依赖CWD）→ 锚定工程根。
8. Editor asmdef 缺 ATO_VRCSDK3A define（会导致完全不收集动画层）→ 补 versionDefines。
9. EditorOnly 判定误引 VRC 类型（无SDK编译失败）→ 纯 tag 遍历。
10. AtlasRenderer ClearAlpha 时机/rt缺UAV/幽灵属性 CurrentAtlasRT → 整体重写。
11. 组件基类列表 #if 切割 → 双分支完整声明。

## 编译验证环境（可复用）/ Compile-Verification Setup (reusable)
- /var/tmp/dn: dotnet SDK 8.0.424（注意 /tmp tmpfs 仅993M，装在 /var/tmp）
- /var/tmp/atocompile: 桩（stubs/*.cs，签名全部按依赖源码核实）+ ATOCompile.csproj（netstandard2.1, C#9, DefineConstants=ATO_VRCSDK3A;ATO_AAO;ATO_LILTOON）
- /var/tmp/atotest: 冒烟测试（net8.0 可执行）
- 本轮桩编译抓出的真实bug：缺 using（System.Linq/UnityEditor/跨子命名空间）、LiltoonTables 命名参数越位、Guard lambda out参数类型、ATOI18n FindAssets 类型、AddAnimatedCutoffs 调用点、SetVector 需 Vector4、Vector4/Color.ToString(fmt)、enumValueIndex/GUILayout.Width 等（后数项为桩缺口非代码bug，均已确认真实API存在）。

## QA 记录（QA×3 独立全文复查）/ QA Log
- QA1: API 对齐 ndmf/AAO/MA/VRC 事实表 ✓；i18n 键双语对齐(43=43) ✓；JSON 可解析 ✓。
- QA2: 资源生命周期（finally 释放、取消异常路径）✓；白名单传播链 ✓；装箱不变式（UV组→单图集）✓。
- QA3: 语法全绿（tree-sitter）；符号交叉引用（跨文件方法签名/命名空间）grep 核对 ✓；已知简化清单如实披露 ✓。
- 共识：可交付 v0.1.0；剩余风险=未在Unity编译（唯一无法在沙箱消除的验证项）。

## 用户验证步骤 / How the user verifies
1. 把本目录整体放入 Unity 工程（或 VCC add package），等待编译。
2. 在 Avatar 根（VRCAvatarDescriptor 对象）添加组件 `ATO Avatar Texture Optimizer`。
3. ndmf 控制台 "Manual Bake" 或直接 Play/Upload 触发构建；观察 [ATO] 日志与进度条；完成看 ndmf 控制台报告。
4. 加速排障：组件勾选 Verbose/Timings 日志。

## 规则提醒 / Standing Rules（用户明令）
- 禁止猜测第三方 API（先读源码）；修改前先读代码取证；不修改材质除贴图外任何参数；
- 每次修改后 git commit + 更新本文件；日志 [ATO] 前缀；注释中英双语；简体中文交流。
