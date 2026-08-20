# AvatarTextureOptimizer — 项目记忆 / Project Memory

> 本文件是本项目**唯一**的记忆载体。每次修改后必须更新。
> This file is the **only** memory store for this project. Update it after every change.

---

## 0. 一句话目标

一个开源的 NDMF 非破坏性 VRChat Avatar 贴图优化工具：建立 **网格 UV → 贴图** 的映射，
按感知质量目标缩放 UV 岛、剔除未使用 UV、重排并合并成形状感知图集，
全程只改**网格 UV 与贴图引用**，绝不改材质的任何其他着色器参数。

- 包名 / Package: `net.fosa.avatar-texture-optimizer`
- 程序集 / Assemblies: `net.fosa.ato.runtime`, `net.fosa.ato.editor`
- 命名空间 / Namespaces: `net.fosa.ato`, `net.fosa.ato.editor`
- 日志前缀 / Log prefix: `[ATO]`
- 图集名前缀 / Atlas prefix: `ATO_`

---

## 1. 已核实的第三方库事实（**不要再猜，以此为准**）

这些结论来自实际阅读 `_deps/` 下解包后的源码，不是推测。

| 事实 | 出处 |
|---|---|
| MA 主体在 `BuildPhase.Transforming`；AAO 主体在 `BuildPhase.Optimizing` | `AvatarOptimizer/Editor/OptimizerPlugin.cs:60`，`modular-avatar/Editor/PluginDefinition.cs:32` |
| AAO 插件限定名 `com.anatawa12.avatar-optimizer`；MA 为 `nadena.dev.modular-avatar` | 同上 |
| **我们的落位**：`InPhase(BuildPhase.Optimizing).Run(...).BeforePlugin("com.anatawa12.avatar-optimizer")`，MA 天然在前 | — |
| `UVUsageCompabilityAPI.IsTexCoordUsed(SkinnedMeshRenderer, int)` / `RegisterTexCoordEvacuation(SkinnedMeshRenderer, int, int)` — **只接受 SkinnedMeshRenderer**，MeshRenderer 无对应 API | `AvatarOptimizer/API-Editor/UVUsageCompabilityAPI.cs` |
| AAO 的 API asmdef `autoReferenced:false`，若直接在 asmdef 里引用、AAO 未安装会编译失败 → **必须用反射调用** | `AvatarOptimizer/API-Editor/*.asmdef` |
| NDMF `Localizer(string defaultLanguage, Func<List<(string, Func<string,string>)>> loader)` 可直接喂 JSON，不需要 LocalizationAsset | `ndmf/Editor/UI/Localization/Localizer.cs:40` |
| NDMF `LanguagePrefs.Language` 可读写，`RegisteredLanguages` 列出已注册语言 | `ndmf/Editor/UI/Localization/LanguagePrefs.cs` |
| NDMF `ErrorReport.ReportError(IError)` + `SimpleError` 抽象类（TitleKey/DetailsKey/HintKey 约定） | `ndmf/Editor/ErrorReporting/` |
| NDMF `BuildContext.AssetSaver` / `OpenSerializationScope()` 用于保存生成资产 | `ndmf/Editor/API/BuildContext.cs` |
| NDMF `AnimatorServicesContext` → `AnimationIndex`（`GetPPtrReferencedObjectsWithBinding`、`RewriteObjectCurves`、`EditClipsByBinding`）与 `VirtualClip`（`GetFloatCurveBindings` / `GetObjectCurveBindings`） | `ndmf/Editor/API/AnimatorServices/` |
| lilToon UV 变换信号有三类：`_ST`、`<Tex>_ScrollRotate`(Vector)、`<Tex>_UVMode`(Int, 0..3=UV0..UV3, 其余为 MatCap/Rim 程序化坐标) | `lilToon/Shader/lts.shader` |
| lilToon 用 `[NoScaleOffset]` 标记的贴图**没有 _ST**，该维度恒安全 | 同上 |

---

