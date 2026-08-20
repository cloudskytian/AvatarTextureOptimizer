# ATO 可行性分析与总体计划 / Feasibility & Plan

## 可行性结论：可行（有 3 处需要用户确认的取舍，见文末）

需求书的整体逻辑链成立：网格UV↔贴图映射 → 质量驱动岛缩放 → 位掩码装箱 → UV/贴图引用重写。
业界先例：AAO 1.9 的 AtlasTexture（同样的网格UV重写+材质替换）、avatar-compressor（贴图参数/压缩）、
Unity TextureImporter（质量/密度思想）。没有需求是不可实现的，其中三块是工程难点：
岛级质量评估性能、装箱布局一致性约束、动画引用更新，均已给出方案。

## 数据流（Pipeline 阶段）
```
Validate → Scan(Renderers/Slots) → AnalyzeShaders → AnalyzeAnimations
  → DedupTextures → BuildUsageGraph(白名单/资格/类型组/连通分量)
  → ExtractIslands(多通道UV/归一/重叠合并/形态键&缩放面积因子)
  → QualityScale(GPU解码缓存 + Burst指标 + 二分搜索[密度钳制→均匀→双轴])
  → [图集模式] Rasterize → CandidatePool → BLF装箱 → ComposeAtlas(GPU Blit + Burst PullPush)
  → RewriteMeshUV(顶点分裂/保形态键/切线不动) → PatchMaterials(+动画) → AAO UV疏散
  → TextureParams(压缩/流式/Clamp/RW off/安全fallback) → FinalDedup(材质/贴图/槽位合并)
  → Report(NDMF控制台) → RemoveComponent
```

## 核心数据模型
- `TextureUse`（贴图的一次引用）: material/property/class(Color|Normal|Mask)/uvChannel/alpha语义
- `UvIsland`（UV空间岛）: usages[(renderer,submesh,tris,channel)] + uvBounds + 每贴图缩放结果
  - 岛按 UV 接缝连通提取；跨网格重叠岛合并；同一岛的全部贴图 = UV 组（自动满足同UV同位置）
- `PackingComponent`（连通分量）: 纹理↔岛二部图连通分量 = 装箱原子单元
- `TypeGroup`: (sRGB, filterMode, hasNormal, hasMask) per 纹理取并集；页面种类共享布局
- 回退路径（不图集化，只整图缩放+参数优化）: 白名单同UV、ST/越界跨缝等资格失败、分量超最大图集

## 阶段↔里程碑
M1 骨架/配置 | M2 扫描分析 | M3 UV岛 | M4 质量 | M5 装箱 | M6 应用 | M7 NDMF集成 | M8 UI | M9 测试/README/QA/打包

## 需要用户确认/已知偏差
1. pull-push 用 Burst CPU 实现（非GPU计算着色器）——理由见 CLAUDE.md 决策10，效果相同。
2. padding 语义按 max(minPadding, ceil(maxEdge/128)) 实现。
3. "类型组"按纹理级并集签名实现（比需求书的例子更保守，保证同贴图不跨图集）。
4. 密度钳制同时考虑形态键 max(base,100) 与动画最大缩放（逐祖先轴取最大，两两乘积最大值）。
