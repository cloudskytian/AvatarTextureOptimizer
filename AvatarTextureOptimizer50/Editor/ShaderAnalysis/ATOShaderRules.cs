// -----------------------------------------------------------------------------
// ATOShaderRules.cs — shader property tables & heuristics.
// ATOShaderRules.cs — 着色器属性表与启发式规则。
//
// lilToon table below was extracted from lilToon 2.3.4 `Shader/lts.shader` sources
// (Properties block + lil_common_frag.hlsl usage). Generic rules use Unity's
// ShaderPropertyFlags (MainTexture/Normal) plus well-known property names, so future
// shader versions that keep naming conventions stay compatible.
// lilToon 表提取自 lilToon 2.3.4 `Shader/lts.shader`（Properties 块与 lil_common_frag.hlsl
// 用法）。通用规则基于 Unity ShaderPropertyFlags（MainTexture/Normal）与常见命名，
// 因此保持命名约定的未来版本可直接兼容。
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>How a texture property is sampled / 纹理属性的采样方式。</summary>
    internal enum SampleKind
    {
        MeshUV,      // sampled with a mesh UV channel (channel resolved at runtime) / 按网格 UV 通道采样
        NotMeshUV,   // matcap / rim / screen / ramp — cannot remap / 非网格UV（MatCap/Rim/屏幕/渐变），不可重映射
    }

    internal readonly struct PropRule
    {
        public readonly string property;
        public readonly TexRole role;
        public readonly SampleKind sample;
        public readonly string uvModeProp;  // int prop selecting UV0-3 (may be null) / 选择UV0-3的int属性
        public readonly int uvModeOffset;   // value offset applied to uvModeProp value / UVMode 值偏移
        public readonly bool srgb;          // relevant only for color roles / 仅颜色角色有效

        public PropRule(string property, TexRole role, SampleKind sample = SampleKind.MeshUV,
            string uvModeProp = null, int uvModeOffset = 0, bool srgb = true)
        {
            this.property = property;
            this.role = role;
            this.sample = sample;
            this.uvModeProp = uvModeProp;
            this.uvModeOffset = uvModeOffset;
            this.srgb = srgb;
        }
    }

    internal static class ATOShaderRules
    {
        // ------------------------------------------------------------------ //
        // lilToon (verified against 2.3.4) / lilToon（对照 2.3.4 校验）
        // ------------------------------------------------------------------ //

        /// <summary>Main-UV (uvMain) bound properties. ST/scroll checked separately.
        /// 绑定主 UV（uvMain）的属性。ST/滚动另行检查。</summary>
        internal static readonly PropRule[] LilToonUvMain =
        {
            new PropRule("_MainTex", TexRole.Main),
            new PropRule("_MainGradationTex", TexRole.ExtraColor),
            new PropRule("_MainColorAdjustMask", TexRole.Gray, srgb: false),
            new PropRule("_Main2ndBlendMask", TexRole.Gray, srgb: false),
            new PropRule("_Main2ndDissolveMask", TexRole.Gray, srgb: false),
            new PropRule("_Main3rdBlendMask", TexRole.Gray, srgb: false),
            new PropRule("_Main3rdDissolveMask", TexRole.Gray, srgb: false),
            new PropRule("_AlphaMask", TexRole.Gray, srgb: false),
            new PropRule("_BumpMap", TexRole.Normal, srgb: false),
            new PropRule("_Bump2ndScaleMask", TexRole.Gray, srgb: false),
            new PropRule("_AnisotropyTangentMap", TexRole.Normal, srgb: false),
            new PropRule("_AnisotropyScaleMask", TexRole.Gray, srgb: false),
            new PropRule("_BacklightColorTex", TexRole.ExtraColor),
            new PropRule("_ShadowStrengthMask", TexRole.Gray, srgb: false),
            new PropRule("_ShadowBorderMask", TexRole.Gray, srgb: false),
            new PropRule("_ShadowBlurMask", TexRole.Gray, srgb: false),
            new PropRule("_ShadowColorTex", TexRole.ExtraColor),
            new PropRule("_Shadow2ndColorTex", TexRole.ExtraColor),
            new PropRule("_Shadow3rdColorTex", TexRole.ExtraColor),
            new PropRule("_RimShadeMask", TexRole.Gray, srgb: false),
            new PropRule("_SmoothnessTex", TexRole.Gray, srgb: false),
            new PropRule("_MetallicGlossMap", TexRole.Gray, srgb: false),
            new PropRule("_ReflectionColorTex", TexRole.ExtraColor),
            new PropRule("_MatCapBlendMask", TexRole.Gray, srgb: false),
            new PropRule("_RimColorTex", TexRole.ExtraColor),
            new PropRule("_OutlineTex", TexRole.ExtraColor),
            new PropRule("_OutlineWidthMask", TexRole.Gray, srgb: false),
            new PropRule("_OutlineVectorTex", TexRole.Normal, srgb: false),
            new PropRule("_DissolveMask", TexRole.Gray, srgb: false),
            new PropRule("_DissolveNoiseMask", TexRole.Gray, srgb: false),
            new PropRule("_AudioLinkMask", TexRole.Gray, srgb: false, uvModeProp: "_AudioLinkMask_UVMode"),
        };

        /// <summary>Properties with selectable UV (UV0..UV3; value 4+ = non-mesh → skip).
        /// 可选 UV 的属性（UV0..UV3；值≥4 为非网格UV→跳过）。</summary>
        internal static readonly PropRule[] LilToonUvSelectable =
        {
            new PropRule("_Main2ndTex", TexRole.ExtraColor, uvModeProp: "_Main2ndTex_UVMode"),
            new PropRule("_Main3rdTex", TexRole.ExtraColor, uvModeProp: "_Main3rdTex_UVMode"),
            new PropRule("_Main2ndDissolveNoiseMask", TexRole.Gray, srgb: false),
            new PropRule("_Main3rdDissolveNoiseMask", TexRole.Gray, srgb: false),
            new PropRule("_Bump2ndMap", TexRole.Normal, srgb: false, uvModeProp: "_Bump2ndMap_UVMode"),
            new PropRule("_GlitterColorTex", TexRole.ExtraColor, uvModeProp: "_GlitterColorTex_UVMode"),
            new PropRule("_EmissionMap", TexRole.ExtraColor, uvModeProp: "_EmissionMap_UVMode"),
            new PropRule("_EmissionBlendMask", TexRole.Gray, srgb: false),
            new PropRule("_Emission2ndMap", TexRole.ExtraColor, uvModeProp: "_Emission2ndMap_UVMode"),
            new PropRule("_Emission2ndBlendMask", TexRole.Gray, srgb: false),
        };

        /// <summary>Properties that are never mesh-UV sampled (whitelist).
        /// 永远不是网格UV采样的属性（白名单）。</summary>
        internal static readonly string[] LilToonNotMeshUV =
        {
            "_MatCapTex", "_MatCapBumpMap", "_MatCap2ndTex", "_MatCap2ndBumpMap",
            "_GlitterShapeTex", "_EmissionGradTex", "_Emission2ndGradTex",
            "_Ramp", "_DitherTex", "_AudioLinkLocalMap", "_ParallaxMap",
            "_AnisotropyShiftNoiseMask", // sampled with noise-warped UV / 噪声扰动UV采样
        };

        /// <summary>Vector/float props that indicate UV animation/rotation for uvMain.
        /// 表示主UV动画/旋转变换的 Vector/Float 属性。</summary>
        internal static readonly string[] LilToonTransformProps =
        {
            "_MainTex_ScrollRotate", "_OutlineTex_ScrollRotate",
        };

        /// <summary>Per-texture transform props for selectable-UV textures.
        /// 可选UV贴图的独立变换属性。</summary>
        internal static readonly string[] LilToonPerTexTransformSuffixes =
        {
            "_ScrollRotate", "_Angle",
        };

        /// <summary>lilToon fur shader family shifts UVs per shell — treat as unsupported.
        /// lilToon 系毛材质按壳偏移UV——视为不支持的材质（白名单）。</summary>
        internal static bool IsLilToonFurShader(string shaderName) =>
            shaderName != null && (shaderName.Contains("Fur") || shaderName.Contains("fakeshadow"));

        internal static bool IsLilToon(string shaderName)
        {
            if (string.IsNullOrEmpty(shaderName)) return false;
            return shaderName.Contains("lilToon") || shaderName.Contains("Hidden/lilToon")
                   || shaderName.Contains("ltspass") || shaderName.Contains("_lil/");
        }

        // ------------------------------------------------------------------ //
        // Generic / standard shader rules (Standard, URP/Lit, most conventions)
        // 通用规则（Standard、URP/Lit 及大多数命名约定一致的着色器）
        // ------------------------------------------------------------------ //

        internal static readonly PropRule[] StandardUv0 =
        {
            new PropRule("_MainTex", TexRole.Main),
            new PropRule("_BumpMap", TexRole.Normal, srgb: false),
            new PropRule("_MetallicGlossMap", TexRole.Gray, srgb: false),
            new PropRule("_OcclusionMap", TexRole.Gray, srgb: false),
            new PropRule("_EmissionMap", TexRole.ExtraColor),
            new PropRule("_ParallaxMap", TexRole.Gray, srgb: false),
            new PropRule("_DetailMask", TexRole.Gray, srgb: false),
        };

        /// <summary>Name-based role heuristics for unknown properties in generic shaders.
        /// 通用着色器中未知属性的命名启发式。</summary>
        internal static TexRole? GuessRoleByName(string prop, bool normalFlag, bool mainFlag)
        {
            if (normalFlag) return TexRole.Normal;
            if (mainFlag) return TexRole.Main;
            var p = prop.ToLowerInvariant();
            if (p.Contains("normal") || p.Contains("bump")) return TexRole.Normal;
            if (p.Contains("matcap") || p.Contains("ramp") || p.Contains("gradtex") ||
                p.Contains("gradientmap") || p.EndsWith("ramp")) return TexRole.Main; // caller marks NotMeshUV / 由调用方标记非网格UV
            if (p.Contains("mask") || p.Contains("metallic") || p.Contains("occlusion") ||
                p.Contains("smooth") || p.Contains("rough") || p.Contains("ao") ||
                p.Contains("thickness") || p.Contains("alpha")) return TexRole.Gray;
            if (p.Contains("emission") || p.Contains("emit")) return TexRole.ExtraColor;
            if (p.Contains("detail")) return TexRole.ExtraColor;
            if (p.Contains("main") || p.Contains("base") || p.Contains("albedo") ||
                p.Contains("color") || p.Contains("tex")) return TexRole.Main;
            return null;
        }

        /// <summary>Properties meaning "this texture is not sampled by plain mesh UV".
        /// 表示“此贴图并非按普通网格UV采样”的命名特征。</summary>
        internal static bool LooksNonMeshUV(string prop)
        {
            var p = prop.ToLowerInvariant();
            return p.Contains("matcap") || p.Contains("ramp") || p.Contains("gradtex") ||
                   p.Contains("gradient") || p.Contains("dither") || p.Contains("lookup") ||
                   p.Contains("lut") || p.Contains("noise"); // noise textures usually warped / 噪声图通常被扰动
        }

        /// <summary>Render-mode guess from shader name / keywords / queue (strictest-first policy).
        /// 从着色器名/关键字/队列推断透明模式（从严策略）。</summary>
        internal static AlphaMode GuessAlphaMode(Material m)
        {
            var shaderName = m.shader != null ? m.shader.name : "";
            var lower = shaderName.ToLowerInvariant();
            if (lower.Contains("cutout") || lower.Contains("cut")) return AlphaMode.Cutout;
            if (lower.Contains("transparent") || lower.Contains("trans") || lower.Contains("overlay") ||
                lower.Contains("fade")) return AlphaMode.Blend;

            if (m.IsKeywordEnabled("_ALPHATEST_ON")) return AlphaMode.Cutout;
            if (m.IsKeywordEnabled("_ALPHABLEND_ON") || m.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON"))
                return AlphaMode.Blend;

            // Surface bookkeeping / 表面数据
            int surface = m.HasProperty("_Surface") ? Mathf.RoundToInt(m.GetFloat("_Surface")) : -1;
            if (surface == 1)
            {
                int blend = m.HasProperty("_Blend") ? Mathf.RoundToInt(m.GetFloat("_Blend")) : 0;
                return blend == 0 ? AlphaMode.Opaque : AlphaMode.Blend;
            }

            int q = m.renderQueue;
            if (q >= 3000) return AlphaMode.Blend;
            if (q >= 2450 && q < 3000) return AlphaMode.Cutout;
            return AlphaMode.Opaque;
        }

        /// <summary>Cutoff value collection / Cutoff 值收集。</summary>
        internal static void CollectCutoffs(Material m, SortedSet<float> into)
        {
            foreach (var p in new[] { "_Cutoff", "_SubpassCutoff", "_AlphaMaskValue", "_CutoffOffset" })
            {
                if (m.HasProperty(p))
                {
                    var v = m.GetFloat(p);
                    if (v > 0f && v < 1f) into.Add(v);
                }
            }
        }
    }
}
