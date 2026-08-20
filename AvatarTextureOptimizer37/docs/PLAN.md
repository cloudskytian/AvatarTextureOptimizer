# ATO 实施计划 / Implementation Plan

> 每个里程碑完成时更新状态。阶段编号与 `ATOPipelinePass` 中的 stageNames 对应。
> Update status as milestones complete.

## 阶段总览 / Stage overview

| # | 阶段 | 类 | 输入 | 输出 | 里程碑 |
|---|---|---|---|---|---|
| 0 | Validate | ATOContext.Validate | avatar root | ATOComponent（或抛出） | P0 ✅ |
| 1 | Analyze | AnalysisStage | 场景 Avatar | ATOModel（渲染器/材质/贴图/岛/组） | P1 |
| 2 | Quality | QualityStage | ATOModel | 每岛缩放决策（统一/双轴 scale、纯色标记、丢弃标记） | P2 |
| 3 | Pack | PackStage | 缩放后岛 | 图集布局（矩形+旋转+分辨率+UV 组对齐） | P3 |
| 4 | Atlas | AtlasStage | 布局+贴图内容 | 图集 Texture2D 像素 + 每网格新 UV + 新引用表 | P4 |
| 5 | Import | ImportStage | 引用表+设置 | 每贴图的 TextureImporter 参数方案 | P5 |
| 6 | Dedup | DedupStage | 全部资产 | 去重映射 + 子网格合并方案 | P5 |
| 7 | Apply | ApplyStage | 全部方案 | Unity 对象落地 + 组件自移除 | P4-P6 |
| 8 | Report | ReportStage | 会话数据 | NDMF 控制台报告 + 日志 | P6 |

## P1 分析阶段（AnalysisStage）详细设计

数据模型（`Sources/Editor/Analysis/`）：

```
ATOUVIsland {
  int id; Renderer owner; int submesh; int uvChannel;
  int[] triangles (owner 网格中的三角索引);
  Vector2[] corners (归一化后 UV 多边形，按轮廓);
  float2 minUV/maxUV (归一化包围盒); float worldArea;
  int textureRefId;         // 所属贴图（去重后 id）
  int uvGroupId;            // UV 组（同 UV 的所有岛/贴图）
  int texTypeGroupId;       // 类型组
  int overlapClusterId;     // 同贴图重叠合并簇
}
ATOTextureRef { Texture2D tex; ImportSettingsIdentity; ColorSpace; bool whitelist; }
ATOTexTypeGroup { colorSpace; filterMode; hasNormal; hasMask; hasEmission; members[] }
ATOUVGroup { uvRects[]; textureRefs[] (主色/法线/蒙版…); anchorIsland }
```

步骤：
1. 收集渲染器：`GetComponentsInChildren<Renderer>(true)`，跳过 `gameObject.CompareTag("EditorOnly")` 的 GameObject 上的渲染器；再查动画中 `m_IsActive` 会启用的渲染器（被动画启用的也算参与）。
2. 收集材质：每渲染器 `sharedMaterials`；扫描动画中的材质/贴图切换（见下）并入。
3. Shader 分析：内置分析器（Standard 关键字 + lilToon）→ 第三方可注册；失败 → 该材质贴图白名单+warning。
4. 变换检测：`GetTextureOffset/GetTextureScale` != (0,1) → 白名单；`_Xxx_ScrollRotate` 动画非零 → 白名单；`UVMode` 属性（lilToon）非 UV0-3 → 特殊用途白名单；`[NoScaleOffset]` 属性 → 特殊用途白名单。
5. 动画扫描（Animator + AnimatorController 全遍历，含 root）：
   - 材质对象引用曲线（object reference curves：`m_Materials.Array.data[i]`）→ 新材质/贴图并入
   - 贴图对象引用曲线（`Material._MainTex` 等）→ 新贴图并入
   - `Material._Xxx_ST._Offset/_Scale` 曲线 → 任意非零 → 该贴图白名单
   - `Material._Cutoff/_SubpassCutoff/_TransparentMode` 曲线 → 记录 min/max（评估取最严）
   - 渲染器 `m_IsActive` 曲线 → 动画启用判定
   - 变换 `m_LocalScale` 曲线 → 该网格最大缩放（面积按最大算）
   - 形态键：只取 0 与 100（两者网格各算一次，取面积大者）
