# AvatarTextureOptimizer — 项目记忆 / Project Memory

> 本文件是本项目的唯一记忆载体。每次修改后必须更新：当前计划、已完成工作、整体进度、未完成工作、注意事项。
> This file is the single memory store for this project. Update after every change.

## 1. 项目身份 / Identity

- 名称：AvatarTextureOptimizer（ATO）
- 包名：`net.fosa.avatar-texture-optimizer`
- 定位：世界最好的 VRChat Avatar 贴图优化 NDMF 工具（开源）
- 交付形态：UPM 包目录（Packages/net.fosa.avatar-texture-optimizer），用户手动同步进 Unity 工程验证
- 交流语言：简体中文（代码注释 英文+中文 双语；i18n JSON 至少 en-US + zh-Hans）
- 日志统一前缀 `[ATO]`，预留日志开关（高级用户调试用）
- 交付：最终打包 zip；不交付半成品；README.md 在全部完成后编写

## 2. 团队流程 / Agent Team Process

- Coder-A / Coder-B：写码前就设计达成共识（写入 docs/TEAMLOG.md）
- Reviewer-1 / Reviewer-2：每次代码完成后双重独立审查，共识后决定打回/通过（记录结论）
- QA-1 / QA-2：全部完成后各自**从头独立通读全部代码**查隐患/Bug/需求符合性，双双通过才可交付
- 每次修改：先读码取证再下结论；git 提交；更新本文件

## 3. 阶段排序 / Pipeline Placement（已取证确认）

- 在 `BuildPhase.Optimizing` 运行：`.BeforePlugin("com.anatawa12.avatar-optimizer")` 且 `.AfterPlugin("nadena.dev.modular-avatar")`
  - 依据：MA 主体在 Transforming 阶段（QualifiedName "nadena.dev.modular-avatar"，另有 late-transform-stages 插件与 Optimizing 的 GCGameObjectsPluginPass）；AAO 主体在 Optimizing（QualifiedName "com.anatawa12.avatar-optimizer"，故意用 ￜￜￜ 命名空间排到最后）
  - 因此在 Optimizing 内 BeforePlugin(AAO) 天然满足"MA 之后、AAO 之前"；另加 AfterPlugin(MA) 锁死 MA 的 Optimizing GC pass
- 插件声明：`[assembly: ExportsPlugin(typeof(ATOPlugin))]`，`Plugin<T>` + `InPhase(...).Run(...)`

## 4. 已取证的第三方 API 事实 / Verified API Facts

### NDMF 1.14.4（Packages/nadena.dev.ndmf）
- `Plugin<T>`：`Configure()` / `InPhase(BuildPhase)` → `Sequence`；`Sequence.Run(Pass<T>|string, InlinePass)` → `DeclaringPass`，有 `BeforePlugin/BeforePass/AfterPlugin/AfterPass/Then/WithRequiredExtensions/PreviewingWith`
- `BuildContext`：`AvatarRootObject/AvatarRootTransform/AssetSaver/SaveAsset? 无 → ctx.AssetSaver.SaveAsset(obj)`（AssetSaver.cs L116 `SaveAsset(Object)`）、`ErrorReport`、`GetState<T>()`、`Extension<T>()`、ObjectRegistry（`RegisterReplacedObject(old,new)`）、`SetEnableUVDistributionRecalculation(mesh,bool)`
- 报错：`ErrorReport.ReportError(IError)`；`SimpleError` 抽象类（Localizer/TitleKey/Severity，可子类化）；`ErrorSeverity { Information, NonFatal, Error, InternalError }`
- 语言：`nadena.dev.ndmf.localization.LanguagePrefs.Language`（public static get/set，值如 "en-us"/"zh-hans"），`RegisterLanguageChangeCallback<T>(handle, cb)`
- 平台：`WellKnownPlatforms.VRChatAvatar30 = "nadena.dev.ndmf.vrchat.avatar3"`（默认插件只跑 VRChatAvatar30，无需额外属性）
- 预览：不支持 → 不注册 IRenderFilter（不调用 PreviewingWith）
- 注意：NDMF 没有 JSON i18n（它用 .po），我们自带 JSON i18n，Auto 模式读 LanguagePrefs.Language

### AAO 1.9.17（com.anatawa12.avatar-optimizer）
- `Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI`（拼写如此，非笔误）
  - `static bool IsTexCoordUsed(SkinnedMeshRenderer renderer, int channel)`（channel 0~7）
  - `static void RegisterTexCoordEvacuation(SkinnedMeshRenderer renderer, int originalChannel, int savedChannel)`（savedChannel 被 AAO 占用会抛 InvalidOperationException；实现是给 GO 加 InternalEvacuateUVChannel 组件）
  - Impl 由 [InitializeOnLoadMethod] 注册 → 任何时刻可调用
  - AAO 使用 UV 的组件：RemoveMeshByMask→channel0；RemoveMeshByUVTile→按配置
