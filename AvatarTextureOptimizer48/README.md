# Avatar Texture Optimizer (ATO)

> 目标：全世界最好的 VRChat 贴图优化工具 —— 在保证视觉质量的前提下最大化贴图利用率。
> Goal: the best VRChat texture optimizer — maximize texture utilization while preserving visual fidelity.

一个适用于 VRChat Avatar 的**非破坏性 NDMF 工具**（`net.fosa.avatar-texture-optimizer`）。

- 分析 Avatar 网格，建立 **网格UV → 贴图** 的映射（无视材质其他参数，同贴图可跨材质复用）。
- 以导入后的有效贴图为基准，按**目标质量算法**缩放每个 UV 岛（不生成图集时缩放整张贴图）。
- 剔除未被使用的 UV 部分，重新分配 UV，把岛碎片重组成一个或多个**图集**。
- 执行时机：**MA 之后、AAO 之前**（NDMF Optimizing 阶段，`AfterPlugin("nadena.dev.modular-avatar")` + `BeforePlugin("com.anatawa12.avatar-optimizer")`）。

## 功能特性 / Features

| 模块 | 说明 |
|---|---|
| 质量算法 | 线性空间重采样；透明贴图预乘 alpha 下采样；MS-SSIM（短边 <176px 回退 SSIM，<11px 忽略）+ CIEDE2000 + alpha（Cutout 轮廓 IoU / Blend 线性 RMSE，跨引用材质取最严苛）；法线解码→重采样→重归一化→角度误差 p95；灰度逐通道线性 RMSE 取最差 |
| 质量挡位 | High / Medium / Low / Custom（自定义默认近无损，参数不被其他挡位覆盖）；换挡参数联动 |
| 像素密度 | 默认最小 2048px/m、最大 4096px/m，提供 512/1024/2048/4096/8192 挡位；受岛在原贴图上的真实尺寸钳制 |
| UV 组 | 同一 UV 对应的所有贴图（类型组 + 动画切换）构成一个 UV 组，保证**同一 UV 在不同图集上的位置一致** |
| 类型组 | 法线/蒙版等特殊贴图按 类型+色彩空间+filterMode 分组，避免"10 张主色 1 张法线 → 法线图集 9/10 浪费"；非主色类型组图集可在满足最小 padding 的前提下整体等比缩小（保持 UV 一致） |
| 装箱 | Burst 光栅位掩码（4px 粒度）+ 全扫描 BLF + 面积降序/边长降序 + 旋转 90°（位掩码转置）；按岛形状装箱，非矩形；候选图集池（POT 默认 / NPOT 实验选项，64 步进，最大 8192/4096，NPOT 时剔除不支持格式如 iOS PVRTC） |
| 图集 | padding = max(4, ceil(边长/128))，可自定义 4/8/16/32/64；GPU pull-push 无限外扩（渗色已知、够用）；命名 `ATO_` 开头；默认关闭 Read/Write、强制 Clamp |
| 导入参数 | 按 透明/不透明/法线/灰度 分类的压缩格式安全枚举；平台 override（PC/Android/iOS）；Mipmap 与 MipStreaming 强制绑定（VRChat 要求）；NPOT/格式合法性兜底并报 warning |
| 动画兼容 | 材质切换、贴图切换（并入原 UV 组）、ST 变换（视为白名单）、形态键 0/100 极值、物体最大缩放、渲染模式/Cutoff 取最严苛；动画剪辑贴图/材质引用自动重写 |
| 白名单 | 不限制对象类型（网格/材质/贴图/动画等）；白名单对象引用的全部贴图跳过所有优化；同 UV 其他贴图跳过图集化但参与整图缩放与导入参数优化 |
| 安全 | 只处理安全采样的 Texture2D（无 ST 变换、非特殊用途如 MatCap/贴花）；任何不满足条件 → 视作白名单并报 warning；绝不修改材质除贴图外的参数；AAO 兼容（UVUsageCompatibilityAPI 反射调用，未装 AAO 也可） |
| 去重 | 入口处按 像素内容+导入设置 去重并更新引用（白名单去重结果也是白名单）；输出处材质/贴图去重开关 |
| 本地化 | 读取 json i18n（现有几个语言文件就显示几个语言）；Auto 跟随 NDMF 语言，回退英文；附带 en-US 与 zh-CN；注释中英双语 |
| 报告 | `[ATO]` 日志（每步耗时、图集来源、岛数量、图集大小、利用率、相对原贴图优化量）；构建完成输出到 NDMF 控制台，默认总体、细节折叠（verboseLogs 展开） |
| 取消/进度 | 构建时进度条支持取消；取消保留硬盘临时资产并释放 CPU/GPU/内存；烘焙后自动移除自身组件 |

## 安装 / Install

1. 把 `net.fosa.avatar-texture-optimizer` 文件夹放进 Unity 工程的 `Packages/` 目录（或通过 VPM 导入）。
2. 依赖（已由你的工程提供）：`nadena.dev.ndmf` ≥1.14.4、`com.vrchat.avatars` ≥3.10、Unity 2022.3。
3. 在 Avatar 根物体（带 `VRCAvatarDescriptor`）上添加组件 **Avatar Texture Optimizer**。
   - 每个 Avatar 只允许一个该组件；非法挂载会在构建时报错中止。
