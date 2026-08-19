# 第三方依赖源码取证笔记 / Third-party API Notes

> 本文件记录对依赖包源码的取证结论（2026-08-19，版本见下）。写代码前必须依据本笔记，
> 禁止凭猜测使用 API。后续若更新依赖版本需重新取证并更新本文件。
> All conclusions below were verified against the actual sources of the listed versions.

## NDMF 1.14.4 (nadena.dev.ndmf)

- 程序集：`nadena.dev.ndmf`（Editor/）、`nadena.dev.ndmf.runtime`（Runtime/）、`nadena.dev.ndmf.vrchat`（Editor/VRChat/）。
  `nadena.dev.ndmf.animator`、`nadena.dev.ndmf.localization`、`nadena.dev.ndmf.util` 等命名空间都在主程序集内。
- 插件注册：`[assembly: ExportsPlugin(typeof(MyPlugin))]` + `class MyPlugin : Plugin<MyPlugin>`。
  - `public override string QualifiedName`、`DisplayName`、`Color? ThemeColor`、`Texture2D LogoTexture`。
  - `protected override void Configure()` 内调用 `InPhase(BuildPhase.xxx)` → 返回 `Sequence`。
  - `Sequence`：`.Run(string displayName, InlinePass pass)`（`InlinePass = Action<BuildContext>`）、
    `.BeforePlugin(string qualifiedName)`、`.AfterPlugin(string)`、`.BeforePass(...)`、`.WithRequiredExtensions(...)`、`.OnPlatforms(...)`。
  - `protected override void OnUnhandledException(Exception e)` 处理未捕获异常。
  - `[RunsOnAllPlatforms]`（命名空间 `nadena.dev.ndmf`）放在插件类上。
- `BuildPhase`（`nadena.dev.ndmf`）：FirstChance、PlatformInit、Resolving、Generating、Transforming、Optimizing、PlatformFinish。
- `BuildContext` 公开成员：`ObjectRegistry`、`ErrorReport`、`AvatarRootObject`、`AvatarRootTransform`、
  `AssetContainer`、`AssetSaver`、`Successful`、`GetState<T>()`、`IsTemporaryAsset(obj)`、`Serialize()`、
  `ActivateExtensionContext<T>()`、`SetEnableUVDistributionRecalculation(Mesh, bool)`（改 UV 后应调用）。
- `ObjectRegistry`：`GetReference(UnityObject, bool create=true)` → `ObjectReference`；`RegisterReplacedObject(old, new)`。
- 错误报告：`ErrorReport`（`nadena.dev.ndmf`）静态方法 `ReportError(IError)`、`ReportException(Exception)`、
  `CaptureErrors(Action)`。`SimpleError`（抽象类）：实现 `Localizer`、`TitleKey`、`Severity`（ErrorSeverity），
  可 override `DetailsKey`（默认 TitleKey+":description"）、`TitleSubst`。
  `ErrorSeverity`：Information、NonFatal、Error、InternalError。
  `Localizer(string defaultLanguage, Func<List<(string, Func<string,string>)>> loader)`。
- 语言：`nadena.dev.ndmf.localization.LanguagePrefs.Language`（如 "en-us"、"zh-hans"、"ja-jp"），
  `LanguagePrefs.RegisterLanguage(string)`。
- 动画服务：`nadena.dev.ndmf.animator.AnimatorServicesContext`（IExtensionContext）：
  `ControllerContext`（CloneContext）、`AnimationIndex`、`ObjectPathRemapper`（`ReplaceObject(GameObject/Transform old, new)`）。
  `VirtualControllerContext` 提供按平台虚拟化控制器。
- 注：NDMF 内置 pass `RemoveEditorOnlyPass` 在 Resolving 相位运行（我们 Optimizing 前 EditorOnly 已被删除；仍做防御检查）。

## Modular Avatar 1.18.2 (nadena.dev.modular-avatar)

