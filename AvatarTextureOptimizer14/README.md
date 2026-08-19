# Avatar Texture Optimizer (ATO)

**中文** | [English](#english)

为 VRChat Avatar 打造的开源 NDMF 贴图优化工具：按感知质量目标自动缩小 UV 岛/整图、装箱为图集、重映射网格 UV、去重并回写材质与动画——全程不改贴图以外的任何材质参数，任何不安全情形一律回退（fallback）。

> 目标：做全世界最好的 VRChat 贴图优化工具。

---

## 功能一览

- **目标质量驱动缩放**：线性空间重采样（透明贴图先预乘 alpha），逐岛二分搜索最小尺寸，满足全部阈值（MS-SSIM、CIEDE2000 P95、alpha Cutout 轮廓 IoU / Blend RMSE、法线角度 mean+P95、蒙版 RMSE）；双轴独立细化、纯色岛短路、近无损挡位直接原样拷贝。
- **世界面积自适应**：按三角形最大世界面积（形态键 0/100 取最大、动画缩放取最大）做像素密度钳制（px/m 档位 512–8192）。
- **UV 岛装箱成图集**：4px 粒度位掩码 Burst 光栅化 + 全扫描 BLF、面积降序+边长降序、90° 转置旋转（法线组岛除外，切线绝不重算）、POT 候选池（实验性 NPOT）、贴图队列原子装箱 + 别名队列（图集数量自然增长、同 UV 组跨图集共位）、pull-push 无限外扩（透明区域 alpha 保持 0）。
- **类型组**：按贴图用途（主色/法线/蒙版并集）+色彩空间+filterMode 分键；非主色 plane 可整平面进一步缩放省体积。
- **图集开关（默认开）**：关闭时不剔除 UV、不做图集，仅整图收敛缩放 + 导入参数优化。
- **安全分析**：lilToon 2.x 与标准关键字着色器属性自动分析；ST/滚动旋转/Decal/动画变换/特殊用途任一不满足 → 白名单回退；跨 wrap 缝 UV 拒绝优化并告警；未知着色器保守白名单；第三方可注册自定义着色器分析器。
- **动画兼容**：材质切换、贴图切换、多材质槽、render-mode/Cutoff 动画一律取质量最严；clip/控制器一律克隆后改写（绝不改用户资产）。
- **去重**：源贴图按像素+导入设置去重（白名单感染去重桶）；生成产物（图集平面/整图）按字节内容再去重；材质按完整内容指纹去重。
- **压缩格式安全枚举**：按 透明/不透明/法线/灰度 分类；PC/Android/iOS 平台 override（折叠，勾选才显示，默认跟随当前构建平台）；iOS 不提供 PVRTC；内容兜底（有透明却选无 alpha 格式 → 自动回退并在 NDMF 控制台告警）。
- **VRC 规则**：Mipmap 与 MipStreaming 绑定单开关（按贴图分类）；图集强制 Clamp、关闭 Read/Write（不开放修改）；其余导入参数取所有源贴图最高质量。
- **AAO 兼容**：经反射对接 `UVUsageCompabilityAPI`（查询占用避让，未安装 AAO 也正常工作）；处理顺序保证在 Modular Avatar 之后、Avatar Optimizer 之前。
- **构建体验**：进度条+可取消（保留磁盘临时资产、释放 CPU/GPU/内存）；全程 `[ATO]` 日志（含各阶段耗时）；NDMF 控制台总体报告 + 细节 verbose；烘焙完成后移除组件自身。

## 安装

**VCC（推荐）**：将本仓库作为 VPM 包加入工程（`package.json` 依赖 `com.vrchat.avatars >= 3.10.4`、`nadena.dev.ndmf >= 1.14.4`）。

**手动**：把仓库内容拷入 `Assets/AvatarTextureOptimizer/`（保留 `Runtime/`、`Editor/`、`i18n/`、`package.json`）。

环境：Unity 2022.3 LTS + VRChat SDK3 Avatars + NDMF 1.14+。

## 使用方法

1. 在带有 **VRCAvatarDescriptor 的根对象**上添加组件 `Avatar Texture Optimizer`（每个 Avatar 仅允许一个，违规会在构建时报错中止）。
2. 按需调整：
   - **Quality 质量挡位**：近无损 / 极高 / 高 / 中（默认）/ 低 / 极低 / 自定义（高级折叠内显示对应阈值；自定义可自由编辑）。挡位越高，允许的缩小越少。
   - **Min/Max Pixel Density**：像素密度上下限（px/m）。
   - **Min Padding**：岛间距下限（4/8/16/32/64，默认 4；实际值还会自适应图集尺寸）。
   - **NPOT（实验）**：允许非二次幂图集候选。
   - **Mips & Streaming**：按主色/法线/蒙版分别开关（与 mipStreaming 绑定）。
   - **去重开关**：贴图/图集去重、材质去重。
   - **白名单**：任意对象（贴图/材质/GameObject/动画 Clip…）——其中引用到的贴图完全跳过优化并跳过图集化（同组其余贴图仍整图缩放）。
   - **Platform Overrides（高级）**：按平台钳制图集最大边长、选择各分类压缩格式。
3. 上传/构建 Avatar（NDMF Build，含 VRC 上传与 Gesture Manager/Av3 Emulator 的 Play Mode 构建）。结束后查看 Console 的 `[ATO]` 日志与 NDMF 报告面板获取：图集清单、各图集来源贴图与利用率、材质替换数、节省估算、全部警告。
4. 生成物位于 `Assets/ATO_Generated/`（组件在烘焙完成后自动移除自身；本地磁盘源资产绝不被修改）。

## 给第三方开发者

- **着色器分析器扩展**：实现 `Fosa.ATO.Editor.IATOShaderAnalyzer`（`CanAnalyze(Shader)` + `Analyze(Material, List<ATOTextureSlot>)`），通过 `ATOShaderAnalyzerRegistry.Register(...)` 注册；你的分析器优先于内置 lilToon/标准规则，ATO 之后仍会施加自身的 ST/旋转/动画守卫。
- **流水线事件**：`ATOEvents.StageFinished`（阶段名 discovery/uv/quality/packing/bake/wholescale/remap/materials/dedup/clips/report），或实现 `IATOPipelineHook` 并 `ATOHookRegistry.Register(...)`。
- **i18n 扩展**：在包内 `i18n/` 目录增加 `<culture>.json`（扁平 `{key:text}`），语言自动出现在组件语言下拉里；也可 `ATOL10n.RegisterLanguageTable(...)` 运行时注册。缺失键回退英文。
- 代码内嵌双语注释（英+中），数据模型见 `Editor/ATOModel.cs` 头注。

## 已知限制与偏差（明示）

- **质量评估与 pull-push 目前为 Burst 加速的 CPU 实现**（GPU 批量管线在路线图上）；大贴图集首个构建会比较耗时，像素缓存有 768MB 上限并按 LRU 逐出。
- 蒙版质量门使用**使用通道合并 RMSE**（阈值为合并尺度，判据等价略宽于"逐通道取最差"）。
- **材质槽合并**：相同且不透明的槽在去重后会共享同一材质引用；**不修改子网格拓扑、不重映射动画槽索引**——真正的槽合并建议交给其后的 AAO `MergeSkinnedMesh` 组件完成。
- NPOT 候选为实验开关：部分平台/压缩格式对 NPOT 有限制（Unity 会自动降级未压缩），追求稳妥请保持默认 POT。
- 暂不支持 NDMF 预览（需求确认）。
- 动画曲线中烘焙进 clip 的 `_ST`/ScrollRotate 等 UV 变换会被视为不安全并整体回退（白名单化）。
- 贴图/网格的**源资产永不被修改**；一切产物都写入 `Assets/ATO_Generated/`，取消构建时亦保留以便排查。

## 许可证

MIT（见仓库 LICENSE，如缺失以 MIT 条款默认适用）。

---

<a id="english"></a>

# Avatar Texture Optimizer (ATO)

An open-source NDMF texture optimizer for VRChat avatars.

Perceptual-quality-driven island/whole-texture downscaling (linear-space, alpha-premultiplied, MS-SSIM + CIEDE2000 P95 + cutout IoU / blend RMSE + normal-angle gates), 4px-granularity Burst raster BLF atlas packing with alias queues and cross-atlas co-location, mesh UV remapping (tangents never recomputed), pull-push bleed, material/texture dedup, animation clip retargeting — all without touching any material parameter other than texture references, with safe fallbacks everywhere.

- Install via VCC as a VPM package (needs VRChat Avatars SDK 3.10.4+, NDMF 1.14+), or copy into `Assets/`.
- Add **one** `Avatar Texture Optimizer` component on the object owning the VRCAvatarDescriptor; pick a quality tier (default Medium) and build/upload. Progress is cancelable; a summary lands in the NDMF console and `[ATO]` logs.
- Third-party: register custom shader analyzers (`ATOShaderAnalyzerRegistry`), pipeline hooks (`ATOEvents`/`ATOHookRegistry`), extra i18n languages (drop a JSON into `i18n/`).

Known divergences (documented honestly): quality evaluation & pull-push are Burst-accelerated CPU paths (GPU batch pipeline planned); identical opaque slots share one material after dedup but submesh topology merging is delegated to AAO MergeSkinnedMesh; NPOT pool is experimental; no NDMF preview support yet. See the Chinese section above for the full list.
