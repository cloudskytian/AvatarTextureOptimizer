# CLAUDE.md — AvatarTextureOptimizer 项目记忆 / Project Memory

> 本文件是本项目的唯一记忆载体。每次会话开始/网络中断恢复后必须先读本文件。
> This file is the single source of project memory. Always read it first when resuming work.
> 所有修改必须 git 提交，并同步更新本文件的进度与注意事项。

## 0. 项目概述 / Overview

- **项目**: AvatarTextureOptimizer (ATO) — 面向 VRChat Avatar 的开源 NDMF 贴图优化工具
- **包名**: `net.fosa.avatar-texture-optimizer`，版本 0.1.0-alpha.1，Unity 2022.3
- **目标**: 质量驱动(M-SSIM/ΔE/alpha/法线角度)的 UV 岛缩放 + 类型组图集装箱 + 动画感知引用重映射
- **流水线位置**: NDMF Optimizing 阶段, MA(`nadena.dev.modular-avatar`)之后, AAO(`com.anatawa12.avatar-optimizer`)之前
- **交付形态**: 纯包(非完整工程)，用户手动同步进 Unity 验证

## 1. 依赖库源码研读结论（已逐字验证的 API 事实）/ Verified API facts

- **NDMF 1.14.4**: `Plugin<T>`/`Pass<T>` + `[assembly: ExportsPlugin(typeof(MyPlugin))]`；
  `Configure()` 内 `InPhase(BuildPhase.Optimizing).AfterPlugin("...").BeforePlugin("...")` 均为 Sequence 方法；
  注册 Pass: `sequence.Run(MyPass.Instance)`（可用 `WithRequiredExtensions(new[]{typeof(AnimatorServicesContext)}, s => s.Run(...))`）。
- **BuildContext**: `AvatarRootObject/Transform`, `ObjectRegistry`(GetReference/RegisterReplacedObject — 仅用于错误追踪，**不会**重写其他资产引用),
  `ErrorReport`(静态 `ReportError(IError)`), `AssetSaver`(SaveAsset/IsTemporaryAsset/SaveAssets), `GetState<T>`, `PlatformProvider`。
- **NDMF 无内置 ProgressBar API** → 用 `UnityEditor.Progress`；取消 → 抛 `OperationCanceledException`。
- **NDMF 语言**: `nadena.dev.ndmf.localization.LanguagePrefs.Language` ("zh-hans"/"en-us"...)。
- **动画非破坏改写**: `context.Extension<AnimatorServicesContext>().AnimationIndex.ClipsWithObjectCurves` +
  `VirtualClip.GetObjectCurve/SetObjectCurve(binding, curve|null)`，无需手动 commit（MA BlendshapeSync 范式）。
- **AAO 1.9.17**: 插件名 `com.anatawa12.avatar-optimizer`；API 程序集 `com.anatawa12.avatar-optimizer.api.editor`；
  `UVUsageCompabilityAPI.IsTexCoordUsed(SMR,int)` / `RegisterTexCoordEvacuation(SMR,orig,saved)`，未初始化时抛 InvalidOperationException → 反射调用 + try/catch。
- **VRC SDK 3.10.4**: `VRCAvatarDescriptor` 在 `VRC.SDK3.Avatars.Components`（程序集 VRC.SDK3A/VRC.SDKBase，无源码为编译DLL）。
- **lilToon 2.3.4**: 贴图属性 `_MainTex/_BumpMap/_Bump2ndMap/...`；matcap/灯光记忆图等为非网格UV采样。
- **avatar-compressor**: NDMF 插件范式 + 不可读贴图 GPU 回读(Blit→RT→ReadPixels, GL.sRGBWrite 开关) 已被借鉴。

## 2. 架构决策日志（Coder A/B/C 共识）/ Design decisions