- 限定符：`nadena.dev.modular-avatar`（主插件，Resolving + Transforming + 一个 Optimizing 相位收尾 pass）、
  `nadena.dev.modular-avatar.late-transform-stages`（LateTransform，Transforming 相位）。
- 程序集：`nadena.dev.modular-avatar.core`（Runtime）、`nadena.dev.modular-avatar.core.editor`（Editor）等。
- 我们只依赖其限定符排序，不引用其 API。

## Avatar Optimizer (AAO) 1.9.17

- 限定符：`com.anatawa12.avatar-optimizer`；Resolving（FetchOriginalStatePass 等）+ 主流程在 Optimizing。
- `UVUsageCompabilityAPI`（注意 AAO 原文拼写 "Compability"）：命名空间 `Anatawa12.AvatarOptimizer.API`，
  程序集 `com.anatawa12.avatar-optimizer.api.editor`。
  - `static bool IsTexCoordUsed(SkinnedMeshRenderer renderer, int channel)`（channel 0~7）。
  - `static void RegisterTexCoordEvacuation(SkinnedMeshRenderer renderer, int originalChannel, int savedChannel)`
    （若 savedChannel 本身被 AAO 使用则抛 InvalidOperationException）。
  - 语义：AAO（如 Remove Mesh By Mask）会使用 UV 坐标；改 UV 的工具应先把原始 UV 备份到空闲通道并注册，
    AAO 处理时使用备份通道并在其流程结束后删除该通道。API 设计允许假阴性（返回 false = 不使用；true = 可能使用）。
  - 实现类 `UVUsageCompabilityAPIImpl` 在 `[InitializeOnLoadMethod]` 时设置 Impl，编辑器加载后即可用。
- 我们通过反射适配（不引用其程序集），AAO 未安装时自动降级。

## VRChat SDK 3.10.4 (com.vrchat.base / com.vrchat.avatars)

- SDK3A 运行时为预编译 DLL（`Runtime/VRCSDK/Plugins/VRCSDK3A.dll`）；无 C# 源码（只有 Editor 代码）。
- 程序集名：`VRC.SDK3A`、`VRC.SDK3A.Editor`、`VRC.SDKBase`（base 包）。
- `VRC.SDK3.Avatars.Components.VRCAvatarDescriptor`：
  - 序列化字段 `baseAnimationLayers`、`specialAnimationLayers`、`customizeAnimationLayers`（由 SDK Editor 代码确认）。
  - `VRCAvatarDescriptor.AnimLayerType`：Base、Additive、Gesture、Action、FX、Sitting、TPose、IKPose。
  - `CustomAnimLayer`：`type`、`isDefault`、`isEnabled`、`animatorController`、`mask`。
  - 默认层为 VRChat 内置控制器（无材质动画），动画扫描时跳过。

## lilToon 2.3.4 (jp.lilxyzw.liltoon)

- 属性声明在 `Shader/lts.shader` 的 Properties 块（属性列表已逐项取证，见 ShaderTextureTable.BuildLiltoonTable()）。
- 关键事实：
  - UV 采样贴图含 `_MainTex`、`_Main2ndTex`、`_Main3rdTex`、`_BumpMap`、`_Bump2ndMap`、`_AnisotropyTangentMap`、
    `_EmissionMap`、`_Emission2ndMap`、`_GlitterColorTex`、`_GlitterShapeTex`、`_OutlineTex`、`_OutlineVectorTex`、
    `_ParallaxMap`、`_BacklightColorTex`、`_BaseMap`、`_BaseColorMap` 及大量蒙版。
  - `_UVMode` 属性（Int）：0=UV0，1=UV1，2=UV2，3=UV3；Main2nd/Main3rd 的 4=MatCap，Emission 的 4=Rim（特殊用途）。
  - `_ScrollRotate` 属性（Vector）：(scrollX, scrollY, rotateDeg, strength)，任何非零分量即 UV 变换。
  - `[NoScaleOffset]` 属性 → 该贴图忽略 ST。
  - MatCap 组（`_MatCapTex` 等）使用 MatCap 球面 UV，非网格 UV。
  - 渐变/灯光值/屏幕空间贴图（`_MainGradationTex`、`_Ramp`、`_ShadowColorTex`、`_DitherTex`、`_AudioLinkLocalMap` 等）按值采样，非网格 UV。
  - 渲染模式由着色器变体名决定（lts / lts_cutout / lts_trans ...；lilShaderUtils 同源逻辑）；`_Cutoff` Range(-0.001,1.001) 默认 0.5；`_SubpassCutoff` Range(0,1) 默认 0.5。
