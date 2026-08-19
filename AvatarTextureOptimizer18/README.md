# AvatarTextureOptimizer (ATO)

> **VRChat Avatar 贴图优化工具** — 开源 NDMF 插件：在保证画质的前提下，最大化你的 Avatar 贴图利用率。
> **A world-class VRChat avatar texture optimizer** — an open-source NDMF tool that maximizes texture utilization while preserving quality.

包名 / Package: `net.fosa.avatar-texture-optimizer`

---

## 它能做什么 / What it does

ATO 在烘焙时分析 Avatar 的网格与贴图，通过**目标质量算法**把每个 UV 岛缩放到"肉眼不可察觉差异"的最小尺寸，剔除未使用的 UV 区域，再用**约束装箱**把大量贴图碎片合并成少数几张图集（Atlas），最终在保证表现一致的前提下大幅降低贴图内存与体积：

- **质量门控缩放**：线性空间重采样 + 预乘 alpha 下采样；MS-SSIM / CIEDE2000 ΔE / Cutout 轮廓 IoU / Blend alpha RMSE / 法线角度 p95 / 灰度逐通道 RMSE 六项指标全部达标才通过（木桶效应取最差），二分搜索逼近最优缩放，双轴独立细化应对各向异性。
- **约束装箱图集**：Burst 光栅化位掩码（4px 粒度）+ 全扫描 BLF + 90° 步进旋转 + 候选图集池；同一张贴图的所有岛保证在同一图集；同一 UV 的所有贴图（颜色/法线/蒙版/动画切换贴图）在图集中位置一致；贴图类型组防止法线图集"9/10 浪费"。
- **安全第一**：任何不确定的转换（ST 变换、特殊用途 UV、跨 wrap 缝、未知着色器……）一律回退白名单并输出警告；绝不动材质里贴图以外的任何参数。
- **只改贴图和 UV**：网格 UV 重写 + 贴图引用更新（含动画中的材质/贴图引用与材质槽索引），绝不修改着色器其他属性。

## 安装 / Installation

VPM 依赖：

- `com.vrchat.base` / `com.vrchat.avatars` ≥ 3.10.4
- `nadena.dev.ndmf` ≥ 1.14.4
- `nadena.dev.modular-avatar` ≥ 1.18.2

将本包放入工程 `Packages/` 目录即可（VPM 仓库地址待发布后补充）。

## 快速开始 / Quick Start

1. 在 Avatar 根物体（**必须与 `VRCAvatarDescriptor` 同一物体**）上添加组件 `VRChat SDK → Avatar Texture Optimizer (ATO)`。
2. 默认设置即可用（质量挡位 High、生成图集开启、密度 2048~4096 px/m）。
3. 需要排除某些对象：在 Avatar 下任意位置挂 `ATO Whitelist` 组件，把网格/材质/贴图/动画/物体拖入列表——白名单对象引用的全部贴图跳过所有优化。
4. 上传/构建 Avatar，烘焙完成后在 NDMF 控制台查看报告（各阶段耗时、图集大小/利用率/贴图来源/优化量）。

一个 Avatar 及其子级只允许挂载一个 ATO 组件；不合规挂载会报错并中止烘焙。

## 设置说明 / Settings

### 基础
| 设置 | 默认 | 说明 |
|---|---|---|
| 生成图集 | 开 | 关闭时：不生成图集、不剔除未使用 UV、不重排 UV，直接缩放整张贴图并做其他优化 |
| 质量挡位 | High | High / Medium / Low / Ultra / Custom。Custom 默认全 1（近无损），参数由你修改且不会被其他挡位覆盖 |
| 最小/最大像素密度 | 2048 / 4096 px/m | 挡位 512/1024/2048/4096/8192；高于 max 的岛缩到 max（防浪费），低于 min 的不再缩小（防发糊） |

### 质量挡位参数（依据学术/业内研究设定）
| 挡位 | MS-SSIM ≥ | ΔE2000 ≤ | Cutout IoU ≥ | Blend α RMSE ≤ | 法线角 p95 ≤ | 灰度 RMSE ≤ |
|---|---|---|---|---|---|---|
| Ultra | 0.995 | 0.5 | 0.999 | 1/255 | 0.5° | 0.5/255 |
| High（默认） | 0.99 | 1.0 | 0.995 | 2/255 | 1.5° | 1/255 |
| Medium | 0.97 | 3.0 | 0.98 | 6/255 | 4° | 3/255 |
| Low | 0.93 | 6.0 | 0.95 | 12/255 | 8° | 6/255 |

参考依据：MS-SSIM（Wang et al. 2004）0.95–0.99 为高质量压缩常见目标；CIEDE2000（Sharma et al. 2005）ΔE ≤ 2.3 为 JND（刚好可察觉），High 取 ≤ 1.0；其余为业内法线/蒙版重采样实践值。Custom 挡位阈值折叠在高级选项中。

### 贴图格式
四类贴图（不透明颜色 / 含透明颜色 / 法线 / 灰度蒙版）分别设置压缩格式与 Mipmap。
**Mipmap 与 MipStreaming 为绑定开关**（VRChat 要求开启 Mipmap 时必须开启 MipStreaming）。
自动模式按平台与内容选择最优；显式格式经平台安全校验（如 iOS 自动剔除 BC7、NPOT 剔除 PVRTC、含透明强制保留 alpha、多通道灰度强制多通道保存并告警）。

### 高级
- 图集 padding（4/8/16/32/64，实际值 = max(选项, ceil(最大边长/128)，≥4px)）
- NPOT 图集（实验性；边长 64 步进，默认关闭）
- 图集最大边长（默认 8192，移动端钳制 4096）
- 贴图去重 / 材质去重 / 合并材质槽（默认开启）
- 详细日志（默认开启；`[ATO]` 前缀，含各阶段耗时与图集明细）