## 2. 与用户确认过的设计判断（含我提出的纠正）

1. **Mip 渗色**：图集 + mipmap 必然跨岛渗色，padding=边长/128 在高 mip 层不够。
   → 方案：**逐 mip 独立做 pull-push 外扩**（而非依赖 Unity 自动生成 mip）。**待用户确认**。
2. **贴图读取**：源贴图普遍 `isReadable=false` + Crunch。**绝不修改用户 importer**，
   统一走 `Graphics.Blit` → `RTFormat.ARGBFloat` → `AsyncGPUReadback`。已实现于 `GPUTextureIO`。
3. **材质槽合并**：仅当该网格所有槽都不被动画单独驱动时才合并，并把 `m_Materials.Array.data[i]` 索引重映射写回动画。
4. **烘焙耗时**：预计首次 1~2 分钟（大量岛的二分搜索）。**待用户确认可接受**。
5. **NPOT**：64px 步进天然是 4 的倍数，满足 BCn/ETC 块要求。mip 链由我们自己 GPU 降采样生成，避免 NPOT floor 舍入不一致。
6. **AAO 兼容**：反射调用 `UVUsageCompabilityAPI`，未安装则跳过并 warning。仅对 SkinnedMeshRenderer 生效。

---

## 3. 目录结构（现状 — 全部已实现）

```
Packages/net.fosa.avatar-texture-optimizer/
├── package.json / README.md / CHANGELOG.md / LICENSE
├── Localization/{en.json, zh-Hans.json}              79 keys each
├── Shaders/ATO_Decode.shader                          色彩空间感知 blit
├── Runtime/  (5 files)
│   ├── ATOConstants.cs  ATOEnums.cs
│   ├── QualityProfile.cs      挡位阈值 + 学术依据注释
│   ├── PlatformProfile.cs     平台配置 / 输出设置
│   └── AvatarTextureOptimizer.cs   组件本体 (VRC.SDKBase.IEditorOnly)
└── Editor/  (33 files, ~7100 lines)
    ├── Core/        ATOLog.cs (计时树/分级日志)  ATOCancellation.cs (进度条+取消)
    ├── Localization/ATOLocalizer.cs  (自研扁平 JSON 解析，不依赖 Newtonsoft)
    ├── Model/       ATOModel.cs  (AtoTexture / UVGroup / UVIsland / TextureUsage / MeshBinding)
    ├── Analysis/    ShaderAnalyzer / WhitelistResolver / AnimationAnalyzer /
    │                RendererCollector / UVGroupBuilder
    ├── Textures/    GPUTextureIO (Blit+AsyncGPUReadback+LRU) / TextureDeduplicator
    ├── Meshes/      UVIslandBuilder (并查集/重叠合并/wrap 归一/形态键面积)
    ├── Quality/     ImageOps / QualityMetrics / IslandScaleSolver
    ├── Packing/     RasterMask / AtlasCandidatePool / ShapePacker
    ├── Atlas/       AtlasCompositor / PullPush / AtlasPipeline / WholeTexturePipeline
    ├── Apply/       MeshRewriter / MaterialRewriter / TextureOutput / PostDeduplicator / AAOCompat
    ├── Plugin/      ATOPlugin / ATOPass / ATOErrors
    ├── Report/      ATOReport
    ├── UI/          ATOComponentEditor
    └── API/         ATOExtensionAPI (IShaderSupportProvider / IATOBuildObserver)
```

---

## 4. 关键实现决策（**改代码前必读**）

1. **贴图读取全部走 GPU**：`Graphics.Blit` → `ARGBFloat RT` → `AsyncGPUReadback`。
   绝不修改用户的 `TextureImporter`，也绝不调用 `GetPixels`（源贴图普遍 Crunch + 不可读）。
2. **度量在 CPU 多核跑，不在 GPU**：岛很小（几千纹素），二分搜索会发出上万次微小比较，
   每次 dispatch+回读会被延迟主导且结果随驱动而变。确定性在这里很重要。
   GPU 只用于解码。（`QualityMetrics` 顶部注释有完整论证。）
