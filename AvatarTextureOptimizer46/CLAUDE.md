# CLAUDE.md — AvatarTextureOptimizer 项目记忆

> 本文件是本项目**唯一**的记忆载体。每次修改后必须更新。
> This file is the single source of project memory. Update it after every change.

## 1. 项目信息

- 项目名：AvatarTextureOptimizer
- 包名：`net.fosa.avatar-texture-optimizer`
- 目标：全世界最好的 VRChat 贴图优化工具（NDMF 非破坏性）
- 语言：代码注释与 XML 文档**英文 + 中文双语**
- 交流语言：简体中文

## 2. 依赖版本（已完整下载并阅读源码，位于 refs/，不随交付物打包）

| 包 | 版本 |
|---|---|
| com.vrchat.base | 3.10.4 |
| com.vrchat.avatars | 3.10.4 |
| nadena.dev.ndmf | 1.14.4 |
| nadena.dev.modular-avatar | 1.18.2 |
| com.anatawa12.avatar-optimizer | 1.9.17 |
| jp.lilxyzw.liltoon | 2.3.4 |
| avatar-compressor | 0.9.0 |
| light-limit-changer | 2.13.0 |

## 3. 已取证的关键事实（**不要再猜，直接引用**）

### NDMF 1.14.4
- `BuildPhase.Optimizing` 在 `Transforming` 之后 → MA 已执行完。
- AAO 插件 QualifiedName = `com.anatawa12.avatar-optimizer`，主序列在 `BuildPhase.Optimizing`
  （`Editor/OptimizerPlugin.cs:60`）→ 用 `.BeforePlugin("com.anatawa12.avatar-optimizer")` 排序。
- `AnimatorServicesContext`（命名空间 `nadena.dev.ndmf.animator`）提供
  `ControllerContext.GetAllControllers()`、`AnimationIndex.RewriteObjectCurves()`、`ObjectPathRemapper`。
- 枚举所有 clip：`controller.AllReachableNodes()` 过滤 `VirtualClip`（`VirtualNode.cs:98`）。
- 本地化：`nadena.dev.ndmf.localization.Localizer(string defaultLanguage, Func<List<(string, Func<string,string>)>>)`，
  语言由 `LanguagePrefs.Language` 决定。
- 错误：继承 `SimpleError`，`ErrorReport.ReportError(IError)`。
- 资产保存：`ctx.AssetSaver.SaveAsset(obj)`。

### AAO 1.9.17
- `API-Editor/UVUsageCompabilityAPI.cs`：**只接受 `SkinnedMeshRenderer`**。
  `RegisterTexCoordEvacuation` 在 savedChannel 被占用时抛 `InvalidOperationException`。
- asmdef `com.anatawa12.avatar-optimizer.api.editor` 的 `autoReferenced: false`
  → **不要在 asmdef 里直接引用**，我们用反射（`Interop/AaoInterop.cs`）。

### lilToon 2.3.4（全部来自源码行号）
- `lil_common_functions.hlsl` `lilCalcDoubleSideUV`：`_ShiftBackfaceUV` 时背面 UV **+1.0**，依赖 repeat
  → 整材质白名单。
- `lil_common_macro.hlsl:272` `LIL_SAMPLE_2D_ST(tex,samp,uv) = tex2D(tex, uv*tex##_ST.xy+tex##_ST.zw)`
  → 副贴图有**各自的 `_ST`**，必须逐属性校验。
- `lil_common_frag.hlsl:746` `_Main2ndTex_UVMode == 4 → fd.uvMat`；`:1825` `_EmissionMap_UVMode == 4 → fd.uvRim`
  → 模式 4 不可图集化。
- `lilParallax` / `lilPOM`（`_UseParallax` / `_UsePOM`）位移 uvMain → 整材质白名单。
- 使用 `fd.uvMain`（= UV0 经 `_MainTex_ST`）的安全属性清单已固化在
  `Analysis/LilToonShaderAnalyzer.cs` 的 `MainUvTextures`（逐条 grep 源码得出）。
