# Avatar Texture Optimizer (ATO)

> An NDMF plugin for VRChat Avatar texture optimization — analyzes meshes, re-UVs and atlases textures with quality-aware island scaling.
>
> VRChat Avatar贴图优化NDMF插件 —— 分析网格，以质量感知的UV岛缩放进行贴图重拆与图集化合。

[English](#english) | [中文](#中文)

---

## English

### Overview

Avatar Texture Optimizer (ATO) is an open-source NDMF tool that optimizes textures on VRChat avatars. It analyzes the mesh UV layout, determines which parts of each texture are actually sampled, and repacks them into efficient atlases — all while maintaining visual quality through sophisticated quality metrics.

### Features

- **Quality-Aware UV Island Scaling**: Binary-search based per-island scaling using MS-SSIM, CIEDE2000 ΔE, alpha metrics, normal map angle error, and grayscale RMSE
- **Texture Type Grouping**: Groups textures by type (normal maps, masks, etc.) to avoid wasting atlas space
- **Animation-Aware**: Handles material swaps, texture changes, render mode changes, and object enable/disable in animations
- **lilToon + Standard Shader Support**: Automatic analysis of shader properties and keywords
- **Platform-Aware**: Per-platform optimization settings for PC, Android, and iOS
- **Smart Atlas Packing**: Raster bitmask bin packing (4px granularity) with Bottom-Left-Fill + rotation support
- **AAO Compatibility**: Integrates with Avatar Optimizer's UVUsageCompabilityAPI
- **Blend Shape Aware**: Evaluates blend shape deformation for accurate area calculation
- **Whitelist System**: Skip optimization for specific meshes, materials, textures, or animations
- **i18n Support**: English and Simplified Chinese with extensible JSON localization
- **NDMF Integration**: Runs after Modular Avatar, before AAO, with proper build report output

### Requirements

- Unity 2022.3+
- VRChat SDK 3.7.0+
- NDMF 1.14.0+
- Modular Avatar 1.18.0+
- (Optional) Avatar Optimizer 1.8.0+ for UV compatibility
- (Optional) lilToon 2.0.0+ for full shader support

### Installation

1. Add the VPM repository: `https://fosa-net.github.io/avatar-texture-optimizer/vpm.json`
2. Install "Avatar Texture Optimizer" via VCC or UPM
3. Add the `Avatar Texture Optimizer` component to your avatar root (same object as VRCAvatarDescriptor)

### Usage

1. Add the `Avatar Texture Optimizer` component to your avatar's root GameObject
2. Configure quality preset, atlas settings, and platform options
3. Build your avatar — ATO runs automatically during the NDMF build pipeline

### Quality Presets

| Preset | MS-SSIM | ΔE (CIEDE2000) | Use Case |
|--------|---------|-----------------|----------|
| Near Lossless | 0.999 | 0.5 | Maximum quality, minimal savings |
| High | 0.97 | 1.0 | High quality with noticeable savings |
| **Balanced** (default) | 0.95 | 2.0 | Best balance of quality and size |
| Performance | 0.90 | 4.0 | Aggressive optimization |
| Aggressive | 0.85 | 6.0 | Maximum size reduction |
| Custom | User-defined | User-defined | Full control |

### Quality Metrics

- **MS-SSIM** (Multi-Scale Structural Similarity): Primary metric for color textures
- **SSIM**: Fallback for small islands (< 176px bounding box)
- **CIEDE2000 ΔE**: Perceptual color difference
- **Alpha IoU**: Cutout silhouette comparison
- **Alpha RMSE**: Blend transparency quality
- **Normal Angle Error**: Normal map accuracy (average + P95)
- **Grayscale RMSE**: Per-channel mask/roughness quality

### Third-Party Extension API

Developers can extend ATO's capabilities:

```csharp
// Register a custom shader analyzer
[InitializeOnLoad]
static class MyPlugin
{
    static MyPlugin()
    {
        ATOShaderAnalyzerRegistry.Register(new MyShaderAnalyzer());
    }
}

// Register a custom texture processor
[InitializeOnLoad]
static class MyProcessor
{
    static MyProcessor()
    {
        ATOTextureProcessorRegistry.Register(new MyTextureProcessor());
    }
}
```

### Architecture

```
Build Pipeline (NDMF Optimizing Phase):
├── ValidationPass     → Validate component placement
├── AnalysisPass       → Analyze materials, animations, shaders
├── DeduplicationPass  → Deduplicate textures by content
├── QualityEvaluationPass → Per-island quality binary search
├── UVProcessingPass   → Atlas packing & UV reassignment
├── ApplicationPass    → Apply changes to avatar
└── PostProcessPass    → Dedup, AAO compat, report
```

### License

MIT License

---

## 中文

### 概述

Avatar贴图优化器（ATO）是一个开源NDMF工具，用于优化VRChat Avatar上的贴图。它分析网格UV布局，确定每张贴图的哪些部分实际被采样，并将它们重新打包到高效的图集中——同时通过复杂的质量指标保持视觉质量。

### 功能特性

- **质量感知UV岛缩放**：基于二分搜索的逐岛缩放，使用MS-SSIM、CIEDE2000 ΔE、alpha指标、法线贴图角度误差和灰度RMSE
- **贴图类型分组**：按类型（法线贴图、蒙版等）分组贴图，避免浪费图集空间
- **动画感知**：处理动画中的材质切换、贴图变化、渲染模式变化和对象启用/禁用
- **lilToon + 标准着色器支持**：自动分析着色器属性和关键字
- **平台感知**：PC、Android和iOS的分平台优化设置
- **智能图集装箱**：光栅位掩码装箱（4px粒度）+ 底部-左侧-填充 + 旋转支持
- **AAO兼容**：与Avatar Optimizer的UVUsageCompabilityAPI集成
- **形态键感知**：评估形态键变形以进行准确的面积计算
- **白名单系统**：跳过特定网格、材质、贴图或动画的优化
- **i18n支持**：英文和简体中文，可扩展JSON本地化
- **NDMF集成**：在Modular Avatar之后、AAO之前运行，带有正确的构建报告输出

### 系统要求

- Unity 2022.3+
- VRChat SDK 3.7.0+
- NDMF 1.14.0+
- Modular Avatar 1.18.0+
- （可选）Avatar Optimizer 1.8.0+ 用于UV兼容性
- （可选）lilToon 2.0.0+ 用于完整着色器支持

### 安装

1. 添加VPM仓库：`https://fosa-net.github.io/avatar-texture-optimizer/vpm.json`
2. 通过VCC或UPM安装"Avatar Texture Optimizer"
3. 将`Avatar Texture Optimizer`组件添加到Avatar根对象（与VRCAvatarDescriptor同一对象）

### 使用方法

1. 在Avatar根GameObject上添加`Avatar Texture Optimizer`组件
2. 配置质量挡位、图集设置和平台选项
3. 构建Avatar —— ATO在NDMF构建管线中自动运行

### 质量挡位

| 挡位 | MS-SSIM | ΔE (CIEDE2000) | 使用场景 |
|------|---------|-----------------|----------|
| 近无损 | 0.999 | 0.5 | 最高质量，最少节省 |
| 高质量 | 0.97 | 1.0 | 高质量且有明显节省 |
| **均衡**（默认） | 0.95 | 2.0 | 质量和大小的最佳平衡 |
| 性能优先 | 0.90 | 4.0 | 激进优化 |
| 激进 | 0.85 | 6.0 | 最大程度减小体积 |
| 自定义 | 用户定义 | 用户定义 | 完全控制 |

### 质量指标

- **MS-SSIM**（多尺度结构相似性）：彩色贴图的主要指标
- **SSIM**：小岛（包围盒 < 176px）的回退指标
- **CIEDE2000 ΔE**：感知颜色差异
- **Alpha IoU**：Cutout轮廓对比
- **Alpha RMSE**：Blend透明度质量
- **法线角度误差**：法线贴图精度（平均值 + P95）
- **灰度RMSE**：逐通道蒙版/粗糙度质量

### 第三方扩展API

开发者可以扩展ATO的功能：

```csharp
// 注册自定义着色器分析器
[InitializeOnLoad]
static class MyPlugin
{
    static MyPlugin()
    {
        ATOShaderAnalyzerRegistry.Register(new MyShaderAnalyzer());
    }
}

// 注册自定义贴图处理器
[InitializeOnLoad]
static class MyProcessor
{
    static MyProcessor()
    {
        ATOTextureProcessorRegistry.Register(new MyTextureProcessor());
    }
}
```

### 许可证

MIT License
