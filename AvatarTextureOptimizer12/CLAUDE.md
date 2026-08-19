# CLAUDE.md — AvatarTextureOptimizer 项目记忆

> 本文件是本项目**唯一**的记忆载体。任何关于本项目的计划、决策、进度、坑，都只记录在这里。

---

## 1. 项目基本信息

| 项 | 值 |
|---|---|
| 项目名 | AvatarTextureOptimizer (ATO) |
| 包名 | `net.fosa.avatar-texture-optimizer` |
| 目标 | 全世界最好的 VRChat 贴图优化工具（开源 NDMF 工具） |
| 语言 | C# / Unity 2022.3 / NDMF 1.14.4 |
| 交流语言 | 简体中文；代码注释中英双语 |
| 工程状态 | **非完整 Unity 工程**，用户手动同步到工程内验证 |

## 2. AgentTeam 组织与流程（本项目强制流程）

| 角色 | 职责 |
|---|---|
| Coder A / Coder B | 每次写代码前互相交流，达成共识后再落实 |
| Reviewer A / Reviewer B | 每次 Coder 写完任何代码后共同审查，共识后决定是否打回 |
| QA A / QA B | 整体完成 + Reviewer 验收后，各自**从头完整通读全部代码**，共同判定；任一不通过即打回 Coder 与 Reviewer |

流程记录见第 7 节「评审记录」。

## 3. 依赖库（已下载并通读关键 API，禁止臆测）

位于 `_refs/`（不随包交付）：

- `com.vrchat.base 3.10.4`, `com.vrchat.avatars 3.10.4`
- `nadena.dev.ndmf 1.14.4`
- `nadena.dev.modular-avatar 1.18.2`
- `com.anatawa12.avatar-optimizer 1.9.17`
- `jp.lilxyzw.liltoon 2.3.4`
- `avatar-compressor 0.9.0`
- `io.github.azukimochi.light-limit-changer 2.13.0`

### 3.1 已取证的关键 API 事实

- **NDMF 插件**：`[assembly: ExportsPlugin(typeof(T))]` + `Plugin<T>`；`Pass<T>` 的 `protected abstract void Execute(BuildContext)`。
- **阶段顺序**：MA 在 `BuildPhase.Transforming`；AAO 的 `QualifiedName == "com.anatawa12.avatar-optimizer"`，主体在 `BuildPhase.Optimizing`。
  → ATO 放在 `Optimizing` 并 `.BeforePlugin("com.anatawa12.avatar-optimizer")`，天然满足「ma 之后、AAO 之前」。
- **`Sequence.WithRequiredExtension(Type, Action<Sequence>)`** 存在（`API/Fluent/Sequence/Extensions.cs:153`）。
- **动画**：`AnimatorServicesContext` → `ControllerContext.GetAllControllers()` → `VirtualNode.AllReachableNodes()` 可枚举全部 `VirtualClip`；
  `VirtualClip.GetFloatCurveBindings/GetObjectCurveBindings/GetFloatCurve/GetObjectCurve`；
  `AnimationIndex.RewriteObjectCurves(Func<Object,Object>)` 可批量重写 PPtr 曲线。
- **资产保存**：`ctx.AssetSaver.SaveAsset(obj)`（`IAssetSaver`）。
- **报错**：`ErrorReport.ReportError(IError)`；`SimpleError` 抽象类，`CreateVisualElement` 可自定义 UI（用于折叠式报告）。
- **本地化**：`nadena.dev.ndmf.localization.Localizer`（loader 回调形式）+ `LanguagePrefs.Language`。
- **AAO API**：`Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI`（原文拼写）
  - `bool IsTexCoordUsed(SkinnedMeshRenderer, int channel)`
  - `void RegisterTexCoordEvacuation(SkinnedMeshRenderer, int originalChannel, int savedChannel)`
  - **仅支持 SkinnedMeshRenderer**；其 asmdef `autoReferenced: false`
  - → **必须用反射调用**（用户可能未安装 AAO）。已实现于 `Editor/Apply/ApplyStage.cs` 的 `AAOBridge`。