| # | 决策 | 备选与理由 |
|---|------|-----------|
| 1 | 装箱按**贴图队列**: 同贴图全部岛尽量同一图集(优先已指派图集), 装不下按候选池新建, 单张贴图都装不进最大图集→整UV组级联回退 | 规格"每个队列以单个贴图及其UV组为原子操作" |
| 2 | **UV组共享缩放(木桶效应)**：同UV全部贴图取各轴最大scale；`individualScale` 快照保留个体阈值→图集整体收缩依据 | 个体独立缩放会导致各图集归一化矩形不一致→UV错位 |
| 3 | 质量阈值档位(学术依据)：High MS-SSIM≥0.99/ΔE≤1.5/IoU≥0.98/αRMSE≤0.01/法线1°/2°/灰度0.004；Medium 0.97/3/0.95/0.02/2°/4°/0.008；Low 0.94/6/0.90/0.04/4°/8°/0.016 | 参照 Wang et al. MS-SSIM、CIEDE2000 JND≈1-2.3、SSIM≥0.95 惯例；默认 High |
| 4 | 密度挡位 512/1024/2048/4096/8192 快捷按钮(同时设 min=max)，默认 min2048/max4096 | 按用户规格 |
| 5 | padding = max(用户最小padding, ceil(图集边长/128))，候选池 POT/NPOT | 按用户规格 |
| 6 | **GPU pull-push(JFA跳跃洪泛)** 已实现(ATOPullPush.compute)；CPU 多源BFS 为回退 | 逐格最近种子颜色; 透明图集alpha=0 |
| 7 | **质量评估双路径**: Burst 行并行流水线(重采样→高斯矩H/V→指标汇总→p95, 逐行部分和防竞争); GPU 全分辨率重采样(原生分辨率→回读降采样到1024比较分辨率, 指标仍Burst确定性计算) | 512(CPU)/1024(GPU) 比较分辨率上限; 批量为"调度回合"并行评估全部活跃岛 |
| 8 | 动画引用经 AnimatorServicesContext 虚拟剪辑改写；材质/贴图/网格一律克隆 | 非破坏原则 |
| 9 | AAO 兼容用反射，疏散到空闲通道后注册 | 未装AAO也能运行 |
| 10 | 自定义质量挡位参数默认全最严苛=近无损 | 用户规格"默认全部为1"的等价解释 |
| 11 | AtlasOnly(同UV白名单)贴图: 整图缩放副本+导入参数优化, UV不动 | 白名单UV不能被改 |
| 12 | 白名单贴图同样加入岛映射以传播"同UV跳过图集化" | 否则共享UV岛会漏判 |
| 13 | **图集整体收缩**: g=min(√(个体面积/共享面积)), 图集缩到≥当前×g 的最小候选尺寸, 归一化矩形不变→无需重新评估(共享缩放余量即质量余量) | 实现规格"次要类型图集可缩放省体积", 对所有图集统一生效 |
| 14 | **同类型组UV伙伴必须与T同图集同矩形放置**(试装阶段发现并修复) | 否则同一UV在不同图集位置不同→UV错位 |
| 15 | 灰度被使用通道: 关键字分析(全引用可确认才收窄)+像素恒定兜底; 单通道格式与内容不符→构建期回退多通道+warning | 规格"先读关键字再像素兜底" |
| 16 | Inspector 按平台能力过滤压缩格式枚举 | 构建期另有兜底校验 |
| 17 | **装箱试装两阶段**: 模拟occ/profile副本上验证全部岛+伙伴, CommitPlan 才写回 | 失败不留半成品 |

## 3. Reviewer 记录 / Review findings (已修复)

- R1: WriteIsland 旋转采样比例反了(用了scale而非cropW/outW)且旋转尺寸未交换 → 与掩码转置/UV重映射统一为 rot1=out(x',y')=in(y',mw-1-x') 定义
- R2: CommitPlacements 非原子(先占用后失败留半成品) → 两阶段(全验证→全提交)
- R3: FallbackStandalone 无级联 → 失败UV组会级联撤除相关贴图全部岛的摆放
- R4: 图集贴图未回写 source.result → 材质引用不会更新 → 已回写
- R5: 最终去重映射类型错误(ATOTextureInfo↔Texture2D 混淆) → 统一 Texture2D 映射
- R6: 网格克隆后未赋回渲染器 → 已赋值 SMR/MeshFilter
- R7: 报告输出像素重复计数 → 改用 outputTextures 唯一列表
- R8: HashPixels/DetectAlpha 后可读拷贝未释放(内存峰值) → 用后即释放
- R9: 组件销毁时机在扩展后处理之前 → 移到 Pass 后处理之后
- R10: ATOAnimationAnalysis 缺 VRC using；Plugin/Pass 缺显式 public 构造器(new()约束) → 已补
- R11: 候选池面积未计 padding 边距 → 已计入 minPadding
- R12: Clamp 对独立贴图误强制 → 仅图集强制 Clamp

## 4. QA 记录 / QA records

