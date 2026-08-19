# AvatarTextureOptimizer — 项目记忆 / Project Memory

> 本文件是 AgentTeam 的唯一持久记忆。每次会话开始必读；每次修改后必须更新本文件。
> This file is the AgentTeam's only persistent memory. Read it at the start of every session; update it after every change.

## 1. 项目定位

- 项目名：AvatarTextureOptimizer（ATO）；包名：`net.fosa.avatar-texture-optimizer`。
- 目标：全世界最好的 VRChat 贴图优化工具——开源 NDMF 工具，分析 Avatar 网格，对满足条件的材质建立
  网格 UV→贴图映射，按目标质量算法缩放 UV 岛（有图集时）或整图（无图集时）、剔除未使用 UV、约束装箱生成图集。
- 铁律：仅修改贴图和 UV，绝不修改材质其他任何着色器参数；优化前后 Avatar 表现必须一致；不安全即 fallback（白名单/放弃+warning）。
- 用户是"默认小白 + 支持高级用户"：必要信息输出到 NDMF 控制台；所有日志以 `[ATO]` 开头；日志有 Verbose 开关（默认开）。
- 完整需求见用户原始消息与 docs/design.md；需求有歧义时以用户原始消息为准。

## 2. AgentTeam 章程与流程

- **Coder A / Coder B**：写代码前先互相交流，达成共识（记录于 `docs/decisions.md`）后再落码。
- **Reviewer A / Reviewer B**：Coder 每写完代码，共同审查，共识后决定放行或打回。
- **QA A / QA B**：Coder 全部完成且通过 Reviewer 后才交 QA；两个 QA 各自独立从头完整通读全部代码；
  同时认为符合要求才打包 zip 交付最终成品；有缺陷则同时通知 Reviewer 和 Coder 打回。
- **PM（协调者）**：分配任务、控制进度、git 提交、维护本文件。
- 规则：修改/排查 bug 前必须先读代码取证，禁止凭表现猜测；禁止未经验证地使用第三方 API
  （已取证结论见 `docs/api-notes.md`）；代码注释必须中英双语。

## 3. 依赖与 API 取证（重要）

- 依赖包下载 URL 见 docs/api-notes.md（含复现命令）；解压到 `/tmp/ato-deps/`（**不在工作区快照内，跨会话会丢失**）。
- **已取证的 API 结论全部记录在 `docs/api-notes.md`**（NDMF 1.14.4 / MA 1.18.2 / AAO 1.9.17 / VRC SDK 3.10.4 / lilToon 2.3.4）。
- 关键结论：插件排序 = Optimizing + AfterPlugin(两个 MA 限定符) + BeforePlugin(AAO)；
  AAO 的 `UVUsageCompabilityAPI` 经反射适配（可选依赖）；VRC 动画层 = baseAnimationLayers/specialAnimationLayers + CustomAnimLayer。

## 4. 当前进度（2026-08-19 更新：全功能完成，待用户在 Unity 验证）

### 已完成（v0.1.0 全部功能，44 个 C# 文件 ≈ 8300 行 + 3 个着色器 + 双语文档）
- **Runtime**：`ATOAvatar` 主组件、`ATOWhitelist` 白名单、`ATOSettings` 完整设置模型、`ATOConstants`。
- **Editor/Core**：`ATOLog`（[ATO] 日志+计时）、`ATOReport`（NDMF 控制台报告 + SimpleError 多语言错误）、
  `ATOExceptions`/`ATOCancellation`（取消→中止构建、保留临时资产）、`ATOContext`（平台解析）。
- **Editor/NDMF**：`ATOPlugin`（Optimizing，MA 后 AAO 前）、`AAOUVUsageAdapter`（反射适配）。
- **Editor/Analysis**：材质槽扫描（槽位=子网格索引，EditorOnly 跳过）、动画扫描（槽位切换/属性动画/贴图动画/启停/缩放/形态键 0&100）、
  贴图收集（分类+像素与导入设置去重+规范化）、白名单解析（Full/NoAtlas 两级）、liltoon 2.3.4 取证属性表 + 未知着色器解析。
- **Editor/Islands**：岛提取（并查集、越界整数归一 Repeat/Clamp/Mirror 回退、重叠岛合并）、世界面积（动画缩放+形态键）、
  `IslandTransform`（旋转/UV 重映射统一约定）。
