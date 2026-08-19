using System;
using System.Collections.Generic;
using System.Globalization;
using Fosa.AvatarTextureOptimizer;
using Fosa.AvatarTextureOptimizer.API;
using UnityEngine;
using UnityEngine.Rendering;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Walks registered analysers then the built-in generic / lilToon analysers.
    /// Future lilToon versions are handled by scanning the property table + keywords
    /// instead of hard-coding every new slot.
    /// 先跑第三方分析器，再跑内置通用 / lilToon 分析器。
    /// 未来 lilToon 版本靠属性表 + 关键字扫描兼容，而不是写死每一个新槽。
    /// </summary>
    public static class ShaderAnalyzeService
    {
        public static AtoShaderAnalysis Analyze(AtoShaderAnalyzeContext ctx, AtoLog log)
        {
            if (ctx == null || ctx.Material == null)
            {
                return new AtoShaderAnalysis
                {
                    Success = false,
                    SkipReason = AtoSkipReason.UnsupportedShader,
                    SkipDetail = "null material"
                };
            }

            foreach (var ext in AtoExtensions.GetShaderAnalyzers())
            {
                try
                {
                    if (ext != null && ext.TryAnalyze(ctx, out var extResult) && extResult != null)
                    {
                        log?.VerboseInfo("Shader analyser hit: " + ext.Id + " on " + ctx.Material.name);
                        return extResult;
                    }
                }
                catch (Exception e)
                {
                    log?.Warn("Extension analyser " + ext.Id + " threw: " + e.Message);
                }
            }

            if (IsLilToon(ctx.Material.shader))
            {
                return LilToonAnalyzer.Analyze(ctx, log);
            }

            return GenericAnalyzer.Analyze(ctx, log);
        }

        public static bool IsLilToon(Shader shader)
        {
            if (shader == null) return false;
            var n = shader.name ?? "";
            return n.IndexOf("lilToon", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   n.IndexOf("liltoon", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   n.StartsWith("Hidden/lts", StringComparison.OrdinalIgnoreCase) ||
                   n.StartsWith("_lil/", StringComparison.OrdinalIgnoreCase);
        }

        public static AtoAlphaMode DetectAlphaMode(Material mat, out float cutoff)
        {
            cutoff = 0.5f;
            if (mat == null) return AtoAlphaMode.Opaque;
            if (mat.HasProperty("_Cutoff")) cutoff = mat.GetFloat("_Cutoff");

            var name = mat.shader != null ? mat.shader.name : "";
            if (name.IndexOf("Cutout", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("FurCutout", StringComparison.OrdinalIgnoreCase) >= 0)
                return AtoAlphaMode.Cutout;
            if (name.IndexOf("Transparent", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Fade", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Refraction", StringComparison.OrdinalIgnoreCase) >= 0)
                return AtoAlphaMode.Blend;

            if (mat.IsKeywordEnabled("_ALPHATEST_ON") || KeywordOn(mat, "_ALPHATEST_ON"))
                return AtoAlphaMode.Cutout;
            if (mat.IsKeywordEnabled("_ALPHABLEND_ON") || mat.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON") ||
                KeywordOn(mat, "_ALPHABLEND_ON") || KeywordOn(mat, "_ALPHAPREMULTIPLY_ON"))
                return AtoAlphaMode.Blend;

            if (mat.HasProperty("_Mode"))
            {
                var mode = mat.GetFloat("_Mode");
                if (Mathf.Approximately(mode, 1f)) return AtoAlphaMode.Cutout;
                if (mode >= 2f) return AtoAlphaMode.Blend;
            }

            if (mat.HasProperty("_TransparentMode"))
            {
                // lilToon uses shader swap; this float is the OnePass/TwoPass variant.
            }

            var tag = mat.GetTag("RenderType", false, "");
            if (string.Equals(tag, "TransparentCutout", StringComparison.OrdinalIgnoreCase)) return AtoAlphaMode.Cutout;
            if (string.Equals(tag, "Transparent", StringComparison.OrdinalIgnoreCase)) return AtoAlphaMode.Blend;

            var src = mat.HasProperty("_SrcBlend") ? (BlendMode)mat.GetInt("_SrcBlend") : BlendMode.One;
            var dst = mat.HasProperty("_DstBlend") ? (BlendMode)mat.GetInt("_DstBlend") : BlendMode.Zero;
            if (src != BlendMode.One || dst != BlendMode.Zero) return AtoAlphaMode.Blend;

            return AtoAlphaMode.Opaque;
        }

        public static bool KeywordOn(Material mat, string kw)
        {
            if (mat == null || string.IsNullOrEmpty(kw)) return false;
            try
            {
                return mat.IsKeywordEnabled(kw);
            }
            catch
            {
                return false;
            }
        }

        public static bool HasNonIdentitySt(Material mat, string texProp)
        {
            var stName = texProp + "_ST";
            if (!mat.HasProperty(stName)) return false;
            var st = mat.GetVector(stName);
            return !ApproximatelyIdentitySt(st);
        }

        public static bool ApproximatelyIdentitySt(Vector4 st)
        {
            return Mathf.Abs(st.x - 1f) < 1e-4f &&
                   Mathf.Abs(st.y - 1f) < 1e-4f &&
                   Mathf.Abs(st.z) < 1e-4f &&
                   Mathf.Abs(st.w) < 1e-4f;
        }

        public static bool HasScrollRotate(Material mat, string texProp)
        {
            var n = texProp + "_ScrollRotate";
            if (!mat.HasProperty(n)) return false;
            var v = mat.GetVector(n);
            return v.sqrMagnitude > 1e-8f;
        }

        public static bool LooksLikeDecal(Material mat, string texProp)
        {
            return FloatOn(mat, texProp + "IsDecal") ||
                   FloatOn(mat, texProp + "IsLeftOnly") ||
                   FloatOn(mat, texProp + "IsRightOnly") ||
                   FloatOn(mat, "_AsDecal");
        }

        public static bool FloatOn(Material mat, string prop)
        {
            return mat != null && mat.HasProperty(prop) && Mathf.Abs(mat.GetFloat(prop)) > 1e-5f;
        }

        public static int ReadUvMode(Material mat, string texProp, int fallback = 0)
        {
            var names = new[]
            {
                texProp + "_UVMode",
                texProp + "UVMode",
                texProp + "_UV",
                "_UVMode"
            };
            foreach (var n in names)
            {
                if (mat.HasProperty(n)) return Mathf.RoundToInt(mat.GetFloat(n));
            }

            return fallback;
        }

        public static bool IsAnimated(AtoShaderAnalyzeContext ctx, string prop)
        {
            if (ctx?.AnimatedProperties == null || prop == null) return false;
            return ctx.AnimatedProperties.TryGetValue(prop, out var v) && v;
        }
    }

    static class GenericAnalyzer
    {
        public static AtoShaderAnalysis Analyze(AtoShaderAnalyzeContext ctx, AtoLog log)
        {
            var mat = ctx.Material;
            var result = new AtoShaderAnalysis
            {
                Material = mat,
                Shader = mat.shader,
                Success = true,
                AlphaMode = ShaderAnalyzeService.DetectAlphaMode(mat, out var cutoff)
            };
            result.Cutoff = cutoff;

            if (mat.shader == null)
            {
                result.Success = false;
                result.SkipReason = AtoSkipReason.UnsupportedShader;
                result.SkipDetail = "missing shader";
                return result;
            }

            string[] names;
            try { names = mat.GetTexturePropertyNames(); }
            catch { names = Array.Empty<string>(); }

            foreach (var prop in names)
            {
                var tex = mat.GetTexture(prop) as Texture2D;
                if (tex == null) continue;

                var slot = BuildSlot(ctx, mat, prop, tex, result.AlphaMode, result.Cutoff);
                result.Slots.Add(slot);
            }

            return result;
        }

        public static AtoTextureSlot BuildSlot(AtoShaderAnalyzeContext ctx, Material mat, string prop, Texture2D tex,
            AtoAlphaMode alpha, float cutoff)
        {
            var slot = new AtoTextureSlot
            {
                Material = mat,
                PropertyName = prop,
                Texture = tex,
                Kind = ShaderCatalog.GuessKind(prop, tex),
                AlphaMode = alpha,
                Cutoff = cutoff,
                IsSrgb = tex.isDataSRGB,
                FilterMode = tex.filterMode,
                ColorSpace = tex.isDataSRGB ? ColorSpace.Gamma : ColorSpace.Linear
            };

            if (ShaderCatalog.TryGet(prop, out var info) && info.DefaultUsedChannels != null)
                slot.UsedChannels = (bool[])info.DefaultUsedChannels.Clone();

            if (ShaderCatalog.IsLikelyNonMesh(prop) || LooksMatcapOrScreen(prop))
            {
                slot.SkipReason = AtoSkipReason.SpecialUse;
                slot.SkipDetail = "non-mesh / matcap / screen UV";
                return slot;
            }

            if (ShaderAnalyzeService.LooksLikeDecal(mat, prop))
            {
                slot.SkipReason = AtoSkipReason.SpecialUse;
                slot.SkipDetail = "decal";
                return slot;
            }

            var uvMode = ShaderAnalyzeService.ReadUvMode(mat, prop, 0);
            if (uvMode >= 4)
            {
                slot.SkipReason = AtoSkipReason.SpecialUse;
                slot.SkipDetail = "uvMode=" + uvMode;
                return slot;
            }

            slot.UvChannel = Mathf.Clamp(uvMode, 0, 7);

            if (ShaderAnalyzeService.HasNonIdentitySt(mat, prop) ||
                ShaderAnalyzeService.HasScrollRotate(mat, prop) ||
                ShaderAnalyzeService.IsAnimated(ctx, prop + "_ST") ||
                ShaderAnalyzeService.IsAnimated(ctx, prop + "_ScrollRotate") ||
                (ctx != null && ctx.HasAnimatedUvTransform &&
                 (ShaderAnalyzeService.IsAnimated(ctx, prop + "_ST") || ShaderAnalyzeService.IsAnimated(ctx, "material." + prop + "_ST"))))
            {
                slot.HasIdentitySt = false;
                slot.SkipReason = AtoSkipReason.HasSTTransform;
                slot.SkipDetail = "ST / scroll / rotation";
                return slot;
            }

            // Also treat a non-identity main ST as affecting properties that share main UV.
            if (prop != "_MainTex" && mat.HasProperty("_MainTex_ST") &&
                !ShaderAnalyzeService.ApproximatelyIdentitySt(mat.GetVector("_MainTex_ST")) &&
                UsesMainUv(prop, uvMode))
            {
                slot.HasIdentitySt = false;
                slot.SkipReason = AtoSkipReason.HasSTTransform;
                slot.SkipDetail = "_MainTex_ST";
                return slot;
            }

            if (ShaderAnalyzeService.FloatOn(mat, "_ShiftBackfaceUV"))
            {
                slot.SkipReason = AtoSkipReason.SpecialUse;
                slot.SkipDetail = "_ShiftBackfaceUV";
                return slot;
            }

            return slot;
        }

        static bool UsesMainUv(string prop, int uvMode) => uvMode == 0;

        static bool LooksMatcapOrScreen(string prop)
        {
            var n = prop ?? "";
            return n.IndexOf("MatCap", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   n.IndexOf("Matcap", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   n.IndexOf("Cube", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   n.IndexOf("Grab", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   n.IndexOf("Screen", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    static class LilToonAnalyzer
    {
        static readonly string[] UseFlags =
        {
            "_UseMain2ndTex", "_UseMain3rdTex", "_UseBumpMap", "_UseBump2ndMap",
            "_UseAnisotropy", "_UseBacklight", "_UseShadow", "_UseRimShade",
            "_UseReflection", "_UseMatCap", "_UseMatCap2nd", "_UseRim",
            "_UseGlitter", "_UseEmission", "_UseEmission2nd", "_UseParallax",
            "_UseAudioLink", "_UseOutline"
        };

        public static AtoShaderAnalysis Analyze(AtoShaderAnalyzeContext ctx, AtoLog log)
        {
            // Start from the generic property scan (future-proof), then apply lilToon-specific gates.
            // 先做通用属性扫描（面向未来版本），再套 lilToon 专用开关。
            var result = GenericAnalyzer.Analyze(ctx, log);
            var mat = ctx.Material;

            var enabled = new HashSet<string>(StringComparer.Ordinal);
            enabled.Add("_MainTex");
            enabled.Add("_BaseMap");
            enabled.Add("_BaseColorMap");
            enabled.Add("_AlphaMask");
            enabled.Add("_MainColorAdjustMask");
            if (ShaderAnalyzeService.FloatOn(mat, "_UseBumpMap")) enabled.Add("_BumpMap");
            if (ShaderAnalyzeService.FloatOn(mat, "_UseBump2ndMap"))
            {
                enabled.Add("_Bump2ndMap");
                enabled.Add("_Bump2ndScaleMask");
            }

            if (ShaderAnalyzeService.FloatOn(mat, "_UseAnisotropy"))
            {
                enabled.Add("_AnisotropyTangentMap");
                enabled.Add("_AnisotropyScaleMask");
                enabled.Add("_AnisotropyShiftNoiseMask");
            }

            if (ShaderAnalyzeService.FloatOn(mat, "_UseBacklight")) enabled.Add("_BacklightColorTex");
            if (ShaderAnalyzeService.FloatOn(mat, "_UseShadow"))
            {
                enabled.Add("_ShadowStrengthMask");
                enabled.Add("_ShadowBorderMask");
                enabled.Add("_ShadowBlurMask");
                enabled.Add("_ShadowColorTex");
                enabled.Add("_Shadow2ndColorTex");
                enabled.Add("_Shadow3rdColorTex");
            }

            if (ShaderAnalyzeService.FloatOn(mat, "_UseRimShade")) enabled.Add("_RimShadeMask");
            if (ShaderAnalyzeService.FloatOn(mat, "_UseReflection"))
            {
                enabled.Add("_SmoothnessTex");
                enabled.Add("_MetallicGlossMap");
                enabled.Add("_ReflectionColorTex");
            }

            if (ShaderAnalyzeService.FloatOn(mat, "_UseMatCap"))
            {
                enabled.Add("_MatCapTex");
                enabled.Add("_MatCapBlendMask");
                enabled.Add("_MatCapBumpMap");
            }

            if (ShaderAnalyzeService.FloatOn(mat, "_UseMatCap2nd"))
            {
                enabled.Add("_MatCap2ndTex");
                enabled.Add("_MatCap2ndBlendMask");
                enabled.Add("_MatCap2ndBumpMap");
            }

            if (ShaderAnalyzeService.FloatOn(mat, "_UseRim")) enabled.Add("_RimColorTex");
            if (ShaderAnalyzeService.FloatOn(mat, "_UseGlitter"))
            {
                enabled.Add("_GlitterColorTex");
                enabled.Add("_GlitterShapeTex");
            }

            if (ShaderAnalyzeService.FloatOn(mat, "_UseEmission"))
            {
                enabled.Add("_EmissionMap");
                enabled.Add("_EmissionBlendMask");
                enabled.Add("_EmissionGradTex");
            }

            if (ShaderAnalyzeService.FloatOn(mat, "_UseEmission2nd"))
            {
                enabled.Add("_Emission2ndMap");
                enabled.Add("_Emission2ndBlendMask");
                enabled.Add("_Emission2ndGradTex");
            }

            if (ShaderAnalyzeService.FloatOn(mat, "_UseParallax")) enabled.Add("_ParallaxMap");
            if (ShaderAnalyzeService.FloatOn(mat, "_UseMain2ndTex"))
            {
                enabled.Add("_Main2ndTex");
                enabled.Add("_Main2ndBlendMask");
                enabled.Add("_Main2ndDissolveMask");
                enabled.Add("_Main2ndDissolveNoiseMask");
            }

            if (ShaderAnalyzeService.FloatOn(mat, "_UseMain3rdTex"))
            {
                enabled.Add("_Main3rdTex");
                enabled.Add("_Main3rdBlendMask");
                enabled.Add("_Main3rdDissolveMask");
                enabled.Add("_Main3rdDissolveNoiseMask");
            }

            var shaderName = mat.shader != null ? mat.shader.name : "";
            if (shaderName.IndexOf("Outline", StringComparison.OrdinalIgnoreCase) >= 0 ||
                ShaderAnalyzeService.FloatOn(mat, "_UseOutline"))
            {
                enabled.Add("_OutlineTex");
                enabled.Add("_OutlineWidthMask");
                enabled.Add("_OutlineVectorTex");
            }

            if (shaderName.IndexOf("Fur", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                enabled.Add("_FurNoiseMask");
                enabled.Add("_FurMask");
                enabled.Add("_FurLengthMask");
                enabled.Add("_FurVectorTex");
            }

            // Unknown future texture slots stay if they look like mesh-UV maps.
            // 未知的未来贴图槽：只要看起来是网格 UV 采样就保留。
            for (int i = result.Slots.Count - 1; i >= 0; i--)
            {
                var s = result.Slots[i];
                if (s.SkipReason != AtoSkipReason.None) continue;
                if (enabled.Contains(s.PropertyName)) continue;
                if (ShaderCatalog.TryGet(s.PropertyName, out _))
                {
                    // Known but gated off. / 已知但开关关闭。
                    result.Slots.RemoveAt(i);
                    continue;
                }

                log?.VerboseInfo("lilToon future/unknown texture kept for analysis: " + s.PropertyName);
            }

            return result;
        }
    }
}