- 我们仅用属性名字符串与解析逻辑，不引用 liltoon 程序集。

## Avatar Compressor 0.9.0 / Light Limit Changer 2.13.0

- 本阶段无需使用其 API（Avatar Compressor 的贴图压缩思路：构建时修改 TextureImporter 平台设置后 reimport，
  与本工具"导入设置优化"阶段一致，实现时注意 reimport 时机与等待）。
- Light Limit Changer 与本工具无 API 交集，未使用。

## 其他已确认的 Unity API 细节

- `Texture2D.imageContentsHash`（Hash128）为导入内容哈希，可用作去重快速路径。
- `TextureImporter.GetPlatformTextureSettings("Standalone"/"Android"/"iPhone")` 返回 `TextureImporterPlatformSettings`。
- `AnimationUtility.GetCurveBindings/GetObjectReferenceCurveBindings/GetObjectReferenceCurve/GetEditorCurve`。
- 材质属性动画绑定形式：path=渲染器路径，propertyName=`m_Materials.Array.data[i].prop[.x/.y/.z/.w]`（float）；
  材质槽切换为对象引用曲线（value=Material）；贴图属性动画为对象引用曲线（value=Texture2D）。
- 形态键绑定：propertyName=`blendShape.NAME`（type=SkinnedMeshRenderer）。
- `Material.enabledKeywords`（LocalKeyword[]）、`material.shaderKeywords`（string[]，兼容保留）。

## 依赖重新下载命令 / Re-download commands

```bash
mkdir -p /tmp/ato-deps && cd /tmp/ato-deps
curl -sSL -o com.vrchat.base-3.10.4.zip https://github.com/vrchat/packages/releases/download/3.10.4/com.vrchat.base-3.10.4.zip
curl -sSL -o com.vrchat.avatars-3.10.4.zip https://github.com/vrchat/packages/releases/download/3.10.4/com.vrchat.avatars-3.10.4.zip
curl -sSL -o nadena.dev.ndmf-1.14.4.zip https://github.com/bdunderscore/ndmf/releases/download/1.14.4/nadena.dev.ndmf-1.14.4.zip
curl -sSL -o nadena.dev.modular-avatar-1.18.2.zip https://github.com/bdunderscore/modular-avatar/releases/download/1.18.2/nadena.dev.modular-avatar-1.18.2.zip
curl -sSL -o com.anatawa12.avatar-optimizer-1.9.17.zip https://github.com/anatawa12/AvatarOptimizer/releases/download/v1.9.17/com.anatawa12.avatar-optimizer-1.9.17.zip
curl -sSL -o jp.lilxyzw.liltoon-2.3.4.zip https://github.com/lilxyzw/lilToon/releases/download/2.3.4/jp.lilxyzw.liltoon-2.3.4.zip
curl -sSL -o avatar-compressor-0.9.0.zip https://github.com/Limitex/avatar-compressor/releases/download/v0.9.0/avatar-compressor-0.9.0.zip
curl -sSL -o light-limit-changer.2.13.0.zip https://azukimochi.github.io/LLC-v2-vpm-repos/io.github.azukimochi.light-limit-changer.2.13.0.zip
for f in *.zip; do mkdir -p "${f%.zip}"; (cd "${f%.zip}" && unzip -q -o "../$f"); done
```
