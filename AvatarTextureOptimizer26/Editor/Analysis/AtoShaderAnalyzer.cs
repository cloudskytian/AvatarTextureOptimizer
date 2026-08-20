using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Net.Fosa.AvatarTextureOptimizer;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Inspects shader property tables + keywords. Compatible with lilToon and Unity-standard keyword sets.
    /// 分析着色器属性表与关键字。兼容 lilToon 与 Unity 标准关键字，尽量面向未来版本。
    /// </summary>
    public static class AtoShaderAnalyzer
    {
        public sealed class TexProp
        {
            public string Name;
            public int UvChannel;
            public bool HasST;
            public bool HasScrollRotate;
            public Vector4 ST = new Vector4(1, 1, 0, 0);
            public Vector4 ScrollRotate;
            public AtoTextureKind Kind;
            public bool IsNormal;
            public bool IsMask;
            public bool IsGray;
        }

        public sealed class MaterialInfo
        {
            public Material Material;
            public bool Ok = true;
            public string FailReason;
            public AtoAlphaMode Alpha = AtoAlphaMode.Opaque;
            public float Cutoff;
            public bool HasNormal;
            public bool HasMask;
            public readonly List<TexProp> Textures = new List<TexProp>();
        }

        public static MaterialInfo Analyze(Material mat)
        {
            var info = new MaterialInfo { Material = mat };
            if (mat == null || mat.shader == null)
            {
                info.Ok = false;
                info.FailReason = "null material/shader";
                return info;
            }

            var shader = mat.shader;
            var shaderName = shader.name ?? "";
            var isLil = shaderName.IndexOf("lilToon", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        shaderName.IndexOf("/lilToon", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        shaderName.StartsWith("_lil/", StringComparison.OrdinalIgnoreCase) ||
                        shaderName.StartsWith("Hidden/lil", StringComparison.OrdinalIgnoreCase);

            info.Alpha = DetectAlpha(mat, shaderName, isLil);
            if (mat.HasProperty("_Cutoff")) info.Cutoff = mat.GetFloat("_Cutoff");
            else if (mat.HasProperty("_Cutoff")) info.Cutoff = mat.GetFloat("_Cutoff");

            var count = ShaderUtil.GetPropertyCount(shader);
            var props = new Dictionary<string, TexProp>(StringComparer.Ordinal);
            for (var i = 0; i < count; i++)
            {
                if (ShaderUtil.GetPropertyType(shader, i) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                var name = ShaderUtil.GetPropertyName(shader, i);
                if (string.IsNullOrEmpty(name)) continue;
                if (!mat.HasProperty(name)) continue;
                var tex = mat.GetTexture(name) as Texture2D;
                if (tex == null) continue; // skip cubes / 3d / rendertextures

                var tp = new TexProp { Name = name };
                ClassifyProp(mat, name, isLil, tp);
                ReadUvTransform(mat, name, tp);
                props[name] = tp;
                info.Textures.Add(tp);
            }

            info.HasNormal = mat.IsKeywordEnabled("_NORMALMAP") ||
                             (mat.HasProperty("_UseBumpMap") && mat.GetFloat("_UseBumpMap") > 0.5f) ||
                             props.ContainsKey("_BumpMap");
            info.HasMask = props.ContainsKey("_MainColorAdjustMask") || props.ContainsKey("_AlphaMask") ||
                           props.ContainsKey("_MetallicGlossMap") || props.ContainsKey("_OcclusionMap") ||
                           (mat.HasProperty("_UseAlphaMask") && mat.GetFloat("_UseAlphaMask") > 0.5f);

            // Future-proof: any extra texture whose name looks like bump/normal.
            // 面向未来：名称像 bump/normal 的额外贴图。
            foreach (var tp in info.Textures)
            {
                if (tp.IsNormal) info.HasNormal = true;
                if (tp.IsMask || tp.IsGray) info.HasMask = true;
            }

            // ST / scroll / rotate means we cannot treat UVs as identity.
            // 存在 ST / 滚动 / 旋转则不能把 UV 当作恒等。
            foreach (var tp in info.Textures)
            {
                if (HasNonIdentityST(tp.ST) || HasNonZero(tp.ScrollRotate))
                {
                    // Individual property is marked; caller decides whitelist.
                    // 单属性标记，由调用方决定白名单。
                    tp.HasST = HasNonIdentityST(tp.ST) || tp.HasST;
                }
            }

            return info;
        }

        public static bool HasNonIdentityST(Vector4 st) =>
            Mathf.Abs(st.x - 1f) > 1e-4f || Mathf.Abs(st.y - 1f) > 1e-4f ||
            Mathf.Abs(st.z) > 1e-4f || Mathf.Abs(st.w) > 1e-4f;

        public static bool HasNonZero(Vector4 v) =>
            Mathf.Abs(v.x) > 1e-5f || Mathf.Abs(v.y) > 1e-5f ||
            Mathf.Abs(v.z) > 1e-5f || Mathf.Abs(v.w) > 1e-5f;

        private static void ReadUvTransform(Material mat, string name, TexProp tp)
        {
            var stName = name + "_ST";
            if (mat.HasProperty(stName))
            {
                tp.ST = mat.GetVector(stName);
                tp.HasST = true;
            }
            else
            {
                try
                {
                    var scale = mat.GetTextureScale(name);
                    var offset = mat.GetTextureOffset(name);
                    tp.ST = new Vector4(scale.x, scale.y, offset.x, offset.y);
                    tp.HasST = true;
                }
                catch { /* some shaders reject this */ }
            }

            var srName = name + "_ScrollRotate";
            if (mat.HasProperty(srName))
            {
                tp.ScrollRotate = mat.GetVector(srName);
                tp.HasScrollRotate = true;
            }
        }

        private static void ClassifyProp(Material mat, string name, bool isLil, TexProp tp)
        {
            var n = name.ToLowerInvariant();
            if (n.Contains("bump") || n.Contains("normal") || n == "_bumpmap" || n == "_bump2ndmap" ||
                n.Contains("tangentmap"))
            {
                tp.Kind = AtoTextureKind.Normal;
                tp.IsNormal = true;
                tp.UvChannel = 0;
                return;
            }

            if (n.Contains("mask") || n.Contains("occlusion") || n == "_metallicglossmap" ||
                n.Contains("smoothness") || n == "_alphamask" || n.Contains("shadowstrength") ||
                n.Contains("shadowborder") || n.Contains("rimshade"))
            {
                tp.Kind = AtoTextureKind.Gray;
                tp.IsGray = true;
                tp.IsMask = true;
                return;
            }

            if (n == "_maintex" || n.Contains("albedo") || n.Contains("basecolor") || n.Contains("diffuse"))
            {
                tp.Kind = AtoTextureKind.OpaqueAlbedo;
                return;
            }

            // Emission / color textures are albedo-like. / 自发光/颜色贴图按主色处理。
            tp.Kind = AtoTextureKind.OpaqueAlbedo;

            if (isLil)
            {
                // lilToon UV mode properties. / lilToon 的 UV 模式属性。
                var uvModeName = name + "_UVMode";
                if (mat.HasProperty(uvModeName))
                {
                    var mode = Mathf.RoundToInt(mat.GetFloat(uvModeName));
                    if (mode >= 0 && mode <= 7) tp.UvChannel = mode;
                }
            }
        }

        private static AtoAlphaMode DetectAlpha(Material mat, string shaderName, bool isLil)
        {
            var sn = shaderName.ToLowerInvariant();
            if (sn.Contains("cutout")) return AtoAlphaMode.Cutout;
            if (sn.Contains("transparent") || sn.Contains("fade") || sn.Contains("refract"))
                return AtoAlphaMode.Blend;

            if (isLil && mat.HasProperty("_TransparentMode"))
            {
                var m = Mathf.RoundToInt(mat.GetFloat("_TransparentMode"));
                // lil: 0 opaque, 1 cutout, 2 transparent, 3 refraction, ...
                if (m == 1) return AtoAlphaMode.Cutout;
                if (m >= 2) return AtoAlphaMode.Blend;
            }

            if (mat.HasProperty("_Mode"))
            {
                var m = Mathf.RoundToInt(mat.GetFloat("_Mode"));
                if (m == 1) return AtoAlphaMode.Cutout;
                if (m >= 2) return AtoAlphaMode.Blend;
            }

            var tag = mat.GetTag("RenderType", false, "");
            if (string.Equals(tag, "TransparentCutout", StringComparison.OrdinalIgnoreCase))
                return AtoAlphaMode.Cutout;
            if (string.Equals(tag, "Transparent", StringComparison.OrdinalIgnoreCase))
                return AtoAlphaMode.Blend;

            if (mat.IsKeywordEnabled("_ALPHATEST_ON")) return AtoAlphaMode.Cutout;
            if (mat.IsKeywordEnabled("_ALPHABLEND_ON") || mat.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON"))
                return AtoAlphaMode.Blend;

            return AtoAlphaMode.Opaque;
        }

        /// <summary>
        /// Standard + lilToon property names that are sampled with mesh UV (not matcap/screenspace).
        /// 用网格 UV 采样的标准/lilToon 属性（排除 matcap / 屏幕空间）。
        /// </summary>
        public static bool IsMeshUvSampled(string property, Material mat)
        {
            var n = property.ToLowerInvariant();
            if (n.Contains("matcap") && n.Contains("tex") && !n.Contains("mask") && !n.Contains("bump"))
                return false;
            if (n.Contains("cubemap") || n.Contains("cube") || n.Contains("ibl") || n.Contains("reflectionprobe"))
                return false;
            if (n.Contains("grab") || n.Contains("screen") || n.Contains("audio") && n.Contains("fft"))
                return false;
            if (n.Contains("lut") || n.Contains("gradation") && n.Contains("color"))
            {
                // gradient ramps are often 1D / non-mesh. / 渐变条通常不是网格 UV。
                if (n.Contains("grad")) return false;
            }
            return true;
        }
    }
}
