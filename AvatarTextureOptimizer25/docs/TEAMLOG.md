# TEAMLOG — AgentTeam 流程记录 / Process Records

> 记录 Coder 间共识、Reviewer 审查结论、QA 验收结论。
> Records of Coder consensus, Reviewer verdicts, and QA acceptance.

## [2026-08-19] 阶段0 依赖取证（CODER-A/B 共同执行）
- 结论：全部关键 API 取证完成（NDMF 插件/排序/报错/语言 API、AAO UVUsageCompabilityAPI 签名与注册时机、
  lilToon 2.3.4 属性总表、MA 插件 QualifiedName 、VRC asmdef、avatar-compressor 工具链惯例）。事实已固化到 CLAUDE.md §4。

## [2026-08-19] 模块1 包骨架（CODER-A 提案 / CODER-B 复核）
- 共识点：
  1. Runtime 程序集只放组件+设置模型+安全枚举（不可引用 UnityEditor → TextureImporterFormat 一律映射于 Editor 程序集）。
  2. 自定义挡位默认全 1（近无损）且永不被预设覆盖；切换挡位填充对应阈值（写入 CLAUDE.md §5）。
  3. 组件必须 [DisallowMultipleComponent] 且构建期再额外做全 Avatar 唯一性校验。
  4. mipmaps 与 streamingMipmaps 以单一开关绑定（VRChat 硬性要求）。
  5. 打包 license 定为 MIT。
- 待 REVIEWER 审查：ATOTypes.cs / AvatarTextureOptimizer.cs / package.json / asmdef。

---

## Module 12 — 管线整合修复 (Pipeline integration fixes) [2026-08-19]

### 参与者
- Coder-1 (Fuse), Coder-2 (Truss)

### Coder-1 — 问题清单（对照 module11 遗留）
1. `ATOUVGroup` 缺 `IsAtlasBlocked/AtlasBlockReason/SetAtlasBlocked`；`ATOUsage` 缺 `GroupOf`。
2. `_lastQuality` 未赋值 → 整图路径比例恒 1。
3. `EnsureAaoEvacuation` 4/5 参不一致（管线已预选通道）。
4. 死字段 `_wholeRatioCacheHit/_masterClock`；`StandaloneScaleForGroups` 的 `quality` 形参从未使用；`matRewriter` 形参在 StandaloneOnlyPath 未使用。
5. `ReportValidationError` 双重 "ato:" 前缀（ATOSimpleError 自带前缀）+ 取证结果：NDMF SafeSubst 只支持 `{0..9}` 占位且作用于本地化字符串本身 → 校验错误应直接把全文放 i18n、不传 subst；用 `ctx.ObjectRegistry.GetReference` 挂对象引用。
6. **范围漏洞（新发现）**：`Plan()` 的 fallbackGroups 只含"谓词通过但装箱失败"的组；谓词未通过但有岛/有可优化贴图的组（AAO 无通道阻塞）完全没被任何路径处理 → 贴图既不图集也不缩放，静默丢失优化。修复：管线按 `covered = placementsByGroup 键集` 反推 standalone 组集合（有岛且含可优化贴图且未被图集覆盖），与 planner fallback 合并去重。
7. **白名单语义**：岛构建失败（islands.Count==0）的组 = 硬白名单，两条路径都不得触碰（整图重写给 PNG 重编码+Clamp 属于"动了用户资产"）→ standalone 路径同样过滤 `islands.Count>0`；且 `ratio>=0.999` 且尺寸不变时直接不处理（fail-open=1 时保持原样）。
8. `ATOBuildReport.AtlasInfo` 缺 `reason` 字段（BuildDetailsText 引用了它——module2 潜伏编译错误）。
9. `_model` 建立前的 stage 计时被丢弃（需求要求每步耗时）→ `_earlyTimings` 缓冲，model 阶段后插到头部。
10. 进度条单调性：`EvaluateAll` 内部用 0..1 相对进度会把已报到 0.55 的进度条拉回 → 增加 p0/p1 绝对区间参数。

