# AvatarTextureOptimizer 架构设计（Coder 共识 v1）

> 本文档是 3 个 Coder 交流后形成的共识设计；Reviewer 已复核。实现必须遵循本文档。
> 涉及第三方库的行为全部经过源码/元数据取证（见 CLAUDE.md §取证结果），禁止猜测 API。

## 0. 可行性结论

用户提出的总体方案**可行**。存在两个需要修正/补充的设计点（已向用户反馈）：

1. **装箱粒度修正**：原方案"按贴图类型组形成贴图队列装箱"与"同一 UV 在不同图集上位置相同"存在冲突（同一 UV 组会出现在多个类型组的图集中，若各自独立装箱，岛位置必然不同）。修正：**装箱以 UV 组为原子、在 UV 归一化空间进行**，岛原点在所有类型组图集间共享（§6）。
2. **动画引用重映射**：NDMF 的 ObjectRegistry 只负责错误报告引用追踪，**不会**自动改动画曲线里的对象引用（已读源码确认）。必须自实现动画曲线重映射（§8）。

其他细节取舍（像素密度带语义等）见 §5.3，已在 CLAUDE.md 标注并向用户说明。

## 1. 程序集与包结构

```
net.fosa.avatar-texture-optimizer/
├── package.json                     # vpmDependencies: ndmf(必装), 其余可选
├── Runtime/
│   ├── net.fosa.avatar-texture-optimizer.asmdef   # 无编辑器依赖
│   ├── Components/AtoAvatarRoot.cs                # Avatar 级组件（含全部设置）
│   └── Settings/*.cs                              # 质量预设/平台/压缩等纯数据
└── Editor/
    ├── net.fosa.avatar-texture-optimizer.editor.asmdef
    │   # references: Runtime, nadena.dev.ndmf.editor, Unity.Burst, Unity.Collections,
    │   #   Unity.Jobs, Unity.Mathematics
    │   # versionDefines: com.anatawa12.avatar-optimizer → ATO_AAO
    │   #                  com.vrchat.avatars            → ATO_VRCSDK3_AVATARS
    │   #                  jp.lilxyzw.liltoon            → ATO_LILTOON
    ├── AssemblyInfo.cs               # [assembly: ExportsPlugin(typeof(AtoPlugin))]
    ├── AtoPlugin.cs                  # 插件注册 + 相位约束 + 单 pass 调度 + 取消
    ├── Pipeline/*.cs                 # 流水线（见 §2）
    ├── Analysis/*.cs                 # 扫描/着色器/动画分析（§3）
    ├── Model/*.cs                    # 领域模型（UV组/类型组/岛/贴图槽…）
    ├── Processing/*.cs               # 质量缩放/装箱/合成/网格重写（§4–§8）
    ├── Import/*.cs                   # 导入参数应用（§9）
    ├── Reporting/*.cs                # [ATO] 日志 + NDMF 报告
    ├── Localization/*.cs + Resources/ATO/i18n/{en,zh-cn}.json
    └── API/*.cs                      # 扩展接口（§11）
```

依赖第三方包**不修改其源码**。AAO/VRC SDK 通过 `versionDefines` + `#if` 条件引用，保证未安装时编译通过、运行时走降级路径。

## 2. 相位与流水线

- 相位：`InPhase(BuildPhase.Optimizing).AfterPlugin("nadena.dev.modular-avatar").BeforePlugin("com.anatawa12.avatar-optimizer")`。约束为 WeakOrder（已读 NDMF Constraints.cs 源码），目标插件缺失时安全。另加 `.AfterPlugin("net.rs64.tex-trans-tool")`，TTT 同时安装时我方在其后运行（对其图集结果再处理），并在控制台提示。
- 全部逻辑集中为一个 `Pass<AtoBuildPass>`（暂不支持 ndmf 预览），内部按阶段推进：
  1. `Scan` 收集渲染器/材质/贴图引用/白名单/AAO 组件
  2. `AnalyzeAnimations` 动画与控制器解析（§3.4）
  3. `DedupeTextures` 贴图内容+导入设置去重（§10）
  4. `ExtractIslands` 岛提取/重叠合并/越界归一（§4）
  5. `ComputeQualityScale` 目标质量缩放（§5）
  6. `PackAtlases` UV组级装箱（§6）
  7. `ComposeAtlases` 图集合成 + padding + pull-push（§7）
  8. `RewriteMeshes` 网格/UV 重写 + AAO 疏散（§8）
  9. `UpdateReferences` 材质/贴图/动画曲线重映射（§8）
  10. `ApplyImportSettings` 导入参数（§9）
  11. `DedupeAssets` 材质/图集去重 + 材质槽合并（§10）
  12. `RemoveSelf` 移除 ATO 组件 + 总报告（§10）
