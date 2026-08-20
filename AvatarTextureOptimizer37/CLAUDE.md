# ATO (AvatarTextureOptimizer) — 项目记忆 / Project Memory

> 本文件是本项目的**唯一记忆载体**。每次修改后必须更新本文件并 git 提交。
> This file is the single source of truth for project memory. Update + commit after every change.

## 1. 项目基本信息 / Project basics

- 项目名：AvatarTextureOptimizer（简称 ATO）
- 包名：`net.fosa.avatar-texture-optimizer`
- 目标：全世界最好的 VRChat Avatar 开源 NDMF 贴图优化工具
- 版本：0.1.0（开发中，配置字段可随意改，无版本兼容负担）
- Unity：2022.3+；NDMF：1.14.4（新 Plugin/Pass/BuildPhase 架构，不是旧 NDMFModule）
- 工作区根：`/home/user/AvatarTextureOptimizer`（本目录 = Unity 包根目录）
- 参考库源码（仅供阅读，不提交、不进 zip）：`.libs/`（ndmf 1.14.4 / MA 1.18.2 / AAO 1.9.17 / lilToon 2.3.4 / VRC base+avatars 3.10.4 / avatar-compressor 0.9.0 / LLC 2.13.0）

## 2. AgentTeam 工作流（必须遵守）/ Workflow

- 3 个 Coder：写码前互相交流形成共识（记录在 `docs/CODER_CONSENSUS_*.md`），再落实代码。
- 3 个 Reviewer：任何代码写完后共同审查，形成共识；不通过则打回 Coder 修改（记录在 `docs/REVIEW_LOG.md`）。
- 3 个 QA：Coder 彻底完成整项目且通过 Reviewer 验收后，3 个 QA **各自独立、从头完整**查一遍全部代码（查隐患/Bug/需求符合性），只有 3 个 QA 同时认可才能交付；有缺陷则同时通知 Reviewer 与 Coder 打回（记录在 `docs/QA_LOG.md`）。
- 交付：全部功能完成 → 打包 zip（不含 `.libs/`、`.git/`）→ 交付最终成品，不交付半成品。
- 沟通语言：简体中文；代码注释：英文+中文双语。
- 每次修改/排查 bug 前**先读代码取证**，不猜。每次修改后 git 提交并更新本文件。

## 3. 总体进度 / Overall progress

| 里程碑 | 内容 | 状态 |
|---|---|---|
| P0 | 包结构/组件/设置/枚举/Api 扩展接口/i18n 加载/NDMF 插件注册/日志/取消进度窗口/AAO 互操作/管线骨架 | ✅ 完成 |
| P1 | 分析：渲染器/材质/贴图收集（跳 EditorOnly/禁用）、变换/特殊用途检测→白名单、动画扫描（贴图/材质切换、ST、Cutoff、渲染模式、缩放、开关）、贴图去重、UV 岛提取（三角形并查集/重叠合并/越界归一）、类型组/UV 组、AAO 通道检测（SMR API + MR 反射） | ✅ 完成 |
| P2 | 质量指标（MS-SSIM/SSIM 回退/ΔE2000/alpha IoU·RMSE/法线角度 p95/灰度 RMSE）+ 岛级二分缩放（先均匀后双轴、纯色短路、形态键 0/100 取大、动画最大缩放、密度钳制、UV 组木桶 K、无损模式） | ✅ 完成 |
| P3 | 装箱：4px 光栅化（CPU）、全扫描 BLF（面积降序/边长降序/90° 旋转转置）、候选图集池（POT 64..8192 移动端 4096；NPOT 步进 64 + 池裁剪）、规范主循环（首个装下全部队列的候选胜出+动态 padding）、同贴图同页、放弃+warning | ✅ 完成 |
| P4 | 图集合成（主图+镜像页、pull-push 无限外扩、透明 alpha 保持 0、法线重归一化）、UV 重映射（旋转折叠进映射）、最终贴图解析、动画改写（对象引用/槽索引/向量曲线）、Apply 原子应用（共享资产克隆+RegisterReplacedObject）、AAO 撤离、组件自移除 | ✅ 完成 |
| P5 | 导入参数：安全压缩格式枚举（类别×平台×NPOT×通道过滤+fallback+警告）、Mipmap+MipStreaming 绑定（m_StreamingMipmaps）、平台 override；材质去重+不透明子网格槽合并+动画索引重映射；优化后贴图/图集去重（开关） | ✅ 完成 |
| P6 | 报告（默认摘要/verbose 细节+临时文件）、耗时日志、图集来源/岛数/大小/利用率/优化量、NDMF 控制台输出、检查器 UI（i18n/平台限制格式选项） | ✅ 完成 |
| R | Reviewer×2 轮共识审查（9+2 项修复）→ QA×3 独立全量验收（QA-1/2/3 各修复后 PASS，全票）→ zip 交付 + README + .meta | ✅ 完成 |