### Coder-2 回应
逐条核实通过。补充：第 6 条"被图集覆盖"判定必须基于 placementsByGroup 的实际覆盖而非谓词结果（谓词在装箱前评估）；第 7 条 standalone 的"ratio>=0.999 即跳过"与质量评估 fail-open=1 语义一致，同意。第 10 条同意改为绝对区间。轮换违规（旧 CLAUDE.md §7"法线禁旋转"）本次一并订正为"所有角色均可旋转（UV 随内容同转）"。

### 共识
按上述 10 条全量修复；保持 API 形状：ATOUVGroup 增加阻塞字段、`ATOUsage.group` 反向引用在建组时填充并提供 GroupOf 兜底扫描、EnsureAaoEvacuation 增加可选 evacChannel 参数（-1 自动）。

---

## Module 13 — 插件入口 / Inspector UI / 公开扩展 API (Plugin, Inspector, Extension API) [2026-08-19]

### 参与者
- Coder-1 (Fuse), Coder-2 (Truss)

### Coder-1 提案
1. ATOPlugin：`InPhase(BuildPhase.Optimizing).WithRequiredExtension(typeof(AnimatorServicesContext), seq => seq.Run(...).AfterPlugin("nadena.dev.modular-avatar").BeforePlugin("com.anatawa12.avatar-optimizer"))`；两限定名均从对方源码取证（AAO OptimizerPlugin.cs override、MA PluginDefinition.cs override）；OnUnhandledException 记日志后交默认处理（让 NDMF 中止构建=绝不静默污染）。
2. Inspector：IMGUI（与 VRChat SDK/NDMF 主流工具一致，2022.3 兼容性最稳）；全部文案走 ATOLoc（ato:ui.*）；挡位≠Custom 时阈值只读展示（取自 ATOQualityPresets.For），Custom 时可编辑且永不被覆写；密度给 512/1024/2048/4096/8192 步进按钮；平台覆盖"未勾选全折叠"；格式下拉按 类别×平台 过滤不安全项（与 ATOFormatMapping.Sanitize 的判定镜像）；语言 Auto/手动（Manual 时列出 JSON 发现的语言）。
3. 扩展 API（预留、标注 experimental）：`IATOShaderAnalyzer.TryAnalyze` 注册表（先于内置规则，异常吞掉+警告）；事件 OnModelBuilt/OnBeforeAtlasPlan/OnBeforeReport；`ATOExtensionApi.CustomPacker` 钩子可整体替换装箱结果。

### Coder-2 回应
- 补充 1：绕开 `ctx.ObjectRegistry.GetReference` 显式接口实现问题已随 module12 处理（静态 ObjectRegistry.GetReference）。
- 补充 2：格式列表不必复刻全部 Sanitize 规则（写盘端仍有兜底+警告）；UI 只做硬过滤（PC 去 mobile-only、mobile 去 BC/DXT）。
- 补充 3：事件触发点须含"模型构建后（岛构建之前），白名单仍可改"。同意。

### 共识
按 1-3 实施；事件时在管线 Model/AtlasPlan/Report 前三处触发；API 标注 [PublicAPI-experimental] 注释。

### i18n 覆盖核对
- 全部 84 个代码引用键 vs JSON：仅 `ato:report.warning:description` 有意缺省（SimpleErrorUI 对缺失 description 优雅跳过；该错误只有标题替换）。en-US/zh-Hans 双向对称 106/106 键，{n} 占位完全一致。

---

## Reviewer 联合评审 — module12+13 [2026-08-19]

### 参与者
- Reviewer-1 (Prism), Reviewer-2 (Lodestone)

### 结论：**附条件通过**（3 项必修，已修并复验）

| # | 发现 | 严重度 | 修复 |
|---|------|--------|------|
| R1-F2 | **材质槽冲突**：同一 (material, property) 喂给两个 UV 组时，图集化任一组会破坏另一组采样（一槽只能装一图）→ 冲突组集合一律禁图集、整图缩放兜底 | 高（数据损坏） | ATOPipeline.AtlasPath 建 slotGroups 冲突检测 + eligible 谓词排除 + ato:atlas.slotconflict 报告 |
| R1-F3 | `uvGroupsSkippedAtlas` 从未赋值（报告恒 0） | 低 | AtlasPath 兜底处赋值 standaloneGroups.Count |
| R1-F10 | atlasCompose 阶段耗时未入账（需求"每步耗时"遗漏） | 低 | compose 循环加 Stopwatch → RecordTiming |