- 取消：`AtoCancellation`（BuildContext State）；阶段间检查；取消即抛 `OperationCanceledException` → 终止 pass、释放 CPU/GPU/内存资源、保留磁盘临时资产（NDMF 会保留其 AssetContainer 文件）。
- 进度：`EditorUtility.DisplayCancelableProgressBar`（阶段级 + 子步骤级）。

## 3. 分析与领域模型

### 3.1 对象模型（Editor/Model/）

- `AtoTextureSlot`：一个材质属性引用一张贴图的一个"槽位"。字段：texture、material、propertyName、`TextureUsage`（Main/Normal/Mask/Tangent/…）、sRGB、filterMode、ST（scale/offset，须为 1/0）、引用材质集合（含动画切换材质）、透明模式集合（每个引用材质的 `_ZWrite`/queue/Cutoff/渲染模式）。
- `AtoUvGroup`：同一份 UV（一个 mesh+channel）对应的全部贴图槽集合（含动画切换进来的贴图）。属性：mesh、channel、islands、`AtoTypeGroup` 集合、动画缩放上限、形态键面积系数。
- `AtoIsland`：岛。属性：bbox（UV）、面积、三角形列表、光栅掩码缓存、每槽缩放因子（x,y）、纯色标记。
- `AtoTypeGroup`：类型组。key=`TextureUsage 签名`（按槽位类型组合）×色彩空间×filterMode。属于多个 UV 组；生成 0..n 张图集。
- `AtoTextureRecord`：被处理的贴图。原始纹理引用、像素缓存（可读副本）、导入设置、去重结果、白名单状态、处理结果（图集成员/整图缩放）。

### 3.2 渲染器与材质扫描

- 遍历 Avatar 全部 `SkinnedMeshRenderer`/`MeshRenderer`（跳过 EditorOnly 子树与白名单对象）。
- 处理前提（任一不满足 → 该贴图视作白名单处理，warning）：
  - 渲染器 `enabled` 或存在动画启用它（§3.4 判定）；
  - 材质属性引用 Texture2D；属性经网格 UV 采样（着色器分析判定）；无 ST 缩放/平移/旋转（含动画修改）；无特殊用途（贴花/Parallax 等排除属性）。
- 每个材质槽可被动画切换为多个材质 → 全部纳入同一 `AtoUvGroup` 的槽集合。
- 仅处理贴图与 UV：**绝不**修改材质中贴图以外的任何着色器参数。

### 3.3 着色器属性分析（ShaderAnalyzer）

- 通用机制：`Shader.FindPropertyIndex` + `Shader.GetPropertyAttributes`（读 `[Normal]`、`[NoScaleOffset]`、`[MainTexture]` 等特性）＋属性名/语义关键字（`_MainTex/_BaseMap`、`Mask`、`Bump/Normal`、`Metallic/Smoothness/Occlusion/AO`、`Parallax/Height`、`Ramp`、`Decal`）＋材质 keywords（启用状态）＋采样器 UV 输入（`GetPropertyAttributes` 不含此信息时按属性名/已知着色器推断）。
- liltoon 特化（`ATO_LILTOON` + 名称前缀检测）：内置 2.3.4 属性表知识（见 CLAUDE.md），未来版本靠通用机制兜底。
- 结果：每个材质属性的 `TextureUsage` 分类 + 是否 `NoScaleOffset` + 使用 UV 通道（uv0/uv1…按关键字/名称推断；推断不出 → 保守：仅当所有候选通道一致时处理，否则白名单）。
- 无法分类 → 白名单 + warning（保守安全）。

### 3.4 动画分析（AnimatorScanner）

自实现解析器（参照 AAO AnimatorParserV2 / MA AnimationDatabase 的设计，代码自写）：

