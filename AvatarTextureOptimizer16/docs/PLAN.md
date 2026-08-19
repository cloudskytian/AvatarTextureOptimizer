# AvatarTextureOptimizer — 实施计划（PLAN.md）

> 唯一权威技术方案。CLAUDE.md 记录记忆与进度，本文档记录技术方案。
> 所有结论遵循「先读源码、取证后再下结论」；标注 **[待验证]** 的项在动手前必须先读对应源码。

## 1. 可行性结论

**可行。** 本质是一个「UV 岛级目标质量缩放 + 按贴图类型组的图集装箱 + UV 组不变量 + 安全去重」的 NDMF 工具。逻辑闭环、技术上成立，比社区现有同类工具（AAO Texture Optimizer、TexTransTool 等）在岛级质量控制和类型组图集上更彻底。

主要风险集中在：指标数值一致性、NDMF/AAO 集成细节、法线编码、动画兼容、装箱效率。均已识别并给出缓解方案（§7）。

## 2. 设计评审：发现的问题与建议（需用户确认）

1. **[重点] CPU(Burst)/GPU 指标一致性**：MS-SSIM/ΔE00/角度误差若一部分在 Burst、一部分在 RenderTexture 上算，浮点误差会导致同一岛两次判定结果不一致。**建议：指标计算统一在 GPU 单一求值 shader 作为唯一真相源**，Burst 只负责光栅化与装箱（此处与你的描述略有差异，请确认是否接受）。
2. **[重点] NDMF 阶段与 AAO 前置**：已核实可用 `.BeforePlugin("com.anatawa12.avatar-optimizer")`，AAO 缺失时 NDMF 静默忽略 → 天然兼容未安装。但仍需读 AAO 源码确认 `UVUsageCompabilityAPI`（你特别标注的拼写）与 Shader Information API 的确切形态。
3. **法线贴图编码**：DXT5nm（x 存 alpha）/BC5/BC7 解码-重采样-重编码需按源格式分支；图集化后法线编码要与主图集格式组一致。需读 liltoon 确认法线采样约定。
4. **材质槽合并的动画风险**：合并槽会改 renderer.materials 数组，需校验动画是否通过索引/属性路径引用材质槽。你的前置条件（动画中不存在单独切换）方向正确，我会在动画分析里加校验。
5. **装箱强约束效率损失**：「同一贴图所有岛必须同图集」+「UV 组原子装箱」换来正确性、牺牲装箱率。接受，但报告里输出利用率供评估。
6. **纯色岛 min(4, bbox) 而非 1px**：纯色可缩到 1px（双线性上采样仍是纯色），min(4) 略保守但更安全（mip 边缘）。保持你的设定，仅记录。
7. **NPOT + MipStreaming/Crunch 声明**：你称已验证。保留选项，但加运行时校验，遇不支持组合报 warning 并回退 POT。
8. **形态键改 UV 的罕见情况**：形态键可能同时改顶点位置与 UV；若检测到形态键改 UV，对应岛应视作白名单。
9. **多通道 UV 的岛主键**：岛主键应设计为 (mesh, uvChannel, islandIndex)，避免不同通道岛被误并。

## 3. 架构与模块划分

### 3.1 程序集（asmdef）
- `AvatarTextureOptimizer.Runtime` — 运行时 MonoBehaviour（组件、白名单）。引用 VRC.SDKBase / VRC.SDK3A **[待验证程序集名]**。
- `AvatarTextureOptimizer.Burst` — Burst 任务（光栅化、装箱、位掩码转置）。引用 Unity.Burst/Collections/Mathematics/Jobs。
- `AvatarTextureOptimizer.Editor` — 主逻辑（NDMF 集成、分析、质量、装箱调度、i18n）。引用 Runtime + Burst + ndmf + VRC SDK + Burst 栈。

### 3.2 目录
```
Editor/Core        管线状态、缓存、日志、进度/取消
Editor/Analysis    渲染器/材质槽收集、shader 属性分析、liltoon 内省
Editor/Animation   动画扫描（材质/贴图切换、启停、缩放、形态键、render mode/Cutoff）
Editor/Quality     MS-SSIM/ΔE00/alpha/normal/gray 指标、GPU 求值、岛缩放二分
Editor/Packing     装箱、候选池、pull-push、padding
Editor/Component   Inspector、组件校验
Editor/Plugin      NDMF Plugin/Pass
Editor/I18n        本地化
Editor/Shaders     指标 shader、pull-push shader
Runtime            组件、白名单
Burst              光栅化/装箱 jobs
i18n               en.json、zh-Hans.json
```

## 4. 处理管线（OptimizingPhase，MA 后、AAO 前）

