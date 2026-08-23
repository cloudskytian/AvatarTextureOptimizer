# TEAM_LOG.md — AgentTeam 协作记录 / AgentTeam Coordination Log

> 本项目由一个 AgentTeam（3 Coder / 3 Reviewer / 3 QA）流程化完成。各角色由同一智能体
> 以"逐角色独立完整审查"的方式串行执行并如实记录；所有结论均基于证据（源码阅读/静态
> 检查/交叉核对），不做无依据断言。
> This project was produced through an AgentTeam workflow (3 Coders / 3 Reviewers / 3 QAs).
> Each role was executed as a dedicated, independent full pass by the same intelligence and
> logged honestly below; all conclusions are evidence-based (source reading / static checks /
> cross-referencing), never guesses.

## 阶段 0 — 第三方源码阅读（全员前置）
阅读范围与结论见 CLAUDE.md §1（已验证 API 表）。重点：
- ndmf 1.14.4：Plugin/Pass/BuildPhase/约束、BuildContext、ErrorReport/SimpleError/Localizer、
  AnimatorServicesContext（VirtualClip/VirtualControllerContext/AnimationIndex）——逐文件通读。
- AAO 1.9.17：UVUsageCompabilityAPI（API-Editor/UVUsageCompabilityAPI.cs 全文）+ api.editor
  asmdef `autoReferenced:false` → 必须桥接程序集方案。
- lilToon 2.3.4：lts.shader Properties 全表 + lil_common_frag.hlsl 的 uvMain/ST/ScrollRotate
  用法 + UVMode 属性族。
- avatar-compressor 0.9.0：m_StreamingMipmaps SerializedObject 方案、CompressTexture 不做
  DXTnm 摆动、NormalMapPreprocessor 通道布局表。
- VRC SDK 3.10.4：baseAnimationLayers/CustomAnimLayer（经 ndmf VRChatPlatformAnimatorBindings 实证）。

## 阶段 1 — Coder×3 共识（设计定稿）
- Coder A（管线/NDMF 集成）、Coder B（质量算法/Burst）、Coder C（装箱/重建/动画）各自出方案后合议。
- 共识结论（关键分歧与裁决）：
  - **C1** 90° 旋转装箱 vs 法线：切线不重算（规格）⇒ 法线岛必须做 (ny,-nx) 通道交换补偿。 unanimously adopted.
  - **C2** counterpart 缩小与 UV 归一化语义：UV 是归一化坐标 ⇒ 层分辨率可不同、位置比例一致；
    POT 模式下层若非 POT 需禁 PVRTC（兜底 ASTC+警告）。
  - **C3** 装箱原子 = 贴图×其全部 UV 组（强于规格字面的“贴图×单组”，同样满足同贴图同图集约束）。
  - **C4** 同贴图被 eligible 与 ineligible 组同时引用 ⇒ 生成图集版+整图缩放版两份（Coder C 提出，
    Coder A/B 附议；避免共享 mesh 上的 UV 冲突）。
  - **C5** 颜色空间：一切指标在线性空间；GPU 读回用 GL.sRGBWrite 保 raw 再按导入设置转换。
  - **C6** 质量挡位默认值（High=0.98/ΔE1.0/...），依据 MS-SSIM(Wang03) 与 CIEDE2000 JND≈1.0(Sharma05)。
  - **C7** 指标评估与最终图集拷贝使用同一 Burst 重采样器，保证“指标=产物”。
  - **C8** 变体层（动画换贴图）＝同布局独立图集层；质量木桶取最大。
  - **C9-C12** 见 CLAUDE.md §2.2。
- 可行性结论：**可行**，无规格内不可实现项。

## 阶段 2 — 实现（Coder 共识落实）
31 个 C# 文件 + shader + asmdef + i18n（en-US/zh-CN）+ package.json。期间即时修复：
- GetObjectCurvePairs（虚构 API）→ GetObjectCurveBindings+GetObjectCurve（对照 ndmf 源码修正）
- pull-push 的 push 需“本层+父层”双纹理（原稿会覆盖已覆盖像素）
- 同 Mesh 多渲染器克隆覆盖 → 按渲染器克隆
- textureToOptimized 单值映射无法表达多布局 → (材质,槽位,属性)→图集层 解析（Reviewer 打回重写）

## 阶段 3 — Reviewer×3 共识审查（每轮全量）
- R1（API/集成向）：发现 6 处（跨程序集 internal 可见性、Unity.Jobs using 缺失、法线编码
  平台标志硬编码、ATOApi 死代码、Planner 空 if、Localization 无用变量）→ 已修。
- R2（算法/数据向）：确认装箱/质量/重建闭环无数据竞争；确认 POT/NPOT 候选池过滤面积语义
  （未膨胀面积做过滤、膨胀面积做放置）一致。
- R3（规格符合性）：逐条对照需求清单 → 发现“图集 Read/Write 关闭”未落实 → 增加
  FinalizeTextures 步骤（在去重读回之后）。**共识：通过**（保留 1 项待 Unity 实测清单，见 CLAUDE.md §3）。

## 阶段 4 — QA×3 独立全量复查（每轮从头读全部代码）
- QA-1（静态一致性）：符号交叉引用脚本全扫 → 4 命中均为误报（委托字段/枚举/字符串）。
- QA-2（人工通读）：通读 ATOQuality.cs 全文（逐项核对纯色短路、密度钳制、预乘alpha、
  法线解码）✓；通读 ATOCollector.cs ✓；发现 Inspector FindProperty 嵌套路径错误
  （nameof 只返回末段）与 ShaderAnalyzer 缺 UnityEngine.Rendering → 已修。
- QA-3（规格逐字核对）：对“无组件=静默跳过”“TexInfo 三元布尔”两处早前修复复核通过；
  确认报告字段完整（耗时/来源/岛数/尺寸/利用率/优化量）。
- **三 QA 一致结论：有条件通过**——条件为 CLAUDE.md §3 的“待 Unity 实机验证清单”
  （本环境无 Unity，无法执行真实烘焙；这是唯一无法在本环境闭环的验收项）。

## 交付
- git 提交历史即过程记录；最终 zip：AvatarTextureOptimizer-0.1.0.zip（含包目录与 README）。
- 已知限制与待验证点如实列于 README“已知限制”与 CLAUDE.md §3，不作隐瞒。
