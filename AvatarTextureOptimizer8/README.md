# Avatar Texture Optimizer (ATO)

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-VRChat%20Avatar-orange.svg)]()

**面向 VRChat Avatar 的开源 NDMF 贴图优化工具 —— 分析网格 UV 与贴图的映射,按感知质量缩放 UV 岛,剔除未使用的贴图区域,把碎片重组成高利用率图集。**
An open-source NDMF texture optimizer for VRChat avatars — analyzes mesh-UV-to-texture mappings, scales UV islands by perceptual quality, removes unused texture space and repacks islands into high-utilization atlases.

- 包名 / Package: `net.fosa.avatar-texture-optimizer`
- 依赖 / Dependencies: NDMF ≥ 1.14 (必需), VRC SDK 3.x, Unity Burst/Mathematics
- 运行时机 / Runs: Modular Avatar 之后、Avatar Optimizer (AAO) 之前,`BuildPhase.Optimizing`
- 版本 / Version: 0.1.0(开发阶段,字段可能随时调整 / development stage, fields may change)

---

## 快速开始 / Quick Start

1. 把本包放入工程的 `Packages/` 目录(或经 VPM/VCC 安装)。
2. 在 Avatar 根物体(带 `VRCAvatarDescriptor` 的物体)上添加组件 **ATO Avatar Texture Optimizer**。
   - 每个 Avatar(含子级)只允许一个;必须挂在根物体上,否则构建报错中止。
3. 选择质量挡位(默认 **High**),直接 **Build**(或用 NDMF 手动烘焙)。
4. 构建结束在 **NDMF 控制台**查看总览报告(细节可展开);`[ATO]` 详细日志在 Console。

### 质量挡位 / Quality Presets

| 挡位 | 说明 | 依据 |
|---|---|---|
| Near Lossless 近无损 | 不重采样,岛原样 1:1 拷贝(质量=1) | — |
| **High(默认)** | MS-SSIM ≥ 0.995,ΔE00 ≤ 1.0,alpha IoU ≥ 0.995 / RMSE ≤ 0.006,法线 ≤1°/3°,灰度 RMSE ≤ 0.010 | CIEDE2000 可觉差≈1.0;MS-SSIM≥0.99 常被视为视觉无损 |
| Medium | 0.98 / 2.5 / 0.99 / 0.015 / 2°/6° / 0.025 | — |
| Low | 0.95 / 5.0 / 0.98 / 0.03 / 4°/12° / 0.05 | — |
| Custom 自定义 | 用户自由修改,不会被其他挡位覆盖;默认值=近无损 | — |

阈值全部折叠在 **Advanced** 中可改;挡位切换自动刷新阈值;像素密度(默认 2048–4096 px/m,挡位 512–8192)防止发糊与浪费。

## 功能一览 / Features

- **贴图类型组 + UV 组**:法线/蒙版伴随关系统一装箱(10 张贴图只有 1 张有法线时不再浪费 9/10);动画切换的材质/贴图并入原组,同一 UV 在所有图集上位置一致。
- **感知质量驱动缩放**:线性空间重采样、透明贴图预乘 alpha 下采样;MS-SSIM(短边 <176px 回退 SSIM,<11px 忽略)+ CIEDE2000 + alpha(Cutout 轮廓 IoU / Blend RMSE,动画阈值逐一取最严);法线角度误差均值+p95(正确解码/重归一化);灰度仅按被使用通道 RMSE。二分搜索+各向异性双轴细化+木桶效应。
- **装箱**:Unity Burst 光栅位掩码(4px 粒度)+ 全扫描 BLF + 旋转 90°(切线保持原样)+ 候选图集池(POT / 实验性 NPOT-64px);padding = max(选项, ceil(边长/128)) ≥ 4;pull-push 无限外扩渗色;图集数量不限,名称 `ATO_` 前缀。
- **白名单**:任意对象类型(网格/材质/贴图/动画/游戏物体…),其引用的全部贴图跳过所有优化;同 UV 的其他贴图自动改为整图缩放(保证不破坏原 UV 采样)。
- **去重**:贴图按像素内容+导入设置去重;材质/图集按内容与参数去重,可合并的不透明材质槽自动合并(含动画索引更新)。
- **平台覆盖**:PC / Android / iOS 分别覆盖全部优化参数(参考 Unity Platform Override);压缩格式按 透明/不透明/法线/灰度 安全枚举,构建时兜底(NPOT 时剔除 PVRTC 等)。
- **Mip + MipStreaming** 绑定单开关(VRC 规则);图集强制 Clamp、关闭 Read/Write。
- **兼容**:lilToon 属性级精确分析(可兼容未来新增属性,无法分析的自动白名单+警告);AAO `UVUsageCompabilityAPI` 集成(未安装自动跳过);只处理"经网格 UV 采样、无任何 ST/滚动/旋转/视差/贴花变换"的贴图。
- **i18n**:JSON 配置文件即语言(内置 English + 简体中文);Auto 跟随 NDMF 语言,缺失回退英文;欢迎第三方补充语言文件。
- **可观测**:[ATO] 前缀日志含每阶段耗时/图集来源/岛数/尺寸/利用率;NDMF 控制台总览+折叠细节;进度条支持取消。

## 高级用法 / Advanced

- **不生成图集**:主开关取消勾选后仅做整图缩放+导入参数优化,不改 UV。
- **实验性 NPOT**:以 64px 步进生成更贴合的图集(NPOT 已验证支持 Mip Streaming 与 Crunch)。
- **调试**:`verboseLogging` 输出全部决策;`debugSaveAtlases` 把图集存为 PNG 到 `Assets/AvatarTextureOptimizerDebug/`。
- **扩展 API**(第三方开发者):见 `DESIGN.md` §7 与 `Editor/Core/ATOApi.cs`(IslandScaleModifier / WhitelistProvider / AtlasesBaked 等钩子)。

## 常见问题 / FAQ

- **构建被中止提示组件挂载错误?** 组件必须在 `VRCAvatarDescriptor` 同一物体上,且整个 Avatar 只能有一个。
- **某贴图没有被优化?** 查看 NDMF 控制台与 verbose 日志:常见原因有白名单命中、材质存在 ST/滚动变换、UV 跨 wrap 缝、着色器无法分析(均会给出具体原因)。
- **取消构建后临时文件?** 取消会释放 CPU/GPU/内存;已写入硬盘的调试 PNG 与已保存的中间资产会保留,可手工删除。

## 许可 / License

MIT —— 见 [LICENSE](LICENSE)。依赖各自遵循其许可证(NDMF/MA/AAO/lilToon 等,本包不修改、不捆绑它们)。
