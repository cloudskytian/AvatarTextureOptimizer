# CLAUDE.md — AvatarTextureOptimizer 项目记忆

> 本文件是本项目唯一的记忆载体。上下文丢失 / 网络中断后，先读此文件恢复状态。

## 项目身份
- 项目名：`AvatarTextureOptimizer`（缩写 ATO）
- 包名：`net.fosa.avatar-texture-optimizer`
- 目标：全世界最好的 VRChat 贴图优化工具 —— 一个开源 NDMF 工具，在保证视觉质量的前提下，通过 UV 岛缩放 + 图集合并，最大化贴图利用率、降低贴图体积与内存占用。
- 性质：NDMF 构建期（Build-time）工具，只改 **网格 UV + 贴图/图集 + 贴图引用**，绝不修改材质除贴图以外的任何参数。

## 硬性约束（必须始终遵守）
1. 不联网搜索外部资料；依赖库源码已在 `/home/user/libs/` 本地解包。
2. 每次写代码前先读相关源码，先取证再下结论，禁止猜 API。
3. 每次修改后 git 提交，并把进度同步回本文件。
4. 全程简体中文交流。
5. 日志一律 `[ATO]` 前缀，含每步耗时、图集来源/岛数/大小/利用率/优化量。

## 依赖库（已下载解包到 /home/user/libs/，实测体量）
| 库 | 版本 | .cs 文件 | .cs 总行数 | 备注 |
|---|---|---|---|---|
| com.vrchat.base | 3.10.4 | 287 | 99,088 | 核心运行时是 **24 个 DLL（无源码）**，只有编辑器脚本有源码 |
| com.vrchat.avatars | 3.10.4 | 41 | 12,092 | |
| nadena.dev.ndmf | 1.14.4 | 205 | 26,536 | |
| nadena.dev.modular-avatar | 1.18.2 | 275 | 40,691 | |
| com.anatawa12.avatar-optimizer (AAO) | 1.9.17 | 246 | 45,740 | |
| jp.lilxyzw.liltoon | 2.3.4 | 48 | 18,347 | 另有 **65 个 .shader + 35 个 .hlsl + 1 个 .cginc**（属性/关键字分析的真正对象） |
| avatar-compressor | 0.9.0 | 92 | 13,848 | |
| light-limit-changer | 2.13.0 | 156 | 11,909 | |
| **合计** | | **约 1350** | **约 26.8 万行** | 另需读 shader/HLSL |

## 已验证的源码事实（先取证，勿再猜）
- **AAO `UVUsageCompabilityAPI`**（`aao/API-Editor/UVUsageCompabilityAPI.cs`，已读）：
  - 拼写确实是 `Compability`（用户提醒正确，AAO 原文如此）。
  - 两个方法：`IsTexCoordUsed(SkinnedMeshRenderer, int channel 0~7)` 与 `RegisterTexCoordEvacuation(SkinnedMeshRenderer, originalChannel, savedChannel)`。
  - 语义：AAO 会用某些 UV 通道做优化；为了兼容，工具应在别的通道保存原 UV，然后调用 `RegisterTexCoordEvacuation` 告知 AAO"原始 UV 已疏散到 savedChannel"。AAO 处理完会移除被疏散的通道。
  - **重要限制**：该 API 只接受 `SkinnedMeshRenderer`（不含 `MeshRenderer`），且仅限构建期、不能用于 in-place 编辑。ATO 自己的处理范围比 AAO 的疏散通道更广（还含 MeshRenderer），这是需要在实现时单独处理的点。
- 网络可用，8 个依赖 zip 均可下载解包。
- 本沙箱 **无法运行 Unity**：不能编译、不能烘焙、不能跑真机 QA。→ 见"待确认事项"。

## AgentTeam 工作流（流程纪律，非 9 个独立模型）
1. **Coder（写）**：写代码前，先在本文件/会话内以"多视角共识"梳理设计，得出结论后再落码。
2. **Reviewer（审）**：每批代码写完后，做一次独立的静态审查（编译正确性、API 是否猜用、安全 fallback、NDMF 生命周期、是否越权改材质），结论达成后才放行或打回。
3. **QA（验）**：项目整体完成后，对全部代码做独立静态 QA（找隐患/Bug、核对需求覆盖），三者一致通过才交付。
4. 每个角色产出结论都记录在 git 提交信息或本文件，形成可追溯链条。

## 里程碑计划（建议，待用户确认交付方式后执行）
- **M0 骨架**：NDMF 插件接入（`IPlugin`/Pass，运行在 MA 之后 AAO 之前）、Avatar 组件（VRCAvatarDescriptor 校验 + 单实例约束）、白名单、数据模型（UV 组 / 贴图类型组 / 去重）、i18n 骨架（中英 json）、`[ATO]` 日志与取消/进度框架。**此里程碑即可在 Unity 挂载 + 进烘焙流程 + 打印报告。**
- **M1 分析**：贴图收集与去重、lilToon + 标准关键字着色器属性表/关键字分析（读 65 个 shader + 35 个 hlsl）、动画扫描（材质切换/贴图切换/启用禁用/形变缩放/渲染模式与 Cutoff 修改）、形态键面积、UV 越界归一。
- **M2 质量算法**：MS-SSIM / 单尺度 SSIM / ΔE2000 / 法线角度误差+p95 / 灰度通道 RMSE 的 GPU(RenderTexture)+Burst 实现，二分搜索 UV 缩放，各向异性细化，木桶效应取最大尺寸。
- **M3 图集装箱**：Burst 位掩码光栅化（4px）+ BLF 全扫描 + 面积/边长降序 + 90° 步进 + 候选图集池（2^n / NPOT）+ pull-push padding + 岛级装箱（非矩形）。
- **M4 输出与兼容**：网格/材质/贴图重写、AAO UV 疏散兼容（含未装 AAO 的兜底）、压缩格式/平台 override/MipStreaming 绑定、材质槽合并、贴图/材质去重。
- **M5 收尾**：报告折叠、取消清理、README、i18n 补全、性能/内存优化复查。

