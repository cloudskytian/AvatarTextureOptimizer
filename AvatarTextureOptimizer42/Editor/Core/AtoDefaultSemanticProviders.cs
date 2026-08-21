using UnityEditor;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Registers conservative built-in semantic providers.
    /// 注册保守的内建语义提供器。
    /// </summary>
    [InitializeOnLoad]
    internal static class AtoDefaultSemanticProviders
    {
        static AtoDefaultSemanticProviders()
        {
            AtoExtensionRegistry.Register(new AtoCommonShaderSemanticProvider());
            AtoExtensionRegistry.Register(new AtoLilToonSemanticProvider());
        }
    }

    /// <summary>
    /// Conservative semantic provider for common property names.
    /// 面向常见属性名的保守语义提供器。
    /// </summary>
    internal sealed class AtoCommonShaderSemanticProvider : IAtoShaderSemanticProvider
    {
        public bool TryDescribe(Material material, string textureProperty, out AtoShaderSemanticDescription description)
        {
            switch (textureProperty)
            {
                case "_MainTex":
                case "_BaseMap":
                case "_BaseColorMap":
                case "_ColorMap":
                case "_EmissionMap":
                    description = new AtoShaderSemanticDescription(AtoTextureSemantic.Color, 0, true, "Common color property.");
                    return true;
                case "_BumpMap":
                case "_NormalMap":
                    description = new AtoShaderSemanticDescription(AtoTextureSemantic.Normal, 0, true, "Common normal property.");
                    return true;
                case "_MetallicGlossMap":
                case "_OcclusionMap":
                case "_ParallaxMap":
                case "_DetailMask":
                case "_AlphaMask":
                    description = new AtoShaderSemanticDescription(AtoTextureSemantic.Mask, 0, true, "Common mask-like property.");
                    return true;
                case "_DetailAlbedoMap":
                case "_DetailNormalMap":
                    description = new AtoShaderSemanticDescription(
                        textureProperty == "_DetailNormalMap" ? AtoTextureSemantic.Normal : AtoTextureSemantic.Color,
                        ResolveStandardSecondaryUv(material),
                        true,
                        "Standard secondary UV property.");
                    return true;
                default:
                    description = default;
                    return false;
            }
        }

        private static int ResolveStandardSecondaryUv(Material material)
        {
            if (material != null && material.HasProperty("_UVSec"))
            {
                var value = Mathf.RoundToInt(material.GetFloat("_UVSec"));
                if (value == 1)
                {
                    return 1;
                }
            }

            return 0;
        }
    }

    /// <summary>
    /// Conservative lilToon semantic provider based on source inspection.
    /// 基于源码取证的保守 lilToon 语义提供器。
    /// </summary>
    internal sealed class AtoLilToonSemanticProvider : IAtoShaderSemanticProvider
    {
        public bool TryDescribe(Material material, string textureProperty, out AtoShaderSemanticDescription description)
        {
            var shaderName = material?.shader?.name ?? string.Empty;
            if (shaderName.IndexOf("lilToon", System.StringComparison.OrdinalIgnoreCase) < 0
                && shaderName.IndexOf("_lil/", System.StringComparison.OrdinalIgnoreCase) < 0
                && shaderName.IndexOf("Hidden/lil", System.StringComparison.OrdinalIgnoreCase) < 0)
            {
                description = default;
                return false;
            }

            switch (textureProperty)
            {
                case "_MainTex":
                case "_BaseMap":
                case "_BaseColorMap":
                case "_Main2ndTex":
                case "_Main3rdTex":
                case "_EmissionMap":
                case "_Emission2ndMap":
                    description = new AtoShaderSemanticDescription(
                        AtoTextureSemantic.Color,
                        ResolveLilToonUvChannel(material, textureProperty),
                        true,
                        "lilToon color-capable texture property.");
                    return true;
                case "_BumpMap":
                case "_Bump2ndMap":
                case "_MatCapBumpMap":
                case "_MatCap2ndBumpMap":
                    description = new AtoShaderSemanticDescription(
                        AtoTextureSemantic.Normal,
                        ResolveLilToonUvChannel(material, textureProperty),
                        true,
                        "lilToon normal-capable texture property.");
                    return true;
                case "_AlphaMask":
                case "_Main2ndBlendMask":
                case "_Main3rdBlendMask":
                case "_EmissionBlendMask":
                case "_Emission2ndBlendMask":
                case "_MetallicGlossMap":
                case "_SmoothnessTex":
                case "_OcclusionMap":
                case "_ShadowStrengthMask":
                case "_ShadowBorderMask":
                case "_ShadowBlurMask":
                    description = new AtoShaderSemanticDescription(
                        AtoTextureSemantic.Mask,
                        ResolveLilToonUvChannel(material, textureProperty),
                        true,
                        "lilToon mask-like texture property.");
                    return true;
                default:
                    description = default;
                    return false;
            }
        }

        private static int ResolveLilToonUvChannel(Material material, string textureProperty)
        {
            if (material == null)
            {
                return 0;
            }

            switch (textureProperty)
            {
                case "_Main2ndTex":
                    return ResolveLilToonUvMode(material, "_Main2ndTex_UVMode");
                case "_Main3rdTex":
                    return ResolveLilToonUvMode(material, "_Main3rdTex_UVMode");
                case "_Bump2ndMap":
                    return ResolveLilToonUvMode(material, "_Bump2ndMap_UVMode");
                case "_EmissionMap":
                    return ResolveLilToonUvMode(material, "_EmissionMap_UVMode");
                case "_Emission2ndMap":
                    return ResolveLilToonUvMode(material, "_Emission2ndMap_UVMode");
                default:
                    return 0;
            }
        }

        private static int ResolveLilToonUvMode(Material material, string propertyName)
        {
            if (material == null || !material.HasProperty(propertyName))
            {
                return 0;
            }

            var mode = Mathf.RoundToInt(material.GetFloat(propertyName));
            return mode switch
            {
                1 => 1,
                2 => 2,
                3 => 3,
                _ => 0,
            };
        }
    }
}
