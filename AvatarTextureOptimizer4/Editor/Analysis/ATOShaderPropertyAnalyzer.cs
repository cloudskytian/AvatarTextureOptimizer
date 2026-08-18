// Avatar Texture Optimizer (ATO)
// Keyword-based shader property analysis: tries to classify texture properties for
// lilToon and other shaders that use standard keywords, so future versions remain compatible.
// 基于关键字/命名约定的着色器属性分析：尽量兼容 lilToon 及使用标准关键字的着色器的未来版本。

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace NetFosa.ATO
{
    /// <summary>
    /// Heuristic analysis of a shader's texture properties by name and type.
    /// 按名称与类型对着色器贴图属性做启发式分析。
    /// </summary>
    public static class ATOShaderPropertyAnalyzer
    {
        private static readonly Dictionary<Shader, Dictionary<string, ATOTextureCategory>> _cache
            = new Dictionary<Shader, Dictionary<string, ATOTextureCategory>>();

        /// <summary>
        /// Analyze a material's texture properties. Returns (propertyName -> category).
        /// 分析材质的贴图属性，返回 (属性名 -> 分类)。
        /// </summary>
        public static Dictionary<string, ATOTextureCategory> Analyze(Material material)
        {
            if (material == null || material.shader == null) return new Dictionary<string, ATOTextureCategory>();
            var shader = material.shader;
            if (_cache.TryGetValue(shader, out var cached)) return cached;

            var result = new Dictionary<string, ATOTextureCategory>();
            try
            {
                int count = shader.GetPropertyCount();
                for (int i = 0; i < count; i++)
                {
                    if (shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                    string name = shader.GetPropertyName(i);
                    result[name] = ClassifyByKeywords(name, shader.name);
                }
            }
            catch (System.Exception e)
            {
                ATOLogger.Warn($"Shader analysis failed for '{shader.name}': {e.Message}; falling back to static table. / 着色器分析失败，回退静态表。");
            }
            _cache[shader] = result;
            return result;
        }

        /// <summary>
        /// Keyword/name-based classification. / 基于关键字/命名的分类。
        /// </summary>
        private static ATOTextureCategory ClassifyByKeywords(string propertyName, string shaderName)
        {
            var n = propertyName.ToLowerInvariant();
            var s = shaderName.ToLowerInvariant();

            // Known table first (exact names win). / 优先精确匹配已知表。
            var exact = ATOPropertyTable.Classify(propertyName);
            if (exact != ATOTextureCategory.Other) return exact;

            // Normal-map keywords. / 法线贴图关键字。
            if (n.Contains("bump") || n.Contains("normal"))
            {
                // Roughness/specular smoothness maps are grayscale even with "normal"-ish words absent.
                if (n.Contains("scale") && n.Contains("mask")) return ATOTextureCategory.Grayscale;
                return ATOTextureCategory.NormalMap;
            }

            // Mask keywords. / 遮罩关键字。
            if (n.Contains("mask") || n.Contains("alphamask") || n.Contains("cutout"))
                return n.Contains("noise") || n.Contains("dissolve") ? ATOTextureCategory.Grayscale : ATOTextureCategory.Mask;

            // Main color keywords. / 主色关键字。
            if (n.Contains("maintex") || n.Contains("basecolor") || n.Contains("basemap") || n.Contains("albedo")
                || n.Contains("color") || n.Contains("diffuse"))
                return ATOTextureCategory.MainColor;

            // Grayscale/data keywords. / 灰度/数据关键字。
            if (n.Contains("metallic") || n.Contains("roughness") || n.Contains("smoothness")
                || n.Contains("ao") || n.Contains("occlusion") || n.Contains("ramp") || n.Contains("grad")
                || n.Contains("gloss") || n.Contains("spec") || n.Contains("thickness"))
                return ATOTextureCategory.Grayscale;

            // Emission / 发光.
            if (n.Contains("emiss"))
                return n.Contains("grad") ? ATOTextureCategory.Other : ATOTextureCategory.MainColor;

            // Anything referencing a matcap/env/glitter/outline vector is non-atlasable. / matcap/环境/闪烁/描边向量不图集化。
            if (n.Contains("matcap") || n.Contains("envmap") || n.Contains("cube") || n.Contains("glitter")
                || n.Contains("outline") || n.Contains("audio") || n.Contains("lut"))
                return ATOTextureCategory.Other;

            return ATOTextureCategory.Other;
        }
    }
}