- 非网格 UV：`_MatCapTex`、`_MatCap2ndTex`、`_DitherTex`、`_MainGradationTex`、`_EmissionGradTex`、
  `_AudioLink*`、`_GlitterColorTex`、`_ParallaxMap`、`_OutlineVectorTex`、`_FurVectorTex`、`_Dissolve*`。

## 4. 已向用户指出并修正的**设计问题**

1. AAO API 只支持 SMR，MeshRenderer 无法 evacuate → 已处理并记录日志。
2. 8 个 UV 通道全满时 evacuate 失败 → 报 warning 并保留原状。
3. 用户的“越界跨缝”判定抓不到 `_ShiftBackfaceUV`（运行时偏移）→ 单独检测。
4. 用户只提了 ST，未提 `_ScrollRotate`/`Angle`/`POM`/`UVMode` → 全部纳入检测。
5. 用户的 UV 组只约束“同 UV 的不同贴图”，未约束“同贴图被不同 UV 通道采样”
   → 新增 `ConflictingUVChannels` 白名单规则。
6. **模型修正**：同一 UV 组的所有岛必须整组落在**同一张图集**（材质槽只能指向一张贴图）；
   跨组共享图集必须按“贴图类型组”（kind + 色彩空间 + filterMode 签名）划分。
   已据此重写 `Plugin/AtoAtlasPipeline.cs`。

## 5. 目录结构

```
Packages/net.fosa.avatar-texture-optimizer/
  package.json
  Runtime/    AtoEnums / AtoQuality / AtoSettings / AvatarTextureOptimizer(组件)
  Editor/
    Api/          AtoShaderApi.cs          — 第三方扩展点 IAtoShaderAnalyzer
    Core/         AtoLog / AtoError / AtoProgress(可取消)
    Localization/ AtoLocalizer / AtoMiniJson
    Analysis/     ShaderAnalysisUtil / LilToonShaderAnalyzer / StandardShaderAnalyzer /
                  ShaderAnalysisService / WhitelistResolver / AnimationAnalyzer
    Model/        AtoModel.cs — UvSlot / TextureUsage / TextureEntry / UvGroup / UvIsland / AtlasResult
    Textures/     GpuTextureUtil / TextureProbe / TextureDeduplicator
    Meshes/       IslandRasterizer(Burst) / UvIslandBuilder / MeshGeometry
    Quality/      ColorMath(CIEDE2000) / QualityMetrics(MS-SSIM 等, Burst) / IslandQualitySolver
    Packing/      AtlasCandidatePool / BitmaskPacker
    Atlas/        AtlasComposer(pull-push) / TextureFormatResolver / EditorTextureCompressor
    Apply/        AtoApplier / MeshUvRewriter(顶点拆分)
    Interop/      AaoInterop(反射)
    Plugin/       AtoPlugin / AtoCollector / AtoGrouping / AtoAtlasPipeline /
                  WholeTextureScaler / AtoBuildReport
    UI/           AvatarTextureOptimizerEditor
    Shaders/      ATO_Copy / ATO_PremultiplyAlpha / ATO_PullPush / ATO_IslandBlit
    Resources/i18n/ en-US.json / zh-Hans.json（70 键，完全对齐）
```

## 6. 当前进度

### 校验能力（**本轮新增，非常重要**）

沙箱内已搭好离线编译 + 单元测试：`Tools~/OfflineVerify/verify.sh`

- 用 **真实 Unity 参考程序集**（NuGet `UnityEngine.Modules 2021.3.33` + `Unity3D.SDK 2021.1.14.1`
  的 `UnityEditor.dll`）+ **真实 NDMF 1.14.4 源码** + `com.unity.mathematics 1.2.6`
  + `com.unity.burst 1.8.7` 源码 一起编译。
