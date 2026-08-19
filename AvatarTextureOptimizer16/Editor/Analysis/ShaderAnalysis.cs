using System.Collections.Generic;
using UnityEngine;

namespace AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Classifies material texture properties into categories (main color / normal / mask-gray)
    /// for lilToon and standard-keyword shaders. / 将材质贴图属性归类（主色/法线/蒙版灰度），
    /// 支持 lilToon 与标准关键字着色器。
    /// </summary>
    public static class ShaderAnalysis
    {
        // lilToon mainTexCheckWords (from lilConstants.cs, verbatim) — a property name containing any
        // of these is NOT a main color texture. / lilToon 的 mainTexCheckWords（照抄 lilConstants.cs）——
        // 属性名包含任一即非主色贴图。
        private static readonly string[] MainTexCheckWords =
        {
            "mask", "shadow", "shade", "outline", "normal", "bumpmap", "matcap", "rimlight",
            "emittion", "reflection", "specular", "roughness", "smoothness", "metallic",
            "metalness", "opacity", "parallax", "displacement", "height", "ambient", "occlusion",
        };

        private static readonly string[] NormalWords = { "bump", "normal" };

        public static bool IsLilToon(Shader shader)
        {
            if (shader == null) return false;
            string n = shader.name;
            return n.StartsWith("lilToon") || n.StartsWith("_lil") ||
                   n.StartsWith("Hidden/lilToon") || n.StartsWith("lts") || n.StartsWith("ltsl");
        }

        /// <summary>Is the property a normal map property? / 该属性是否为法线贴图属性？</summary>
        public static bool IsNormalProperty(string propertyName, Texture2D tex, Material mat)
        {
            string lower = propertyName.ToLowerInvariant();
            foreach (var w in NormalWords)
                if (lower.Contains(w)) return true;
            // [Normal] attribute path (lilToon marks normal props this way)
            if (mat != null && mat.shader != null)
            {
                int id = mat.shader.FindPropertyIndex(propertyName);
                if (id >= 0)
                {
                    var attrs = mat.shader.GetPropertyAttributes(id);
                    if (attrs != null)
                        foreach (var a in attrs)
                            if (a.Equals("Normal", System.StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            return false;
        }

        /// <summary>Is the property a mask/gray texture (non-color)? / 该属性是否为蒙版/灰度贴图（非颜色）？</summary>
        public static bool IsMaskProperty(string propertyName)
        {
            string lower = propertyName.ToLowerInvariant();
            foreach (var w in MainTexCheckWords)
                if (lower.Contains(w)) return true;
            return false;
        }

        /// <summary>Is the property a main color texture? / 该属性是否为主色贴图？</summary>
        public static bool IsMainColorProperty(string propertyName)
        {
            string lower = propertyName.ToLowerInvariant();
            if (lower.Contains("ramp") || lower.Contains("matcap")) return false;
            return !IsMaskProperty(propertyName);
        }

        /// <summary>
        /// Determine the render mode (opaque / cutout / transparent) and cutoff for a material,
        /// without touching shader parameters. / 判定材质渲染模式（opaque/cutout/transparent）与 Cutoff，
        /// 不修改任何着色器参数。
        /// </summary>
        public static (bool isTransparent, bool isCutout, float cutoff) GetRenderMode(Material mat)
        {
            if (mat == null || mat.shader == null)
                return (false, false, 0.5f);

            string n = mat.shader.name.ToLowerInvariant();
            bool transparent = n.Contains("transparent") || n.Contains("fade") || n.Contains("refraction") ||
                               n.Contains("overlay") || n.Contains("gem");
            bool cutout = n.Contains("cutout");

            float cutoff = 0.5f;
            if (mat.HasProperty("_Cutoff")) cutoff = mat.GetFloat("_Cutoff");
            else if (mat.HasProperty("_CutoffRange")) cutoff = mat.GetFloat("_CutoffRange");

            // lilToon render-mode keywords
            if (IsLilToon(mat.shader))
            {
                // read via keyword: _CUTOUT / _TRANSPARENT etc. / 通过关键字读取
                foreach (var kw in mat.enabledKeywords)
                {
                    string k = kw.name.ToUpperInvariant();
                    if (k.Contains("CUTOUT")) { cutout = true; transparent = false; }
                    else if (k.Contains("TRANSPARENT") || k.Contains("FADE")) { transparent = true; }
                }
            }

            return (transparent, cutout, cutoff);
        }

        /// <summary>
        /// Classify a texture into a category given its property and material. / 依据属性与材质将贴图分类。
        /// </summary>
        public static ATOTextureCategory Classify(Texture2D tex, string propertyName, Material mat)
        {
            if (IsNormalProperty(propertyName, tex, mat)) return ATOTextureCategory.Normal;
            if (IsMaskProperty(propertyName)) return ATOTextureCategory.Gray;

            var (transparent, _, _) = GetRenderMode(mat);
            bool hasAlpha = HasAlpha(tex);
            if (transparent || hasAlpha) return ATOTextureCategory.TransparentColor;
            return ATOTextureCategory.OpaqueColor;
        }

        /// <summary>
        /// Heuristic: does the texture actually contain alpha? / 启发式：贴图是否实际包含 alpha 通道？
        /// Uses the asset importer's alpha-is-transparency flag when readable; falls back to true (safe).
        /// </summary>
        public static bool HasAlpha(Texture2D tex)
        {
            if (tex == null) return false;
            try
            {
                var importer = UnityEditor.AssetImporter.GetAtPath(UnityEditor.AssetDatabase.GetAssetPath(tex))
                    as UnityEditor.TextureImporter;
                if (importer != null) return importer.alphaIsTransparency;
            }
            catch { /* ignore */ }
            return false;
        }
    }
}