## 10. 交付后待验证（用户侧 Unity 验证）/ Post-delivery verification

- [ ] 用户同步工程后：编译通过（本环境无 Unity，未能实际编译）
- [ ] 典型 Avatar 完整烘焙：控制台 [ATO] 报告、图集 ATO_* 资产、UV 重映射正确性（目视）
- [ ] 有 lilToon / 标准着色器 / 动画切换材质贴图的 Avatar
- [ ] 无 AAO / 有 AAO（RemoveMeshByUVTile）两种场景
- [ ] 取消路径（烘焙中点取消 → Avatar 原样 + 报错）
- [ ] 移动端构建目标（4096 上限、ASTC/EAC）
- 若发现 bug：先读代码取证 → 修复 → 更新本文件 → git commit → 重跑受影响 QA 项

## 4. 已确认的 NDMF 1.14.4 API 要点（已读源码验证）/ NDMF API facts

- 插件注册：`[assembly: ExportsPlugin(typeof(XPlugin))]` + `class XPlugin : Plugin<XPlugin>`，`Configure()` 内 `InPhase(BuildPhase.Optimizing).AfterPlugin("...").BeforePlugin("...").Run(typeof(XPass))`。
- 插件可 override `QualifiedName`（字符串，用于约束引用与 fallback 排序）；Pass 为 `Pass<T>` 单例，`Execute(BuildContext)`。
- 约束是 WeakOrder，引用不存在的插件名时自动退化为幻影（未装 MA/AAO 也安全）。
- 相位顺序：FirstChance → PlatformInit → Resolving → Generating → Transforming → **Optimizing** → PlatformFinish。MA 主要在 Resolving/Transforming；AAO 在 Optimizing 且刻意用 U+FFDC 命名排最后。ATO 用 Optimizing + 显式约束 + ASCII QualifiedName → 天然位于 MA 后、AAO 前。
- Pass 抛异常会被 `ExecutePassBody` 捕获 → `Plugin.OnUnhandledException(e)` + `ErrorReport.ReportException(e)`（public static）。**构建不会因此中断**，但 VRC 构建会因 ErrorReport 有 Error 而失败 —— ATO 用"抛出 ATOPipelineFatalException / ATOPipelineCancelledException"作为"报错中止构建"的惯用方式。
- 新资产：`ctx.AssetSaver.SaveAsset(tex)`（或 `ctx.OpenSerializationScope()`）；`ctx.IsTemporaryAsset(obj)` 判断是否本次构建临时资产（可安全改写）。
- 平台过滤：Pass 默认只跑 `WellKnownPlatforms.VRChatAvatar30`；插件标 `[RunsOnAllPlatforms]` 则全平台。
- i18n：NDMF 语言存于 `LanguagePrefs.Language`（EditorPrefs "nadena.dev.ndmf.language-selection"）；NDMF 的 `InlineError` 是 internal（不可用），第三方只能走 `ErrorReport.ReportException`。MA 的 i18n 模式 = 包内 JSON + 自写 Localizer 桥接（ATO 采用同样思路但更独立：`Sources/Editor/I18n/ATOI18n.cs`）。
- NDMF 无取消钩子 → ATO 自建 `ATOSession`（窗口+标志+检查点），取消 = 抛 `ATOPipelineCancelledException` + ErrorReport 显示 + Avatar 未改（原子 Apply 设计）。

## 5. 关键设计决策（Coder 共识，详见 docs/CODER_CONSENSUS_01.md）/ Key decisions

