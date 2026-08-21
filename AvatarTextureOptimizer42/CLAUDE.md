# AvatarTextureOptimizer 项目记忆 / Project Memory

## 当前总体目标 / Current Overall Goal
- 项目名称：AvatarTextureOptimizer
- 包名：`net.fosa.avatar-texture-optimizer`
- 目标：面向 VRChat Avatar 的开源 NDMF 贴图优化工具。
- 当前阶段策略：**优先完成可被验证的安全流水线、源码取证、分析建模、日志/i18n/配置/扩展骨架**，对尚未被充分证明正确的变换一律安全回退，不冒险直接改贴图/网格引用。
- 用户当前要求：**继续尽最大努力推进硬核部分，不停止推进，不主动停下来问。**

## 已完成取证 / Verified Facts From Source Inspection
1. **NDMF 顺序控制可行**
   - 可通过 `Sequence.AfterPlugin/BeforePlugin` 在 NDMF 中把 ATO 放到 `Modular Avatar` 之后、`Avatar Optimizer` 之前。
   - NDMF 对缺失插件约束会自动当作可选依赖跳过，不会因为目标插件未安装而直接炸。
2. **AAO UVUsageCompabilityAPI 有硬限制**
   - 已确认 `UVUsageCompabilityAPI` 直接面向 `SkinnedMeshRenderer`，不是通用 `Renderer` API。
   - 因此 MeshRenderer 侧不能把正确性完全建立在该 API 上，必须自洽处理。
3. **AAO 已内建较复杂的 lilToon ShaderInformation**
   - 已阅读 AAO 中 lilToon 相关 ShaderInformation 代码，确认其对属性/UV/采样器判断非常复杂，说明未来完整兼容不能靠猜。
4. **NDMF 本地化语言源**
   - 已阅读 `LanguagePrefs` / `Localizer` 实现，当前 ATO 已采用 JSON 本地化文件并可跟随 NDMF 当前语言。
5. **AAO API 软依赖策略已确认**
   - 目前通过反射访问 `com.anatawa12.avatar-optimizer.api.editor`，不会把 ATO 编译正确性强绑定到 AAO 安装状态上。

## 当前代码结构 / Current Code Structure
- `Runtime/AvatarTextureOptimizer.cs`
  - 用户组件
  - 基础配置模型
  - 质量挡位与高级参数
  - 平台 override 骨架
- `Editor/Plugin/AvatarTextureOptimizerPlugin.cs`
  - NDMF 插件注册
  - 运行顺序：MA 后、AAO 前
  - Validate / Collect / Analyze / Plan / Execute / Report / Cleanup 阶段骨架
- `Editor/Core/AtoLocalization.cs`
  - JSON i18n 加载
  - 支持 Auto 跟随 NDMF
- `Editor/Core/AtoUtilities.cs`
  - 日志、进度条、取消、反射、错误上报、路径辅助
- `Editor/Core/AtoState.cs`
  - 会话状态、扫描结果、UV 组、UV 岛、计划模型、图集计划模型、报告模型
  - 材质动画重写映射表
- `Editor/Core/AtoScanner.cs`
  - 扫描 Renderer / Material / Texture / AnimationClip
  - 初步白名单命中、ST 动画检测、材质引用动画检测、重复贴图指纹检测、语义推断
  - UV 通道 / UV 越界可平移归一分析
  - 关联 UV 组
- `Editor/Core/AtoMeshAlgorithms.cs`
  - 按 submesh + UV 通道提取 UV 岛
  - 计算 UV 面积、物体空间面积、UV bounds
  - 初步把 BlendShape 逐个 100% 的最大面积纳入估算
- `Editor/Core/AtoMeshRebuilder.cs`
  - 为多材质槽 / 多 submesh 图集路径重建独立顶点副本
  - 支持按 submesh 独立重映射 UV，减少同一 UV channel 冲突