- **Editor/UvGroups**：UV 组（同 UV 贴图成组）、类型组（种类多重集+sRGB+filterMode）、NoAtlas 传播、同岛分辨率一致性检查。
- **Editor/Quality**：`QualityMath`（sRGB/CIEDE2000/MS-SSIM(5层,<176px 回退 SSIM,<11px 忽略)/IoU/RMSE/角度误差/法线编解码/双线性往返，全 NativeArray+Burst）、
  `QualityJobs`（Burst 均匀/各向异性评估作业）、`TextureCache`（GPU 线性预乘半精度池，法线 DXT5nm 探测、灰度通道使用探测）、
  `IslandScaler`（密度钳制、纯色短路（含 alpha 判定）、二分搜索、各向异性细化、整图缩放目标）。
- **Editor/Packing**：`BitMask`（4px 位掩码+膨胀+旋转）、`Rasterizer`（Burst 三角形光栅化）、
  `Packer`（贴图连通簇装箱、BLF 全扫描、90° 旋转、候选图集池 POT/NPOT、同组图集统一尺寸、fallback+warning）。
- **Editor/Atlases**：`AtlasBuilder`（双线性采样自半精度池、GPU pull-push 跳跃洪泛外扩（透明 alpha=0）、法线重归一化、
  sRGB 编码 PNG、导入设置：Read/Write 关/强制 Clamp/Mipmap-MipStreaming 绑定/平台格式）、`FormatResolver`（平台/alpha/灰度/NPOT 安全校验）、
  `AtlasGpu`（GPU 工具）+ 3 个着色器（Dilate/NormalizeNormal/Encode）。
- **Editor/Apply**：`MeshApplier`（网格克隆+UV 重写+AAO 通道疏散）、`SlotMerger`（子网格合并+动画索引重写，临时资产安全条件）、
  `MaterialApplier`（每材质一克隆（含动画切换材质）、仅改贴图属性、材质去重、纹理替换注册）、`AnimationBindingRemapper`（贴图属性动画按属性重写）、
  `FallbackTextureProcessor`（整图缩放/导入副本，DXT5nm 正确解码）、`TextureDedupPost`（图集字节去重）、`ComponentCleanup`（移除自身）。
- **Editor/Pipeline**：`PipelineStages`（13 个阶段编排+钩子+计时+取消）、`ATOBuildProcess`（校验+进度条+报告）。
- **Editor/UI**：`ATOAvatarEditor`（分区 Inspector：基础/质量/格式/高级/平台覆盖/语言；密度挡位、格式白名单下拉、Undo）。
- **Editor/Extensions**：`IATOPipelineHook` 自动发现 + `ATOAnalysisSnapshot`。
- **i18n**：en-us/zh-hans 各 ~145 key；Auto 读 NDMF 语言；可扩展。
- **工具**：`tools/gen_meta.py`、`tools/check_project.py`（括号平衡/i18n key/meta/GUID/TODO）、`tools/inject_i18n.py`。
- **文档**：README.md、docs/api-notes.md、docs/design.md、docs/decisions.md、LICENSE。git 已提交（main 分支）。

### 待办（验证后迭代）
1. **用户在 Unity 中的编译验证**：本环境无 C# 编译器（apt 不可用），只能做结构性检查。
   用户同步进工程后首轮编译报错必须优先处理（列表见下）。
2. 验证清单（建议用户按序验证）：组件挂载校验 → 白名单 → 简单图集烘焙 → 动画切换 → 无图集模式 → 平台覆盖 → 取消。
3. 已知取舍（记录在案，用户可提出调整）：密度语义（max=缩放上限/min=下限1）；ΔE 用均值；各向异性先 X 后 Y；
   质量指标归约在 Burst CPU（GPU 用于解码/外扩/编码）；同岛贴图分辨率必须一致才能图集化。

## 5. 注意事项

- 所有注释中英双语；日志 `[ATO]` 前缀；新增代码同步补 i18n key（en-us 与 zh-hans 两份）。
- 修改代码后必须跑 `python3 tools/check_project.py` 与 `python3 tools/gen_meta.py`（新文件要生成 .meta）。
- 沙箱无 C# 编译器，无法本地编译验证；结构性检查 + API 取证替代。
- 用户要求：每次修改后 git 提交（用环境变量 GIT_AUTHOR_NAME 等，`.git/config` 不持久）。
- 取消语义：进度条取消 → ATOCancelledException → NDMF 中止；硬盘临时资产保留；资源随栈展开释放。
- 打包：`python3 tools/make_zip.py`（产出 AvatarTextureOptimizer-<版本>.zip，排除 .git 与 tools/）。

## 6. 下次会话起点

1. 读本文件 + docs/decisions.md + docs/api-notes.md。
2. 收集用户在 Unity 中的编译错误/烘焙结果反馈，按"先取证再修"原则处理。
3. 每次修改：git 提交 + 更新本文件"当前进度"。
