# CLAUDE.md — AvatarTextureOptimizer (ATO) 项目记忆 / Project Memory

> 本文件是项目的唯一记忆载体。任何一轮工作开始前先读此文件；每轮结束前更新此文件。
> This file is the single source of project memory. Read it before starting any work; update it before finishing.

## 0. 基本信息 / Basics
- 项目名 / Project: **AvatarTextureOptimizer (ATO)** — VRChat Avatar 贴图优化 NDMF 插件
- 包名 / Package: `net.fosa.avatar-texture-optimizer`，版本 0.1.0，Unity 2022.3，NDMF >= 1.14.4，VRC SDK 3.10.4
- 语言约定：代码注释双语（EN+ZH）；日志前缀 `[ATO]`；与用户交流用简体中文
- 阶段约定（重要）：**MA 之后、AAO 之前**（`BuildPhase.Optimizing` + `AfterPlugin("nadena.dev.modular-avatar")` + `BeforePlugin("com.anatawa12.avatar-optimizer")`）
- 参考库源码在 `refs/`（已完整读通关键 API，见 §5），**禁止未验证的 API 猜测**

## 1. 已验证的关键第三方 API（禁止偏离）
| 库 | 事实 |
|---|---|
| ndmf 1.14.4 | `[assembly: ExportsPlugin(typeof(X))]`; `Plugin<T>`(.QualifiedName/.DisplayName/.Configure); `InPhase(BuildPhase.Optimizing).AfterPlugin("nadena.dev.modular-avatar").WithRequiredExtension(typeof(AnimatorServicesContext), seq => seq.Run(Pass.Instance).BeforePlugin("com.anatawa12.avatar-optimizer"))`; `Pass<T>.Execute(BuildContext)`; `ctx.AvatarRootObject/AvatarRootTransform`; `ctx.AssetSaver.SaveAsset(obj)`(IAssetSaver); `ctx.GetState<T>()`; `ctx.Extension<T>()`; `ErrorReport.ReportError(Localizer, ErrorSeverity, key, params object[])`; `SimpleError`(TitleKey/DetailsKey/Localizer/Severity/References); `ObjectRegistry.GetReference(obj)`; `Localizer(string defaultLang, Func<List<(string, Func<string,string>)>>)`; `LanguagePrefs.Language` |
| ndmf animator | ns `nadena.dev.ndmf.animator`; `AnimatorServicesContext` → `.AnimationIndex`(`GetPPtrReferencedObjectsWithBinding`/`RewriteObjectCurves(Func<Object,Object>)`/`GetClipsForBinding`)、`.ControllerContext.Controllers`(key=VRCAvatarDescriptor.AnimLayerType)；`VirtualClip.GetFloatCurveBindings()/GetFloatCurve(b)/GetObjectCurveBindings()/GetObjectCurve(b)/SetObjectCurve(b,kfs)`；`VirtualNode.AllReachableNodes()`；asmdef: `nadena.dev.ndmf`, `nadena.dev.ndmf.runtime` |
| AAO 1.9.17 | ns `Anatawa12.AvatarOptimizer.API`; `UVUsageCompabilityAPI.IsTexCoordUsed(SkinnedMeshRenderer, int ch)` / `.RegisterTexCoordEvacuation(SkinnedMeshRenderer, int origCh, int savedCh)`；asmdef `com.anatawa12.avatar-optimizer.api.editor` 且 **autoReferenced:false** → 必须独立桥接 asmdef + versionDefines(com.anatawa12.avatar-optimizer ≥1.8.0 → ATO_AAO) + defineConstraints；插件 QualifiedName `com.anatawa12.avatar-optimizer` |
| MA 1.18.2 | 插件 QualifiedName `nadena.dev.modular-avatar`（Transforming 主体 + Optimizing GCGameObjects） |
| VRC SDK 3.10.4 | `VRC.SDK3.Avatars.Components.VRCAvatarDescriptor`（VRC.SDK3A）、`VRC.SDKBase.IEditorOnly`（VRC.SDKBase）；`baseAnimationLayers/specialAnimationLayers: CustomAnimLayer[]{animatorController,isDefault,type}`（经 ndmf 的 VRChatPlatformAnimatorBindings 验证） |
| lilToon 2.3.4 | 主色 `[MainTexture] _MainTex`(+`_MainTex_ScrollRotate`,`_ShiftBackfaceUV`)；法线 `[Normal] _BumpMap/_Bump2ndMap/_AnisotropyTangentMap/_MatCapBumpMap/_MatCap2ndBumpMap/_OutlineVectorTex`；UV 选择 `_<Tex>_UVMode`(0..3=UV0..3, 4=MatCap; Emission 有 Rim=5)；UV 动画 `_<Tex>_ScrollRotate`(Vector)；cutoff `_Cutoff`/`_SubpassCutoff`/`_AlphaMaskMode` |
| Unity 技术 | 运行时贴图 MipStreaming: `new SerializedObject(tex).FindProperty("m_StreamingMipmaps").boolValue=true`（avatar-compressor 验证）；`EditorUtility.CompressTexture(tex, fmt, quality)` **不做** DXTnm 摆动 → PC DXT5/BC7=AG 布局、BC5/ASTC=RG 布局需手动打包；`shader.GetPropertyCount()/GetPropertyName(i)/GetPropertyType(i)==ShaderPropertyType.Texture/GetPropertyFlags(i)`、`material.GetTexture(id)`（AAO 实证） |

