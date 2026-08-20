# AvatarTextureOptimizer — 项目记忆

## 目标
VRChat Avatar 的开源 NDMF 贴图优化工具。包名 `net.fosa.avatar-texture-optimizer`。

## 设计可行性结论（AgentTeam 共识）
整体逻辑**可行**。需要指出的设计修正：

1. **像素密度默认 2048–4096 px/m 偏高**：对 VRChat 人形常见面片会几乎不缩岛。已按需求保留默认，建议后续用实测调低 Medium 挡位密度。
2. **质量评估不含最终压缩损失**：按需求实现。用户选 ASTC/DXT 后观感可能差于指标。
3. **图集必须按 UV 组原子装箱**：已按此做，避免法线/主色错位。
4. **类型组升级规则**（同时被有法线/无法线材质引用 → 归有法线组）：已实现。
5. **NDMF 预览**：按需求暂不支持。
6. **贴图必须可读**：质量与装箱依赖 `isReadable`。不可读则跳过精细评估、仅密度钳制，并打日志。
7. **第三方 SDK 未内置**：用户自行同步 VRC/NDMF/MA/AAO/lilToon。AAO 用反射调用 `UVUsageCompabilityAPI`，未安装可编译。
8. **未完整下载并通读**用户给出的 zip（体积/离线限制）。API 仅使用公开稳定面（`Plugin<T>`、`BuildPhase.Optimizing`、`AfterPlugin`/`BeforePlugin`、`VRCAvatarDescriptor`、`IEditorOnly`）。**禁止猜测的接口一律反射或跳过**。
9. **GPU MS-SSIM**：本版本 CPU 实现完整（MS-SSIM / CIEDE2000 / 法线角度 / 灰度 RMSE）。GPU 路径预留，默认 CPU 以保证确定性。
10. **材质除贴图外不改参数**：遵守。
11. **取消烘焙**：进度条可取消；临时 `Assets/ATO_Generated` 保留；抛出 `BuildCanceledException` 中止 NDMF。

## 执行顺序
MA 之后、AAO 之前（`BuildPhase.Optimizing`）。

## 已完成
- 包结构、Runtime 组件、质量挡位、平台覆盖
- UV 岛提取、越界归一、重叠合并、形态键 0/100、动画扫描
- 目标质量二分 + 各向异性细化 + 密度钳制 + 纯色短路
- 位掩码 BLF 装箱、候选图集池、ATO_ 命名、Clamp、pull-push
- 网格 UV 回写、材质贴图重定向、去重
- i18n en / zh-Hans、Inspector、扩展钩子
- NDMF 插件、报告日志 `[ATO]`

## 未完成 / 需用户在 Unity 验证
- 真实 Avatar 烘焙观感
- Burst Job 与装箱主路径完全打通（Job 已写，装箱仍以托管位掩码为主）
- GPU pull-push / 批量 SSIM
- 导入器级压缩格式真正写入（CreateAsset 的 Texture2D 无 TextureImporter）
- 不透明材质槽合并与动画下标重写（骨架已有，复杂 Animator 需实机测）
- lilToon 全关键字表需对照 2.3.4 源码再收紧白名单

## 质量挡位（文献启发）
- Ultra: MS-SSIM 0.995 / ΔE 0.8
- High（默认）: 0.985 / 1.5
- Medium: 0.97 / 2.5
- Low: 0.94 / 4.0
- Custom: 默认全 1（近无损），不被其他挡位覆盖

## 注意事项
- 一个 Avatar 只允许一个组件，且必须与 `VRCAvatarDescriptor` 同物体。
- 日志前缀 `[ATO]`，`verboseLogs` 控制细节。
- 配置字段无版本迁移。
- 每次改动后应 `git commit` 并更新本文件。
