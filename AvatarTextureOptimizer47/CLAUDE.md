# AvatarTextureOptimizer 项目记忆（唯一记忆文件）

## 约束
- 项目：AvatarTextureOptimizer；包名：`net.fosa.avatar-texture-optimizer`；Unity 2022.3。
- 只在本文件记录项目计划、进度、决策、审查和待办；产品文档仅描述公开使用方法，不记录过程记忆。
- 每批修改先取证、三 Coder 达成共识，修改后由三 Reviewer 联审；最终三 QA 各自全量检查。
- 当前环境无 Unity Editor，不能声称完成真实 Avatar 烘焙；以源码 API 核对、静态检查及纯算法测试代替，并把真实 Unity 验证步骤写入 README。
- 安全优先：无法证明安全即白名单/fallback，绝不修改非贴图材质参数。

## 依赖取证（2026-08-23）
- 已下载并检索用户指定版本：VRCSDK Base/Avatars 3.10.4、NDMF 1.14.4、MA 1.18.2、AAO 1.9.17、lilToon 2.3.4、avatar-compressor 0.9.0、LLC 2.13.0。
- NDMF：插件使用 `[assembly: ExportsPlugin]`；优化阶段以 `.AfterPlugin("nadena.dev.modular-avatar").BeforePlugin("com.anatawa12.avatar-optimizer")` 排序；动画使用 `AnimatorServicesContext.AnimationIndex`；替换资产使用 `ObjectRegistry.RegisterReplacedObject`。
- AAO：公开 API 精确名称为 `Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI`；`IsTexCoordUsed(SkinnedMeshRenderer,int)` 与 `RegisterTexCoordEvacuation(SkinnedMeshRenderer,int,int)`；无 AAO 时须无硬依赖。
- lilToon：属性表可由 `Shader.GetPropertyCount/GetPropertyType/GetPropertyAttributes` 动态读取；`_MainTex_ST`、`_MainTex_ScrollRotate` 等非恒等变换必须拒绝；`_BumpMap` 等类型需按属性/导入器共同判定。
- 临时生成 Texture2D 可通过其 SerializedObject 的 `m_StreamingMipmaps` 启用流式 Mip，压缩使用 `EditorUtility.CompressTexture`；NDMF 后续会校验。

## 架构共识（Coder-1/2/3，第 1 轮）
1. Runtime 只保存组件与可序列化设置；Editor 中实现 NDMF、分析、质量、装箱、烘焙与 UI。
2. 动画处理以 NDMF 虚拟动画为唯一构建期真源；扫描所有材质 PPtr、材质属性/ST/渲染模式、启用状态和缩放曲线。
3. 图集以 UV 组为装箱原子；一个类型组内所有贴图类型共享候选尺寸、旋转和落点，避免不同类型错位。
4. 无法解析 shader UV 语义、动画或 wrap 时整条关联分量 fallback；不猜 API/语义。
5. 图集写入采用 GPU RenderTexture；质量 CPU 部分用 Burst job；装箱使用 4px 光栅位掩码与全扫描 BLF。

## 当前进度
- [x] 初始化 UPM/VPM 包骨架、程序集、测试程序集、许可证。
- [ ] Runtime 配置与 Inspector/i18n。
- [ ] NDMF 插件和构建管线。
- [ ] 分析/白名单/动画/去重。
- [ ] UV 岛、质量算法、GPU/Burst。
- [ ] 形状装箱、图集、UV/材质/动画更新。
- [ ] 压缩与平台 override、报告、取消/清理。
- [ ] 测试、三 Reviewer、三 QA、README、打包。

## Reviewer 门禁
- 尚未进入代码功能审查；当前仅包骨架。

