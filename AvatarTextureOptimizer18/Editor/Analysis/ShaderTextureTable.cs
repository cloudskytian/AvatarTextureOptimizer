using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Analysis
{
    // 单个贴图属性的着色器元信息。Shader metadata for one texture property.
    public sealed class ShaderTextureInfo
    {
        public string propertyName;
        public ATOTextureKind kind = ATOTextureKind.Color;
        // 默认 UV 通道。Default UV channel.
        public int defaultUvChannel;
        // liltoon 的 UVMode 属性名（如 _EmissionMap_UVMode）；null = 固定通道。
        // liltoon UVMode property name; null = fixed channel.
        public string uvModeProperty;
        // liltoon 的 ScrollRotate 属性名（如 _MainTex_ScrollRotate）。liltoon ScrollRotate property name.
        public string scrollRotateProperty;
        // [NoScaleOffset] → ST 无关。Shader declares NoScaleOffset → ST is irrelevant.
        public bool noScaleOffset;
        // 特殊用途（MatCap/Rim/渐变/屏幕空间/灯光值采样等）→ 白名单。Special purpose (matcap/rim/gradation/screen-space/lighting) → whitelist.
        public bool specialPurpose;
        // 存在可动画的 ST/ScrollRotate/UVMode。Has animatable ST/ScrollRotate/UVMode.
        public bool animatableTransform;
    }

    // 材质贴图属性表：liltoon 专用表（依据 lilToon 2.3.4 源码取证）+ 标准关键字表 + 未知着色器通用解析。
    // Material texture-property table: liltoon-specific (sourced from lilToon 2.3.4) + standard-keyword table + generic parser.
    internal static class ShaderTextureTable
    {
        // ---- liltoon 2.x 表（取证自 lilToon 2.3.4 Shader/lts.shader 属性块）----
        private static readonly Dictionary<string, ShaderTextureInfo> Liltoon = BuildLiltoonTable();

        // ---- 标准关键字表 ----
        private static readonly Dictionary<string, ShaderTextureInfo> Standard = BuildStandardTable();

        // 通用解析缓存（null = 解析失败/不支持）。Generic parser cache (null = parse failed / unsupported).
        private static readonly Dictionary<Shader, Dictionary<string, ShaderTextureInfo>> ParsedCache = new Dictionary<Shader, Dictionary<string, ShaderTextureInfo>>();

        // 属性行正则：([Attr])* _Name ("display", 2D) = "default" {}
        private static readonly Regex PropPattern = new Regex(@"^\s*((?:\[[^\]]*\]\s*)*)_(\w+)\s*\(\s*""[^""]*""\s*,\s*(2D)\s*\)", RegexOptions.Compiled);

        // 返回该材质着色器已知的全部贴图属性。Returns all known texture properties of the material's shader.
        public static List<ShaderTextureInfo> GetProperties(Material material)
        {
            var result = new List<ShaderTextureInfo>();
            if (material == null || material.shader == null) return result;

            string name = material.shader.name.ToLowerInvariant();
            if (name.Contains("liltoon") || name.StartsWith("lts_") || name.StartsWith("liltoon"))
            {
                foreach (var kv in Liltoon)
                {
                    if (material.HasProperty(kv.Key)) result.Add(kv.Value);
                }
                return result;
            }

            // 标准关键字表 + 通用解析兜底。Standard table + generic parser fallback.
            foreach (var kv in Standard)
            {
                if (material.HasProperty(kv.Key)) result.Add(kv.Value);
            }
            var parsed = ParseShader(material.shader);
            if (parsed != null)
            {
                foreach (var kv in parsed)
                {
                    if (material.HasProperty(kv.Key) && !HasProperty(result, kv.Key)) result.Add(kv.Value);
                }
            }
            return result;
        }

        // 着色器是否受支持：liltoon → 是；否则标准属性或通用解析成功 → 是；其余 → 否（白名单 + warning）。
        // Whether the shader is supported: liltoon → yes; standard props or successful generic parse → yes; otherwise no (whitelist + warning).
        public static bool IsShaderSupported(Material material)
        {
            if (material == null || material.shader == null) return false;
            string name = material.shader.name.ToLowerInvariant();
            if (name.Contains("liltoon") || name.StartsWith("lts_")) return true;
            foreach (var kv in Standard)
            {
                if (material.HasProperty(kv.Key)) return true;
            }
            return ParseShader(material.shader) != null;
        }

        private static bool HasProperty(List<ShaderTextureInfo> list, string propertyName)
        {
            foreach (var info in list)
            {
                if (info.propertyName == propertyName) return true;
            }
            return false;
        }

        // ---- liltoon 表构建 ----
        private static Dictionary<string, ShaderTextureInfo> BuildLiltoonTable()
        {
            var t = new Dictionary<string, ShaderTextureInfo>();

            // 主颜色组。Main color group.
            Add(t, "_MainTex", ATOTextureKind.Color, 0, null, "_MainTex_ScrollRotate", false, false);
            Add(t, "_Main2ndTex", ATOTextureKind.Color, 0, "_Main2ndTex_UVMode", "_Main2ndTex_ScrollRotate", false, false);
            Add(t, "_Main3rdTex", ATOTextureKind.Color, 0, "_Main3rdTex_UVMode", "_Main3rdTex_ScrollRotate", false, false);
            Add(t, "_BaseMap", ATOTextureKind.Color, 0, null, null, false, false);
            Add(t, "_BaseColorMap", ATOTextureKind.Color, 0, null, null, false, false);

            // 法线组。Normal group.
            Add(t, "_BumpMap", ATOTextureKind.NormalMap, 0, null, null, false, false);
            Add(t, "_Bump2ndMap", ATOTextureKind.NormalMap, 0, "_Bump2ndMap_UVMode", null, false, false);
            Add(t, "_AnisotropyTangentMap", ATOTextureKind.NormalMap, 0, null, null, false, false);
            Add(t, "_OutlineVectorTex", ATOTextureKind.NormalMap, 0, null, null, false, false);

            // 发光组。Emission group.
            Add(t, "_EmissionMap", ATOTextureKind.Color, 0, "_EmissionMap_UVMode", "_EmissionMap_ScrollRotate", false, false);
            Add(t, "_Emission2ndMap", ATOTextureKind.Color, 0, "_Emission2ndMap_UVMode", "_Emission2ndMap_ScrollRotate", false, false);

            // 描边。Outline.
            Add(t, "_OutlineTex", ATOTextureKind.Color, 0, null, "_OutlineTex_ScrollRotate", false, false);

            // 其他 UV 采样颜色贴图。Other UV-sampled color textures.
            Add(t, "_GlitterColorTex", ATOTextureKind.Color, 0, "_GlitterColorTex_UVMode", null, false, false);
            Add(t, "_GlitterShapeTex", ATOTextureKind.Mask, 0, null, null, false, false);
            Add(t, "_BacklightColorTex", ATOTextureKind.Color, 0, null, null, true, false);

            // 高度图。Height map.
            Add(t, "_ParallaxMap", ATOTextureKind.Grayscale, 0, null, null, true, false);

            // MatCap 组：使用 MatCap 球面 UV，非网格 UV → 特殊用途白名单。MatCap group: matcap sphere UV → whitelist.
            Add(t, "_MatCapTex", ATOTextureKind.Color, 0, null, null, false, true);
            Add(t, "_MatCapBumpMap", ATOTextureKind.NormalMap, 0, null, null, false, true);
            Add(t, "_MatCap2ndTex", ATOTextureKind.Color, 0, null, null, false, true);
            Add(t, "_MatCap2ndBumpMap", ATOTextureKind.NormalMap, 0, null, null, false, true);

            // 蒙版组。Mask group.
            Add(t, "_MainColorAdjustMask", ATOTextureKind.Mask, 0, null, null, true, false);
            Add(t, "_Main2ndBlendMask", ATOTextureKind.Mask, 0, null, null, true, false);
            Add(t, "_Main2ndDissolveMask", ATOTextureKind.Mask, 0, null, null, true, false);
            Add(t, "_Main2ndDissolveNoiseMask", ATOTextureKind.Mask, 0, null, "_Main2ndDissolveNoiseMask_ScrollRotate", true, false);
            Add(t, "_Main3rdBlendMask", ATOTextureKind.Mask, 0, null, null, true, false);
            Add(t, "_Main3rdDissolveMask", ATOTextureKind.Mask, 0, null, null, true, false);
            Add(t, "_Main3rdDissolveNoiseMask", ATOTextureKind.Mask, 0, null, "_Main3rdDissolveNoiseMask_ScrollRotate", true, false);
            Add(t, "_AlphaMask", ATOTextureKind.Mask, 0, null, null, true, false);
            Add(t, "_Bump2ndScaleMask", ATOTextureKind.Mask, 0, null, null, true, false);
            Add(t, "_AnisotropyScaleMask", ATOTextureKind.Mask, 0, null, null, true, false);
            Add(t, "_AnisotropyShiftNoiseMask", ATOTextureKind.Mask, 0, null, null, true, false);
            Add(t, "_ShadowStrengthMask", ATOTextureKind.Mask, 0, null, null, true, false);
            Add(t, "_ShadowBorderMask", ATOTextureKind.Mask, 0, null, null, true, false);
            Add(t, "_ShadowBlurMask", ATOTextureKind.Mask, 0, null, null, true, false);
            Add(t, "_RimShadeMask", ATOTextureKind.Mask, 0, null, null, true, false);
            Add(t, "_SmoothnessTex", ATOTextureKind.Mask, 0, null, null, true, false);
            Add(t, "_MetallicGlossMap", ATOTextureKind.Mask, 0, null, null, true, false);
            Add(t, "_MatCapBlendMask", ATOTextureKind.Mask, 0, null, null, true, false);
            Add(t, "_MatCap2ndBlendMask", ATOTextureKind.Mask, 0, null, null, true, false);
            Add(t, "_EmissionBlendMask", ATOTextureKind.Mask, 0, null, "_EmissionBlendMask_ScrollRotate", true, false);
            Add(t, "_Emission2ndBlendMask", ATOTextureKind.Mask, 0, null, "_Emission2ndBlendMask_ScrollRotate", true, false);
            Add(t, "_OutlineWidthMask", ATOTextureKind.Mask, 0, null, null, true, false);
            Add(t, "_DissolveMask", ATOTextureKind.Mask, 0, null, null, true, false);
            Add(t, "_DissolveNoiseMask", ATOTextureKind.Mask, 0, null, "_DissolveNoiseMask_ScrollRotate", true, false);
            Add(t, "_AudioLinkMask", ATOTextureKind.Mask, 0, "_AudioLinkMask_UVMode", "_AudioLinkMask_ScrollRotate", true, false);
            Add(t, "_RimColorTex", ATOTextureKind.Mask, 0, null, null, true, false);
            Add(t, "_ReflectionColorTex", ATOTextureKind.Mask, 0, null, null, true, false);

            // 渐变/灯光值/屏幕空间采样 → 特殊用途白名单。Gradation / lighting-value / screen-space sampling → whitelist.
            Add(t, "_MainGradationTex", ATOTextureKind.Color, 0, null, null, true, true);
            Add(t, "_EmissionGradTex", ATOTextureKind.Color, 0, null, null, true, true);
            Add(t, "_Emission2ndGradTex", ATOTextureKind.Color, 0, null, null, true, true);
            Add(t, "_Ramp", ATOTextureKind.Color, 0, null, null, true, true);
            Add(t, "_ShadowColorTex", ATOTextureKind.Color, 0, null, null, true, true);
            Add(t, "_Shadow2ndColorTex", ATOTextureKind.Color, 0, null, null, true, true);
            Add(t, "_Shadow3rdColorTex", ATOTextureKind.Color, 0, null, null, true, true);
            Add(t, "_DitherTex", ATOTextureKind.Color, 0, null, null, true, true);
            Add(t, "_AudioLinkLocalMap", ATOTextureKind.Color, 0, null, null, true, true);

            return t;
        }

        private static void Add(Dictionary<string, ShaderTextureInfo> table, string prop, ATOTextureKind kind, int uvChannel,
            string uvModeProperty, string scrollRotateProperty, bool noScaleOffset, bool specialPurpose)
        {
            table[prop] = new ShaderTextureInfo
            {
                propertyName = prop,
                kind = kind,
                defaultUvChannel = uvChannel,
                uvModeProperty = uvModeProperty,
                scrollRotateProperty = scrollRotateProperty,
                noScaleOffset = noScaleOffset,
                specialPurpose = specialPurpose,
                animatableTransform = !noScaleOffset || !string.IsNullOrEmpty(uvModeProperty) || !string.IsNullOrEmpty(scrollRotateProperty)
            };
        }

        // ---- 标准关键字表 ----
        private static Dictionary<string, ShaderTextureInfo> BuildStandardTable()
        {
            var t = new Dictionary<string, ShaderTextureInfo>();
            Add(t, "_MainTex", ATOTextureKind.Color, 0, null, null, false, false);
            Add(t, "_BaseMap", ATOTextureKind.Color, 0, null, null, false, false);
            Add(t, "_BumpMap", ATOTextureKind.NormalMap, 0, null, null, false, false);
            Add(t, "_NormalMap", ATOTextureKind.NormalMap, 0, null, null, false, false);
            Add(t, "_MetallicGlossMap", ATOTextureKind.Mask, 0, null, null, false, false);
            Add(t, "_OcclusionMap", ATOTextureKind.Mask, 0, null, null, false, false);
            Add(t, "_EmissionMap", ATOTextureKind.Color, 0, null, null, false, false);
            Add(t, "_DetailAlbedoMap", ATOTextureKind.Color, 1, null, null, false, false);
            Add(t, "_DetailNormalMap", ATOTextureKind.NormalMap, 1, null, null, false, false);
            Add(t, "_DetailMask", ATOTextureKind.Mask, 1, null, null, false, false);
            Add(t, "_ParallaxMap", ATOTextureKind.Grayscale, 0, null, null, false, false);
            Add(t, "_MaskTex", ATOTextureKind.Mask, 0, null, null, false, false); // UTS2 惯例
            return t;
        }

        // ---- 未知着色器通用解析：读取 .shader 的 Properties 块。Generic parser: reads the Properties block of a .shader file.
        private static Dictionary<string, ShaderTextureInfo> ParseShader(Shader shader)
        {
            Dictionary<string, ShaderTextureInfo> cached;
            if (ParsedCache.TryGetValue(shader, out cached)) return cached;

            Dictionary<string, ShaderTextureInfo> table = null;
            try
            {
                var path = AssetDatabase.GetAssetPath(shader);
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    ParsedCache[shader] = null;
                    return null;
                }
                string text = File.ReadAllText(path);
                int start = text.IndexOf("Properties", StringComparison.Ordinal);
                if (start < 0) { ParsedCache[shader] = null; return null; }
                int brace = text.IndexOf('{', start);
                if (brace < 0) { ParsedCache[shader] = null; return null; }
                int depth = 0, end = -1;
                for (int i = brace; i < text.Length; i++)
                {
                    if (text[i] == '{') depth++;
                    else if (text[i] == '}') { depth--; if (depth == 0) { end = i; break; } }
                }
                if (end < 0) { ParsedCache[shader] = null; return null; }

                table = new Dictionary<string, ShaderTextureInfo>();
                string block = text.Substring(brace + 1, end - brace - 1);
                foreach (var rawLine in block.Split('\n'))
                {
                    var match = PropPattern.Match(rawLine);
                    if (!match.Success) continue;
                    string prop = "_" + match.Groups[2].Value;
                    string attrs = match.Groups[1].Value.ToLowerInvariant();
                    bool isNormal = attrs.Contains("[normal]");
                    bool noST = attrs.Contains("[noscaleoffset]");
                    string lp = prop.ToLowerInvariant();

                    ATOTextureKind kind;
                    if (isNormal) kind = ATOTextureKind.NormalMap;
                    else if (lp.Contains("parallax") || lp.Contains("height")) kind = ATOTextureKind.Grayscale;
                    else if (lp.Contains("bump")) kind = ATOTextureKind.NormalMap; // "bump" 惯例为法线
                    else if (lp.Contains("mask") || lp.Contains("smoothness") || lp.Contains("metallic") || lp.Contains("occlusion")) kind = ATOTextureKind.Mask;
                    else kind = ATOTextureKind.Color;

                    table[prop] = new ShaderTextureInfo
                    {
                        propertyName = prop,
                        kind = kind,
                        defaultUvChannel = 0,
                        noScaleOffset = noST,
                        animatableTransform = !noST
                    };
                }
                if (table.Count == 0) table = null;
            }
            catch (Exception e)
            {
                ATOLog.Warn(string.Format("着色器解析失败 / shader parse failed: {0} ({1})", shader.name, e.Message));
                table = null;
            }

            ParsedCache[shader] = table;
            return table;
        }
    }

    // 透明模式检测：liltoon 依据着色器变体名；通用依据关键字/RenderType/渲染队列。
    // Alpha-mode detection: liltoon by shader variant name; generic by keywords/RenderType/render queue.
    internal static class ATOAlphaModeUtil
    {
        public static void Detect(Material material, out ATOAlphaMode mode, out float cutoff)
        {
            mode = ATOAlphaMode.Opaque;
            cutoff = 0.5f;
            if (material == null || material.shader == null)
            {
                mode = ATOAlphaMode.Unknown;
                return;
            }

            if (material.HasProperty("_Cutoff")) cutoff = material.GetFloat("_Cutoff");
            if (material.HasProperty("_SubpassCutoff")) cutoff = Mathf.Max(cutoff, material.GetFloat("_SubpassCutoff"));

            string name = material.shader.name.ToLowerInvariant();
            // liltoon：渲染模式由着色器变体名决定（与 lilShaderUtils 同源逻辑）。
            if (name.Contains("liltoon") || name.StartsWith("lts"))
            {
                if (name.Contains("cutout")) mode = ATOAlphaMode.Cutout;
                else if (name.Contains("trans")) mode = ATOAlphaMode.Blend;
                else mode = ATOAlphaMode.Opaque;
                return;
            }

            // 通用：关键字 → RenderType 标签 → 渲染队列。
            var kws = CollectKeywords(material);
            if (kws.Contains("_alphatest_on")) { mode = ATOAlphaMode.Cutout; return; }
            if (kws.Contains("_alphablend_on") || kws.Contains("_alphapremultiply_on")) { mode = ATOAlphaMode.Blend; return; }

            string rt = material.GetTag("RenderType", false, "");
            if (rt.IndexOf("TransparentCutout", System.StringComparison.OrdinalIgnoreCase) >= 0 || rt.IndexOf("TransparentCutOut", System.StringComparison.OrdinalIgnoreCase) >= 0) { mode = ATOAlphaMode.Cutout; return; }
            if (rt.IndexOf("Transparent", System.StringComparison.OrdinalIgnoreCase) >= 0) { mode = ATOAlphaMode.Blend; return; }

            int rq = material.renderQueue;
            if (rq >= 3000 && rq < 5000) mode = ATOAlphaMode.Blend;
            else if (rq >= 2000 && rq < 2500) mode = ATOAlphaMode.Cutout;
            else mode = ATOAlphaMode.Opaque;
        }

        private static HashSet<string> CollectKeywords(Material m)
        {
            var set = new HashSet<string>();
            if (m.shaderKeywords != null)
            {
                foreach (var k in m.shaderKeywords) set.Add(k.ToLowerInvariant());
            }
            try
            {
                foreach (var k in m.enabledKeywords) set.Add(k.name.ToLowerInvariant());
            }
            catch (Exception)
            {
                // 某些上下文下 enabledKeywords 不可用；忽略。enabledKeywords unavailable in some contexts; ignore.
            }
            return set;
        }
    }
}
