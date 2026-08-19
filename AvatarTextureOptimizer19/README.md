# Avatar Texture Optimizer (ATO)

面向 VRChat Avatar 的开源 [NDMF](https://github.com/bdunderscore/ndmf) 贴图优化工具。

分析 Avatar 网格 UV，在**完全无视材质其它着色器参数**的前提下，按目标质量缩小 UV 岛、剔除未使用区域，并把碎片重组成一张或多张图集。目标是：看起来几乎一样，贴图占用尽量小。

> 开发阶段：配置字段随时可能改，没有迁移负担。

## 你需要什么

- Unity 2022.3
- [NDMF](https://github.com/bdunderscore/ndmf) ≥ 1.8
- VRChat Avatars SDK ≥ 3.7
- 可选：[Modular Avatar](https://github.com/bdunderscore/modular-avatar)、[AAO](https://github.com/anatawa12/AvatarOptimizer)、lilToon

用 VCC / VPM 安装本包，或把整个文件夹放进 `Packages/` / `Assets/`。

包名：`net.fosa.avatar-texture-optimizer`

## 小白怎么用（推荐）

1. 选中带 **VRCAvatarDescriptor** 的 Avatar 根物体。
2. `Add Component` → **FOSA / Avatar Texture Optimizer**。
3. 整个 Avatar（含子物体）只能挂 **一个**。
4. 质量挡位保持 **High**，其它不用动。
5. 上传或 NDMF 手动烘焙。完成后 NDMF 控制台会给出总览报告。

白名单：把不想动的网格 / 材质 / 贴图 / 动画拖进去。它们引用到的全部贴图会跳过优化。

## 它会做什么 / 不会做什么

会做：

- 按像素 + 导入设置去重贴图
- 只处理「网格 UV 采样、没有 ST 变换、不是贴花/MatCap」的 Texture2D
- 质量驱动的 UV 岛缩放（或关闭图集时的整图缩放）
- 类型组图集（法线 / 蒙版不会因为 1/10 有法线就浪费 9/10）
- UV 组保证同一套 UV 在不同图集上的相对位置一致
- 只改网格 UV 和贴图引用，**绝不改材质其它参数**
- 烘焙后从成品上移除本组件

不会做：

- NDMF Preview（暂不支持）
- 修改 lilToon / Standard 的颜色、描边宽度等非贴图参数
- 猜测看不懂的着色器（会当白名单并 Warning）

## 质量挡位

依据 MS-SSIM（Wang）、CIEDE2000（Sharma/Wu/Dalal，约 1.0 为刚可辨差）以及业内常用法线角度误差。

| 挡位 | 含义 |
|---|---|
| NearLossless | 目标质量 = 1，跳过 UV 缩放，原样拷贝 |
| Ultra | 几乎看不出 |
| High | 默认。细节很好，体积明显下降 |
| Medium | 更激进 |
| Low | 最小体积 |
| Custom | 参数完全由你改，不会被其它挡位覆盖；默认全是近无损 |

高级折叠里可以改 MS-SSIM、ΔE00、alpha RMSE、Cutout IoU、法线 p95 角度、灰度 RMSE。

像素密度默认 2048–4096 px/m，防止过糊或浪费。

## 执行时机

MA（含 late-transform）和 TexTransTool **之后**，AAO **之前**（NDMF `Optimizing` 阶段）。

若安装了 AAO，会走 `UVUsageCompabilityAPI`（AAO 原文拼写）把 AAO 还要用的原 UV 疏散到空闲通道。

## 平台

PC / Android / iOS。默认用当前构建目标。勾选对应平台 override 才会出现覆盖项。

图集强制 **Clamp**、关闭 **Read/Write**。开启 Mipmap 时强制 MipStreaming（VRChat 要求，二者绑定成一个开关）。

图集名称以 `ATO_` 开头，写在 `Assets/ATO_Generated/<avatar>/`。取消烘焙会释放内存，但保留磁盘临时文件。

## 日志

所有日志以 `[ATO]` 开头。组件上的 Verbose Logging 打开后会打印每一步耗时、图集来源、岛数量、尺寸、利用率、相对原图优化量。NDMF 控制台默认只显示总览。

## 第三方开发者

命名空间：`Net.Fosa.AvatarTextureOptimizer.API`

```csharp
using Net.Fosa.AvatarTextureOptimizer.API;
using UnityEditor;

[InitializeOnLoad]
static class MyAtoAddon
{
    static MyAtoAddon()
    {
        ATOExtensionRegistry.Register(new MyShaderAnalyzer());
        ATOExtensionRegistry.Register(new MyIslandProcessor());
        ATOExtensionRegistry.Register(new MyHook());
    }
}

sealed class MyShaderAnalyzer : IATOShaderAnalyzer
{
    public string Id => "me.myshader";
    public int Priority => 50; // lilToon built-in = 100, standard = 10
    public bool CanAnalyze(UnityEngine.Material m) => m.shader != null && m.shader.name.StartsWith("My/");
    public System.Collections.Generic.IReadOnlyList<ATOTextureSlotInfo> Analyze(UnityEngine.Material m)
    {
        // Return mesh-sampled Texture2D slots. Set IsSpecialPurpose / HasTransform to skip unsafely.
        return System.Array.Empty<ATOTextureSlotInfo>();
    }
}
```

- `IATOShaderAnalyzer`：分析未知着色器。分析失败请设 `Warning`，ATO 会当白名单。
- `IATOIslandProcessor`：装箱前改岛。
- `IATOPipelineHook`：观察 `start` / `scanned` / `done`。

i18n：在 `Editor/Localization/` 再丢一个 `xx-yy.json`（扁平 `"key":"value"`）就会自动出现在语言列表。`Auto` 跟随 NDMF 当前语言，缺翻译回退英文。

## 设计上请注意的几点

1. **法线 90° 装箱**：网格切线不重算；旋转岛时会同步旋转法线贴图的切线空间 XY，避免光照错误。
2. **生成贴图必须独立导入**：MipStreaming / 平台压缩 / Crunch 不能做在 NDMF sub-asset 上，所以图集会写成 PNG 再走 TextureImporter。
3. **看不懂就不改**：ST 动画、跨 wrap 缝、MatCap、贴花、无法分析的关键字，一律跳过并 Warning。

## 许可证

MIT
