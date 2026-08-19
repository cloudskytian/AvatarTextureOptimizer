# Avatar Texture Optimizer (ATO)

**VRChat Avatar 贴图优化 NDMF 插件**：质量驱动的 UV 岛缩放 + 光栅化岛形装箱图集 + 贴图/材质去重。
Quality-driven UV-island scaling, island-shape atlas packing and texture/material deduplication, delivered as an NDMF plugin for VRChat avatars.

版本 v0.1.0 · MIT License · Unity 2022.3

---

## 这是什么

在 avatar 构建时（非破坏、构建产物内），ATO 会：

1. **取证式分析**：扫描全部渲染器/材质/动画（含材质切换、对象启停、ST 动画、缩放动画、Cutoff/renderMode、形态键 0/100 极值、多 UV 通道），结合着色器知识表（lilToon 全属性表 + Standard 系）判断每张贴图的角色、使用通道、UV 变换；**无法证明安全的贴图一律进白名单并给出 warning**（绝不乱动）。
2. **质量评估**：按岛评估「缩小后是否达到阈值」——线性空间重采样（透明贴图预乘 alpha）、MS-SSIM（短边 <176px 岛回退单尺度 SSIM，<11px 岛忽略）+ ΔE2000 + alpha 专用指标（Cutout 用 clip 后轮廓 IoU、Blend 用线性 RMSE；一图多材质引用时逐材质取最严苛）+ 法线角度误差（均值与 p95）+ 灰度逐通道 RMSE 取最差。二分搜索每岛最优比例，同 UV 组木桶取最大需求。
3. **图集化**：按类型组（角色组合 + 色彩空间 + filterMode）分组，**岛形状光栅化位掩码（4px 粒度）全扫描 BLF 装箱**（禁止矩形装箱）+ 90° 旋转步进 + 候选图集池；岛间 padding 后 GPU pull-push 无限外扩 RGB（padding 区 alpha 保持 0）；动态像素密度（默认 2048–4096 px/m，挡位 512/1024/2048/4096/8192）。
4. **重写与收尾**：网格 UV 重写（法线分裂缝全顶点覆盖）、材质槽位换图、材质/贴图内容去重、不透明重复槽位合并（含动画槽索引更新）。
5. **报告**：NDMF 控制台输出总览（细节默认折叠）：每步耗时、图集尺寸/利用率/岛数/节省字节、逐 UV 组最终处置、全部白名单与警告，全部双语。

## 安装

先决条件（用 VCC/ALCOM 安装）：

- **VRChat Avatar SDK** `com.vrchat.avatars` ≥ 3.10.4
- **NDMF** `nadena.dev.ndmf` ≥ 1.14.4

然后二选一：

- **VCC 本地包**：把本仓库的 `Packages/net.fosa.avatar-texture-optimizer` 文件夹复制到你的项目 `Packages/` 下（Unity 2022.3 会自动加载本地包）。
- **Unity Package Manager**：`Add package from disk...` 选择 `package.json`。

依赖的 Unity 内建包（Burst/Collections/Mathematics/Jobs）会按 `package.json` 自动解析。

## 用法（30 秒）

1. 在 **VRCAvatarDescriptor 所在的根物体**上 `Add Component` → **Avatar Texture Optimizer**（每 avatar 一个，`DisallowMultipleComponent`）。
2. 按需调整选项（下面的表），大多数情况**默认值即可**。
3. 正常构建/上传（Play Mode 或 Build & Upload）。ATO 在 NDMF `Optimizing` 阶段、**Modular Avatar 之后、Avatar Optimizer 之前**运行。
4. 构建完成后在 NDMF 控制台查看 ATO 报告；生成资产在 `Assets/AvatarTextureOptimizer-Generated/<buildId>/`，跨构建复用（内容与设置一致时秒级缓存命中）。

你的**源贴图/材质/网格/动画资产永不被修改**——所有改动只发生在构建产物里。

## 组件选项

| 选项 | 默认 | 说明 |
|---|---|---|
| Quality Preset | High | 质量预设（Performance/Low/Balanced/High/Maximum/Custom）。阈值见下表；Custom 可逐项编辑 |
| Min/Max Pixel Density | 2048 / 4096 px/m | 目标像素密度（挡位 512/1024/2048/4096/8192），受岛在原贴图的真实尺寸钳制 |
| Generate Atlas | 开 | 关闭则只做整图缩放（不打图集） |
| Min Atlas Padding | 4 px | 4/8/16/32/64；与自动 padding `ceil(最大边/128)` 取大者 |
| Experimental NPOT | 关 | 允许非 2 次幂图集（64 步进；自动剔除 iOS PVRTC 等不支持格式） |
| Deduplicate Textures / Materials | 开 | 按「像素内容 + 导入设置」合并重复贴图；按全内容指纹合并重复材质 |
| PC/Android/iOS Override | 关（折叠） | 未勾选的覆盖**整体折叠不生效**；默认用当前构建平台的规则 |
| Whitelist | 空 | 任意类型对象：贴图/材质/网格/GameObject，命中即跳过并记录 |
| Language | Auto | Auto 跟随 NDMF 语言设置（`LanguagePrefs.Language`）；Manual 可强制 en-US/zh-Hans；缺键回退英文 |
| Verbose Logging | 关 | `[ATO]` 前缀的分步耗时与细节日志 |