- 兼容策略：**反射调用**（用户可能未装 AAO）；只有改过某 renderer 某通道 UV 时才检查/登记；选 savedChannel=未被 AAO 使用且未被我们改写的空闲通道；找不到则跳过重映射该 UV 并报 warning（安全兜底）
- AAO 材质动画兼容：AAO 的 TraceAndOptimize 之后可能自己做 UV 用途操作，所以我们必须在它之前完成所有 UV 改写并登记疏散

### lilToon 2.3.4
- 属性总表在 Editor/lilInspector/lilMaterialProperties.cs（`new lilMaterialProperty("_Name", true, PropertyBlock.XXX)` 形式，isTexture 标记）
- 编辑器类型 `lilToon.lilToonInspector`（internal? → 反射取非公开实例字段）：字段类型 `lilToon.lilMaterialProperty`，公开字段 `propertyName`/`isTexture`/`blocks`
- **自动分析方案**：编辑器内通过反射实例化 lilToonInspector 不行（需要 target），改为反射读取该类型的**非公开实例字段声明**（字段 initializer 是 `new lilMaterialProperty("...", true, ...)`，无法直接取构造参数）→ 改为**静态反射不足**，故：
  - 方案A（主）：烘焙进库的 lilToon 2.3.4 属性指纹表（贴图属性名/角色/UV 相关浮点属性）+ 运行时用 `shader.FindPropertyIndex` 校验实际存在性（兼容未来新增属性：未知的贴图类型属性 → 一律视为不可处理 → 白名单+warning）
  - 方案B（增强扩展口）：公开 `IATOShaderAnalyzer` 注册接口，第三方可补充着色器支持
- 判定 lilToon 家族：`shader.name` 以 "lilToon"/"Hidden/liltoon" 开头或含 "liltoon"
- UV 变换/特殊用途判定属性（非默认值则该贴图白名单）：`*_ScrollRotate`（应全为0）、`*_UVMode`（必须为0=UV0）、`*Decal*`/`MSDF`、`_ShiftBackfaceUV`、`_MainGradationStrength` 等渐变/HSVG 修改存在时仅细化处理：v0.1 中 HSVG 非默认/Gradation 非默认 → 主色白名单（保守）
- Matcap/Ramp(阴影)/Parallax/AudioLink/Outline 等**非网格 UV 采样**贴图：不纳入优化（它们不是"经网格 UV 采样"的贴图）
- 渲染模式：material.renderQueue（<2450 Opaque；2450~2999 Cutout；≥3000 Transparent）+ `_Cutoff` 值；动画可改 `_Cutoff` → 取最严格

### Modular Avatar 1.18.2
- QualifiedName "nadena.dev.modular-avatar"（+ ".late-transform-stages"）；我们只依赖排序约束，不依赖其 API

### VRChat SDK 3.10.4（com.vrchat.base/avatars）
- 运行时需要 `VRC.SDK3.Avatars.Components.VRCAvatarDescriptor`（asmdef: VRC.SDK3.Avatars? 见 Runtime asmdef 名：com.vrchat.avatars 的 Runtime asmdef 名称为 "VRC.SDK3.Avatars" —— 写 asmdef 引用时按 GUID/名称 "VRC.SDK3.Avatars"；Editor 侧 "VRC.SDK3.Avatars.Editor"）【TODO: 写 asmdef 前再精确核对一次 asmdef 名字】

### avatar-compressor 0.9.0 / LLC 2.13.0
- 仅作惯例参考，不复制其代码、不依赖；LLC 在 Transforming 生成材质颜色动画（不影响我们：我们只改贴图引用与 UV）

### NDMF GeneratedAssets
- 只是 ScriptableObject 容器，不含磁盘目录方案 → 生成贴图必须走"磁盘 PNG + TextureImporter"

## 5. 关键架构决策（Coder 共识）/ Key Decisions

1. **生成资产落盘导入**：图集/缩放图写到 `Assets/AvatarTextureOptimizer-Generated/<buildId>/`，用 TextureImporter 设置（逐平台 TextureImporterPlatformSettings、Mipmap+Streaming 绑定、Read/Write Off、Repeat→图集强制 Clamp、sRGB/Normal map、filterMode 取最高）→ 这是获得 Crunch/平台格式/MipStreaming 的唯一正确路径
   - 缓存：最终像素+导入参数哈希复用磁盘文件，跨构建免重复导入
   - 清理：下次构建开始时清理上一代遗留 + 菜单手动清理；取消构建时保留在磁盘（符合用户要求）
