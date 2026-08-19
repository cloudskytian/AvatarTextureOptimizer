# Avatar Texture Optimizer

面向 VRChat Avatar 的开源 NDMF 贴图优化工具。分析网格 UV，按感知质量缩小 UV 岛，再尽可能打成一张或多张图集。**只改网格 UV 和贴图引用，绝不改材质里的其它着色器参数。**

An open-source NDMF texture optimizer for VRChat avatars. It analyses mesh UVs, shrinks UV islands to a perceptual quality target, and packs them into one or more atlases. **Only mesh UVs and texture references are changed.**

包名 / package: `net.fosa.avatar-texture-optimizer`

## 写给第一次用的人 / For first-time users

1. 用 VCC / ALCOM 把本包和依赖装进工程：
   - `com.vrchat.avatars` ≥ 3.7
   - `nadena.dev.ndmf` ≥ 1.8
2. 选中 Avatar 根物体（上面已经有 `VRCAvatarDescriptor`）。
3. `Add Component` → **FOSA / Avatar Texture Optimizer**。一个 Avatar 只能挂一个。
4. 默认选项就是给小白准备的：质量挡位 **High**，生成图集，最小密度 2048 px/m，最大 4096 px/m。
5. 点 VRChat 构建，或 NDMF 的 Bake。进度条可以取消；取消后磁盘上的 `Assets/ATO_Generated/` 会留下，内存会释放。
6. 烘焙结束后看 NDMF 控制台：默认只有总览，展开 Details 能看到每个图集的来源、尺寸、利用率、耗时。

不想拼图集、只想缩小贴图：把 **Generate atlases / 生成图集** 关掉。

某张脸/某条裙子绝对不能动：把它的网格、材质、贴图或动画拖进 **Whitelist / 白名单**。

## 它会做什么 / What it does

处理发生在 **Modular Avatar 之后、AAO 之前**。

1. 跳过 `EditorOnly`，只看启用中或被动画启用的 `MeshRenderer` / `SkinnedMeshRenderer`。
2. 读着色器属性表和关键字（lilToon + 标准关键字 + 通用回退）。无法证明“用网格 UV 采样、且没有 ST/旋转/特殊用途”的贴图会进白名单并 warning。
3. 读动画：材质/贴图切换、物体开关、缩放、Cutoff、渲染模式。取最严苛的质量要求。
4. 按像素 + 导入设置对贴图去重。
5. 提取 UV 岛（多通道；形态键只取 0 和 100；动画缩放取最大；可整体平移的越界 UV 会归一，跨 wrap 缝的进白名单）。
6. 按 MS-SSIM + CIEDE2000 + alpha（Cutout=轮廓 IoU，Blend=线性 RMSE）+ 法线角度 / 灰度 RMSE 做二分缩放。先均匀，再双轴独立。质量=1 时原样拷贝。
7. 按类型组（法线/蒙版/色彩空间/filterMode）和 UV 组装箱。Burst 4px 位掩码 + 全扫描 BLF + 90° 旋转。
8. 图集名以 `ATO_` 开头。强制 Clamp、关闭 Read/Write。Mipmap 与 MipStreaming 绑定（VRChat 要求）。
9. 从成品 Avatar 上移除本组件，并在 NDMF 控制台写报告。

未安装 AAO / lilToon / Modular Avatar 也能编译和运行。装了 AAO 时，会通过 `UVUsageCompabilityAPI`（AAO 原文拼写）把 AAO 还要用的原始 UV 疏散到空闲通道。

## 质量挡位 / Quality presets

依据 CIEDE2000（ΔE00&lt;1 不可辨、1–2 近距可辨）和 MS-SSIM（0.99 视觉无损、0.97 优秀）。

| 挡位 | 行为 |
|---|---|
| Lossless | 不缩放 |
| Ultra | MS-SSIM 0.99 / ΔE 1.0 |
| **High（默认）** | MS-SSIM 0.97 / ΔE 2.0 |
| Medium | MS-SSIM 0.94 / ΔE 3.5 |
| Low | MS-SSIM 0.90 / ΔE 6.0 |
| Custom | 你自己改，不会被其它挡位覆盖；默认全 1（近无损） |

详细数字折在 **Advanced / 高级选项** 里。换挡位时这些数字会跟着变。

## 平台覆盖 / Platform override

和 Unity 自己的 platform override 一样，分 PC / Android / iOS。默认读当前构建平台。勾选对应平台后才会显示覆盖项。移动端图集最大边长 4096，PC 8192。

## 设计上故意做的安全决定 / Safety decisions

- 法线岛旋转 90° 时 **swizzle 切线空间 RG**，网格 tangent **绝不重算**。
- 存在透明度的图集不会给你不带 alpha 的压缩选项；即使用户选了也会 fallback 并 warning。
- 灰度图若实际用了多通道，不会按单通道格式保存。
- 实验性 NPOT 开启时自动剔除 PVRTC（iOS）。
- 动画切换的多张贴图共享同一套 layout，但生成多张图集，避免互斥内容画在同一像素上。
- 暂不支持 NDMF 预览。

## 给第三方开发者 / For third-party developers

扩展点在运行时程序集 `FOSA.AvatarTextureOptimizer`：

```csharp
ATOApi.RegisterShaderAnalyzer(new MyAnalyzer());
ATOApi.RegisterTextureClassifier(new MyClassifier());
ATOApi.RegisterQualityMetric(new MyMetric());
ATOApi.RegisterPacker(new MyPacker());
```

接口：

- `IATOShaderAnalyzer` — 告诉 ATO 某个材质属性是不是网格 UV、哪一路 UV、是不是特殊用途。
- `IATOTextureClassifier` — 把贴图分到 Opaque / Transparent / Normal / Gray。
- `IATOQualityMetric` — 额外质量否决票。
- `IATOPacker` — 替换内置 BLF；返回 false 则回退内置。

i18n：在 `Editor/Localization/` 丢一个 `xx-YY.json` 就会自动出现在语言列表。缺 key 回退英文。`Auto` 跟随 NDMF `LanguagePrefs.Language`。

日志一律以 `[ATO]` 开头。组件上的 **Verbose [ATO] logs** 可关。

AAO 兼容通过反射调用，asmdef **不引用** AAO，避免没装 AAO 时编译失败。

处理顺序（NDMF `BuildPhase.Optimizing`）：

```
After  nadena.dev.modular-avatar
After  net.rs64.tex-trans-tool     (弱约束，没装也没关系)
Before com.anatawa12.avatar-optimizer
```

## 工程结构 / Layout

```
Runtime/     组件、设置、扩展 API（玩家上传时会被剥掉，实现了 INDMFEditorOnly）
Editor/      NDMF 插件、分析、质量、图集、回写、检视器、i18n
Editor/Shaders/  Hidden/ATO/* 编辑器着色器
```

把本目录放到 `Packages/net.fosa.avatar-texture-optimizer` 或用 VPM 安装即可。这不是一个完整 Unity 工程。

## 许可 / License

MIT。见 `LICENSE`。
