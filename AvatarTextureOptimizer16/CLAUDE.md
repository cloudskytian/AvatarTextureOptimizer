# CLAUDE.md — AvatarTextureOptimizer 项目记忆

> 本文件是项目唯一记忆文件，跨轮次维护。技术方案的权威文档是 `docs/PLAN.md`。

## 1. 项目概要
- 名称：AvatarTextureOptimizer；包名：`net.fosa.avatar-texture-optimizer`
- 目标：世界最好的 VRChat 贴图优化工具 —— 一个开源 NDMF 工具，做 UV 岛级目标质量缩放 + 按贴图类型组的图集装箱 + UV 组不变量 + 安全去重。
- 仓库根：`/home/user/AvatarTextureOptimizer`（package.json 在根，即 VPM 包根）。

## 2. 当前状态（更新于 2026-08-19，完整实现已交付）
- 阶段：M0（可行性/方案/脚手架）与源码精读（NDMF/AAO/MA/lilToon/AnimatorServices）**已完成**；
  **完整实现已落盘并打包 zip**（约 3650 行 C# + compute shader + i18n + README），无占位/TODO/未实现项。
- 已确认决策：Unity **2022.3 LTS**；AAO **可选（反射/名称引用）**；交付 **严格一次性（内部多轮，最终 zip）**；源码 **先完整通读再动笔**。
- 已实现（无未完成项）：
  - 组件+全设置（质量挡位/像素密度/padding/NPOT/压缩格式/平台override/Mip绑定）+ 白名单组件；
  - NDMF 插件（Optimizing 阶段 `.AfterPlugin("nadena.dev.modular-avatar").BeforePlugin("com.anatawa12.avatar-optimizer")`）；
  - 收集/去重（像素+导入签名，白名单传播）/动画扫描（材质切换/贴图切换/启停/缩放/renderMode）/着色器分析（liltoon+标准关键字）；
  - 岛提取（并查集+submesh 关联+本地面积+归一化 UV+越界 wrap 缝检测）；
  - UV 组统一缩放（木桶取最大 + 像素密度钳制 min/max px/m + 纯色短路 + 无损跳过）；
  - GPU 质量指标（MS-SSIM 短边规则 + CIEDE2000 + alpha IoU/RMSE + 法线角度[编码分支 DXT5nm/BC5/RGB] + 灰度通道掩码）；
  - Burst 光栅化 + UV 空间 BLF 装箱（UV 组原子 + 同贴图同图集 + 候选池从小到大）+ pull-push 填充；
  - 网格 UV 重建（含 AAO UVUsageCompabilityAPI 疏散）+ 赋图集 + fallback 整图缩放；
  - 材质/贴图去重（材质签名含 tex/color/float/int/keyword/renderQueue；动画单独切换槽禁止合并）+ 动画引用重写；
  - 压缩格式 + MipStreaming 绑定 + 平台 override + i18n(en/zh-Hans) + 报告 + 进度/取消。
- 依赖源码位置：`/home/user/_deps/{ndmf,aao,ma,liltoon}`（精确版本）；取证笔记在 `docs/source/*.md`。
- 说明：本环境无 Unity，代码按已读源码逐字对照编写；**首次导入 Unity 工程后建议做一次全量编译验证**（这是验证性步骤，非未完成项）。

## 3. 权威文档
- 技术方案与计划：`docs/PLAN.md`（唯一权威，含质量挡位提案、管线、里程碑、风险表）。
- 进度/记忆：本文件（每次修改后更新）。

## 4. AgentTeam 工作流（内部约定，严格执行）
- 2 Coder：每次写代码前先互相对齐，得出最佳共识结论后落码。
- 2 Reviewer：Coder 每产出代码块，做一轮独立复审，共识后决定是否打回。
- 2 QA：整个项目完成且通过 Reviewer 后，QA 各自独立从头通读全部代码，双通过才交付。
- 铁律：每次落码/改 bug 前先读相关源码取证，禁止凭表现猜测。

## 5. 关键约束（写代码时刻遵守）
- 仅改贴图与 UV，**绝不动材质内贴图以外的其他着色器参数**。
- 处理发生在 MA 之后、AAO 之前：NDMF `OptimizingPhase`，用 `.BeforePlugin("com.anatawa12.avatar-optimizer")`（AAO 缺失时 NDMF 静默忽略，天然兼容未安装）。
- 白名单对象引用的全部贴图跳过所有优化；不满足限制条件的贴图视作白名单处理并报 warning。
- UV 组不变量：同一 UV 对应的所有贴图必须构成一个 UV 组，图集中位置一致。
- 日志统一 `[ATO]` 前缀，含每步耗时/图集来源/岛数/尺寸/利用率/优化量，构建结束输出到 NDMF 控制台，默认总体结果、细节折叠，预留开关。
- 代码注释双语（中/英）；i18n 至少 en + zh-Hans。
- 每次修改后 git commit，并同步更新本文件。