1. **单 Pass 管线 + 原子 Apply**：阶段 0-6 只算内存 PLAN，阶段 7 才写 Unity 对象。取消/异常永远不留半成品。
2. **UV 组同位约束**：同 UV 组各图集**归一化布局完全一致**（同 scale+offset）；图集**像素尺寸**可按类型缩小（法线图集可更小省体积），padding 按各图集自身最大边计算。
3. **白名单语义**：白名单对象引用的贴图跳过**全部**优化（含导入参数）；与其同 UV 的其他贴图跳过**图集化**，但参与整图缩放+导入参数优化。去重结果若含白名单则也视为白名单。
4. **像素密度**：岛世界尺寸 = 岛 UV 尺寸 × (网格 bounds 平均边长 / 网格 UV 跨度)，像素预算 = clamp(世界尺寸 × 密度, 4px, 原尺寸)，密度受用户 min(2048)/max(4096) 钳制；最终还受岛在原文件上的真实像素钳制。
5. **质量档位**：Lossless(1.0)/High(0.95)/Medium(0.90,默认)/Low(0.80)/Extreme(0.70)/Custom（原始参数，默认近无损）。映射：ssim=q；ΔE2000=0.4+6t²；alphaRMSE=0.002+0.12t^1.5；cutoutIoU=0.99-0.5t²（t=1-q）；normalP95=0.5+12t^1.5°；grayRMSE=0.002+0.12t^1.5。全部指标同时达标才算通过。
6. **UV 越界归一**：岛整体可平移进 [0,1]（extent≤1/轴）→ 平移归一并按原 UV 采样；跨 wrap 缝依赖 repeat 或 extent>1 → 白名单+warning。
7. **同贴图重叠岛**：归一化后 UV 区域重叠的岛合并为一个复合岛（位掩码并集），图集内只占一份，各网格引用同一矩形。
8. **AAO 互操作**：反射调用 `Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI`（IsTexCoordUsed / RegisterTexCoordEvacuation）。只支持 SkinnedMeshRenderer；MeshRenderer 冲突时白名单+warning。AAO 未安装 = no-op。
9. **lilToon 2.x 关键点**（已读源码）：alpha 模式 = `_TransparentMode` float（0 不透明/1 裁剪/2 透明/4 预乘），关键字 `UNITY_UI_ALPHACLIP`(cutout) / `UNITY_UI_CLIP_RECT`(trans)；主色 `_MainTex`（`_MainTex_ScrollRotate` 非零→白名单）；法线 `_BumpMap`（`_UseBumpMap` 开关）；次色 `_Main2ndTex`（`_UseMain2ndTex`+`_Main2ndTex_UVMode` 0-3=UV0-3，4=MatCap→特殊用途白名单）；`_Main3rdTex` 同理；`_EmissionMap`（`_EmissionMap_UVMode`，4=Rim→白名单）；cutoff=`_Cutoff`、`_SubpassCutoff`。`[NoScaleOffset]` 贴图（渐变/抖动/调节蒙版）= 特殊用途白名单。
10. **标准关键字着色器**：按标准属性名（_MainTex/_BaseMap、_BumpMap/_NormalMap、_MetallicGlossMap、_EmissionMap…）+ 关键字（_ALPHATEST_ON/_ALPHABLEND_ON/_ALPHAPREMULTIPLY_ON）+ `_Cutoff` 识别；无法确定 UV 通道/角色 → 白名单+warning（安全默认）。
11. **材质槽合并**：仅同一网格、内容+参数完全一致、且**不透明队列**的材质才合并子网格与材质槽；动画中材质引用（object reference 曲线）与槽索引同步重映射。透明材质绝不合并（过绘顺序）。
12. **压缩格式安全**：每类别（不透明/透明/法线/灰度）安全枚举；构建时校验通道需求（有透明度绝不选无 alpha 格式）+ NPOT 支持（NPOT 时剔除 PVRTC）+ 平台支持；不满足 → 安全回退 + 控制台 warning（如灰度单通道格式遇到多通道灰度图 → 仍按多通道保存并 warning）。
13. **Mipmap/MipStreaming 绑定**：VRChat 要求开 Mipmap 必开 MipStreaming → 每类别单一开关同时控制两者；默认开启；不在白名单的贴图/图集默认开 MipStreaming。
14. **NPOT**：默认关（POT 64..8192，移动端 4096 上限）；开 → 步进 64，上限同；自动剔除不支持格式。
15. **图集命名**：`ATO_` 前缀。图集数量不限，随处理自然增长。
16. **内存**：贴图解码缓存一份、岛位图按批处理、用完即释放；GPU 用 RenderTexture 批量做双线性重采样/上采样/边缘 pull-push；CPU 用 Burst Jobs 做光栅化与指标计算。
17. **日志**：`[ATO]` 前缀，含每步耗时；报告默认摘要、细节折叠（verbose 开关展开）；日志类别掩码 + verbose 开关（高级用户）。
18. **i18n**：`i18n/*.json` 扁平键值；有几个文件就有几个语言；Auto 跟随 NDMF 语言；缺失回退英文→键名。控制台日志恒英文（机器可读）。
19. **扩展 API**（`Sources/Api/`，autoReferenced）：IATOShaderAnalyzer / IATOWhitelistContributor / IATOQualityMetric / IATOAtlasPacker / IATOTexturePostProcessor，`ATOApiRegistry` 自动发现+显式注册。
20. **自清理**：Apply 成功后 `DestroyImmediate(atoComponent)`（构建目标是克隆体，安全）。

## 6. 目录结构 / Layout

```
package.json / i18n/{en,zh-Hans}.json / CLAUDE.md / README.md(最后写)
Sources/Runtime/   ATOComponent, ATOEnums, ATOQualityParams          (asmdef: ...runtime)
Sources/Api/       公开扩展接口 + 注册表                              (asmdef: ...api, Editor-only, autoReferenced)
Sources/Editor/    ATOPlugin, ATOPipelinePass, Core/, Interop/, I18n/,
                   Stages/, Analysis/, Quality/, Packing/, Atlas/,
                   Import/, Dedup/, UI/, Report/                     (asmdef: ...editor, autoReferenced=false)
.libs/             参考库源码（.gitignore，不进 zip）
docs/              PLAN.md, CODER_CONSENSUS_*.md, REVIEW_LOG.md, QA_LOG.md
```

