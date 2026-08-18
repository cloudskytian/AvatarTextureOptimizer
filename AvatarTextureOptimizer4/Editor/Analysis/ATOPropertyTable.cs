// Avatar Texture Optimizer (ATO)
// Texture-property classification table.
// 贴图属性分类表。
//
// Property names were verified against:
//   - lilToon 2.3.4 shader sources (jp.lilxyzw.liltoon)
//   - Unity Standard / URP / HDRP documentation
//   - avatar-compressor's TexturePropertyDefinitions (MIT) as a cross-check
// 属性名已对照以下来源验证：lilToon 2.3.4 着色器源码、Unity Standard/URP/HDRP 文档、
// avatar-compressor 的 TexturePropertyDefinitions（MIT）交叉校验。

using System.Collections.Generic;

namespace NetFosa.ATO
{
    /// <summary>
    /// Static classification of well-known texture properties, keyed by property name.
    /// Unknown properties are handled by ATOShaderPropertyAnalyzer's keyword-based fallback.
    /// 已知贴图属性的静态分类，按属性名索引。未知属性由 ATOShaderPropertyAnalyzer 的关键字兜底分析。
    /// </summary>
    public static class ATOPropertyTable
    {
        public static readonly HashSet<string> MainColor = new HashSet<string>
        {
            "_MainTex", "_BaseMap", "_BaseColorMap", "_BaseMap_ST",
        };

        public static readonly HashSet<string> NormalMap = new HashSet<string>
        {
            "_BumpMap", "_NormalMap", "_DetailNormalMap", "_Bump2ndMap", "_Bump3rdMap",
            "_MatCapBumpMap", "_MatCap2ndBumpMap", "_AnisotropyTangentMap", "_OutlineBumpMap",
        };

        public static readonly HashSet<string> AlphaMask = new HashSet<string>
        {
            "_AlphaMask", "_MaskTex", "_DetailMask",
        };

        /// <summary>
        /// Grayscale/data textures: sampled but not color. / 灰度/数据贴图：被采样但非颜色。
        /// </summary>
        public static readonly HashSet<string> Grayscale = new HashSet<string>
        {
            "_MetallicGlossMap", "_SpecGlossMap", "_OcclusionMap", "_MaskMap", "_SmoothnessTex",
            "_Ramp", "_RampTex", "_MainColorAdjustMask", "_Main2ndBlendMask", "_Main3rdBlendMask",
            "_ShadowStrengthMask", "_ShadowBorderMask", "_ShadowBlurMask", "_RimShadeMask",
            "_GlitterShapeTex", "_EmissionBlendMask", "_Emission2ndBlendMask", "_AnisotropyScaleMask",
            "_Bump2ndScaleMask", "_OutlineWidthMask", "_MatCapBlendMask", "_MatCap2ndBlendMask",
            "_BacklightColorTex", "_AudioLinkMask", "_DissolveMask", "_DissolveNoiseMask",
            "_Main2ndDissolveMask", "_Main2ndDissolveNoiseMask", "_Main3rdDissolveMask", "_Main3rdDissolveNoiseMask",
        };

        /// <summary>
        /// Known properties that should NOT be atlased because they are not UV-sampled color/normal/mask
        /// (e.g. matcaps, environment maps, emission ramps, audio-link LUTs). They are treated as
        /// whitelist-like (skipped) unless the shader analyzer decides otherwise.
        /// 不应图集化的已知属性（非 UV 采样的颜色/法线/遮罩，如 matcap、环境贴图、发光渐变、AudioLink LUT），
        /// 除着色器分析器另行判定外按白名单跳过。
        /// </summary>
        public static readonly HashSet<string> NonAtlasable = new HashSet<string>
        {
            "_MatCapTex", "_MatCap2ndTex", "_GlitterColorTex", "_EmissionGradTex", "_Emission2ndGradTex",
            "_MainGradationTex", "_OutlineVectorTex", "_AudioLinkLocalMap", "_RimColorTex", "_ShadowColorTex",
            "_Shadow2ndColorTex", "_Shadow3rdColorTex", "_EnvMap", "_Cube",
        };

        /// <summary>Property names that are 2D textures and carry a transform (ST) vector. / 携带 ST 变换向量的 2D 贴图属性名。</summary>
        public static bool HasStVector(Material m, string propertyName)
        {
            return m.HasProperty(propertyName + "_ST");
        }

        /// <summary>Classify a property name into a category using the static table. / 用静态表把属性名归类。</summary>
        public static ATOTextureCategory Classify(string propertyName)
        {
            if (MainColor.Contains(propertyName)) return ATOTextureCategory.MainColor;
            if (NormalMap.Contains(propertyName)) return ATOTextureCategory.NormalMap;
            if (AlphaMask.Contains(propertyName)) return ATOTextureCategory.Mask;
            if (Grayscale.Contains(propertyName)) return ATOTextureCategory.Grayscale;
            if (NonAtlasable.Contains(propertyName)) return ATOTextureCategory.Other;
            return ATOTextureCategory.Other;
        }

        public static bool IsKnownAtlasable(string propertyName)
        {
            return MainColor.Contains(propertyName) || NormalMap.Contains(propertyName)
                || AlphaMask.Contains(propertyName) || Grayscale.Contains(propertyName);
        }
    }
}
