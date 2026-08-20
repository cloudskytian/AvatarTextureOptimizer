using UnityEngine;

namespace Fosa.Ato.Editor.Analysis
{
    /// <summary>
    /// Detects the effective alpha/transparency mode of a material, handling lilToon, Standard, URP
    /// Lit, and HDRP Lit keywords/properties. When multiple materials reference the same texture, the
    /// strictest mode wins (Cutout > Blend > Opaque) and the highest cutoff is used.
    /// 检测材质的有效透明模式，兼容 lilToon、Standard、URP/HDRP Lit。同一贴图被多材质引用时取最严格
    /// 模式（Cutout > Blend > Opaque）与最高 cutoff。
    /// </summary>
    internal static class MaterialTransparency
    {
        public static Pipeline.TexAlphaMode Detect(Material mat)
        {
            if (mat == null || mat.shader == null) return Pipeline.TexAlphaMode.Opaque;
            string sn = mat.shader.name ?? "";

            // Render queue is a strong signal / render queue 是强信号
            int q = mat.renderQueue;
            bool transparent = q >= 2450;
            bool cutout = q >= 2400 && q < 2550; // alpha test range

            if (sn.IndexOf("liltoon", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                sn.StartsWith("_lil/", System.StringComparison.Ordinal))
            {
                if (mat.IsKeywordEnabled("_ALPHABLEND_ON") || mat.GetFloat("_TransparentMode") == 2 ||
                    mat.HasProperty("_AlphaBoost") && transparent)
                    return Pipeline.TexAlphaMode.Blend;
                if (mat.IsKeywordEnabled("_ALPHATEST_ON") || mat.GetFloat("_TransparentMode") == 1)
                    return Pipeline.TexAlphaMode.Cutout;
                return Pipeline.TexAlphaMode.Opaque;
            }

            if (mat.IsKeywordEnabled("_ALPHABLEND_ON") || mat.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT") ||
                transparent && mat.HasProperty("_BaseColorMap"))
                return Pipeline.TexAlphaMode.Blend;
            if (mat.IsKeywordEnabled("_ALPHATEST_ON") || mat.HasProperty("_Cutoff") && cutout)
                return Pipeline.TexAlphaMode.Cutout;

            // Standard/URP _Mode / _Surface / 标准/URP
            if (mat.HasProperty("_Mode"))
            {
                switch ((int)mat.GetFloat("_Mode"))
                {
                    case 1: return Pipeline.TexAlphaMode.Cutout;
                    case 2:
                    case 3: return Pipeline.TexAlphaMode.Blend;
                }
            }
            if (mat.HasProperty("_Surface"))
            {
                if ((int)mat.GetFloat("_Surface") == 1)
                    return mat.IsKeywordEnabled("_ALPHATEST_ON")
                        ? Pipeline.TexAlphaMode.Cutout
                        : Pipeline.TexAlphaMode.Blend;
            }
            return Pipeline.TexAlphaMode.Opaque;
        }

        public static float Cutoff(Material mat) => mat != null && mat.HasProperty("_Cutoff") ? mat.GetFloat("_Cutoff") : 0.5f;

        /// <summary>Combine two modes taking the strictest. / 合并两个模式，取最严格。</summary>
        public static Pipeline.TexAlphaMode Strictest(Pipeline.TexAlphaMode a, Pipeline.TexAlphaMode b)
            => (Pipeline.TexAlphaMode)Mathf.Max((int)a, (int)b);
    }
}
