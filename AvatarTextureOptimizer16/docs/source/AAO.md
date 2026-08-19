# AAO (Avatar Optimizer) 1.9.17 — 源码精读笔记

> 来源：`/home/user/_deps/aao`（精确版本 1.9.17）。

## 1. 程序集
- `com.anatawa12.avatar-optimizer.api.editor`（**公开 API**，含 UVUsageCompabilityAPI、ShaderInformation、MeshRemovalProvider）——集成优先引用此程序集。
- `com.anatawa12.avatar-optimizer.editor`（实现，含 OptimizerPlugin、各 Processor）。
- 其余为 internal 子程序集（meshinfo2、animator-optimizer、localization 等）。

## 2. 插件与 pass 顺序（Editor/OptimizerPlugin.cs）
- `QualifiedName = "com.anatawa12.avatar-optimizer"`（**确认**）。
- AAO 主要在 `BuildPhase.Optimizing` 运行；另有少量 Resolving（`FetchOriginalStatePass` 等）。
- Optimizing 阶段 mainSequence 的关键 pass 顺序（与我相关的）：
  `Validation → LoadTraceAndOptimizeConfiguration → OptimizationWarnings → DuplicateAssets → ParseAnimator → GatherShaderMaterialInformation → ... → MaxTextureSizeProcessor → EditSkinnedMeshComponentProcessor → RemoveMeshByMask/ByUVTile/ByBlendShape/InBox（EditSkinnedMeshComponentProcessor 内） → AutoMergeSkinnedMesh → MergeMaterialSlots → RemoveUnusedMaterialProperties → RemoveUnusedMaterialTextures → OptimizeTexture → AnimatorOptimizer 系列 → LogOptimizationMetricsAfter`
- 结论：我 `InPhase(Optimizing).BeforePlugin("com.anatawa12.avatar-optimizer")` 即「MA 后、AAO 前」，AAO 缺失安全（NDMF 幽灵 pass）。
- **注意**：AAO 的 `MaxTextureSizeProcessor` / `TraceAndOptimize` 默认会限制贴图最大尺寸，会作用在我生成的图集上（它在我之后跑）。这是已知的兼容性叠加，需在文档中说明。

## 3. UVUsageCompabilityAPI（API-Editor/UVUsageCompabilityAPI.cs）★核心兼容点
命名空间 `Anatawa12.AvatarOptimizer.API`，静态类，引入于 AAO 1.8.0。
- `bool IsTexCoordUsed(SkinnedMeshRenderer renderer, int channel)`：该通道是否会被 AAO 用于优化（如 RemoveMeshByMask 用 UV0、RemoveMeshByUVTile 用各槽 uvChannel）。
- `void RegisterTexCoordEvacuation(SkinnedMeshRenderer renderer, int originalChannel, int savedChannel)`：登记「原通道已疏散到 savedChannel」。
  - savedChannel 若被 AAO 使用会抛 `InvalidOperationException`。
  - 内部会给 renderer 挂 `InternalEvacuateUVChannel` 组件记录疏散关系，AAO 处理时用疏散通道、事后移除。
- **我必须做的**：改写某 renderer 的 UV 通道前，若 `IsTexCoordUsed` 为真，选一个空闲高通道（如 UV7），把原始 UV 拷过去，再 `RegisterTexCoordEvacuation(renderer, channel, spare)`，然后才改写原通道。这样 AAO 的 RemoveMeshByMask 等仍用原始 UV，不会因我重排 UV 而错删。
- **AAO 可选时的调用方式**：反射调用（目标类型 `Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI`，程序集 `com.anatawa12.avatar-optimizer.api.editor`），避免编译期硬依赖。

## 4. ShaderInformation API（API-Editor/ShaderInformation.cs）
- `ShaderInformationRegistry.RegisterShaderInformation(shader, info)` / `RegisterShaderInformationWithGUID(guid, info)`（`InitializeOnLoad` 时注册）。
- `ShaderInformation`（抽象）：`SupportedInformationKind`（TextureAndUVUsage / VertexIndexUsage）+ `GetMaterialInformation(MaterialInformationCallback)`。
- `MaterialInformationCallback`（**动画感知的属性读取，极有用**）：
  - `GetInt/GetInteger/GetFloat/GetVector(propertyName, considerAnimation=true)` —— 属性被动画修改时返回 null。
  - `IsShaderKeywordEnabled(keywordName)` —— 本地关键字状态（bool?）。
  - `RegisterOtherUVUsage(UsingUVChannels)` —— 声明「该 UV 通道 AAO 不要动」。
  - `RegisterTextureUVUsage(texturePropName, samplerState, uvChannels, uvMatrix)` —— 声明贴图+UV 用途（AAO 可能图集化）。
  - `RegisterVertexIndexUsage()`。
- `UsingUVChannels` 枚举（UV0..UV7、NonMesh、Unknown）；`SamplerStateInformation`（含 Point/Linear/Trilinear × Clamp/Repeat/Mirror/MirrorOnce 预设 + 隐式 string 转换）；`Matrix2x3`（2x3 仿射，含 Scale/Translate/Rotate，用于 UV 矩阵与 _ST）。
- **我的用途**：我的着色器分析可复用 `GetMaterialInformation` 的动画感知读取（若 AAO 存在则反射调用，作为我自身动画分析的补充/交叉验证）。但主要分析仍自实现（AAO 可选）。

## 5. MeshRemovalProvider（API-Editor/MeshRemovalProvider.cs）
- `MeshRemovalProvider.GetForRenderer(renderer)`：预测哪些三角形会被 AAO 移除（无假阳性，即可能「保留」被误报为「保留」）。
- 可用于：装箱前剔除将被移除的岛（省空间）。可选优化，非必需。

## 6. 结论 / 对我架构的影响
1. 用 `BeforePlugin("com.anatawa12.avatar-optimizer")`，AAO 缺失安全。
2. **必须**在重排 UV 前做 `UVUsageCompabilityAPI` 疏散（反射），否则 RemoveMeshByMask/ByUVTile 会错删三角形——这是与 AAO 兼容的核心正确性要求。
3. 我的着色器分析可与 AAO 的 ShaderInformation/MaterialInformationCallback 交叉验证（反射，可选）。
4. 注意 AAO MaxTextureSize 会在我之后叠加作用于图集；文档说明。
