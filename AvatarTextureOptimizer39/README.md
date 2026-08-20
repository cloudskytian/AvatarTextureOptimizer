# Avatar Texture Optimizer (ATO)

> 适用于 VRChat Avatar 的开源 NDMF 贴图优化工具 — 在保证画质的前提下，最大化贴图利用率。

**Avatar Texture Optimizer (ATO)** is an open-source [NDMF] tool that analyzes your avatar's
meshes, rebuilds the UV→texture mapping, scales UV islands with a target-quality algorithm,
and repacks them into atlases — maximizing texture utilization while preserving visual
fidelity.

[NDMF]: https://github.com/bdunderscore/ndmf

---

## ✨ 核心特性 / Features

- **UV 岛缩放（目标质量算法）** — 以 MS-SSIM + ΔE(CIEDE2000) + alpha(IoU/RMSE) + 法线角度误差 + 灰度 RMSE 为质量度量，用二分搜索找到可接受的最小缩放；先均匀缩放再双轴独立细化（各向异性）。
- **图集打包** — BLF（左下优先）+ 90° 步进旋转 + 4px 粒度位掩码光栅化 + 候选图集池；同一贴图的所有岛原子装箱。
- **贴图类型组** — 法线/蒙版等特殊贴图按（类别 + 色彩空间 + filterMode）分组，避免法线图集利用率浪费。
- **UV 组一致性** — 同一 UV 对应的所有贴图保持相同变换，同一 UV 在不同图集上位置一致。
- **贴图/材质去重** — 按实际像素 + 导入设置去重并更新材质与动画引用。
- **白名单** — 白名单对象（网格/材质/贴图/动画…）引用的贴图跳过所有优化。
- **动画兼容** — 分析动画中的材质/贴图切换、ST 变换、渲染模式/Cutoff 修改，取最严苛要求。
- **lilToon 分析** — 自动分析 lilToon 与其他使用标准关键字的着色器属性表。
- **安全 fallback** — 任意不安全转换（UV 跨缝、ST 变换、无法分析的着色器）一律白名单并告警。
- **平台覆盖** — PC / Android / iOS 分别 override（图集格式、最大边长、NPOT）。
- **可扩展 i18n** — JSON 配置文件本地化，内置英文 + 简体中文。
- **性能** — 预留 Burst/GPU 并行接口；缓存避免重复解码/光栅化；烘焙进度可取消。

## 🚀 快速开始 / Quick Start

1. 确保已安装 [NDMF]（≥1.14.4）与 VRChat SDK（Avatar 3.0）。
2. 在 Avatar 根节点（带 `VRCAvatarDescriptor` 的对象）上添加组件
   `Avatar Texture Optimizer → ATO Avatar Optimizer`。
3. 按需调整质量挡位、像素密度、压缩格式、白名单等。
4. 正常构建/上传 Avatar。烘焙完成后组件会自动从成品移除。

## ⚙️ 主要配置 / Configuration

| 设置 | 说明 |
|---|---|
| Generate Atlas | 是否生成图集（关闭则仅整图缩放，不剔 UV、不重排） |
| Min/Max Pixel Density | 像素密度钳制（默认 2048–4096 px/m，可选 512/1024/2048/4096/8192） |
| Quality | 质量挡位（Ultra/High/Balanced/Economy/Custom，默认 High） |
| Atlas Padding | 岛间距离（4/8/16/32/64，默认 4） |
| Allow NPOT | 实验性 NPOT 图集边长 |
| Compression | 按贴图类别（透明/不透明/法线/灰度）设置压缩格式与 Mip+Streaming 绑定开关 |
| Platform Override | 各平台覆盖 |
| Whitelist | 白名单对象列表 |
| Deduplicate Materials/Textures | 优化后去重 |

## 🎯 质量挡位 / Quality Levels

| 挡位 | MS-SSIM | ΔE2000 | Cutout IoU | 法线角度 | 灰度 RMSE |
|---|---|---|---|---|---|
| Ultra | 0.999 | 1.5 | 0.995 | 0.5° | 1/255 |
| **High（默认）** | 0.99 | 2.3 | 0.99 | 1.0° | 2/255 |
| Balanced | 0.97 | 4.0 | 0.98 | 2.0° | 3/255 |
| Economy | 0.95 | 6.0 | 0.95 | 3.0° | 5/255 |
| Custom | 全 1（近无损） | — | — | — | — |

> 阈值基于 MS-SSIM（Wang et al. 2003）、ΔE2000（Sharma et al. 2005）及业内经验。
> 小岛（包围盒短边 <176px）回退单尺度 SSIM；<11px 忽略 SSIM。

## 🔧 处理顺序 / Pipeline

ATO 运行在 NDMF 的 **Optimizing** 阶段，**在 Modular Avatar 之后、Avatar Optimizer (AAO) 之前**：

```
Validate → Collect → ShaderAnalysis → Dedup → ExtractIslands → ScaleIslands
→ PackAtlases → RegenerateTextures → RewriteReferences → Report
```

## 🧩 扩展开发 / Extending (for developers)

- **自定义着色器分析器** — 实现 `IATOShaderAnalyzer` 并调用
  `ATOShaderAnalyzerRegistry.Register(...)`。
- **AAO 兼容** — 通过反射调用 `UVUsageCompabilityAPI`（注意 AAO 原文拼写），
  在重排 UV 前疏散 AAO 使用的 UV 通道；未安装 AAO 时自动跳过。
- **i18n 扩展** — 在 `Assets/Editor/Localization/` 放置新语言 JSON 文件即可新增语言。

## 📄 依赖 / Dependencies

- [NDMF](https://github.com/bdunderscore/ndmf) ≥ 1.14.4
- [VRChat Avatars SDK](https://vrchat.com) ≥ 3.10
- 可选：Avatar Optimizer（自动检测，用于 UV 兼容）

## ⚠️ 已知限制 / Known Limitations

- 暂不支持 NDMF 预览。
- 多通道 UV 的贴图属性→UV 通道映射默认假设 UV0（预留扩展点）。
- 开发阶段版本，配置字段可随意调整。

## 📜 License

MIT（可后续按需调整）。