- 因此**所有 NDMF / Unity API 调用都被真正类型检查过**，不是对着手写桩检查。
- 当前状态：**ATO 包内 0 error / 0 warning**。
- NDMF 与 Burst 自身会报 ~20 个错误，全部是 Unity 2022 专有 API 在 2021 参考程序集中不存在
  （`AnimatorControllerParameter.name` setter、`ObjectChangeKind.ChangeChildrenOrder`、
  `EditorApplication.isFocused`、`TreeView.SetRootItems`、`NativeQueue<>` 等）。脚本只统计 ATO 包内错误。
- 单元测试（`Tools~/OfflineVerify/Tests`）**39 项全部通过**：
  - CIEDE2000 对照 Sharma/Wu/Dalal (2005) 官方 21 组验证数据，全部吻合到 4 位小数
  - 位掩码形状装箱：凹槽嵌套（矩形装箱做不到）、90 度旋转、padding 分隔、快照回滚、拒绝重叠
  - UV 映射：非旋转与旋转两种情形的角点round-trip、面积守恒；
    旋转约定与 `Hidden/ATO/IslandBlit` 的 `uv' = (v, 1-u)` 严格对应
  - 候选图集池：排序、POT/NPOT 约束、padding 规则

**注意**：Docker/沙箱里的 dotnet 装在 `~/.cache/dotnet`（被快照排除），换环境需重装。

### 已完成
- 全部依赖源码阅读与取证（见第 3 节）
- 运行时设置模型 + 质量挡位（含学术依据）
- 日志 / 进度 / 取消 / 错误报告 / i18n（JSON 可扩展，Auto 跟随 NDMF）
- 着色器分析（lilToon + 标准着色器 + 未知着色器安全回退）
- 动画分析（材质切换、贴图切换、_ST/滚动/UVMode、Cutoff、缩放、启用状态）
- 白名单解析（任意对象类型，`EditorUtility.CollectDependencies`）
- 贴图去重（GPU 解码 + 导入设置签名 + SHA256）
- GPU 贴图 IO（不需要 Read/Write、线性空间、预乘 alpha 降采样）
- Burst 保守光栅化 + 连通分量岛提取（自动合并重叠岛）
- 质量度量：MS-SSIM / SSIM / CIEDE2000 / 轮廓 IoU / alpha RMSE / 法线角度 p95 / 逐通道 RMSE
- 岛缩放二分搜索（先均匀后双轴）+ 像素密度钳制 + 纯色短路 + 无损跳过
  + **各向异性组合的最终验证与回退**（本轮新增：两轴是先后细化的，组合从未被整体评估过，
    现在会验证并在失败时朝已知可通过的均匀解二分回退）
- 候选图集池（POT / 实验性 NPOT）+ padding 规则
- 位掩码 BLF 形状装箱 + 90 度旋转（掩码转置）+ 整组原子放置 + 快照回滚
- 贴图类型组划分与跨组共享图集
- 图集合成 + GPU pull-push 无限外扩
- 压缩格式解析（按平台/分类，含不安全组合的自动升级与警告）
- 网格 UV 重写（含顶点拆分、形态键/骨骼权重/多 UV 通道随动）
- 材质克隆 + 动画曲线重写
- AAO UVUsageCompabilityAPI 反射集成
- NDMF 插件与 Pass（Optimizing 阶段，BeforePlugin AAO）
- 组件校验（唯一性、必须在 Descriptor/根物体上）+ 构建后自我移除
- 无图集路径（整图缩放）
- 构建报告（总体 + 可折叠细节）
- README.md（双语）

