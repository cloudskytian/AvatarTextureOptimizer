# AvatarTextureOptimizer — Design Document（设计文档）

> 本文档记录架构决策、规格映射与实现取舍，供第三方开发者与后续维护者参考。
> This document records architecture decisions, spec mappings and engineering trade-offs.

## 1. 总体架构（Architecture）

```
NDMF BuildPhase.Optimizing
  ATONdmfPlugin (BeforePlugin "com.anatawa12.avatar-optimizer", 缺 AAO 时自动忽略)
    └─ ATORunner.Run(BuildContext)
        1. 校验（单组件 / VRCAvatarDescriptor 锚点）—— 违规即报错中止
        2. 动画分析 AnimationAnalyzer（Mecanim + 旧版 Animation 剪辑索引）
        3. 白名单 WhiteListEvaluator
        4. 贴图去重 TextureDeduper（像素 MD5 + 导入设置指纹；白名单成员 → 结果白名单）
        5. UV↔贴图收集 TextureUseCollector（渲染器/材质槽/着色器属性/动画候选）
        6. 质量缩放 IslandScaler（二分搜索：先均匀后双轴细化；密度钳制；纯色/近无损短路）
        7. 图集装配 AtlasBuilder（类型桶 → 全局布局 → (桶,贴图) 图集）
        8. 图集烘焙 AtlasTextureBaker（GPU 采样 + JFA pull-push 外扩）
        9. 引用重写 ReferenceUpdater（材质/剪辑/控制器克隆，绝不改用户资产）
       10. 整图缩放回退 WholeTextureScale
       11. 网格重映射 MeshReplacer（克隆网格、UV 重排、AAO UV 转移）
       12. 材质/槽位去重 MaterialDeduper（含子网格合并 + 动画槽位索引重映射）
       13. 导入参数 ImportSettingsApplier（PNG 落盘、mip+streaming 联动、Clamp、平台格式）
       14. 移除自身组件、报告、清理
```

## 2. 关键设计决策（Key decisions）

### D1. UV 组跨图集同位（UV-group consistency across atlases）
同一网格 UV 被多个材质/贴图采样（含动画切换），且一个 UV 可能同时被主色图集与法线图集采样。
**方案**：所有图集化 UV 组先在参考分辨率 `D_max`（PC 8192 / 移动 4096）上做**一次全局布局**
（组宏观矩形矩形 BLF → 全局归一化原点），每个岛获得固定归一化矩形 + 旋转。随后每个
`(桶, 贴图)` 图集只选择满足该贴图各岛像素需求的**最小候选边长 D** 并实例化同一归一化矩形。
由此同一网格 UV 在所有图集指向同一位置，杜绝"有法线与无法线主色混用 UV 出错"。

### D2. 装箱（Packing）
- 4px 粒度位掩码光栅化（Burst 可用时走 `RasterIslandJob`，否则托管 `AtoRaster`，两者算法一致）。
- 全扫描 BLF：按（光栅面积降序、长边降序）排序，0°/90° 旋转，边界跳过扫描（正确且高效，经 200 轮模糊测试验证）。
- 岛间距 = `max(ceil(最大边长/128), 用户最小padding)`，向上取整到 4px。
- 图集数量不限，随 (桶,贴图) 自然增长。
- 单岛/单组放不进最大图集 → 该组回退整图缩放并报 warning。

### D3. 质量挡位参数（Quality tiers）
以学术/业内参考为据（详见 §3），挡位参数随挡位联动变化；自定义挡位默认全 ≈1（近无损）且不被其他挡位覆盖。

### D4. 质量评估（Quality evaluation）
- 缩小岛区域 → GPU 双线性采样 → **CPU 双线性放大回原尺寸** → 与原区域比较（与原规格一致）。
- 指标：MS-SSIM（短边<176px 回退单尺度 SSIM，<11px 忽略）+ CIEDE2000 均值 + alpha
  （Cutout 裁剪后 IoU / Blend 线性 RMSE，多材质多状态取最严苛）+ 法线角度误差 p95 + 灰度逐通道最差 RMSE。