## Coder 共识（第 2 轮：配置/UI）
- Coder-1：用 0..1 归一化保真参数表达“自定义默认全为 1”；内部派生 ΔE、RMSE、角度和 IoU 阈值，避免“误差阈值也为 1”与近无损矛盾。
- Coder-2：Mipmap 与 MipStreaming 只暴露一个绑定开关；格式使用安全枚举，并由 Inspector 按平台/Alpha/语义过滤。
- Coder-3：i18n 扫描任意 `ATO_i18n` JSON TextAsset；Auto 读取 NDMF 1.14.4 的 `LanguagePrefs.Language`，缺失时回退英文。
- 共识已落实：Runtime 配置、组件、英中 JSON、Inspector。

## Reviewer 联审（第 2 轮）
- Reviewer-1（需求/安全）：发现首次切到 Custom 会继承上个预设而非全 1；已修正为进入 Custom 时仅初始化一次，后续不覆盖。
- Reviewer-2（Unity/API）：确认组件实现 `INDMFEditorOnly`，Runtime 仅依赖 NDMF Runtime；Inspector 对 VRCAvatarDescriptor 的引用位于 Editor 程序集。
- Reviewer-3（平台/UI）：确认透明类别不显示 BC1/ETC2RGB，NPOT iOS 不显示 PVRTC，Mipmap 与 Streaming 无独立冲突开关。
- 联合结论：本轮通过，可进入构建核心；最终仍需 Unity 编译验证。

## Coder 共识（第 3 轮：构建核心）
- Coder-1（数据/安全）：采用“无法证明安全即 Protected/fallback”；动画来自 NDMF 虚拟控制器；白名单做任意 Unity Object 引用闭包；解码像素+完整 importer JSON 去重。
- Coder-2（质量/GPU）：GPU 在线性 RT 中裁剪与上下采样；透明色预乘 Alpha；法线解码/重采样/归一化/编码；Burst 并行计算多尺度结构、CIEDE2000、Alpha、法线角度/p95、通道 RMSE；质量统一二分后双轴细化。
- Coder-3（图集/兼容）：4px Burst 光栅位掩码、全扫描 BLF、90°转置、POT/NPOT 候选池和原子队列；同类型层共享 layout；GPU 跳洪 pull-push；AAO API 用经源码核实的反射调用，缺失 AAO 无硬依赖。

## Reviewer 联审（第 3 轮）
- Reviewer-1（正确性）：发现 NDMF `IError` 还要求 `AddReference`，已补齐；发现同材质/贴图中任一 fallback 必须向连通 UV 组传播，已增加 `PropagateFallbacks`。
- Reviewer-2（性能/内存）：确认贴图哈希按 128 行条带读取，质量按 128 行条带读取，scratch 由 `ResourceScope` 清理；形态键只逐个保留 0/100 最大面积，不做组合。指出 NPOT 全候选与 BLF 在极端岛数下仍昂贵，保留为最终性能审查项。
- Reviewer-3（兼容/安全）：确认生成图集只改纹理引用和 UV，不改非贴图材质参数；法线旋转不重算切线；AAO 无空闲通道时整 Renderer fallback；动画材质与贴图曲线均重写。发现共享 fallback 纹理不能与同一材质的 atlas 混用，已通过连通传播修正。
- 联合结论：核心设计审查通过进入测试阶段，但尚未有 Unity 编译/烘焙证据，不得标为最终验收通过。

## 当前进度更新
- [x] Runtime 配置与 Inspector/i18n。
- [x] NDMF 插件和构建管线。
- [x] 分析/白名单/动画/去重。
- [x] UV 岛、质量算法、GPU/Burst。
- [x] 形状装箱、图集、UV/材质/动画更新。
- [x] 压缩与平台 override、报告、取消/清理。
- [ ] Unity API 编译核对、算法测试、三 Reviewer 最终审查、三 QA、README、打包。