- `Editor/Core/AtoPlanner.cs`
  - 从当前安全子集生成保守计划
  - 输出 UV 组与贴图类型组摘要
  - 结合密度估算与 CPU 近似质量估算输出目标尺寸
- `Editor/Core/AtoAtlasPlanning.cs`
  - 估算 UV 组源像素尺寸与目标像素尺寸
  - 候选图集尺寸池
  - 干运行级别的 atlas packing 规划
  - 共享 atlas layout 规划
- `Editor/Core/AtoQualityEvaluator.cs`
  - CPU 侧最小可用质量评估
  - 基于光栅化 patch + 双线性回放的 RMSE / SSIM / MS-SSIM / ΔE2000 / alpha / 法线角误差近似估算
  - 对近似纯色岛做最小尺寸短路
  - 进行各向异性双轴独立二分细化近似
- `Editor/Core/AtoExecutor.cs`
  - 当前已证明安全子集的真实执行链
  - 支持：
    - 非图集模式下整图缩放 + clone 材质
    - 图集模式下共享 layout atlas 生成 + clone 材质 + clone 网格 + UV 重映射
    - SkinnedMeshRenderer 若被 AAO 使用对应 UV channel，会尝试做 UV evacuation 兼容
    - 动画材质引用基础重写
    - 基础 mesh / 贴图 / 材质去重后处理
- `Editor/Core/AtoAnimationRewriter.cs`
  - 将动画中的材质引用在可安全判定时改写到生成材质
  - 当前主要覆盖 object reference curve 中的材质槽切换
- `Editor/Core/AtoTextureRasterizer.cs`
  - 基于 UV 岛三角形的裁剪贴图光栅化
  - 相比单纯包围盒采样更贴近“剔除未使用 UV 区域”
- `Editor/Core/AtoTexturePostprocess.cs`
  - 透明边界扩张填充
  - 法线贴图重归一化
  - 轻量后处理
- `Editor/Core/AtoTextureCompression.cs`
  - 基于平台与语义的保守压缩格式映射
  - 对生成贴图/图集应用 Editor 侧压缩
- `Editor/Core/AtoExtensionPoints.cs`
  - 为高级用户和第三方保留扩展接口
- `Editor/Core/AtoDefaultSemanticProviders.cs`
  - 默认通用 Shader 语义提供器
  - 基于已取证逻辑的保守 lilToon 语义提供器
- `Editor/Core/AtoReporting.cs`
  - NDMF/Unity 控制台汇总输出
- `Editor/Inspector/AvatarTextureOptimizerEditor.cs`
  - 新手友好的 Inspector
- `Editor/Localization/en-US.json`
- `Editor/Localization/zh-Hans.json`

## 当前实际能力 / Current Actual Capability
- 可作为 **可导入 Unity 的 NDMF 构建期工具骨架** 使用。
- 会做：
  - 组件合法性检查
  - Avatar 根对象校验
  - 分析 Renderer / Material / Texture / AnimationClip
  - 检测白名单命中
  - 检测非 Texture2D、非 identity ST、动画 ST、动画材质引用切换等安全回退条件
  - 检测 UV 是否已在 `[0,1]`，或是否可整体平移归一回 `[0,1]`
  - 按 submesh + UV 通道提取初步 UV 岛
  - 统计 UV 岛数量、面积、包围盒
  - 初步考虑动画缩放对面积与目标像素估算的影响
  - 初步考虑 BlendShape 0/100 单独极值对面积的影响
  - 建立初步 UV 组与贴图类型组计划
  - 基于密度 + CPU 近似质量评估估算目标尺寸
  - 多材质槽图集路径下，支持按 submesh 独立顶点重建 mesh 以避免 UV 冲突
  - 基于估算尺寸做**干运行图集规划**
  - **对简单安全子集执行真实优化：**
    - identity ST / 可归一 UV / 无白名单冲突 / 无动画材质引用切换
    - 同一 Renderer 上对应 UV channel 不能被多个材质槽共享，否则回退
    - 非图集：按整图缩放生成贴图、clone 材质，不改 UV / mesh
    - 图集：共享 layout 生成 atlas、clone 材质、clone mesh、重映射 UV
    - 透明边界扩张填充、法线贴图重归一化
    - SkinnedMeshRenderer 若被 AAO 使用对应 UV channel，会尝试做 UV evacuation 兼容
    - 基础动画材质引用重写
    - 基础 mesh / 贴图 / 材质去重后处理
  - 检测潜在重复贴图组（仅报告，不改引用）
  - 构建完成时从成品 clone 上移除自身组件
  - 输出阶段耗时与总体报告
