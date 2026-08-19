# AvatarTextureOptimizer — 项目记忆

> 本文件是本仓库唯一的工作记忆。上下文被截断或中断后，必须先读本文件再改代码。
> 取证后再下结论。禁止凭「看起来像」猜第三方 API。

## 当前状态 / 整体进度

- **版本**：0.1.0（开发中，配置字段无兼容负担）
- **交付物**：UPM/VPM 包 `net.fosa.avatar-texture-optimizer`（不是完整 Unity 工程）
- **环境限制**：此沙盒没有 Unity Editor，**无法在此对模型做完整烘焙验证**。用户同步进工程后才能用真实 Avatar 验收。
- **已完成**：组件 / Inspector / i18n / NDMF 插件与校验 / 动画与着色器分析 / UV 岛 / 质量缩放 / 位掩码 BLF 图集 / 写回网格材质动画 / 去重 / AAO 软依赖 / 报告与取消 / 扩展 API / git 初稿
- **未完成 / 待用户验证**：在 Unity 2022.3 + NDMF 1.14.4 + VRC 3.10.4 工程里挂到带 `VRCAvatarDescriptor` 的 Avatar 上做一次完整 NDMF 烘焙，核对观感、控制台报告、图集与 fallback

## AgentTeam 共识（Coder ×2）

1. **可行性**：主流程可行。必须修正的设计见下一节，已按安全方向落地，而不是照抄会坏材质/法线的原文。
2. **执行阶段**：`BuildPhase.Optimizing`，`AfterPlugin("nadena.dev.modular-avatar")` + `AfterPlugin("nadena.dev.modular-avatar.late-transform-stages")`，`BeforePlugin("com.anatawa12.avatar-optimizer")`。这样 MA / late-transform / 多数 Transforming 插件（含 TTT，因为 MA late 已 AfterPlugin TTT）都跑完，且早于 AAO 的 Optimizing 主流程。
3. **AAO**：`UVUsageCompabilityAPI`（AAO 原文拼写）用反射软依赖。未安装 AAO 时跳过。该 API **只接受 SkinnedMeshRenderer**。
4. **法线 + 90° 旋转**：用户要求「切线保持原样、绝不重算」。若旋转 UV 岛而不转切线，切线空间法线会错。**含法线/切线空间图的类型组禁止旋转装箱**。Albedo-only 组允许位掩码转置 90°。
5. **质量挡位**（文献：Wang MS-SSIM；CIEDE2000 JND≈1；游戏法线公差）：
   - Lossless：跳过一切 UV 缩放（含纯色）
   - Ultra / High(默认) / Medium / Low：见 `AvatarTextureOptimizer.GetBuiltinPreset`
   - Custom：默认全 1，不被其他挡位覆盖；近无损时同样跳过缩放
6. **lilToon**：先读 2.3.4 属性与 AAO `ShaderInformation.Liltoon.cs`，再「属性表 + 关键字 + `_UseX` 开关」扫描，未知未来槽只要像网格 UV 就保留，无法安全判断则白名单 + warning。
7. **不改材质上除 Texture2D 引用以外的任何参数。**

## 对需求原文的纠正（必须告诉用户）

| 原文 | 问题 | 落地 |
| --- | --- | --- |
| 法线岛可旋转且不重算切线 | 光照方向错误 | 含法线的 UV/类型组禁止旋转 |
| AAO UV API 用于所有网格 | 实现只支持 `SkinnedMeshRenderer` | MeshRenderer 不 evacuate，打日志 |
| 形态键只取 0 与 100 | 中间值面积可能更大（非单调） | 按需求实现，记为已知限制 |
| `ShaderInformationRegistry.GetShaderInformation` | **internal**，不能当公共 API 用 | 自建分析器，不猜 AAO 内部表 |
| 处理放在「MA 后 AAO 前」 | MA 主体在 Transforming，AAO 主体在 Optimizing | 放 Optimizing + 显式 BeforePlugin AAO |
| 目标质量算法含 GPU+Burst | 必须有 CPU 权威路径 | CPU = MS-SSIM+CIEDE2000 权威；Burst 并行 ΔE；GPU 为粗比较可选 |

## 第三方库（已下载并阅读关键实现，禁止再猜）

路径：`/home/user/deps/`（不打进交付 zip）。

| 库 | 版本 | 用到的已核实 API |
| --- | --- | --- |
| nadena.dev.ndmf | 1.14.4 | `ExportsPlugin`, `Plugin<T>`, `Pass<T>`, `InPhase`, `AfterPlugin`/`BeforePlugin`, `WithRequiredExtension`, `BuildContext`, `AnimatorServicesContext`, `AnimationIndex.RewriteObjectCurves`, `ErrorReport`, `IError`, `Localizer`, `LanguagePrefs`, `IAssetSaver.SaveAsset`, `ObjectRegistry.RegisterReplacedObject`, `ErrorSeverity` |
| nadena.dev.modular-avatar | 1.18.2 | QualifiedName `nadena.dev.modular-avatar` 与 `nadena.dev.modular-avatar.late-transform-stages` |
| com.anatawa12.avatar-optimizer | 1.9.17 | QualifiedName `com.anatawa12.avatar-optimizer`；`UVUsageCompabilityAPI.IsTexCoordUsed` / `RegisterTexCoordEvacuation`（SMR, UV 0–7）；主流程在 Optimizing |
| jp.lilxyzw.liltoon | 2.3.4 | `RenderingMode`/`TransparentMode`；贴图属性与 `_Use*`、`_ST`、`_ScrollRotate`、decal 标志；着色器名切换 Cutout/Transparent |
| com.vrchat.avatars / base | 3.10.4 | `VRCAvatarDescriptor`；`IEditorOnly` |
| avatar-compressor / LLC | 参考 | NDMF 插件顺序与贴图属性目录，**不复制其代码** |

