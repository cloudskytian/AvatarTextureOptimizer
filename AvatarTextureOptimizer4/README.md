# Avatar Texture Optimizer (ATO)

**包名 / Package:** `net.fosa.avatar-texture-optimizer`

一个面向 VRChat Avatar 的、开源的 NDMF 贴图/UV 优化工具：分析 Avatar 身上的网格，为满足条件的材质建立"网格 UV ↔ 贴图"的映射关系，在**目标质量算法**的约束下缩小 UV 岛、剔除未使用区域、按类型组重组为一张或多张图集，在保证画质的同时最大化贴图利用率。

> An open-source NDMF tool for VRChat avatars that builds a UV↔texture mapping for eligible materials, shrinks UV islands under a perceptually-validated target-quality algorithm, and re-packs them into one or more atlases grouped by texture type — maximizing texture utilization while preserving quality.

---

## 特性 / Features

- **目标质量算法（可感知验证）**：线性空间重采样；透明贴图预乘 alpha 下采样；`MS-SSIM`（岛短边 < 176px 回退单尺度 `SSIM`，< 11px 忽略）＋ `ΔE(CIEDE2000)` ＋ alpha（Cutout 用裁剪轮廓 IoU / Blend 用线性 RMSE，多材质引用时逐引用取最严）；法线贴图用角度误差 p95；灰度贴图按被使用通道的线性 RMSE 取最差。二分搜索求最小可接受缩放，Burst 并行 + 托管兜底。
- **类型组**：按「分类 + 色彩空间 + filterMode + alpha + 是否存在法线/遮罩伴随」分组，避免"10 张主色 1 张法线"导致法线图集 9/10 浪费的问题；同一 UV 的贴图构成 **UV 组**，在不同图集上位置一致。
- **装箱**：4px 粒度位掩码光栅化 + 全扫描 BLF + 候选图集池（POT / 实验性 NPOT）+ 90° 步进旋转（法线贴图锁定 0°）；岛形状装箱，非矩形装箱；padding 自动 `max(用户最小值, ⌈边长/128⌉)`。
- **安全兜底**：白名单（不限对象类型）；ST 变换/动画 ST/越界跨缝 repeat/特殊用途（matcap、emission ramp、AudioLink LUT 等）一律按白名单跳过并 warning；压缩格式按平台校验并兜底；材质/网格/动画资产先克隆再改，绝不污染用户原始资产。
- **兼容**：运行于 MA 之后、AAO 之前（NDMF `Optimizing` 阶段 + `BeforePlugin("com.anatawa12.avatar-optimizer")`）；通过 AAO 的 `UVUsageCompabilityAPI`（原文拼写）疏散被 AAO 占用的 UV 通道；AAO 未安装时自动跳过。
- **去重**：贴图（内容+导入设置）、材质（参数+关键字）、相同的不透明材质槽合并（含动画引用与槽索引改写）。
- **其他**：Mip Streaming 与 Mipmap 绑定开关；分平台 override（PC/Android/iOS）；i18n（JSON 扩展，Auto 跟随 NDMF 语言，缺省回退英文）；`[ATO]` 分级日志；构建进度与取消；NDMF 控制台报告；处理后移除自身组件。

---

## 安装 / Installation

1. 通过 VCC/VPM 添加本包（`net.fosa.avatar-texture-optimizer`），或直接把整个文件夹放入 Unity 工程的 `Packages/` 或 `Assets/`。
2. 依赖：`com.vrchat.avatars`（≥3.7.0）、`nadena.dev.ndmf`（≥1.14.0）。可选：`com.anatawa12.avatar-optimizer`（≥1.8.0，提供 `UVUsageCompabilityAPI` 兼容）、`jp.lilxyzw.liltoon`。

## 使用 / Usage

1. 在 Avatar 根对象（带 `VRCAvatarDescriptor` 的对象）上添加 **ATO Avatar Optimizer** 组件（一个 Avatar 及其子级只允许一个）。
2. 默认即"开箱即用"：High 质量、生成图集、padding 4、密度 2048–4096 px/m。
3. 高级用户可折叠展开：质量挡位（Ultra/High/Medium/Low/Custom）、图集开关、NPOT、padding、密度、压缩/流式、白名单、分平台 override、调试日志、语言。
4. 构建/烘焙时自动执行，完成后在 NDMF 控制台查看报告。

## 第三方开发者 / Developers

- **扩展点**：所有阶段均为独立静态类（`Editor/` 下按目录划分），可在 `ATOPipeline.Run` 中插入自定义阶段；数据模型（`Editor/ATOModel.cs`）是公开契约。
- **i18n**：在 `Localization/` 下新增 `ato_<lang>.json`（格式 `{"entries":[{"key":"...","value":"..."}]}`）即可新增语言；有多少文件显示多少语言。
- **日志**：`ATOLogger`（`[ATO]` 前缀，debug/verbose 两级，`ATOAdvancedSettings` 控制）。

## 已知限制 / Known limitations（开发阶段）

- 暂不支持 NDMF 预览（preview）。
- 质量指标的 GPU（RenderTexture/ComputeShader）批量评估为可选路径，当前以 Burst + 托管参考实现为准（正确性优先）。
- 图集 padding 在"参考分辨率布局 → 实际尺寸"映射中按比例缩放，为工程近似（详见 `ATOAtlasPacker.cs` 注释）。
- 材质属性动画按路径绑定，克隆材质时通过 `AssetSaver` 持久化并重映射路径；极端组合建议实测。

## 许可证 / License

MIT（见 `LICENSE`）。
