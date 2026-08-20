using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Fosa.ATO.Editor
{
    /// <summary>
    /// 分析材质的着色器属性表与关键字，把每个贴图属性归类为贴图类型。
    /// 兼容 lilToon（标准关键字命名：_MainTex/_BumpMap/_EmissionMap/_MatCap/_Outline 等）与未来版本，
    /// 无法识别的属性归为 Other 并由后续保守处理（或白名单）。
    ///
    /// Analyzes a material's shader property table & keywords, classifying each texture
    /// property into a texture type. Compatible with lilToon & future versions via standard
    /// keyword naming; unknown properties fall back to "Other" (handled conservatively).
    /// </summary>
    public static class ATOShaderAnalyzer
    {
        /// <summary>贴图属性的用途描述。Describes a texture property's role.</summary>
        public struct TexturePropInfo
        {
            public string name;
            public ATOTextureType type;
            public bool isNormalMap;
            public bool noScaleOffset;
        }

        private static readonly (string[] needles, ATOTextureType type)[] Rules =
        {
            (new[] { "_bumpmap", "_bump", "normalmap", "_normal", "_nrm", "normaltex" }, ATOTextureType.NormalMap),
            (new[] { "matcap" }, ATOTextureType.MatCap),
            (new[] { "_emissionmap", "_emissive", "_emission" }, ATOTextureType.Emission),
            (new[] { "_occlusion", "_aomap", "occlusionmap" }, ATOTextureType.Occlusion),
            (new[] { "_mask", "alphamask", "cutout", "_cutoffmask", "mrao", "rma", "maskmap", "_metallicglossmap", "_metallic", "_specularglossmap", "_specgloss" }, ATOTextureType.MetallicGloss),
            (new[] { "_maintex", "_main", "albedo", "_diffuse", "_basecolor", "_basecolormap", "_base" }, ATOTextureType.MainColor),
            (new[] { "mask", "alphamap" }, ATOTextureType.Mask),
        };

        /// <summary>列出材质上所有贴图属性及其分类。List all texture props with classification.</summary>
        public static List<TexturePropInfo> GetTextureProperties(Material material)
        {
            var result = new List<TexturePropInfo>();
            var shader = material.shader;
            if (shader == null) return result;

            var names = new List<string>();
            try { names.AddRange(material.GetTexturePropertyNames()); }
            catch { /* 某些内置材质可能抛异常 / some materials may throw */ }

            var seen = new HashSet<string>();
            foreach (var name in names)
            {
                if (seen.Contains(name)) continue;
                seen.Add(name);

                var tex = material.GetTexture(name) as Texture2D;
                if (tex == null) continue;

                var info = new TexturePropInfo
                {
                    name = name,
                    type = Classify(name),
                    isNormalMap = false,
                    noScaleOffset = false,
                };
                info.isNormalMap = info.type == ATOTextureType.NormalMap || HasNormalAttribute(shader, name);
                if (info.isNormalMap) info.type = ATOTextureType.NormalMap;

                result.Add(info);
            }
            return result;
        }

        private static ATOTextureType Classify(string propName)
        {
            var lower = propName.ToLowerInvariant();
            foreach (var (needles, type) in Rules)
            {
                foreach (var n in needles)
                {
                    if (lower.Contains(n)) return type;
                }
            }
            return ATOTextureType.Other;
        }

        /// <summary>判断着色器属性是否标记为 [Normal]（反射调用，避免版本差异）。</summary>
        private static bool HasNormalAttribute(Shader shader, string propName)
        {
            try
            {
                // ShaderUtil.GetPropertyAttributes(shader, index) 返回属性数组。
                for (int i = 0; i < ShaderUtil.GetPropertyCount(shader); i++)
                {
                    if (ShaderUtil.GetPropertyName(shader, i) != propName) continue;
                    var attrs = ShaderUtil.GetPropertyAttributes(shader, i);
                    if (attrs == null) continue;
                    foreach (var a in attrs)
                        if (a != null && a.ToLowerInvariant().Contains("normal")) return true;
                }
            }
            catch { /* 版本差异 / version differences */ }
            return false;
        }

        /// <summary>判断某个属性是否被 ST 变换（缩放/平移/旋转）。有 ST 变换则不能安全处理。</summary>
        public static bool HasSTTransform(Material material, string propName)
        {
            var scale = material.GetTextureScale(propName);
            var offset = material.GetTextureOffset(propName);
            const float eps = 1e-5f;
            return Mathf.Abs(scale.x - 1f) > eps || Mathf.Abs(scale.y - 1f) > eps ||
                   Mathf.Abs(offset.x) > eps || Mathf.Abs(offset.y) > eps;
        }
    }
}