- **lilToon 属性约定**（来自 `Editor/lilInspector/lilMaterialProperties.cs` 与 AAO 的 `ShaderInformation.Liltoon.cs`）：
  `_X_UVMode`(0..3 = UV0..UV3, 4 = 非网格 UV)、`_X_ScrollRotate`、`_XAngle`、`_XIsDecal`、`_XIsMSDF`、`_XShouldCopy/FlipMirror/FlipCopy`、`_MainTex_ST`。
  → 通用分析器已按这些约定实现，不硬编码着色器名，未来版本可自动兼容；无法证明安全的一律按白名单跳过并 warning。
- **着色器通用反射**：`Shader.GetPropertyCount/GetPropertyName/GetPropertyType/GetPropertyFlags/GetPropertyTextureDimension`；
  `ShaderPropertyFlags.MainTexture / Normal / NoScaleOffset` 是权威来源。

## 4. 可行性结论

**整体可行**。已核对的风险点与处理方式：

| 风险 | 结论 / 处理 |
|---|---|
| 无法读取压缩/不可读贴图 | GPU `Graphics.Blit` → `ReadPixels`，且让 RT 的 sRGB 属性与源一致，拿到的就是「存储字节」，之后自己做线性化，色彩空间无歧义 |
| 图集化破坏 AAO 的 UV 用途 | 反射调用 AAO `UVUsageCompabilityAPI` 做 UV 迁移；无 AAO 时无害跳过 |
| 跨 wrap 缝 UV | 判定「岛是否完整位于同一整数瓦片内」，是则整数平移归一；否则整条 UV 流按白名单跳过 + warning |
| 材质槽合并改变子网格索引 | **当前只检测并报告，不实际合并**（见「已知限制」），避免破坏动画绑定 |
| 内存爆炸 | 解码像素 LRU 缓存（默认 768MB 软预算）+ `TextureIntrospection.ReleaseAll()` 在 finally 中兜底 |
| 形态键组合爆炸 | 只取每个形态键 0/100 的**逐顶点最远包络**，是面积上界，不做 2^n 组合 |

## 5. 代码结构

```
AvatarTextureOptimizer/
  package.json
  README.md  CLAUDE.md
  Runtime/
    ATOEnums.cs                  平台/挡位/分类/格式/padding/密度 枚举
    ATOSettings.cs               设置模型 + 各挡位默认参数（含学术依据）
    AvatarTextureOptimizer.cs    组件（INDMFEditorOnly, DisallowMultipleComponent）
  Editor/
    Core/ATOLog.cs               [ATO] 日志 + 阶段耗时表
    Core/ATOProgress.cs          可取消进度（ATOCancelledException）
    Core/ATOErrors.cs            ATOError / ATOReportUtil
    Localization/ATOMiniJson.cs  无依赖 JSON 解析（支持嵌套扁平化 + // 注释）
    Localization/ATOL.cs         扫描 ato-lang.<code>.json，用户可扩展
    Localization/ato-lang.en.json, ato-lang.zh-CN.json
    Analysis/ShaderAnalysis.cs   通用属性表分析 + lilToon 约定 + 拒绝原因
    Analysis/TextureIntrospection.cs  解码/缓存/内容分析/去重键/分类/sRGB LUT
    Analysis/AvatarScan.cs       动画扫描、渲染器收集、白名单展开
    Analysis/UsageGraph.cs       去重 + UV↔贴图关系图 + UV 组 + 类型组 + MaterialAlpha
    MeshOps/UVIslands.cs         岛提取/归一化/Burst 保守光栅化/重叠合并/RasterMask
    MeshOps/MeshMetrics.cs       世界面积（形态键包络 + 动画缩放最大值）
    Quality/QualityMetrics.cs    LinearImage / MS-SSIM / CIEDE2000 / IoU / RMSE / 角度误差
    Quality/IslandScaler.cs      NormalCodec / 岛提取 / 二分搜索 / UV 组木桶效应
    Atlas/AtlasPacker.cs         候选池 / padding / BLF 全扫描 / 90° 转置 / 队列装箱
    Atlas/AtlasBaker.cs          图集烘焙 / UV 重映射 / PullPush 无限外扩
    Atlas/TextureOutput.cs       格式解析 + 安全 fallback + Crunch/NPOT + 显存估算
    Apply/ApplyStage.cs          AAOBridge / 网格重写 / 材质与动画重写 / 材质去重
    Plugin/ATOPlugin.cs          插件定义（Optimizing, BeforePlugin AAO）
    Plugin/ATOMainPass.cs        主 Pass 编排
    Plugin/ATOStages.cs          IslandStage / QualityStage / PackStage / BakeStage
    Plugin/ATOBuildReport.cs     NDMF 控制台报告（总览 + 折叠详情）
    UI/ATOComponentEditor.cs     面板（折叠、平台覆盖、白名单、调试）
    API/ATOExtensionAPI.cs       IATOShaderProvider / IATOPackingStrategy / IATOBuildHook
```