4. 正常执行 NDMF 烘焙 / 上传即可。运行顺序：MA → **ATO** → AAO。

## 使用 / Usage

- 默认参数即为通用最优解，小白可直接烘焙。
- 高级用户：质量挡位、像素密度、图集与装箱（NPOT、padding、pull-push）、压缩格式、平台 override、白名单均可调整。
- 需要把原 UV 备份给 AAO 时工具会自动做（UV 疏散到备用通道）。

## 架构 / Architecture

```
NDMF Pass (Optimizing, after MA before AAO)
└─ PipelineRunner
   ├─ AvatarAnalyzer        材质槽/贴图收集、限制条件判定、动画扫描、白名单、去重、UV 组构建、岛提取（并查集+重叠合并+形态键面积）
   ├─ IslandScaler          质量二分（均匀→双轴细化）、纯色短路、像素密度钳制、UV 组木桶效应
   ├─ PackingPlanner        统一布局画布（UV 一致性）、类型组图集分配、非主色组等比缩小、回退整图缩放
   ├─ AtlasBaker            GPU 岛绘制 + pull-push 外扩 + PNG/EXR 保存 + 导入器配置（压缩/平台/mip）
   └─ WriteBackProcessor    网格 UV 重映射、材质替换、动画重写、材质去重、移除组件、AAO UV 疏散
```

## 设计决策与已知取舍 / Design decisions & known trade-offs

1. **UV 一致性优先**：为满足"同一 UV 在不同图集上的位置相同"，采用**统一布局画布**：所有岛在同一个布局中装箱，各类型组图集复用同一布局坐标；非主色类型组整体等比缩小仍保持 UV 一致（整体缩放不变 UV）。代价：主色图集在主色岛稀疏时可能留白。**未实现**"各类型组独立装箱尺寸"（会破坏 UV 一致性）。
2. **材质槽物理合并**（把相同材质的子网格合并为一个子网格）**未实现**；改为把相同内容的材质去重到同一资产（引用自动更新），物理合并留给 AAO（在其后执行）。
3. **指标计算在 CPU 并行**（多线程），重采样/绘制/pull-push 在 GPU；岛光栅化使用 **Burst**。MS-SSIM 的完整 GPU 批处理版留待后续（接口已预留）。
4. **pull-push 为近似实现**（多次膨胀外扩），渗色问题符合预期（"够用了"）。
5. **lilToon 兼容**基于属性名/关键字启发式 + ST/特殊用途检测（属性名已对照 lilToon 2.3.4 源码核实）；无法判定时按白名单跳过并报 warning。
6. **动画贴图切换**：动画中的贴图并入原 UV 组；ST 类动画属性判定为不安全并跳过。
7. **多通道 UV**：默认只处理 UV0；勾选"处理全部 UV 通道"后 UV1..UV7 各自作为独立 UV 组。
8. 输出资产保存在 `Assets/ATO_Generated/<Avatar>/`（PNG/EXR + 导入设置）；上传后可自行决定是否保留。
9. 本版本**未包含**：ndmf 预览（按规格不支持）、完整 GPU MS-SSIM、自动化单元测试。

## 日志 / Logs

所有日志以 `[ATO]` 开头；构建完成后 NDMF 控制台输出报告：
```
================ [ATO] Avatar Texture Optimizer Report ================
STATUS: SUCCESS
Total time: 12.34s
Islands processed: 1234 | Textures processed: 56 | Whitelist skipped: 3 | Warnings: 1
Texels: 67,108,864 -> 16,777,216  (savings 75.0%)
Atlases generated: 2 (total 16,777,216 texels, savings 75.0%)
(enable verboseLogs on the component for detailed report)
=======================================================================
```
勾选组件的 verboseLogs 可展开各阶段耗时、图集来源、利用率等细节。

## 扩展 / Extending

- 各阶段以静态类 + 数据模型（`AnalysisResult` / `PackingResult` / `TexRecord` / `UVGroup`）组织，第三方可替换/扩展单阶段。
- i18n：在 `Editor/Localization/` 增加 `xx-XX.json` 即可新增语言，检查器自动显示。
- 质量指标集中在 `Quality/MetricMath.cs`，可扩展新指标。

## 验证 / Verification

本包在无 Unity 的环境下编写，**未经过 Unity 编译验证**。请在你的 Unity 工程中按以下清单验证：

- [ ] 两个 asmdef 是否成功编译（`net.fosa.avatar-texture-optimizer.runtime` / `.editor`）。
- [ ] 组件挂载与校验（无 VRCAvatarDescriptor / 多个组件时构建应报错中止）。
- [ ] 简单 Avatar 全流程烘焙：贴图→图集、UV 重映射、材质引用、动画引用。
- [ ] Cutout/Blend 透明贴图、法线贴图、蒙版贴图各类型。
- [ ] 动画切换材质/贴图后烘焙，运行时切换是否正常。
- [ ] AAO 已安装/未安装两种情况。
- [ ] 取消烘焙是否保留临时资产并释放资源。
- [ ] 白名单、平台 override、NPOT 选项。

遇到编译/运行问题请连同 Unity 报错反馈，我会据此修复。
