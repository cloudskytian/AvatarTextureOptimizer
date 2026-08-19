// Shader Analyzer - Analyzes shader properties to find textures, UV usage, etc.
// 着色器分析器 - 分析着色器属性以查找贴图、UV用途等

using System.Collections.Generic;
using System.Linq;
using net.fosa.avatar_texture_optimizer.Editor.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace net.fosa.avatar_texture_optimizer.Editor.Compatibility
{
    /// <summary>
    /// Analyzes shader properties for texture usage, UV transforms, keywords, etc.
    /// Supports lilToon, standard Unity shaders, and auto-detection of unknown shaders.
    /// 分析着色器属性的贴图用途、UV变换、关键字等。
    /// 支持lilToon、标准Unity着色器以及未知着色器的自动检测。
    /// </summary>
    public static class ShaderAnalyzer
    {
        /// <summary>
        /// Analyze a material's shader to determine texture properties and compatibility.
        /// 分析材质的着色器以确定贴图属性和兼容性。
        /// </summary>
        public static ShaderAnalysisResult Analyze(Material material, ATOBuildContext atoCtx)
        {
            var result = new ShaderAnalysisResult
            {
                Shader = material.shader,
                ShaderName = material.shader?.name ?? "Unknown"
            };

            if (material.shader == null)
            {
                result.IsCompatible = false;
                result.IncompatibilityReason = "Null shader / 空着色器";
                return result;
            }

            // Detect shader type
            // 检测着色器类型
            result.IsLilToon = IsLilToonShader(material.shader.name);
            result.IsStandard = IsStandardShader(material.shader.name);

            // Get active keywords
            // 获取活跃关键字
            try
            {
                var keywords = material.shaderKeywords;
                if (keywords != null)
                    result.ActiveKeywords = keywords.ToList();
            }
            catch { }

            // Analyze texture properties
            // 分析贴图属性
            var shader = material.shader;
            int propCount = shader.GetPropertyCount();

            for (int i = 0; i < propCount; i++)
            {
                var propType = shader.GetPropertyType(i);
                var propName = shader.GetPropertyName(i);

                if (propType == ShaderPropertyType.Texture)
                {
                    var texProp = AnalyzeTextureProperty(material, shader, propName, i, result);
                    if (texProp != null)
                    {
                        result.TextureProperties.Add(texProp);
                    }
                }
            }

            // Check for ST transforms on each texture property
            // 检查每个贴图属性的ST变换
            foreach (var texProp in result.TextureProperties)
            {
                string stPropName = texProp.PropertyName + "_ST";
                string scrollRotateName = texProp.PropertyName + "_ScrollRotate"; // lilToon specific

                // Check if there's an ST property
                if (material.HasProperty(stPropName))
                {
                    var st = material.GetVector(stPropName);
                    // ST = (scaleX, scaleY, offsetX, offsetY)
                    // If scale != 1 or offset != 0, the texture has a transform
                    if (!Approximately(st.x, 1f) || !Approximately(st.y, 1f) ||
                        !Approximately(st.z, 0f) || !Approximately(st.w, 0f))
                    {
                        texProp.HasSTTransform = true;
                    }
                }

                // Check lilToon scroll/rotate
                if (material.HasProperty(scrollRotateName))
                {
                    var sr = material.GetVector(scrollRotateName);
                    if (sr != Vector4.zero)
                    {
                        texProp.HasSTTransform = true;
                    }
                }

                // Check if there's animation modifying this property
                if (atoCtx.AnimationAnalysis != null)
                {
                    foreach (var stChange in atoCtx.AnimationAnalysis.STTransformChanges)
                    {
                        if (stChange.Material == material && stChange.PropertyName == texProp.PropertyName)
                        {
                            if (stChange.HasOffsetChange || stChange.HasScaleChange || stChange.HasRotationChange)
                            {
                                texProp.HasSTTransform = true;
                            }
                        }
                    }
                }
            }

            // Check for special/decal textures
            // 检查特殊/贴花贴图
            CheckSpecialTextures(result, material);

            return result;
        }

        private static ShaderTextureProperty AnalyzeTextureProperty(
            Material material, Shader shader, string propName, int propIndex,
            ShaderAnalysisResult parentResult)
        {
            var role = DetermineTextureRole(propName, parentResult);

            // Determine UV channel
            int uvChannel = 0;
            // lilToon uses _MainTex for UV0, some shaders may use different channels
            // For standard shaders, most textures use UV0
            // Check for UV channel properties (e.g., _UVSec for detail maps)
            if (propName.Contains("Detail") && material.HasProperty("_UVSec"))
            {
                uvChannel = (int)material.GetFloat("_UVSec");
            }

            // Check if this texture property is affected by keywords
            bool isKeywordDisabled = IsTextureDisabledByKeywords(propName, parentResult, material);

            if (isKeywordDisabled)
                return null; // Skip textures disabled by keywords

            return new ShaderTextureProperty
            {
                PropertyName = propName,
                Role = role,
                UVChannel = uvChannel,
                UVPropertyName = propName + "_ST"
            };
        }

        private static TextureRole DetermineTextureRole(string propName, ShaderAnalysisResult shaderResult)
        {
            // lilToon property naming
            if (shaderResult.IsLilToon)
            {
                return DetermineLilToonTextureRole(propName);
            }

            // Standard / PBR property naming
            return DetermineStandardTextureRole(propName);
        }

        private static TextureRole DetermineLilToonTextureRole(string propName)
        {
            // Main textures
            if (propName == "_MainTex" || propName == "_Main2ndTex" || propName == "_Main3rdTex" ||
                propName == "_BaseMap" || propName == "_BaseColorMap")
                return TextureRole.MainColor;

            // Normal maps
            if (propName == "_BumpMap" || propName == "_Bump2ndMap")
                return TextureRole.NormalMap;

            // Alpha mask
            if (propName.Contains("_AlphaMask"))
                return TextureRole.AlphaMask;

            // Shadow masks / shadow color maps
            if (propName.Contains("_Shadow") && (propName.Contains("ColorMap") || propName.Contains("2nd") || propName.Contains("3rd")))
                return TextureRole.MainColor; // Shadow color maps are color textures

            if (propName.Contains("_ShadowMask"))
                return TextureRole.Mask;

            // Emission
            if (propName.Contains("_Emission") && propName.Contains("Map"))
                return TextureRole.Emission;

            // Other masks
            if (propName.Contains("_Mask"))
                return TextureRole.Mask;

            // Metallic / smoothness
            if (propName == "_MetallicGlossMap")
                return TextureRole.Metallic;
            if (propName == "_SmoothnessTex")
                return TextureRole.Roughness;

            // Anisotropy
            if (propName.Contains("_Anisotropy") && propName.Contains("Map"))
                return TextureRole.Mask;

            // Parallax
            if (propName.Contains("_Parallax") && propName.Contains("Map"))
                return TextureRole.Mask;

            // MatCap
            if (propName.Contains("_MatCap") && propName.Contains("Tex"))
                return TextureRole.Other; // MatCap is view-space, not UV-based

            // Rim
            if (propName.Contains("_Rim") && propName.Contains("Tex"))
                return TextureRole.Other;

            // Outline
            if (propName.Contains("_Outline") && propName.Contains("Tex"))
                return TextureRole.MainColor;

            // Fur
            if (propName.Contains("_Fur") && propName.Contains("Tex"))
                return TextureRole.Other;

            // Dissolve
            if (propName.Contains("_Dissolve") && propName.Contains("Mask"))
                return TextureRole.Mask;

            // Default: if it's a texture property, treat as MainColor
            return TextureRole.MainColor;
        }

        private static TextureRole DetermineStandardTextureRole(string propName)
        {
            if (propName == "_MainTex" || propName == "_BaseMap")
                return TextureRole.MainColor;
            if (propName == "_BumpMap" || propName == "_NormalMap" || propName == "_Bump")
                return TextureRole.NormalMap;
            if (propName == "_MetallicGlossMap" || propName == "_MetallicMap")
                return TextureRole.Metallic;
            if (propName == "_OcclusionMap" || propName == "_SpecGlossMap")
                return TextureRole.Occlusion;
            if (propName == "_EmissionMap" || propName == "_EmissiveMap")
                return TextureRole.Emission;
            if (propName == "_DetailAlbedoMap" || propName == "_DetailMask")
                return TextureRole.Detail;
            if (propName == "_DetailNormalMap")
                return TextureRole.NormalMap;
            if (propName == "_ParallaxMap" || propName == "_HeightMap")
                return TextureRole.Mask;
            if (propName.Contains("Mask"))
                return TextureRole.Mask;
            return TextureRole.Other;
        }

        private static bool IsTextureDisabledByKeywords(string propName, ShaderAnalysisResult result, Material mat)
        {
            // lilToon keyword-based feature toggles
            if (result.IsLilToon)
            {
                // _UseXxx keywords control feature visibility
                string useKeyword = null;

                if (propName.Contains("Main2nd"))
                    useKeyword = "_UseMain2ndTex";
                else if (propName.Contains("Main3rd"))
                    useKeyword = "_UseMain3rdTex";
                else if (propName.Contains("Shadow") && !propName.Contains("Color"))
                    useKeyword = "_UseShadow";
                else if (propName.Contains("Emission") && !propName.Contains("2nd"))
                    useKeyword = "_UseEmission";
                else if (propName.Contains("Emission2nd"))
                    useKeyword = "_UseEmission2nd";
                else if (propName == "_BumpMap")
                    useKeyword = "_UseBumpMap";
                else if (propName.Contains("Bump2nd"))
                    useKeyword = "_UseBump2ndMap";
                else if (propName.Contains("Anisotropy"))
                    useKeyword = "_UseAnisotropy";
                else if (propName.Contains("Backlight"))
                    useKeyword = "_UseBacklight";
                else if (propName.Contains("Reflection") || propName.Contains("Specular"))
                    useKeyword = "_UseReflection";
                else if (propName.Contains("MatCap") && !propName.Contains("2nd"))
                    useKeyword = "_UseMatCap";
                else if (propName.Contains("MatCap2nd"))
                    useKeyword = "_UseMatCap2nd";
                else if (propName.Contains("Rim") && !propName.Contains("Shade"))
                    useKeyword = "_UseRim";
                else if (propName.Contains("RimShade"))
                    useKeyword = "_UseRimShade";
                else if (propName.Contains("Glitter"))
                    useKeyword = "_UseGlitter";
                else if (propName.Contains("Parallax"))
                    useKeyword = "_UseParallax";
                else if (propName.Contains("AudioLink"))
                    useKeyword = "_UseAudioLink";
                else if (propName.Contains("Outline") && propName.Contains("Tex"))
                    useKeyword = "_UseOutline";

                if (useKeyword != null && mat.HasProperty(useKeyword))
                {
                    float val = mat.GetFloat(useKeyword);
                    if (val < 0.5f) return true; // Feature disabled
                }
            }

            return false;
        }

        private static void CheckSpecialTextures(ShaderAnalysisResult result, Material material)
        {
            foreach (var texProp in result.TextureProperties)
            {
                // Check if texture is used as a decal or has special deformation
                // 检查贴图是否用作贴花或具有特殊变形
                string propName = texProp.PropertyName;

                // lilToon decal textures (Main2nd/3rd with decal mode)
                if (result.IsLilToon)
                {
                    if (propName.Contains("2nd") || propName.Contains("3rd"))
                    {
                        // Check if decal mode is enabled
                        string decalProp = propName.Contains("2nd") ? "_Main2ndTexIsDecal" : "_Main3rdTexIsDecal";
                        if (material.HasProperty(decalProp) && material.GetFloat(decalProp) > 0.5f)
                        {
                            texProp.IsDecalOrSpecial = true;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Get the transparency mode for a material.
        /// 获取材质的透明模式。
        /// </summary>
        public static TransparencyMode GetTransparencyMode(Material material, ShaderAnalysisResult shaderResult)
        {
            if (shaderResult.IsLilToon)
            {
                // lilToon uses _TransparentMode property
                if (material.HasProperty("_TransparentMode"))
                {
                    int mode = (int)material.GetFloat("_TransparentMode");
                    switch (mode)
                    {
                        case 0: return TransparencyMode.Opaque;
                        case 1: return TransparencyMode.Cutout;
                        case 2: return TransparencyMode.Blend;
                        case 3: return TransparencyMode.Premultiply;
                        default: return TransparencyMode.Opaque;
                    }
                }

                // Check render queue as fallback
                if (material.renderQueue <= 2000) return TransparencyMode.Opaque;
                if (material.renderQueue <= 2450) return TransparencyMode.Cutout;
                return TransparencyMode.Blend;
            }

            // Standard shader transparency
            if (material.HasProperty("_Mode"))
            {
                int mode = (int)material.GetFloat("_Mode");
                switch (mode)
                {
                    case 0: return TransparencyMode.Opaque;
                    case 1: return TransparencyMode.Cutout;
                    case 2: return TransparencyMode.Blend;  // Fade
                    case 3: return TransparencyMode.Premultiply;
                    default: return TransparencyMode.Opaque;
                }
            }

            // Check surface type keyword
            if (material.IsKeywordEnabled("_ALPHATEST_ON"))
                return TransparencyMode.Cutout;
            if (material.IsKeywordEnabled("_ALPHABLEND_ON"))
                return TransparencyMode.Blend;
            if (material.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON"))
                return TransparencyMode.Premultiply;

            return TransparencyMode.Opaque;
        }

        private static bool IsLilToonShader(string shaderName)
        {
            return shaderName != null && (
                shaderName.StartsWith("_lil") ||
                shaderName.Contains("lilToon") ||
                shaderName.StartsWith("Hidden/lilToon")
            );
        }

        private static bool IsStandardShader(string shaderName)
        {
            return shaderName != null && (
                shaderName == "Standard" ||
                shaderName == "Standard (Specular setup)" ||
                shaderName == "Universal Render Pipeline/Lit" ||
                shaderName == "Universal Render Pipeline/Simple Lit" ||
                shaderName.Contains("Standard") ||
                shaderName.Contains("Lit")
            );
        }

        private static bool Approximately(float a, float b, float epsilon = 0.001f)
        {
            return Mathf.Abs(a - b) < epsilon;
        }
    }
}
