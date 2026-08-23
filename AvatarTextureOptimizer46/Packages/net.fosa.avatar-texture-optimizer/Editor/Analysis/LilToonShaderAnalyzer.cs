// SPDX-License-Identifier: MIT
// EN: lilToon analyzer. Every rule below was derived by reading lilToon 2.3.4 sources, not guessed:
//       * Shader/Includes/lil_common_frag.hlsl        - which UV each texture is sampled with
//       * Shader/Includes/lil_common_functions.hlsl   - lilCalcUV / lilCalcDoubleSideUV / lilParallax
//       * Shader/Includes/lil_common_macro.hlsl:272   - LIL_SAMPLE_2D_ST applies the per-texture _ST
//       * CustomShaderResources/Properties/*.lilblock - the property table
// ZH: lilToon 分析器。以下每条规则都来自阅读 lilToon 2.3.4 源码，而非猜测：
//       * Shader/Includes/lil_common_frag.hlsl        - 每张贴图使用哪个 UV 采样
//       * Shader/Includes/lil_common_functions.hlsl   - lilCalcUV / lilCalcDoubleSideUV / lilParallax
//       * Shader/Includes/lil_common_macro.hlsl:272   - LIL_SAMPLE_2D_ST 会应用每张贴图各自的 _ST
//       * CustomShaderResources/Properties/*.lilblock - 属性表