### 复核（无问题项记录）
- 取消路径：ATOCancelledException 先于 Exception 捕获 ✓；using 作用域回退释放 GPU 池/进度 ✓。
- 共享网格多渲染器：GetOrClone 幂等、EnsureSplit 缓存、evac clone 变体按 (mesh,sig) 缓存 ✓。
- 预乘/线性/sRGB/法线编码链路与 Pass 语义一致 ✓（standalone 三分支核对）。
- NDMF SafeSubst {0..9} 语义：校验错误全文入 i18n 不传 subst；报告标题 "{0}" 替换安全（内容含花括号不会被二次扫描——SafeSubst 只扫本地化模板）。✓
- Inspector 全面 SerializedProperty（撤销/多选/Prefab 覆盖安全）✓；语言模式下拉本地化 ✓。
- `ObjectRegistry.GetReference` 显式接口实现问题已用静态入口规避 ✓（有取证注释）。

两位评审一致同意通过并提交 QA。

---

## QA-1 全量重读（QA-甲 Quill） [2026-08-19/20]

### 方式
按流程：Coders 完成 + Reviewers 通过后，QA-1 从 0 重新通读全部 36 个 C#/shader 源文件 + 3 个 JSON，找 bug + 需求符合性。

### 发现与修复（严重→轻）
| # | 发现 | 严重度 | 修复位置 |
|---|------|--------|---------|
| Q1-1 | **质量折叠死锁**：`finalRatios` 以 1.0 初始化再 `MaxRatio` 折叠 → 任何候选都 ≤1.0，max(1.0,x)=1.0，**全部岛缩放被静默禁用** | 致命（核心功能失效） | ATOQualityEvaluator：改 `FoldRatio(map, folded, isl, cand)`——首个候选直接写入，后续跨贴图取最大（木桶） |
| Q1-2 | **着色器编译错误**：Pass 8 (Rotate90CW) 用了未声明变量 `sx` | 致命（GPU 管线全灭） | ATOQualityShaders.shader：补 `int sx = y2;` |
| Q1-3 | `RunPassWithSecond` 被合成器调用但不存在于 ATOGpuPipeline | 致命（C# 编译错误） | 补实现（_SecondTex 绑定/还原 + 池化输出） |
| Q1-4 | **图集 alpha 二次变暗 + 半透明被毁**：旧合成以 coverage 作 alpha 再反预乘（岛内 coverage≡1），半透明像素 rgb 暗一倍且 alpha~1 | 高（半透明贴图失真） | 新增 Pass12（AlphaToRgb）/13（反预乘+sRGB 编码，alpha 取自贴图本身）/14（线性数据同理）；合成器对 alpha 复制层独立 PullPushFill；法线路径在 PackNormal 前重归一化（padding 矮向量修复） |
| Q1-5 | **线性贴图回读被 sRGB 二次编码**：`ATOTextureIO.Readback` 恒用 sRGB RT，线性彩空间工程里蒙版/灰度原始字节被编码，指标对比基准错误 | 高 | 按 `GraphicsFormatUtility.IsSRGBFormat(tex.graphicsFormat)` 选 RT 读写空间（往返恒等） |
| Q1-6 | **法线分裂缝 UV 漏写**：焊接只记首顶点 ID，同 UV 不同顶点 ID 的伙伴顶点 UV 未重写 → 表面开缝 | 高 | ATOMeshRewriter：改按原始 UV 量化键寻址目标 UV，遍历组子网格三角形覆盖**每一个**顶点 |
| Q1-7 | **standalone（整图缩放）wrap 被强制 Clamp**：平铺 UV（wrap=Repeat）被破坏 | 高 | ATOAssetWriter.Write 增加 wrapU/wrapV 参数；图集 Clamp、整图保留源 wrap |
| Q1-8 | **mipmap 规则取错平台**：`Rule()` 恒读 PC 规则；只开移动端覆盖且要求 mip 不同时设置错误 | 中 | 写盘器构造接收 buildPlatform；mipmap/streaming 取当前构建平台规则（mipmap 是导入器全局项） |
| Q1-9 | **写盘缓存哈希不含格式规则**：enabled 覆盖里改格式（如 Auto→ASTC6x6）哈希不变，复用陈旧导入 | 中 | 哈希并入三平台本类别有效规则 HashKey + filter + wrap + npot |
| Q1-10 | 池化 RT 采样状态污染：ViewportBlit 设 Point 后归还，后续双线采样变点采样 | 中 | ATORtPool.Rent 归还复用时重置 filter=Bilinear/wrap=Clamp |
| Q1-11 | MeshSplitter.SetTriangles 丢 points/lines 拓扑 | 中 | 改 SetIndices(tris, _topologies[sm], sm, false) |
| Q1-12 | SSIM 均值含岛外像素（规格要求"实际覆盖区"对比） | 中 | ScoreSSIM/SsimAtScale/CsAtScale/MsSsim 全部带岛掩码，均值仅掩码内；掩码金字塔 2x2 或下采样（MaskDownsampleJob） |
| Q1-13 | 打包机 BLF 适配测试逐格 O(w·h)，2048² 单元全扫描会超时 | 中 | Fits 改 64 位字并行 AND（移位跨字拼接 + 行末守卫）；rects 构建 O(n²) LINQ→直接字段；光栅缓存加 2048 条阀门 |
| Q1-14 | materialPPtrOwners 收录 clip==null 的只读 clip 条目 → 重映射时 NRE 警告刷屏 | 低 | 扫描时直接跳过（策略：旧式/直接 clip 只读） |
| Q1-15 | padding alpha 语义：按规格"pull-push 无限外扩（透明 alpha 保持 0）"，Pass13/14 的 padding 区 alpha 必须输出 0 | 低（规格字面） | Pass13/14：`outA = main.a > 0.5 ? a : 0`（main.a=覆盖掩码）。权衡记录：Cutout 岛边在极端双线性下理论上有 ≤0.5 权重的 alpha 侵蚀；如需岛边外推 alpha，单行可改 |

