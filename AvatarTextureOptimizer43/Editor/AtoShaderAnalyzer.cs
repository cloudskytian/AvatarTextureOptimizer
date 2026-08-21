using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Fosa.ATO;

namespace Fosa.ATO.Editor
{
    /// <summary>
    /// Shader property / keyword analyzer.
    /// Built-in: lilToon 2.x (read from jp.lilxyzw.liltoon 2.3.4 shaders) + Unity standard keywords.
    /// Unknown shaders: ShaderUtil property table. Unparseable UV/ST → ineligible + warning.
    /// 着色器属性/关键字分析。lilToon 按 2.3.4 源码，其余走标准关键字与 ShaderUtil。
    /// </summary>
    public static class AtoShaderAnalyzer
    {
        static readonly Regex StProp = new Regex(@"^(.+)_ST$", RegexOptions.Compiled);
        static readonly Regex ScrollProp = new Regex(@"^(.+)_ScrollRotate$", RegexOptions.Compiled);
        static readonly Regex UvModeProp = new Regex(@"^(.+)_UVMode$", RegexOptions.Compiled);

        static readonly HashSet<string> NormalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "_BumpMap", "_Bump2ndMap", "_DetailNormalMap", "_NormalMap", "_NormalTex",
            "_OutlineVectorTex", "_AnisotropyTangentMap", "_MatCapBumpMap", "_MatCap2ndBumpMap"
        };

