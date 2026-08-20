# Avatar Texture Optimizer (ATO)

**net.fosa.avatar-texture-optimizer** — 面向 VRChat Avatar 的开源 NDMF 贴图优化工具。  
An open-source NDMF tool that atlases and quality-scales VRChat avatar textures by analysing mesh UVs.

> 只改网格 UV 和贴图引用，**绝不**改材质里其它着色器参数。  
> Only mesh UVs and texture references are changed. **Never** other shader parameters.

## 安装 / Install

1. 需要 VRChat Avatars SDK ≥ 3.7、NDMF ≥ 1.14.4、Unity 2022.3。
2. 用 VCC / VPM 添加本包，或把本文件夹放进 `Packages/net.fosa.avatar-texture-optimizer/`。
3. 可选：Modular Avatar、Avatar Optimizer (AAO)、lilToon。未安装 AAO 时会跳过 `UVUsageCompabilityAPI`。

## 小白怎么用 / For beginners

1. 选中 Avatar 根物体（必须有 `VRCAvatarDescriptor`）。
2. `Add Component` → **FOSA / Avatar Texture Optimizer**。整棵 Avatar 只能挂 **一个**。
3. 保持默认（High 质量、生成图集、PC/Quest 自动默认压缩）。
4. 正常 NDMF Bake / VRChat 上传即可。进度条可取消；取消后磁盘上的 `Assets/_ATO_Generated/` 会保留，内存会释放。

## 它做什么 / What it does

处理发生在 **Modular Avatar 之后、AAO 之前**（NDMF `Transforming`）。

1. 收集启用中或动画会启用的 `MeshRenderer` / `SkinnedMeshRenderer`。
2. 分析材质（lilToon 属性表 + 标准关键字 + 通用属性）与动画（切材质/贴图、ST、缩放、Cutoff、渲染模式）。
3. 不满足条件的贴图（ST/旋转/Decal/MatCap/跨 wrap 缝…）视为白名单并 warning。
4. 按像素 + 导入设置去重。
5. 提取 UV 岛（多通道、重叠合并、可平移归一到 [0,1]）。
6. 面积考虑形态键 0/100 与动画最大缩放。
7. 目标质量：线性重采样、预乘 alpha、MS-SSIM + CIEDE2000 + alpha IoU/RMSE；法线解码后角误差；灰度按通道 RMSE。Burst CPU，GPU 辅助。
8. 类型组避免「一张法线配九张空图集」；UV 组保证同一 UV 在不同图集上位置相同。
9. 4px 位掩码 BLF 装箱 + 90°（法线只转像素切线 XY，**不重算网格切线**）。
10. 回写网格 UV、材质贴图引用、动画对象曲线；可选材质/贴图去重与不透明槽合并。
11. 图集强制 Clamp、关闭 Read/Write；Mipmap 与 MipStreaming 绑定。
12. 烘焙后移除自身组件，NDMF 控制台输出报告。日志前缀 `[ATO]`。

## 质量挡位 / Quality presets

依据 Wang MS-SSIM、CIEDE2000（ΔE&lt;1 不可察、≈2 近看可察）以及业界法线角误差惯例：

| 挡位 | 行为 |
| --- | --- |
| Near Lossless | 目标质量=1，跳过 UV 缩放（含纯色） |
| Ultra / High(默认) / Medium / Low | 见组件高级折叠里的阈值 |
| Custom | 默认全 1，**不会被其它挡位覆盖** |

像素密度默认 2048–4096 px/m，挡位 512/1024/2048/4096/8192，并受原岛物理像素钳制。

## 白名单 / Whitelist

对象类型不限（网格、材质、贴图、动画…）。白名单引用到的 **全部 Texture2D** 跳过所有优化。  
与其同 UV 的其它贴图：**跳过图集化**，但仍可整图缩放与导入参数优化。

## 平台 / Platform

可选按 PC / Android / iOS 覆盖压缩格式。不勾选则用当前构建目标的通用最优默认值。  
实验性 NPOT 会剔除 PVRTC 等不支持格式。安全回退：有 alpha 不许选无 alpha 格式；多通道灰度不会被强行存成单通道。

## 暂不支持 / Not yet

- NDMF 预览

## 给第三方开发者 / For third-party developers

扩展接口在 Runtime 程序集 `net.fosa.avatar-texture-optimizer.runtime`：

```csharp
// 自定义着色器分析器（Priority 越大越先）。
ShaderAnalyzerRegistry.Register(new MyAnalyzer());

public class MyAnalyzer : IShaderAnalyzer
{
    public int Priority => 100;
    public string Name => "MyShader";
    public bool TryAnalyze(Material mat, out ShaderAnalysisResult result) { /* ... */ }
}

// 管线前后钩子
AtoHookRegistry.Register(new MyHook());
```

- 请先读 lilToon / NDMF / AAO 源码再对接，不要猜测 API。
- i18n：把 `Editor/i18n/<bcp47>.json` 丢进去即出现新语言。`Auto` 跟随 NDMF `LanguagePrefs`，缺失回退 `en-us`。
- 日志：`[ATO]`，组件上有 Verbose 开关。
- 图集名以 `ATO_` 开头，生成目录 `Assets/_ATO_Generated/`。

## 许可证 / License

MIT. 第三方 SDK（VRChat, NDMF, MA, AAO, lilToon…）保持其原许可证，本仓库不修改它们。
