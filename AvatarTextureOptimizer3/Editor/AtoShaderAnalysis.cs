// English: Analyze shader property tables + keywords (lilToon and Unity standard names). Never guess unknown APIs.
// 中文：分析着色器属性表与关键字（lilToon 与 Unity 标准名）。禁止猜测未知 API。
using System;
using System.Collections.Generic;
using net.fosa.ato;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace net.fosa.ato.editor
{
    public readonly struct AtoTexSlot
    {
        public readonly string Name;
        public readonly AtoTextureClass Class;
        public readonly int UvChannel; // from _ST / UVSET if known, else 0
        public AtoTexSlot(string n, AtoTextureClass c, int uv)
        {
            Name = n; Class = c; UvChannel = uv;
        }
    }

    public static class AtoShaderAnalysis
    {
        // Verified lilToon property names from jp.lilxyzw.liltoon 2.3.4 Editor/lilInspector/lilMaterialProperties.cs
        private static readonly Dictionary<string, AtoTextureClass> Known = new Dictionary<string, AtoTextureClass>
        {
            ["_MainTex"] = AtoTextureClass.TransparentAlbedo,
            ["_Main2ndTex"] = AtoTextureClass.TransparentAlbedo,
            ["_Main3rdTex"] = AtoTextureClass.TransparentAlbedo,
            ["_BumpMap"] = AtoTextureClass.Normal,
            ["_Bump2ndMap"] = AtoTextureClass.Normal,
            ["_AlphaMask"] = AtoTextureClass.Gray,
            ["_EmissionMap"] = AtoTextureClass.TransparentAlbedo,
            ["_Emission2ndMap"] = AtoTextureClass.TransparentAlbedo,
            ["_ShadowColorTex"] = AtoTextureClass.Mask,
            ["_Shadow2ndColorTex"] = AtoTextureClass.Mask,
            ["_Shadow3rdColorTex"] = AtoTextureClass.Mask,
            ["_RimColorTex"] = AtoTextureClass.Mask,
            ["_OutlineTex"] = AtoTextureClass.TransparentAlbedo,
            ["_OutlineWidthMask"] = AtoTextureClass.Gray,
            ["_MatCapTex"] = AtoTextureClass.TransparentAlbedo,
            ["_MatCap2ndTex"] = AtoTextureClass.TransparentAlbedo,
            ["_GlitterColorTex"] = AtoTextureClass.Mask,
            ["_AnisotropyTangentMap"] = AtoTextureClass.Normal,
            ["_AnisotropyScaleMask"] = AtoTextureClass.Gray,
            ["_BacklightColorTex"] = AtoTextureClass.Mask,
            ["_DissolveMask"] = AtoTextureClass.Gray,
            ["_DissolveNoiseMask"] = AtoTextureClass.Gray,
            ["_AudioLinkMask"] = AtoTextureClass.Gray,
            ["_MainColorAdjustMask"] = AtoTextureClass.Gray,
            // Unity Standard / URP Lit
            ["_BaseMap"] = AtoTextureClass.TransparentAlbedo,
            ["_BaseColorMap"] = AtoTextureClass.TransparentAlbedo,
            ["_MetallicGlossMap"] = AtoTextureClass.Mask,
            ["_OcclusionMap"] = AtoTextureClass.Gray,
            ["_SpecGlossMap"] = AtoTextureClass.Mask,
            ["_ParallaxMap"] = AtoTextureClass.Gray,
            ["_DetailAlbedoMap"] = AtoTextureClass.TransparentAlbedo,
            ["_DetailNormalMap"] = AtoTextureClass.Normal,
            ["_EmissionColorTex"] = AtoTextureClass.TransparentAlbedo,
        };

        public static List<AtoTexSlot> CollectSlots(Material mat, out bool compatible, out string warn)
        {
            var list = new List<AtoTexSlot>();
            compatible = true;
            warn = null;
            if (mat == null || mat.shader == null)
            {
                compatible = false;
                warn = "material or shader is null";
                return list;
            }

            var shader = mat.shader;
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                if (shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                var name = shader.GetPropertyName(i);
                if (name.EndsWith("_ST", StringComparison.Ordinal)) continue;

                var cls = Classify(shader, name);
                int uv = GuessUvChannel(mat, name);
                list.Add(new AtoTexSlot(name, cls, uv));
            }
            return list;
        }

        public static AtoTextureClass Classify(Shader shader, string prop)
        {
            if (Known.TryGetValue(prop, out var c)) return c;
            var n = prop.ToLowerInvariant();
            if (n.Contains("bump") || n.Contains("normal")) return AtoTextureClass.Normal;
            if (n.Contains("mask") || n.Contains("metallic") || n.Contains("occlusion") || n.Contains("smooth"))
                return AtoTextureClass.Mask;
            return AtoTextureClass.Unknown;
        }

        public static AtoAlphaMode ReadAlphaMode(Material mat, out float cutoff)
        {
            cutoff = 0.5f;
            if (mat == null) return AtoAlphaMode.Opaque;
            if (mat.HasProperty("_Cutoff")) cutoff = mat.GetFloat("_Cutoff");
            if (mat.HasProperty("_CutoffMode")) { /* lil unused */ }

            // lilToon: _TransparentMode 0 Opaque 1 Cutout 2 Transparent 3 Fur 4 Gem? (from lil docs / inspector)
            if (mat.HasProperty("_TransparentMode"))
            {
                var m = Mathf.RoundToInt(mat.GetFloat("_TransparentMode"));
                if (m == 1) return AtoAlphaMode.Cutout;
                if (m >= 2) return AtoAlphaMode.Blend;
                return AtoAlphaMode.Opaque;
            }
            if (mat.IsKeywordEnabled("_ALPHATEST_ON") || mat.IsKeywordEnabled("_ALPHATEST"))
                return AtoAlphaMode.Cutout;
            if (mat.IsKeywordEnabled("_ALPHABLEND_ON") || mat.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT"))
                return AtoAlphaMode.Blend;
            if (mat.renderQueue >= 2450 && mat.renderQueue < 3000) return AtoAlphaMode.Cutout;
            if (mat.renderQueue >= 3000) return AtoAlphaMode.Blend;
            return AtoAlphaMode.Opaque;
        }

        private static int GuessUvChannel(Material mat, string texProp)
        {
            // Standard: _DetailAlbedoMap uses UV1 when _UVSec == 1
            if (texProp == "_DetailAlbedoMap" || texProp == "_DetailNormalMap")
            {
                if (mat.HasProperty("_UVSec") && mat.GetFloat("_UVSec") > 0.5f) return 1;
            }
            var uvProp = texProp + "UV";
            if (mat.HasProperty(uvProp))
            {
                var v = Mathf.RoundToInt(mat.GetFloat(uvProp));
                if (v >= 0 && v < 8) return v;
            }
            return 0;
        }

        public static bool HasNonIdentityST(Material mat, string texProp)
        {
            var stName = texProp + "_ST";
            if (!mat.HasProperty(stName)) return false;
            var st = mat.GetVector(stName);
            if (Mathf.Abs(st.x - 1f) > 1e-4f || Mathf.Abs(st.y - 1f) > 1e-4f) return true;
            if (Mathf.Abs(st.z) > 1e-4f || Mathf.Abs(st.w) > 1e-4f) return true;
            var rot = texProp + "_ScrollRotate";
            if (mat.HasProperty(rot))
            {
                var r = mat.GetVector(rot);
                if (r.sqrMagnitude > 1e-8f) return true;
            }
            return false;
        }
    }
}