## Coder 共识（第 4 轮：最终修正）
- Coder-1：原“每源贴图一张全尺寸图集”会制造严重浪费；改为按规范化属性角色构建组合图集。互不冲突的静态材质共享同一组合，仅材质/贴图动画冲突时生成必要变体。
- Coder-2：全局结构统计不等同标准 SSIM；改为 GPU 11×11 高斯局部 SSIM，并按早期尺度 contrast-structure、末尺度 SSIM 组合五尺度 MS-SSIM。质量 RT 使用 halo 条带，避免条带边界失真。
- Coder-3：显式增加线性到 sRGB 最终编码、正常图 A=1 安全布局、Crunch 枚举、平台过滤、非 Renderer 组件/MaterialPropertyBlock/HDR/多语义保护和逐图集 scratch scope。
- 共识：目标质量 1 为严格满足“不重采样原样拷贝”，禁用图集并采用同尺寸整图复制；其他挡位才执行岛缩放与图集化。

## Reviewer 联审（第 4 轮）
- Reviewer-1（图集正确性）：打回“每张源贴图各建空白大图集”；修为多源组合图集，并核对材质切换、贴图动画、多个布局与同 UV 落点。
- Reviewer-2（质量/色彩）：打回全局 SSIM 与隐式 ReadPixels 色彩假设；修为局部 MS-SSIM及 `ATO_Finalize` 显式 sRGB 编码。透明为预乘下采样、直通输出；法线输出 RGB+Alpha=1。
- Reviewer-3（资源/引用）：打回图集 scratch RT 累积到构建末尾；修为每图层独立 ResourceScope。补全非 Renderer 组件材质/贴图保护、序列化引用重写和共享 fallback 传播。
- 联合结论：源码审查通过；进入三 QA 全量静态验收。

## 三 QA 独立验收（第 1 次）
- QA-1（需求矩阵）：独立读取 50 个 Runtime/Editor/Shader/Resource/i18n/Test 文件；检查 NDMF 顺序、AAO API、质量指标、NPOT、命名、MipStreaming、取消、自移除、扩展接口与 manifest，全部静态项通过。
- QA-2（安全/API）：独立合并阅读全部 5115 行 C#；确认工作材质没有非贴图 Set 调用、没有修改/重导入源 importer；列举并逐处核对所有 RT 分配释放；tree-sitter C# AST 0 语法错误。首次 QA 脚本因 grep 参数冲突失败，修正 QA 脚本后从头重跑通过（产品代码未因脚本误报而豁免）。
- QA-3（测试/性能/资源）：独立读取全部 5397 行 C#/Shader/Compute；确认四组 EditMode 测试、Burst/TempJob、RT/CommandBuffer 清理、JumpFlood、LocalSsim、无 TODO/预览接入、英中成对注释。首次脚本把成对英中两行误判为“单行双语”，修正判定并从头重跑通过。
- 三 QA 共同结论：静态源码、需求映射和资源所有权验收通过。
- 环境限制仍然成立：当前容器没有 Unity 2022.3，无法执行 Unity 编译、Shader 编译、EditMode Test Runner 或真实 Avatar Bake；最终交付不得声称已完成这些运行时验证。`Readme.md` 已给出完整 Unity 验证矩阵。

## 最终进度
- [x] 包骨架、配置、平台 override、Inspector、英中 i18n。
- [x] NDMF 插件、动画分析、安全白名单、去重。
- [x] UV 岛/重叠/越界、多 UV、形态键/缩放面积。
- [x] GPU/Burst 质量、均匀及各向异性二分。
- [x] 类型组、组合图集、4px 形状 BLF、POT/NPOT、pull-push。
- [x] Mesh/材质/动画引用、AAO evacuation、压缩/MipStreaming、去重/槽合并。
- [x] 报告、日志、进度/取消、扩展 API、文档、静态测试源码。
- [x] 三 Reviewer 与三 QA 静态门禁。
- [ ] 外部 Unity 2022.3 工程中的编译、测试与 Avatar 实机烘焙（交付环境不可执行，必须由接收方按 README 验证）。
