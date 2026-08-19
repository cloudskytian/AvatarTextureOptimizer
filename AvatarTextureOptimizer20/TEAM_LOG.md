# TEAM_LOG.md — AgentTeam 协作记录

角色：Coder-A / Coder-B（结对设计与实现）、Reviewer-1 / Reviewer-2（共识审查）、
QA-1 / QA-2（独立全量验收）。

## 第 1 轮：Coder 共识设计（写码前）

Coder-A 提案 / Coder-B 质询，达成的关键共识：

1. **调度**：Optimizing 阶段，`AfterPlugin("nadena.dev.modular-avatar")` +
   `BeforePlugin("com.anatawa12.avatar-optimizer")`（两个 QualifiedName 均从源码取证）。
2. **"同一UV在所有图集位置相同"的实现**：Coder-B 指出若按贴图独立装箱无法保证该不变量，
   最终共识为 PackUnit 模型——以 (Mesh,uvChannel) 映射为边做贴图并查集，连通分量整体
   作为原子装箱单元；动画切换的同UV贴图变体通过"同布局分层物理图集"解决。
3. **动画引用重写**：Coder-A 原提案手工深拷贝 AnimatorController，Coder-B 否决
   （层级/BlendTree/SyncedLayer 太易出错），改用 ndmf AnimatorServicesContext 的
   VirtualClip/AnimationIndex.RewriteObjectCurves——这是 ndmf 官方为此设计的机制。
4. **AAO 兼容**：versionDefines 硬引用 vs 反射。共识：反射（未安装 AAO 时零依赖，
   API 仅两个静态方法，反射成本可忽略）。
5. **法线旋转问题**：Coder-B 指出规格中"旋转90°+切线数据保持原样"存在物理矛盾
   （UV旋转后切线帧与法线XY不再匹配）。共识：含法线的类型组禁用旋转，其余组允许，
   既遵守"绝不重算切线"又保证渲染正确。已在交付说明中向需求方报告。
6. **质量评估管线**：GPU 批量重采样（预乘alpha/线性空间）+ 一次读回 + Burst 指标，
   逐岛二分先均匀后双轴，与规格一致。

## 第 2 轮：Reviewer 共识审查（Coder 提交后）

Reviewer-1 与 Reviewer-2 分别通读后合并意见，打回 Coder 修复：

| # | 发现者 | 问题 | 处置 |
|---|---|---|---|
| R1 | Rev-1 | `System.Numerics.BitOperations` 不在 Unity .NET Standard 2.1 | 打回：改手写可移植 popcount ✅ |
| R2 | Rev-2 | BakeStage GL 绘制误用 Resample pass 0（预乘）导致 RGB 被 alpha 污染 | 打回：改 pass 1 直拷贝 ✅ |
| R3 | Rev-2 | GL.LoadPixelMatrix y-down 与 ReadPixels y-up 双重翻转风险 | 打回：统一 y-up 约定并写入 CLAUDE.md 不变量 ✅ |
| R4 | Rev-1 | 旋转岛的 quad texcoord 与 IslandToAtlasPx 逐角验算不一致 | 打回：按映射逐角重推导 ✅ |
| R5 | Rev-1 | GridPacker 回滚使用了错误的缓存掩码（后放置岛的掩码清除先放置岛） | 打回：TryPlaceUnit 逐岛保存 stamped 掩码 ✅ |
| R6 | Rev-2 | PullPush 最终合成 pass 会把空白区 alpha 拉高，违反"透明贴图 alpha 保持 0" | 打回：新增 shader pass 2（RGB取填充、alpha取原图）✅ |
| R7 | Rev-2 | IslandStage 合并后 RemoveAll+IndexOf O(n²) 且语义错误 | 打回：改 kept 列表重建 ✅ |
| R8 | Rev-1 | ScanAnimations 中无用的 mats 克隆（易误导后续维护者） | 打回：删除 ✅ |

复审：Reviewer-1/2 确认全部修复，一致通过，移交 QA。

## 第 3 轮：QA 独立验收（两名 QA 各自从头全量通读）

**QA-1 报告**（逐文件通读 + API 取证核对）：
- 核对 ndmf `SimpleError.SafeSubstByKey` 的 `{0}` 占位符机制与 i18n 文件一致 ✅
- 核对 `InlinePass` 委托签名 `void(BuildContext)` ✅
- 核对 `VirtualAnimatorController : VirtualNode`（AllReachableNodes 可用）✅
- 核对白名单→整图缩放降级链路（badMappings→PackUnit 排除→BakeWholeScaled）✅
- 指出岛 srcRect 跨分辨率 ≤1px 对齐差 → 认定为可接受取舍（dilation 覆盖），记入 CLAUDE.md ✅

**QA-2 报告**（需求条目逐条对照 + 边界场景推演）：
- 逐条核对 40+ 项需求（映射复用/UV组/类型组/密度钳制/纯色短路/形态键0-100/动画缩放/
  多通道UV/越界归一/重叠合并/各向异性/Cutoff动画/NPOT/padding挡位/pull-push/
  格式安全兜底/Mip绑定/平台override/去重开关/单组件校验/取消/报告/i18n/扩展接口）
  → 结论：全部有对应实现路径，安全回退齐备。
- 边界推演：空 Avatar、无贴图材质、全白名单、quality=1、generateAtlas=off、
  仅动画启用的渲染器、单岛超过最大图集 → 各路径均有 guard/warning。
- 声明限制：**沙箱无 Unity，无法执行实机烘焙**；QA 验收为静态审查级别，
  已把实机验证清单写入 CLAUDE.md 供需求方首轮验证。

**QA 结论**：QA-1 ✅ 通过、QA-2 ✅ 通过（附实机验证清单）。允许打包交付 v0.1.0。