## 待确认事项（阻塞项，需用户拍板）
1. **交付方式**：用户要求"一次性交付全部功能"；但本工具体量≈数万行、需多会话，且沙箱无法编译/烘焙验证。一次性交付≈不可验证的半成品，与用户自己的 QA 验收要求矛盾。**已向用户说明，等其选择：分里程碑 vs 一次性 vs 先最小骨架。**
2. 用户将在本地 Unity 手动烘焙验证；我方只能做静态审查 + 尽量保证可编译。

## 进度
- [x] 下载并解包 8 个依赖库，量化体量。
- [x] 读取并验证 AAO `UVUsageCompabilityAPI` 源码。
- [x] 初始化 git 仓库 + 本记忆文件。
- [x] 交付方式确认：用户选择「一次性交付全部 M0~M5」。
- [x] 读通 NDMF 插件/Pass/相位/BuildContext/AssetSaver API + AAO 插件 QualifiedName + lilToon 属性名（全部取证，未猜）。
- [x] 一次性实现全部代码（约 3200 行 C#，22 文件，见 `Packages/net.fosa.avatar-texture-optimizer/`）：
  - M0：NDMF 插件（Optimizing 相位，BeforePlugin AAO）、组件+白名单+设置、数据模型、日志/进度/取消、i18n（中英）+ 自定义 Inspector。
  - M1：收集去重白名单、UV 组、动画扫描、岛提取、形态键/缩放面积、UV 越界归一。
  - M2：质量算法（MS-SSIM/SSIM/ΔE2000/角度/RMSE）+ 二分搜索缩放 + 各向异性细化 + 像素密度钳制。
  - M3：图集装箱（三角形形状光栅位掩码 4px + BLF + 90° 旋转 + 候选图集池 + pull-push padding）。
  - M4：图集生成、网格 UV 重写、材质重指向、AAO 反射兼容、压缩/平台/MipStreaming（best-effort）、去重、报告、移除组件。
- [ ] 用户在 Unity 中编译/烘焙验证（我方无法编译/运行 Unity）。

## 进度（第二轮：全部完成 / 补全 TODO）
- [x] **压缩/MipStreaming 真正落地**（ATOCompression 重写）：
  - 取证确认：NDMF 生成贴图是子资产，TextureImporter 无效；正确做法是 `EditorUtility.CompressTexture`（源须未压缩）+ 直接设 wrapMode/filterMode/anisoLevel + `SetStreamingMipMapSettings` 反射透传（照 AAO OptimizeTexture 源码）。
  - 安全枚举 + 平台过滤 + NPOT 剔除（iOS 剔除 PVRTC）+ 透明贴图禁止无 alpha 格式 + 灰度多通道强制 RGBA + warning。
- [x] **材质去重 + 材质槽合并 + 动画索引重映射**（新增 ATODedup.cs）：
  - 取证确认：AAO 用 internal `RecordMoveProperties`/`GetAnimationComponent`，我们不可用 → 自己扫描动画剪辑 `m_Materials.Array.data[i]` object curves 直接重写。
  - 安全前提：动画"单独切换"的材质/槽不合并。
- [x] **pull-push padding 迭代到收敛**（真正无限外扩，透明 alpha 保持 0）。
- [x] **动画扫描增强**：BlendTree clip 已含在 controller.animationClips；新增 ST 变换动画检测（`material.<prop>_ST`）、动画切换贴图并入 UV 组（IntegrateAnimatedTextures）、动画切换材质里的贴图收集（CollectAnimatedMaterialTextures）。
- [x] **Burst 加速**（新增 Editor/Burst/ 程序集 + ATOBurstJobs.cs）：三角形光栅化（只置位、并行无竞争）+ SSIM + 分块均值方差 job；ATOPacker 走 Burst + CPU 回退。
- [x] 公共工具 ATOUtil.cs（ParseSlotIndex/FindAtPath/GetPath 去重）。
- 当前代码约 4200 行 C#，32 文件。

## 仍受沙箱限制（无法消除，已诚实告知）
- 沙箱无法编译/运行 Unity，所有代码未经编译验证，**必须在用户 Unity 中编译并烘焙验证**。
- GPU（RenderTexture/ComputeShader）质量评估仍是 CPU 实现（Burst 已接，GPU 需在 Unity 环境实测后再接，接口已留）。
- 着色器属性分析对非标准着色器（无标准关键字）保守归 Other，需真机测试完善。
- 下次继续：Unity 编译 → 修编译错 → 最小用例烘焙 → 复杂用例（法线/蒙版/动画/多UV）。