## 2. 架构设计（Coder 共识定稿 / Coder consensus, final）
### 2.1 数据流 / Pipeline（全部在单 Pass `ATOOptimizePass` 内分 9 阶段，每阶段计时+可取消）
1. **Validate** 校验组件唯一性/挂载位置（必须=Avatar 根，含 VRCAvatarDescriptor），违规 → ErrorReport(Error)+中止
2. **Collect** 遍历 Renderer（跳 EditorOnly / 常关且无动画开启的）；动画扫描（AnimatorServicesContext）：材质槽 PPtr 换材质、`material.*` float/color（ST/Scroll/Angle→判废；_Cutoff 收集阈值范围；渲染模式取最严）、`blendShape.*`(0/100 max)、`m_LocalScale`(最大面积系数=两轴积最大值)、`m_IsActive`/`m_Enabled`
3. **ShaderAnalyze** lilToon 属性表 + 通用 Shader 属性表（flags/名称启发式）；不认识的 shader → 其纹理全部白名单+warning；UVMode→UV 通道；ST/Scroll/Rotate≠默认 → 白名单化该纹理
4. **Dedup(textures)** GPU 读回(blit, GL.sRGBWrite) + 内容哈希 + 导入设置快照（不同=不同贴图）；合并引用（材质/动画 PPtr 经 SerializedObject 替换）；白名单传染
5. **Model** 建 TexInfo / UvGroup(=Renderer×UVChannel) / Island（并查集按共享 UV 边切岛；越界整体平移归一化，跨 wrap 缝→白名单+warning；同贴图重叠岛合并；世界面积含 blendshape(max 0/100)+动画缩放）
6. **Quality** 纯色岛短路 min(4,短边)；质量=1 → 原样拷贝；否则二分：均匀缩放达标后双轴独立细化；密度钳制 [min,max px/m]；每 UV 组取所有贴图木桶最大尺寸（≤组内最大原尺寸）；指标：MS-SSIM(短边<176→SSIM, <11→忽略)、ΔE00、alpha(Cutout IoU/Blend RMSE，逐材质最严)、法线角度误差 mean+p95、灰度逐通道 RMSE
7. **Pack** 类型组（签名=特殊图种类集∪{sRGB,filterMode}；动画换贴图并入原图所在组作"变体层"）；4px 位掩码光栅 + 全扫描 BLF + 90°旋转(位掩码转置) + 候选图集池（POT 64..8192 / 移动 4096；NPOT 64 步进实验性）；装箱原子 = 贴图 + 其全部 UV 组的岛；padding=max(ceil(边/128), 用户最小 padding)；counterpart（法线/蒙版/变体）图集 = 同布局同归一化位置，整体可按比例缩小（保持最小 padding）；单贴图装不下最大图集 → 放弃图集化走整图缩放+warning
8. **Build** CPU(Burst) 重采样岛→图集 RGBA32（预乘 alpha；法线解码/重归一化/编码 + 旋转补偿通道交换）；GPU pull-push 无限外扩填空白（透明图集 alpha=0）；压缩(格式安全 fallback)+mip+MipStreaming(SerializedObject)+Read/Write 关闭；`ATO_` 前缀
9. **Rebind+Report** 克隆 Mesh 重写 UV（AAO 占用通道→搬移原 UV 到空通道并 RegisterTexCoordEvacuation）；克隆材质仅替换贴图引用；AnimationIndex.RewriteObjectCurves 更新动画；材质/贴图去重（无独立切换动画时合并不透明材质槽并改写槽位索引索引）；移除组件；ErrorReport(Information) 报告 + 控制台全量日志
### 2.2 关键共识决策（含用户需求修正）
- **C1** 90° 旋转装箱 vs 法线：切线不重算 → 法线岛写入图集时若旋转需 G/R 通道交换（含符号），否则光照错误
- **C2** counterpart 图集缩小：UV 是归一化坐标，归一化位置一致即可，分辨率可不同；POT 模式下 counterpart 非 POT 时禁 PVRTC→fallback ASTC+警告
- **C3** 装箱原子取“贴图×全部 UV 组”（比规格的“贴图×单 UV 组”更强，同样满足“同贴图同图集”约束，簿记更简单）
- **C4** 贴图被 eligible 组与 ineligible 组同时引用：生成图集版（eligible）+ 整图缩放版（ineligible）两份，各自材质指向正确版本
- **C5** 指标在**线性空间**计算；GPU 读回统一转线性（GL.sRGBWrite 保 raw 后按导入设置转换），最终写 sRGB 图集时编码回去；Gamma 工程同样成立（读回=raw）
- **C6** 质量挡位（学术依据）：MS-SSIM(Wang+03)；CIEDE2000 JND≈1.0(Sharma+05)；High(默认)=0.98/ΔE1.0/IoU.995/RMSE2.5/法线1°&3°/灰2.0；Medium=0.95/2/.99/4/1.5°&5°/3；Aggressive=0.90/3.5/.98/6/2.5°&8°/5；Custom 默认全 1（近无损，msSsim=1→跳过缩放含纯色，原样拷贝）
- **C7** 整图缩放路径：不生成图集开关打开时对所有贴图走“整图=一个岛”的质量缩放（不剔除/不重排UV）
- **C8** 变体层（动画切换贴图）：与基础布局完全一致的独立图集层；质量木桶取最大
- **C9** 灰度贴图实际使用多通道时即便用户选单通道格式也按多通道保存+警告（规格要求）
- **C10** 桥接 AAO 用独立 asmdef+versionDefines，主程序集通过委托钩子调用（防未安装时编译错误）
- **C11** VRC 要求：Mipmap 与 MipStreaming 绑定为单开关；图集 Read/Write=off、Clamp 强制，不给用户改
- **C12** 报告：ErrorReport(SimpleError:Information) 标题=总览，描述=紧凑明细；完整明细打 Unity 控制台
### 2.3 目录 / Layout
```
net.fosa.avatar-texture-optimizer/
  package.json
  Runtime/  (asmdef net.fosa.avatar-texture-optimizer.runtime; refs VRC.SDKBase)
    ATOComponent.cs      组件+全部序列化设置+枚举
    ATOExtensionHost.cs  第三方扩展接口（运行时可见）
  Editor/   (asmdef net.fosa.avatar-texture-optimizer.editor; refs runtime, nadena.dev.ndmf, Burst/Collections/Mathematics, VRC)
    ATOPlugin.cs ATOApi.cs
    Core/    ATOBuildState/ATOLog/ATOProgress/ATOReport
    i18n/    ATOLocalization
    ShaderAnalysis/ ATOShaderRules(lilToon+通用表) ATOShaderAnalyzer
    Collect/ ATOCollector
    Model/   ATOModel(TexInfo/UvGroup/Island/TypeGroup/AtlasResult…)
    Dedup/   ATOTexDedup
    Gpu/     ATOGpu(读回/pull-push)
    Islands/ ATOIslands(切岛/归一化/重叠合并/光栅化)
    Quality/ ATOQuality(二分+密度) ATOQualityJobs(Burst:SSIM/MS-SSIM/ΔE00/alpha/normal/gray) ATOPresets
    Atlas/   ATOPacker(BLF位掩码) ATOAtlasBuilder(合成/压缩/流式)
    Rebuild/ ATOMeshRebuild ATOMaterialRebuild ATOSlotMerge
    Optimize/ ATOTextureParams(整图缩放+导入参数)
    Platform/ ATOPlatform(PC/Android/iOS override+格式安全枚举)
    UI/      ATOInspector
    AAO/     AAOBridge(钩子) + aao-bridge.asmdef + AAOBridgeImpl
    Shaders/ ATOGpu.shader(blit/premult/pull-push)
  Localization/ en-US.json zh-CN.json
```