        static readonly HashSet<string> MaskNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "_MainColorAdjustMask", "_AlphaMask", "_ShadowBorderMask", "_ShadowBlurMask",
            "_ShadowStrengthMask", "_RimShadeMask", "_EmissionBlendMask", "_Emission2ndBlendMask",
            "_Bump2ndScaleMask", "_AnisotropyScaleMask", "_AnisotropyShiftNoiseMask",
            "_SmoothnessTex", "_MetallicGlossMap", "_OcclusionMap", "_ParallaxMap",
            "_MatCapBlendMask", "_MatCap2ndBlendMask", "_RimColorTex", "_GlitterColorTex",
            "_OutlineWidthMask", "_AudioLinkMask", "_DissolveMask", "_DissolveNoiseMask",
            "_BitMask", "_UDIMDiscardTex", "_DitherTex", "_Main2ndBlendMask", "_Main3rdBlendMask"
        };

        static readonly HashSet<string> SpecialPurpose = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "_MatCapTex", "_MatCap2ndTex", "_MainGradationTex", "_EmissionGradTex", "_Emission2ndGradTex",
            "_GrabTex", "_ReflectionCubeTex"
        };

        /// <summary>lilToon _TransparentMode: Opaque|Cutout|Transparent|Refraction|Fur|FurCutout|Gem (lts.shader). </summary>
        static readonly Dictionary<int, AtoShaderInfo> BakeCache = new Dictionary<int, AtoShaderInfo>();

        public static void ClearBakeCache() => BakeCache.Clear();

        public static AtoShaderInfo Analyze(Material mat)
        {
            if (mat == null || mat.shader == null)
                return new AtoShaderInfo { Compatible = false, Warning = "null material/shader" };

            int id = mat.GetInstanceID();
            if (BakeCache.TryGetValue(id, out var hit)) return hit;

            foreach (var ext in AtoApi.ShaderAnalyzers)
            {
                try
                {
                    var custom = ext.Analyze(mat);
                    if (custom != null) return custom;
                }
                catch (Exception e)
                {
                    AtoLog.Warn("Extension analyzer failed: " + e.Message);
                }
            }

            var shader = mat.shader;
            var name = shader.name ?? "";
            var info = new AtoShaderInfo();
            foreach (var k in mat.shaderKeywords) info.Keywords.Add(k);

            bool lil = name.IndexOf("lilToon", StringComparison.OrdinalIgnoreCase) >= 0
                       || name.IndexOf("Hidden/lts", StringComparison.OrdinalIgnoreCase) >= 0
                       || name.IndexOf("Hidden/_lil/", StringComparison.OrdinalIgnoreCase) >= 0;

            if (lil) FillLilToon(mat, info);
            else FillGeneric(mat, info);

            BakeCache[id] = info;
            return info;
        }

        static void FillLilToon(Material mat, AtoShaderInfo info)
        {
            // From Shader/lts.shader: _TransparentMode "Rendering Mode|Opaque|Cutout|Transparent|Refraction|Fur|FurCutout|Gem"
            int tm = GetInt(mat, "_TransparentMode", InferLegacyRenderMode(mat));
            switch (tm)
            {
                case 1: // Cutout
                case 5: // FurCutout
                    info.AlphaMode = AtoAlphaMode.Cutout;
                    break;
                case 0: // Opaque
                    info.AlphaMode = AtoAlphaMode.Opaque;
                    break;
                default:
                    info.AlphaMode = AtoAlphaMode.Blend;
                    break;
            }
            info.Cutoff = GetFloat(mat, "_Cutoff", 0.5f);

            int count = ShaderUtil.GetPropertyCount(mat.shader);
            var texProps = new HashSet<string>();
            var extra = new Dictionary<string, int>(StringComparer.Ordinal);

            for (int i = 0; i < count; i++)
            {
                var pName = ShaderUtil.GetPropertyName(mat.shader, i);
                var pType = ShaderUtil.GetPropertyType(mat.shader, i);
                if (pType == ShaderUtil.ShaderPropertyType.TexEnv)
                    texProps.Add(pName);
                else
                    extra[pName] = i;
            }

            foreach (var p in texProps)
            {
                if (LilFeatureOff(mat, p)) continue;
                var slot = BuildSlot(mat, p, extra);
                // lilToon MainTex is always UV0 (no _MainTex_UVMode in lts.shader).
                if (p == "_MainTex") slot.UvChannel = 0;
                info.Slots.Add(slot);
            }
        }

        static void FillGeneric(Material mat, AtoShaderInfo info)
        {
            // Standard / URP Lit / HDRP / Poiyomi-like keyword heuristics.
            // 标准关键字启发式。
            bool cutout = mat.IsKeywordEnabled("_ALPHATEST_ON")
                          || mat.IsKeywordEnabled("_ALPHATEST")
                          || HasFloat(mat, "_AlphaClip", out var ac) && ac > 0.5f;
            bool blend = mat.IsKeywordEnabled("_ALPHABLEND_ON")
                         || mat.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT")
                         || mat.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON");
            if (HasFloat(mat, "_Mode", out var mode))
            {
                // Legacy Standard shader: 0 Opaque, 1 Cutout, 2 Fade, 3 Transparent
                if (Mathf.RoundToInt(mode) == 1) cutout = true;
                if (Mathf.RoundToInt(mode) >= 2) blend = true;
            }
            if (HasFloat(mat, "_Surface", out var surface) && surface > 0.5f) blend = true;

            if (cutout) info.AlphaMode = AtoAlphaMode.Cutout;
            else if (blend) info.AlphaMode = AtoAlphaMode.Blend;
            else info.AlphaMode = AtoAlphaMode.Opaque;
            info.Cutoff = GetFloat(mat, "_Cutoff", GetFloat(mat, "_CutoffA", 0.5f));

            int count = ShaderUtil.GetPropertyCount(mat.shader);
            var extra = new Dictionary<string, int>(StringComparer.Ordinal);
            var texProps = new List<string>();
            for (int i = 0; i < count; i++)
            {
                var pName = ShaderUtil.GetPropertyName(mat.shader, i);
                var pType = ShaderUtil.GetPropertyType(mat.shader, i);
                if (pType == ShaderUtil.ShaderPropertyType.TexEnv) texProps.Add(pName);
                else extra[pName] = i;
            }
            foreach (var p in texProps)
                info.Slots.Add(BuildSlot(mat, p, extra));
        }

        static AtoShaderSlot BuildSlot(Material mat, string prop, Dictionary<string, int> extra)
        {
            var slot = new AtoShaderSlot
            {
                PropertyName = prop,
                UvChannel = 0,
                Class = Classify(mat, prop),
                HasST = extra.ContainsKey(prop + "_ST") || mat.HasProperty(prop + "_ST"),
                HasScrollRotate = extra.ContainsKey(prop + "_ScrollRotate") || mat.HasProperty(prop + "_ScrollRotate"),
                SpecialPurpose = SpecialPurpose.Contains(prop)
                                  || prop.IndexOf("MatCap", StringComparison.OrdinalIgnoreCase) >= 0
                                  || prop.IndexOf("Grab", StringComparison.OrdinalIgnoreCase) >= 0
                                  || prop.IndexOf("Cubemap", StringComparison.OrdinalIgnoreCase) >= 0
                                  || prop.IndexOf("Cube", StringComparison.OrdinalIgnoreCase) >= 0
                                  || IsDecal(mat, prop)
            };

            // UV mode. lilToon: "UV0|UV1|UV2|UV3|MatCap/Rim"
            string uvModeName = prop + "_UVMode";
            if (mat.HasProperty(uvModeName))
            {
                int mode = GetInt(mat, uvModeName, 0);
                if (mode >= 0 && mode <= 3) slot.UvChannel = mode;
                else
                {
                    slot.UvChannel = -1;
                    slot.SpecialPurpose = true;
                }
            }

            // Emission Rim UV mode is not a mesh UV. Emission 的 Rim 不是网格 UV。
            if (prop.StartsWith("_Emission", StringComparison.Ordinal) && mat.HasProperty(prop + "_UVMode"))
            {
                int mode = GetInt(mat, prop + "_UVMode", 0);
                if (mode >= 4) { slot.UvChannel = -1; slot.SpecialPurpose = true; }
            }

            if (slot.Class == AtoTextureClass.Normal)
                slot.CompanionOf = GuessMainOfNormal(prop);
            else if (slot.Class == AtoTextureClass.Gray)
                slot.CompanionOf = GuessMainOfMask(prop);

            return slot;
        }

        /// <summary>
        /// lilToon 2.3.4 `_Use*` toggles (lts.shader / lilMaterialProperties). Off → skip that slot.
        /// 功能开关为 0 的贴图槽直接跳过。
        /// </summary>
        static bool LilFeatureOff(Material mat, string prop)
        {
            bool Off(string n) => mat.HasProperty(n) && mat.GetFloat(n) < 0.5f;
            if (prop.StartsWith("_Main2nd") && Off("_UseMain2ndTex")) return true;
            if (prop.StartsWith("_Main3rd") && Off("_UseMain3rdTex")) return true;
            if (prop.StartsWith("_Shadow") && Off("_UseShadow")) return true;
            if (prop.StartsWith("_Emission2nd") && Off("_UseEmission2nd")) return true;
            if (prop.StartsWith("_Emission") && Off("_UseEmission")) return true;
            if (prop == "_BumpMap" && Off("_UseBumpMap")) return true;
            if (prop.StartsWith("_Bump2nd") && Off("_UseBump2ndMap")) return true;
            if (prop.StartsWith("_MatCap2nd") && Off("_UseMatCap2nd")) return true;
            if (prop.StartsWith("_MatCap") && Off("_UseMatCap")) return true;
            if (prop.StartsWith("_Rim") && Off("_UseRim")) return true;
            if (prop.StartsWith("_Glitter") && Off("_UseGlitter")) return true;
            if (prop.StartsWith("_Backlight") && Off("_UseBacklight")) return true;
            if (prop.StartsWith("_Parallax") && Off("_UseParallax")) return true;
            if (prop.StartsWith("_AudioLink") && Off("_UseAudioLink")) return true;
            if (prop.StartsWith("_Dissolve") && Off("_UseDissolve")) return true;
            if (prop.StartsWith("_Anisotropy") && Off("_UseAnisotropy")) return true;
            if (prop.StartsWith("_Reflection") || prop == "_SmoothnessTex" || prop == "_MetallicGlossMap")
                if (Off("_UseReflection")) return true;
            if (prop.StartsWith("_RimShade") && Off("_UseRimShade")) return true;
            if (prop == "_AlphaMask" && mat.HasProperty("_AlphaMaskMode") && mat.GetFloat("_AlphaMaskMode") < 0.5f)
                return true;
            return false;
        }

        static bool IsDecal(Material mat, string prop)
        {
            return GetFloat(mat, prop + "IsDecal", 0) > 0.5f
                   || GetFloat(mat, "_Main2ndTexIsDecal", 0) > 0.5f && prop.StartsWith("_Main2nd")
                   || GetFloat(mat, "_Main3rdTexIsDecal", 0) > 0.5f && prop.StartsWith("_Main3rd");
        }

        static AtoTextureClass Classify(Material mat, string prop)
        {
            if (NormalNames.Contains(prop) || prop.IndexOf("Bump", StringComparison.OrdinalIgnoreCase) >= 0
                || prop.IndexOf("Normal", StringComparison.OrdinalIgnoreCase) >= 0)
                return AtoTextureClass.Normal;
            if (MaskNames.Contains(prop)
                || prop.IndexOf("Mask", StringComparison.OrdinalIgnoreCase) >= 0
                || prop.IndexOf("Metallic", StringComparison.OrdinalIgnoreCase) >= 0
                || prop.IndexOf("Occlusion", StringComparison.OrdinalIgnoreCase) >= 0
                || prop.IndexOf("Smoothness", StringComparison.OrdinalIgnoreCase) >= 0)
                return AtoTextureClass.Gray;

            var tex = mat.GetTexture(prop) as Texture2D;
            if (tex != null)
            {
                var path = AssetDatabase.GetAssetPath(tex);
                var ti = string.IsNullOrEmpty(path) ? null : AssetImporter.GetAtPath(path) as TextureImporter;
                if (ti != null)
                {
                    if (ti.textureType == TextureImporterType.NormalMap) return AtoTextureClass.Normal;
                    if (ti.textureType == TextureImporterType.SingleChannel) return AtoTextureClass.Gray;
                }
            }
            return AtoTextureClass.Opaque; // alpha refined later from pixels / material alpha mode
        }

        static string GuessMainOfNormal(string prop)
        {
            if (prop == "_BumpMap") return "_MainTex";
            if (prop == "_Bump2ndMap") return "_MainTex";
            if (prop == "_DetailNormalMap") return "_DetailAlbedoMap";
            if (prop == "_MatCapBumpMap") return "_MatCapTex";
            if (prop == "_MatCap2ndBumpMap") return "_MatCap2ndTex";
            return "_MainTex";
        }

        static string GuessMainOfMask(string prop)
        {
            if (prop.StartsWith("_Main2nd")) return "_Main2ndTex";
            if (prop.StartsWith("_Main3rd")) return "_Main3rdTex";
            if (prop.StartsWith("_Emission2nd")) return "_Emission2ndMap";
            if (prop.StartsWith("_Emission")) return "_EmissionMap";
            if (prop.StartsWith("_Outline")) return "_OutlineTex";
            if (prop.StartsWith("_MatCap2nd")) return "_MatCap2ndTex";
            if (prop.StartsWith("_MatCap")) return "_MatCapTex";
            return "_MainTex";
        }

        static int InferLegacyRenderMode(Material mat)
        {
            var tag = mat.GetTag("RenderType", false, "");
            if (tag == "TransparentCutout") return 1;
            if (tag == "Transparent") return 2;
            return 0;
        }

        public static bool HasNonIdentityST(Material mat, string prop)
        {
            if (!mat.HasProperty(prop)) return false;
            try
            {
                var scale = mat.GetTextureScale(prop);
                var offset = mat.GetTextureOffset(prop);
                if (Mathf.Abs(scale.x - 1f) > 1e-5f || Mathf.Abs(scale.y - 1f) > 1e-5f) return true;
                if (Mathf.Abs(offset.x) > 1e-5f || Mathf.Abs(offset.y) > 1e-5f) return true;
            }
            catch { /* some texenv lack ST */ }
            if (mat.HasProperty(prop + "_ScrollRotate"))
            {
                var sr = mat.GetVector(prop + "_ScrollRotate");
                if (sr.sqrMagnitude > 1e-8f) return true;
            }
            return false;
        }

        static bool HasFloat(Material m, string n, out float v)
        {
            v = 0;
            if (!m.HasProperty(n)) return false;
            v = m.GetFloat(n);
            return true;
        }

        static float GetFloat(Material m, string n, float d) => m.HasProperty(n) ? m.GetFloat(n) : d;
        static int GetInt(Material m, string n, int d) => m.HasProperty(n) ? m.GetInt(n) : d;
    }
}