## 架构

```
Runtime/   组件 + 序列化设置 + 公共扩展接口（可挂在 Avatar 上）
Editor/
  Plugin/       NDMF 插件、校验、主通道、Session
  Analysis/     白名单、动画、着色器、去重、UV 岛、面积、Graph
  Quality/      解码缓存、MS-SSIM/ΔE/法线/灰度、二分缩放、GPU 粗比较
  Atlas/        候选池、4px Burst 位掩码、全扫描 BLF、pull-push、生成 ATO_* 图集
  Apply/        网格 UV、材质引用、动画曲线、后去重、压缩格式
  Compatibility/ AAO 反射桥
  Localization/  en.json + zh-Hans.json（有几个 json 显示几个语言）
  UI/           小白向 Inspector，高级质量/平台折叠
```

**处理顺序（与需求一致，发生在 MA 后 AAO 前）**

1. 校验：根上有且仅有一个组件，且与 `VRCAvatarDescriptor` 同物体，否则 Error 中止
2. 收集白名单对象引用的全部 Texture2D
3. 收集启用或被动画启用的 SMR/MR（跳过 EditorOnly）
4. 分析材质 + 动画（材质切换、贴图切换、ST、Cutoff、缩放、形态键、启用）
5. 贴图按「像素 + 导入设置」去重并改引用；去重源有白名单则结果也是白名单
6. 建 UV↔贴图、UV 组、类型组
7. 提岛 / 重叠合并 / 越界可平移则归一，跨缝则白名单+warning
8. 目标质量缩放岛（或不生成图集则整图缩放）
9. 类型组队列 + 候选图集池 + 单贴图及其 UV 组原子装箱；装不下最大图集则该 UV 组放弃图集化
10. 写 UV、赋贴图引用、改动画对象曲线；后去重；不透明同材质槽可合并
11. 压缩/MipStreaming；NDMF 报告；Destroy 自身（同时实现 `IEditorOnly`）

## 质量算法要点

- 线性空间重采样；透明预乘 Alpha 下采样
- 缩小岛双线性上采样回原包围盒再比
- 短边 < 176：单尺度 SSIM；< 11：忽略 SSIM
- 不透明：MS-SSIM + CIEDE2000
- Cutout：clip 后轮廓 IoU；Blend：线性 Alpha RMSE；多材质引用取最严
- 法线：解码→重采样→重归一→编码；平均角度 + p95
- 灰度：只在已用通道上线性 RMSE，逐通道取最差
- 先均匀二分达标，再双轴独立二分
- UV 组木桶取最大所需尺寸，且 ≤ 组内最大原尺寸
- 像素密度默认 min 2048 / max 4096 px/m，挡位 512…8192，并受原文件岛尺寸钳制
- 目标质量为 1 / Lossless / Custom 全 1：跳过缩放（含纯色）
- 非 1 时纯色岛缩到 `min(4, 短边)`
- 权威路径在 CPU；大岛 CIEDE2000 走 Burst `IJobParallelFor`；GPU compute 仅粗比较

## 图集要点

- 名称 `ATO_` 前缀
- padding = `max(minPadding, ceil(maxEdge/128))`，minPadding 默认 4
- 强制 Clamp；默认关 Read/Write（压缩后不再 Apply 以免打爆格式）
- 空白 GPU/CPU pull-push 外扩；透明图集空白 Alpha=0
- 默认 POT：64…8192（移动 4096）；NPOT：64 步进；NPOT 时剔除 PVRTC
- 同一源贴图的全部岛必须在同一图集
- 同一 UV 在各类型图集上位置相同（共享 layout）
- 图集数量不封顶
- 不支持 NDMF 预览（按需求）

## 日志

- 全部以 `[ATO]` 开头
- 每阶段耗时、图集来源、岛数、尺寸、利用率、相对原图像素数
- `verboseLogging` 控制是否把细节打到 Unity Console；NDMF 报告默认只展开总览

## 注意事项（改代码前必读）

1. **先读现有代码再改**。第三方 API 以 `/home/user/deps` 源码为准。
2. 每次改完必须让用户能在 Unity 里完整烘焙；本沙盒无法代替这一步。
3. 每次改完 `git commit`，并更新本文件的「当前状态 / 未完成」。
4. 发现可能让贴图或材质出错的设计，先停下来写进本文件并告知用户。
5. 白名单贴图跳过一切优化；同 UV 其他贴图跳过图集化，但仍可整图缩放与导入参数优化。
6. 取消：停烘焙、保留硬盘临时资产、释放 NativeArray / RT / 解码缓存。
7. 开发阶段可改序列化字段，不必做迁移。

## 已做工作清单

- [x] 下载并阅读 NDMF / MA / AAO / lilToon / VRC / 参考插件关键源码
- [x] 可行性与设计纠正
- [x] Runtime 组件与挡位
- [x] Editor 全流程代码
- [x] en / zh-Hans i18n
- [x] 扩展 API
- [x] 本记忆文件

## 下次开工

1. 用户把包放进 `Packages/net.fosa.avatar-texture-optimizer`
2. 在测试 Avatar 上挂组件，NDMF 烘焙
3. 根据控制台 `[ATO]` 与 NDMF 报告修编译/空引用/观感问题
4. 修之前先读相关 `.cs`，取证，再改，再 commit，再更新本文件