### 平台覆盖
PC / Android / iOS 三个平台可分别覆盖全部优化参数；未勾选时使用通用设置，默认读取当前构建平台。

### 语言
`Auto` 跟随 NDMF 语言设置；`Packages/net.fosa.avatar-texture-optimizer/Localization/` 下的 json 配置文件有几个语言就显示几个语言（内置英文与简体中文），缺失翻译回退英文。

## 管线 / Pipeline

全部发生在 **Modular Avatar 之后、Avatar Optimizer 之前**（NDMF Optimizing 相位）：

```
验证 → 扫描材质槽 → 扫描动画 → 过滤槽位 → 收集贴图（去重）→ 白名单解析
→ UV 岛提取（越界归一/重叠合并/世界面积）→ UV 组与类型组 → 目标质量缩放（GPU + Burst）
→ 装箱（位掩码 BLF）→ 图集生成（GPU pull-push 外扩 + 格式/导入设置）→ 应用
（网格 UV 重写 + AAO UV 通道疏散、槽位合并与动画索引重写、材质克隆与去重、
动画贴图属性重写、fallback 整图缩放与导入副本、图集去重、移除自身组件）
→ 报告输出
```

要点：

- **UV 组**：同一 UV 对应的所有贴图（含动画切换贴图）构成一个 UV 组；同类型组内图集统一尺寸，保证同一 UV 在不同图集上的归一化位置一致。
- **类型组**：贴图种类组合 + sRGB + filterMode 完全相同的岛同组装箱（存在法线贴图的纹理集中成组，法线图集不会浪费 9/10）。
- **贴图连通簇**：同一张贴图的所有岛必定装入同一图集。
- **动画兼容**：材质槽切换、贴图属性动画、渲染模式/Cutoff 动画、缩放动画、形态键（仅取 0 与 100 状态的最大值）、GameObject/Renderer 启停全部纳入分析。
- **AAO 兼容**：反射调用 AAO 的 `UVUsageCompabilityAPI`（AAO 原文拼写）：被 AAO 使用的 UV 通道先备份到空闲通道并注册疏散，AAO 处理完后自动删除备份；未安装 AAO 时自动降级。
- **取消**：烘焙进度条可随时取消——中止烘焙、保留硬盘上的临时资产、释放 CPU/GPU/内存资源。

## 安全与回退 / Safety & Fallbacks

以下情况自动**白名单跳过**（跳过全部优化，含导入参数）并输出警告：

- 白名单对象（不限类型）中引用的全部贴图；白名单动画剪辑引用的贴图/材质；去重结果涉及白名单。
- 贴图存在 ST 平移/缩放/旋转（含动画修改）、`_ScrollRotate`、`_UVMode` 被动画修改、特殊用途 UV（MatCap/Rim/渐变/灯光值采样等）。
- UV 越界且跨 wrap 缝、Clamp/Mirror wrap 越界、无法归一。
- 着色器不受支持（liltoon 与标准关键字着色器自动兼容，其余经通用属性解析，解析失败则白名单 + warning）。
- 岛无法装入最大图集 → 放弃该岛整个 UV 组图集化，整图缩放后进入后续优化 + warning。
- 材质槽合并遇到非临时资产上的动画绑定 → 跳过该渲染器的合并 + warning。

同 UV 的其他贴图（NoAtlas 级）跳过图集化，但仍参与整图缩放与导入参数优化。

## 扩展开发 / For Developers

### 管线钩子（自动发现）

实现 `Fosa.AvatarTextureOptimizer.Editor.Extensions.IATOPipelineHook`（须有无参构造），编辑器加载时自动注册，可在任一管线阶段前后执行：

```csharp
public class MyHook : IATOPipelineHook
{
    public string Name => "MyHook";

    public void OnBeforeStage(ATOAnalysisSnapshot snapshot, BuildContext context)
    {
        Debug.Log($"[MyHook] before {snapshot.stageId}: {snapshot.textureCount} textures");
    }

    public void OnAfterStage(ATOAnalysisSnapshot snapshot, BuildContext context) { }
}
```

也可手动调用 `ATOExtensions.Register(hook)`。

### i18n 扩展

在 `Localization/` 目录添加 `<lang>.json`（如 `ja-jp.json`），key 与 `en-us.json` 一致即可；缺失 key 回退英文。UI 中会自动出现新语言。

### 质量算法参数

全部阈值在 `ATOMetricThresholds`（Runtime 设置模型）中；Custom 挡位可在 Inspector 高级选项中逐项修改。

## 构建报告 / Build Report

烘焙完成后输出到 NDMF 控制台：总体结果（总耗时、各阶段耗时、贴图/槽位统计、去重节省）+ 详细内容（详细日志开启时：每个图集的大小、利用率、岛数、贴图来源、相对原贴图的估算优化量）。

## 已知限制 / Known Limitations

- 暂不支持 NDMF 预览（按设计）。
- 质量指标在未压缩线性域计算，不包含最终压缩格式引入的损失。
- 岛的指标归约在 Burst（CPU 多线程）执行；GPU（RenderTexture）用于贴图线性化/预乘解码、图集外扩填充与编码。
- 同岛贴图分辨率必须一致才能图集化（共享 UV 映射的数学前提）；不一致时自动回退整图优化 + warning。
- 版本 0.1.0 处于开发阶段，配置字段可能变更，不做版本兼容承诺。

## 许可 / License

MIT
