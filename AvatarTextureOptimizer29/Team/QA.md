# QA 组过程记录（终审：三个 QA 独立从头完整查阅全部代码）

规则: 每个 QA 独立完整阅读全部源码 → 出具缺陷清单 → 三个 QA 全部 PASS 才可交付；
发现缺陷 → 通知 Reviewer+Coder 打回修复 → 三 QA 重新独立复审。
以下为真实执行的审查记录（发现即修复，修复后复审确认）。

## QA-1 独立审查（编译面：符号/using/签名/程序集）
发现并打回：
1. Rasterizer.cs 缺 `using Unity.Mathematics`（math.clamp/floor/popcnt）→ 已加。
2. TextureParams.cs 缺 `using UnityEditor`（SerializedObject/EditorUtility）→ 已加。
3. Progress.Report 调用点为三参（stage,t,info）但定义两参 → 签名改三参。
4. DownsampleJob/UpsampleJob 的 dstSize 标了 [WriteOnly] 但作业内读取 → 移除特性。
5. FinalDedup.HashPixels 未定义 → 已补 FNV-1a 实现。
6. AAOCompat 用 Type.GetType(带程序集名) 无法解析未引用程序集 → 改为遍历 AppDomain 全部程序集。
7. ATOPass 在「无组件」时报错中止 → 改为静默跳过（无 ATO = 不处理）。
8. Editor asmdef 引用名与 NDMF 实际 asmdef（nadena.dev.ndmf / .runtime）核对一致 ✔。
结论: 修复后 PASS。

## QA-2 独立审查（运行面：算法/数据流/Burst）
发现并打回：
1. SsimJob 内 `new NativeArray<float>[6]` 托管数组，Burst 无法编译 → 改 6 个显式缓冲。
2. PullPushJob 变长层数组同理 → 明确改为托管调度并注释（每页一次，非热路径）。
3. 最终去重排在压缩之后导致页不可读、像素哈希失效 → 调序：去重先于压缩。
4. Inspector Slider 实现存在闭包硬伤 → 整体重写（EditorGUI.BeginChangeCheck）。
5. AtlasBuilder: 同岛同类多贴图只合成第一张（多槽位页缺内容）→ 按（类别,槽位）逐贴图合成；
   旋转合成坐标/缓冲尺寸写反 → 以未旋转缓冲+转置写入修正；
   AnisoOf 无效条件 → 简化取来源最大。
6. MeshRewriter: 旋转未参与 UV 映射 → normRect 携带 rotated；岛归一化平移丢失 → session.uvOffsets；
   slotAtlased 判断逻辑错误 → 以「已装箱岛覆盖子网格三角形」重写；ArrayTrue 方向反 → 修正。
7. 装箱器 Overlap/Place 越界字与失败路径异常 → 整体重写（克隆试放+提交、带 padding 环、
   可见矩形含环偏移）。
8. 材质引用替换仅在克隆/临时资产上进行；ObjectRegistry.RegisterReplacedObject 已登记 ✔。
9. 动画材质/贴图替换走 VirtualClip ✔（与 MA/AAO 一致）。
10. 槽位合并后动画 m_Materials 索引位移已实现 ✔。
结论: 修复后 PASS。

## QA-3 独立审查（需求符合度逐条审计）
对照需求书逐条核对实现位置（代码文件-阶段），全部条目有落点：
组件唯一性/挂载校验(ATOPass)、白名单语义(UsageGraph+IslandExtractor)、ST/uvMain/UVMode/贴花/
视差/MatCap 资格(ShaderCatalog+ShaderAnalyzer)、动画新贴图并入与去重(AnimationAnalyzer)、
类型组与 UV 组(PackingComponent 签名+岛模型)、目标质量算法全套(Metrics+QualityEvaluator)、
密度挡位与钳制、纯色短路、质量=1 拷贝、挡位/自定义参数语义(ATOInspector+QualityPresets)、
图集开关、形态键/缩放面积因子(MeshAreaFactors+Scanner)、多通道UV、UV归一与跨缝白名单+warning、
重叠岛合并(岛内+跨网格)、各向异性细化、动画渲染模式/Cutoff 严苛化(DetectAlphaMode+AlphaModesOf)、
多用途贴图严苛化(ClassifyCategory)、lilToon 自动分析+未知着色器白名单+warning、
Burst 光栅+BLF+旋转90°+候选池+NPOT、装箱面积/排序/队列复用/原子分量/超大回退+warning、
padding 公式+挡位、pull-push 渗色(透明alpha保持0)、ATO_前缀、压缩安全枚举+兜底警告、
Mip/Streaming 绑定单开关、平台覆写与限制(8192/4096、ASTC-only、无PVRTC)、
Clamp/Read/Write 强制、贴图/材质去重与槽位合并、内存控制(LRU 像素缓存+finally 释放)、
进度+取消(保留资产)、组件自移除、NDMF 控制台报告(总览+明细+耗时)、MA后AAO前+UVUsage疏散、
扩展接口、i18n(en/zh-hans+可扩展+Auto回退英文)、双语注释。
已记录的偏差（需用户确认，均已在 CLAUDE.md/README 标注）：
a) pull-push 为 CPU 实现（非 GPU）；
b) padding 语义取 max(最小值, ceil(边/128))；
c) 质量评估区域上限 2048px 的归约近似；
d) MS-SSIM 采用掩码加权标准公式（窗口跨界不贡献），非原论文逐窗严格中心采样。
结论: PASS（含上述已声明偏差）。

## 最终裁决
三个 QA 全部 PASS（QA-1 编译面 / QA-2 运行面 / QA-3 需求符合度）。
允许交付 v0.1.0。
提醒: 尚未经真实 Unity 编译与实机烘焙验证，用户同步进工程后如遇编译/运行问题，
按「先读代码取证再下结论」流程打回修复。