- 收集：`VRCAvatarDescriptor` 的 base+special layers（`ATO_VRCSDK3_AVATARS`；未装 SDK 则收集场景内全部 Animator）；`Animator.runtimeAnimatorController` 递归展开（AnimatorOverrideController→base，state/substate machine/blend tree 的全部 motion）；直接 `Animation` 组件及其 clips。
- 曲线分析（AnimationUtility.GetCurveBindings + 对象引用曲线 + 通用/PPtr 曲线）：
  - **材质切换**：`m_Materials.Array.data[i]` 的引用值 → 槽 i 的候选材质集合（去重）。
  - **贴图切换**：`material._XXX` 对象引用曲线 → 槽位候选贴图集合。
  - **ST/变换**：`material._XXX_ST.x/.y/.z/.w` 曲线存在 → 该槽白名单。
  - **渲染器/物体启用**：`m_Enabled`/`m_IsActive` 曲线 → 判定"被动画启用"；初始值为 false 且动画从不置 true → 跳过渲染器。
  - **物体缩放**：本物体及祖先的 `m_LocalScale.x/y/z` 曲线 → 每轴最大 |值|；非均匀取每轴最大值独立评估（保守）。
  - **渲染模式/Cutoff**：`_Cutoff`、keywords 曲线 → 透明模式集合与 Cutoff 取值域（质量评估取最严苛）。
- 表达参数默认值：读取 `VRCExpressionParameters`（SDK 存在时）与 animator 各层初始状态；无法确定默认值的参数按"最不利"处理。
- 形态键：对每岛，取形态键 0 与 100 两帧的面积最大值（`GetBlendShapeFrameVertices`），不枚举组合/负数/超 100。

### 3.5 白名单（Whitelist）

- 用户配置：不限对象类型（网格/材质/贴图/动画等）。
- 语义：白名单对象引用的贴图 → 跳过一切优化（含导入参数）；同 UV 的其他贴图 → 跳过图集化（不剔除/不重排/不换 UV），但参与整图缩放与导入参数优化。
- 不合规贴图视作白名单处理（同语义）。
- 贴图去重时若任一同义贴图在白名单 → 去重结果整体视为白名单。

## 4. 岛提取（ExtractIslands）

- 逐 mesh×channel：并查集按"共享边两端点 UV 相同"合并三角形 → 岛。顶点无需分裂（每顶点每通道仅属一岛）。
- 同贴图内重叠岛合并：按光栅掩码（4px 粒度）连通性合并；UV 组内**任一**槽重叠即整组合并（跨槽取并集掩码）。
- 越界归一：岛 bbox 超出 [0,1] 时，若存在整数平移使 bbox 落入 [0,1] 且不跨 wrap 缝 → 网格 UV 平移归一（纹理映射不变）；否则白名单 + warning。
- 多通道 UV：每通道独立成岛集合与 UV 组（`AtoUvGroup` 以 mesh+channel 为单位），类型组共享。
- 世界尺寸：`islandWorldSize = uvBBoxSize × maxObjectScale × blendShapeFactor`；maxObjectScale 取动画/形态键最大面积（§3.4）。

## 5. 目标质量算法（QualityScale）

### 5.1 指标实现（Burst，线性空间）

- 重采样：线性空间；透明贴图预乘 alpha 下采样；法线正确解码（DXT5nm→切线向量）→ 重采样 → 重归一化 → 编码。
- 评估：缩小后岛覆盖区 → 双线性上采样回原尺寸 → 与原图比较。
- 指标：
  - 不透明/彩色：MS-SSIM（包围盒短边 <176px 退单尺度 SSIM；<11px 跳过）＋ ΔE00 均值；
  - alpha：Cutout → 按每个引用材质的模式+Cutoff clip 后轮廓 IoU；Blend → 线性 RMSE；多材质逐一遍历取最严；
  - 法线：角度误差均值 + p95；
  - 灰度：仅使用通道，线性 RMSE 逐通道取最差；
  - 岛外像素不参与比较（掩码）。
- GPU 批量：光栅化用 GPUMeshAPI（`AtoGpuUtility` 单点封装，含 CPU 回退）；指标计算 Burst 并行 job。

### 5.2 缩放搜索

