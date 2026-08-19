# AvatarTextureOptimizer

> 世界最好的 VRChat 贴图优化工具 · The world's best VRChat avatar texture optimizer.

**AvatarTextureOptimizer (ATO)** is an open-source [NDMF](https://github.com/bdunderscore/ndmf) tool that optimizes
VRChat avatar textures at the **UV-island level**: it scales islands toward a target visual quality, packs them into
type-grouped atlases, and deduplicates materials/textures — all non-destructively, with maximum safety.

AvatarTextureOptimizer（ATO）是一个开源的 NDMF 工具，在 **UV 岛级别** 优化 VRChat Avatar 贴图：
将岛缩放到目标视觉质量、按类型组装箱成图集、并对材质/贴图去重——全程非破坏、最大化安全。

Package id: `net.fosa.avatar-texture-optimizer` · Requires Unity 2022.3 + VRCSDK 3.10 + NDMF 1.14.

---

## 1. Features / 特性

- **UV-island quality scaling** — 基于 MS-SSIM + ΔE(CIEDE2000) + alpha + 法线角度误差 + 灰度 RMSE 的目标质量算法，
  GPU 求值、二分搜索、各向异性细化。
- **Type-grouped atlas packing** — 按贴图类型组（有法线/有蒙版/动画切换、色彩空间、filterMode）分组装箱，
  最大化利用率；Burst 光栅位掩码 + BLF 装箱 + 90° 旋转 + pull-push 填充。
- **UV group invariant** — 同一 UV 对应的所有贴图在图集中位置一致，避免法线/主色混用出错。
- **Texture/material deduplication** — 按像素+导入设置去重贴图，按内容+参数去重材质，安全合并材质槽并更新动画引用。
- **Whitelist** — 白名单对象引用的贴图跳过所有优化。
- **Animation aware** — 扫描材质/贴图切换、渲染器启停、缩放、形态键、渲染模式/Cutoff 动画。
- **lilToon + standard shader analysis** — 自动分析属性表与关键字；无法兼容者白名单 + warning。
- **Compression & MipStreaming** — 按贴图分类提供压缩格式；Mipmap 与 MipStreaming 绑定（VRChat 要求）。
- **Platform override** — PC / Android / iOS 分别 override。
- **i18n** — JSON 配置文件，含英文与简体中文；代码注释双语。
- **AAO / MA compatible** — 在 MA 之后、AAO 之前运行；通过 UVUsageCompabilityAPI 兼容 AAO。

## 2. Installation / 安装

Add the package to your VCC project (`net.fosa.avatar-texture-optimizer`). Requires NDMF; Modular Avatar and
Avatar Optimizer are **optional** (supported but not required).

将包添加到 VCC 工程。依赖 NDMF；Modular Avatar 与 Avatar Optimizer 为可选（受支持但非必需）。

## 3. Usage / 使用

1. Add **Avatar Texture Optimizer** to the object holding your `VRCAvatarDescriptor` (only one per avatar).
2. (Optional) Add **Texture Whitelist** to any object and list objects whose textures must be left untouched.
3. Adjust settings (quality preset, atlas on/off, padding, compression, platform override).
4. Build/upload — ATO runs automatically during the NDMF build and prints a `[ATO]` report to the console.

## 4. Pipeline / 处理管线

处理发生在 MA 之后、AAO 之前（NDMF Optimizing 阶段）：
whitelist → collect & dedup textures → animation scan → shader analysis → island extraction →
UV groups → island quality scaling → atlas packing (BLF + pull-push) → rebuild mesh UV + assign atlases →
dedup materials/textures + merge slots (update animations) → compression/MipStreaming → report.

## 5. Quality algorithm / 目标质量算法

指标（统一 GPU 求值，唯一真相源）：彩色不透明 = MS-SSIM + ΔE00；彩色透明 = 预乘 alpha 下采样 + MS-SSIM + ΔE00 + alpha
（Cutout → clip 轮廓 IoU / Blend → 线性 RMSE）；法线 = 解码→重采样→重归一化→编码后角度误差 p95；灰度 = 使用通道线性 RMSE。
包围盒短边 <176px 回退单尺度 SSIM，<11px 忽略。缩放使用二分搜索（先均匀、后双轴各向异性细化）。

挡位默认值（高级选项可改）：

| 挡位 | MS-SSIM | ΔE00 p95 | alpha IoU | 法线 p95 | 灰度 RMSE |
|------|---------|----------|-----------|----------|-----------|
| 近无损 | 1.0 | 0 | 1.0 | 0° | 0 |
| 高质量（默认） | 0.995 | 1.0 | 0.95 | 1.5° | 2/255 |
| 均衡 | 0.990 | 2.0 | 0.90 | 3.0° | 4/255 |
| 性能 | 0.980 | 3.0 | 0.85 | 5.0° | 8/255 |
| 自定义 | 全 1（近无损） | | | | |

## 6. Architecture / 架构

```
Runtime/     AvatarTextureOptimizer (component + settings), TextureWhitelist
Editor/      NDMF plugin & processor, analysis, quality (GPU), packing (Burst), component UI, i18n
Burst/       island rasterization + BLF packing jobs
i18n/        en.json, zh-Hans.json
docs/        PLAN.md, source-reading notes
```

Assembly definitions: `AvatarTextureOptimizer.Runtime`, `AvatarTextureOptimizer.Burst`, `AvatarTextureOptimizer.Editor`.

## 7. Extensibility / 扩展

- **i18n**: drop a `lang.json` in `i18n/` with `{ "entries": [ { "key": "...", "value": "..." } ] }`.
- **Custom quality presets**: use the `Custom` preset and tune parameters.
- **Third-party developers**: the pipeline stages (`ATOTextureCategory`, `TextureEntry`, `UvIsland`, `AtlasResult`,
  `IslandScaler`, `AtlasPacker`) are public extension points; the NDMF pass ordering is
  `AfterPlugin("nadena.dev.modular-avatar").BeforePlugin("com.anatawa12.avatar-optimizer")`.

## 8. Logging / 日志

All logs are prefixed `[ATO]`, include per-stage timing, atlas sources, island counts, sizes, utilization, and
optimization delta. The build report is printed to the NDMF console (summary by default; details when `ATOLogger.Verbose`).

## 9. Known limitations / 已知限制

- No NDMF preview support (by design, 暂不支持 NDMF 预览).
- Normal-map decode supports DXT5nm / BC5 / unpacked-RGB encodings; exotic encodings fall back to whitelist.
- AAO's `MaxTextureSize` (Trace And Optimize) runs after ATO and may additionally clamp generated atlases.

## 10. License

MIT.
