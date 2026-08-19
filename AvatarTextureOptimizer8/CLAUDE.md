# CLAUDE.md — AvatarTextureOptimizer (ATO) 项目记忆

> 本文件是本项目的**唯一记忆载体**。任何阶段性进展、决策、注意事项都记录在此。
> 交流语言:简体中文。代码注释:英文+中文双语。包名:`net.fosa.avatar-texture-optimizer`。

## 1. 项目目标

做一个面向 VRChat Avatar 的开源 NDMF 贴图优化工具(目标:全世界最好的 vrchat 贴图优化工具):
- 在 **MA 之后、AAO 之前**(BuildPhase.Optimizing)运行
- 网格 UV → 贴图 映射;按目标质量算法对 UV 岛缩放;剔除未用 UV;重新装箱图集
- 贴图类型组(法线/蒙版等伴随关系)、UV 组(同 UV 同位置约束)
- MS-SSIM/ΔE(CIEDE2000)/alpha/法线角度/灰度 RMSE 质量算法;Burst+GPU
- 白名单、动画分析、去重、平台 override、i18n、NDMF 控制台报告、进度条+取消
- 详细需求见用户原始需求文档(已内化到本文件与 DESIGN.md)

## 2. AgentTeam 分工与流程

- **CoderA/CoderB**:每次写代码前先互相交流得出共识,再落实代码(共识记录见 §7)
- **ReviewerA/ReviewerB**:Coder 每完成一个模块,两人共同审查,共识决定是否打回
- **QA1/QA2**:全部功能完成后,两人**独立从头完整查阅全部代码**,同时认可才可交付
- 全程一次性完成所有功能后打包 zip 交付,不交付半成品

## 3. 第三方库调研结论(已读通,凭据见 /tmp/deps,若丢失可重新下载)

| 库 | 关键结论 |
|---|---|
| NDMF 1.14.4 | `[assembly: ExportsPlugin(typeof(X))]` + `Plugin<T>` 重写 `QualifiedName/DisplayName/Configure()`;`InPhase(BuildPhase.Optimizing).AfterPlugin("nadena.dev.modular-avatar").BeforePlugin("com.anatawa12.avatar-optimizer").Run(pass)`;`WithRequiredExtension(typeof(AnimatorServicesContext), s=>s.Run(...))`;Pass<T> 重写 Execute(BuildContext)。BuildContext:`AvatarRootObject/AvatarRootTransform/AssetSaver/IAssetSaver.SaveAsset/IsTemporaryAsset/GetState<T>/Extension<T>()`。错误报告:`ErrorReport.ReportError(Localizer, ErrorSeverity, key, args)`,SimpleError 子类化可自定义 TitleKey/DetailsKey/HintKey/References。i18n:`nadena.dev.ndmf.localization.Localizer(defaultLang, Func<List<(lang, Func<string,string>)>>)` + `LanguagePrefs.Language`。动画服务:`AnimatorServicesContext`(IExtensionContext)→ `AnimationIndex.RewriteObjectCurves(Func<Object,Object>)`、`GetClipsForBinding`、`ClipsWithObjectCurves`、`GetPPtrReferencedObjectsWithBinding`;`VirtualClip.GetFloatCurveBindings/GetFloatCurve/SetFloatCurve/GetObjectCurve(SetObjectCurve)`。asmdef 名 `nadena.dev.ndmf`(引用 Unity.Burst/Collections/Mathematics,故 VRC 工程必有) |
| AAO 1.9.17 | 插件 qname `com.anatawa12.avatar-optimizer`。`Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI.IsTexCoordUsed(SkinnedMeshRenderer,channel)` / `RegisterTexCoordEvacuation(renderer,original,saved)`(AAO 1.8.0+,编辑器 API,asmdef `com.anatawa12.avatar-optimizer.api.editor`)。AAO 内部 LiltoonShaderInformation(ShaderInformation.Liltoon.cs,853行)完整给出 lilToon 2.3.x 每个贴图属性的 UV 通道/矩阵语义,已读通并提炼为自己的表 |
| MA 1.18.2 | qname `nadena.dev.modular-avatar`;asmdef `nadena.dev.modular-avatar.core`(runtime)、`...core.editor` |
| VRC SDK 3.10.4 | asmdef:`VRC.SDKBase`(runtime)、`VRC.SDK3A`(avatars runtime)。`VRC.SDKBase.IEditorOnly` 在 VRCSDKBase.dll;`VRC.SDK3.Avatars.Components.VRCAvatarDescriptor` 在 VRC.SDK3A。baseAnimationLayers/specialAnimationLayers: CustomAnimLayer{type,animatorController,isDefault,...} |
| lilToon 2.3.4 | Editor 全 C# 源码,lilInspector/lilMaterialProperties.cs 等齐全,可精确提取属性表 |
| avatar-compressor 0.9.0 | 仅参考(不依赖)。参考其 GPU 图集思路 |
| LLC 2.13.0 | 下载链接 404(GitHub Pages Page not found),非依赖项,跳过(已在交付说明中注明) |

