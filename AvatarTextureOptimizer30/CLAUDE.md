# CLAUDE.md — 项目记忆 / Project Memory (AvatarTextureOptimizer)

> 本文件记录本项目的一切记忆：计划、已完成工作、整体进度、注意事项。每次修改后更新。
> This file is the single memory for this project: plan, completed work, progress, notes. Updated after every change.

## 项目概览 / Overview

- **名称**: AvatarTextureOptimizer（包名 `net.fosa.avatar-texture-optimizer`）
- **目标**: 世界最好的 VRChat 贴图优化工具 —— 开源 NDMF 工具：分析 Avatar 网格，建立 UV→贴图映射，
  按目标质量算法缩放 UV 岛、剔除未使用区域、重排图集，最大化贴图利用率且保证视觉一致。
- **用户**: 默认面向小白（常规选项直观），支持高级用户（质量参数折叠在高级选项）。
- **运行位置**: NDMF Optimizing 阶段，MA (`nadena.dev.modular-avatar`) 之后、AAO (`com.anatawa12.avatar-optimizer`) 之前。
- **目标环境**: Unity 2022.3 LTS+，VRCSDK 3.10.x，NDMF 1.14.4，C# 9。

## 架构 / Architecture

```
Runtime/Fosa.AvatarTextureOptimizer.asmdef（零依赖：组件+配置+白名单）
Editor/Fosa.AvatarTextureOptimizer.Editor.asmdef
  ├─ ATOPlugin.cs                  NDMF 插件注册（MA 后 AAO 前）
  ├─ Build/ATOBuildSession.cs      管线编排（验证→RW→扫描→去重→岛→引用→AAO→求解→组→装箱→合成→写入→材质→去重/槽合并→动画→UV→输出去重→移除自身→报告）
  ├─ Build/ATOLog.cs              [ATO] 日志 + 阶段计时注册表 + 进度/取消
  ├─ Build/ATOReport.cs           NDMF 控制台报告（IError，总体+折叠细节）
  ├─ Build/ATOI18n.cs             json i18n（Auto=NDMF 语言，回退英文，可扩展）
  ├─ Analysis/ATOAvatarScanner     渲染器/槽/白名单/贴图注册表
  ├─ Analysis/ATOMaterialAnalyzer  着色器属性表 + lilToon 源码解析 + ST/Cutoff/关键字
  ├─ Analysis/ATOAnimationAnalyzer 启用性/缩放/槽切换/贴图切换/float 属性动画
  ├─ Analysis/ATOTextureDedup      内容+导入设置去重（白名单传染）
  ├─ Mesh/ATOIslandExtractor      岛提取/越界归一/世界面积（实例+动画+形态键0/100）
  ├─ Mesh/ATOIslandRefBuilder     (贴图×角色)引用 + 裁剪矩形 + 合并岛偏移
  ├─ Quality/ATOMetrics           Burst CPU：MS-SSIM/SSIM/CIEDE2000/法线角度/IoU/RMSE/线性预乘重采样
  ├─ Quality/ATOGpuMetrics + ATOCompute.compute  GPU 同语义路径（回退 CPU）
  ├─ Quality/ATOIslandScaler      均匀二分→双轴细化；纯色短路；密度钳制；近无损跳过
  ├─ Quality/ATOQualityEvaluator  源裁剪缓存 + 调度 + 贴图级释放
  ├─ Sampling/ATOIslandCrop       sRGB/线性、预乘、法线解码/编码、纯色检测
  ├─ Atlas/ATOBitmask             4px 粒度位掩码（SAT 光栅化/转置旋转/BLF/Stamp，Burst）
  ├─ Atlas/ATOPackItemBuilder     贴图刚性装箱项（UV 相对布局 + 形状光栅化）
  ├─ Atlas/ATOAtlasPacker         候选池（POT/NPOT 排序）+ 箱复用 + 共享岛定位约束 + 角色缩放系数
  ├─ Atlas/ATOCompositor          合成 + 旋转 + 法线编码 + GPU pull-push（>4096 跳过并警告）
  ├─ Output/ATOTextureWriter      PNG+导入设置（分类/平台格式安全过滤、MipStreaming 绑定、强制 Clamp、RW 关闭）
  ├─ Output/ATOMeshWriter         网格克隆 + UV 重写 + AAO UV 迁移
  ├─ Output/ATOAnimationRewriter  动画曲线重写（贴图/材质替换 + 槽重绑）
  ├─ Output/ATODedup              材质去重 + 槽合并（含子网格合并）
  ├─ Output/ATOAAOCompat          UVUsageCompabilityAPI 反射桥
  ├─ Extensions/ATOExtensions     第三方扩展接口 + 注册表
  └─ UI/ATOAvatarTextureOptimizerEditor  IMGUI 检查器
Tests/Editor                     NUnit 单元测试（15 个，全部通过）
Verify/                          编译验证（Unity API 桩 + dotnet；不随包分发）
Localization/                    ato.i18n.en.json / ato.i18n.zh-Hans.json
```

