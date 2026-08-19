# Avatar Texture Optimizer (ATO)

NDMF 工具：分析 VRChat Avatar 网格 UV，按感知质量缩小 Texture2D 的 UV 岛，并在安全的前提下把它们重打包成一张或多张图集，尽量在**观感不变**的同时提高贴图利用率。

Package: `net.fosa.avatar-texture-optimizer`  
组件菜单: **FOSA / Avatar Texture Optimizer**

> 开发中。把本文件夹放到 Unity 工程的 `Packages/net.fosa.avatar-texture-optimizer`，或用 VPM 安装。这不是完整 Unity 工程。

## 你需要什么

- Unity 2022.3
- [NDMF](https://github.com/bdunderscore/ndmf) ≥ 1.8
- VRChat Avatars SDK ≥ 3.7
- （可选）Modular Avatar、Avatar Optimizer、lilToon

ATO 跑在 **Modular Avatar 之后、Avatar Optimizer 之前**。

## 小白怎么用

1. 选中 Avatar 根物体（上面要有 `VRCAvatarDescriptor`）。
2. `Add Component` → `FOSA / Avatar Texture Optimizer`。
3. **一个 Avatar 及其子级只能挂一个**。挂错地方会在烘焙时直接报错停掉。
4. 默认即可：生成图集、质量挡位 **High**、最小密度 2048 px/m、最大 4096 px/m。
5. 有不想动的衣服/特效/贴花，把网格、材质、贴图或动画拖进 **Whitelist**。
6. 上传或 NDMF 手动烘焙。完成后看 NDMF 控制台里的 ATO 报告。

烘焙后组件会从成品上消失（它也实现了 `IEditorOnly`）。

### 常见开关

| 选项 | 默认 | 含义 |
| --- | --- | --- |
| Generate atlas | 开 | 开：裁未使用 UV、重排、打图集。关：只整图缩放 + 改导入参数 |
| Quality preset | High | Lossless 完全不缩 UV。Custom 的数字只属于你，不会被其他挡位改掉 |
| Pixel density | 2048–4096 | 按模型和岛的真实面积限制像素，避免又糊又浪费 |
| Deduplicate | 开 | 内容和参数都一样的材质/贴图会合并。不透明且动画不会单独切的槽也会合并 |
| Platform | Auto | 跟随当前构建目标。要分 PC/Android/iOS 就勾对应 Override |
| Mipmap + MipStreaming | 开 | VRChat 要求这两个绑在一起，所以只有一个开关 |
| Verbose logs | 关 | 高级用户看逐步 `[ATO]` 日志 |

高级质量数字（MS-SSIM、ΔE、法线角度…）默认折起来。不懂就不要改，换挡位即可。

## ATO 会动什么、不会动什么

**会：**

- 克隆并改网格 UV
- 克隆材质，只改上面的 **Texture2D 引用**
- 改动画里指向材质/贴图的对象曲线
- 生成 `ATO_` 开头的图集
- 去重相同材质/贴图

**不会：**

- 改着色器的颜色、Cutoff 以外的数值、Keyword（Cutoff 只用于评估，不写回）
- 动白名单对象引用到的贴图
- 处理有 tiling/offset/旋转/滚动（含动画）的贴图
- 处理 MatCap、贴花、屏幕 UV、Cube、非 Texture2D
- 处理跨 wrap 缝、依赖 Repeat 的 UV（会警告并当白名单）
- 提供 NDMF 预览

任一条件不满足，这张贴图按白名单跳过，并在 NDMF 控制台打 warning。

## 质量挡位从哪来

- **MS-SSIM**：Wang 等，0.99 接近无感，0.97 作默认
- **CIEDE2000**：约 1 为恰可辨差异，默认允许 2
- **法线角度**：游戏里常见 2–10°
- **Lossless / Custom=全 1**：不缩放 UV 岛（包括纯色）
- 非无损时，纯色岛直接缩到 `min(4, 原短边)`

比较在**线性空间**做，透明图预乘 Alpha 再下采样。缩小后的岛会双线性放大回原尺寸再和原图比。**不含最终压缩格式的损失。**

同 UV 的多张贴图按木桶效应取最大所需尺寸。先均匀缩到全部达标，再按 U/V 独立二分，避免各向异性浪费或发糊。

## 图集

- 按「类型组」分：法线/蒙版是否存在、色彩空间、FilterMode。一张贴图既被有法线的材质用、又被没有的材质用，归到有法线那组
- 同一 UV 对应的所有贴图构成 UV 组，它们在不同图集上的位置相同
- 装箱是 **4px 粒度的形状位掩码 + 全扫描 BLF**，不是矩形装箱
- 同一张源贴图的全部岛必须在同一张图集里；一次原子操作 = 一张贴图 + 它的 UV 组
- 单个 UV 组装不进最大图集 → 放弃该组图集化，只做质量缩放，并 warning
- **含法线的组不会 90° 旋转**（否则切线空间会错；我们不会重算切线）
- Padding = `max(你设的最小值, ceil(长边/128))`
- 空白区域 pull-push 外扩；透明图集空白 Alpha 保持 0
- 图集强制 Clamp；默认关 Read/Write
- 实验性 NPOT（64 步进）已按需求视为可与 MipStreaming / Crunch 一起用；勾选后 iOS 去掉 PVRTC

## 平台与格式

PC / Android / iOS。Auto 读当前 Build Target。  
透明图集不会给你无 Alpha 的格式。灰度若实际是多通道，会保存为多通道并 warning。

## 语言

`Editor/Localization/` 下有几个 `.json` 就显示几个语言。默认 **Auto** 跟随 NDMF 语言，缺词回退英文。目前自带 `en` 与 `zh-Hans`。

自己加语言：丢一个 `ja.json`（扁平 `"key":"value"`）即可。

## 给第三方开发者

```csharp
using Fosa.AvatarTextureOptimizer.API;
using UnityEditor;

[InitializeOnLoad]
static class Register
{
    static Register()
    {
        AtoExtensions.RegisterShaderAnalyzer(new MyAnalyzer());
        AtoExtensions.RegisterQualityHook(new MyQuality());
        AtoExtensions.RegisterAtlasHook(new MyAtlasHook());
    }
}
```

- `IAtoShaderAnalyzer.TryAnalyze`：返回 `false` 交给内置分析器；返回 `true` 且 `SkipReason != None` 可安全白名单
- **不要改材质上除贴图引用以外的参数**
- 详细类型见 `Runtime/API/`

AAO 的 `UVUsageCompabilityAPI`（原文拼写）在安装 AAO 时通过反射调用，未安装则跳过。

## 日志

所有日志以 `[ATO]` 开头。NDMF 控制台默认只显示总览（贴图数、图集数、岛数、节省比例、耗时），细节折叠。勾选 Verbose 后逐步输出耗时、图集来源、利用率等。

烘焙/构建可取消：临时资产留在硬盘，CPU / GPU / NativeArray / 解码缓存会释放。

## 已知限制

- 形态键只比较权重 0 与 100，忽略负数、超过 100、中间极值
- 父级缩放动画用当前 lossyScale × 本物体曲线近似
- MeshRenderer 无法使用 AAO 的 UV evacuate
- 不含 NDMF 预览
- 本仓库的 CI 环境没有 Unity，完整烘焙请在本地工程验证

## 许可证

MIT。见 `LICENSE`。