6. 贴图去重：内容哈希（像素+尺寸）+ 导入设置（格式/压缩/过滤/色彩空间等关键字段）不同 = 不同；更新全部引用（材质+动画）；去重涉及白名单 → 结果也白名单。
7. UV 岛提取：每 (renderer, submesh, uvChannel)：按 UV 连通性提取岛（三角 UV 顶点邻接）；同贴图归一化空间内包围盒重叠 → 合并为复合岛；越界处理（平移归一/跨缝→白名单+warning）。
8. 类型组/UV 组：按 (色彩空间, filterMode, 是否带法线, 是否带蒙版, 是否带自发光) 分类型组；同 UV 的所有贴图（含动画切换、含类型组成员）构成 UV 组。
9. 白名单物化：`ctx.WhitelistedTextures`（含原因）。

## P2 质量阶段（QualityStage）详细设计

1. 解码缓存：每张参与贴图 ReadPixels 一次（线性空间 float 缓存；法线解码→RGB 重归一化；灰度按使用通道）。
2. 每岛目标像素预算：`budget = clamp(worldSize * density, 4, originalPx)`，density∈[minDensity,maxDensity]（默认 2048..4096）。
3. 质量=1（Lossless/自定义 q=1）：跳过缩放，原样拷贝（含纯色）。
4. 纯色岛（q<1）：短路到 min(4, 原短边)。
5. 二分搜索 scale：
   - 先均匀二分（scale s）：渲染 s 缩放岛 → 上采样回原尺寸 → 指标评估（Burst 并行岛级 + GPU 批量重采样）
   - 通过后双轴细化：固定通过后的均匀 s，对 sx∈[s, 1/s*?] 与 sy 独立二分（各向异性修正），防单方向浪费/劣化
   - 判定：所有内置指标 + 自定义指标同时达标（ssim 阈值等来自档位参数）
   - UV 组木桶：组内所有贴图取**最严格** scale（最小通过值）；结果 ≤ 组内最大原尺寸
6. 指标实现（`Sources/Editor/Quality/`）：
   - MS-SSIM（176px+）/ SSIM（<176px 回退）/ <11px 忽略
   - ΔE2000（线性空间，sRGB→线性 或 贴图色彩空间解码）
   - alpha：Cutout→按引用材质 Cutoff clip 后轮廓 IoU；Blend/Premult→线性 RMSE；多材质逐一评估取最严
   - 法线：角度误差 p95（度）
   - 灰度：使用通道线性 RMSE，逐通道取最差
7. 输出：每岛 `finalScaleX/Y`、`isPureColor`、`dropFromAtlas`（装不下时）。

## P3 装箱阶段（PackStage）详细设计

1. Burst 光栅化：每岛 4px 粒度位掩码（CPU Jobs 并行；结果缓存）。
2. 候选图集池：POT 64,128,…,8192（移动端 4096）；NPOT 勾选时 64,128,…（步进 64）。每池允许非正方形（w,h 组合，面积相近时优先正方形）。
3. 队列：按贴图（去重后）为单位，含该贴图全部岛；排序=光栅化总面积降序；同类类型组连续。
4. 装箱：全扫描 BLF（全扫描底左优先），岛按面积降序+边长降序，允许 90° 旋转（位掩码转置；法线数据不转置——只转置摆放位置，采样时保持切线原样）；UV 组：锚岛决定位置，其余组内岛复制变换+组内偏移。
5. 失败处理：队列装不进当前候选 → 更小候选；单贴图装不进最大图集剩余 → 新开/复用同类队列；单贴图都进不了最大图集 → 放弃该 UV 组图集化 + warning（按质量缩放后直接整图优化）。
6. 输出：`ATOPackedAtlas { size, layout[], uvGroupTransforms, texTypeGroup, memberTextureIds }`。

## P4 图集/Apply 详细设计