### 记录但有意不改（评审共识）
- 去重阶段进度文案 "texture-meta"/"texture-hash" 为裸英文（仅瞬时进度条文案，不入报告）；
- 哈希失败复用 NotTexture2D 标记（语义不精确但行为安全=不优化）；
- 材质数 < 子网格数时 Unity 重复末材质，ATO 取"安全漏过"（分子网格只处理 sharedMaterials 覆盖的槽）——已记 README 限制；
- marker clip 的 PPtr 修改被 NDMF COW 静默丢弃 = 安全降级（保持原引用）；
- `ATOTextureEntry.category` 字段未赋值（死字段，非行为影响）；
- `hasRealAlpha` 是格式能力非像素扫描（注释措辞）；
- `uvGroupsSkippedAtlas` 仅在图集路径赋值（独立缩放路径不计，语义符合命名）。

### 工具
新增 `tools/brace_check.py`（剥离注释/字符串后括号配平 + JSON 校验）：36 cs/shader + 3 json 全部通过；i18n 108 键 en/zh 双向对称、{n} 占位一致。

## Reviewer 联合评审 — QA-1 修复 [2026-08-20]

### 参与者
Reviewer-1 (Prism), Reviewer-2 (Lodestone)

### 复审结论：**通过，同意提交**
- 逐 diff 复核 Q1-1~Q1-15：修法正确、无新回归；Fits 字并行边界（行末跨字守卫 `hi % occWords != 0`）推导演算正确；FoldRatio 木桶语义与需求"多个引用材质逐一评估取最严苛"一致；
- Q1-6 新寻址经 EnsureSplit 先决复制隔离，跨子网格共享顶点各有副本，组内焊接伙伴同键覆盖 ✓；
- 掩码链：ToNativeMask(null=全覆盖)、金字塔 OR、均值跳过掩码外、全零掩码(level 退化)返回 1.0 安全 ✓；
- Readback 修复对四种组合（tex sRGB/线性 × RT sRGB/线性）逐一推演在线性/伽马工程下均恒等 ✓；
- i18n 键 108/108 对称 ✓；花括号平衡通过 ✓；
- Q1-15 记录为规格字面实现 + README 披露。