## 6. 质量挡位默认值依据

- MS-SSIM：≈0.99+ 普遍报告为「视觉无损」，≈0.95 为「良好」。
- CIEDE2000：ΔE00 ≤ 1 为 1 个 JND；≤ 2「仔细看才察觉」；≤ 3.5 经典印刷可接受上限。
- MS-SSIM 五尺度需要 11 × 2⁴ = **176px** —— 与需求中的 176px 阈值一致（互相印证）。
- 挡位：Draft / Performance / **Balanced（默认）** / High / Lossless / Custom。
  Custom 默认等于 Lossless 参数，**独立字段 `customQuality`，切换挡位不会被覆盖**。

## 7. 评审记录

### 第 1 轮

**Coder 共识**
- 单 Pass 编排：原生缓存生命周期可控，取消时可确定性释放。
- 反射调用 AAO，不在 asmdef 里硬引用（AAO asmdef `autoReferenced:false`，硬引用会在未安装时编译失败）。

**Reviewer 共识 —— 打回 3 项**
1. `MultiScaleSsim` 的 `ref w/h` 写法有误 → 重写并加权重归一化。
2. `ScanAnimations` 残留占位循环 → 改用 `AllReachableNodes()` + `visited` 去重。
3. `ATOReportError` 依赖 `Resources.GetBuiltinResource<Font>` → 版本兼容风险，移除。

**QA 判定** —— 不通过，交回 Coder：功能清单未完成（GPU 路径、材质槽合并、扩展接口、贴图去重四项缺失）。

### 第 2 轮

**Coder 共识**
- GPU 走 compute + StructuredBuffer，而不是 RenderTexture：指标计算是纯数据并行，不需要光栅化管线；
  且扁平 buffer 免去格式协商，读回路径唯一。
- 每个 GPU 入口都必须有 CPU 参考实现并可回退，任一 dispatch 抛异常则本次构建整体降级到 CPU
  （`GpuImageOps._disabled`），避免半 GPU 半 CPU 造成结果不一致。
- 材质槽合并必须有「安全门」：动画驱动 / 同物体存在按索引寻址材质槽的组件 / 子网格数与材质数不符 /
  非三角形拓扑 —— 任一条成立即拒绝合并。

**Reviewer 共识 —— 打回 4 项（均为真 bug）**
1. **padding 偏移符号错误**：`Dilate()` 把岛内容内移了 `+padCells`，
   原代码却把放置原点 `-= padCells`，会让贴图向左下越界（在 x=0 处直接产生负坐标）。→ 改为 `+=`。
2. **UV 组共享岛导致跨图集位置被覆盖**（架构级）：`IslandStage` 让同一 UV 流的所有贴图共享同一个
   `UVIsland` 对象，而 `AtlasOrigin` / `AtlasIndex` 也存在该对象上。
   于是主色图集与法线图集分别装箱时，后者会覆盖前者的位置，
   直接违反「同一个 UV 在不同图集上的位置必须相同」这条硬性要求。
   → **重构装箱模型为「图集族」**：装箱的原子单位从「贴图」改为「UV 组」，
   一次装箱产生一个族，族内按层各输出一张图集，尺寸与岛位置完全一致。
   `AtlasIndex` 从 `UVIsland` 移到 `IslandPlan`；`Scale` 改为岛上的 `TargetWidth/TargetHeight`。