## 关键设计决策（已实现）/ Key Design Decisions (implemented)

1. **装箱模型**: 类型组共享"归一化布局"（岛 → 位置/尺寸/旋转，记录在 `ATOTypeGroup.layout`）；
   每张贴图及其 UV 组为刚性原子项；岛矩形 = 木桶最大尺寸（4px 对齐）；
   同一 UV 在不同箱/角色图集上位置一致；同内容不同贴图（动画切换）因区域占用自然落入不同箱。
2. **角色图集缩放**: 每箱每角色取 min(岛角色尺寸/岛基础尺寸)（木桶），图集 = 箱尺寸 × (fU,fV)，布局按比例保持。
3. **动画贴图切换**: 场景路径 (path, prop) 内角色必须一致（冲突→白名单+警告）；资产级（path=""）绑定 → 整图路径。
4. **安全兜底**: 一切不确定 → 白名单 + warning；材质只改贴图引用，绝不动其他参数。
5. **AAO 兼容**: 反射调用 `UVUsageCompabilityAPI.IsTexCoordUsed/RegisterTexCoordEvacuation`（无空闲通道→白名单该通道）。
6. **质量挡位默认值**（学术依据: MS-SSIM Wang 2003；CIEDE2000 Sharma 2005（JND≈2.3，标准数据集已单测验证）；法线角度误差为法线压缩文献惯例；IoU 为分割惯例）：
   Ultra 0.9985/0.35/0.25°/0.999/0.0039/0.0039；High 0.995/0.75/0.5°/0.995/0.0078/0.0078；
   Standard(默认) 0.985/1.5/1.0°/0.985/0.0118/0.0118；Performance 0.96/3.0/2.0°/0.95/0.0235/0.0235；
   Custom 全 1（近无损，跳过缩放原样拷贝）。
7. **像素密度**: 默认 2048~4096 px/m（挡位 512/1024/2048/4096/8192），受源贴图物理像素钳制（scale≤1）。
8. **padding** = max(用户挡位(默认4), ceil(图集最大边长/128))；岛形状按 padding 膨胀后装箱。
9. **Mipmap 与 MipStreaming 绑定**一个开关（VRChat 要求），默认开；输出图集 RW 关闭、强制 Clamp（不给用户改）。

## 第三方库取证记录 / Third-party source verification notes

- NDMF 1.14.4（已读源码）: Plugin<T>/InPhase/AfterPlugin/BeforePlugin（缺失插件惰性空阶段，SolverContext.GetPluginPhases）；
  BuildContext（ObjectRegistry 静态/实例、AssetSaver 无路径 API → 用 AssetContainer 路径写 PNG、ErrorReport.ReportError(IError)）；
  Finish() 自动对临时网格 RecalculateUVDistributionMetrics。
