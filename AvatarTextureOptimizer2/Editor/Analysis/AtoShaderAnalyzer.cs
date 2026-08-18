using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Net.Fosa.AvatarTextureOptimizer;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Analyzes lilToon and standard keyword shaders via property tables + UVMode ints.
    /// 通过属性表、关键字与 UVMode 整型分析 lilToon/标准着色器。
    /// </summary>
    public static class AtoShaderAnalyzer
    {
        public struct TexSlot
        {
            public string Property;
            public int NameId;
            public AtoTextureRole Role;
            public int UvChannel;
            public bool IsSrgbHint;
            public bool SpecialUv; // MatCap / Rim / AudioLink etc. → whitelist
            public bool HasSt;     // per-slot ST / scroll
        }

        public struct MaterialAnalysis
        {
            public bool Compatible;
            public string Reason;
            public AtoBlendMode Blend;
            public float Cutoff;
            public List<TexSlot> Slots;
            public bool HasStTransform;
        }

        static readonly string[] NormalNames =
        {
            "_BumpMap", "_Bump2ndMap", "_NormalMap", "_DetailNormalMap",
            "_OutlineTex", "_OutlineVectorTex"
        };

        static readonly string[] MaskNames =
        {
            "_AlphaMask", "_ShadowColorTex", "_Shadow2ndColorTex", "_Shadow3rdColorTex",
            "_ShadowBorderMask", "_ShadowBlurMask", "_ShadowStrengthMask",
            "_RimColorTex", "_EmissionBlendMask", "_Emission2ndBlendMask",
            "_MatCapBlendMask", "_MatCap2ndBlendMask", "_ReflectionColorTex",
            "_AnisotropyTangentMap", "_AnisotropyScaleMask", "_SlowNormalMask"
        };

        static readonly string[] GrayNames =
        {
            "_MetallicGlossMap", "_OcclusionMap", "_ParallaxMap",
            "_SmoothnessTex", "_MetallicGlossMap"
        };

        static readonly string[] SpecialUse =
        {
            "_MatCapTex", "_MatCap2ndTex", "_MatCapBumpMap", "_MatCap2ndBumpMap",
            "_AudioLinkMask", "_DissolveMask", "_DissolveNoiseMask",
            "_Main2ndDissolveMask", "_Main3rdDissolveMask",
            "_FurNoiseMask", "_FurMask", "_FurLengthMask", "_FurVectorTex",
            "_TriMask", "_IDMask"
        };

        public static MaterialAnalysis Analyze(Material mat)
        {
            if (AtoExtensionPoints.TryOverrideShader(mat, out var ov))
                return ov;

            var r = new MaterialAnalysis
            {
                Compatible = true,
                Slots = new List<TexSlot>(),
                Blend = AtoBlendMode.Opaque,
                Cutoff = 0.5f
            };
            if (mat == null || mat.shader == null)
            {
                r.Compatible = false;
                r.Reason = "null material/shader";
                return r;
            }

            var shader = mat.shader;
            try
            {
                DetectBlend(mat, ref r);
                int n = shader.GetPropertyCount();
                for (int i = 0; i < n; i++)
                {
                    if (shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                    var name = shader.GetPropertyName(i);
                    if (name.EndsWith("_ST", StringComparison.Ordinal)) continue;

                    var texObj = mat.GetTexture(name);
                    if (texObj == null) continue;
                    if (!(texObj is Texture2D))
                    {
                        // Cubemap / 3D used as special purpose → not eligible, don't fail whole material.
                        AtoLog.VerboseInfo($"skip non-Texture2D {mat.name}.{name} ({texObj.GetType().Name})");
                        continue;
                    }

                    if (HasScrollRotate(mat, name) || IsSpecialUse(name))
                    {
                        r.HasStTransform = true;
                    }

                    var scale = mat.GetTextureScale(name);
                    var offset = mat.GetTextureOffset(name);
                    if (Mathf.Abs(scale.x - 1f) > 1e-4f || Mathf.Abs(scale.y - 1f) > 1e-4f ||
                        Mathf.Abs(offset.x) > 1e-4f || Mathf.Abs(offset.y) > 1e-4f)
                    {
                        r.HasStTransform = true;
                    }

                    var uvInfo = ReadUvChannel(mat, name);
                    var role = Classify(name, shader, i);
                    r.Slots.Add(new TexSlot
                    {
                        Property = name,
                        NameId = shader.GetPropertyNameId(i),
                        Role = role,
                        UvChannel = uvInfo.channel,
                        IsSrgbHint = role == AtoTextureRole.Albedo,
                        SpecialUv = uvInfo.special,
                        HasSt = slotSt
                    });
                }
            }
            catch (Exception ex)
            {
                r.Compatible = false;
                r.Reason = ex.Message;
            }

            return r;
        }

        static bool IsSpecialUse(string name)
        {
            foreach (var s in SpecialUse)
                if (name.Equals(s, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static bool HasScrollRotate(Material mat, string texName)
        {
            var p = texName + "_ScrollRotate";
            if (!mat.HasProperty(p)) return false;
            var v = mat.GetVector(p);
            return Mathf.Abs(v.x) > 1e-6f || Mathf.Abs(v.y) > 1e-6f ||
                   Mathf.Abs(v.z) > 1e-6f || Mathf.Abs(v.w) > 1e-6f;
        }

        /// <summary>
        /// lilToon: `_Main2ndTex_UVMode` = UV0|UV1|UV2|UV3|MatCap(4).
        /// URP: `_UVSec` for secondary maps. / 读取 UV 通道。
        /// </summary>
        public static (int channel, bool special) ReadUvChannel(Material mat, string texName)
        {
            string[] candidates =
            {
                texName + "_UVMode",
                texName + "UVMode",
                texName + "_UV",
                texName + "UV"
            };
            foreach (var c in candidates)
            {
                if (!mat.HasProperty(c)) continue;
                int v = Mathf.RoundToInt(mat.GetFloat(c));
                if (v >= 0 && v <= 3) return (v, false);
                return (0, true); // MatCap / Rim / Position
            }

            if (texName.IndexOf("2nd", StringComparison.OrdinalIgnoreCase) >= 0 && mat.HasProperty("_UVSec"))
            {
                int v = Mathf.RoundToInt(mat.GetFloat("_UVSec"));
                return (Mathf.Clamp(v, 0, 7), false);
            }

            if (texName.IndexOf("UV2", StringComparison.OrdinalIgnoreCase) >= 0) return (1, false);
            if (texName.IndexOf("UV3", StringComparison.OrdinalIgnoreCase) >= 0) return (2, false);
            if (texName.IndexOf("UV4", StringComparison.OrdinalIgnoreCase) >= 0) return (3, false);
            return (0, false);
        }

        static void DetectBlend(Material mat, ref MaterialAnalysis r)
        {
            if (mat.HasProperty("_Cutoff")) r.Cutoff = mat.GetFloat("_Cutoff");
            if (mat.IsKeywordEnabled("_ALPHATEST_ON") || mat.IsKeywordEnabled("_ALPHATEST") ||
                mat.IsKeywordEnabled("_CUTOUT"))
                r.Blend = AtoBlendMode.Cutout;
            else if (mat.IsKeywordEnabled("_ALPHABLEND_ON") || mat.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON") ||
                     mat.IsKeywordEnabled("_TRANSPARENT_ON") || mat.IsKeywordEnabled("_ALPHAMODULATE_ON"))
                r.Blend = AtoBlendMode.Blend;
            else if (mat.HasProperty("_Mode"))
            {
                var mode = mat.GetFloat("_Mode");
                if (mode > 1.5f) r.Blend = AtoBlendMode.Blend;
                else if (mode > 0.5f) r.Blend = AtoBlendMode.Cutout;
            }
            else if (mat.HasProperty("_TransparentMode"))
            {
                var mode = mat.GetFloat("_TransparentMode");
                if (mode > 1.5f) r.Blend = AtoBlendMode.Blend;
                else if (mode > 0.5f) r.Blend = AtoBlendMode.Cutout;
            }
            else if (mat.renderQueue >= 2450 && mat.renderQueue < 3000)
                r.Blend = AtoBlendMode.Cutout;
            else if (mat.renderQueue >= 3000)
                r.Blend = AtoBlendMode.Blend;
        }

        static AtoTextureRole Classify(string name, Shader shader, int index)
        {
            foreach (var n in NormalNames)
                if (name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) return AtoTextureRole.Normal;
            foreach (var n in MaskNames)
                if (name.Equals(n, StringComparison.OrdinalIgnoreCase)) return AtoTextureRole.Mask;
            foreach (var n in GrayNames)
                if (name.Equals(n, StringComparison.OrdinalIgnoreCase)) return AtoTextureRole.Gray;

            var flags = shader.GetPropertyFlags(index);
            if ((flags & ShaderPropertyFlags.Normal) != 0) return AtoTextureRole.Normal;
            if (name == "_MainTex" || name == "_BaseMap" || name == "_BaseColorMap" ||
                name == "_ColorMask" || name == "_Main2ndTex" || name == "_Main3rdTex" ||
                name == "_OutlineTex")
                return AtoTextureRole.Albedo;
            if (name.IndexOf("Mask", StringComparison.OrdinalIgnoreCase) >= 0) return AtoTextureRole.Mask;
            if (name.IndexOf("Normal", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Bump", StringComparison.OrdinalIgnoreCase) >= 0)
                return AtoTextureRole.Normal;
            return AtoTextureRole.Albedo;
        }
    }
}