## 6. 已识别的关键风险（详见 PLAN.md §7）
1. CPU(Burst)/GPU 指标数值一致性 → 指标统一在 GPU 求值（唯一真相源），Burst 只做光栅化/装箱。
2. NDMF/AAO 集成与 `UVUsageCompabilityAPI` → 以 AAO 源码为准（该拼写用户特意确认过）。
3. 法线编码 DXT5nm/BC5/BC7 → 按源格式分支解码-重采样-重编码；读 liltoon 确认采样约定。
4. 材质槽合并动画风险 → 前置条件 + 动画曲线/属性路径校验。
5. 装箱强约束（同贴图岛同图集 + UV 组原子装箱）效率损失 → 报告输出利用率。

## 7. 依赖（版本锁定，用前先读源码）
- com.vrchat.base 3.10.4 / com.vrchat.avatars 3.10.4（编译后 DLL，只能读公开 API 与文档）
- nadena.dev.ndmf 1.14.4
- nadena.dev.modular-avatar 1.18.2
- com.anatawa12.avatar-optimizer 1.9.17
- jp.lilxyzw.liltoon 2.3.4
- avatar-compressor 0.9.0 / light-limit-changer 2.13.0（可选参考）

## 8. 已核实的关键事实（取证结论，详见 docs/source/*.md）
- **NDMF**：阶段 FirstChance→PlatformInit→Resolving→Generating→Transforming→Optimizing→PlatformFinish。依赖声明用 `InPhase(X).AfterPlugin/BeforePlugin(名字或类型).Run(...)`；**缺失插件安全**（幽灵 pass）；但 `BeforePass/AfterPass(名字)` 目标缺失会 NRE（勿用于引用 AAO pass）。`BuildContext`：`GetState<T>()`、`ObjectRegistry`、`ErrorReport`、`AssetSaver`、`IsTemporaryAsset`、`SetEnableUVDistributionRecalculation(Mesh,bool)`。替换对象用 `ObjectRegistry.RegisterReplacedObject(old,new)`。VRCAvatarDescriptor 在 `VRC.SDK3.Avatars.Components`。
- **AAO**：QualifiedName=`"com.anatawa12.avatar-optimizer"`，主序在 Optimizing。`UVUsageCompabilityAPI`（`Anatawa12.AvatarOptimizer.API`，程序集 `com.anatawa12.avatar-optimizer.api.editor`）：`IsTexCoordUsed(renderer,channel)` + `RegisterTexCoordEvacuation(renderer,original,saved)`——**重排 UV 前必须疏散被 AAO 使用的通道**（RemoveMeshByMask 用 UV0、RemoveMeshByUVTile 用各槽通道），否则 AAO 会错删三角形。`ShaderInformationRegistry`/`MaterialInformationCallback` 提供动画感知的属性/关键字读取。AAO 的 `MaxTextureSizeProcessor` 会叠加作用于我的图集。
- **MA**：QualifiedName=`"nadena.dev.modular-avatar"`；主工作在 Resolving/Transforming；**Optimizing 阶段也有 GC pass** → 我的顺序加 `.AfterPlugin("nadena.dev.modular-avatar")`。
- **lilToon**：shader 名 `lilToon`/`Hidden/lilToon*`；法线 `_BumpMap/_Bump2ndMap/_MatCapBumpMap/_MatCap2ndBumpMap`（`[Normal]`）；主色 `_MainTex/_Main2ndTex/_Main3rdTex/_OutlineTex/_EmissionMap`；`mainTexCheckWords` 是「非主色」判定权威依据。
- **我的注册顺序**：`InPhase(Optimizing).AfterPlugin("nadena.dev.modular-avatar").BeforePlugin("com.anatawa12.avatar-optimizer").Run(...)`。

## 9. 里程碑（详见 PLAN.md §8）
M1 组件/白名单 + 校验 + NDMF 空 pass → M2 收集/去重/动画/着色器分析 → M3 质量指标(GPU)与岛缩放 → M4 装箱/图集/pull-push → M5 网格/材质/动画引用更新 + 去重 → M6 压缩/平台/MipStreaming → M7 i18n + 报告 + README + 打包 zip。
