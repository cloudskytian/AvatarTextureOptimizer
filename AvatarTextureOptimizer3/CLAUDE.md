# AvatarTextureOptimizer 项目记忆

## 目标

全世界最好的 VRChat Avatar NDMF 贴图优化器。包名 `net.fosa.avatar-texture-optimizer`。

## 计划

1. ~~可行性 + 读 NDMF/AAO/MA/lilToon API~~
2. ~~完整 UPM 包首版~~
3. ~~补齐装箱/类型组/压缩/槽合并/GPU 下采样/岛形状光栅/Burst 膨胀/关 ReadWrite~~
4. 用户同步到 Unity 后对真实 Avatar 完整烘焙验收（工程外无法代替）

## 已完成（可交付）

- Runtime 组件 + 质量挡位 + 平台覆盖 + 白名单
- NDMF `Optimizing`：After MA，Before AAO
- 挂载校验：同一 Avatar 仅一个；**组件所在物体必须有 VRCAvatarDescriptor**
- 动画扫描、lilToon/标准关键字分析
- UV 岛、重叠合并、可平移归一、跨缝白名单
- 质量：MS-SSIM / CIEDE2000 / IoU / RMSE / 法线角；各向异性二分；大岛 GPU RenderTexture 下采样
- 岛 **三角形 4px 位掩码** + BLF + 90° 转置
- **类型组** + **UV 组原子装箱** + 主色/法线/蒙版 **共用岛坐标**
- 白名单贴图跳过全部；同 UV 其它贴图只跳过图集，仍缩放+导入
- 安全压缩枚举 + 透明/多通道灰度 fallback
- 材质去重 + 不透明槽合并（动画不单独切槽时）并重写动画下标
- AAO `UVUsageCompabilityAPI` 可选
- `AtoHooks`、en-US/zh-Hans、进度取消、成品卸组件、`[ATO]` 日志

## 刻意不做

- NDMF 预览（需求写明暂不支持）
- 不把第三方 zip 打进本包

## 已核实 API

- NDMF 1.14.4 Plugin/Pass/ErrorReport/AssetSaver/Localizer
- AAO 1.9.17 `UVUsageCompabilityAPI`（拼写 Compability）
- MA QualifiedName `nadena.dev.modular-avatar`
- lilToon 2.3.4 属性名见 `lilMaterialProperties.cs`

## 注意

- 记忆只写本文件
- 改前先读代码取证
- 日志前缀 `[ATO]`
- 不要改第三方库