using System;
using System.Collections.Generic;
using Net.Fosa.AvatarTextureOptimizer.Api;
using UnityEditor;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Analysis
{
    /// <summary>
    /// EN: Analyzer for the lilToon shader family. Anything not explicitly proven safe is reported as
    ///     <see cref="AtoSamplingSpace.NonMeshUV"/> so that ATO leaves it alone.
    /// ZH: lilToon 着色器族的分析器。凡是未被明确证明安全的内容，一律报告为
    ///     <see cref="AtoSamplingSpace.NonMeshUV"/>，让 ATO 不去动它。
    /// </summary>
    public sealed class LilToonShaderAnalyzer : IAtoShaderAnalyzer
    {
        /// <inheritdoc/>
        public int Priority => 100;

        /// <summary>
        /// EN: Textures sampled with <c>fd.uvMain</c>, i.e. UV0 transformed by <c>_MainTex_ST</c>.
        ///     Confirmed one by one against lil_common_frag.hlsl.
        /// ZH: 使用 <c>fd.uvMain</c> 采样的贴图，即经 <c>_MainTex_ST</c> 变换后的 UV0。
        ///     已逐条对照 lil_common_frag.hlsl 确认。
        /// </summary>
        private static readonly HashSet<string> MainUvTextures = new HashSet<string>(StringComparer.Ordinal)
        {
            "_MainTex", "_MainColorAdjustMask", "_AlphaMask",
            "_BumpMap", "_Bump2ndScaleMask",
            "_AnisotropyTangentMap", "_AnisotropyScaleMask", "_AnisotropyShiftNoiseMask",
            "_BacklightColorTex",
            "_EmissionBlendMask", "_Emission2ndBlendMask",
            "_Main2ndBlendMask", "_Main3rdBlendMask",
            "_MatCapBlendMask", "_MatCapBumpMap", "_MatCap2ndBlendMask", "_MatCap2ndBumpMap",
            "_MetallicGlossMap", "_SmoothnessTex", "_ReflectionColorTex",
            "_RimColorTex", "_RimShadeMask",
            "_ShadowColorTex", "_Shadow2ndColorTex", "_Shadow3rdColorTex",
            "_ShadowBlurMask", "_ShadowBorderMask", "_ShadowStrengthMask",
            "_OutlineTex", "_OutlineWidthMask", "_OutlineColorMask",
            "_FurMask", "_FurLengthMask", "_FurColorMask",
        };

        /// <summary>
        /// EN: Textures whose UV channel is selected by a companion <c>_UVMode</c> property.
        ///     Mode 0 means uvMain (UV0), 1..3 mean UV1..UV3 and 4 means a projected space we cannot follow.
        /// ZH: UV 通道由配套 <c>_UVMode</c> 属性选择的贴图。
        ///     模式 0 表示 uvMain（UV0），1..3 表示 UV1..UV3，4 表示我们无法跟踪的投影空间。
        /// </summary>
        private static readonly Dictionary<string, string> UvModeTextures = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "_Main2ndTex", "_Main2ndTex_UVMode" },
            { "_Main3rdTex", "_Main3rdTex_UVMode" },
            { "_Bump2ndMap", "_Bump2ndMap_UVMode" },
            { "_EmissionMap", "_EmissionMap_UVMode" },
            { "_Emission2ndMap", "_Emission2ndMap_UVMode" },
        };

        /// <summary>
        /// EN: Per texture scroll/rotate vectors. Any non zero component animates or rotates the UV, so
        ///     the texture must not be atlased. lilCalcUV(uv, st, sr) proves the effect.
        /// ZH: 每张贴图的滚动/旋转向量。任意分量非零都会让 UV 动起来或旋转，
        ///     因此该贴图不能进图集。lilCalcUV(uv, st, sr) 证明了这一点。
        /// </summary>
        private static readonly Dictionary<string, string> ScrollRotateProps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "_MainTex", "_MainTex_ScrollRotate" },
            { "_Main2ndTex", "_Main2ndTex_ScrollRotate" },
            { "_Main3rdTex", "_Main3rdTex_ScrollRotate" },
            { "_OutlineTex", "_OutlineTex_ScrollRotate" },
            { "_EmissionMap", "_EmissionMap_ScrollRotate" },
            { "_Emission2ndMap", "_Emission2ndMap_ScrollRotate" },
            { "_EmissionBlendMask", "_EmissionBlendMask_ScrollRotate" },
            { "_Emission2ndBlendMask", "_Emission2ndBlendMask_ScrollRotate" },
            { "_AudioLinkMask", "_AudioLinkMask_ScrollRotate" },
        };

        /// <inheritdoc/>
        public bool CanAnalyze(Shader shader)
        {
            if (shader == null) return false;
            var n = shader.name;
            return n.StartsWith("lilToon", StringComparison.Ordinal)
                   || n.StartsWith("Hidden/lilToon", StringComparison.Ordinal)
                   || n.StartsWith("Hidden/ltspass", StringComparison.Ordinal)
                   || n.StartsWith("_lil/", StringComparison.Ordinal);
        }

        /// <inheritdoc/>
        public AtoMaterialAnalysis Analyze(Material material)
        {
            var result = new AtoMaterialAnalysis();
            var shader = material.shader;

            ShaderAnalysisUtil.ResolveAlphaMode(material, out var mode, out var cutoff);
            result.AlphaMode = mode;
            result.Cutoff = cutoff;

            // EN: lilCalcDoubleSideUV shifts the backface UV by +1.0 in X, which only works because the
            //     sampler wraps. Atlasing destroys that, so the whole material becomes untouchable.
            // ZH: lilCalcDoubleSideUV 会把背面 UV 在 X 上偏移 +1.0，这仅在采样器 repeat 时才成立。
            //     图集化会破坏它，因此整个材质不可动。
            if (!ShaderAnalysisUtil.Approximately(ShaderAnalysisUtil.GetFloat(material, "_ShiftBackfaceUV", 0f), 0f))
            {
                result.ForceWhitelist = true;
                result.ForceWhitelistReason = "lilToon _ShiftBackfaceUV relies on UV wrapping";
                return result;
            }

            // EN: Parallax / POM displaces uvMain inside the fragment shader.
            // ZH: 视差 / POM 会在片元着色器中位移 uvMain。
            if (!ShaderAnalysisUtil.Approximately(ShaderAnalysisUtil.GetFloat(material, "_UseParallax", 0f), 0f)
                || !ShaderAnalysisUtil.Approximately(ShaderAnalysisUtil.GetFloat(material, "_UsePOM", 0f), 0f))
            {
                result.ForceWhitelist = true;
                result.ForceWhitelistReason = "lilToon parallax/POM displaces the main UV";
                return result;
            }

            // EN: The main UV transform gates every uvMain based texture.
            // ZH: 主 UV 变换是所有基于 uvMain 的贴图的前提条件。
            bool mainUvClean = ShaderAnalysisUtil.HasIdentityScaleOffset(material, "_MainTex")
                               && IsZeroVector(material, "_MainTex_ScrollRotate");

            foreach (var prop in ShaderAnalysisUtil.GetTextureProperties(shader))
            {
                var tex = material.GetTexture(prop);
                if (tex == null) continue;

                var refInfo = new AtoTextureRef
                {
                    PropertyName = prop,
                    Texture = tex,
                    Kind = ShaderAnalysisUtil.ClassifyKind(shader, prop, tex),
                    IgnoresScaleOffset = (ShaderAnalysisUtil.GetFlags(shader, prop) & UnityEngine.Rendering.ShaderPropertyFlags.NoScaleOffset) != 0,
                    Space = AtoSamplingSpace.NonMeshUV,
                    UvChannel = 0,
                };

                bool ownStClean = ShaderAnalysisUtil.HasIdentityScaleOffset(material, prop);
                bool scrollClean = !ScrollRotateProps.TryGetValue(prop, out var srProp) || IsZeroVector(material, srProp);

                if (MainUvTextures.Contains(prop))
                {
                    if (mainUvClean && ownStClean && scrollClean)
                    {
                        refInfo.Space = AtoSamplingSpace.MeshUV;
                        refInfo.UvChannel = 0;
                    }
                }
                else if (UvModeTextures.TryGetValue(prop, out var uvModeProp))
                {
                    int uvMode = Mathf.RoundToInt(ShaderAnalysisUtil.GetFloat(material, uvModeProp, 0f));
                    bool decalClean = IsDecalClean(material, prop);
                    bool angleClean = ShaderAnalysisUtil.Approximately(
                        ShaderAnalysisUtil.GetFloat(material, prop + "Angle", 0f), 0f);

                    if (uvMode >= 0 && uvMode <= 3 && ownStClean && scrollClean && decalClean && angleClean
                        && (uvMode != 0 || mainUvClean))
                    {
                        refInfo.Space = AtoSamplingSpace.MeshUV;
                        refInfo.UvChannel = uvMode;
                    }
                }
                // EN: Everything else (_MatCapTex, _DitherTex, _MainGradationTex, _EmissionGradTex,
                //     _AudioLink*, _GlitterColorTex, _ParallaxMap, _OutlineVectorTex, _FurVectorTex,
                //     _Dissolve*) is sampled in a space ATO cannot reproduce - leave it alone.
                // ZH: 其余全部（_MatCapTex、_DitherTex、_MainGradationTex、_EmissionGradTex、
                //     _AudioLink*、_GlitterColorTex、_ParallaxMap、_OutlineVectorTex、_FurVectorTex、
                //     _Dissolve*）都在 ATO 无法复现的空间中采样，保持原样。

                result.Textures.Add(refInfo);
            }

            return result;
        }

        /// <summary>
        /// EN: lilToon's decal / copy / MSDF features remap the second and third layer UVs.
        /// ZH: lilToon 的 decal / copy / MSDF 功能会重映射第二、第三层的 UV。
        /// </summary>
        private static bool IsDecalClean(Material m, string prop)
        {
            if (prop != "_Main2ndTex" && prop != "_Main3rdTex") return true;
            return ShaderAnalysisUtil.Approximately(ShaderAnalysisUtil.GetFloat(m, prop + "IsDecal", 0f), 0f)
                   && ShaderAnalysisUtil.Approximately(ShaderAnalysisUtil.GetFloat(m, prop + "IsMSDF", 0f), 0f)
                   && ShaderAnalysisUtil.Approximately(ShaderAnalysisUtil.GetFloat(m, prop + "ShouldCopy", 0f), 0f)
                   && ShaderAnalysisUtil.Approximately(ShaderAnalysisUtil.GetFloat(m, prop + "ShouldFlipCopy", 0f), 0f)
                   && ShaderAnalysisUtil.Approximately(ShaderAnalysisUtil.GetFloat(m, prop + "ShouldFlipMirror", 0f), 0f)
                   && ShaderAnalysisUtil.Approximately(ShaderAnalysisUtil.GetFloat(m, prop + "IsLeftOnly", 0f), 0f)
                   && ShaderAnalysisUtil.Approximately(ShaderAnalysisUtil.GetFloat(m, prop + "IsRightOnly", 0f), 0f);
        }

        private static bool IsZeroVector(Material m, string name)
        {
            var v = ShaderAnalysisUtil.GetVector(m, name, Vector4.zero);
            return ShaderAnalysisUtil.Approximately(v.x, 0f) && ShaderAnalysisUtil.Approximately(v.y, 0f)
                   && ShaderAnalysisUtil.Approximately(v.z, 0f) && ShaderAnalysisUtil.Approximately(v.w, 0f);
        }

        /// <summary>
        /// EN: The set of properties whose animation invalidates atlasing for this shader family.
        ///     The animation analyzer consults this to detect animated UV transforms.
        /// ZH: 该着色器族中，一旦被动画驱动就会使图集化失效的属性集合。
        ///     动画分析器会查询它来检测被动画驱动的 UV 变换。
        /// </summary>
        public static IEnumerable<string> UvCriticalPropertyNames()
        {
            yield return "_ShiftBackfaceUV";
            yield return "_UseParallax";
            yield return "_UsePOM";
            foreach (var kv in ScrollRotateProps) yield return kv.Value;
            foreach (var kv in UvModeTextures) yield return kv.Value;
            yield return "_Main2ndTexAngle";
            yield return "_Main3rdTexAngle";
            yield return "_Main2ndTexIsDecal";
            yield return "_Main3rdTexIsDecal";
        }
    }
}