1. 图集页合成：GPU（RenderTexture）按布局 blit 各岛（双线性，含法线重归一化后编码）；padding 区与空白做 pull-push 边缘无限外扩（透明图 alpha 保持 0）；命名 `ATO_{类型}_{i}`。
2. UV 重映射：每岛 UV → 图集内矩形（含组对齐变换）；写回网格 uv0..uv3（多通道各通道独立处理；被 AAO 使用的通道先经 AAOInterop 撤离）。
3. 引用更新：材质贴图槽指向图集页/新贴图（仅贴图槽！）；动画中的对象引用曲线（材质/贴图）按映射表重映射。
4. 不生成图集模式：整贴图按质量缩放（同指标），直接替换。
5. AAO 通道协调：改写 SMR 的 UV 通道前 `IsTexCoordUsed` → 真则 `TryRegisterEvacuation`（找空闲通道）；失败/MR → 白名单+warning。
6. Apply：`SerializationScope` + `AssetSaver.SaveAsset` 保存新 Texture2D；重建网格（共享顶点流，只改 UV 数组与子网格索引）；材质赋值；动画重映射；`DestroyImmediate(atoComponent)`。
7. 每 mesh 之间检查取消；取消时不保存新资产、Avatar 原样。

## P5 导入/去重详细设计

1. 格式安全表（FormatSafety）：每类别允许集合 × 平台 × NPOT × 通道需求（alpha 使用判定：透明模式/蒙版用法；lilToon 关键字+像素内容兜底）。
2. 应用：`TextureImporter`（sRGB 按色彩空间、filterMode 取最严、mipmap+mipstreaming 绑定、NPOT 剔除 PVRTC、Read/Write 关、WrapMode 强制 Clamp（图集）/保持原（整图））。
3. 回退：用户选择不安全 → 自动改安全项 + `[ATO]` warning（如灰度单通道遇多通道 → 多通道保存+warning）。
4. 材质去重：哈希(shader, 全部 float/color/vector/keyword, 贴图引用集合, renderQueue)；仅当可判定相同且动画不单独切换其中某材质时合并；不透明同网格合并子网格（索引拼接）+ 动画槽索引重映射。
5. 贴图/图集去重：内容哈希+导入设置；更新引用（材质+动画）。

## P6 报告详细设计

- 摘要行（NDMF 控制台）：岛数、图集页数、总字节 before→after（%）、白名单数、warning 数、总耗时。
- 每图集细节（verbose 或折叠块）：来源贴图列表、岛数、尺寸、利用率（覆盖像素/图集像素）、相对原图优化量。
- 每步耗时表；全部 [ATO] 前缀。
- 报告文本同时写入临时目录文件（高级用户可查）：`ATO_Report_{avatar}_{time}.txt`。

## 质量档位映射表（依据 MS-SSIM/CIEDE2000 可见性研究调参）

| 档位 | q | SSIM/MS-SSIM | ΔE2000 max | alpha RMSE | Cutout IoU | 法线 p95° | 灰度 RMSE |
|---|---|---|---|---|---|---|---|
| Lossless | 1.00 | 1.00 | 0.40 | 0.002 | 0.99 | 0.5° | 0.002 |
| High | 0.95 | 0.95 | 0.45 | 0.0054 | 0.9875 | 1.05° | 0.0054 |
| Medium(默认) | 0.90 | 0.90 | 0.56 | 0.0089 | 0.98 | 1.63° | 0.0089 |
| Low | 0.80 | 0.80 | 0.84 | 0.0165 | 0.96 | 3.18° | 0.0165 |
| Extreme | 0.70 | 0.70 | 1.34 | 0.0296 | 0.93 | 5.25° | 0.0296 |

（t=1-q；公式见 CLAUDE.md 第 5 条。）

## 测试/验证策略

- 本环境无 Unity：所有代码以"可编译+逻辑自洽"为准，由用户手动同步工程验证烘焙。
- 每阶段完成后：git commit + 更新 CLAUDE.md + Reviewer 轮 + 阶段自检清单（边界：空 Avatar、单材质、全白名单、NPOT、移动端平台、无 AAO、无 lilToon、多 UV 通道、形态键、动画切换等）。