3. **动画切换的同角色贴图会被塞进同一图集位置**：它们是互斥备选而非配套贴图，后者会覆盖前者。
   → 层键加变体编号（`role#1`、`role#2`…），每个变体输出为独立的平行图集。
4. **占位大于源矩形时仍走 box 下采样**：UV 组木桶效应会让低分辨率成员的占位大于自己的源矩形。
   → 按方向选择 `Downsample` / `UpsampleTo`。

另修：岛被重复光栅化（每张贴图一次 → 每个岛一次）、`IslandCount` 统计重复计数。

**QA 判定（各自从头通读全部代码）**
- QA A：功能清单逐条核对，§8 原 1–7 项中的 2–7 全部落地；静态检查（括号配对、
  中英 i18n 键完全对齐 78/78、代码中引用的 `ATO:` 键全部存在、Linq using 完备、无陈旧引用）通过。
- QA B：重点复核第 2 轮 4 项打回的修复，确认旋转映射 `dst(x,y)=src(y,H-1-x)` 与
  `RemapUv` 的逆映射自洽；确认 GPU / CPU 两条路径计算的是同一组量。
- **一致结论：代码层面通过。唯一未完成项是「在 Unity 内编译 + 实机烘焙」——
  本环境没有 Unity 也没有 C# 编译器，这一步只能由用户执行。**

## 8. 功能完成度

§8 原有的 1–7 项待办已全部完成：

| 原编号 | 内容 | 状态 |
|---|---|---|
| 2 | 材质槽合并实际执行 | ✅ `Editor/Apply/SubMeshMerger.cs`，含 4 道安全门与动画绑定重编号 |
| 3 | GPU 质量评估 | ✅ `Editor/Shaders/ATOImageOps.compute`（11 个 kernel）+ `GpuImageOps` |
| 4 | GPU pull-push | ✅ `Editor/Shaders/ATOPullPush.compute`（pull/push/resolve） |
| 5 | 扩展接口接入主流程 | ✅ ShaderProvider 在 `ShaderAnalysis`，Hook 在 `ATOMainPass`，PackingStrategy 在 `PackStage` |
| 6 | `dedupTextures` 后置去重 | ✅ `ApplyStage.DeduplicateTextures`，按尺寸+格式+mip/filter/wrap/aniso+原始字节哈希 |
| 7 | NPOT 与 Crunch | ✅ 自动剔除 Crunch 并 warning |
| 8 | ndmf 预览 | 按需求不实现 |

**剩余唯一未完成项：第 1 项「在 Unity 中编译并实机烘焙验证」。**
本沙箱内没有 Unity、没有 dotnet/mono，无法执行。已做的替代验证：括号配平、
命名空间与 using 完备性、跨文件符号引用一致性、i18n 键双向对齐、
所有第三方 API 调用均能在 `_refs/` 源码中指出出处。

## 9. GPU / CPU 双路径约定

- 每个 GPU 入口形如 `TryXxx(...) -> bool`，返回 false 时调用方走 CPU 参考实现。
- 小于 `GpuImageOps.GpuThresholdPixels`（64×64）时直接走 CPU，避免 dispatch 开销超过计算本身。
- 任一 dispatch 抛异常 → `_disabled = true`，本次构建剩余部分全部降级到 CPU，
  由 `ResetForNewBuild()` 在下次构建开始时复位。
- `GpuImageOps.ForceCpu` 供排查问题时强制 CPU。
- Buffer 采用按 2 的幂分桶的池化复用，`ReleaseAll()` 在主 Pass 的 finally 中兜底。

## 10. 下一步

1. 用户同步进 Unity 工程 → 反馈编译错误 → Coder 修 → Reviewer → QA（第 3 轮）。
2. 实机烘焙验证重点：
   - UV 重映射是否正确（特别是旋转过的岛）；
   - 主色图集与配套法线/蒙版图集的岛位置是否严格一致；
   - 动画切换材质/贴图后表现是否与优化前一致；
   - 材质槽合并后的 Draw Call 与动画是否正常；
   - GPU 与 CPU 两条路径产出的图集是否肉眼一致。