3. **mip 层数上限 = `log2(padding)+1`**（用户选定方案）。mip 链由 ATO 自己用
   感知 alpha 的盒式滤波生成，不用 Unity 自动生成。见 `AtlasCompositor.MipCountFor`。
4. **padding 全程用同一个值**（依最大候选图集推导），否则装箱器换尺寸时掩码失效。
   掩码在 `AtlasPipeline.BuildMasks` 里预膨胀 padCells，`ShapePacker` 放置后内缩还原真实矩形。
5. **旋转 90° = 掩码转置 + 网格 U/V 交换**，永不重算切线。
6. **UV 组按 (Mesh, subMesh, uvChannel) 建键**；`MeshRewriter` 也按 **(Mesh, channel)** 分组，
   不能按渲染器分组——两个渲染器可能共享同一网格资产，按渲染器会互相覆盖布局。
7. **AAO 用反射调用**（API 程序集 `autoReferenced:false`），只接受 `SkinnedMeshRenderer`。
8. **`TextureOutput.Apply` 保留 CPU 副本**（`Apply(false,false)`），因为输出去重要
   `GetRawTextureData`。统一在 `ATOPass.ReleaseCpuCopies` 里最后释放。
9. **`WholeTexturePipeline` 会跳过已在 remap 中的贴图**，避免图集与整图缩放互相覆盖。
10. **`ShaderAnalyzer.IsDeformingProperty` 是拒绝列表**，误判只损失优化机会，不损害正确性。

---

## 5. 质量挡位参数（已定，依据见 `QualityProfile.cs` 注释）

| 挡位 | targetQuality | MS-SSIM≥ | dE00 mean≤ | dE00 p95≤ | Cutout IoU≥ | Blend αRMSE≤ | 法线 mean≤ | 法线 p95≤ | 灰度 RMSE≤ |
|---|---|---|---|---|---|---|---|---|---|
| Lossless | 1.00 | 全跳过 | — | — | — | — | — | — | — |
| VeryHigh | 0.95 | 0.995 | 1.0 | 2.0 | 0.999 | 0.004 | 1.0° | 2.0° | 0.005 |
| **High(默认)** | 0.85 | 0.99 | 2.0 | 4.0 | 0.997 | 0.008 | 2.0° | 4.0° | 0.010 |
| Medium | 0.70 | 0.98 | 3.0 | 6.0 | 0.99 | 0.016 | 3.5° | 7.0° | 0.020 |
| Low | 0.50 | 0.96 | 5.0 | 10.0 | 0.98 | 0.030 | 5.0° | 10.0° | 0.040 |
| Custom | 用户改，默认=Lossless，永不被其他挡位覆盖 |

二分搜索：均匀 8 次 + 双轴各 6 次（用户选择"质量优先，可接受 1~3 分钟"）。

---

## 6. 当前状态

**v0.1.0 全部功能已实现并提交。** 尚未在真实 Unity 工程中编译验证——
下一步需要用户同步到工程内烘焙，把编译错误 / 实际表现反馈回来。

### 已知待验证点（首次烘焙时重点看）
- `Hash128.Append(NativeArray<Color>)` 的重载在目标 Unity 版本是否存在。
- `EditorUtility.CompressTexture` 对自建 mip 链的贴图是否保留 mip。
- NPOT + Crunch 在目标平台的实际可用性。
- 大型 Avatar 上 `IslandScaleSolver` 的实际耗时。

### 尚未做的
- NDMF 预览（需求明确说暂不支持）。
- 单元测试。
- 单元测试与 NDMF 预览之外，功能已闭环；`ShaderAnalyzer` 已接入 `ATOExtensionRegistry.TryDescribe`，
  第三方 provider 的判定优先于内置启发式。

---

## 7. 每次改动后的固定动作

1. 先读相关源码再动手，不靠猜。
2. 注释必须英文 + 中文双语。
3. `git commit` + 更新本文件。
