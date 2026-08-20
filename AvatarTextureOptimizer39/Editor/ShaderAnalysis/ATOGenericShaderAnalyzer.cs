// Avatar Texture Optimizer (ATO)
// Bilingual comments: English + Simplified Chinese. 双语注释：英文 + 简体中文。

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AvatarTextureOptimizer.Editor.ShaderAnalysis
{
    /// <summary>
    /// Generic shader analyzer using Unity's ShaderUtil property/attribute introspection
    /// and standard keyword conventions. Handles built-in, URP/HDRP and most custom
    /// shaders that use [MainTexture]/[Normal]/[NoScaleOffset] attributes.
    ///
    /// 通用着色器分析器：基于 Unity ShaderUtil 的属性/特性自省 + 标准关键字约定。
    /// 处理内置、URP/HDRP 及多数使用 [MainTexture]/[Normal]/[NoScaleOffset] 特性的自定义着色器。
    /// </summary>
    public sealed class ATOGenericShaderAnalyzer : IATOShaderAnalyzer
    {
        public bool TryAnalyze(Shader shader, ATOShaderInfo result)
        {
            if (shader == null) { result.Unsupported = true; result.UnsupportedReason = "null shader"; return true; }

            result.Unsupported = false;
            result.Textures.Clear();

            int count = ShaderUtil.GetPropertyCount(shader);
            var propertyNames = new HashSet<string>();
            for (int i = 0; i < count; i++)
            {
                propertyNames.Add(ShaderUtil.GetPropertyName(shader, i));
            }

            for (int i = 0; i < count; i++)
            {
                if (ShaderUtil.GetPropertyType(shader, i) != ShaderUtil.ShaderPropertyType.TexEnv)
                    continue;

                string name = ShaderUtil.GetPropertyName(shader, i);
                string desc = ShaderUtil.GetPropertyDescription(shader, i);
                string[] attrs = ShaderUtil.GetPropertyAttributes(shader, i);

                var info = new ATOShaderTextureInfo
                {
                    PropertyName = name,
                    Description = desc,
                    Semantic = Classify(name, attrs),
                };

                // Detect [NoScaleOffset]. 检测 [NoScaleOffset]。
                info.NoScaleOffset = Contains(attrs, "NoScaleOffset");

                // Detect possible transform sibling properties. 检测可能的变换兄弟属性。
                if (!info.NoScaleOffset)
                {
                    foreach (var candidate in new[]
                             {
                                 name + "_ST",
                                 name + "_ScrollRotate",   // lilToon
                                 name + "_Pan",
                                 name + "_Rot",
                                 name + "_Angle",
                             })
                    {
                        if (propertyNames.Contains(candidate))
                            info.TransformProperties.Add(candidate);
                    }
                }

                result.Textures.Add(info);
            }

            return true;
        }

        private static ATOTextureSemantic Classify(string name, string[] attrs)
        {
            bool has(string a) => Contains(attrs, a);
            string n = name.ToLowerInvariant();

            // Attributes win. 特性优先。
            if (has("Normal") || has("NormalMap")) return ATOTextureSemantic.Normal;
            if (has("MainTexture")) return ATOTextureSemantic.Albedo;
            if (has("Emission")) return ATOTextureSemantic.Emission;
            if (has("Mask")) return ATOTextureSemantic.Mask;
            if (has("Metallic") || has("SpecGloss")) return ATOTextureSemantic.MetallicGloss;

            // Name conventions. 名称约定。
            if (n == "_maintex" || n == "_basemap" || n == "_basecolormap" || n == "_albedo" ||
                n == "_diffuse" || n == "_diffusemap" || n == "_color" || n == "_colormap" ||
                n == "maintexture")
                return ATOTextureSemantic.Albedo;

            if (n == "_bumpmap" || n == "_normalmap" || n == "_normal" || n == "bumpmap" ||
                n == "_bump" || (n.Contains("normal") && n.Contains("map")))
                return ATOTextureSemantic.Normal;

            if (n.Contains("mask") || n == "_maskmap") return ATOTextureSemantic.Mask;
            if (n.Contains("emission") || n.Contains("emissive") || n.Contains("_emissionmap"))
                return ATOTextureSemantic.Emission;
            if (n.Contains("metallic") || n.Contains("specgloss") || n.Contains("maskmap"))
                return ATOTextureSemantic.MetallicGloss;
            if (n.Contains("matcap")) return ATOTextureSemantic.MatCap;

            return ATOTextureSemantic.Other;
        }

        private static bool Contains(string[] arr, string value)
        {
            if (arr == null) return false;
            foreach (var a in arr)
                if (a == value) return true;
            return false;
        }
    }
}
