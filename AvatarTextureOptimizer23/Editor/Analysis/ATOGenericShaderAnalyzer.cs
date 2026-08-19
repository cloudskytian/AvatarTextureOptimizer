using UnityEngine;
using UnityEngine.Rendering;
using FOSA.AvatarTextureOptimizer;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Keyword / property-table based analyzer. Designed to keep working on future shader versions
    /// as long as they use standard Unity property names and keywords.
    /// 基于关键字和属性表的分析器。只要未来版本仍用标准属性名和关键字，就能继续兼容。
    /// </summary>
    internal static class ATOGenericShaderAnalyzer
    {
        public static ATOTextureSlotInfo Analyze(Material mat, string prop)
        {
            var info = new ATOTextureSlotInfo
            {
                propertyName = prop,
                uvChannel = GuessUvChannel(mat, prop),
                category = GuessCategory(mat, prop),
                alphaMode = GuessAlphaMode(mat),
                cutoff = mat.HasProperty("_Cutoff") ? mat.GetFloat("_Cutoff") : 0.5f
            };

            // ST transform? Identity only is eligible. / 仅 identity ST 才合格。
            var stName = prop + "_ST";
            if (mat.HasProperty(stName))
            {
                var st = mat.GetVector(stName);
                if (!IsIdentityST(st))
                {
                    info.eligible = false;
                    info.hasTransform = true;
                    info.ineligibleReason = $"{prop}_ST is not identity ({st})";
                    return info;
                }
            }

            // Common rotation / scroll properties. / 常见旋转、滚动属性。
            if (HasNonZero(mat, prop + "_ScrollRotate") ||
                HasNonZero(mat, "_MainTex_ScrollRotate") && prop == "_MainTex")
            {
                info.eligible = false;
                info.hasTransform = true;
                info.ineligibleReason = $"{prop} has scroll/rotate";
                return info;
            }

            if (IsSpecialPurpose(mat, prop))
            {
                info.eligible = false;
                info.isSpecialPurpose = true;
                info.ineligibleReason = $"{prop} is a special-purpose / non-mesh-UV map";
                return info;
            }

            info.eligible = true;
            return info;
        }

        public static bool IsIdentityST(Vector4 st)
        {
            return Mathf.Abs(st.x - 1f) < 1e-4f && Mathf.Abs(st.y - 1f) < 1e-4f &&
                   Mathf.Abs(st.z) < 1e-4f && Mathf.Abs(st.w) < 1e-4f;
        }

        public static ATOAlphaMode GuessAlphaMode(Material mat)
        {
            if (mat.IsKeywordEnabled("_ALPHATEST_ON") ||
                mat.IsKeywordEnabled("_ALPHATEST") ||
                ShaderTagContains(mat, "TransparentCutout") ||
                ShaderNameContains(mat, "Cutout"))
                return ATOAlphaMode.Cutout;

            if (mat.IsKeywordEnabled("_ALPHABLEND_ON") ||
                mat.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON") ||
                mat.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT") ||
                ShaderTagContains(mat, "Transparent") ||
                ShaderNameContains(mat, "Transparent"))
                return ATOAlphaMode.Blend;

            if (mat.HasProperty("_Mode"))
            {
                var mode = Mathf.RoundToInt(mat.GetFloat("_Mode"));
                if (mode == 1) return ATOAlphaMode.Cutout;
                if (mode >= 2) return ATOAlphaMode.Blend;
            }

            if (mat.HasProperty("_TransparentMode"))
            {
                // lilToon RenderingMode: 0 Opaque, 1 Cutout, 2+ transparent-like.
                // lilToon RenderingMode：0 不透明，1 裁剪，2+ 半透明一类。
                var tm = Mathf.RoundToInt(mat.GetFloat("_TransparentMode"));
                if (tm == 1) return ATOAlphaMode.Cutout;
                if (tm >= 2) return ATOAlphaMode.Blend;
            }

            if (mat.HasProperty("_Surface"))
            {
                if (mat.GetFloat("_Surface") > 0.5f) return ATOAlphaMode.Blend;
            }

            return ATOAlphaMode.Opaque;
        }

        public static ATOTextureCategory GuessCategory(Material mat, string prop)
        {
            var p = prop.ToLowerInvariant();
            if (p.Contains("bump") || p.Contains("normal") || p.Contains("tangent"))
                return ATOTextureCategory.Normal;

            if (p.Contains("emission") || p.Contains("emissive"))
                return ATOTextureCategory.OpaqueAlbedo;

            if (IsLikelyGray(p))
                return ATOTextureCategory.Gray;

            var alpha = GuessAlphaMode(mat);
            if ((prop == "_MainTex" || prop == "_BaseMap" || prop == "_BaseColorMap") &&
                alpha != ATOAlphaMode.Opaque)
                return ATOTextureCategory.TransparentAlbedo;

            return ATOTextureCategory.OpaqueAlbedo;
        }

        private static bool IsLikelyGray(string p)
        {
            return p.Contains("mask") || p.Contains("occlusion") || p.Contains("metallic") ||
                   p.Contains("smoothness") || p.Contains("gloss") || p.Contains("thickness") ||
                   p.Contains("ao") || p.Contains("alphamask");
        }

        public static int GuessUvChannel(Material mat, string prop)
        {
            var names = new[]
            {
                prop + "_UVMode", prop + "UV", prop + "_UV",
                "_UVSec", "_DetailAlbedoMap_UV"
            };
            foreach (var n in names)
            {
                if (!mat.HasProperty(n)) continue;
                var v = Mathf.RoundToInt(mat.GetFloat(n));
                if (v >= 0 && v <= 7) return v;
            }
            return 0;
        }

        private static bool IsSpecialPurpose(Material mat, string prop)
        {
            var p = prop.ToLowerInvariant();
            if (p.Contains("matcap") || p.Contains("cubemap") || p.Contains("cube") ||
                p.Contains("dither") || p.Contains("lut") || p.Contains("grad") ||
                p.Contains("ramp") || p.Contains("sdf") || p.Contains("dfg") ||
                p.Contains("audio") || p.Contains("dissolve") || p.Contains("noise") ||
                p.Contains("decal") || p.Contains("flipbook") || p.Contains("video") ||
                p.Contains("grab") || p.Contains("screen"))
                return true;

            // UV mode MatCap / Screen / Rim etc. / UV 模式为 MatCap / 屏幕 / 边缘等。
            var uvModeName = prop + "_UVMode";
            if (mat.HasProperty(uvModeName))
            {
                var mode = Mathf.RoundToInt(mat.GetFloat(uvModeName));
                if (mode >= 4) return true;
            }

            if (mat.HasProperty("_Main2ndTexIsDecal") && prop.Contains("2nd") && mat.GetFloat("_Main2ndTexIsDecal") > 0.5f)
                return true;
            if (mat.HasProperty("_Main3rdTexIsDecal") && prop.Contains("3rd") && mat.GetFloat("_Main3rdTexIsDecal") > 0.5f)
                return true;

            return false;
        }

        private static bool HasNonZero(Material mat, string prop)
        {
            if (!mat.HasProperty(prop)) return false;
            var v = mat.GetVector(prop);
            return v.sqrMagnitude > 1e-8f;
        }

        private static bool ShaderNameContains(Material mat, string token)
        {
            return mat.shader != null && mat.shader.name.IndexOf(token, System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool ShaderTagContains(Material mat, string token)
        {
            var tag = mat.GetTag("RenderType", false, "");
            return tag.IndexOf(token, System.StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
