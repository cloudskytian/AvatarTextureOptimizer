# AvatarTextureOptimizer (ATO)

> 全世界最好的 VRChat Avatar 贴图优化工具（目标）· The goal: the best VRChat avatar texture optimizer.
> 开源 NDMF 工具：分析 Avatar 网格，建立网格 UV→贴图映射，按目标质量算法缩小 UV 岛，
> 将 UV 岛重新打包进一个或多个图集，并安全优化导入参数，在保证观感一致的前提下最大化贴图利用率。

- **包名**：`net.fosa.avatar-texture-optimizer`
- **运行时机**：NDMF `Optimizing` 阶段——**Modular Avatar 之后、Avatar Optimizer 之前**（缺装 AAO 不受影响）
- **依赖**：NDMF ≥ 1.14.4、VRChat Avatars 3.10.x、Burst/Collections/Mathematics（随 NDMF 依赖链获得）
- **可选依赖**：Avatar Optimizer ≥ 1.8.0（启用 UV 兼容桥）

## 安装（Install）

1. 将 `net.fosa.avatar-texture-optimizer` 文件夹放入项目的 `Packages/` 目录（或通过 VPM 添加）。
2. 在 Avatar 的 **VRCAvatarDescriptor 所在对象**上添加组件：`Add Component → AvatarTextureOptimizer`。
   - 每个 Avatar 只允许一个组件；挂错位置会在烘焙时报错中止。
3. 在组件上按需配置（质量挡位 / 密度 / 图集 / 压缩 / 白名单 / 平台覆盖 / 语言），然后正常烘焙（Build & Publish 或 NDMF 触发）。

## 功能一览（Features）

- **UV↔贴图映射**：遍历材质槽（跳过 EditorOnly），结合动画（材质/贴图切换、启停、缩放、形态键 0/100、渲染模式与 Cutoff 动画）建立映射；多通道 UV 拆分为独立 UV。
- **目标质量缩放**：MS-SSIM（短边<176px 回退 SSIM，<11px 忽略）+ CIEDE2000 + alpha（Cutout IoU / Blend RMSE）+ 法线角度 p95 + 灰度逐通道 RMSE；"全部达标才算通过"；二分搜索先均匀后双轴细化（各向异性）；密度 [2048,4096] px/m 钳制（可选手动 512~8192）；纯色岛短路缩到 `min(4, 短边)`；目标质量=1（近无损）原样拷贝。
- **图集装箱**：Burst 4px 粒度位掩码光栅化 + 全扫描 BLF + 面积/边长降序 + 90° 旋转；候选池 POT（64..8192/移动 4096）或 NPOT（64 步进，实验性，自动剔除不支持格式如 iOS PVRTC）；岛形状装箱（非矩形）；padding = `max(ceil(最大边长/128), 最小4/8/16/32/64)`；GPU JFA pull-push 无限外扩填充。
- **类型组与 UV 组**：法线/蒙版与主色分图集防浪费；同 UV 所有贴图跨图集同位，杜绝混用出错；动画切换贴图并入同组。
- **安全**：绝不动材质非贴图参数；任何不确定路径（ST 变换、MatCap、跨缝、无法分析等）自动白名单 + warning；资产一律克隆后修改，绝不改用户原资产；`[ATO]` 日志 + 分阶段耗时/利用率报告；支持取消（保留临时资产、释放资源）；烘焙后自动移除自身。
- **去重**：贴图按像素+导入设置去重（白名单传播）；优化后再做材质/图集/材质槽去重（含子网格合并与动画槽位索引重映射）。
- **导入参数**：图集强制 Clamp、默认关闭 Read/Write；Mip 与 MipStreaming 单开关联动；按主色(不透明/透明)/法线/灰度分类的平台安全压缩格式；平台覆盖（PC/Android/iOS，参考 Unity platform override）。
- **i18n**：扫描 `Resources/i18n/*.json`（en-US、zh-CN 已内置），Auto 跟随 NDMF 语言，缺失回退英文。

## 给第三方开发者（For third-party developers）

- **扩展点**（预留接口）：
  - 质量指标：`QualityEvaluator.Evaluate` 与 `Pure.QualityMath`（纯 C#，可单测）。
  - 装箱：`Pure.AtoBLF` / `Pure.AtoGroupLayout` / `Pure.AtoRectBLF` / `Pure.AtoAtlasSizes`。
  - 着色器属性表：`ShaderPropertyTable`（未来 shader 自动兼容；无法分析自动白名单）。
  - 岛提取：`Pure.IslandCore`。
  - 平台/格式规则：`ImportSettingsApplier`、`ATOCompressionFormat`。
  - i18n：向 `Runtime/i18n/Resources/i18n/` 添加 JSON 即自动出现在语言选项中。
- **纯 C# 核心单测**：`Tools/AtoCoreTests`（dotnet 8 控制台），链接 `PackingCore.cs`、`QualityMath.cs`、`IslandCore.cs` 原样编译测试：`dotnet run --project Tools/AtoCoreTests`。
- **架构与决策**：见 `Documentation~/DESIGN.md`。
- **调试**：`PlayerPrefs "ATO.Verbose" = 1` 开启详细日志；所有日志 `[ATO]` 前缀。

## 兼容性说明（Compatibility）

- 与 AAO 的 `UVUsageCompabilityAPI`（拼写为 AAO 原文）兼容：AAO 会用到 UV 通道时自动转移原始 UV 到备用通道并注册。
- 白名单对象引用贴图跳过全部优化；同 UV 其他贴图跳过图集化但仍做整图缩放与导入参数优化。
- 已知取舍详见 `DESIGN.md` §4（指标核 Burst 化、JFA 半分辨率、OverrideController 展开、剪辑直接贴图引用等为迭代点）。

## 许可证（License）

MIT License（见 LICENSE）。

---
*AvatarTextureOptimizer 仍处于开发阶段（0.1.0）：配置字段可能随版本变化；任何"看起来对但未在真实 Unity 工程验证"的行为，请在验证后反馈。*
