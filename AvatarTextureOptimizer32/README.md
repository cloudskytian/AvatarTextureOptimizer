# Avatar Texture Optimizer (ATO)

> 全世界最好的 VRChat 贴图优化工具 —— 一个开源 NDMF 工具，在保证视觉质量的前提下最大化贴图利用率、降低贴图体积与内存占用。
>
> The world's best VRChat texture optimization tool — an open-source NDMF tool that maximizes texture utilization while preserving visual quality.

包名 / Package name：`net.fosa.avatar-texture-optimizer`

---

## 这是什么 / What it is

ATO 在 NDMF 构建期（Modular Avatar 之后、AAO 之前）分析 Avatar 上的网格与材质，建立「网格 UV → 贴图」的映射关系。它通过一个**目标质量算法**按质量挡位缩放每个 UV 岛（或整张贴图），剔除未被使用的 UV 区域，再把岛重新装箱合并成一个或多个图集，从而在保证画质的同时大幅提高贴图利用率、降低显存占用。

**核心原则 / Core principles**

- 只改 **网格 UV + 贴图/图集 + 贴图引用**，绝不修改材质除贴图以外的任何着色器参数。
- 优化前后保持 Avatar 表现一致性；任何可能不安全的转换都会 fallback。
- 目标质量算法保证「缩到什么程度」是可量化、可控的。

---

## 快速上手 / Quick start

1. 在你的 Avatar 根物体（必须带 `VRCAvatarDescriptor`）上添加组件 `Avatar Texture Optimizer`。
2. 按需调整质量挡位、像素密度、图集开关等参数。
3. 正常烘焙 / 构建 Avatar。ATO 会在 NDMF 流程中自动运行，并在 NDMF 控制台输出报告。

一个 Avatar 及其子级上**只允许挂载一个**组件；挂载对象上必须存在 `VRCAvatarDescriptor`，否则会报错中止烘焙。

---

## 主要功能 / Features

- **目标质量算法**：线性空间重采样、透明预乘 alpha 下采样；MS-SSIM（短边 <176px 回退单尺度 SSIM，<11px 忽略）+ ΔE(CIEDE2000) + alpha（Cutout 用 clip 后轮廓 IoU / Blend 用线性 RMSE）；法线贴图用角度误差 + p95；灰度贴图逐通道线性 RMSE 取最差。UV 缩放用二分搜索（先均匀达标后双轴独立细化）。
- **质量挡位**：Lowest / Low / Medium / High / Ultra / Custom（自定义挡位参数默认全 1，近无损），挡位变化时具体参数随之变化。
- **贴图类型组**：法线/蒙版/主色等按类型（含色彩空间、filterMode）分组图集化，解决「一张大图集只有一张贴图有法线」导致的利用率浪费问题。
- **UV 组**：同一 UV 对应的所有贴图（含动画切换）构成一个 UV 组，保证同一 UV 在不同图集上位置一致。
- **图集装箱**：Burst 光栅位掩码（4px 粒度）+ BLF 全扫描 + 面积/边长降序 + 90° 旋转步进 + 候选图集池（2^n，可选 NPOT 64 步进），岛形状光栅化装箱（非矩形）。
- **白名单**：白名单不限对象类型；白名单对象引用的全部贴图跳过所有优化。
- **去重**：贴图按实际像素 + 导入设置去重；材质/贴图内容参数完全相同则去重并更新引用（含动画引用与材质槽索引）。
- **压缩 / 平台**：按透明/不透明/法线/灰度分类提供压缩格式；支持 PC/Android/iOS 平台 override；Mipmap 与 MipStreaming 绑定控制（VRChat 要求）。
- **像素密度**：默认最小 2048px/m、最大 4096px/m，按 UV 岛大小与模型真实大小的对应关系钳制，防止浪费或发糊。
- **形态键 / 动画**：形态键取 0/100 面积最大值；动画物体缩放按最大缩放算面积；兼容动画材质切换、多材质槽、渲染模式/Cutoff 修改等。
- **多通道 UV / UV 越界归一**：支持多通道 UV；越界但可整体平移归一到 [0,1] 的自动归一，跨 wrap 缝的视作白名单并 warning。
- **i18n**：读取包内 `Editor/I18n/*.json`（有几个语言文件就显示几个语言），支持手动切换，默认 Auto 读取 NDMF 语言，无翻译回退英文。

---

## 质量挡位参数参考依据 / Quality preset rationale

- ΔE(CIEDE2000)：人类视觉的「可察觉差」阈值 JND≈2.3，ΔE<1 几乎不可察觉。Medium 挡位取 2.5（略可察觉但安全），High/Ultra 取 1.5/0.75。
- MS-SSIM：约 0.99 可视作近无损（Wang et al. 多尺度结构相似度）。Ultra 取 0.999。

---

## 面向第三方开发者 / For developers

### 项目结构

```
Runtime/   组件与数据定义（组件、白名单、设置、质量参数、枚举）
Editor/
  ATOPlugin.cs         NDMF 插件注册（Optimizing 相位，BeforePlugin AAO）
  Passes/ATOPasses.cs  五阶段 Pass（收集/分析/处理/装箱/应用）
  Core/                核心逻辑
    ATOModel.cs        数据模型
    ATOCollector.cs    收集 + 去重 + 白名单
    ATOAnalyzer.cs     UV 组 / 动画扫描 / 岛提取 / 面积
    ATOQualityMetrics.cs 质量算法（MS-SSIM / ΔE2000 / 角度 / RMSE）
    ATOProcessor.cs    UV 缩放（二分搜索 + 像素密度钳制）
    ATOPacker.cs       图集装箱（光栅位掩码 + BLF）
    ATOApplier.cs      输出（图集/网格 UV/材质重指向/报告）
    ATOAAOBridge.cs    AAO 兼容（反射）
    ATOCompression.cs  压缩/平台/MipStreaming（真正落地）
    ATODedup.cs        材质去重 + 材质槽合并 + 动画索引重映射
    ATOUtil.cs         公共工具
  Burst/               Burst 加速作业（光栅化 / SSIM）
  I18n/                en.json / zh-CN.json
  UI/                  自定义 Inspector
```

### 依赖 / Dependencies

- `nadena.dev.ndmf`（1.14.4）
- `com.unity.burst` / `com.unity.collections`（VRChat 项目自带，用于图集装箱加速；不可用时自动回退 CPU）
- 可选：`com.anatawa12.avatar-optimizer`（AAO，未安装时自动降级）

### 扩展点 / Extension points

- **自定义质量算法**：`ATOQualityMetrics` 为静态工具类，可替换/扩展指标实现。
- **自定义着色器分析**：`ATOShaderAnalyzer.Rules` 可增加关键字规则，兼容未来着色器。
- **i18n**：在 `Editor/I18n/` 新增 `{lang}.json` 即可扩展语言（扁平 key→value 结构）。
- **平台/压缩**：`ATOCompression.PickFormat` / `Apply` 集中处理格式解析、落地与兜底。

### 日志 / Logging

所有日志以 `[ATO]` 开头。组件开启 `verboseLogging` 可输出每步耗时、图集贴图来源、岛数量、图集大小/利用率、优化量等细节。

---

## 开发状态 / Status

本项目处于开发阶段。配置字段可随时调整，不保证向后兼容。

## License

（待补充 / TBD）