### 质量预设阈值

| 预设 | 目标质量 | MS-SSIM ≥ | ΔE2000 ≤ | 法线角度 均值/p95 ≤ | Alpha RMSE ≤ | Cutout IoU ≥ | 灰度 RMSE ≤ |
|---|---|---|---|---|---|---|---|
| Performance | 0.80 | 0.90 | 6.0 | 4.0° / 8.0° | 0.060 | 0.90 | 0.05 |
| Low | 0.90 | 0.935 | 4.5 | 3.0° / 6.0° | 0.045 | 0.93 | 0.04 |
| Balanced | 0.95 | 0.96 | 3.0 | 2.0° / 4.0° | 0.030 | 0.95 | 0.03 |
| High（默认） | 0.975 | 0.975 | 2.0 | 1.5° / 3.0° | 0.020 | 0.97 | 0.02 |
| Maximum | 0.99 | 0.985 | 1.0 | 1.0° / 2.0° | 0.010 | 0.985 | 0.01 |
| Custom | 自定义 | 默认近无损(1) | — | — | — | — | — |

目标质量 = 1（或 Custom 中对应项为 1）时跳过 UV 缩放（含纯色短路），原样拷贝。

## 与其他工具协作

- **Modular Avatar**：ATO 运行在 MA 之后，读取的是虚拟化后的最终控制器/网格。
- **Avatar Optimizer (AAO)**：ATO 运行在 AAO 之前，并通过反射调用 AAO 的 `UVUsageCompabilityAPI` 登记被移动/腾挪的 UV 通道；**AAO 不存在时完全正常**（fail-closed 设计）。
- 未知 / 未来版本 lilToon 属性、非 Standard 系着色器：一律白名单 + warning。

## 生成资产管理

- 路径 `Assets/AvatarTextureOptimizer-Generated/`；旧构建残留会被下次构建自动清理，也可手动 `Tools → Avatar Texture Optimizer → Clean Generated Assets`。
- 取消构建：CPU/GPU/内存立即释放，已写磁盘的临时资产保留（下次可复用缓存）。
- `Tools → Avatar Texture Optimizer → Reload i18n`：重新加载翻译 JSON。用户可在 `Assets/AvatarTextureOptimizer/I18n/*.json` 放自己的语言包（与包内同结构；`language` 字段为语言 ID，如 `zh-Hans`/NDMF 语言 ID）。

## 已知限制（诚实清单）

- **不支持 NDMF Preview**（构建时生成资产）。
- 着色器支持范围：**lilToon 全系 + Standard 系（含 `_MainTex`/`_BaseMap`/`_BaseColorMap` 的通用兼容）**；其余一律白名单化，安全但无优化收益。
- Gamma（Gamma color space）工程：sRGB 解码走着色器内显式解码路径（`ATO_DECODE_SRGB`）；建议 Linear 工程。
- 灰度单通道格式（R16）：8-bit 值域下采样精度有限，已尽量规避有损路径。
- Cutout 岛边在极端双线性采样下理论存在 ≤0.5 权重的 alpha 侵蚀（按规格 padding alpha 恒 0）；如遇问题加一档 Min Atlas Padding 即可。
- 材质数 < 子网格数的网格：Unity 会重复末个材质，ATO 取安全漏过（不处理超出槽位的子网格）。
- 只读动画 clip（NDMF marker clip）内的材质引用修改会被 NDMF 的 COW 机制静默丢弃 → 保持原引用（安全降级）。
- 小型（包围盒短边 <11px）岛保持原尺寸，不参与缩放。
- 旋转虽然对所有角色逐纹素证明安全（内容与 UV 同转，映射不变），mipmap 各向异性过滤在极端长条岛旋转后过滤形状略变 —— 阈值评估已包含最终图集采样。

## 扩展 API（高级用户 / 第三方）

程序集 `net.fosa.avatar-texture-optimizer.editor` 暴露 `FOSA.AvatarTextureOptimizer.Editor.ATOExtensionApi`：

```csharp
// 自定义着色器分析器（在用表之前询问，返回 false 继续走内置表）
ATOExtensionApi.RegisterShaderAnalyzer(myAnalyzer); // IATOShaderAnalyzer.TryAnalyze(Material, out ATOMaterialAnalysis)

// 生命周期事件（模型建完 / 图集规划前 / 报告提交前；事件内可追加白名单与排除）
ATOExtensionApi.ModelBuilt     += model => { /* ... */ };
ATOExtensionApi.BeforeAtlasPlan += model => { /* g.SetAtlasBlocked(...) */ };
ATOExtensionApi.BeforeReport   += report => { /* ... */ };

// 自定义打包器：返回 null 回退内置打包器
ATOExtensionApi.CustomPacker = ctx => { /* ctx.Model/QualityRatios/Platform/IsGroupAtlasEligible */ return null; };
```

## 版本与支持

- 仓库：https://github.com/fosa/avatar-texture-optimizer
- 开发与质量：本插件按「双 Coder 共识 → 双 Reviewer 联审 → 双 QA 独立全量重读」流程开发（全过程记录见 `docs/TEAMLOG.md`），并带括号/JSON 自检脚本 `tools/brace_check.py`。
- Issues/Discussions 见仓库页。

## License

MIT — 见 [LICENSE](LICENSE)。