- 每岛每槽独立评估指标，UV 组木桶效应：`s_uv = min over slots of s_slot`（再受 §5.3 钳制），结果尺寸 ≤ UV 组内最大原尺寸（不上采样）。
- 二分搜索：对候选 s，逐槽重建缩小覆盖区→上采样→比较，全部达标才通过。
- 各向异性：先均匀 s 达标，再双轴独立二分细化（a_x,a_y ∈ [s,1]），每步重评全部指标。
- 纯色岛（质量<1）：直接 min(4, 短边)。
- 目标质量=1（近无损挡位）：跳过该贴图类型岛的缩放（含纯色），原样拷贝不重采样。
- 整图缩放模式（不生成图集）：s_tex = 所有岛需求的最小值（最严），整贴图重采样。

### 5.3 像素密度带（解读选择，已向用户说明）

- 密度 d = 贴图像素 / 世界尺寸（px/m，逐轴）。
- 最终 s_final = clamp(s_quality, Dmin/d, 1) —— Dmin 硬下钳防糊（默认 2048px/m）。
- 若 s_quality > Dmax/d（密度超过 Dmax，默认 4096px/m）→ 不强制压缩，仅控制台告警（质量优先）。
- 挡位选项：512/1024/2048/4096/8192（min/max 各一）。
- 同时受"岛在原贴图物理文件上的真实大小"钳制（s≤1 恒成立）。

## 6. 装箱（AtlasPacker）—— UV 组级装箱（核心修正）

**不变式：同一 UV 在所有类型组图集中的岛原点（UV 归一化坐标）完全一致；类型组图集只决定岛尺寸。** 因此：

1. 每岛在每个类型组的像素尺寸来自 §5 缩放结果（每类型组可以不同）；代理形状 = 各类型组中逐轴最大值（旋转后尺寸也取最大），用于碰撞。
2. 以 UV 组为单位：其全部岛按代理形状 BLF 装箱（面积降序、边长降序、90° 旋转步进）到"代理图集"（候选池挑选，见下），产生岛原点（UV 空间归一化）。
3. 各类型组图集 = 该组全部成员岛的像素矩形（原点 × 该组图集边长 + 该组缩放后像素尺寸）；由于代理 ≥ 各组成员的实际尺寸，不会碰撞。类型组图集边长 S_g = 候选池中 ≥ maxExtent_g + 2·pad_g 的最小边长。
4. 候选池：POT（默认）：边长 ∈ {64..8192 的 2^n}（移动端上限 4096），允许非正方形（长/短比升序优先）；实验性 NPOT 选项：64 步进（同样上限），勾选时剔除不支持的压缩格式（如 iOS PVRTC）。选代理图集：面积 ≥ 全部岛代理面积的最小候选开始，按面积升序、长/短比升序尝试，第一个装下全部岛者即成品。
5. 装不下：UV 组（原子）移入新队列/复用同类队列；单 UV 组超出最大图集 → 放弃该组图集化（按质量缩放后走整图路径）+ warning。
6. 光栅化：Burst 位掩码（4px 粒度）+ 全扫描 BLF；旋转 = 位掩码转置 + 内容同步旋转；含切线数据（AnisotropyTangentMap）的类型组禁用旋转（位掩码转置禁用），保持切线原样、绝不重算。
7. padding：pad = ceil(代理图集最大边长/128)，下钳 4；用户最小 padding 选项 {4,8,16,32,64} 默认 4（pad = max(上式, 用户值)）。代理装箱按 max(各类型组 pad 的 UV 归一化值) 保守膨胀。

## 7. 图集合成（AtlasCompositor）

- 每类型组图集：对每个岛，按缩放后像素尺寸从源贴图重采样（线性、预乘 alpha、法线按 §5.1 处理）写入图集矩形（按旋转步进转置写入）；UV 不变（重采样区域即原岛区域）。
- pull-push 无限外扩：岛边缘颜色迭代膨胀填满图集空白（Burst 八邻域迭代）；透明贴图 alpha 保持 0（RGB 外扩、A 恒 0，渗色已知可接受）。
- 图集命名 `ATO_` 前缀；保存为 PNG（8bit）或 EXR（HDR 源）到 NDMF AssetContainer，再按 §9 设导入参数并 reimport。
- 内存：岛像素缓存于掩码缓存，图集逐张生成后释放源缓存；GPU RenderTexture 复用池。

## 8. 网格重写与引用更新