- "全部达标才算通过"；缩放永不大于 1（不上采样），并受密度 [minPx/m, maxPx/m] 与原始物理尺寸钳制。

### D5. Pull-push（无限外扩）
GPU 跳 flood（JFA）：种子遍 → 步长减半传播 → 汇聚。>4096 图集在半分辨率工作区运行后放大合成，
控制内存（~1.5GB 峰值，可接受）。透明贴图种子 alpha=0 → 填充保持透明。

### D6. 资产安全（Asset safety）
NDMF 在**克隆体**上烘焙，但材质/网格/剪辑/控制器资产仍是用户原资产。
ATO 一律：材质用 `new Material`、网格/剪辑/控制器用 `Object.Instantiate` 克隆后再修改，
再通过渲染器/Animator 引用接入克隆体；由 NDMF 在 Serialize 阶段保存。绝不原地修改用户资产。

### D7. 白名单与回退（Whitelist & fallback）
- 白名单对象类型不限（网格/材质/贴图/动画/渲染器/GameObject）；其引用贴图跳过**所有**优化。
- 同 UV 其他贴图跳过图集化但参与整图缩放与导入参数优化（因为共享 UV 不能重排）。
- 任意不确定路径（ST 变换含动画、MatCap、跨缝、超界、无法分析 shader、未知属性种类…）→ 自动白名单 + warning。

### D8. 可选依赖（Optional dependencies）
- AAO 桥接放独立 asmdef（`defineConstraints: ["ATO_AAO_API_AVAILABLE"]` + versionDefines `com.anatawa12.avatar-optimizer >= 1.8.0`），缺 AAO 自动编译为桩。
- Burst 用 `ATO_BURST_AVAILABLE` 守卫，不可用时回退托管实现。

## 3. 质量挡位研究依据（Tier rationale）

| 挡位 | targetQuality | minSSIM(MS-) | maxΔE00 | αRMSE | Cutout IoU | 法线 p95 | 灰度 RMSE |
|---|---|---|---|---|---|---|---|
| Ultra 近无损 | 1.0（跳过重采样） | 0.999 | 0.5 | 0.002 | 0.9998 | 0.25° | 0.002 |
| High（默认） | 0.95 | 0.98 | 1.0 | 0.005 | 0.999 | 0.5° | 0.005 |
| Medium | 0.90 | 0.96 | 2.3 | 0.012 | 0.996 | 1.0° | 0.012 |
| Low | 0.85 | 0.94 | 3.5 | 0.020 | 0.990 | 2.0° | 0.020 |
| Minimum | 0.80 | 0.91 | 5.0 | 0.032 | 0.980 | 3.0° | 0.032 |

依据：
- **SSIM/MS-SSIM**：Wang et al. (2004) 结构相似度；实践共识 0.95+ 难察觉、0.99 近无损（Netflix/B站 转码实践区间）。
- **CIEDE2000**：Sharma-Wu-Dalal (2005)；人眼 JND ≈ 1.0~2.3（ISO/CIE 感知阈值）。
- **Alpha**：Cutout 用裁剪后覆盖率 IoU（与视觉一致），Blend 用线性 alpha RMSE（阈值 1/255 量级对应 8bit 一档）。
- **法线角度**：p95 角度误差 0.5° 内不可感知（业界法线压缩标准区间）。
- **密度**：默认 2048~4096 px/m（VRChat 常见 avatar 经验值；可选手动 512~8192）。

> 以上为工程参考默认值，用户可随时在"自定义挡位"中修改，不会被其他挡位覆盖。

## 4. 规格映射与已知取舍（Spec mapping & trade-offs）

