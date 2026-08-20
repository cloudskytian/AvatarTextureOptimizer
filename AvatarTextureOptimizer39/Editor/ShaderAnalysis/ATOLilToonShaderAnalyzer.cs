// Avatar Texture Optimizer (ATO)
// Bilingual comments: English + Simplified Chinese. 双语注释：英文 + 简体中文。

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AvatarTextureOptimizer.Editor.ShaderAnalysis
{
    /// <summary>
    /// Dedicated analyzer for lilToon. Handles lilToon's property conventions including
    /// secondary/tertiary maps (_Main2ndTex/_Main3rdTex), MatCap bump maps, and the
    /// scroll/rotate transform properties (_X_ScrollRotate).
    ///
    /// lilToon 专用分析器。处理 lilToon 的属性约定：第二/第三主色图、MatCap bump、
    /// 以及 scroll/rotate 变换属性（_X_ScrollRotate）。
    /// </summary>
    public sealed class ATOLilToonShaderAnalyzer : IATOShaderAnalyzer
    {
        private static bool IsLilToon(Shader shader)
        {
            if (shader == null) return false;
            string n = shader.name;
            return n.Contains("lilToon") || n.Contains("liltoon") ||
                   n.StartsWith("Hidden/lil") || n.StartsWith("_lil/") ||
                   n.StartsWith("Hidden/ltspass");
        }

        public bool TryAnalyze(Shader shader, ATOShaderInfo result)
        {
            if (!IsLilToon(shader)) return false; // defer to generic. 交给通用分析器。

            result.Unsupported = false;
            result.Textures.Clear();

            int count = ShaderUtil.GetPropertyCount(shader);
            var names = new HashSet<string>();
            for (int i = 0; i < count; i++)
                names.Add(ShaderUtil.GetPropertyName(shader, i));

            for (int i = 0; i < count; i++)
            {
                if (ShaderUtil.GetPropertyType(shader, i) != ShaderUtil.ShaderPropertyType.TexEnv)
                    continue;

                string name = ShaderUtil.GetPropertyName(shader, i);
                string desc = ShaderUtil.GetPropertyDescription(shader, i);

                var info = new ATOShaderTextureInfo
                {
                    PropertyName = name,
                    Description = desc,
                    Semantic = ClassifyLilToon(name),
                };

                info.NoScaleOffset = false;

                // lilToon scroll/rotate: _X_ScrollRotate (Vector4: scroll.xy, rotate.zw).
                // lilToon 的 scroll/rotate：_X_ScrollRotate（Vector4：scroll.xy, rotate.zw）。
                string scrollRotate = name + "_ScrollRotate";
                if (names.Contains(scrollRotate)) info.TransformProperties.Add(scrollRotate);
                string st = name + "_ST";
                if (names.Contains(st)) info.TransformProperties.Add(st);

                result.Textures.Add(info);
            }

            return true;
        }

        private static ATOTextureSemantic ClassifyLilToon(string name)
        {
            string n = name.ToLowerInvariant();

            // Main color maps (sRGB). 主色图（sRGB）。
            if (n == "_maintex" || n == "_main2ndtex" || n == "_main3rdtex" ||
                n == "_basemap" || n == "_1st_shademap" || n == "_2nd_shademap" ||
                n == "_3rd_shademap")
                return ATOTextureSemantic.Albedo;

            // Normal maps. 法线图。
            if (n == "_bumpmap" || n == "_bump2ndmap" || n == "_bump3rdmap" ||
                n == "_normalmap" || n == "_matcapbumpmap" || n == "_matcap2ndbumpmap")
                return ATOTextureSemantic.Normal;

            // MatCap color maps (sRGB-ish but linear-sampled; treat as MatCap group). MatCap 图。
            if (n == "_matcapmap" || n == "_matcap2ndmap")
                return ATOTextureSemantic.MatCap;

            // Emission. 自发光。
            if (n == "_emissionmap" || n == "_emission2ndmap" || n == "_emission3rdmap" ||
                n.Contains("emission") || n.Contains("emissive"))
                return ATOTextureSemantic.Emission;

            // Masks / data maps. 蒙版/数据图。
            if (n.Contains("mask") || n.Contains("blend") && n.Contains("map"))
                return ATOTextureSemantic.Mask;

            // Other known data textures. 其他已知数据贴图。
            if (n == "_glittermap" || n == "_glittercolormap" || n.Contains("glitter"))
                return ATOTextureSemantic.Mask;

            return ATOTextureSemantic.Other;
        }
    }
}