- 新网格：克隆原网格（顶点/骨骼/权重/形态键/法线/切线全部保留），仅按 §4/§6 重写各通道 UV（岛原点 → 新 UV 矩形）。形态键、边界盒等不变。替换 `sharedMesh`。
- AAO 疏散（`#if ATO_AAO`）：对我改写的每个 SMR 通道 k：若 `UVUsageCompabilityAPI.IsTexCoordUsed(renderer,k)` → 选 saved 通道（未被任何材质采样、未被 AAO 使用、<8）→ 新网格中把原 UV 拷入 saved 通道 → `RegisterTexCoordEvacuation(renderer,k,saved)`。选不出 → 该渲染器整体白名单化 + warning。**注意：必须先拷 UV 数据再注册**（AAO EvacuateProcessor 会在其处理期交换）。未装 AAO：跳过（无消费者）。
- 材质/贴图引用更新：渲染器 materials 数组、动画对象引用曲线的值、动画 `m_Materials.Array.data[i]` 引用 → 全部通过自实现 `ReferenceRemapper` 更新（**ObjectRegistry.RegisterReplacedObject 须先于对新对象的 GetReference**）。
- 动画曲线重映射规则（参照 AAO ObjectMapping 机制自实现）：
  - 对象引用曲线（含 PPtr 曲线）值 ∈ 旧对象集合 → 换新对象；
  - `m_Materials.Array.data[i]` 绑定 → 槽合并后索引平移；
  - `material._XXX` 绑定 → 材质合并后改绑新材质对象（属性名不变）；`_MainTex` 类贴图属性绑定 → 值换图集。
- 确保"任意选项组合下材质表现一致"：存在透明度 → 图集必带 alpha；灰度单通道格式仅纯单通道使用时启用；任何不安全组合走 fallback（§10.5）。

## 9. 导入参数（ImportSettingsApplier）

- 所有非白名单贴图：Mipmap+MipStreaming 绑定单开关（VRC 要求二者同开同关）；图集强制 Clamp + Read/Write 关；其余参数取所有来源贴图中质量最高者。
- 压缩格式：按 透明/不透明/法线/灰度 分类 × 平台（PC/Android/iOS）提供安全枚举（随 liltoon 关键字与像素实际内容兜底；实验性 NPOT 时剔除不支持的格式）。
- 平台 override：通用参数默认折叠；勾选对应平台才显示并生效；默认值读取当前构建平台（`EditorUserBuildSettings.activeBuildTarget`）。

## 10. 去重、合并与收尾

- 贴图去重（阶段 3）：按实际像素内容 + 导入设置（不同即视为不同）分组去重，更新全部引用；任一同义贴图在白名单 → 结果整体白名单。
- 材质去重：优化后内容+参数完全相同、且动画中不存在单独切换/单独属性动画 → 合并并更新引用。
- 贴图/图集去重：内容+参数完全相同 → 合并引用。
- 材质槽合并：同网格内不透明材质完全相同且动画无独立槽切换 → 合并子网格与材质槽，动画 `m_Materials.Array.data[i]` 索引平移重映射。
- 自移除：pass 末尾销毁 Avatar 克隆上的 ATO 组件。
- 报告：NDMF 控制台输出总体结果（ErrorReport Information），细节折叠；日志含每步耗时/图集来源/岛数/大小/利用率/相对原贴图优化量。

## 11. 扩展 API（Editor/API/）

- `AtoTextureUsageProvider`（纹理分类扩展）、`AtoQualityMetricProvider`（自定义质量指标）、`AtoPackingProvider`（自定义装箱器）——接口 + 静态注册表 + 程序集特性扫描；默认实现即本文档算法。
- i18n：JSON 配置（`Resources/ATO/i18n/{code}.json`，当前 en/zh-cn）；选项 Auto=读取 `nadena.dev.ndmf.ui.LanguagePrefs.Language`，无对应翻译回退英文；同时 `LanguagePrefs.RegisterLanguage` 注册。

## 12. 安全与回退总则

- 一切不确定场景 → 白名单处理（跳过优化）＋ warning，绝不产出错误结果。
- 处理只影响：网格 UV、贴图引用、贴图资产、材质槽（受限）；材质其他属性永不修改。
- 全程进度可取消、内存受限、无泄漏（逐阶段释放 GPU/CPU 资源）。