## 3. 进度 / Progress
- [x] 第三方源码阅读（§1 表）+ 可行性确认
- [x] 架构定稿（§2）
- [x] Runtime：组件/设置/枚举/扩展接口
- [x] Editor：全部 9 阶段管线（Collect/Analyze/Dedup/Model/Quality/Pack/Build/Rebind/Report）
- [x] i18n（en-US/zh-CN，用户可扩展：放 Localization/*.json 即生效）
- [x] AAO 桥接（未安装 AAO 安全退化）
- [x] Inspector（挡位联动/平台 override 折叠/白名单/日志）
- [x] README、打包 zip
- [x] AgentTeam 三阶段流程（TEAM_LOG.md）与三轮 QA 静态验收（条件通过，见 §3）
- [ ] **用户在 Unity 内实测**（本环境无 Unity，QA 为静态审查+静态一致性检查；以下为已知待验证点）
### 待 Unity 验证 / TODO-verify
1. 编译通过性（无 Unity 编译器，靠静态核对；重点：Burst job 泛型约束、asmdef 引用名）
2. GL.sRGBWrite 读回在 Gamma/Linear 工程的颜色一致性
3. EditorUtility.CompressTexture 对 BC5/BC7 在编辑器下的实际支持（PC 目标）
4. SerializedObject(m_StreamingMipmaps) 在 2022.3 的存在性（avatar-compressor 同代验证过，风险低）
5. pull-push shader 采样的 bleeding 效果
6. lilToon UVMode 属性在各变体 shader 上是否齐全（lts.shader 全量核对过，Multi/Lite 变体可能缺属性→运行时按属性存在性判断）

### QA 验收结论（2026-08-22）
三 QA 独立全量复查一致结论：**有条件通过**。已修复的 QA/Reviewer 发现：
跨程序集可见性(InternalsVisibleTo)、FindProperty 嵌套路径、ShaderPropertyFlags 命名空间、
Unity.Jobs using、法线平台编码标志、无组件静默跳过、TexInfo 三元布尔、Read/Write 最终关闭、
AAO 搬移时序（克隆后改写前）、pull-push 双纹理、按渲染器克隆 Mesh、多布局贴图解析。
唯一未闭环项：**无 Unity 环境实机烘焙验证**（见上）。

## 4. 工作约定 / Conventions
- 每次修改后 git commit + 更新本文件
- 日志全部 `[ATO]` 前缀，带阶段耗时； verbosity 由组件设置控制（默认 Info）
- 交付物 zip：`AvatarTextureOptimizer-<ver>.zip`（含包目录，不含 refs）
- AgentTeam 记录见 TEAM_LOG.md