### 本轮补齐的待办
1. **离线编译校验**（见上）——原第 1 条。
2. **优化后的材质/贴图去重**（`Dedup/FinalDeduplicator.cs` + `Dedup/MaterialSignature.cs`）：
   - 生成图集按 `尺寸|格式|采样状态|FNV-1a(解码像素)` 分桶合并
   - 材质按完整内容签名（着色器 + 全部声明属性 + 关键字集合 + renderQueue + GI + 标签）合并
   - **只处理 `AssetSaver.IsTemporaryAsset` 为真的资产**，绝不改动用户的原始资产
   - 材质槽合并：仅当材质相同**且不透明**、且该渲染器没有任何动画切换材质槽时才合并子网格；
     合并后重写 `m_Materials.Array.data[N]` 动画绑定索引
   - 结果计入 NDMF 报告
3. **材质槽 → 子网格索引**：核实过 Unity 语义——材质多于子网格时，多余材质重复渲染**最后一个**
   子网格，因此 `min(slot, subMeshCount-1)` 本来就是正确的。已补注释与日志，不是 bug。
4. **形态键最大面积**：`MeshGeometry.TriangleMaxWorldAreas` 改为**逐三角形**比较基础姿态与
   每个形态键权重 100 时的面积并取最大；并跳过不影响该子网格的形态键。
   （旧的 `MaxAreaVertices` 逐顶点近似已删除）
5. **各向异性细化**：见上，已加最终验证 + 二分回退。
6. **内存上限控制**：新增 `Textures/LinearSourceCache.cs`，有字节预算的 LRU + pin/unpin。
   求解阶段固定一整组、合成阶段只固定一张。峰值由最大单组决定，不再是整个 Avatar。
   报告会打印峰值 MB 与重新解码次数。
7. **顺带修掉的真实 Bug**：
   - `MeshUvRewriter` 原来用 `mesh.Clear(false)` 增长顶点缓冲，会**丢失未参与重写的子网格三角形**，
     且 bindposes 有风险。改为不 Clear、先清空索引缓冲再增长各数据流，并重新上传**所有**子网格。
   - `VertexSplitter.Resolve` 原来用 `_map.Keys.All(...)` 查询，复杂度是顶点数的**平方**。
     改为 `HashSet<int>`，O(1)。
   - `AtlasComposer.Compose` 局部变量 `target` 与参数同名（CS0136，编译不过）。已改名。
   - 抽出 `Apply/AtlasUvMapping.cs` 作为无依赖的纯函数，便于单元测试旋转约定。

### 仍未完成 / 已知限制
1. **没有在真实 Unity 里跑过**。离线校验覆盖不到：GPU blit / pull-push、网格手术的实际结果、
   NDMF pass 的运行时行为、压缩格式在各平台的实际产出。**必须靠烘焙真实 Avatar 验证**。
2. **同一网格多 UV 通道**时，第二个通道的重写会读到第一个通道已复制出的顶点。逻辑上正确
   （复制顶点的 UV 是从源顶点拷贝的），但这条路径没有测试覆盖，属于低频场景，需重点观察。
3. **NDMF 预览**：按需求不支持，无需实现。
4. UI 用 IMGUI 而非 UIElements，因此没有接 NDMF 的 `Localizer.LocalizeUIElements`；
   本地化走自己的 `AtoLocalizer.Tr`，功能等价。
5. `TextureDeduplicator`（前置去重）对每张贴图做全分辨率 SHA256 回读，8K 贴图较慢。
   若实测太慢，可改为先按 256x256 缩略图分桶再做全分辨率确认。

## 7. 注意事项

- **绝不修改材质除贴图以外的任何参数**。这是本工具的核心承诺，任何改动都不得违反。
- 修改前先读代码、先取证，不要根据表现猜结论。
- 所有 `RenderTexture` 必须走 `GpuTextureUtil.GetTemp/Release`，成对释放；
  取消（`AtoCancelledException`）时靠 Pass 里的 try/finally 兜底。
- 生成的图集名一律以 `ATO_` 开头。
- 图集强制 `Clamp` + 关闭 Read/Write（`Texture2D` 默认不可读，不额外开启）。
- 工具处于开发阶段，序列化字段可随意改，无需版本兼容。
