# AvatarTextureOptimizer（ATO）

> 适用于 VRChat Avatar 的开源 **NDMF** 贴图优化工具。
> 分析网格 UV ↔ 贴图映射 → 按目标质量算法缩放 UV 岛 → 剔除未使用 UV → 重组图集，
> 在保证表现一致的前提下最大化贴图利用率。
> An open-source NDMF texture optimizer for VRChat avatars: analyzes mesh UV→texture mappings,
> scales UV islands by target-quality algorithms, trims unused UV space and re-packs textures into atlases
> to maximize utilization while preserving appearance.

- 包名 / Package: `net.fosa.avatar-texture-optimizer`
- 阶段 / Phase: `BuildPhase.Transforming`，**MA 之后 / AAO 之前**（After Modular Avatar, Before AAO）
- 依赖 / Depends: NDMF ≥ 1.14.4、VRChat SDK 3（avatars/base ≥ 3.10.4）、Burst、Collections、Mathematics
- 兼容 / Compatible: AAO（UVUsageCompabilityAPI，未安装 AAO 时自动跳过）、liltoon（属性表自动分析）
- 许可 / License: MIT

## 功能总览 / Feature Overview

| 模块 | 说明 |
|---|---|
| UV 岛分析 | Burst 光栅连通域提岛；重叠岛合并；形态键 0/100 最大面积；动画最大缩放；多通道 UV；越界整体平移归一（跨 wrap 缝 → 白名单 + warning） |
| 动画分析 | 材质/贴图切换、ST 变换、渲染模式/Cutoff 变更、渲染器启用、最大缩放；动画贴图并入基础 UV 组 |
| 贴图去重 | 按「实际像素 + 导入设置」去重并更新引用；白名单随去重保留 |
| 分类 | 烘焙时自动分析着色器属性表（liltoon/标准/未知），名称模式 + `[Normal]` 属性 + 特殊 UV 排除（matcap/反射/全景等）；ST 非单位或动画 → 白名单 |
| 类型组 / UV 组 | 类型组 = 用途集合 + 色彩空间 + filterMode（法线/蒙版图集利用率对齐）；UV 组 = 同一网格 UV 几何，模板布局共享 → 同一 UV 在不同图集同位 |
| 质量算法 | 线性空间重采样；透明贴图预乘 alpha 下采样；MS-SSIM（短边<176px→单尺度 SSIM；<11px 忽略）+ ΔE(CIEDE2000) + alpha（Cutout=轮廓 IoU / Blend=线性 RMSE，多材质取最严苛）+ 法线（解码→重采样→重归一化→编码，角度误差 mean+p95）+ 灰度（仅使用通道、线性 RMSE、最差通道）；比较限岛实际覆盖区（三角形栅格掩码）；缩小后双线性上采样回原尺寸比较；GPU compute 路径自检后启用，CPU（Burst 并行）兜底 |
| UV 缩放 | 二分搜索；先均匀达标再双轴独立细化；像素密度钳制（默认 min 2048 / max 4096 px/m，挡位 512–8192）；受原图真实大小钳制；UV 组木桶取最大（≤组内最大原尺寸）；纯色岛短路 min(4,短边)；近无损（质量=1）跳过缩放原样拷贝 |
| 图集装箱 | Burst 4px 位掩码光栅化；BLF 全扫描（粗网格加速）；面积降序 + 边长降序；90° 旋转步进（位掩码转置，法线绝不重算切线）；候选池 POT（64–8192，移动端 4096）/ NPOT 实验（64 步进，iOS 自动剔除 PVRTC）；队列 = 贴图+UV 组原子；装不下最大图集 → 开新队列/放弃该 UV 组图集化并 warning；图集数量自然增长 |
| Padding | ceil(候选图集最大边长/128) 向下钳制到 4；选项 4/8/16/32/64（默认 4）；岛边缘 GPU pull-push 无限外扩（透明 alpha 保持 0；CPU 扩张兜底） |
| 压缩 | 透明/不透明（按图集是否有 alpha 区分）/法线/灰度分类安全枚举；平台校验与安全回退（BC/ETC/ASTC/PVRTC）；Mipmap ⇔ MipStreaming 绑定（VRChat 要求）；强制 Clamp、关闭 Read/Write；NPOT 剔除 PVRTC；多通道灰度强制多通道保存 + warning |
| 平台覆盖 | PC / Android / iOS 全参数 override（勾选才显示、才生效）；默认读取当前构建平台 |
| 重映射 | 新网格资产（UV 重映射、顶点按岛拆分、形态键重建、`RecalculateUVDistributionMetrics` + NDMF opt-out）；材质克隆只改贴图引用；动画对象引用曲线重写（贴图/材质/槽索引） |
| 后处理 | 材质/贴图内容去重 + 材质槽合并（动画不单独切换时）并更新动画绑定 |
| AAO 兼容 | 反射调用 `UVUsageCompabilityAPI`：AAO 使用中的通道先复制原始 UV 到空闲通道并登记疏散 |
| i18n | JSON 语言文件（内置 en / zh-CN）；Auto 读取 NDMF 当前语言（`LanguagePrefs.Language`），无匹配回退英文；可手动切换 |
| 日志/报告 | `[ATO]` 前缀；每阶段耗时、图集来源、岛数、大小、利用率、相对原贴图优化量；构建完成输出报告（默认汇总、细节折叠）；详细日志开关 |
| 扩展 | `IAtoExtension` 接口（分析前/烘焙后/全部完成），`AtoExtensions.Register` + 自动发现 |

