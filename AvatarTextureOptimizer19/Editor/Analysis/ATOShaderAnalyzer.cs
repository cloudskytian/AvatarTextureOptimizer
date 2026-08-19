// English: Shader / material analysis. lilToon table + standard keywords + third-party IATOShaderAnalyzer.
// 中文：着色器/材质分析。lilToon 属性表 + 标准关键字 + 第三方 IATOShaderAnalyzer。
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Net.Fosa.AvatarTextureOptimizer;
using Net.Fosa.AvatarTextureOptimizer.API;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    internal static class ATOShaderAnalyzer
    {
        public static List<ATOTextureSlotInfo> Analyze(Material material, ATOLogger log)
        {
            var result = new List<ATOTextureSlotInfo>();
            if (material == null || material.shader == null)
            {
                return result;
            }

            var analyzers = new List<IATOShaderAnalyzer>(ATOExtensionRegistry.GetShaderAnalyzers());
            analyzers.Sort((a, b) => b.Priority.CompareTo(a.Priority));
            foreach (var ext in analyzers)
            {
                try
                {
                    if (ext == null || !ext.CanAnalyze(material)) continue;
                    var slots = ext.Analyze(material);
                    if (slots == null) continue;
                    result.AddRange(slots);
                    log.VerboseInfo("shader analyzer '" + ext.Id + "' handled " + material.shader.name);
                    return result;
                }
                catch (Exception e)
                {
                    log.Warn("extension analyzer " + (ext != null ? ext.Id : "?") + " failed: " + e.Message);
                }
            }

            if (IsLilToon(material.shader))
            {
                result.AddRange(ATOLilToonAnalyzer.Analyze(material, log));
                if (result.Count > 0) return result;
            }

            result.AddRange(AnalyzeByShaderUtil(material, log));
            return result;
        }

        public static bool IsLilToon(Shader shader)
        {
            if (shader == null) return false;
            var n = shader.name;
            return n.IndexOf("lilToon", StringComparison.OrdinalIgnoreCase) >= 0
                   || n.IndexOf("lil/", StringComparison.OrdinalIgnoreCase) >= 0
                   || n.IndexOf("Hidden/lil", StringComparison.OrdinalIgnoreCase) >= 0
                   || n.IndexOf("ltspass", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static bool HasNonIdentityST(Material mat, string texProp)
        {
            if (mat == null || string.IsNullOrEmpty(texProp)) return false;
            var stName = texProp + "_ST";
            if (mat.HasProperty(stName))
            {
                var st = mat.GetVector(stName);
                if (!Approximately(st, new Vector4(1, 1, 0, 0))) return true;
            }

            try
            {
                var scale = mat.GetTextureScale(texProp);
                var offset = mat.GetTextureOffset(texProp);
                if (!Approximately(scale, Vector2.one) || !Approximately(offset, Vector2.zero)) return true;
            }
            catch
            {
                // some shaders reject GetTextureScale
            }

            var rot = texProp + "_ScrollRotate";
            if (mat.HasProperty(rot))
            {
                var v = mat.GetVector(rot);
                if (v.sqrMagnitude > 1e-8f) return true;
            }

            var angle = texProp + "Angle";
            if (mat.HasProperty(angle) && Mathf.Abs(mat.GetFloat(angle)) > 1e-6f) return true;

            var isDecal = texProp + "IsDecal";
            if (mat.HasProperty(isDecal) && mat.GetFloat(isDecal) > 0.5f) return true;

            return false;
        }

        internal static ATOAlphaMode DetectAlphaMode(Material mat, out float cutoff)
        {
            cutoff = 0.5f;
            if (mat == null) return ATOAlphaMode.Opaque;

            if (mat.HasProperty("_Cutoff")) cutoff = mat.GetFloat("_Cutoff");
            else if (mat.HasProperty("_CutoffAlpha")) cutoff = mat.GetFloat("_CutoffAlpha");

            var tag = mat.GetTag("RenderType", false, "");
            if (string.Equals(tag, "TransparentCutout", StringComparison.OrdinalIgnoreCase)) return ATOAlphaMode.Cutout;
            if (string.Equals(tag, "Transparent", StringComparison.OrdinalIgnoreCase)) return ATOAlphaMode.Blend;

            if (mat.IsKeywordEnabled("_ALPHATEST_ON") || mat.IsKeywordEnabled("_ALPHATEST")) return ATOAlphaMode.Cutout;
            if (mat.IsKeywordEnabled("_ALPHABLEND_ON") || mat.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON"))
                return ATOAlphaMode.Blend;

            // lilToon
            if (mat.HasProperty("_TransparentMode"))
            {
                var m = Mathf.RoundToInt(mat.GetFloat("_TransparentMode"));
                if (m == 1) return ATOAlphaMode.Cutout;
                if (m >= 2) return ATOAlphaMode.Blend;
            }

            if (mat.HasProperty("_Mode"))
            {
                var m = Mathf.RoundToInt(mat.GetFloat("_Mode"));
                if (m == 1) return ATOAlphaMode.Cutout;
                if (m >= 2) return ATOAlphaMode.Blend;
            }

            if (mat.shader != null)
            {
                var n = mat.shader.name;
                if (n.IndexOf("Cutout", StringComparison.OrdinalIgnoreCase) >= 0) return ATOAlphaMode.Cutout;
                if (n.IndexOf("Trans", StringComparison.OrdinalIgnoreCase) >= 0) return ATOAlphaMode.Blend;
            }

            return ATOAlphaMode.Opaque;
        }

        internal static ATOTextureSemantic GuessSemantic(string prop, Texture2D tex, ATOAlphaMode alpha)
        {
            var p = prop ?? "";
            var pl = p.ToLowerInvariant();
            if (pl.Contains("bump") || pl.Contains("normal") ||
                ATOTextureCache.TextureImporterTypeGuess(tex) == TextureImporterType.NormalMap)
                return ATOTextureSemantic.Normal;
            if (pl.Contains("mask") || pl.Contains("occlusion") || pl.Contains("metallic") ||
                pl.Contains("smooth") || pl.Contains("rough") || pl.Contains("ao") ||
                pl.Contains("shadow") || pl.Contains("rimshade") || pl.Contains("aniso"))
                return ATOTextureSemantic.Gray;
            if (tex != null && ATOTextureCache.IsLinearAsset(tex) && !pl.Contains("main") && !pl.Contains("albedo") &&
                !pl.Contains("base") && !pl.Contains("emission") && !pl.Contains("color"))
                return ATOTextureSemantic.Gray;
            if (alpha == ATOAlphaMode.Opaque) return ATOTextureSemantic.AlbedoOpaque;
            return ATOTextureSemantic.AlbedoTransparent;
        }

        private static List<ATOTextureSlotInfo> AnalyzeByShaderUtil(Material material, ATOLogger log)
        {
            var list = new List<ATOTextureSlotInfo>();
            var shader = material.shader;
            var count = ShaderUtil.GetPropertyCount(shader);
            float cutoff;
            var alpha = DetectAlphaMode(material, out cutoff);
            var unknownSpecial = false;

            for (var i = 0; i < count; i++)
            {
                if (ShaderUtil.GetPropertyType(shader, i) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                var prop = ShaderUtil.GetPropertyName(shader, i);
                var tex = material.HasProperty(prop) ? material.GetTexture(prop) as Texture2D : null;
                if (tex == null) continue;

                var slot = new ATOTextureSlotInfo
                {
                    Material = material,
                    PropertyName = prop,
                    Texture = tex,
                    UvChannel = GuessUvChannel(material, prop),
                    HasTransform = HasNonIdentityST(material, prop),
                    IsMeshSampled = true,
                    IsSpecialPurpose = IsSpecialPurpose(prop),
                    Semantic = GuessSemantic(prop, tex, alpha),
                    AlphaMode = alpha,
                    Cutoff = cutoff,
                    WrapMode = tex.wrapMode,
                    FilterMode = tex.filterMode,
                    LinearColorSpace = ATOTextureCache.IsLinearAsset(tex)
                };

                if (slot.UvChannel < 0)
                {
                    slot.IsMeshSampled = false;
                    slot.IsSpecialPurpose = true;
                }

                if (slot.IsSpecialPurpose) unknownSpecial = true;
                list.Add(slot);
            }

            if (unknownSpecial)
            {
                log.VerboseInfo("standard analyzer marked special-purpose slots on " + shader.name);
            }

            return list;
        }

        internal static int GuessUvChannel(Material mat, string prop)
        {
            var uvModeNames = new[]
            {
                prop + "_UVMode", prop + "UVMode", prop + "_UV", "_MainTex_UVMode"
            };
            foreach (var n in uvModeNames)
            {
                if (!mat.HasProperty(n)) continue;
                var v = Mathf.RoundToInt(mat.GetFloat(n));
                if (v >= 0 && v <= 7) return v;
                if (v == 4) return -1; // matcap / non-mesh
            }

            return 0;
        }

        internal static bool IsSpecialPurpose(string prop)
        {
            var p = (prop ?? "").ToLowerInvariant();
            if (p.Contains("matcap")) return true;
            if (p.Contains("cubemap") || p.Contains("cube")) return true;
            if (p.Contains("dither")) return true;
            if (p.Contains("lut") || p.Contains("gradation")) return true;
            if (p.Contains("audio")) return true;
            if (p.Contains("decal")) return true;
            if (p.Contains("parallax") || p.Contains("height")) return true;
            if (p.Contains("grab") || p.Contains("screen")) return true;
            return false;
        }

        internal static bool Approximately(Vector4 a, Vector4 b)
        {
            return (a - b).sqrMagnitude < 1e-8f;
        }

        internal static bool Approximately(Vector2 a, Vector2 b)
        {
            return (a - b).sqrMagnitude < 1e-8f;
        }
    }
}