2. **质量算法拆分**：GPU(RenderTexture) 负责解码/线性空间/预乘 alpha/双线性重采样；指标（MS-SSIM/ΔE2000/alpha IoU·RMSE/法线角度/灰度分通道 RMSE）在 Burst 并行 CPU 上算（符合规格"GPU+Burst 批量"）；对照区=原岛覆盖掩码内双线性上采样回原尺寸后比对
3. **UV 组/贴图类型组**：UV 组=同一 (mesh, submesh, uvChannel) 采样点共享同一 UV 布局的贴图栈（主色+法线+蒙版+动画替换全部并入），组内同位置同尺寸（木桶=max 需求，≤组内最大原尺寸）；类型组 key=(角色签名, 色彩空间, filterMode)
4. **装箱**：Burst 光栅位掩码（4px 粒度）+ 全扫描 BLF + 面积降序 + 边长降序 + 90°步进旋转（位掩码转置；法线切线不动，因为 UV 同步旋转采样结果不变）+ 候选图集池（POT 64..8192/移动端4096；NPOT 实验项 64 步进，剔除平台不支持格式如 iOS PVRTC）；队列以"贴图+其 UV 组"为原子；候选池排序=面积升序、同面积长短边比升序
5. **去重**：贴图=像素内容+导入设置双哈希；材质=全属性指纹（纹理引用+浮点+颜色+向量+关键字+renderQueue+shader）；合并不透明重复槽→合并 submesh 并重映射动画材质索引，无法安全判定时跳过合并
6. **形态键/缩放**：面积权重=逐形态键 max(|w=0|,|w=100|) 的位移叠加（不排列组合）+ 动画 scale 曲线最大值 + transform 静态缩放
7. **组件**：`VRC.SDK3.Avatars.Components.VRCAvatarDescriptor` 同 GameObject 上，全 Avatar 只允许一个 ATO 组件，否则报错中止
8. **i18n**：JSON 文件放包内 i18n 目录 + `Assets/AvatarTextureOptimizer-Generated/i18n` 用户扩展目录；Auto→LanguagePrefs.Language，找不到翻译回退 en-US
9. **进度/取消**：UnityEditor.Progress（可取消）+ CancellationToken；异常级联安全释放(RTok/NativeArray)；取消保留磁盘临时资产
10. **无 Preview**：不注册 preview filter（暂不支持 NDMF 预览，文档明示）
11. **报告**：ErrorReport Information/NonFatal 条目 = 总览（细节折叠进 details 文本），Debug 日志 `[ATO]` 前缀

## 6. 进度 / Progress

- [x] 阶段0：依赖库下载/解压/取证（8 包全读关键面：NDMF API、AAO API、lilToon 属性表、MA 排序、VRC 平台）
- [x] 架构设计共识 + 本记忆文件
- [x] 包骨架（package.json/asmdef/组件/设置模型）—— module1
- [x] i18n + 本地化器 + en-US/zh-Hans 翻译 —— 109 键双语对称
- [x] 分析管线（白名单/着色器分析/动画扫描/UV-贴图映射）—— module3/4/5
- [x] 贴图去重 —— module6（importSignature + pixel contentHash，按规格）
- [x] UV 岛构建（含越界归一、重叠合并、各向异性、面积评估）—— module7
- [x] 质量评估引擎（指标 + 二分 + 各向异性细化）—— module8
- [x] 图集装箱（位掩码 BLF + 候选池 + 类型组队列）—— module9（QA-1 后 64 位字并行加速）
- [x] 图集合成（GPU 合成 + pull-push + 落盘导入 + 缓存）—— module9（QA-1 后 Pass12/13/14 alpha 管线）
- [x] 引用重写（材质/网格/动画）+ 后去重（材质/贴图/槽合并）—— module10/11
- [x] AAO 兼容登记 + 报告 + 自移除 + 进度取消 —— module12/13
- [x] Inspector UI（折叠高级选项、平台 override、白名单列表、语言选择）—— module13（SerializedProperty 全链路）
- [x] 公开扩展 API —— ATOExtensionApi（事件+自定义打包器）
- [x] Reviewer 联合评审 R1 —— e7201c0（材质槽冲突守卫等 3 项）
- [x] QA-1 全量重读 —— 04a3d73（15 项修复，含质量折叠死锁/着色器编译错误/alpha 管线/焊接伙伴 UV）
- [x] QA-2 独立重读（需求矩阵视角）—— e4c5654（组处置追踪 + 类别安全格式枚举；4 项抗辩采信）
- [x] README.md + zip 交付 —— ca4937f，74 文件 zip
- [ ] 用户 Unity 实测验收（用户手动同步包进工程烘焙）