## 7. 用户要求备忘（验收基准）/ User requirements checklist（节选）

- 仅处理：被启用或被动画启用的 SMR/MeshRenderer（跳过 EditorOnly）上、经网格 UV 采样、**无 ST 平移/缩放/旋转（含动画）**、**无特殊用途**的 Texture2D；任一不符 → 白名单。
- 只改贴图和 UV，**绝不改材质其他任何着色器参数**。
- 处理前按实际像素+导入设置去重并更新引用。
- 图集开关默认勾选；不勾选 → 不剔 UV、不重排、直接缩整图。
- 形态键只取 0/100 两者最大；动画缩放取最大面积。
- 多通道 UV：直接拆成独立 UV 处理。
- 动画兼容：材质/贴图切换、渲染模式/Cutoff 动画取最严、多材质槽、VRC 组件兼容。
- 目标质量=1 → 跳过对应类型岛缩放（含纯色），不重采样原样拷贝；≠1 → 纯色岛短路缩到 min(4, 原包围盒短边)。
- UV 缩放二分搜索取最严阈值；UV 组木桶效应（取组内最严格），结果不大于组内最大原尺寸。
- MS-SSIM：原尺寸包围盒短边 <176px 回退单尺度 SSIM；<11px 忽略该参数（不透明同理）。
- alpha：Cutout=clip 后轮廓 IoU；Blend=线性 RMSE；多材质引用逐一评估取最严。
- 法线：正确解码→重采样→重归一化→编码后角度误差 p95。灰度：仅被使用通道、线性 RMSE、逐通道取最差。
- 比较方式：缩小后岛实际覆盖区双线性上采样回原尺寸与原图比。评估用 Burst 并行 + GPU（RenderTexture）批量；不含最终压缩损失。
- 装箱细节：4px 粒度光栅 + 全扫描 BLF + 光栅化后面积降序+边长降序+90° 步进（位掩码转置；法线切线数据保持原样绝不重算）+ 候选图集池。
- 装箱队列规则：同贴图所有岛必须同图集；先算队列全部岛面积，丢弃装不下的候选，按面积升序+长宽比接近正方形优先；单个贴图装不下最大图集剩余 → 新开/复用同类队列；单贴图都装不进最大图集 → 放弃该 UV 组图集化 + warning。
- padding = max(ceil(图集最大边/128), 用户最小 4/8/16/32/64 默认4)；边缘 GPU pull-push 无限外扩（透明 alpha 保持 0）。
- 平台：PC/Android/iOS override（勾选才显示），默认读当前构建平台。
- 图集默认关 Read/Write、强制 Clamp（不给用户改）；其余参数取所有贴图中质量最高的。
- 剔除危险选项 + 构建时安全 fallback，任意选项组合不伤材质。
- 烘焙/构建显示阶段+进度+可取消（取消保留磁盘临时资产、释放 CPU/GPU/内存）。
- 烘焙后移除自身；NDMF 控制台显示报告；兼容 AAO UVUsageCompabilityAPI（原文拼写）；支持未装 AAO。
- 暂不支持 NDMF 预览；内存占用要克制、无泄漏。
- 处理顺序：MA 后、AAO 前。
- 交付物：zip 包 + README.md（面向用户与第三方开发者）。

## 8. 已知风险与取舍 / Risks & accepted tradeoffs（详见 README"已知行为"）

- 本环境无 Unity：代码未经实际编译；所有 API 用法均对照 NDMF/AAO/MA/lilToon 源码验证，
  但用户侧首次编译仍可能发现小问题（先取证再修，见交付后验证清单）。
- 光栅化与质量评估为 CPU 实现（Burst 仅间接受益）；大岛多时速度受限（README 已说明）。
- SSIM 2048 上限、材质关键字表近似、形态键 submesh0 近似（均为安全方向取舍）。
- LICENSE：MIT（package.json），待用户最终确认。

## 9. 最近提交记录 / Recent commits

- 2026-08-21：交付完成 —— P0-P6 全部实现；Coder 共识（docs/CODER_CONSENSUS_01.md）；
  Reviewer 2 轮（9+2 项修复）；QA×3 独立全量验收（各修复后 PASS）；README.md；
  全部 .meta（稳定 GUID）；i18n en/zh-Hans；zip 交付。
- 主要提交：d573d90 P0 / c80130f P1-P3 / ea9652d P4-P6 / f5e58ae R1 / d97c975 R2 /
  5cb9d51 QA-1 / b3076eb QA-2/3 / 最终 README+.meta。