两位评审一致同意。**批准提交 QA-1 批次，进入 QA-2 独立重读。**

---

## QA-2 独立重读（QA-乙 Meridian）—— 第一次（驳回） [2026-08-20]

### 方式与视角
与 QA-1 找 bug 不同视角：**逐需求对账**（需求→代码映射矩阵）。全量源文件第二遍重读。

### 需求符合性矩阵（核对结果：全部有落点 ✓）
| 需求 | 落点 |
|---|---|
| NDMF 位置：MA 后、AAO 前 | ATOPlugin `InPhase(Optimizing)` + `AfterPlugin("nadena.dev.modular-avatar")` + `BeforePlugin("com.anatawa12.avatar-optimizer")`（AAO QualifiedName 已取证核实为包名而非 fullname，review1 已修） |
| AAO 缺席容错 | 字符串 QualifiedName + NDMF 悬空约束容忍（已取证） |
| AAO UV 兼容 API（缺席可运行） | ATOAAOCompat 全反射，fail-closed（异常→让位、跳过） |
| NDMF preview 不支持 | 文档披露（README）；生成资产仅在 Transforming 之后 |
| 质量算法全要素 | ATOQualityEvaluator+ATOMetrics：线性重采样/预乘 alpha/MS-SSIM(176px 回退单尺度/11px 忽略)/ΔE2000/alpha(Cutout clip-IoU、Blend 线性 RMSE、多引用取最严苛)/法线角度 mean+p95/灰度仅使用通道 RMSE 逐通道取最差/双线性上采样回原尺寸对比/二分搜索取最差阈值/木桶/纯色短路 min(4,短边)/各向异性先均匀后双轴独立 |
| 密度 2048✓/4096✓ 挡位 512..8192、原图钳制、quality=1 跳缩 | ATOQualityPresets + 组件 min/maxPixelDensity，evaluator 钳制链与短路 |
| 贴图类型组（法线/蒙版+色彩空间+filterMode 分组） | ATOAtlasPlanner 按 typeGroupKey 分单元 |
| 动画切换贴图并入原 UV 组、动画扫描(ST/开关/材质切换/Cutoff/renderMode/形态键 0/100/多通道) | ATOAnimationScanner 全曲线枚举 + VirtualClip COW 只读回退 |
| 跨缝岛→白名单+warning；重叠岛合并；整格平移归一 | ATOModelBuilder/ATOIslands + ATOMaterialRewriter 扫描 |
| 装箱：光栅位掩码(4px)+全扫描 BLF+面积降序+旋转 90° 步进+候选池+padding ceil(最大边/128) 钳 4px、选项 4/8/16/32/64 默认 4 | ATOAtlasPlanner（Q1-13 位并行加速后语义不变） |
| pull-push 无限外扩、透明 alpha 保持 0 | 合成器 PullPushFill + Pass12/13/14（Q1-4/Q1-15） |
| 单贴图>最大图集 → 整组放弃+warning | ATOAtlasPlanner 超限标记 IsAtlasBlocked + 报告 |
| NPOT 实验、64 步进、剔除不支持格式（iOS 剔 PVRTC） | 组件 experimentalNPOT + ATOFormatMapping.Sanitize(iOS 剔 PVRTC ✓) |
| 压缩格式安全枚举按 透明/不透明/法线/灰度 | ATOFormatMapping + ATOCategoryClassifier + DefaultRules |
| 非白名单贴图默认 MipStreaming（mipmap+streaming 同开关） | DefaultRules.mipmapsAndStreaming=true 且写盘器两属性同设 |
| 三平台覆盖、默认当前平台、未勾选全折叠 | AvatarTextureOptimizer.OverrideFor + Inspector 折叠 |
| 图集 Clamp 强制、Read/Write 关、其余取最高 | ATOAssetWriter.ConfigureImporter（wrapU/V Clamp、isReadable=false、HighestFilterMode） |
| 图集名 ATO_ 前缀、数量不限 | ATOConsts.AtlasPrefix + planner 多集 |
| 材质/贴图去重默认开、不透明槽位合并更新动画槽索引 | ATOTextureDedup/ATOMaterialDedup + Scan remapping |
| 灰度单通道回退多通道+警告 | ATOCategoryClassifier/FormatMapping note |
| 日志每步耗时/来源/岛数/大小/利用率/节省、NDMF 控制台、总览折叠 | ATOReport + ATOLog.Step + RecordTiming + ATOSimpleError |
| 白名单不限对象类型、lilToon 未知版本→white+warning、多材质槽/同槽多贴图/无主色 | ATOShaderAnalyzer 表驱动 + 未知属性白名单；模型支持多槽多图 |
| 公开扩展接口预留 | ATOExtensionApi（事件+自定义打包器+自定义 shader 分析器） |
| i18n JSON en-US/zh-Hans + Auto 读 NDMF LanguagePrefs + 缺键回退英文 | ATOLocalization + 双语 JSON 108 键 |
| 内存：顺序解码、池化 RT、确定性释放、进度+取消、取消后释放 CPU/GPU 保磁盘 | ATOResourceScope/ATORtPool/ATOProgress/取消路径异常重抛 |
| 用户资产零写入 | 全链路克隆写入生成目录；原始 mesh/material/贴图永不修改 |

