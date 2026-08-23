using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Fosa.AvatarTextureOptimizer.Editor.Analysis
{
    internal sealed class ShaderTextureAnalyzer
    {
        private enum ShaderFamily
        {
            Unknown,
            UnityStandard,
            VrcDiffuse,
            VrcBumped,
            VrcStandardLite,
            VrcToonLit,
            VrcToonStandard,
            LilToon
        }

        private static readonly HashSet<string> VerifiedLilToonShaders = new HashSet<string>(StringComparer.Ordinal)
        {
            "lilToon", "_lil/lilToonMulti",
            "Hidden/lilToonCutout", "Hidden/lilToonCutoutOutline", "Hidden/lilToonFur",
            "Hidden/lilToonFurCutout", "Hidden/lilToonFurTwoPass", "Hidden/lilToonGem",
            "Hidden/lilToonOutline", "Hidden/lilToonOnePassTransparent", "Hidden/lilToonOnePassTransparentOutline",
            "Hidden/lilToonRefraction", "Hidden/lilToonRefractionBlur", "Hidden/lilToonTessellation",
            "Hidden/lilToonTessellationCutout", "Hidden/lilToonTessellationCutoutOutline",
            "Hidden/lilToonTessellationOutline", "Hidden/lilToonTessellationOnePassTransparent",
            "Hidden/lilToonTessellationOnePassTransparentOutline", "Hidden/lilToonTessellationTransparent",
            "Hidden/lilToonTessellationTransparentOutline", "Hidden/lilToonTessellationTwoPassTransparent",
            "Hidden/lilToonTessellationTwoPassTransparentOutline", "Hidden/lilToonTransparent",
            "Hidden/lilToonTransparentOutline", "Hidden/lilToonTwoPassTransparent",
            "Hidden/lilToonTwoPassTransparentOutline", "Hidden/lilToonLite", "Hidden/lilToonLiteCutout",
            "Hidden/lilToonLiteCutoutOutline", "Hidden/lilToonLiteOutline", "Hidden/lilToonLiteOnePassTransparent",
            "Hidden/lilToonLiteOnePassTransparentOutline", "Hidden/lilToonLiteTransparent",
            "Hidden/lilToonLiteTransparentOutline", "Hidden/lilToonLiteTwoPassTransparent",
            "Hidden/lilToonLiteTwoPassTransparentOutline", "Hidden/lilToonMultiFur", "Hidden/lilToonMultiGem",
            "Hidden/lilToonMultiOutline", "Hidden/lilToonMultiRefraction",
            "_lil/[Optional] lilToonOutlineOnlyCutout", "_lil/[Optional] lilToonFakeShadow",
            "_lil/[Optional] lilToonFurOnlyTransparent", "_lil/[Optional] lilToonFurOnlyCutout",
            "_lil/[Optional] lilToonFurOnlyTwoPass", "_lil/[Optional] lilToonOutlineOnly",
            "_lil/[Optional] lilToonOverlay", "_lil/[Optional] lilToonOverlayOnePass",
            "_lil/[Optional] lilToonOutlineOnlyTransparent", "_lil/[Optional] lilToonLiteOverlay",
            "_lil/[Optional] lilToonLiteOverlayOnePass"
        };

        // Only explicitly audited full cutout/blend outline passes reach the alpha/layer closures below. Opaque
        // LIL_RENDER=0 and every LIL_LITE variant compile those branches out; Multi is handled from its verified
        // _TransparentMode plus compiled local keywords. An explicit set prevents a renamed/future variant bypass.
        private static readonly HashSet<string> VerifiedLilToonAlphaOutlineShaders =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "Hidden/lilToonCutoutOutline", "Hidden/lilToonOnePassTransparentOutline",
                "Hidden/lilToonTessellationCutoutOutline",
                "Hidden/lilToonTessellationOnePassTransparentOutline",
                "Hidden/lilToonTessellationTransparentOutline",
                "Hidden/lilToonTessellationTwoPassTransparentOutline",
                "Hidden/lilToonTransparentOutline", "Hidden/lilToonTwoPassTransparentOutline",
                "_lil/[Optional] lilToonOutlineOnlyCutout",
                "_lil/[Optional] lilToonOutlineOnlyTransparent"
            };

        // The two optional OutlineOnly wrappers expose forward outline passes but deliberately omit
        // SHADOW_CASTER_OUTLINE. Only these regular wrappers reach 2nd/3rd layer alpha through that pass.
        private static readonly HashSet<string> VerifiedLilToonAlphaOutlineShadowShaders =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "Hidden/lilToonCutoutOutline", "Hidden/lilToonOnePassTransparentOutline",
                "Hidden/lilToonTessellationCutoutOutline",
                "Hidden/lilToonTessellationOnePassTransparentOutline",
                "Hidden/lilToonTessellationTransparentOutline",
                "Hidden/lilToonTessellationTwoPassTransparentOutline",
                "Hidden/lilToonTransparentOutline", "Hidden/lilToonTwoPassTransparentOutline"
            };

        // These non-outline/non-fur lilToon 2.3.4 variants all use the audited common-fragment equation for
        // main-texture alpha. They are accepted only when every optional alpha combiner is statically disabled.
        // 下列非描边/非毛发变体共享已审计的主纹理 Alpha 方程；仅当所有可选 Alpha 合成均静态关闭时才放行。
        private static readonly HashSet<string> VerifiedLilDirectAlphaShaders = new HashSet<string>(StringComparer.Ordinal)
        {
            "Hidden/lilToonCutout", "Hidden/lilToonTransparent", "Hidden/lilToonOnePassTransparent",
            "Hidden/lilToonTwoPassTransparent", "Hidden/lilToonTessellationCutout",
            "Hidden/lilToonTessellationTransparent", "Hidden/lilToonTessellationOnePassTransparent",
            "Hidden/lilToonTessellationTwoPassTransparent"
        };

        // These names select fixed 2.3.4 pass programs (directly or through UsePass). Their compiled alpha behavior
        // cannot be negated by stale _TransparentMode, queue, tag, or material Blend metadata. Multi's dynamic base
        // shaders stay out of these sets; their replacement keywords and material state are evaluated separately.
        // 这些名称直接或经 UsePass 选择固定 Alpha pass；材质上的冗余模式、队列、标签或 Blend 元数据不能覆盖它。
        private static readonly HashSet<string> FixedLilCutoutShaders = new HashSet<string>(StringComparer.Ordinal)
        {
            "Hidden/lilToonCutout", "Hidden/lilToonCutoutOutline", "Hidden/lilToonFurCutout",
            "Hidden/lilToonTessellationCutout", "Hidden/lilToonTessellationCutoutOutline",
            "Hidden/lilToonLiteCutout", "Hidden/lilToonLiteCutoutOutline",
            "_lil/[Optional] lilToonOutlineOnlyCutout", "_lil/[Optional] lilToonFurOnlyCutout",
            "Hidden/lilToonMultiFur"
        };

        private static readonly HashSet<string> FixedLilBlendShaders = new HashSet<string>(StringComparer.Ordinal)
        {
            "Hidden/lilToonFur", "Hidden/lilToonFurTwoPass", "Hidden/lilToonGem",
            "Hidden/lilToonOnePassTransparent", "Hidden/lilToonOnePassTransparentOutline",
            "Hidden/lilToonTransparent", "Hidden/lilToonTransparentOutline",
            "Hidden/lilToonTwoPassTransparent", "Hidden/lilToonTwoPassTransparentOutline",
            "Hidden/lilToonRefraction", "Hidden/lilToonRefractionBlur",
            "Hidden/lilToonTessellationOnePassTransparent",
            "Hidden/lilToonTessellationOnePassTransparentOutline",
            "Hidden/lilToonTessellationTransparent", "Hidden/lilToonTessellationTransparentOutline",
            "Hidden/lilToonTessellationTwoPassTransparent",
            "Hidden/lilToonTessellationTwoPassTransparentOutline",
            "Hidden/lilToonLiteOnePassTransparent", "Hidden/lilToonLiteOnePassTransparentOutline",
            "Hidden/lilToonLiteTransparent", "Hidden/lilToonLiteTransparentOutline",
            "Hidden/lilToonLiteTwoPassTransparent", "Hidden/lilToonLiteTwoPassTransparentOutline",
            "Hidden/lilToonMultiRefraction",
            "_lil/[Optional] lilToonFurOnlyTransparent", "_lil/[Optional] lilToonFurOnlyTwoPass",
            "_lil/[Optional] lilToonOverlay", "_lil/[Optional] lilToonOverlayOnePass",
            "_lil/[Optional] lilToonOutlineOnlyTransparent", "_lil/[Optional] lilToonLiteOverlay",
            "_lil/[Optional] lilToonLiteOverlayOnePass"
        };

        // lilToon 2.3.4 verified coordinate families. The first samples fd.uvMain as-is;
        // the second applies the property's ST on top of fd.uvMain. ShaderLab's
        // NoScaleOffset annotation is deliberately not used as proof of shader coordinates.
        private static readonly HashSet<string> LilDirectMainUvTextures = new HashSet<string>(StringComparer.Ordinal)
        {
            "_MainTex", "_MainColorAdjustMask", "_Main2ndBlendMask", "_Main3rdBlendMask",
            "_RimShadeMask", "_TriMask"
        };

        private static readonly HashSet<string> LilOwnStOnMainUvTextures = new HashSet<string>(StringComparer.Ordinal)
        {
            "_AlphaMask", "_BumpMap", "_Bump2ndScaleMask", "_AnisotropyTangentMap",
            "_AnisotropyScaleMask", "_BacklightColorTex", "_AnisotropyShiftNoiseMask",
            "_SmoothnessTex", "_MetallicGlossMap", "_ReflectionColorTex", "_MatCapBumpMap",
            "_MatCapBlendMask", "_MatCap2ndBumpMap", "_MatCap2ndBlendMask", "_RimColorTex",
            "_EmissionBlendMask", "_Emission2ndBlendMask"
        };

        // In lilToon 2.3.4, LIL_OUTLINE aliases sampler_MainTex to sampler_OutlineTex. Full alpha-capable forward
        // outline passes reach AlphaMask/top-level dissolve, while their shadow-caster also runs the 2nd/3rd alpha
        // layers from lil_common_frag_alpha.hlsl. Keep this exact property closure explicit and testable.
        private static readonly HashSet<string> LilOutlineSharedSamplerTextures = new HashSet<string>(StringComparer.Ordinal)
        {
            "_AlphaMask", "_DissolveMask", "_DissolveNoiseMask",
            "_Main2ndBlendMask", "_Main2ndDissolveMask", "_Main2ndDissolveNoiseMask",
            "_Main3rdBlendMask", "_Main3rdDissolveMask", "_Main3rdDissolveNoiseMask"
        };

        // These shared-sampler textures use fd.uvMain too. In an outline pass that coordinate is controlled by
        // _OutlineTex_ST/_OutlineTex_ScrollRotate instead of the normal pass's _MainTex controls.
        private static readonly HashSet<string> LilOutlineMainUvTextures = new HashSet<string>(StringComparer.Ordinal)
        {
            "_AlphaMask", "_Main2ndBlendMask", "_Main3rdBlendMask"
        };

        // These layer properties are reached only by SHADOW_CASTER_OUTLINE, not by the forward-only optional
        // OutlineOnly wrappers. Top-level AlphaMask/dissolve remain reachable in forward outline.
        private static readonly HashSet<string> LilOutlineShadowOnlySamplerTextures =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "_Main2ndBlendMask", "_Main2ndDissolveMask", "_Main2ndDissolveNoiseMask",
                "_Main3rdBlendMask", "_Main3rdDissolveMask", "_Main3rdDissolveNoiseMask"
            };

        public IEnumerable<ShaderTextureInfo> Analyze(Material material, Func<string, bool> isTransformAnimated,
            Func<string, IEnumerable<Texture2D>> animatedTextures = null, Func<string, bool> isTextureAnimated = null)
        {
            if (material == null || material.shader == null) yield break;
            animatedTextures = animatedTextures ?? (_ => Array.Empty<Texture2D>());
            isTextureAnimated = isTextureAnimated ?? (_ => false);
            var shader = material.shader;
            var family = Family(shader.name, material);
            var count = ShaderUtil.GetPropertyCount(shader);
            for (var i = 0; i < count; i++)
            {
                if (ShaderUtil.GetPropertyType(shader, i) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                if (ShaderUtil.GetTexDim(shader, i) != TextureDimension.Tex2D) continue;
                var property = ShaderUtil.GetPropertyName(shader, i);
                int uvChannel;
                var reason = family == ShaderFamily.Unknown
                    ? UnsupportedShader(out uvChannel)
                    : ValidateProperty(material, property, family, isTransformAnimated, animatedTextures,
                        isTextureAnimated, out uvChannel);
                var kind = Classify(material, property, material.GetTexture(property) as Texture2D);
                var surfaceAlpha = SurfaceAlphaUsage(material, property, family, isTransformAnimated);
                yield return new ShaderTextureInfo(property, kind, uvChannel, reason == null, reason, surfaceAlpha,
                    UsedChannels(family, property));
            }
        }

        private static ShaderFamily Family(string name, Material material)
        {
            var assetPath = AssetDatabase.GetAssetPath(material.shader).Replace('\\', '/');
            if (name == "Standard" || name == "Standard (Specular setup)")
                return assetPath == "Resources/unity_builtin_extra"
                    ? ShaderFamily.UnityStandard : ShaderFamily.Unknown;

            var isVerifiedVrc = IsPackageAsset(assetPath, "com.vrchat.base", "3.10.4");
            if (isVerifiedVrc)
            {
                switch (name)
                {
                    case "VRChat/Mobile/Diffuse": return ShaderFamily.VrcDiffuse;
                    case "VRChat/Mobile/Bumped Diffuse":
                    case "VRChat/Mobile/Bumped Specular":
                    case "VRChat/Mobile/Bumped Mapped Specular": return ShaderFamily.VrcBumped;
                    case "VRChat/Mobile/Standard Lite": return ShaderFamily.VrcStandardLite;
                    case "VRChat/Mobile/Toon Lit": return ShaderFamily.VrcToonLit;
                    case "VRChat/Mobile/Toon Standard":
                    case "VRChat/Mobile/Toon Standard (Outline)": return ShaderFamily.VrcToonStandard;
                }
            }

            return IsVerifiedLilToonMaterial(material, name, assetPath)
                ? ShaderFamily.LilToon : ShaderFamily.Unknown;
        }

        internal static bool IsVerifiedLilToonMaterial(Material material)
        {
            if (material == null || material.shader == null) return false;
            return IsVerifiedLilToonMaterial(material, material.shader.name ?? string.Empty,
                AssetDatabase.GetAssetPath(material.shader).Replace('\\', '/'));
        }

        private static bool IsVerifiedLilToonMaterial(Material material, string shaderName, string assetPath)
        {
            return material != null && material.HasProperty("_lilToonVersion") &&
                   Mathf.Abs(material.GetFloat("_lilToonVersion") - 45f) <= 1e-4f &&
                   VerifiedLilToonShaders.Contains(shaderName) &&
                   IsPackageAsset(assetPath, "jp.lilxyzw.liltoon", "2.3.4");
        }

        internal static void FixedLilPassAlphaFlags(string shaderName, out bool cutout, out bool blend)
        {
            cutout = !string.IsNullOrEmpty(shaderName) && FixedLilCutoutShaders.Contains(shaderName);
            blend = !string.IsNullOrEmpty(shaderName) && FixedLilBlendShaders.Contains(shaderName);
        }

        internal static void AccumulateVerifiedLilPassAlphaFlags(Material material, ref bool cutout, ref bool blend)
        {
            if (material == null || material.shader == null) return;
            FixedLilPassAlphaFlags(material.shader.name, out var fixedCutout, out var fixedBlend);
            if (!fixedCutout && !fixedBlend) return;
            // Name alone is insufficient: only the already verified official lilToon 2.3.4 family may contribute proof.
            // 仅名称不足以证明身份；固定 pass 证据只接受已经完成官方 2.3.4 门禁的 shader family。
            if (Family(material.shader.name, material) != ShaderFamily.LilToon) return;
            cutout |= fixedCutout;
            blend |= fixedBlend;
        }

        private static ATOSurfaceAlphaUsage SurfaceAlphaUsage(Material material, string property,
            ShaderFamily family, Func<string, bool> isPropertyAnimated)
        {
            if (family == ShaderFamily.UnityStandard)
            {
                if (property != "_MainTex") return ATOSurfaceAlphaUsage.None;
                // Unity Standard computes surface alpha as _MainTex.a * _Color.a. The texture cutoff and blend
                // metrics are exact only when that multiplier is invariant and one.
                if (!material.HasProperty("_Color") || isPropertyAnimated("_Color") ||
                    !Finite(material.GetColor("_Color").a) ||
                    Mathf.Abs(material.GetColor("_Color").a - 1f) > 1e-6f)
                    return ATOSurfaceAlphaUsage.UnsupportedComposite;
                return ATOSurfaceAlphaUsage.TextureAlpha;
            }
            if (family == ShaderFamily.LilToon)
            {
                if (!LilMainAlphaIsDirect(material, isPropertyAnimated))
                    return ATOSurfaceAlphaUsage.UnsupportedComposite;
                return property == "_MainTex"
                    ? ATOSurfaceAlphaUsage.TextureAlpha : ATOSurfaceAlphaUsage.None;
            }
            // Every accepted VRC Mobile 3.10.4 family has fixed opaque ShaderLab passes: its compiled variants do
            // not expose alpha-test/blend state, and the fragment/surface output is opaque. Texture alpha may still
            // carry gloss or other channel data, but it never drives surface coverage, so report None for every slot.
            // 已验证的 VRC Mobile 3.10.4 pass 固定为不透明；纹理 Alpha 可承载光泽等数据，但不参与表面透明度。
            if (family == ShaderFamily.VrcDiffuse || family == ShaderFamily.VrcBumped ||
                family == ShaderFamily.VrcStandardLite || family == ShaderFamily.VrcToonLit ||
                family == ShaderFamily.VrcToonStandard)
                return ATOSurfaceAlphaUsage.None;

            // Unknown families must never acquire an inferred compositing equation from names or material tags.
            // 未验证 shader 不得从属性名或材质标签猜测 Alpha 合成方程。
            return family == ShaderFamily.Unknown
                ? ATOSurfaceAlphaUsage.UnsupportedComposite : ATOSurfaceAlphaUsage.None;
        }

        private static bool LilMainAlphaIsDirect(Material material, Func<string, bool> isPropertyAnimated)
        {
            if (!VerifiedLilDirectAlphaShaders.Contains(material.shader.name)) return false;
            if (!material.HasProperty("_Color") || isPropertyAnimated("_Color") ||
                !Finite(material.GetColor("_Color").a) ||
                Mathf.Abs(material.GetColor("_Color").a - 1f) > 1e-6f) return false;

            // lil_common_frag.hlsl 2.3.4 can replace/combine fd.col.a through each of these controls. A main-alpha
            // quality measurement is exact only when none of them can participate, including animation curves.
            var zeroFloats = new[] { "_AlphaMaskMode", "_Main2ndTexAlphaMode", "_Main3rdTexAlphaMode",
                "_UseDither", "_DepthFadeToAlpha" };
            foreach (var property in zeroFloats)
            {
                if (!material.HasProperty(property)) continue;
                var value = material.GetFloat(property);
                if (isPropertyAnimated(property) || !Finite(value) || Mathf.Abs(value) > 1e-6f) return false;
            }
            if (material.HasProperty("_DissolveParams"))
            {
                var value = material.GetVector("_DissolveParams");
                if (isPropertyAnimated("_DissolveParams") || !Finite(value) || Mathf.Abs(value.x) > 1e-6f)
                    return false;
            }
            if (material.HasProperty("_DistanceFadeColor"))
            {
                var value = material.GetColor("_DistanceFadeColor");
                if (isPropertyAnimated("_DistanceFadeColor") || !Finite(value.a) ||
                    Mathf.Abs(value.a - 1f) > 1e-6f) return false;
            }
            return true;
        }

        internal static bool IsVerifiedOpaqueMergeShader(Material material)
        {
            if (material == null || material.shader == null) return false;
            var family = Family(material.shader.name, material);
            if (family == ShaderFamily.UnityStandard || family == ShaderFamily.VrcDiffuse ||
                family == ShaderFamily.VrcBumped || family == ShaderFamily.VrcStandardLite ||
                family == ShaderFamily.VrcToonLit) return true;
            // The non-outline Toon Standard source has fixed opaque depth/blend passes. Outline and lilToon
            // variants are intentionally excluded: their extra/configurable passes require a separate proof.
            return family == ShaderFamily.VrcToonStandard &&
                   material.shader.name == "VRChat/Mobile/Toon Standard";
        }

        private static bool IsPackageAsset(string assetPath, string expectedName, string expectedVersion)
        {
            if (string.IsNullOrEmpty(assetPath)) return false;
            var package = PackageInfo.FindForAssetPath(assetPath);
            return package != null && string.Equals(package.name, expectedName, StringComparison.Ordinal) &&
                   string.Equals(package.version, expectedVersion, StringComparison.Ordinal);
        }

        private static string UnsupportedShader(out int uvChannel)
        {
            uvChannel = 0;
            return "shader texture sampling is not a verified Standard, VRChat Mobile, or official lilToon 2.3.4 path";
        }

        private static string ValidateProperty(Material material, string property, ShaderFamily family,
            Func<string, bool> isTransformAnimated, Func<string, IEnumerable<Texture2D>> animatedTextures,
            Func<string, bool> isTextureAnimated, out int uvChannel)
        {
            uvChannel = 0;
            string reason;
            switch (family)
            {
                case ShaderFamily.UnityStandard:
                    reason = ValidateUnityStandard(material, property, isTransformAnimated, out uvChannel); break;
                case ShaderFamily.VrcDiffuse:
                    reason = property == "_MainTex"
                        ? ValidateTextureTransform(material, "_MainTex", isTransformAnimated)
                        : "property is not a verified VRC Mobile Diffuse mesh-UV texture"; break;
                case ShaderFamily.VrcBumped:
                    reason = ValidateVrcBumped(material, property, isTransformAnimated); break;
                case ShaderFamily.VrcStandardLite:
                    reason = ValidateStandardLite(material, property, isTransformAnimated, out uvChannel); break;
                case ShaderFamily.VrcToonLit:
                    reason = property == "_MainTex"
                        ? ValidateTextureTransform(material, "_MainTex", isTransformAnimated)
                        : "property is not a verified VRC Mobile Toon Lit mesh-UV texture"; break;
                case ShaderFamily.VrcToonStandard:
                    reason = ValidateToonStandard(material, property, isTransformAnimated, out uvChannel); break;
                case ShaderFamily.LilToon:
                    reason = ValidateLilToon(material, property, isTransformAnimated, animatedTextures,
                        isTextureAnimated, out uvChannel); break;
                default: return UnsupportedShader(out uvChannel);
            }
            // Each family validator above proves the actual controller ST (which may belong to a
            // different property, or may intentionally be absent). A generic property_ST check
            // would both miss shared transforms and reject shader paths that ignore declared ST.
            return reason;
        }

        private static string ValidateUnityStandard(Material material, string property,
            Func<string, bool> isTransformAnimated, out int uvChannel)
        {
            uvChannel = 0;
            switch (property)
            {
                // UnityStandardInput packs all base-family samples, including _DetailMask, from
                // TRANSFORM_TEX(uv0, _MainTex). The per-texture *_ST values are not the coordinates used.
                case "_MainTex": case "_MetallicGlossMap": case "_SpecGlossMap": case "_BumpMap":
                case "_OcclusionMap": case "_EmissionMap": case "_DetailMask":
                    return ValidateTextureTransform(material, "_MainTex", isTransformAnimated);
                case "_DetailAlbedoMap": case "_DetailNormalMap":
                    var selectorReason = ResolveUvSelector(material, "_UVSec", 1, isTransformAnimated, out uvChannel);
                    return selectorReason ?? ValidateTextureTransform(material, "_DetailAlbedoMap", isTransformAnimated);
                default: return "Standard shader property is not proven to use an affine mesh UV";
            }
        }

        private static string ValidateVrcBumped(Material material, string property,
            Func<string, bool> isTransformAnimated)
        {
            if (property != "_MainTex" && property != "_BumpMap")
                return "property is not a verified VRC Mobile bumped mesh-UV texture";
            // These surface shaders sample both textures with uv_MainTex, so _BumpMap_ST is not the transform used.
            return ValidateTextureTransform(material, "_MainTex", isTransformAnimated);
        }

        private static string ValidateStandardLite(Material material, string property,
            Func<string, bool> isTransformAnimated, out int uvChannel)
        {
            uvChannel = 0;
            switch (property)
            {
                // Standard Lite's texcoord0 is transformed once by _MainTex_ST and reused for every base/detail-mask map.
                case "_MainTex": case "_MetallicGlossMap": case "_BumpMap": case "_OcclusionMap":
                case "_EmissionMap": case "_DetailMask":
                    return ValidateTextureTransform(material, "_MainTex", isTransformAnimated);
                case "_DetailAlbedoMap": case "_DetailNormalMap":
                    var reason = ResolveUvSelector(material, "_UVSec", 1, isTransformAnimated, out uvChannel);
                    // texcoord1 always uses _DetailAlbedoMap_ST, including the detail-normal lookup.
                    return reason ?? ValidateTextureTransform(material, "_DetailAlbedoMap", isTransformAnimated);
                default: return "VRC Standard Lite property is not proven to use an affine mesh UV";
            }
        }

        private static string ValidateToonStandard(Material material, string property,
            Func<string, bool> isTransformAnimated, out int uvChannel)
        {
            uvChannel = 0;
            switch (property)
            {
                case "_MainTex": case "_BumpMap": case "_MetallicMap": case "_GlossMap":
                case "_MatcapMask": case "_OcclusionMap": case "_DetailMask": case "_HueShiftMask":
                case "_ColorMask":
                    return ValidateTextureTransform(material, property, isTransformAnimated);
                // VRC Toon Standard samples the outline mask from raw UV0 and does not apply _OutlineMask_ST.
                case "_OutlineMask": return null;
                case "_EmissionMap":
                    var emissionReason = ResolveUvSelector(material, "_EmissionUV", 1, isTransformAnimated, out uvChannel);
                    return emissionReason ?? ValidateTextureTransform(material, property, isTransformAnimated);
                case "_DetailAlbedoMap": case "_DetailNormalMap":
                    var detailReason = ResolveUvSelector(material, "_DetailUV", 1, isTransformAnimated, out uvChannel);
                    return detailReason ?? ValidateTextureTransform(material, property, isTransformAnimated);
                case "_Ramp": return "Toon Standard ramp uses lighting coordinates instead of mesh UV";
                case "_Matcap": return "Toon Standard matcap uses view-normal coordinates instead of mesh UV";
                default: return "Toon Standard property is not proven to use an affine mesh UV";
            }
        }

        private static string ValidateLilToon(Material material, string property,
            Func<string, bool> isTransformAnimated, Func<string, IEnumerable<Texture2D>> animatedTextures,
            Func<string, bool> isTextureAnimated, out int uvChannel)
        {
            uvChannel = 0;
            var reason = ValidateLilMultiMode(material);
            if (reason != null) return reason;

            if (LilDirectMainUvTextures.Contains(property))
            {
                reason = ValidateLilMainUv(material, isTransformAnimated);
                if (reason == null) reason = ValidateLilOutlineUvIfUsed(material, property, isTransformAnimated);
                return reason ?? ValidateLilMainSampler(material, property, animatedTextures, isTextureAnimated,
                    LilOutlinePassUsesSharedSampler(property));
            }

            if (LilOwnStOnMainUvTextures.Contains(property))
            {
                // RefractionBlur samples this map in its blur pass with lil_sampler_linear_repeat, in addition
                // to the normal forward path's shared sampler. A Clamp atlas cannot preserve that fixed footprint.
                if (property == "_SmoothnessTex" && material.shader.name == "Hidden/lilToonRefractionBlur")
                    return "lilToon RefractionBlur smoothness texture uses a fixed linear Repeat sampler";
                reason = ValidateLilMainUv(material, isTransformAnimated);
                if (reason == null) reason = ValidateLilOwnTransform(material, property, isTransformAnimated);
                if (reason == null) reason = ValidateLilOutlineUvIfUsed(material, property, isTransformAnimated);
                return reason ?? ValidateLilMainSampler(material, property, animatedTextures, isTextureAnimated,
                    LilOutlinePassUsesSharedSampler(property));
            }

            switch (property)
            {
                // Outline texturing starts from raw UV0 and has its own sampler, ST, and scroll.
                case "_OutlineTex":
                    return ValidateLilOwnTransform(material, property, isTransformAnimated);
                case "_Main2ndTex": case "_Main3rdTex":
                    reason = ResolveUvSelector(material, property + "_UVMode", 3, isTransformAnimated, out uvChannel);
                    if (reason == null) reason = ValidateLilSubTexture(material, property, isTransformAnimated);
                    return reason ?? ValidateLilOwnTransform(material, property, isTransformAnimated);
                // lilToon hard-codes a linear-repeat sampler for the second normal map. A Clamp atlas cannot
                // preserve that footprint, even when the source importer's wrap mode happens to be Clamp.
                case "_Bump2ndMap":
                    return "lilToon second normal map uses a fixed Repeat sampler";
                case "_ShadowStrengthMask": case "_ShadowBlurMask": case "_ShadowBorderMask":
                    return "lilToon shadow mask uses a fixed linear Repeat sampler";
                case "_GlitterColorTex":
                    reason = ResolveUvSelector(material, property + "_UVMode", 3, isTransformAnimated, out uvChannel);
                    // UV mode zero starts from fd.uvMain; the other modes start from their raw UV channel.
                    if (reason == null && uvChannel == 0) reason = ValidateLilMainUv(material, isTransformAnimated);
                    if (reason == null) reason = ValidateLilOwnTransform(material, property, isTransformAnimated);
                    return reason ?? ValidateLilMainSampler(material, property, animatedTextures, isTextureAnimated, false);
                case "_EmissionMap": case "_Emission2ndMap":
                    reason = ResolveUvSelector(material, property + "_UVMode", 3, isTransformAnimated, out uvChannel);
                    var parallaxDepth = property == "_EmissionMap" ? "_EmissionParallaxDepth" : "_Emission2ndParallaxDepth";
                    if (reason == null && (PropertyEnabled(material, parallaxDepth) || isTransformAnimated(parallaxDepth)))
                        reason = "lilToon emission parallax changes texture coordinates";
                    return reason ?? ValidateLilOwnTransform(material, property, isTransformAnimated);
                case "_ShadowColorTex": case "_Shadow2ndColorTex": case "_Shadow3rdColorTex":
                    if (PropertyEnabled(material, "_ShadowColorType") || isTransformAnimated("_ShadowColorType"))
                        return "lilToon shadow color texture can be sampled as a LUT";
                    reason = ValidateLilMainUv(material, isTransformAnimated);
                    return reason ?? ValidateLilMainSampler(material, property, animatedTextures, isTextureAnimated, false);
                case "_FurMask":
                    reason = ValidateLilMainUv(material, isTransformAnimated);
                    return reason ?? ValidateLilMainSampler(material, property, animatedTextures, isTextureAnimated, false);
                case "_FurNoiseMask":
                    reason = ValidateTextureTransform(material, property, isTransformAnimated);
                    return reason ?? ValidateLilMainSampler(material, property, animatedTextures, isTextureAnimated, false);
                case "_DissolveMask": case "_Main2ndDissolveMask": case "_Main3rdDissolveMask":
                    reason = ValidateTextureTransform(material, property, isTransformAnimated);
                    return reason ?? ValidateLilMainSampler(material, property, animatedTextures, isTextureAnimated,
                        LilOutlinePassUsesSharedSampler(property));
                case "_DissolveNoiseMask": case "_Main2ndDissolveNoiseMask": case "_Main3rdDissolveNoiseMask":
                    reason = ValidateLilOwnTransform(material, property, isTransformAnimated);
                    return reason ?? ValidateLilMainSampler(material, property, animatedTextures, isTextureAnimated,
                        LilOutlinePassUsesSharedSampler(property));
                case "_OutlineWidthMask": case "_OutlineVectorTex": case "_FurLengthMask": case "_FurVectorTex":
                    return "lilToon vertex texture path uses a fixed Repeat sampler";
                default:
                    return "lilToon property is not proven to use an affine mesh UV";
            }
        }

        private static string ValidateLilMainUv(Material material, Func<string, bool> isTransformAnimated)
        {
            var reason = ValidateTextureTransform(material, "_MainTex", isTransformAnimated);
            if (reason == null) reason = ValidateLilScrollRotate(material, "_MainTex", isTransformAnimated);
            if (reason != null) return "lilToon main UV transform is unsafe: " + reason;
            if (PropertyEnabled(material, "_ShiftBackfaceUV") || isTransformAnimated("_ShiftBackfaceUV"))
                return "lilToon backface UV shifting changes mesh texture coordinates";
            if (PropertyEnabled(material, "_UseParallax") || PropertyEnabled(material, "_UsePOM") ||
                isTransformAnimated("_UseParallax") || isTransformAnimated("_UsePOM"))
                return "lilToon parallax changes mesh texture coordinates";
            return null;
        }

        internal static bool LilOutlinePassUsesSharedSampler(string property) =>
            property != null && LilOutlineSharedSamplerTextures.Contains(property);

        internal static bool LilOutlinePassUsesOutlineUv(string property) =>
            property != null && LilOutlineMainUvTextures.Contains(property);

        internal static bool LilOutlineAlphaPassCanSample(string shaderName, int multiTransparentMode = 0,
            bool alphaClipKeyword = false, bool clipRectKeyword = false, bool requireShadowOutline = false)
        {
            if (string.IsNullOrEmpty(shaderName)) return false;
            if (VerifiedLilToonAlphaOutlineShaders.Contains(shaderName))
                return !requireShadowOutline || VerifiedLilToonAlphaOutlineShadowShaders.Contains(shaderName);
            if (shaderName != "Hidden/lilToonMultiOutline") return false;
            // Multi derives LIL_RENDER from these serialized local keywords. _TransparentMode normally drives them
            // through the inspector, but a stale/manually edited mode must not hide an actually compiled alpha pass.
            // Multi 的 LIL_RENDER 来自这两个序列化局部关键字；即使模式值过期，也不能漏掉已编译的 Alpha pass。
            return alphaClipKeyword || clipRectKeyword || multiTransparentMode != 0;
        }

        private static bool LilOutlineAlphaPassCanSample(Material material, bool requireShadowOutline)
        {
            if (material == null || material.shader == null) return false;
            var shaderName = material.shader.name ?? string.Empty;
            if (shaderName != "Hidden/lilToonMultiOutline")
                return LilOutlineAlphaPassCanSample(shaderName, requireShadowOutline: requireShadowOutline);
            var alphaClipKeyword = material.IsKeywordEnabled("UNITY_UI_ALPHACLIP");
            var clipRectKeyword = material.IsKeywordEnabled("UNITY_UI_CLIP_RECT");
            // ValidateLilMultiMode has already proved this property finite, integral, and in [0,2]. Keep a defensive
            // unsafe value alpha-capable so a future call-order change cannot silently weaken the sampler gate.
            if (!material.HasProperty("_TransparentMode")) return true;
            var value = material.GetFloat("_TransparentMode");
            if (!Finite(value)) return true;
            var mode = Mathf.RoundToInt(value);
            if (Mathf.Abs(value - mode) > 1e-4f || mode < 0 || mode > 2) return true;
            return LilOutlineAlphaPassCanSample(shaderName, mode, alphaClipKeyword, clipRectKeyword,
                requireShadowOutline);
        }

        private static string ValidateLilOutlineUvIfUsed(Material material, string property,
            Func<string, bool> isTransformAnimated)
        {
            var requiresShadow = LilOutlineShadowOnlySamplerTextures.Contains(property);
            if (!LilOutlinePassUsesOutlineUv(property) ||
                !LilOutlineAlphaPassCanSample(material, requiresShadow)) return null;
            var reason = ValidateLilOwnTransform(material, "_OutlineTex", isTransformAnimated);
            return reason == null ? null : "lilToon outline UV transform is unsafe: " + reason;
        }

        private static string ValidateLilMainSampler(Material material, string property,
            Func<string, IEnumerable<Texture2D>> animatedTextures, Func<string, bool> isTextureAnimated,
            bool outlinePassAlsoSamples)
        {
            var targets = new List<Texture2D>();
            if (material.GetTexture(property) is Texture2D current) targets.Add(current);
            targets.AddRange(animatedTextures(property).Where(value => value != null));
            if (targets.Count == 0) return null;

            var reason = ValidateLilSamplerController(material, property, targets, "_MainTex", isTextureAnimated);
            if (reason != null) return reason;
            if (outlinePassAlsoSamples && LilOutlineAlphaPassCanSample(material,
                    LilOutlineShadowOnlySamplerTextures.Contains(property)))
                return ValidateLilSamplerController(material, property, targets, "_OutlineTex", isTextureAnimated);
            return null;
        }

        private static string ValidateLilSamplerController(Material material, string property,
            IEnumerable<Texture2D> targets, string controllerProperty, Func<string, bool> isTextureAnimated)
        {
            if (property == controllerProperty) return null;
            if (isTextureAnimated(controllerProperty))
                return "lilToon shared sampler controller " + controllerProperty + " is texture-animated";
            if (!(material.GetTexture(controllerProperty) is Texture2D controller))
                return "lilToon shared sampler controller " + controllerProperty + " is not an assigned Texture2D";
            return targets.All(target => SameSamplingState(target, controller)) ? null :
                "lilToon texture sampling state differs from shared sampler controller " + controllerProperty;
        }

        private static bool SameSamplingState(Texture2D left, Texture2D right)
        {
            return left.filterMode == right.filterMode && left.wrapModeU == right.wrapModeU &&
                   left.wrapModeV == right.wrapModeV && left.anisoLevel == right.anisoLevel &&
                   Finite(left.mipMapBias) && Finite(right.mipMapBias) &&
                   Mathf.Abs(left.mipMapBias - right.mipMapBias) <= 1e-6f;
        }

        private static string ValidateLilOwnTransform(Material material, string property,
            Func<string, bool> isTransformAnimated)
        {
            var reason = ValidateTextureTransform(material, property, isTransformAnimated);
            return reason ?? ValidateLilScrollRotate(material, property, isTransformAnimated);
        }

        private static string ValidateLilMultiMode(Material material)
        {
            var shaderName = material.shader == null ? string.Empty : material.shader.name ?? string.Empty;
            if (shaderName != "_lil/lilToonMulti" &&
                !shaderName.StartsWith("Hidden/lilToonMulti", StringComparison.Ordinal)) return null;
            if (!material.HasProperty("_TransparentMode")) return "lilToon Multi rendering mode is missing";
            var value = material.GetFloat("_TransparentMode");
            if (!Finite(value)) return "lilToon Multi rendering mode is invalid";
            var mode = Mathf.RoundToInt(value);
            if (Mathf.Abs(value - mode) > 1e-4f) return "lilToon Multi rendering mode is invalid";
            if (shaderName.IndexOf("MultiFur", StringComparison.Ordinal) >= 0)
                return mode == 4 || mode == 5 ? null : "lilToon Multi fur shader and rendering mode disagree";
            if (shaderName.IndexOf("MultiGem", StringComparison.Ordinal) >= 0)
                return mode == 6 ? null : "lilToon Multi gem shader and rendering mode disagree";
            if (shaderName.IndexOf("MultiRefraction", StringComparison.Ordinal) >= 0)
                return mode == 3 ? null : "lilToon Multi refraction shader and rendering mode disagree";
            return mode >= 0 && mode <= 2 ? null : "lilToon Multi base shader and rendering mode disagree";
        }

        private static string ValidateLilScrollRotate(Material material, string property,
            Func<string, bool> isTransformAnimated)
        {
            var scrollName = property + "_ScrollRotate";
            if (material.HasProperty(scrollName))
            {
                var value = material.GetVector(scrollName);
                if (!Finite(value) || value.sqrMagnitude > 1e-12f) return "lilToon scroll/rotation is enabled";
            }
            return isTransformAnimated(scrollName) ? "lilToon scroll/rotation is animated" : null;
        }

        private static string ValidateLilSubTexture(Material material, string property,
            Func<string, bool> isTransformAnimated)
        {
            foreach (var suffix in new[]
                     {
                         "Angle", "IsDecal", "IsLeftOnly", "IsRightOnly", "ShouldCopy", "ShouldFlipMirror",
                         "ShouldFlipCopy", "IsMSDF"
                     })
            {
                var associated = property + suffix;
                if (PropertyEnabled(material, associated)) return "lilToon decal, angle, copy, or MSDF sampling is enabled";
                if (isTransformAnimated(associated)) return "lilToon decal, angle, copy, or MSDF sampling is animated";
            }
            var animation = property + "DecalAnimation";
            var subParameter = property + "DecalSubParam";
            if (material.HasProperty(animation) && !Approximately(material.GetVector(animation), new Vector4(1f, 1f, 1f, 30f)))
                return "lilToon decal atlas animation is enabled";
            if (material.HasProperty(subParameter) && !Approximately(material.GetVector(subParameter), new Vector4(1f, 1f, 0f, 1f)))
                return "lilToon decal atlas sub-rectangle is enabled";
            if (isTransformAnimated(animation) || isTransformAnimated(subParameter))
                return "lilToon decal atlas sampling is animated";
            return null;
        }

        private static string ResolveUvSelector(Material material, string property, int maximum,
            Func<string, bool> isTransformAnimated, out int uvChannel)
        {
            uvChannel = 0;
            if (!material.HasProperty(property)) return "required UV selector is absent";
            if (isTransformAnimated(property)) return "UV selector is animated";
            var value = material.GetFloat(property);
            if (!Finite(value)) return "UV selector is not finite";
            var rounded = Mathf.RoundToInt(value);
            if (Mathf.Abs(value - rounded) > 1e-4f || rounded < 0 || rounded > maximum)
                return "UV mode is not a supported mesh UV channel";
            uvChannel = rounded;
            return null;
        }

        private static string ValidateTextureTransform(Material material, string property,
            Func<string, bool> isTransformAnimated)
        {
            if (!material.HasProperty(property)) return "texture property is absent";
            var scale = material.GetTextureScale(property);
            var offset = material.GetTextureOffset(property);
            if (!Finite(scale) || !Finite(offset)) return "texture ST is not finite";
            if ((scale - Vector2.one).sqrMagnitude > 1e-12f || offset.sqrMagnitude > 1e-12f)
                return "non-identity texture ST";
            return isTransformAnimated(property) ? "texture coordinates are animated" : null;
        }

        private static bool PropertyEnabled(Material material, string property)
        {
            if (!material.HasProperty(property)) return false;
            var value = material.GetFloat(property);
            return !Finite(value) || Mathf.Abs(value) > 1e-6f;
        }

        private static bool Approximately(Vector4 left, Vector4 right) =>
            Finite(left) && (left - right).sqrMagnitude <= 1e-12f;

        private static bool Finite(Vector2 value) => Finite(value.x) && Finite(value.y);
        private static bool Finite(Vector4 value) => Finite(value.x) && Finite(value.y) && Finite(value.z) && Finite(value.w);
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static ATOTextureChannels UsedChannels(ShaderFamily family, string property)
        {
            // Only channels proven from the audited shader source are narrowed. Unknown/dynamic selectors retain RGBA.
            // 仅缩窄已从指定版本 shader 源码证明的通道；未知或动态选择器继续保守检查 RGBA。
            if (family == ShaderFamily.UnityStandard || family == ShaderFamily.VrcStandardLite)
            {
                switch (property)
                {
                    case "_MetallicGlossMap": return ATOTextureChannels.R | ATOTextureChannels.A;
                    case "_OcclusionMap": return ATOTextureChannels.G;
                    case "_DetailMask": return ATOTextureChannels.A;
                }
            }
            if (family == ShaderFamily.LilToon)
            {
                switch (property)
                {
                    case "_MainColorAdjustMask": case "_Main2ndBlendMask": case "_Main3rdBlendMask":
                    case "_RimShadeMask": case "_AlphaMask": case "_Bump2ndScaleMask":
                    case "_AnisotropyScaleMask": case "_AnisotropyShiftNoiseMask":
                    case "_SmoothnessTex": case "_MetallicGlossMap": case "_FurMask":
                    case "_FurNoiseMask": case "_DissolveMask": case "_Main2ndDissolveMask":
                    case "_Main3rdDissolveMask": case "_DissolveNoiseMask":
                    case "_Main2ndDissolveNoiseMask": case "_Main3rdDissolveNoiseMask":
                        return ATOTextureChannels.R;
                    case "_TriMask": case "_MatCapBlendMask": case "_MatCap2ndBlendMask":
                        return ATOTextureChannels.Rgb;
                }
            }
            return ATOTextureChannels.Rgba;
        }

        internal static ATOTextureKind Classify(Material material, string property, Texture2D texture)
        {
            var name = property.ToLowerInvariant();
            var importer = texture == null ? null : AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture)) as TextureImporter;
            // Shader semantics win over importer metadata and broad "bump" name tokens: scale/normal masks are
            // scalar data even if a user accidentally imported them as NormalMap. Treating them as encoded normals
            // would run the wrong resampler and quality metric.
            if (name.Contains("mask") || name.Contains("metallic") || name.Contains("smoothness") ||
                name.Contains("roughness") || name.Contains("occlusion") || name.Contains("thickness"))
                return ATOTextureKind.Grayscale;
            if ((importer != null && importer.textureType == TextureImporterType.NormalMap) ||
                name.Contains("normal") || name.Contains("bump")) return ATOTextureKind.Normal;
            return HasAlpha(texture, importer) ? ATOTextureKind.ColorAlpha : ATOTextureKind.ColorOpaque;
        }

        private static bool HasAlpha(Texture2D texture, TextureImporter importer)
        {
            if (texture == null) return false;
            if (importer != null)
            {
                if (importer.alphaSource == TextureImporterAlphaSource.None) return false;
                if (importer.alphaSource == TextureImporterAlphaSource.FromGrayScale) return true;
                return importer.DoesSourceTextureHaveAlpha();
            }
            return GraphicsFormatUtility.HasAlphaChannel(texture.graphicsFormat);
        }
    }
}