- 不会做（当前仍未实现，保持 no-op / fallback）：
  - 完整的学术级 SSIM / MS-SSIM / ΔE2000 / alpha / normal 正式质量链（目前是 CPU 近似版）
  - Burst / GPU 批量正式质量求解
  - 完整产品级岛形装箱最终执行（当前 atlas 执行仍以共享 layout + patch 思路为主）
  - 多材质槽 + 动画材质切换的完整全链路修复
  - 最终 README 与对外发布级文档

## 当前计划 / Current Plan
1. 继续补**硬核执行链**，从“简单安全子集真实执行”推进到“更广子集真实执行”。
2. 下一阶段优先补：
   - 更可靠的多材质槽 / 多 submesh 支持
   - 更完整的动画材质切换修复
   - 更强的 atlas packing 执行
   - 更正式的平台格式与导入参数落地
3. 再继续做：
   - 真正的材质/贴图去重收尾
   - 更深的 AAO / lilToon 兼容
   - README 与发布整理

## 关键注意事项 / Critical Notes
- 一切未被充分验证正确的路径必须 fallback，不能为了“看起来在工作”而乱改材质或贴图。
- 任何下一步修改前都要先阅读现有代码和相关依赖源码，先取证再下结论。
- 需要继续坚持：日志统一以 `[ATO]` 开头。
- 代码注释尽量保持中英双语。
- 当前没有完整 Unity 工程，任何“最终可发布”结论都必须谨慎，不能虚构验证结果。
- `AtoLilToonSemanticProvider` 当前是**保守子集**，只是把已取证的常见属性名与 UVMode 读法先接上，不代表完成 lilToon 全兼容。
- 当前 atlas 执行已经不是纯 no-op，但依然属于**保守安全子集**，不是完整产品级最终执行器。

## 已完成 git 提交 / Completed Git Commits
- `0edc1ad` `feat: bootstrap ATO pipeline, analysis scaffold, and i18n`
- `a889a46` `feat: add UV group analysis, planning, and semantic providers`
- `5487e6c` `feat: add UV island extraction and dry-run atlas planning`
- `b0e8232` `feat: execute safe subset texture cropping, atlasing, and UV remap`
- `ca92075` `feat: rasterize UV islands and add basic dedup execution`
- `56fe375` `feat: account for animated scale in planning and fallback analysis`
- `bd5ccb2` `feat: add quality-guided sizing, AAO UV evacuation, and whole-texture direct scaling`
- `833e3e1` `feat: add platform compression, conservative direct scaling, and improved warnings`
- `9d789bb` `feat: add animation material rewrites and approximate structural color metrics`
- `bb0348f` `feat: add conservative animation material rewrites and blended quality metrics`
- `e217d1d` `feat: rebuild multi-submesh atlas meshes and track generated material rewrites`

## 进度判断 / Progress Assessment
- 里程碑 1（Pipeline / Safety / Analysis Scaffold）：**已完成主要骨架**
- 里程碑 2（UV 岛 / 计划模型 / 干运行图集规划）：**已推进并落地第一版**
- 里程碑 3（简单安全子集真实执行）：**已开始落地并继续扩展**
- 里程碑 4（复杂场景真实优化执行）：**尚未完成**
- 整体项目完成度：**约 55% 左右（保守估计）**