## 质量挡位 / Quality Presets

| 挡位 | qualityTarget | MS-SSIM | ΔE | alpha RMSE | Cutout IoU | 法线 mean/p95 | 灰度 RMSE |
|---|---|---|---|---|---|---|---|
| 近无损 NearLossless | 1.0（跳过缩放，原样拷贝） | 1.0 | 1.0 | 1.0 | 1.0 | 1°/1° | 1.0 |
| 高 High | 0.98 | 0.98 | 2.3 | 0.02 | 0.98 | 3°/8° | 0.02 |
| 中 Medium | 0.95 | 0.95 | 4.0 | 0.04 | 0.96 | 5°/12° | 0.04 |
| 低 Low | 0.90 | 0.90 | 8.0 | 0.08 | 0.92 | 8°/20° | 0.08 |
| 自定义 Custom | 默认全 1（近无损），可改，不被其他挡位覆盖 | | | | | | |

参数依据（见代码注释与本文档）：Wang & Bovik 的 SSIM/MS-SSIM 研究（0.95+ 通常视为感知近无损）；Sharma et al. 的 CIEDE2000 色差公式（ΔE≈2.3 为 JND 参考）；Levin et al. 的 pull-push；ILM 风格的纹理密度经验值（2048–4096 px/m）。

## 安装 / Installation

1. 将本包放入工程的 `Packages/net.fosa.avatar-texture-optimizer/`（或 VPM 方式添加）。
2. 在 Avatar 根物体（带有 `VRCAvatarDescriptor` 的物体）上添加 `Avatar Texture Optimizer` 组件。
3. 配置选项（质量挡位、图集、压缩、平台覆盖、白名单等），然后正常烘焙/构建。
4. 构建时 NDMF 会在 MA 之后、AAO 之前自动执行；控制台输出 `[ATO]` 日志与最终报告。

约束：一个 Avatar（含子物体）只允许一个组件；挂载对象必须带 `VRCAvatarDescriptor`，否则报错中止。

## 开发者 / For Developers

- 扩展：实现 `net.fosa.avatar_texture_optimizer.IAtoExtension`（`OnBeforeAnalyze` / `OnAfterBake` / `OnAfterAll`），经 `AtoExtensions.Register` 注册或自动发现。
- i18n：在 `Runtime/Localization/` 添加 `<语言码>.json`（`{"key": "text"}`）即自动加载并出现在语言选项中。
- 着色器兼容：分类器自动读取着色器属性表（`Shader.GetProperty*`）与 `[Normal]` 属性，未来 liltoon/标准着色器变更无需改代码；无法安全分类的贴图自动白名单 + warning。
- 日志：所有日志带 `[ATO]` 前缀；组件上可关闭详细日志。

## 已知限制与安全设计 / Known Limits & Safety

- 未实现 NDMF 预览支持（按需求暂不支持）。
- GPU 度量路径启用前会与 CPU 实现自检（容差 0.02），不一致自动回退 CPU 并告警。
- CPU 回退路径的分析分辨率上限 1024px（GPU 路径全分辨率）——见 `VERIFY.md`。
- pull-push 的 CPU/扩张回退为近似实现（渗色问题已知，够用）。
- 顶点按岛拆分会使共享顶点复制（UV 缝合处顶点数增加）；形态键自动重建。
- 白名单对象引用的贴图完全跳过（含导入参数）；同 UV 的其它贴图跳过图集化但保留整图缩放与导入优化。
- 所有临时资产位于 NDMF 临时目录（构建结束自动清理；取消时保留）。

## 目录结构 / Layout

```
Runtime/  组件、配置、日志、i18n、着色器（blit / quality metrics / pull-push）
Editor/   NDMF 插件、分析层、质量层、装箱层、烘焙层、后处理、AAO 兼容、Inspector
Documentation~/VERIFY.md   Unity 端验证清单
```
