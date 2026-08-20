# Avatar Texture Optimizer (ATO)

面向 VRChat Avatar 的开源 NDMF 贴图优化工具。分析网格 UV，按感知质量缩小 UV 岛，剔除未使用区域，并将岛装箱到一张或多张图集，同时保持法线/蒙版与主色的 UV 对齐。

**包名：** `net.fosa.avatar-texture-optimizer`  
**阶段：** 开发中（0.1.0）  
**预览：** 暂不支持 NDMF Preview

## 给小白

1. 用 VCC / VPM 安装本包，并确保已安装 VRChat Avatars SDK 与 NDMF。
2. 选中 Avatar 根物体（上面要有 `VRCAvatarDescriptor`）。
3. `Add Component` → `FOSA/Avatar Texture Optimizer`。
4. 保持默认「生成图集」与质量挡位 **High**，点击 VRChat / NDMF 的 Bake / Build。
5. 看 Console 里以 `[ATO]` 开头的报告：图集数量、利用率、警告。

**不要**在子物体上再挂第二个组件。  
白名单里放入不想动的网格、材质、贴图或动画即可。

## 会做什么 / 不会做什么

- 只改 **网格 UV** 和 **贴图引用**。
- **不改** 材质里除贴图外的任何着色器参数。
- 有 ST 缩放/偏移/旋转、当贴花、跨 wrap 缝、未知着色器用途 → 当白名单并警告。
- 处理发生在 Modular Avatar 之后、Avatar Optimizer 之前。

## 质量挡位

参考 MS-SSIM（Wang）与 CIEDE2000 可感差：

| 挡位 | MS-SSIM≥ | ΔE00≤ | 说明 |
| --- | --- | --- | --- |
| Ultra | 0.995 | 0.8 | 几乎看不出 |
| High（默认） | 0.985 | 1.5 | 推荐 |
| Medium | 0.97 | 2.5 | 更省 |
| Low | 0.94 | 4.0 | 激进 |
| Custom | 用户自定，默认全 1 | 不会被其它挡位覆盖 |

目标质量为近无损时跳过缩放（含纯色），原样拷贝。

## 平台

通用参数默认折叠；勾选 PC / Android / iOS override 后才显示对应块。移动端图集最大边默认 4096，PC 为 8192。

## 给第三方开发者

扩展钩子：`Net.Fosa.AvatarTextureOptimizer.AtoExtensionApi`

```csharp
AtoExtensionApi.AfterAnalyze += ctx => { /* 读 ctx.Groups */ };
```

程序集：

- `net.fosa.avatar-texture-optimizer`（Runtime）
- `net.fosa.avatar-texture-optimizer.editor`（Editor / NDMF Pass）

本地化：向 `Localization/` 添加 `xx.json` 即可出现语言。`language = Auto` 跟随系统/NDMF，缺词回退英文。

AAO：若安装了 Avatar Optimizer，会尝试调用 `UVUsageCompabilityAPI`（原文拼写）。未安装可正常工作。

## 依赖（请在 Unity 工程中解析，本仓库不含第三方源码）

- com.vrchat.base / com.vrchat.avatars 3.10.4+
- nadena.dev.ndmf 1.14.4+
- 可选：Modular Avatar、Avatar Optimizer、lilToon
- com.unity.burst / mathematics / collections

## 日志

`[ATO]` 前缀。组件上 `verboseLogs` 打开逐步耗时、岛数量、图集来源与利用率。

## 许可

MIT