- AAO 1.9.17: 插件名 `com.anatawa12.avatar-optimizer`（Optimizing）；`UVUsageCompabilityAPI` 在
  `Anatawa12.AvatarOptimizer.API`（程序集 `com.anatawa12.avatar-optimizer.api.editor`，拼写确为 Compability）；
  动画克隆模式 = new AnimationClip + ObjectRegistry.RegisterReplacedObject + 拷贝 m_UseHighQualityCurve。
- MA 1.18.2: 插件名 `nadena.dev.modular-avatar`（Transforming 主流程 + Optimizing GC）。
- VRCSDK 3.10.4: 程序集 `VRC.SDK3A` / `VRC.SDK3A.Editor`（NDMF asmdef 引用同款）。
- lilToon 2.3.4: 58 个 2D 属性（已核对角色表）；渲染模式由 shader 文件名区分（cutout/trans/fake）；
  `_Cutoff` clip；`_*_UVMode` 枚举（UV0~UV3，MatCap/Rim 非网格 UV → 白名单）。

## 验证状态 / Verification status

- [x] 全量代码 + 测试在 API 桩下编译通过（dotnet 8，LangVersion 9，0 错误）
- [x] 15/15 单元测试通过（含 CIEDE2000 Sharma 标准数据集 13 组）
- [x] Reviewer 共识审查（3 视角全量重读）→ 修复 9 项缺陷
- [x] QA 三重独立验收（需求符合性/正确性内存/工程健壮性）→ 修复 3 项缺陷 → 全部通过
- [ ] Unity 实机烘焙验证（用户手动验证，见 README 验证清单）
- [ ] GPU 路径实机验证（ComputeShader 与 CPU 阈值一致性）
- [ ] 性能基准（大 Avatar、多岛装箱耗时）

## 审查/验收修复记录 / Review & QA fix log

- R-A: 候选池面积单位（格→px²）；padding 两侧膨胀量；装箱失败贴图未走整图路径；
  贴图级图集化判定（部分岛与白名单共用 UV 时整贴图走整图路径，否则会破坏未重排 UV）
- R-B: 整图路径法线未解码/透明未预乘（现已解码 DXT5nm、预乘+还原、重归一化）
- R-C: 动画绑定路径唯一解析（嵌套 Animator；不唯一→整图路径）；材质去重改为按"优化后"内容；
  去重后动画引用更新；槽合并网格克隆链冲突（同步岛引用）；渲染器材质槽显式指向克隆（保险）
- QA-1: Cutoff 动画 → 采样 {当前, 0.25, 0.5, 0.75} 取最严
- QA-2: PackIslandRegistry 生命周期（合成阶段仍需要）；MS-SSIM 金字塔临时缓冲泄漏；移除冗余分配
- QA-3: 法线输出编码改为 RGB 法线字节（由 Unity 导入器按平台编码 DXT5nm/ASTC），不直接写 AG

## 已知限制与待办 / Known limitations & TODOs

- 形态键只取 0/100 面积（按需求；不处理 UV 形态键 delta —— 文档已注明）。
- 场景路径材质属性动画保守落到"该渲染器全部具备该属性的材质"。
- >4096px 图集跳过 GPU pull-push（罕见；已警告）。
- 装箱项内部布局 = UV 空间相对位置（效率与简单性折中）。
- 动画 float 曲线中形如 `m_Materials.Array.data[i].xxx` 的槽级材质属性曲线未重绑（罕见，Unity 惯例为 type=Material 绑定）。
- 报告字节数为估算（按格式 bpp）。
- TODO: 性能基准后按需为 BLF 增加列运行区间剪枝。

## 交付物 / Deliverables

- 包目录 `AvatarTextureOptimizer/`（可直接放入 Packages/ 或 Assets/ 手动同步到工程验证）
- 每次修改 git 提交；全部完成后打包 zip。
