# AvatarTextureOptimizer (ATO) — AgentTeam Memory

> 本文件是本项目的唯一持久记忆。一切计划、进度、设计决策、注意事项只记录在这里。

## 项目身份

- 名称：AvatarTextureOptimizer
- 包名：`net.fosa.avatar-texture-optimizer`
- 类型：开源 NDMF 工具（VRChat Avatar 贴图优化）
- Unity：2022.3（与 NDMF 1.14.4 / VRC SDK 3.10.4 对齐）
- 语言：C# 9 兼容
- 日志前缀：`[ATO]`
- 图集名前缀：`ATO_`
- 暂不支持 NDMF Preview
- 版本：0.1.0（开发阶段）

## AgentTeam 共识（已执行）

### CoderA / CoderB

- 相位：Optimizing + AfterPlugin(MA / MA late / TTT) + BeforePlugin(AAO)
- AAO 只走反射，asmdef 不引用 AAO
- 图集写成 `Assets/ATO_Generated/` 独立 PNG + TextureImporter
- 法线 90°：切线不重算，旋转法线 XY
- 装箱原子：唯一 UV 岛（renderer+channel+uvBounds），同 UV 组所有贴图共享 Pack 坐标
- 按 semantic 分别出主色 / 法线 / 灰度图集
- NPOT 不全量笛卡尔积（烘焙不可行），改为 64 步进面积阶梯 + 正方形优先 + 2:1 / 4:3 / 3:2
- 质量评估：GPU Blit 解码 + CPU MS-SSIM/CIEDE2000；Burst 做像素哈希
- Custom 挡位永不被其它挡位覆盖

### ReviewerA / ReviewerB

打回并已修：

1. 主色/法线不能打进同一张图集且位置各异 → 改为唯一 UV 布局 + 分 semantic 出图
2. NPOT 64 步进全 W×H 候选会炸 → 改为面积阶梯
3. 装箱曾用包围盒冒充形状 → 改为三角形 4px 光栅
4. 质量二分曾把 density cap 当成搜索上界导致发糊 → 质量优先，density 只在仍达标时再收
5. UV 归一不得就地污染共享列表（已改为本地副本）
6. AAO 疏散必须写在新 mesh 的空闲通道上

仍接受的折中：

- 质量指标主路径是 CPU（Blit 负责重采样）。完整 GPU MS-SSIM 归约作为 compute 占位，避免未验证 kernel 决定烘焙结果
- 形态键面积用 0 与末帧（视为 100）包络，不做排列组合

### QAA / QAB（独立通读）

两人同时通过交付的条件：

- 插件相位、组件约束、白名单、资格过滤、去重、岛、质量、装箱、写回、去重、报告、取消、i18n、扩展 API 均有对应实现
- 不修改第三方库
- 不猜测 AAO / NDMF / lilToon API（均对照过源码）
- 本环境无 Unity，无法实机烘焙；用户同步工程后必须用真实 Avatar 验证

已知需用户实机验证的点：

- lilToon 关键字未来版本
- 复杂动画材质槽合并
- 多通道 UV + AAO RemoveMeshByMask 同机
- ASTC / BC5 在目标平台的导入器行为

## 对用户设计的修正（必须遵守）

1. 法线旋转补偿 XY，网格切线不重算
2. 执行相位见上
3. AAO API 类名就是 `UVUsageCompabilityAPI`
4. 生成贴图必须独立导入才能 MipStreaming
5. 目标质量 = 1 跳过缩放（含纯色）
6. 绝不修改材质上除贴图引用外的参数
7. NPOT 候选池做了烘焙安全的子集，覆盖用户要的步进与最大边长，但不是每个 64×64 矩形

## 目标质量挡位

| 挡位 | targetQuality | MS-SSIM min | ΔE00 max | Alpha RMSE | Cutout IoU | Normal p95° | Gray RMSE |
|---|---|---|---|---|---|---|---|
| NearLossless | 1.00 | 1.00 | 0.00 | 0.00 | 1.00 | 0.00 | 0.00 |
| Ultra | 0.90 | 0.995 | 0.80 | 0.010 | 0.995 | 3.0 | 0.010 |
| High（默认） | 0.75 | 0.980 | 2.00 | 0.030 | 0.980 | 8.0 | 0.030 |
| Medium | 0.55 | 0.950 | 3.50 | 0.060 | 0.950 | 12.0 | 0.060 |
| Low | 0.35 | 0.900 | 6.00 | 0.100 | 0.900 | 18.0 | 0.100 |
| Custom | 1.00 起，用户持有 | 1.00 | 0.00 | 0.00 | 1.00 | 0.00 | 0.00 |

像素密度默认 min=2048 px/m，max=4096 px/m。

## 第三方 QualifiedName

- MA: `nadena.dev.modular-avatar`
- MA late: `nadena.dev.modular-avatar.late-transform-stages`
- AAO: `com.anatawa12.avatar-optimizer`
- TTT: `net.rs64.tex-trans-tool`
- ATO: `net.fosa.avatar-texture-optimizer`

## 已完成

- [x] 阅读 NDMF / MA / AAO / lilToon / VRC / compressor / LLC 关键源码
- [x] 可行性评估与设计修正
- [x] 完整包结构、组件、设置、Inspector、i18n
- [x] NDMF 插件与流水线
- [x] 分析 / 去重 / 岛 / 质量 / 装箱 / 写回 / AAO / 报告
- [x] 扩展 API
- [x] README.md
- [x] zip 交付

## 未完成（需用户工程验证）

- Unity 内对真实 Avatar 完整烘焙与外观对比
- 根据实机日志再调质量默认值与装箱启发式

## 目录

```
Runtime/   组件 + 设置 + 公共 API
Editor/    NDMF 插件、流水线、分析、质量、装箱、写回、Inspector、i18n、Burst、Compute
```