## 7. 注意事项 / Caveats

- 规格中"目标质量=1"的语义：QualitySettings.TargetQuality≥0.999 → 近无损路径：跳过 UV 缩放（含纯色不交）、原样拷贝进图集
- **动画读取/写入安全（已取证）**：必须通过 NDMF AnimatorServicesContext 的 `VirtualControllerContext.GetAllControllers()` 读取动画（否则在 MA/LLC 虚拟化控制器后读完的是旧控制器）；写动画用 `VirtualClip.SetFloatCurve/SetObjectCurve`——对 marker clip（平台特殊 Motion）写入会被静默忽略，这是 NDMF 的 COW 保护；用户资产不会被污染。旧式 Animation 组件只读扫描、不写（VRC 本就忽略它们；材质 clone 后旧 clip 指向原材质属可接受并记录日志）
- lilToon 的 uvMain 受 `_MainTex_ST`/`_MainTex_ScrollRotate`/POM（`_UseParallax`,`_UsePOM`）影响 → 任何非默认即白名单主色链（已写进 lilToon 表 ZeroChecks）
- 材质属性动画（`material._Cutoff` 等）绑到 Renderer path 无槽位索引 → 保守应用为该渲染器全部槽位的最严格值
- **90° 旋转（推翻旧结论，现行共识：全部角色允许旋转）**：旧笔记误称"法线禁止旋转"。逐纹素映射论证：图集化时**内容与 UV 同转**——对任意表面点 P，新 UV u'(P)=R(u(P))，且新贴图内容 T'(R(u))=T(u)，故采样值 T'(u'(P))=T(u(P)) 逐点不变；网格切线帧从未改动 → 法线/彩色/蒙版全部安全。旧担忧混淆了"只转内容不转 UV"（那才是错的）。实现一致性已复核：打包器输出 rotated90 → 合成器 Pass8 Rotate90CW 转内容 → mesh rewrite 用同一 placement 变换 UV
- **padding alpha 规格**：pull-push 仅无限外扩 **RGB**；padding 区 alpha 恒 0（透明保持透明）。Pass13/14 用 覆盖掩码（main.a>0.5）区分岛内/外。已知权衡：Cutout 岛边在极端双线性下理论有 ≤0.5 权重 alpha 侵蚀；单行可改（README 披露）
- **QA-1 关键不变量（勿回退）**：① 质量折叠=首个候选写入、后续跨贴图 max（木桶）；禁止向 1.0 初始表折叠 max（会锁死全部缩放）。② `ATOTextureIO.Readback` 的 RT 读写空间必须与贴图 sRGB 属性一致（线性彩空间工程往返恒等）；③ 焊接伙伴顶点按**原始 UV 量化键**寻址重写（法线分裂缝的所有顶点都要写）；④ 池化 RT 复用必须重置 filter=Bilinear/wrap=Clamp；⑤ standalone 整图缩放保留源 wrap；⑥ 写盘 mipmap 取**当前构建平台**规则（mipmap 是导入器全局项）；⑦ 写盘缓存哈希含三平台本类别规则+wrap+filter+npot
- **组处置契约**：`ATOUVGroup.FinalDisposition` 报告生成前恒非空（atlas/standalone:<tag>/whitelist:<原因>/kept-original），全部进报告 `[group]` 行
- **格式安全枚举**（开 ATOFormatMapping.IsCompatible）：Normal∈{RGBA32,ARGB32,BC5,BC7,ASTC*,ETC2_RGBA8}；Transparent 排除 R8/R16；Opaque 排除 R8/R16；Grayscale∈{R8,R16,RGBA32,ARGB32,BC7,ASTC*,ETC2_RGBA8,DXT1}；灰度内容多通道→`isEffectivelyGray=false`→写盘降级多通道格式+warning
- EditorOnly 对象已被 NDMF 移除（Resolving 内置 pass），我们遍历时仍双重检查 tag
- 复杂动画（AnimatorController 里嵌 SubStateMachine/BlendTree）→ 遍历全部 VirtualClip?→ v0.1 直接收集所有 AnimationClip（descriptor playable layers + 全 Animator 组件 + 全 Animation 组件合并去重）
- 任何无法满足"无变换/无特殊用途"的贴图 → 一律白名单化 + warning（绝不出错图）
- 用户反馈点：① NDMF 语言 API 存在（已确认可用）；② 90°旋转与法线贴图冲突已在设计中规避；③ "最大像素密度 4096px/m"会限制特写大面积布料的高分辨率 —— 提供用户可调