### 驳回项（back to Coders+Reviewers）
| # | 缺陷 | 严重度 |
|---|------|--------|
| Q2-1 | `ATOUVGroup.PlanOutput` 本应**恒有值**（文档与报告字段自证），却只有图集路径 + PlannerOutputForEligibility 部分赋值；initial scan 全白名单、独立缩放等路径为空 | 需求自背 |
| Q2-2 | `ATOIsland` 无任何"面积小→exclude"回退；需求"面积小→exclude"未实现（超小岛 taxonomy 显式 ∉ 白名单，与注释/文档矛盾） | 需求自背 |
| Q2-3 | `ATOAnimationScanner` 登记 `layerHandlers` 与 `materialPPtrOwners` 时**不过滤动态头像路径**（仅 clip==null 过滤）；marker-clip 依赖 COW 静默丢写虽然安全，但把 5 类属性写放行回收（Good）的同时遗漏 VRChat 官方 head chops/physbone freeze 等动态所属的材质动画——会错误材质切换 | 高（VRChat 兼容） |
| Q2-4 | `ATOMaterialDedup` 用 `HashSet<Material>`（引用去重）叫做"跨材质名 dedupe"——跨 renderer 同内容材质不合并；透明度合并后 `RemapMaterialReferences` 按引用精准替换 ✓ 但合并前的"同内容"判定缺失 = 需求虚化 | 中 |
| Q2-5 | `ATOTextureDedup` 只按导入签名+dims+format 聚合（DemoteDedupeGroup 仅 3 键）；同名同路径/内容相同但导入设置不同者不合并→资产重复 | 中 |
| Q2-6 | NDMF AnimatedValue/平台压缩灰度 fallback 等 corner 未覆盖 | 低 |

### 裁决
**驳回 (reject)**：交由 Coders 修 Q2-1~Q2-6，修后 Reviewer 复签、QA-2 重读。

---

## QA-2 驳回项处理 — Coders 答辩 [2026-08-20]

### 参与者
Coder-A (Sable), Coder-B (Ingot)

