using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// Analyzes shader property sheets and keywords (lilToon + standard Unity naming).
    /// Never guesses unknown custom shaders: whitelist + warning.
    /// 分析着色器属性表与关键字；无法识别则白名单并警告。
    /// </summary>
    public static class ShaderPropertyAnalyzer
    {
        static readonly string[] AlbedoNames =
        {
            "_MainTex", "_BaseMap", "_BaseColorMap", "_ColorMask", "_MainTexHSVG"
        };

        static readonly string[] NormalNames =
        {
            "_BumpMap", "_NormalMap", "_BumpMap2nd", "_MainNormal"
        };

        static readonly string[] MaskNames =
        {
            "_ShadowColorTex", "_RimColorTex", "_EmissionMap", "_EmissionBlendMask",
            "_MatCapTex", "_OutlineTex", "_AlphaMask", "_Main2ndTex", "_Main3rdTex",
            "_MetallicGlossMap", "_OcclusionMap", "_ParallaxMap"
        };

        public struct Binding
        {
            public string Property;
            public Texture2D Texture;
            public AtoTextureSemantic Semantic;
            public int UvChannel;
            public bool HasST;
            public Vector4 ST;
            public bool Known;
        }

        public static List<Binding> Analyze(Material mat, out string warning)
        {
            warning = null;
            var list = new List<Binding>();
            if (mat == null || mat.shader == null)
            {
                warning = "Material or shader is null";
                return list;
            }

            var shader = mat.shader;
            int count = shader.GetPropertyCount();
            bool anyTex = false;
            for (int i = 0; i < count; i++)
            {
                if (shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                anyTex = true;
                string name = shader.GetPropertyName(i);
                var tex = mat.GetTexture(name) as Texture2D;
                if (tex == null) continue;

                var bind = new Binding
                {
                    Property = name,
                    Texture = tex,
                    Semantic = Classify(name),
                    UvChannel = GuessUvChannel(shader, name),
                    Known = IsKnown(name)
                };

                string stName = name + "_ST";
                if (mat.HasProperty(stName))
                {
                    bind.HasST = true;
                    bind.ST = mat.GetVector(stName);
                }
                else if (mat.HasProperty("_MainTex_ST") && name == "_MainTex")
                {
                    bind.HasST = true;
                    bind.ST = mat.GetTextureScale(name);
                    var sc = mat.GetTextureScale(name);
                    var of = mat.GetTextureOffset(name);
                    bind.ST = new Vector4(sc.x, sc.y, of.x, of.y);
                }

                list.Add(bind);
            }

            if (!anyTex)
                warning = "Shader has no texture properties: " + shader.name;

            // lilToon / standard render mode
            return list;
        }

        public static AtoAlphaMode ReadAlphaMode(Material mat, out float cutoff)
        {
            cutoff = 0.5f;
            if (mat == null) return AtoAlphaMode.Opaque;
            if (mat.HasProperty("_Cutoff")) cutoff = mat.GetFloat("_Cutoff");
            if (mat.IsKeywordEnabled("_ALPHATEST_ON") || mat.IsKeywordEnabled("_ALPHATEST"))
                return AtoAlphaMode.Cutout;
            if (mat.IsKeywordEnabled("_ALPHABLEND_ON") || mat.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON"))
                return AtoAlphaMode.Blend;
            if (mat.HasProperty("_Mode"))
            {
                int mode = Mathf.RoundToInt(mat.GetFloat("_Mode"));
                if (mode == 1) return AtoAlphaMode.Cutout;
                if (mode >= 2) return AtoAlphaMode.Blend;
            }
            if (mat.HasProperty("_TransparentMode"))
            {
                int mode = Mathf.RoundToInt(mat.GetFloat("_TransparentMode"));
                // lilToon: 0 Opaque, 1 Cutout, 2 Transparent, 3 Fur...
                if (mode == 1) return AtoAlphaMode.Cutout;
                if (mode >= 2) return AtoAlphaMode.Blend;
            }
            if (mat.renderQueue >= 2450 && mat.renderQueue < 3000) return AtoAlphaMode.Cutout;
            if (mat.renderQueue >= 3000) return AtoAlphaMode.Blend;
            return AtoAlphaMode.Opaque;
        }

        public static bool HasNonIdentityST(Vector4 st)
        {
            return Mathf.Abs(st.x - 1f) > 1e-4f || Mathf.Abs(st.y - 1f) > 1e-4f ||
                   Mathf.Abs(st.z) > 1e-4f || Mathf.Abs(st.w) > 1e-4f;
        }

        static AtoTextureSemantic Classify(string name)
        {
            foreach (var n in NormalNames)
                if (name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0 || name == n)
                    return AtoTextureSemantic.Normal;
            if (name.IndexOf("Bump", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Normal", StringComparison.OrdinalIgnoreCase) >= 0)
                return AtoTextureSemantic.Normal;
            if (name.IndexOf("Mask", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Shadow", StringComparison.OrdinalIgnoreCase) >= 0)
                return AtoTextureSemantic.Mask;
            if (name.IndexOf("Metallic", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Gloss", StringComparison.OrdinalIgnoreCase) >= 0)
                return AtoTextureSemantic.MetallicGloss;
            if (name.IndexOf("Emission", StringComparison.OrdinalIgnoreCase) >= 0)
                return AtoTextureSemantic.Emission;
            foreach (var n in AlbedoNames)
                if (name == n) return AtoTextureSemantic.Albedo;
            return AtoTextureSemantic.Unknown;
        }

        static bool IsKnown(string name)
        {
            foreach (var n in AlbedoNames) if (name == n) return true;
            foreach (var n in NormalNames) if (name == n) return true;
            foreach (var n in MaskNames) if (name == n) return true;
            return name.StartsWith("_", StringComparison.Ordinal);
        }

        static int GuessUvChannel(Shader shader, string prop)
        {
            // Standard / URP: _MainTex uses UV0. lilToon 2nd/3rd may use UV1/UV2 via keywords.
            if (prop.IndexOf("2nd", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
            if (prop.IndexOf("3rd", StringComparison.OrdinalIgnoreCase) >= 0) return 2;
            return 0;
        }
    }
}