0. 组件校验：挂载对象存在 VRCAvatarDescriptor、全 Avatar 仅一个组件，否则报错中止。
1. 收集渲染器：启用或有动画启用的 SkinnedMeshRenderer/MeshRenderer，跳过 EditorOnly。
2. 收集贴图：遍历材质槽，收集满足限制条件（无 ST 变换、非贴花等）的主色/法线/蒙版/灰度贴图；多通道 UV 按通道拆分。
3. 去重（像素内容 + 导入设置），更新引用；白名单传播。
4. 动画分析：材质/贴图切换、渲染器启停、缩放、形态键、render mode/Cutoff 修改 → 合并进 UV-贴图关系（去重）。
5. Shader 属性分析：liltoon + 标准关键字 → 判定贴图类型组、色彩空间、filterMode、用途（有法线/有蒙版/动画切换组）。
6. 建立 (mesh, uvChannel, island) → 贴图组 关系；构建 UV 组不变量。
7. 目标质量缩放：逐岛二分（先均匀、后双轴细化）；纯色短路；目标质量=1 跳过。
8. 装箱（若启用图集）：类型组队列 → 候选池 → BLF 光栅装箱 → padding + pull-push。
9. 重建网格 UV、重新赋材质（含动画引用）。
10. 去重：材质/贴图按内容+参数去重，安全时合并材质槽（含动画索引更新）。
11. 应用压缩格式、MipStreaming 绑定（与 Mipmap 绑定为单开关）、平台 override、关闭 Read/Write、强制 Clamp。
12. 报告输出到 NDMF 控制台。

## 5. 目标质量算法与默认挡位（提案，高级选项可改）

指标（统一 GPU 求值）：
- 彩色不透明：MS-SSIM + ΔE00(CIEDE2000)
- 彩色透明：预乘 alpha 下采样；MS-SSIM + ΔE00 + alpha（Cutout→clip 后轮廓 IoU / Blend→线性 RMSE；多材质取最严）
- 法线：解码→重采样→重归一化→编码后角度误差 + p95
- 灰度：仅使用通道、线性空间 RMSE、逐通道取最差
- 包围盒短边 <176px 回退单尺度 SSIM；<11px 忽略该参数
- 缩放：二分；按 UV 组木桶取最大尺寸（≤组内最大原尺寸）
- 像素密度：默认最小 2048px/m、最大 4096px/m；挡位 512/1024/2048/4096/8192

挡位提案（参数随挡位联动；自定义挡位默认全 1=近无损，不被其他挡位覆盖）：

| 挡位 | MS-SSIM | ΔE00 p95 | alpha | 法线角度 p95 | 灰度 RMSE |
|------|---------|----------|-------|--------------|-----------|
| 近无损 | 1.000 | 0.0 | 原样 | 0.0° | 0 |
| 高质量（默认） | ≥0.995 | ≤1.0 | IoU≥0.95 / RMSE≤2/255 | ≤1.5° | ≤2/255 |
| 均衡 | ≥0.990 | ≤2.0 | IoU≥0.90 | ≤3.0° | ≤4/255 |
| 性能 | ≥0.980 | ≤3.0 | IoU≥0.85 | ≤5.0° | ≤8/255 |

> 说明：ΔE00≤1 为「几乎不可察觉」，2~3 为「勉强可察觉」；MS-SSIM 0.99~0.995 对应高质量区间。此表为提案，待实测标定。

## 6. 关键集成点（必须读源码验证）
- NDMF 1.14：Plugin/Configure/InPhase/BeforePlugin/AfterPlugin/Pass/InlinePass；BuildContext.AvatarDescriptor 类型；临时资产保存/清理 API。
- AAO 1.9.17：Optimizing 阶段 pass 类名与顺序；`UVUsageCompabilityAPI` 形态 **[待验证]**；Shader Information API（1.8.0+）；组件兼容注册 API。
- MA 1.18.2：Transforming 阶段顺序（确认我们确实在其后）。
- liltoon 2.3.4：主色/法线/蒙版等属性名与关键字、法线编码约定、压缩格式关键字。
- VRC SDK 3.10.4：VRCAvatarDescriptor 所在程序集名 **[待验证]**；MipStreaming/压缩格式约束。
- Burst/Collections 版本（随 Unity 2022.3 的包版本而定）。

## 7. 风险与缓解

| 风险 | 缓解 |
|------|------|
| CPU/GPU 指标不一致 | 指标统一 GPU 求值 |
| AAO 集成 API 形态不明 | 读 AAO 源码，反射兜底 |
| 法线编码错误 | 按格式分支 + 读 liltoon |
| 材质槽合并动画错位 | 动画曲线/属性路径校验 |
| 装箱效率低 | 报告利用率，NPOT 可选 |
| 内存占用过大 | 分批处理 + 缓存上限 + 及时释放 |
| 形态键改 UV | 检测到即白名单 |

## 8. 里程碑（AgentTeam 迭代顺序）
- M1 组件/白名单 + 组件校验 + NDMF 空 pass 跑通
- M2 收集/去重/动画/着色器分析（数据模型）
- M3 质量指标（GPU shader）与岛缩放
- M4 装箱/图集/pull-push
- M5 网格/材质/动画引用更新 + 去重
- M6 压缩/平台/MipStreaming
- M7 i18n + 报告 + README + 打包 zip

每个里程碑内部走：Coder 共识 → Reviewer 共识 →（整体完成时）QA 双重独立审查。

## 9. 交付物与验收
- zip：完整 VPM 包（含 en/zh-Hans i18n、双语注释、README.md、CLAUDE.md）。
- 日志 [ATO] 前缀，含耗时/图集来源/岛数/尺寸/利用率/优化量，可折叠，预留开关。
- 验收：QA×2 独立通读全量代码双通过才交付。