- Q1 轮(本轮): 3 名 QA 独立通读全部 17 个代码文件 → 发现 R1-R12(已修复) + 规格缺失项(见 §6)
- **QA 结论: 未通过最终验收** —— 存在路线图缺陷(§6)，v0.1.0-alpha.1 为"可编译可烘焙的核心实现"，**不是最终成品**
- 需用户在 Unity 实际烘焙验证后反馈 bug，再迭代

## 5. 当前进度 / Progress

### 已完成 / Done (v0.1.0-alpha.2)
- [x] 包骨架(package.json/asmdef×2/Runtime组件+Inspector)
- [x] NDMF 插件/Pass/顺序/组件校验/烘焙后自移除/进度+取消/控制台报告
- [x] 收集: 渲染器/材质槽/动画扫描(槽切换/贴图属性/ST/Cutoff/渲染模式/启用/缩放/形态键)/贴图分类/白名单/贴图去重
- [x] UV岛(多通道/越界归一/重叠合并/形态键+缩放动画面积)
- [x] 质量算法: MS-SSIM/CIEDE2000/alpha IoU+RMSE/法线angle+p95/灰度逐通道RMSE(预乘线性+上采样回原尺寸)
- [x] **Burst 装箱作业**: 行并行光栅位掩码/膨胀/BLF/占用/列高/固定位校验 (ATOPackingJobs.cs)
- [x] **Burst 质量流水线**: 重采样/高斯矩H/V(行并行)/指标汇总/p95 (ATOQualityJobs.cs)
- [x] **批量二分搜索调度器** (ATOBatchSearch.cs): 调度回合并行评估全部活跃岛, 均匀→双轴细化, 纯色短路
- [x] **GPU 全分辨率重采样** (ATOResampleGPU.compute + ATOGpu.cs): 原生分辨率重采样+回读降采样(1024比较分辨率)
- [x] **GPU pull-push(JFA)** (ATOPullPush.compute): CPU多源BFS回退
- [x] 装箱: 按贴图队列/同贴图同图集/试装两阶段/候选池/90°旋转/padding/级联回退/**图集整体收缩**
- [x] 图集构建: 双线性旋转写入(预乘alpha)+JFA/BFS填充+导入参数(MipStreaming绑定/按类别格式/平台校验/NPOT剔PVRTC/单通道兜底/ReadWrite关+Clamp)
- [x] 应用: 网格克隆+UV重写+AAO疏散、材质克隆、动画虚拟剪辑重写、材质/贴图去重、不透明槽合并
- [x] 灰度被使用通道(关键字+像素兜底)、Inspector 平台格式过滤
- [x] i18n(en/zh-CN)、[ATO]日志、扩展接口、CLAUDE.md、README.md

### 未完成 / Remaining
- [ ] **未在 Unity 编译/烘焙验证** —— 最高优先级, 等用户反馈
- [ ] 单元测试/烘焙冒烟测试套件
- [ ] NDMF 预览支持(明确暂不支持, 需求未变)
- [ ] CHANGELOG.md

## 6. 已知问题与注意事项 / Known issues & notes

1. **未在 Unity 中编译/烘焙验证** —— 本环境无 Unity。compute shader 与 Burst 作业尤其需实测; 有报错请贴日志, 先取证再下结论。
2. Burst 指标与 CPU 参考实现(ATOQuality.cs, 回退路径)数值应一致(同一公式), 如不一致以 Burst 为准。
3. 装箱试装会为失败尝试留下空图集(构建期跳过, 无害)。
4. 级联回退后占用数组不清除 → 后续装箱只会更保守(浪费空间, 不会重叠, 安全方向)。
5. GPU 比较分辨率上限 1024(CPU 512); 法线贴图不走 GPU 路径。
6. 图集收缩依据 individualScale 快照(共享缩放前), 单调性保证不违反质量阈值。
7. 顶点被多岛共享且UV不一致 → 越界归一保守放弃。
8. 材质槽合并在动画切换该渲染器任何槽时禁用(保守)。
9. 取消烘焙: 临时资产保留, CPU/GPU/内存释放(ReleaseAll)。

## 7. 下次开工清单 / Next-session checklist

1. 读本文件 §2/§5/§6
2. 等用户 Unity 验证反馈 → 修复 → git 提交 → 更新本文件
3. 若编译报错优先检查: ATOBatchSearch/ATOQualityJobs 的 NativeArray 切片边界、compute shader 的 RWTexture 声明、asmdef 的 Burst 引用(Unity.Collections/Unity.Burst/Unity.Jobs 由 com.unity.burst 与内置模块提供)
