# Avatar Texture Optimizer (ATO)

> 全世界最好的 VRChat 贴图优化工具 — 开源 NDMF 工具 / The best VRChat avatar texture optimizer — an open-source NDMF tool.

ATO 分析 Avatar 上的网格，为满足条件的材质建立“网格 UV → 贴图”的映射关系；按目标质量算法（MS-SSIM、ΔE00、alpha、法线角度误差、灰度 RMSE）在导入后的有效贴图上缩小 UV 岛，剔除未使用的 UV 区域，重新分配 UV 并把贴图碎片重组为图集——在保证质量的同时最大化贴图利用率、降低贴图内存与体积。

ATO analyzes the meshes on your avatar, builds the UV→texture mapping for eligible materials, shrinks UV islands against a target-quality algorithm (MS-SSIM, ΔE00, alpha, normal angle error, grayscale RMSE) on the imported textures, discards unused UV areas, re-lays-out the UVs and recombines the pieces into atlases — maximizing texture utilization while preserving quality.

## 安装 / Installation

通过 VCC/VPM 安装依赖后把本仓库作为包安装（或直接放入 `Packages/`）：

1. 安装 [NDMF](https://github.com/bdunderscore/ndmf)（必需）。
2. 推荐安装 [Modular Avatar](https://github.com/bdunderscore/modular-avatar) 与 [Avatar Optimizer](https://github.com/anatawa12/AvatarOptimizer)（均可选，ATO 自动检测并兼容；未安装 AAO 时自动跳过 UV 疏散）。
3. 把本包 `net.fosa.avatar-texture-optimizer` 放入 `Packages/` 或通过 VPM 添加。

Requirements: Unity 2022.3+, NDMF 1.14.4+, Burst / Collections / Jobs / Mathematics packages (Unity built-in).

## 快速上手 / Quick Start

1. 在 Avatar 根对象上添加组件 `ATO → Avatar Texture Optimizer (Root)`（挂载对象必须有 `VRCAvatarDescriptor`；一个 Avatar 只允许一个）。
2. 保持默认设置（高画质挡位、生成图集、2048–4096 px/m 密度带、4px padding）。
3. 上传/构建 Avatar。NDMF 控制台会显示最终报告（总体结果默认展开、细节折叠），Unity 控制台有 `[ATO]` 前缀的完整日志。

## 工作原理 / How it works

1. **扫描**：收集渲染器/材质槽/贴图槽；着色器属性表+`[Normal]`/`[NoScaleOffset]`/`[MainTexture]` 特性+关键字自动分析贴图用途（liltoon 优先支持，其他标准关键字着色器通用兼容；无法分类→白名单+警告）。
2. **动画分析**：解析全部 Animator/Animation 剪辑——材质切换、贴图切换、ST 动画、启用/禁用、物体缩放、Cutoff/渲染模式动画（全部取最严苛）。
3. **贴图去重**：像素内容+导入设置完全相同的贴图合并并更新全部引用。
4. **岛提取**：并查集分割 UV 岛；重叠岛合并；越界 UV 安全整数平移归一（跨 wrap 缝/Clamp/Mirror 不安全→白名单）；多通道 UV 逐通道独立处理；形态键取 0/100 帧最大面积。
5. **目标质量缩放**（Burst 并行，线性空间）：逐岛逐贴图二分搜索最小缩放（预乘 alpha 重采样、双线性回放大后与原图比较）——MS-SSIM（短边<176px 退单尺度 SSIM、<11px 跳过）+ΔE00+alpha（Cutout IoU / Blend RMSE）+法线角度误差（均值+p95）+灰度逐通道 RMSE；先均匀后逐轴各向异性细化；纯色岛缩到 min(4,短边)；质量=1（自定义挡位全 1）时跳过缩放原样拷贝；密度带 [2048, 4096] px/m（可调 512–8192）防糊/防浪费。
6. **装箱**：候选图集池（POT 64–8192 / 实验性 NPOT 64 步进，移动端上限 4096）；4px 粒度位掩码 BLF 全扫描；90° 旋转步进（含切线数据的组禁用，切线绝不重算）；padding = max(ceil(边长/128), 用户最小值 4/8/16/32/64)。**同一 UV 在所有图集（主色/法线/蒙版…）中位置完全一致**；类型组质量需求更低时对应图集整体更小。
7. **合成**：预乘重采样写入图集（法线 解码→重采样→重归一化→编码；近无损原样拷贝）；pull-push 无限外扩填充空白（alpha 保持 0）；图集 `ATO_` 前缀、数量不限、强制 Clamp、Read/Write 关。
8. **网格/引用**：克隆网格重写 UV（不动其他任何数据）；AAO 存在时按 `UVUsageCompabilityAPI` 疏散被 AAO 使用的通道；动画曲线引用（材质/贴图/槽索引）全部重映射；用户原始材质/贴图资产绝不修改（必要时克隆材质）。
9. **导入参数**：Mipmap+MipStreaming 单开关绑定（VRChat 要求）；压缩格式按 不透明/透明/法线/灰度 × 平台（PC/Android/iOS override）安全枚举，NPOT 时剔除 PVRTC，灰度单通道仅在确为单通道时使用（否则回退+警告）。
10. **去重/合并**：完全相同的材质、图集去重；同网格不透明相同材质槽合并（动画槽索引自动重映射）。
11. **收尾**：移除自身组件；NDMF 控制台报告；`[ATO]` 日志含每步耗时、图集来源/岛数/大小/利用率/节省量；全程进度可取消（保留磁盘临时资产、释放内存）。

## 白名单 / Whitelist

白名单不限对象类型（网格/材质/贴图/动画等）：白名单对象引用的贴图跳过一切优化（含导入参数）；同 UV 的其他贴图跳过图集化、参与整图缩放与导入参数优化。任何不安全用法（ST 动画、非默认 ST、跨缝 UV、无法分类的着色器属性等）自动视作白名单并输出警告——**绝不产出错误结果**。

## 选项摘要 / Options

| 分类 | 选项 | 默认 |
|---|---|---|
| 图集 | 生成图集开关 / 最小 padding / 实验性 NPOT | 开 / 4 / 关 |
| 目标质量 | 挡位（极高/高/中/低/自定义） | 高（MS-SSIM 0.999, ΔE00 1.0, IoU 0.9995, αRMSE 0.005, 法线 0.25°/1°, 灰度 0.005） |
| 像素密度 | min/max px/m | 2048 / 4096（挡位 512–8192） |
| Mip | Mipmap+MipStreaming 单开关 | 开 |
| 压缩 | 透明/不透明/法线/灰度 × 平台 override | Auto（PC: BC7/BC5，移动: ASTC） |
| 其他 | 白名单、语言（Auto 跟随 NDMF）、日志级别 | — |

## 兼容性 / Compatibility

- 运行于 NDMF Optimizing 阶段：Modular Avatar 之后、Avatar Optimizer 之前；检测到 TexTransTool 时在其后运行并提示。
- 兼容 AAO 的 `UVUsageCompabilityAPI`（原拼写）；未安装 AAO 时自动跳过疏散。
- 未安装 VRC SDK / MA / liltoon 时均可工作（反射/通用分析降级）。
- 暂不支持 NDMF 预览。
- 处理只修改：网格 UV、贴图引用、贴图资产、材质槽（受限合并）；材质其他着色器参数绝不修改。

## 开发者 / Developers

- 扩展点（`Editor/API/AtoExtensions.cs`）：`AtoTextureUsageProvider`（自定义贴图分类）、`AtoQualityMetricProvider`（自定义质量指标）——自动发现或手动注册。
- i18n：`Editor/Resources/ATO/i18n/*.json`；在 `Assets` 下任意 `ATO/i18n` 文件夹添加同格式 JSON 即可新增语言（有几个文件显示几个语言，缺失回退英文）。维护脚本：`tools/gen_i18n.py`。
- 日志：`AtoLog` 三级（Summary/Normal/Verbose）；所有阶段均输出耗时与统计。
- 架构设计见 `docs/architecture.md`；质量指标与预设依据见 `docs/quality.md`；开发记忆见 `CLAUDE.md`。

## 已知限制 / Known limitations

- 图集背景 pull-push 外扩存在渗色（已知、可接受；透明贴图 alpha 保持 0）。
- 骨骼动画形变不参与面积计算（以绑定姿态/形态键 0/100 帧为准）。
- 多图集共享 UV 布局时，后生成的图集沿用首先生成的 padding 布局（安全性由质量阈值兜底）。
- 无 dotnet 编译环境：代码经过 3 轮 Reviewer + 3 轮 QA 静态审查，请在你的 Unity 工程中验证烘焙结果。
