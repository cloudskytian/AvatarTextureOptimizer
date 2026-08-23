# AvatarTextureOptimizer

[简体中文](#简体中文) · [English](#english)

## 简体中文

AvatarTextureOptimizer（ATO）是一个面向 VRChat Avatar 的开源 NDMF 贴图与 UV 优化器。它在 **Modular Avatar 之后、Avatar Optimizer 之前**运行，以“无法证明安全就不转换”为原则，分析静态与动画可达的 Renderer、材质、Texture2D 和多通道 UV，并生成质量受控的整图缩放或形状图集。

### 安装要求

- Unity 2022.3
- VRChat SDK Base / Avatars 3.10.4
- NDMF 1.14.4
- Unity Burst 1.8.7、Collections 2.1.4、Mathematics 1.2.6
- Modular Avatar 与 AAO 均为可选；安装时 ATO 会按 NDMF 名称正确排序。AAO 未安装时不会产生硬依赖。

将本目录作为 UPM/VPM 包放入项目，或复制到项目的 `Packages/net.fosa.avatar-texture-optimizer`。这不是完整 Unity 工程。

### 新手使用方法

1. 在 Avatar 根对象（存在 `VRCAvatarDescriptor` 的对象）添加 **Avatar Texture Optimizer**。
2. 一个 Avatar 及子级只能存在一个 ATO 组件。
3. 默认保持“生成图集”开启，并使用 **Balanced** 挡位。
4. 不理解高级选项时保持默认值即可。构建时 ATO 会显示阶段、进度和取消按钮。
5. 构建后在 NDMF Console 查看总体报告；展开详细信息可查看耗时、来源贴图、岛数量、图集尺寸、利用率、估算体积变化和 fallback 原因。

### 安全模型

ATO 仅修改：

- 克隆材质上的 Texture 引用；
- 克隆 Mesh 的 UV；
- 必要的材质槽、Mesh、动画对象引用。

ATO **绝不修改材质中的非贴图 Shader 参数**。以下情况会保护贴图或使相关连通 UV 组 fallback：

- Texture ST、滚动、旋转或 UV 模式不是可证明安全的恒等映射，或这些属性被动画修改；
- 非 Texture2D、HDR、贴花、MatCap、LUT、屏幕空间、反射、视差等特殊用途；
- 未知 Shader 属性语义；
- MaterialPropertyBlock；
- UV 跨越 wrap 缝，或越界布局不能用整数 tile 平移到 `[0,1]`；
- Mesh 不可读、非三角形拓扑、无法为 AAO 保存原始 UV；
- 贴图/材质还被非 Renderer 组件直接引用；
- 目标质量为 1：为保证“不重采样原样拷贝”，自动走无图集整图路径。

白名单接受任意 Unity Object。对象引用闭包中的全部 Texture2D 跳过所有优化；同一连通 UV/材质/贴图组不会图集化，其他安全贴图仍可做整图处理。

### 质量算法

所有质量判断均在最终压缩之前执行：

- GPU RenderTexture 在线性空间裁剪、重采样和双线性回放；
- 透明色先预乘 Alpha 再下采样；
- 不透明/透明颜色：局部 11×11 高斯窗口 SSIM；短边 ≥176 px 使用五尺度 MS‑SSIM，短边 <176 px 使用单尺度 SSIM，短边 <11 px 忽略结构项；
- 颜色：CIEDE2000；
- Cutout：对每个材质与动画可达 Cutoff 分别计算轮廓 IoU，并取最严格结果；
- Blend：线性 Alpha RMSE；
- 法线：正确解码、重采样、重归一化、编码，再计算平均角度误差与 p95；
- 灰度/蒙版：仅比较已用通道，逐通道线性 RMSE 取最差；
- Burst 并行计算像素指标；大图按条带回读，避免一次性分配整张 8K 浮点图；
- 先均匀二分到全部指标通过，再独立细化 X/Y，缓解各向异性浪费；
- UV 组采用木桶效应，任何关联贴图不通过都会提高共同尺寸；尺寸不会超过源岛真实像素；
- 非 1 质量下，所有关联贴图都为纯色的岛直接缩到最多 4×4；
- 像素密度默认 2048–4096 px/m，可选 512、1024、2048、4096、8192 px/m；形态键逐个比较 0/100，动画缩放使用保守最大面积。

质量挡位采用以下归一化阈值：

| 挡位 | 结构 | 色彩 | Alpha | 法线 | 灰度 |
|---|---:|---:|---:|---:|---:|
| Performance | .940 | .850 | .960 | .850 | .940 |
| Balanced（默认） | .970 | .950 | .980 | .950 | .970 |
| High | .985 | .980 | .992 | .980 | .985 |
| NearLossless | 1 | 1 | 1 | 1 | 1 |
| Custom 初始值 | 1 | 1 | 1 | 1 | 1 |

归一化色彩值映射为 `ΔE00 最大值=(1-value)×10`；Alpha Blend/灰度映射为线性 RMSE；法线映射为平均角度与 p95 上限。阈值参考：Wang 等人的 SSIM（2004）和 MS‑SSIM（2003）、Sharma 等人的 CIEDE2000 实现说明（2005），并采用图像工程中常见的约 0.95–0.99 结构相似度与 ΔE00 约 1–3 的可见性区间。默认 Balanced 优先保证 Avatar 外观，Performance 才明显偏向体积。

### 图集与装箱

- UV 岛按共享边连通，并合并同贴图中的几何重叠岛；
- 同一 UV 对应的全部颜色、法线、蒙版和动画变体构成 UV 组，所有图集共享尺寸、旋转与归一化落点；
- 类型组考虑特殊贴图存在集合、色彩空间、FilterMode 和已用通道；
- 4 px 粒度 Burst 光栅化位掩码；按面积、边长排序；全扫描 Bottom‑Left‑Fill；支持 90° 位掩码转置；不重算法线切线；
- 以共享贴图/材质连通分量为原子，保证同一源贴图的全部岛进入同一图集；装不进最大图集的原子整体 fallback；
- 默认候选边长为 64 到平台上限的 2 次幂；实验性 NPOT 使用 64 px 步进并剔除不兼容格式（例如 PVRTC）；
- Padding 为 `max(最小挡位, ceil(图集最大边/128))`，再按 4 px 光栅向上对齐；
- GPU Jump Flood 将最近岛边缘颜色无限外扩到空白区；透明图集空白 Alpha 始终为 0；
- 图集强制 Clamp、关闭 Read/Write；过滤与各向异性取来源中最高质量设置；名称均以 `ATO_` 开头。

### 平台与压缩

平台为 PC、Android、iOS；Auto 读取当前 Unity BuildTarget。通用设置默认折叠，每个平台勾选 override 后显示完整配置。格式下拉框按语义、Alpha、平台和 NPOT 条件过滤；构建阶段再次验证，非法组合自动回退并报警。

支持 BC1/3/5/7、ASTC 4×4/6×6/8×8、ETC2、PVRTC（仅安全 POT iOS 情况）、DXT/ETC Crunch 和 RGBA32。Mipmap 与 MipStreaming 只有一个绑定开关：开启 Mipmap 就开启 MipStreaming，关闭则同时关闭。

### AAO 兼容

ATO 对 AAO 1.8+ 的公开 `UVUsageCompabilityAPI` 使用可选反射，不要求安装 AAO。若 AAO 使用将被修改的 UV 通道，ATO 会复制原 UV 到空闲通道并注册 evacuation；没有空闲通道则安全 fallback。接口拼写 `Compability` 与 AAO 原文一致。

### 第三方扩展

Editor 程序集公开：

- `IAtoTexturePropertyAnalyzer`：添加 Shader/属性 UV 语义；
- `IAtoIslandQualityConstraint`：否决不满足自定义指标的候选尺寸；
- `IAtoBuildStageExtension`：观察分析、图集、完成阶段；
- `IAtoGeneratedTexturePostprocessor`：压缩前处理生成贴图；
- `AtoExtensionRegistry.Register(...)`：注册并返回可释放句柄。

```csharp
using Fosa.AvatarTextureOptimizer.Editor.API;

IDisposable registration = AtoExtensionRegistry.Register(new MyShaderAnalyzer());
```

扩展应保守处理异常，不得在回调结束后持有克隆 Avatar 对象。

### 本地化扩展

在任意 `Assets` 或 `Packages` 路径加入名称含 `ATO_i18n` 的 JSON TextAsset：

```json
{
  "locale": "ja-JP",
  "displayName": "日本語",
  "entries": [{"key": "component.title", "value": "..."}]
}
```

有几个有效 JSON 就显示几个语言。Auto 跟随 NDMF 当前语言；缺少键或语言时回退英文。包内提供英文与简体中文。

### Unity 验证

本包带 `Tests/Editor` EditMode 测试。导入完整项目后请执行：

1. Test Runner → EditMode → `net.fosa.avatar-texture-optimizer.tests`；
2. NDMF 手动 Bake，检查 ATO 位于 MA 后、AAO 前；
3. 对 PC、Android、iOS 分别构建测试 Avatar；
4. 覆盖静态/动画材质切换、贴图动画、Cutout/Blend、多 UV、越界 UV、重叠岛、形态键、对象缩放、共享材质、无 AAO/有 AAO；
5. 对比烘焙前后截图、法线方向、动画与材质槽索引，并检查 NDMF 报告。

当前不支持 NDMF Preview，这是设计约束而不是故障。

---

## English

AvatarTextureOptimizer is an open-source, safety-first NDMF texture/UV optimizer for VRChat avatars. Add one component to the avatar root, keep the default Balanced profile, and inspect the folded NDMF report after baking. It runs after Modular Avatar and before AAO, modifies only cloned texture references and mesh UVs, and falls back whenever shader, animation, UV, component-reference, or platform behavior cannot be proven safe.

Key features include animation-aware material/texture analysis, arbitrary-object whitelist closure, decoded-pixel/import-setting deduplication, local SSIM/MS‑SSIM + CIEDE2000 + alpha/normal/mask metrics, Burst/GPU processing, anisotropic binary search, multi-channel UV groups, shape-mask BLF packing, POT/NPOT candidate pools, jump-flood edge filling, platform overrides, safe compression, optional AAO UV evacuation, JSON i18n, detailed `[ATO]` telemetry, cancellation, and public extension interfaces.

See the Chinese sections above for complete behavior, algorithms, safety rules, and Unity validation steps.

## License

MIT. Third-party packages are not redistributed by this repository; see `ThirdPartyNotices.md`.