| 规格点 | 实现 | 备注 |
|---|---|---|
| NDMF 排序 MA后 AAO前 | Optimizing 阶段 + `BeforePlugin("com.anatawa12.avatar-optimizer")` | AAO 缺装安全（NDMF 可选弱约束，已验证源码） |
| AAO UVUsageCompabilityAPI | `AAOCompatBridge`（转移原始 UV 到备用通道并注册） | 仅 SkinnedMeshRenderer；无空闲通道时 warning |
| lilToon/标准 shader 自动分析 | `ShaderPropertyTable`（ShaderUtil 属性+标记+UVMode+ScrollRotate+Toggle） | 未知 → 白名单 + warning |
| 多通道 UV | 按通道拆分为独立 UV 组 | — |
| 越界平移 / 跨缝 | `IslandCore` 判定；平移用取模内容矩形采样 | 跨缝 → 白名单 + warning |
| 重叠岛合并 | `IslandCore.MergeOverlapping`（AABB 保守合并） | — |
| 各向异性 | 先均匀达标后双轴独立二分细化 | — |
| 形态键 0/100 | 动画形态键按 100 权、静态按当前权，世界尺寸膨胀 | 不枚举组合 |
| 动画缩放 | `WorstLocalScale` 取最大 | — |
| 渲染模式/Cutoff 动画 | 取最严苛（min cutoff / 最高要求模式） | — |
| 去重（含白名单传播） | 像素 MD5 + 导入设置指纹；成员白名单 → 结果白名单 | — |
| 生成图集开关 | `generateAtlas=false` → 整图缩放 + 导入参数 | — |
| 图集格式安全枚举 | `ATOCompressionFormat` + 平台安全规则（BC→PC、ASTC/ETC2→Android、PVRTC→iOS、NPOT 剔除 PVRTC） | 透明贴图不给无 alpha 选项；灰度多通道回退 + 警告 |
| Mip 与 MipStreaming 联动 | 单开关同时控制二者 | VRChat 要求 |
| 平台覆盖 | `ATOSettingsData.Resolve(platform)`，PC/Android/iOS 全参数覆盖 | 默认读当前构建目标 |
| 组件合规 | 每 Avatar 仅一个 + 必须挂 VRCAvatarDescriptor，违规报错中止 | — |
| 烘焙后移除自身 | 从 NDMF 克隆体 `DestroyImmediate(ATOSettings)` | — |
| 取消 | `ATOCancellation` 轮询进度条取消 + 编译中止 | 取消时保留硬盘临时资产、释放资源 |
| 报告 | 每阶段耗时、图集来源/岛数/尺寸/利用率、相对优化量；折叠细节 | `[ATO]` 前缀 |
| i18n | `Resources/i18n/*.json` 扫描；Auto 跟随 NDMF；缺失回退英文 | en-US/zh-CN 已提供 |

### 已知取舍 / 迭代点（Known limitations）
1. **指标计算**：重采样走 GPU（RenderTexture 双线性），指标在 CPU 并行（`Parallel.For`）计算；
   Burst 已用于装箱光栅化；指标核的 Burst 化列为后续迭代（当前与 dotnet 单测共用同一份纯 C# 数学，
   保证正确性可验证）。
2. **JFA 填充**：>4096 图集用半分辨率工作区（内存受限），填充经双线性放大合成，精度足够（padding ≤ 64px）。
3. **动画覆盖控制器（AnimatorOverrideController）**：v1 不展开其 clip；仅收集普通 AnimatorController 与
   旧版 Animation 的剪辑（扩展点预留）。
4. **整图缩放与剪辑直接贴图引用**：材质引用会被替换；剪辑中直接引用原贴图的对象引用曲线保持原贴图
   （安全性优先，不悬空），记录 warning。
5. **Burst BLF**：BLF 顺序性天然单线程，v1 用托管实现（正确性经模糊测试）；光栅化与质量像素循环为并行热点，已 Burst/多线程。
6. 材质槽合并仅在子网格数与材质数一致且槽位无动画属性时执行。
