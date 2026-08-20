# Reviewer 审查日志 / Reviewer Log

> 规则：任何代码变更后，Reviewer×3 共同审查（正确性/需求符合/性能与内存/生态兼容），
> 记录结论：PASS 或打回（附问题清单与责任 Coder）。
> Rule: after any code change, 3 reviewers jointly review; record PASS or
> bounce-back (with issue list + responsible coder).

## 轮次 / Rounds

（P0 代码提交后在此记录第一轮审查）

## Round 1（2026-08-21，Reviewer×3 联合）

审查范围：P0-P6 全部代码（逐文件）。

### 发现并已修复 / Found & fixed
1. **[严重] 共享资产破坏**：NDMF 构建克隆 GameObject 但不克隆共享资产；直接 SetUV/SetTexture/改导入器会修改用户源资产。→ Apply 阶段新增 PrepareMeshes/PrepareMaterials/CloneTexture：按需克隆 + ObjectRegistry.RegisterReplacedObject 重绑定；源资产零改动。
2. **[严重] 岛提取丢三角**：按顶点建图时 UV 不连续的孤立三角无岛表示 → 重映射后指向错误内容。→ 改为按三角形并查集（共享顶点对连接，孤立三角自成岛）。
3. **[严重] 向量动画曲线拷贝丢分量**：GetEditorCurve 只复制向量第一分量 → 改用 GetEditorVectorCurve/SetEditorVectorCurve。
4. **[高] UV 组含白名单贴图的 UV 重映射冲突**：重映射会破坏白名单贴图的原始映射。→ 新增 island.NoRemap：整组保持原 UV，非白名单贴图回退整图缩放；Quality/Pack/UVRemap/Resolver 各阶段联动。
5. **[高] 白名单状态迭代中传播不完整**：岛阶段中途白名单化的贴图未标记 tref，后续子网格/通道仍会为其建岛。→ WhitelistTexture 同步标记全部去重代表 tref。
6. **[中] 镜像图集上限被纯色岛错误压低** → 纯色岛不参与上限计算。
7. **[中] 导入器平台名错误**（target.ToString()）→ PlatformSettingsName（Standalone/Android/iOS）；iOS 自动格式 ETC2（该平台不支持）→ ASTC_8x8；Opaque+alpha 误含 Alpha8（丢 RGB）→ 剔除。
8. **[低] ChainMaxScale 空指针风险** → null 防护 + 深度上限。
9. **[低] RefineAxis 无用变量** → 移除。

### 保留的已知取舍 / Accepted tradeoffs（记录在案）
- SSIM 在 2048 分辨率上限内计算（大区域性能），其余指标全原尺寸。
- 材质比较含已知关键字表探测（无法枚举材质关键字）；未覆盖的关键字差异不会导致误合并（安全方向）。
- 形态键面积系数按 submesh 0 的索引近似。
- 无 AAO 时 MeshRenderer 的 UV 通道冲突：按 AAO 源码该 API 仅支持 SMR；MR 场景 AAO 组件若存在（RemoveMeshByMask 等），其 UV 使用未纳入检测——当前版本对 MR 不做通道撤离（文档说明）。

## Round 2（2026-08-21，Reviewer×3 联合，重点：质量/装箱/合成/无损模式）

### 发现并已修复
1. **[高] 无损模式 K 取值**：原实现用锚岛 px/UV，导致高分辨率成员被降采样、低分辨率成员被截断。→ K = 成员 px/UV 最小值（木桶），允许放大至 2x 原始上限，保证 UV 组归一化布局一致且内容安全。
2. **[低] AssignTargets 无损分支**：目标尺寸上限按模式区分（无损 2x / 有损 1x 原始）。

### 复查通过项 / Verified OK
- 装箱候选池排序（面积升序、最方优先）、部分装填开新页、最大图集装不下放弃+warning。
- 镜像页归一化布局一致 + 上限 = 质量允许上限（纯色岛除外）。
- 动画改写：向量曲线分量完整拷贝；槽索引重映射；材质去重链（原始->代表->克隆）经 ObjectRegistry 重绑定。
- 取消路径：Check 检查点覆盖全部长循环；原子 Apply 之前取消 = Avatar 未改。
- i18n：语言文件动态发现、Auto 跟随 NDMF、缺失回退英文。
