# NDMF 1.14.4 — 源码精读笔记

> 来源：`/home/user/_deps/ndmf`（精确版本 1.14.4）。本文档是取证结论，写代码时以本文为准。

## 1. 程序集
- `nadena.dev.ndmf`（Editor，主 API）——引用 VRC.SDKBase / VRC.SDK3A / VRC.SDK3A.Editor / VRC.SDKBase.Editor / Unity.Burst / Collections / Mathematics。
- `nadena.dev.ndmf.runtime`（运行时）——含 `RuntimeUtil`（如 `FindAvatarInParents`、`RelativePath`）。
- `nadena.dev.ndmf.vrchat`（Editor）——VRCSDK 集成，含 `VRChatContextExtensions`。define `VRCHAT_AVATARS_PRESENT`（versionDefines 由 com.vrchat.avatars 触发）。

## 2. Plugin 定义（Editor/API/Fluent/Plugin.cs）
```csharp
[assembly: ExportsPlugin(typeof(MyPlugin))]
public class MyPlugin : Plugin<MyPlugin> {
    protected override void Configure() {
        InPhase(BuildPhase.Optimizing).Run(...);
    }
}
```
- `Plugin<T>` 单例 `Instance`；`QualifiedName` 默认 = `typeof(T).FullName`（可 override 成包名）。
- `InPhase` 只能在 `Configure()` 内调用。

## 3. BuildPhase（Editor/API/Attributes/BuildPhase.cs）
顺序：`FirstChance → PlatformInit → Resolving → Generating → Transforming → Optimizing → PlatformFinish`。
- MA 主要在 Transforming；AAO 在 Optimizing。故「MA 后、AAO 前」= Optimizing 阶段 + `BeforePlugin("com.anatawa12.avatar-optimizer")`。

## 4. 依赖声明（Fluent）
- `Sequence` 级：`BeforePlugin(名字/类型)`、`AfterPlugin(名字/类型)`、`AfterPass(名字/类型)`、`WaitFor(typeof(T))`。
- `DeclaringPass` 级（`Run` 返回）：`BeforePlugin(名字)`、`BeforePass(名字/类型)`，用 `.Then.Run(...)` 链式。
- `Run<T>(T pass)`（`Pass<T>` 类型化）/ `Run(string displayName, InlinePass inlinePass)`（匿名）。
- **关键**：`BeforePlugin`/`AfterPlugin` 对缺失插件安全——`SolverContext.GetPluginPhases` 会为未知名字创建幽灵起止 pass（`InnatePhases`），永不执行。因此 `BeforePlugin("com.anatawa12.avatar-optimizer")` 在 AAO 未安装时也安全。
- **注意**：`AfterPass(string qualifiedName)` / `BeforePass(string qualifiedName)` 用 `Passes.Find(...)` 查找，**目标不存在会抛 NullReferenceException**；只有 `BeforePlugin/AfterPlugin(名字)` 是安全的。所以引用 AAO 的 pass 时用 plugin 名，不要用 pass 名。
- AAO 的 QualifiedName = `"com.anatawa12.avatar-optimizer"`（AAO 1.9.0 changelog 明示）。

## 5. BuildContext（Editor/API/BuildContext.cs）
关键成员：
- `ObjectRegistry ObjectRegistry`；`ErrorReport ErrorReport`；`GameObject AvatarRootObject`；`Transform AvatarRootTransform`。
- `Object AssetContainer`（资产容器）；`IAssetSaver AssetSaver`；`bool Successful`（无 ≥Error 级错误）。
- `GetState<T>()`（构建级共享状态，跨 pass）。
- `Extension<T>() where T:IExtensionContext`（扩展上下文，AAO 集成可能走这里）。
- `OpenSerializationScope()` / `Serialize()` / `IsTemporaryAsset(obj)`。
- `SetEnableUVDistributionRecalculation(Mesh, bool)` —— **重要**：NDMF 在构建末尾会对所有临时网格调用 `mesh.RecalculateUVDistributionMetrics()`（供 mip streaming 用）。若我自行对某通道算 UV 分布，需 `SetEnableUVDistributionRecalculation(mesh, false)` 退出。
- `DeactivateExtensionContext<T>()` / `ActivateExtensionContext<T>()`。

## 6. VRCAvatarDescriptor
- 类型：`VRC.SDK3.Avatars.Components.VRCAvatarDescriptor`（确认）。
- 获取：推荐 `context.AvatarRootObject.GetComponent<VRCAvatarDescriptor>()`；或 `nadena.dev.ndmf.vrchat` 的 `VRChatContextExtensions.VRChatAvatarDescriptor()` 扩展方法。
- `BuildContext.AvatarDescriptor` 属性已 `[Obsolete]`，勿用。

## 7. ObjectRegistry（Editor/API/ObjectRegistry.cs）
- 静态 `ObjectRegistry.RegisterReplacedObject(oldObj, newObj)`（用 ambient ActiveRegistry）：登记对象被替换/克隆，供错误溯源与动画引用解析。**替换材质/贴图/网格时务必调用**。
- `GetReference(obj)` / `TryRegisterReplacedObject(...)`。
- 注意：`RegisterReplacedObject` 必须在 `GetReference(newObject)` 之前调用，否则抛异常。

## 8. IAssetSaver（Editor/API/Serialization/IAssetSaver.cs）
- `SaveAsset(obj)`（立即保存；持久资产/null 则无操作）、`SaveAssets(IEnumerable)`（批量，包 AssetDatabase.Start/StopAssetEditing）。
- `IsTemporaryAsset(obj)`、`CurrentContainer`、`GetPersistedAssets()`。
- 贴图/网格等生成资产：用 `SaveAsset` 提前落盘（NDMF 末尾也会自动序列化临时资产）。

## 9. ErrorReport（Editor/ErrorReporting/）
- `ErrorReport.ReportError(IError error)`（静态，用 ambient 上下文）。
- `ErrorReport.ReportError(Localizer, ErrorSeverity, key, ...)`（带 NDMF 自带 i18n Localizer）。
- `ErrorReport.WithContextObject(obj, action)` 设上下文对象。
- 错误类型：`SimpleError`、`InlineError`、`StackTraceError`；`ErrorSeverity` 枚举。
- 我自实现 i18n，可用 `SimpleError` + 已本地化的字符串，或复用 NDMF `Localizer`。

## 10. 结论 / 对我架构的影响
1. ATOPlugin 用 `InPhase(BuildPhase.Optimizing).BeforePlugin("com.anatawa12.avatar-optimizer")` 注册，天然「MA 后、AAO 前」，且 AAO 缺失安全。
2. 分多个 `Pass<T>`（分析/缩放/装箱/应用/去重），便于进度与取消、也便于 `GetState<T>` 共享状态。
3. 替换材质/贴图/网格 → `ObjectRegistry.RegisterReplacedObject`。
4. 生成图集/网格 → `context.AssetSaver.SaveAsset`。
5. 重排 UV 后注意 `RecalculateUVDistributionMetrics`（必要时 opt-out 并自行计算正确通道）。
6. 报 warning/error 用 `ErrorReport.ReportError`，日志用 `[ATO]` 前缀 Debug.Log（构建时输出到控制台）。
