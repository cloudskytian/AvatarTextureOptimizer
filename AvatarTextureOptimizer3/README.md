# Avatar Texture Optimizer (ATO)

适用于 VRChat Avatar 的开源 **NDMF** 工具：分析网格 UV 与贴图引用，按目标质量缩小 UV 岛，剔除未使用区域，并把岛重新装箱成一张或多张图集。只改 **网格 UV** 和 **贴图引用**，不改材质上其它着色器参数。

Package: `net.fosa.avatar-texture-optimizer`

## 给完全不了解 NDMF 的用户

1. 用 VCC / ALCOM 安装本包，以及 NDMF、VRChat Avatars SDK。
2. 选中带 `VRCAvatarDescriptor` 的 Avatar 根物体。
3. `Add Component` → `FOSA / Avatar Texture Optimizer`。
4. **同一 Avatar 只能挂一个**。
5. 保持默认即可。上传或 NDMF 手动烘焙时会自动处理。
6. 控制台与 NDMF 报告里会看到以 `[ATO]` 开头的摘要。

默认：**生成图集**、质量挡位 **High**、最小 padding **4px**、像素密度 **2048–4096 px/m**。

关掉「生成图集」时：不重排 UV、不剔岛，只按质量缩放整张贴图。

## 质量挡位（依据）

| 挡位 | MS-SSIM≥ | ΔE00≤ | 法线均角≤ | 说明 |
| --- | --- | --- | --- | --- |
| Lossless / Custom 默认 | 1 | 0 | 0 | 跳过缩放，原样拷贝 |
| Ultra | 0.995 | 1 | 3° | 接近 JND |
| **High（默认）** | 0.985 | 2 | 6° | 游戏烘焙常用“高” |
| Medium | 0.97 | 3 | 10° | 可见轻微差 |
| Low | 0.94 | 5 | 15° | 更狠的体积 |

切换挡位会改写阈值；**Custom** 不会被其它挡位覆盖。

算法（实现于 `AtoQuality.cs`）：

- 线性空间盒式下采样；透明预乘 alpha。
- 不透明 / 主色：MS-SSIM（短边 &lt;176 回退单尺度 SSIM；&lt;11 忽略）+ CIEDE2000。
- Cutout：clip 后轮廓 IoU；Blend：alpha 线性 RMSE。多材质引用取最严。
- 法线：解码 → 重采样 → 重归一化 → 平均角 + p95。
- 灰度：线性 RMSE，逐通道取最差。
- 缩小后再双线性放大回原岛包围盒比较。
- 先均匀二分，再双轴独立细化（各向异性）。
- 目标质量为 1：跳过缩放（含纯色）。否则纯色岛缩到 `min(4, 短边)`。

评估当前为 **CPU**（可并行扩展 Burst）。不含最终 GPU 压缩格式损失。

## 图集与类型组

- 按色彩空间、FilterMode、是否有法线/蒙版划分 **类型组**。
- 同一 UV 上的所有贴图构成 **UV 组**，保证各图集上的岛位置一致。
- 装箱：岛 **三角形** 4px 位掩码 + 全扫描 BLF + 面积/边长降序 + 90° 转置。法线切线 **不重算**。
- 装箱原子是「贴图 + 其所属全部 UV 组」。同一 UV 的主色/法线/蒙版图层共用岛坐标。
- 同一张源贴图的全部岛必须在同一图集。
- padding = `max(最小挡位, ceil(边长/128), 4)`。
- 空白区域 GPU/CPU pull-push 渗色（透明 alpha 保持 0）。
- 名称以 `ATO_` 开头。数量不限制。
- 图集强制 **Clamp**，默认关 Read/Write。
- 开启 Mipmap 时绑定 MipStreaming（VRChat 要求）。

## 安全与跳过

以下情况视为白名单并 warning：

- 白名单对象引用到的全部贴图
- 材质或动画存在 ST / ScrollRotate 等变换
- UV 越界且跨 wrap 缝（可整体平移回 [0,1] 的会先归一化）
- 无法分析的着色器
- 单贴图 UV 组无法装入最大图集

只处理启用或被动画启用的 `MeshRenderer` / `SkinnedMeshRenderer` 上的 `Texture2D`。

形态键只取 **0 与 100** 的面积最大值。动画缩放按最大缩放面积。

## 执行顺序

`BuildPhase.Optimizing`，`AfterPlugin(nadena.dev.modular-avatar)`，`BeforePlugin(com.anatawa12.avatar-optimizer)`。

可选兼容 AAO `UVUsageCompabilityAPI`（原文拼写如此）。未安装 AAO 时反射失败即跳过。

烘焙后从成品上移除本组件。暂不支持 NDMF 预览。

进度条可取消：停止处理并释放内存，磁盘临时资产保留。

## 第三方开发

```csharp
[InitializeOnLoad]
static class MyAtoAddon {
    static MyAtoAddon() {
        net.fosa.ato.editor.AtoHooks.ClassifyProperty += (mat, prop) => {
            if (prop == "_MySpecialTex") return net.fosa.ato.AtoTextureClass.Mask;
            return null;
        };
        net.fosa.ato.editor.AtoHooks.BeforePack += ctx => { /* ... */ };
    }
}
```

本地化：向 `Editor/i18n/` 添加 `xx-YY.json` 即可出现语言。默认 Auto 跟随 NDMF。

## 依赖（只读使用，未修改）

- NDMF 1.14.4 API：`Plugin<T>` / `Pass<T>` / `BuildPhase` / `ErrorReport` / `IAssetSaver` / `Localizer`
- AAO 1.9.17：`UVUsageCompabilityAPI`
- Modular Avatar：仅排序约束 `nadena.dev.modular-avatar`
- lilToon 2.3.4：属性名来自 `lilMaterialProperties.cs`（`_MainTex` `_BumpMap` `_AlphaMask` 等）
- VRChat Avatars：`VRCAvatarDescriptor`（asmdef `versionDefines`）

请将本文件夹放到 Unity 工程的 `Packages/` 或 `Assets/` 后烘焙验证。