### lilToon 贴图属性语义表(源自 AAO ShaderInformation.Liltoon.cs 提炼,针对 lts 2.3.x)

uvMain = UV0 ∘ matrix(_MainTex_ST, _MainTex_ScrollRotate) 且 _ShiftBackfaceUV==0
- uvMain: _MainTex/_BaseMap/_BaseColorMap(采样_MainTex)/_MainColorAdjustMask/_Main2ndBlendMask/_Main3rdBlendMask/_AlphaMask/_BumpMap/_Bump2ndScaleMask/_AnisotropyTangentMap/_AnisotropyScaleMask/_AnisotropyShiftNoiseMask/_BacklightColorTex/_ShadowStrengthMask/_ShadowBorderMask/_ShadowBlurMask/_ShadowColorTex(ShadowColorType==0)/_Shadow2ndColorTex/_Shadow3rdColorTex/_RimShadeMask/_SmoothnessTex/_MetallicGlossMap/_ReflectionColorTex/_MatCapBlendMask/_MatCapBumpMap/_MatCap2ndBlendMask/_MatCap2ndBumpMap/_RimColorTex/_GlitterColorTex/_EmissionBlendMask(无ScrollRotate时)/_Emission2ndBlendMask/_OutlineTex/_OutlineWidthMask/_FurMask/_FurLengthMask/_FurVectorTex
- UV per mode int 属性(_XXX_UVMode: 0=UV0,1=UV1,2=UV2,3=UV3,4=NonMesh(MatCap/uvRim),null/other=全部+NonMesh): _Main2ndTex/_Main3rdTex(_UseMain2ndTex/_UseMain3rdTex开关) 走 LIL_GET_SUBTEX(decal/flip/copy/MSDF等不支持→null matrix);_Bump2ndMap(_UseBump2ndMap);_EmissionMap/_Emission2ndMap(_EmissionMap_UVMode,注意 _EmissionParallaxDepth!=0 时为 parallax);_OutlineVectorTex(_AudioLinkMask_UVMode??AAO源码如此:outline的UVMode借用_AudioLinkMask_UVMode)
- UV0: _ParallaxMap(_UseParallax)、_DissolveMask/_DissolveNoiseMask(无_ST变体时UV0)、_FurNoiseMask
- NonMesh(不可装箱): _DitherTex/_MainGradationTex/_ShadowColorTex(LUT模式)/_MatCapTex/_MatCap2ndMesh...2ndTex/_GlitterShapeTex/_EmissionGradTex/_AudioLinkLocalMap/_MatCapTex
- 其他:_AudioLinkMask per _AudioLinkMask_UVMode;Fur:_FurNoiseMask UV0
- 开关: _UseXXX != 0 才生效(如 _UseBumpMap/_UseShadow/_UseEmission/...)
- 溶解:_DissolveParams.x!=0 且使用 _DissolveMask/_DissolveNoiseMask
- 不支持矩阵(null→跳过优化): decal 系(_XXXIsDecal等)、POM、fur 的 _ShiftBackfaceUV!=0
- ST+ScrollRotate 矩阵:matrix = T·R·S;若任一非恒等→该贴图"有变换"→白名单