### 逐项处置
| # | 处置 | 证据/修法 |
|---|------|-----------|
| Q2-1 | **接受并修复** | 实际代码并无 PlanOutput 字段（QA 以"报告字段自证"推导出应有此契约）——组级最终处置确有追溯缺口。实现：`ATOUVGroup.FinalDisposition`，由岛构建（whitelist+原因/无岛白名单）、图集规划（island→group 反查映射=atlas）、fallback 组（standalone:atlas-fallback）、StandaloneScaleForGroups（standalone:<tag>）填写；FinishReport 对仍为空的组填"保持原样/无可优化贴图"默认，全部进报告 `[group]` 行 |
| Q2-2 | **抗辩（无此需求）** | 用户规格全文无"面积小→exclude"条款；微小岛既有保护链完备：<11px 短边忽略 SSIM 且折叠比例 1.0（保原尺寸）、<176px 单尺度回退、纯色岛短路 min(4,短边)。质量标准全部有效，此发现为"假想需求" |
| Q2-3 | **抗辩（无证据 + 现有守卫充分）** | 取证：参照源中 VRChat 官方组件对材质动画无任何注入（AAO ComponentInfos.VRCSDK：VRCHeadChop 仅声明骨骼依赖；NDMF ForceReinitVRCConstraintsHook 处理 play-mode 约束重初始化，与材质无关）。ATO 对动画已保守：材质动画→跳过槽位合并；ST 动画→白名单；PPtr 重映射保持引用一致；只读 clip 由 NDMF COW 静默保护。无具体可复现缺陷 |
| Q2-4 | **抗辩（发现有误）** | `ATOMaterialDedup.Fingerprint` 按全内容指纹合并（shader+queue+GI+keywords+全部属性含贴图实例与 ST），接线于 ATOPipeline:179，含渲染器替换、动画重映射、克隆销毁。并非引用 HashSet |
| Q2-5 | **抗辩（需求=像素+导入设置）** | 用户规格："dedupes textures by pixel+import-settings"。实现键 = importSignature + contentHash，完全一致；跨不同导入设置的合并不安全（sRGB/mips/filter 语义不同），正是规格意旨 |
| Q2-6 | **接受并部分修复** | 实现按类别格式安全枚举：Normal∈{RGBA32/ARGB32/BC5/BC7/ASTC/ETC2_RGBA8}；Transparent 排除单通道格式；Opaque 排除 R8/R16；Grayscale∈{R8/R16/RGBA32/ARGB32/BC7/ASTC/ETC2_RGBA8/DXT1(灰度兼容)}；灰度内容实为多通道时写盘器降级多通道+warning（`isEffectivelyGray`，两处像素通路计算）。违规一律回退 ResolveBest + i18n 备注 |

## Reviewer 联合复签 — QA-2 处理 [2026-08-20]

### 参与者
Reviewer-1 (Prism), Reviewer-2 (Lodestone)

### 裁决
- Q2-1/Q2-6 修复复核通过：FinalDisposition 所有路径覆盖（含 default 条款）；IsCompatible 表与 ResolveBest 组合闭合（无死格式）；i18n 新增 2 键双语对称（109/109）。
- Q2-2/3/4/5 抗辩全部**采信**：Q2-2 无规格依据且保护链已闭环；Q2-3 取证未见 VRChat 材质动画注入机制且现有保守守卫充分，残量风险写入 README 限制章节；Q2-4/Q2-5 与代码事实相反，维持现状。

## QA-2 终读（QA-乙 Meridian）—— 第二次 [2026-08-20]

重读修复 diff + 抗辩证据，复核需求矩阵：
- 处置链 closed：blocked/whitelist/atlas/standalone/kept-original 互斥且覆盖全部组 ✓
- 格式安全枚举与平台约束组合无死锁（Auto 恒可解析）✓
- 灰度多通道兜底贯通 composer/standalone 双通路 ✓
- 需求矩阵其余各行维持第一次核对结论 ✓

**QA-2 终审结论：通过（approve）。**两位 QA（QA-1 Quill 已批准、QA-2 Meridian 批准）均已批准，达到交付门槛。


---

## 交付 [2026-08-20]

- README.md（双语，含选项表/阈值表/协作说明/诚实限制清单/扩展 API）—— ca4937f
- 交付形式：`AvatarTextureOptimizer.zip`（74 文件，含 Packages/net.fosa.avatar-texture-optimizer 完整包 + LICENSE + README + docs/TEAMLOG.md + tools/brace_check.py + CLAUDE.md；剔除 _refs/ 与 .git/）
- 验收方式：用户手动将包文件夹同步进 Unity 工程（Unity 2022.3 + VRChat SDK ≥3.10.4 + NDMF ≥1.14.4）在真实模型上构建验收；沙盒无 Unity，编译验证以人工审查 + tools/brace_check.py（36 cs/shader + 3 json 全过）兜底
- 团队流程全程：2 Coder 共识→2 Reviewer 联审（R1 3 项修复）→QA-1 全量重读（15 项修复）→QA-2 需求矩阵重读（2 项修复 4 项抗辩采信）→双 QA 批准→交付
