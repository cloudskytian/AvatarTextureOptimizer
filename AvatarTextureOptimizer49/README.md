# Avatar Texture Optimizer (ATO)

**全世界最好的 VRChat Avatar 贴图优化工具 — The best texture optimizer for VRChat avatars.**

ATO 是一个开源的 [NDMF](https://github.com/bdunderscore/ndmf) 插件（包名 `net.fosa.avatar-texture-optimizer`），基于**网格 UV 与贴图的映射关系**对 Avatar 贴图做**保质量、岛级（island-level）**的图集化与缩放优化。它在 **Modular Avatar 之后、Avatar Optimizer (AAO) 之前**执行，全程非破坏式，只修改网格 UV 与材质的贴图引用，**绝不修改材质的其他任何着色器参数**。

---

## 特性 / Features

- **UV→贴图映射**：同一贴图即使用于不同材质，只要贴图不变、网格 UV 不变，映射即可复用，完全无视材质其他参数。
- **岛级质量缩放**：以导入后的有效贴图为基准，二分搜索 UV 岛缩放，指标全部达标才通过：
  - `MS-SSIM`（短边 <176px 回退单尺度 SSIM，<11px 忽略）+ `ΔE CIEDE2000`（颜色贴图）
  - alpha：Cutout 用 clip 后轮廓 IoU、Blend 用线性 RMSE；贴图被多个材质引用时逐一评估取最严
  - 法线贴图：正确解码→重采样→重归一化→重编码后按角度误差 mean+p95 对比
  - 灰度/蒙版：线性空间逐通道 RMSE 取最差
  - 各向异性：先均匀缩放达标，再双轴独立二分细化
  - 纯色岛直接短路缩到 `min(4, 短边)`；像素密度钳制（默认 2048–4096 px/m，挡位 512–8192）
  - GPU(RenderTexture) 批量重采样 + Burst 并行度量；目标质量=1（近无损）时原样拷贝不重采样
- **贴图类型组**：是否存在法线/蒙版等辅助贴图、色彩空间、filterMode 构成类型组；法线/蒙版生成**同布局镜像图集**，全部岛质量余量允许时整体 2^k 缩小，解决“10 张贴图合成图集、法线图集 9/10 浪费”的问题。
- **UV 组**：同一 UV 的所有贴图（含动画切换）在所有图集上共享同一套岛矩形，杜绝跨图集采样错位。
- **形状光栅化装箱**：Unity Burst 光栅位掩码（4px 粒度）+ 全扫描 BLF + 面积/边长降序 + 90° 旋转（位掩码真旋转，法线绝不重算切线）+ 候选图集池（POT 默认 / 实验性 NPOT 64px 步进，移动端上限 4096）。
- **pull-push 无限外扩渗色**：GPU 金字塔外扩填满图集空白，透明图集空白区 alpha 保持 0。
- **动画安全**：材质切换、贴图切换、渲染模式/Cutoff 动画、缩放动画、启用/禁用动画全部纳入分析并取最严苛；ST/滚动/旋转/贴花等不安全用途一律白名单+警告。
- **lilToon 支持**：属性表/`_UseXXX` 特性开关/`_XXX_UVMode` 多通道 UV 全支持（基于 lilToon 2.3.4 源码与 AAO 实现交叉验证），未来版本未知属性自动白名单+警告。
- **去重**：处理前按“实际像素 + 完整导入设置”去重贴图；处理后按“内容 + 参数”去重材质/图集；可判定安全时合并相同不透明材质槽并更新动画索引。
- **平台覆盖**：PC / Android / iOS 分别覆盖全部优化参数（参考 Unity platform override）；压缩格式按 透明/不透明/法线/灰度 × 平台提供安全枚举，任意组合都有 fallback，绝不产出错误材质。
- **Mip + MipStreaming 绑定单开关**（VRChat 要求），默认开启。
- **进度与取消**：烘焙/构建显示阶段与进度，可取消；取消保留磁盘临时资产并释放 CPU/GPU/内存。
- **NDMF 控制台报告**：每阶段耗时、图集贴图来源、岛数、图集尺寸、利用率、相对原贴图优化量；默认总览、明细折叠。
- **i18n**：`Localization/*.json` 即语言（放几个文件就有几种语言），Auto 跟随 NDMF 语言，缺失回退英文；自带英文与简体中文。
- **扩展 API**：`Fosa.AvatarTextureOptimizer.Editor.ATOApi`（第三方贴图分类器、额外白名单提供者）。

## 质量挡位依据 / Quality preset references

| 挡位 | MS-SSIM | ΔE2000 | 法线 mean/p95 | 依据 |
|---|---|---|---|---|
| NearLossless | 全 1（原样拷贝） | — | — | 近无损 |
| Ultra | 0.995 | 1.0 | 0.75°/1.5° | 感知无损级 |
| **High（默认）** | 0.99 | 1.5 | 1.0°/2.5° | Wang et al. 2004 MS-SSIM / CIEDE2000 JND≈2.3 (Sharma 2005) |
| Balanced | 0.98 | 2.3 | 1.5°/3.5° | 恰可察觉差 |
| Aggressive | 0.95 | 3.5 | 2.5°/5.0° | 轻微可见损失 |
| Custom | 用户自定（默认全 1） | | | |

## 安装 / Install

1. 前置：NDMF ≥ 1.14.4（VPM 依赖）；VRChat Avatars SDK 3.10+；AAO / lilToon 可选（AAO 未安装时自动跳过 UV 疏散）。
2. 将本包放入 Unity 工程的 `Packages/` 目录（或通过 VPM/VCC 以 `net.fosa.avatar-texture-optimizer` 添加）。
3. 在 Avatar 根物体（挂 `VRCAvatarDescriptor` 的物体）上添加组件 **ATO Avatar Texture Optimizer**。
   - 一个 Avatar（含子级）只允许一个组件，且必须在根物体上，否则构建报错中止。
4. 构建上传（或 NDMF 手动烘焙）即可。烘焙完成后 NDMF 控制台输出报告。

## 使用 / Usage

- **质量挡位**：默认 High；切换挡位会同步具体参数；手动改任何参数自动变为 Custom。
- **生成图集**（默认开）：关闭后仅整图缩放 + 导入参数优化，不剔除未用 UV、不重排 UV。
- **白名单**：任意类型对象（网格、材质、贴图、动画、GameObject…），其引用到的全部贴图跳过所有优化；与其同 UV 的其他贴图跳过图集化但保留其他优化。
- **高级选项**：质量阈值、像素密度、padding（4/8/16/32/64，默认 4，实际取 `max(所选, ceil(图集最大边/128))`）、NPOT 实验、去重开关。
- **平台覆盖**：勾选平台后整体覆盖通用参数（含图集上限：移动端默认 4096）。
- **格式**：按 透明/不透明/法线/灰度 分别设置；`Auto` 为平台最优解；Mip+Stream 为绑定开关。
- **调试**：开启 Verbose 后输出全部 `[ATO]` 详细日志（含每步耗时）。

## 第三方开发者 / For developers

```csharp
[InitializeOnLoad]
static class MyAtoExtension
{
    static MyAtoExtension()
    {
        ATOApi.RegisterClassifier(new MyClassifier());       // 自定义贴图分类 / UV 通道 / 安全性
        ATOApi.RegisterWhitelistProvider(new MyWhitelist()); // 构建期提供额外白名单
    }
}
```

- 处理时机：`BuildPhase.Optimizing`，`AfterPlugin("nadena.dev.modular-avatar")`，`BeforePlugin("com.anatawa12.avatar-optimizer")`。
- AAO 兼容：反射调用 `Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI`（拼写与 AAO 源码一致），改写 UV 前将原 UV 疏散到空闲通道并注册，AAO 未安装时自动禁用。
- i18n：在 `Localization/` 放 `<lang>.json`（扁平键值）即可新增语言。

## 已知限制与设计取舍 / Known limitations

- 度量在 CPU Burst 计算、重采样在 GPU（混合模式，结果一致性优先）。
- 蒙版/灰度“被使用通道”无法从通用着色器推断时按全通道评估（最严格）；第三方可经 `ITextureClassifier` 精确指定。
- NPOT + Crunch 组合依赖 Unity 版本行为，失败自动回退并控制台警告；iOS NPOT 下不提供 PVRTC。
- 同一贴图被多个网格 UV 使用时，像素会按 UV 组分别入集（必要代价），材质按 (renderer, slot) 克隆指向正确图集。
- 材质槽合并仅在“渲染器完全无材质槽动画”时执行（最保守）。
- 暂不支持 NDMF 预览。

## License

MIT © fosa