## 4. 架构设计(DESIGN)

见仓库内 `DESIGN.md`(与第三方开发者的公共设计说明)与本节摘要。

### 4.1 数据流(6 Pass)
```
Validate → Analyze → Scale(质量) → Pack(装箱) → Bake(烘焙+重写) → Finalize(去重/报告/清理)
```

### 4.2 核心数据模型
- **IslandKey**:(renderer,mesh,submesh,uvChannel,islandId)
- **UVGroup**(连通分量):网格岛↔贴图二部图的连通分量;装箱原子单位;签名=(伴随角色集,sRGB,filterMode)
- **TextureLayer**:同 UVGroup 内共享任一网格岛的贴图互为"变体"(动画切换),必须平行图集(同 layout);用贪心图着色分配 layer
- **TypeGroup**:签名相同的 UVGroup 集合,共享候选图集池
- 岛质量缩放:UVGroup 内所有贴图逐层评估,木桶效应取最大尺寸;先均匀二分再双轴独立二分
- 图集装箱:4px 光栅位掩码+全扫描BLF+面积降序+旋转90°(位掩码转置)+候选池(POT/NPOT-64步进,移动端4096上限)

### 4.3 关键设计决策(含用户确认/我方修正)
1. **切线与旋转**:旋转岛内容+重映射UV+保持原切线=一致正确(切线是网格数据,不随图集变),故支持旋转90°
2. **动画变体平行图集**:同一UV对应的多张贴图分属不同图集但同 layout,满足"同UV不同图集位置相同"
3. **AAO 兼容**:我们重写UV前,若 AAO 存在且 IsTexCoordUsed(ren,ch) 则将原UV备份至空闲通道并 RegisterTexCoordEvacuation;未装 AAO 时跳过
4. **像素处理管线**:GPU(RenderTexture)负责源贴图读取(readback)与上传;Burst 负责重采样/指标/光栅化/装箱扫描/图集组装;上传用 SetPixelData;压缩 EditorUtility.CompressTexture
5. pipeline 位置:Optimizing 阶段 + AfterPlugin(MA) + BeforePlugin(AAO)
6. 白名单对象展开:Texture/Material/Renderer/GameObject(递归子树)/AnimationClip(曲线值)/Mesh→使用该mesh的渲染器
7. 质量挡位(NearLossless/High/Medium/Low/Custom):参数化阈值;Custom 默认全1(近无损→跳过缩放);默认挡位 High
8. 密度钳制:默认 min 2048 px/m、max 4096 px/m,挡位 512..8192;受原贴图物理尺寸钳制(s≤1)
9. 兜底策略:任何单元失败→保留原引用+warning,不中断整体;组件挂载不合规→Error 中止
10. 内存:NativeArray/RT 用完即弃,try/finally;读回缓存 LRU;峰值日志
11. 图集 Read/Write 关、强制 Clamp;Mip+Streaming 绑定单开关
12. 压缩格式安全枚举:按 透明/不透明/法线/灰度 × 平台;构建时兜底(如灰度多通道强制多通道格式+警告)
13. i18n:JSON 文件枚举加载;Auto=ndmf 语言;回退英文
14. 日志 [ATO] 前缀+计时+可开关;NDMF 控制台报告(总览+折叠细节)

## 5. 当前状态与进度

- [x] 需求评审、可行性确认(可行:TexTransTool/AAO 已证明该方向可行,方案有依据)
- [x] 第三方库全部下载并读通关键实现(见 §3;LLC 链接 404 已注明)
- [x] 仓库初始化、CLAUDE.md
- [x] M1:包骨架+Runtime组件+插件注册+i18n+Logger+进度+UI
- [x] M2:分析层(ShaderAnalyzer/lilToon表/动画分析/渲染器收集/白名单/去重/岛提取)
- [x] M3:质量层(SSIM/MS-SSIM/ΔE00/alpha/法线/灰度 Burst 作业+二分缩放+密度钳制+纯色短路)
- [x] M4:装箱层(光栅化/膨胀/转置/BLF/候选池/辅助层缩放)
- [x] M5:烘焙层(GPU读取/图集组装/pull-push渗色/网格材质动画重写/压缩兜底)
- [x] M6:收尾(材质去重+槽位合并/AAO反射集成/NDMF报告/移除组件/清理)+ 双QA修复
- [x] M7:README/DESIGN/zip 交付(v0.1.0)

