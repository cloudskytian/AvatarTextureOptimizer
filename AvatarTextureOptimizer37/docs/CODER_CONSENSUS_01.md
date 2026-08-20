# Coder 共识记录（第 1 轮）/ Coder Consensus Round 1

> 参与者：Coder-A（架构）、Coder-B（图形/算法）、Coder-C（NDMF 生态/集成）
> 日期：2026-08-21。记录讨论要点、结论与落选方案。

## 议题 1：管线在 NDMF 中的落地形态

- Coder-C：NDMF 1.14 是新的 Plugin/Pass 架构（旧 NDMFModule/ModuleBuilder 已不存在）。AAO 与 MA 都在 Optimizing/Transforming 相位工作；AAO 用 U+FFDC 命名强制排最后。
- Coder-A：主张**单 Pass**（而非多 Pass）：原子性最好（中间状态不暴露给其他插件）、取消语义简单、调试路径短。
- Coder-B：担心单 Pass 太长不可中断——回应：阶段边界检查点 + 进度窗口，粒度足够。
- **结论**：单 Pass（`ATOPipelinePass`）运行于 `BuildPhase.Optimizing`，`AfterPlugin("nadena.dev.modular-avatar").BeforePlugin("com.anatawa12.avatar-optimizer")` 显式弱序约束 + ASCII QualifiedName（双重保险）。

## 议题 2："报错中止构建"与取消

- Coder-C：NDMF Pass 异常会被捕获并汇入 ErrorReport（构建因 Error 失败），不会中断后续 Pass。没有官方取消钩子。
- 讨论：能否让 ATO 之后的 AAO 不跑？不能（除非 hack NDMF，不可接受）。
- **结论**：
  - 致命配置错误 → 抛 `ATOPipelineFatalException` → ErrorReport（VRC 构建失败，信息可见）。
  - 用户取消 → 抛 `ATOPipelineCancelledException` → 同上；**且由于"阶段 1-6 只算 PLAN、阶段 7 才写对象"，取消时 Avatar 一定未被修改**；临时资产按用户要求保留在磁盘。

## 议题 3：UV 组"位置相同"与"法线图集可缩小"是否矛盾

- Coder-B：若法线图集像素尺寸不同而布局相同（归一化坐标一致），则 UV→内容映射在每张图集上各自正确；"位置相同"指的是**归一化布局**，不是像素矩形。
- Coder-A：验证了材质切换场景：同一槽位切换 A(主色X+法线) / B(主色Y无法线)，只要 X、Y 所在图集对同一 UV 的归一化布局一致即可；法线图集可以更小（低分辨率）。
- **结论**：UV 组 = 归一化布局一致；图集像素尺寸按类型独立取最小候选；padding 按各图集自身最大边计算。

## 议题 4：像素密度与"模型真实大小"

- Coder-B：严格的世界尺寸需要逐面片 UV 梯度反投影（昂贵）。采用估算：网格 bounds 平均边长 / 该通道 UV 跨度 = 每 UV 单位的世界长度；岛世界尺寸 = 岛 UV 尺寸 × 该系数；clamp(世界尺寸×密度, 4px, 原尺寸)，再受用户 min/max 密度与原文件真实像素钳制。
- Coder-C：估算误差可接受，因为最终质量由 P2 的二分搜索兜底（密度只决定"初始候选/上限"，不达标会再缩、达标会尽量放大）。
- **结论**：采用估算方案；在 UI/README 中说明密度是"目标像素密度"（每米模型对应像素），供用户按实际体形调整。

## 议题 5：质量档位参数

- Coder-B（依据）：MS-SSIM 0.9 左右在纹理域接近"多数场景不可感知"；CIEDE2000 <1 人眼基本不可感知、2 左右可接受上限；法线 p95 角度 1-3° 内光斑偏移可忽略；alpha RMSE 0.01（线性）对剪影/混合边缘影响小。
- **结论**：五档 + 自定义（映射公式见 CLAUDE.md 第 5 条 / PLAN.md 表格）。默认 Medium(0.90)。自定义档默认全部近无损，参数不被其他档位覆盖。

## 议题 6：lilToon 与标准关键字识别

- Coder-C：lilToon 2.x 已读源码：alpha 用 `_TransparentMode` float（0/1/2/4）+ `UNITY_UI_ALPHACLIP`/`UNITY_UI_CLIP_RECT` 关键字；功能开关是 float（`_UseMain2ndTex` 等）；UV 通道选择是 int 属性（`_Main2ndTex_UVMode`，4=MatCap 特殊用途）。
- Coder-B：`[NoScaleOffset]` 属性（渐变/抖动/调节蒙版）= 特殊用途，白名单。`_MainTexHSVG` 只改采样后的像素值、不改变 UV 映射 → 不构成障碍。
- **结论**：内置 lilToon 分析器（属性表见 CLAUDE.md 第 9 条）+ 标准关键字通用分析器（保守：通道/角色不确定 → 白名单+warning）+ 第三方 `IATOShaderAnalyzer` 扩展点。

## 议题 7：AAO 互操作

- Coder-C：已读 AAO 源码：`UVUsageCompabilityAPI`（原文拼写）只接受 SkinnedMeshRenderer；`RegisterTexCoordEvacuation` 会在失败时抛异常。
- **结论**：反射访问（AAO 可选依赖）；SMR 可撤离；MeshRenderer 冲突 → 白名单+warning；AAO 不存在 = no-op。

## 议题 8：内存与性能预算

- Coder-B：典型 Avatar 20-50 张贴图（≤8192²）。全解码缓存上限约 50-100MB；岛位图 4px 粒度很小。策略：按贴图分批处理（分析→质量→装箱→合成流水线式），批内用完即释放；RenderTexture 复用池；Burst Jobs 并行岛级指标。
- **结论**：见 CLAUDE.md 第 16 条；禁止一次性全量物化所有大图。

## 议题 9：i18n 与日志

- Coder-A：NDMF 的 InlineError 是 internal，第三方只能用 ErrorReport.ReportException；NDMF 语言在 `LanguagePrefs.Language`。
- **结论**：i18n 用包内 `i18n/*.json`（扁平键值），Auto 跟随 NDMF 语言，回退英文→键名；控制台日志恒英文（机器可读，`[ATO]` 前缀+耗时+类别掩码+verbose 开关）。

## 落选方案 / Rejected alternatives

1. 多 Pass 管线（可被其他插件插队，但原子性差）——落选。
2. 直接引用 AAO 程序集——落选（可选依赖，用反射）。
3. 逐面片 UV 梯度精确世界尺寸——落选（太贵，估算+二分兜底）。
4. 复用 NDMF Localizer + LocalizationAsset(.po)——落选（用户要求 JSON 可扩展）。
5. 取消时中止整个 NDMF 构建（后续插件）——落选（无法安全实现；改为"Avatar 未改+ErrorReport 失败"，效果等价且安全）。
