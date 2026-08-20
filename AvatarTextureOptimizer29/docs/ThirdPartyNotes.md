# 第三方库源码阅读笔记（已读通的关键 API 证据）

来源 zip 均下载解压于本地 libs/（不随包分发）。以下 API 均直接来自源码，非猜测。

## NDMF 1.14.4 (nadena.dev.ndmf)
- `Plugin<T>`: `Configure()` 内 `InPhase(BuildPhase.Optimizing)` 返回 Sequence。
- `Sequence.BeforePlugin(string)` / `AfterPlugin(string)` / `Run(Pass<T>.Instance)` / `Run(name, InlinePass)`；
  `WithRequiredExtension(Type, Action<Sequence>)` 声明扩展依赖（SolverPass._requiredExtensions）。
- `Pass<T>`: `protected abstract void Execute(BuildContext)`.
- `BuildContext`: `AvatarRootObject`, `AvatarRootTransform`, `AssetSaver`(IAssetSaver.SaveAsset/IsTemporaryAsset),
  `ObjectRegistry`, `GetState<T>()`, `Extension<T>()`, `IsTemporaryAsset(obj)`.
- `ErrorReport.ReportError(Localizer, ErrorSeverity, key, args)`; `ErrorSeverity.{Information,NonFatal,Error,InternalError}`;
  `ErrorReport.ReportException(e)`. Information 级也会进 NDMF 控制台报告。
- `Localizer`: `new Localizer("en", Func<List<(string lang, Func<string,string> lookup)>>)`;
  `nadena.dev.ndmf.localization.LanguagePrefs.Language`（如 "en-us"/"zh-hans"），`RegisterLanguage`。
- AnimatorServices (1.14): `AnimatorServicesContext : IExtensionContext`，属性 `ControllerContext`
  (VirtualControllerContext: `GetAllControllers()`, `Clone(RuntimeAnimatorController)`)、
  `AnimationIndex`、`ObjectPathRemapper`。`VirtualClip`: `GetFloatCurveBindings()/GetObjectCurveBindings()/
  GetObjectCurve(b)/SetObjectCurve(b, ObjectReferenceKeyframe[])/GetFloatCurve/SetFloatCurve/Clone()/Create(name)`。
  MA 全部经 `context.Extension<AnimatorServicesContext>()` 使用 → 直接改 AnimationClip 资产是错的，必须走 VirtualClip。
- BuildPhase: Resolving → Generating → Transforming → Optimizing → PlatformFinish。
- `ObjectRegistry.RegisterReplacedObject(old, new)`。

## AAO 1.9.17 (com.anatawa12.avatar-optimizer)
- OptimizerPlugin.QualifiedName == "com.anatawa12.avatar-optimizer"（我们 BeforePlugin 的目标）。
- `Anatawa12.AvatarOptimizer.API.UVUsageCompabilityAPI`（API-Editor/UVUsageCompabilityAPI.cs）:
  - `bool IsTexCoordUsed(SkinnedMeshRenderer, int channel)`
  - `void RegisterTexCoordEvacuation(SkinnedMeshRenderer, int originalChannel, int savedChannel)`
  - 实现通过给渲染器 GameObject 挂 `InternalEvacuateUVChannel` 组件，AAO 自己消费并清理。
  - 仅 SMR；MeshRenderer 无此 API。
- package.json 用 UPM "dependencies" 声明 com.unity.burst（VCC 会随 VPM 装包时带上）→ 我们照抄。
- asmdef 模式: overrideReferences + precompiledReferences: ["VRC.SDKBase.dll", "VRCSDK3A.dll", ...]，
  versionDefines 用表达式定义宏（我们: com.anatawa12.avatar-optimizer → AATO_AAO）。

## Modular Avatar 1.18.2
- QualifiedName: "nadena.dev.modular-avatar"（主）与 "nadena.dev.modular-avatar.late-transform-stages"。
- 动画读写全部走 AnimatorServicesContext（佐证我们的做法）。

## lilToon 2.3.4 (jp.lilxyzw.liltoon)
- Shader/lts.shader Properties 全量纹理属性已提取（见 Editor/Analysis/ShaderCatalog.cs）。
- `fd.uvMain = lilCalcDoubleSideUV(uv0, facing, _ShiftBackfaceUV)` →
  `lilCalcUVWithoutAnimation(uv, _MainTex_ST, _MainTex_ScrollRotate)`（先 *xy+zw 再旋转 z 弧度）。
- `LIL_SAMPLE_2D_ST(tex,samp,uv) = tex2D(tex, uv*tex##_ST.xy+tex##_ST.zw)`（贴图自身 ST）。
- `_Main2ndTex_UVMode/_Main3rdTex_UVMode`: 0=UV0 1=UV1 2=UV2 3=UV3 4=MatCap；
  `_Bump2ndMap_UVMode/_GlitterColorTex_UVMode/_AudioLinkMask_UVMode`: 0..3；
  `_EmissionMap_UVMode/_Emission2ndMap_UVMode`: 0..3, 4=Rim（视空间）。
- Decal 标志: `_Main2ndTexIsDecal/_Main3rdTexIsDecal` + `_Main2ndTexAngle/_Main3rdTexAngle`。
- MatCap(_MatCapTex/_MatCap2ndBumpMap...)、LUT(_Ramp/_EmissionGradTex/_MainGradationTex)、
  视差(_ParallaxMap)、闪烁(_GlitterShapeTex)、Dither、AudioLinkLocal → 非网格UV采样 → 白名单。
- _SmoothnessTex/_AlphaMask/_MainColorAdjustMask/_ShadowColorTex/_BacklightColorTex/_RimColorTex/
  _ReflectionColorTex 等均以 uvMain 采样（已从 Includes/lil_common_frag.hlsl 核实）。

## avatar-compressor 0.9.0 (Limitex)
- 生成贴图开 MipStreaming: `new SerializedObject(tex).FindProperty("m_StreamingMipmaps").boolValue = true`。
- 压缩: `EditorUtility.CompressTexture(tex, format, TextureCompressionQuality.Best)`；失败回退
  PC=DXT5 / Mobile=ASTC_6x6。
- **EditorUtility.CompressTexture 不做 DXTnm 通道转换**，必须手动预排列：
  BC5=RG, DXT5nm/BC7(默认)=AG(法线XY→A,G), BC7保alpha时=RGB；源布局由源格式判断。
  （Core/Services/NormalMapPreprocessor.cs，注释即证据）
- 引用替换后调用 `ObjectRegistry.RegisterReplacedObject`。

## VRChat SDK 3.10.4
- 以预编译 DLL 发行（VRCSDK3A.dll 等），无源码。使用稳定公共 API：
  `VRC.SDK3.Avatars.Components.VRCAvatarDescriptor`（baseAnimationLayers/specialAnimationLayers）、
  `VRC.SDKBase.IEditorOnly`。EditorOnly 为 Unity 内建 tag。