**交付物**:仓库 `/home/user/AvatarTextureOptimizer`(git 历史 6 提交)+ zip 包。
**下一步(待用户实测反馈)**:Unity 内编译→烘焙验证→按表现回传 bug 清单修复。

## 6. 注意事项(踩坑/要点)

- `.git/config` 不在快照内,跨回合提交需 `git -c user.name=... -c user.email=...`
- Unity 2022.3/C# 9;禁止 C#10+ 语法(如 record struct、file class)
- 组件必须实现 `VRC.SDKBase.IEditorOnly` 以免 VCC 构建 errors;NDMF 烘焙后 Pass 中 DestroyImmediate 组件
- EditorUtility.DisplayCancelableProgressBar 在 VRChat 构建 Dialog 无取消按钮(平台限制),已用状态机+检测 VRC Control Panel 判断,尽力支持
- m_Materials 动画曲线替换后,须破坏共享依赖(_EditorGameDataPath 相关)无关;直接改克隆 clip
- 灰度通道使用:优先 lilToon 已知蒙版语义;未知→全通道保守
- NPOT+Crunch/MipStreaming 已验证可用;iOS 剔除 PVRTC
- 上下文若中断:先读本文件,再读 DESIGN.md,再看 git log
- /tmp/deps 若丢失,按 §3 表格 URL 重新下载解压即可

## 6.1 组件挂载验证流程

1. `ATOBuildState.EnsureInstance` 在每个阶段开头调用;找不到→该阶段直接 return
2. 失败原因分两级:
   - **Error(中止构建)**:挂载点无 VRCAvatarDescriptor
   - **NonFatal**:冗余组件自动移除 + NDMF 控制台提示
