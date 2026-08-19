// ATO — Avatar Texture Optimizer
// Detects a material's alpha handling mode (opaque / cutout / blend) and cutoff, using
// lilToon shader-name conventions, the Standard shader _Mode property, and generic
// blend-state keywords. Used to select alpha quality metrics (IoU vs RMSE).
// 检测材质的透明处理模式（不透明/Cutout/Blend）与 Cutoff：使用 lilToon 着色器命名约定、
// Standard 的 _Mode 属性与通用混合状态关键字。用于选择 alpha 质量指标（IoU vs RMSE）。

using UnityEngine;
using UnityEngine.Rendering;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// Alpha mode detection helpers. 透明模式检测辅助。
    /// </summary>
    public static class AlphaModeDetector
    {
        /// <summary>Detect the material's alpha mode. 检测材质透明模式。</summary>
        public static ATOAlphaMode Detect(Material m)
        {
            if (m == null || m.shader == null) return ATOAlphaMode.Opaque;
            string name = m.shader.name;

            // Standard / URP-style _Mode property. Standard/URP 风格 _Mode 属性。
            if (m.HasProperty("_Mode"))
            {
                float mode = m.GetFloat("_Mode");
                if (mode >= 1f) return ATOAlphaMode.Cutout;   // 1 = Cutout
                if (mode >= 2f) return ATOAlphaMode.Blend;    // 2 = Fade, 3 = Transparent
                return ATOAlphaMode.Opaque;
            }

            // lilToon: transparency is baked into the shader variant name. lilToon：透明性由变体名体现。
            if (name.Contains("lilToon") || name.Contains("_lil/"))
            {
                if (name.Contains("Cutout")) return ATOAlphaMode.Cutout;
                if (name.Contains("Transparent") || name.Contains("Fur") || name.Contains("Gem"))
                    return ATOAlphaMode.Blend;
                return ATOAlphaMode.Opaque;
            }

            // Generic blend-state detection. 通用混合状态检测。
            if (m.HasProperty("_SrcBlend") && m.HasProperty("_DstBlend"))
            {
                var src = (BlendMode)m.GetFloat("_SrcBlend");
                var dst = (BlendMode)m.GetFloat("_DstBlend");
                if (!(src == BlendMode.One && dst == BlendMode.Zero))
                {
                    // Not plain opaque → treat as cutout if _Cutoff/_AlphaToMask exists, else blend.
                    // 非纯不透明 → 若存在 _Cutoff/_AlphaToMask 视为 cutout，否则 blend。
                    if (m.HasProperty("_AlphaToMask") && m.GetFloat("_AlphaToMask") > 0f) return ATOAlphaMode.Cutout;
                    if (m.HasProperty("_Cutoff")) return ATOAlphaMode.Cutout;
                    return ATOAlphaMode.Blend;
                }
            }
            return ATOAlphaMode.Opaque;
        }

        /// <summary>Detect the material's cutoff value (default 0.5). 检测 Cutoff 值（默认 0.5）。</summary>
        public static float DetectCutoff(Material m)
        {
            if (m != null && m.HasProperty("_Cutoff")) return m.GetFloat("_Cutoff");
            return 0.5f;
        }
    }
}
