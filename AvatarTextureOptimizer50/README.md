# Avatar Texture Optimizer (ATO)

**全世界最好的 VRChat Avatar 贴图优化工具 / The best texture optimizer for VRChat avatars**

[中文说明](#中文说明) | [English](#english)

---

## 中文说明

`Avatar TextureOptimizer`（ATO）是一个开源的 [NDMF](https://github.com/bdunderscore/ndmf) 插件（包名 `net.fosa.avatar-texture-optimizer`），在**进入游玩/上传构建**时对你的 Avatar 做非破坏性的贴图优化：

### 核心能力

- **网格 UV → 贴图映射**：建立每个网格 UV 通道到贴图的映射关系。即使同一张贴图被多个不同材质引用（或被动画切换的材质引用），映射关系自动复用——完全无视材质的其他参数。
- **UV 岛级质量缩放**：以感知质量为目标（MS-SSIM + CIEDE2000 色差 + alpha IoU/RMSE + 法线角度误差 + 灰度逐通道 RMSE），对每个 UV 岛二分搜索最小可用尺寸；支持各向异性双轴细化；提供像素密度上下限（默认 2048–4096 px/m）防止发糊或浪费；纯色岛直接缩到最小；目标质量为 1（近无损）时原样拷贝。
- **类型组图集装箱**：按“是否存在对应法线/蒙版等特殊贴图 + 色彩空间 + filterMode”分组，同组共用图集布局；对应层（法线/蒙版/动画换装贴图）与主色图集**共享同一归一化布局**（位置一致，分辨率可独立缩小）；装箱使用 4px 粒度光栅位掩码 + 全扫描 BLF + 90° 旋转 + 候选图集池（POT 默认，NPOT 实验性）。
- **动画安全**：完整扫描所有 VRC 动画层的材质切换、材质属性动画（ST/UV 滚动判定、cutoff 集合、渲染模式从严）、物体启停、缩放、形态键（0/100 两态最大面积）；换装贴图生成同布局变体图集层。
- **去重**：优化前按“内容+导入设置”去重贴图；优化后对内容参数完全一致的材质/贴图/图集去重，并在安全（不透明、无单槽切换动画）时合并材质槽并改写动画槽位索引。
- **AAO 兼容**：处理发生在 Modular Avatar 之后、Avatar Optimizer 之前；若 AAO 声明占用某 UV 通道，原始 UV 会自动搬移到空闲通道并经 `UVUsageCompabilityAPI` 登记（未安装 AAO 时自动退化，不影响使用）。
- **白名单**：任何类型对象（网格/材质/贴图/动画/物体/任意组件）引用的贴图全部跳过所有优化。
- **平台**：PC / Android / iOS 独立 override（压缩格式、图集尺寸上限），格式枚举安全化并在构建时兜底。
- **i18n**：读取 `Localization/*.json` 自动发现语言，默认跟随 NDMF 语言，缺失回退英文；内置 en-US 与 zh-CN，用户可自行添加语言文件。
- **报告**：构建完成后在 NDMF 控制台输出报告（总览+折叠明细），含每阶段耗时、图集来源/大小/利用率、优化量、警告；日志以 `[ATO]` 前缀输出并带级别开关。

### 使用方法

1. 通过 VPM（VRChat Creator Companion / VCC）导入本包及其依赖（ndmf ≥ 1.14.4、VRC SDK 3.x）。
2. 在 **Avatar 根对象**（挂有 `VRCAvatarDescriptor` 的那个）上添加组件 `Avatar Texture Optimizer → ATO Avatar (VRChat)`。每个 Avatar 只允许一个，必须挂在根上，否则构建会报错中止。
3. 按需调整设置（默认值即推荐值）：
   - **质量挡位**：近无损 / 高（默认）/ 中 / 激进 / 自定义（切换挡位会同步具体参数；自定义挡位参数永不被覆盖，默认全部为 1 = 近无损）。
   - **高级质量**：每个指标阈值、像素密度上下限。
   - **高级图集**：最小岛间距（默认 4px）、实验性 NPOT、各类贴图的流式 Mip（与 Mipmap 绑定为单开关）。
   - **平台覆盖**：勾选对应平台后显示该平台的格式与图集尺寸设置。
4. 进入游玩或上传——进入构建时自动执行，完成后 NDMF 控制台显示报告；**不会修改你的原始资产**（全部在构建副本上进行）。

### 安全性设计

- 只改网格 UV 与材质的**贴图引用**，绝不修改材质其他着色器参数。
- 无法理解/存在 UV 变换（ST/滚动/旋转/视差/MatCap/贴花用途等）的贴图一律白名单跳过并警告。
- 跨 wrap 缝依赖 repeat 采样的岛 → 白名单跳过并警告。
- 图集 Read/Write 强制关闭、Wrap 强制 Clamp、Mip↔MipStreaming 绑定。
- 法线图集旋转 90° 时自动做 RG 通道旋转补偿（切线绝不重算）。
- 任何压缩格式不安全时自动兜底并在控制台警告（例如含 alpha 的贴图不会给无 alpha 格式；多通道灰度贴图拒绝单通道格式；PVRTC 仅在 POT 图集可用）。

### 第三方开发者扩展

- **贴图过滤器**：在运行时程序集 `net.fosa.ato` 中 `ATOExtensionHost.RegisterTextureFilter(candidate => candidate.Skip("reason"))` 可把任意贴图踢进白名单；`IATOTextureCandidate` 暴露贴图、引用材质与跳过原因。
- **i18n 扩展**：`ATOLocalization.RegisterFolder(path)` 注册额外的 JSON 语言目录。
- **AAO 集成**：`net.fosa.avatar-texture-optimizer.aao-bridge` 程序集仅在安装 AAO ≥ 1.8.0 时编译（versionDefines），通过 `AAOBridgeHooks` 挂接 `UVUsageCompabilityAPI`。
- 内部类型对测试与桥接程序集开放（`InternalsVisibleTo`）。

### 已知限制（v0.1）

- 尚不支持 NDMF 预览（按设计暂缓）。
- counterpart 图集层的缩小探测使用限次采样（1/.75/.5/.25）而非全量二分（速度优先，后续版本可换）。
- 法线岛 90° 旋转补偿方向以 UV 转置为准，极端自定义管线下需目视验证。
- 本版本未经 Unity 实机全量验证，欢迎在真实 Avatar 上测试并反馈（日志以 `[ATO]` 开头，默认 Info 级别，可开 Debug/Trace 排查）。

---

## English

**Avatar Texture Optimizer (ATO)** is an open-source [NDMF](https://github.com/bdunderscore/ndmf) plugin (`net.fosa.avatar-texture-optimizer`) that optimizes VRChat avatar textures non-destructively at enter-play/upload time.

### Highlights

- **Mesh-UV → texture mapping** that ignores all non-texture material parameters and reuses mappings across materials/animations sharing the same texture.
- **Per-UV-island quality scaling** driven by perceptual metrics (MS-SSIM + CIEDE2000 + alpha IoU/RMSE + normal angular error + per-channel gray RMSE), uniform binary search + per-axis refinement, pixel-density clamps, pure-color short-circuit, lossless copy at quality 1.
- **Type-grouped atlas packing**: bitmask raster BLF with 90° rotation, POT/NPOT candidate pools; counterpart layers (normals/masks/animated swaps) share the exact same normalized layout and may shrink independently.
- **Animation-aware**: material swaps become same-layout variant atlases; ST/scroll/cutoff/render-mode animations are honored strictly; blendshape & scale animation areas considered.
- **Dedup & slot merging** with animation index rewriting (opaque-only slot merges).
- **Runs between Modular Avatar and Avatar Optimizer**, with `UVUsageCompabilityAPI` evacuation when AAO claims a UV channel (degrades gracefully when AAO is absent).
- **Whitelist** any object type; **platform overrides** (PC/Android/iOS) with safe format enums & build-time fallbacks; **extensible JSON i18n**; **NDMF console report** with stage timings.

### Usage

Add exactly one `ATO Avatar (VRChat)` component to the avatar root (the object with `VRCAvatarDescriptor`). Tune the quality preset if desired (defaults are recommended). Build/play — nothing on disk is modified.

### For developers

Subscribe texture filters via `ATOExtensionHost.RegisterTextureFilter` (runtime assembly), register extra i18n folders via `ATOLocalization.RegisterFolder`, or hook AAO through `AAOBridgeHooks`. Internals are visible to the bridge & test assemblies.

### License & attribution

Open source; uses NDMF, VRC SDK, and interoperates with Modular Avatar, Avatar Optimizer and lilToon (all © their respective authors, unmodified).

> ⚠️ v0.1 has not been fully validated inside Unity yet — please test on real avatars and report issues with `[ATO]` logs.