3. 挂载规则:整个 Avatar(含子级)最多 1 个组件;必须在 VRCAvatarDescriptor 同一 GameObject 上
4. 组件实现 `VRC.SDKBase.IEditorOnly`:VRChat SDK 构建管线会自动剥离 IEditorOnly 组件(即使我们不主动移除也不会泄漏进上传);NDMF 烘焙(手动 Build)时我们在最终 Pass 中 DestroyImmediate 移除自身
```

## 7. AgentTeam 记录(倒序,最新在上)

### 2026-08-19 QA 验收(全部功能完成后)
- **QA1(编译正确性视角,独立从头通读全部代码)**:
  - 对照 NDMF 源码核 API:发现 `AnimationIndex.RewriteObjectCurves` 返回 void(我方误当 int)→ 修复
  - using 缺失 7 处(Unity.Collections/Unity.Jobs/UnityEditor/nadena.dev.ndmf/.animator/.localization)→ 修复
  - `System.Object`/`UnityEngine.Object` 歧义 → 全限定修复;ATOLogger.ResetTimings 残留元组垃圾 → 清理
  - 全项目 mcs 语法检查通过(约束 C#7 保守语法:无 switch 表达式/元组解构/local function/default 字面量)
- **QA2(逻辑/规范视角,独立从头通读全部代码)**:
  - [严重] 岛重叠合并的代表 ID 错位 → 可能丢岛 → 修复(_mergeMap 改为映射到代表岛 Id)
  - [严重] 白名单污染:同 UV 存在白名单/未处理贴图时,若仍图集化会重写 UV 破坏原贴图采样 → 实现不动点传播(岛封禁→贴图NoAtlas→其他岛封禁),受污染组件整体回退整图缩放(规范要求的行为)
  - [严重] 多分辨率组小贴图有效缩放未按分辨率换算评估(会被过度缩小而未评估)→ IslandPasses 逐贴图换算 + FillWholeTexScales 同步修正
  - [严重] RasterizeScaled 把目标像素尺寸当贴图尺寸传参 → 双重缩放 → 改为虚拟贴图尺寸
  - [严重] 旋转写入时 DstW/DstH 语义错误(应为源空间尺寸)→ 修复(颜色/法线/MarkValid 三处一致)
  - 透明图集:是否有 alpha 改为仅按有效像素判定;渗色后空白区 alpha 强制 0(规范)
  - 验收结论:**两 QA 一致通过**(上述问题均已修复并回归语法/一致性检查:方法定义vs调用全对齐、无悬挂引用)
- 提交: `QA fixes` + `QA round 2`
### 2026-08-19 M1 骨架完成
- Coder共识:包结构/asmdef引用/组件字段/i18n JSON加载/NDMF注册方式;质量挡位参数化(QualityParams 7阈值+像素密度);平台Profile嵌套复用 QualityParams;默认挡位 High
- Reviewer共识:通过,建议(1)UI 默认折叠高级选项(2)白名单 Object 列表用 HE弛缓序列化(3)组件加 IEditorOnly。已落实
- QA:骨架阶段无逻辑可测,通过
- 提交: `M1 skeleton`
### 2026-08-19 需求评审+调研
- 可行性:可行。TexTransTool/AAO/AtlasTools 先例证明 UV 岛重排+图集在 NDMF 环境可落地;质量算法(WSIM/ΔE)有成熟文献;Burst/GPU 装箱有工程先例
- 用户需求 2 处需修正/提醒(已在最终回复中向用户说明): (a) 动画切换贴图的"平行图集"结构 (b) NPOT+MipStreaming 兼容性已验证的声明我们按真处理
- 风险:Unity 环境外无法编译验证;靠静态 QA + 用户实机验证

## 8. 待用户实测验证清单(下一步工作)

1. Unity 2022.3 工程内编译(主要风险:Burst 作业与 NDMF API 的实际签名;语法已过 mcs,类型级靠人工审计)
2. 手动 NDMF 烘焙一个含 lilToon 材质+法线+蒙版+动画切换贴图的 Avatar:检查报告/图集/UV/表现
3. 验证:白名单对象、ST 变换材质(应白名单+警告)、UV 越界(应归一化/白名单)、多通道 UV、形态键面积、取消按钮
4. 验证压缩兜底路径与平台 override;AAO 安装/未安装两种环境
5. 性能:大 Avatar 的耗时与内存峰值([ATO] 日志有计时)

## 9. 已知限制(向用户声明)

- SMR 世界面积用 renderer 变换近似;蒙皮姿态不参与
- 正式 VRC 构建对话框的取消按钮由平台决定;NDMF 手动烘焙可取消
- 灰度通道表以 lilToon/Standard 为准,未知着色器保守全通道评估
- BLF 为贪心启发式,极端岛形利用率可能非最优
- 材质分析缓存按 Material 键(槽位级动画浮点差异取保守近似)
- UI 全局压缩区显示 Auto 说明,显式格式在平台覆盖区设置(0.1.x 简化,后续可拆独立全局区)

## 10. 用户原始需求(摘要备份,防上下文丢失)

见 git 历史(首个提交含完整需求文本)。关键点:质量算法(MS-SSIM+ΔE+alpha;法线角度误差;灰度通道RMSE;木桶效应;二分搜索;176px/11px 阈值;纯色短路;密度钳制;质量1跳过)、图集装箱(BLF+位掩码+旋转+候选池;padding ceil(边长/128)≥4;图集数量不限;ATO_ 前缀)、类型组/UV组、白名单全类型、去重(内容+导入设置;材质去重+槽位合并)、平台 override(PC/Android/iOS)、压缩安全枚举、Mip+Streaming 绑定、组件规则(单组件+Descriptor同物体)、内存/进度/取消、[ATO]日志、AAO UVUsageCompabilityAPI、i18n JSON、报告到NDMF控制台、预留扩展接口、README
