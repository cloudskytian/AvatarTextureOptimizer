// ATOMaterialAnalyzer.cs — 材质贴图用途分析器 / Material texture-usage analyzer.
// 说明：解析材质着色器，提取贴图用途（角色/UV通道/ST/透明度模式/Cutoff）。
// lilToon 及其他使用标准关键字的着色器：解析着色器资产源码的属性表与关键字，尽可能兼容未来版本；
// 无法解析的属性/着色器 → 该用途按白名单处理并报 warning。
// Note: parses the material's shader and extracts texture usages (role / UV channel / ST / alpha mode / cutoff).
// lilToon and other keyword-based shaders: parse the shader asset source (property table & keywords) to stay
// compatible with future versions; properties/shaders that cannot be resolved → whitelist that usage + warning.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// 着色器属性解析结果缓存。/ Per-shader parsed property info cache.
    /// </summary>
    internal sealed class ATOShaderInfo
    {
        public string name;                                   // 着色器名 / shader name
        public bool supported;                                // 是否支持 / supported
        public Dictionary<string, ATOPropInfo> props;         // 属性名 → 信息 / property name → info
        public HashSet<string> keywords;                      // 关键字集合 / keyword set
        public string alphaModeKeywordTest = "";              // 透明度关键字前缀（如 _ALPHATEST_ON）/ alpha-mode keyword prefix
        public string cutoffProp = "_Cutoff";                 // Cutoff 属性名 / cutoff property name
        public bool nameBasedAlpha;                           // 用着色器文件名判断渲染模式（lilToon）/ shader-name-based render mode (lilToon)
    }

    /// <summary>着色器属性信息。/ Shader property info.</summary>
    internal sealed class ATOPropInfo
    {
        public string name;              // 属性名 / property name
        public bool isTexture;           // 是否为 2D 贴图 / is a 2D texture
        public ATORole role;             // 角色 / role
        public int defaultUvChannel;     // 默认 UV 通道 / default UV channel
        public bool hasUvMode;           // 是否有 *UVMode 枚举属性 / has a *UVMode enum property
        public string stProp;            // 对应 ST 属性名 / matching ST property name
        public bool recognized;          // 是否识别（未识别 → 白名单）/ recognized (unrecognized → whitelist)
        public string note;              // 说明 / note
    }

    /// <summary>材质贴图用途分析器。/ Material texture usage analyzer.</summary>
    internal static class ATOMaterialAnalyzer
    {
        private static readonly Dictionary<Shader, ATOShaderInfo> ShaderCache = new Dictionary<Shader, ATOShaderInfo>();
        private static readonly HashSet<string> WarnedShaders = new HashSet<string>();

        // ---- 关键字驱动的内置着色器表 / keyword-driven builtin shader tables ----
        private static readonly Dictionary<string, (ATORole role, int uv, bool hasUvMode, string note)>
            StandardProps = new Dictionary<string, (ATORole, int, bool, string)>
        {
            ["_MainTex"] = (ATORole.Main, 0, false, "albedo"),
            ["_BumpMap"] = (ATORole.Normal, 0, false, "normal"),
            ["_MetallicGlossMap"] = (ATORole.Mask, 0, false, "metallic/gloss"),
            ["_OcclusionMap"] = (ATORole.Mask, 0, false, "occlusion"),
            ["_EmissionMap"] = (ATORole.Color, 0, false, "emission"),
            ["_DetailAlbedoMap"] = (ATORole.Color, 1, false, "detail albedo (UV1)"),
            ["_DetailNormalMap"] = (ATORole.Normal, 1, false, "detail normal (UV1)"),
            ["_DetailMask"] = (ATORole.Mask, 1, false, "detail mask (UV1)"),
            ["_ParallaxMap"] = (ATORole.Mask, 0, false, "height (data)"),
        };

        private static readonly Dictionary<string, (ATORole role, int uv, bool hasUvMode, string note)>
            URPLitProps = new Dictionary<string, (ATORole, int, bool, string)>
        {
            ["_BaseMap"] = (ATORole.Main, 0, false, "albedo"),
            ["_BumpMap"] = (ATORole.Normal, 0, false, "normal"),
            ["_MetallicGlossMap"] = (ATORole.Mask, 0, false, "metallic/gloss"),
            ["_OcclusionMap"] = (ATORole.Mask, 0, false, "occlusion"),
            ["_EmissionMap"] = (ATORole.Color, 0, false, "emission"),
            ["_ParallaxMap"] = (ATORole.Mask, 0, false, "height (data)"),
        };

        private static readonly Dictionary<string, (ATORole role, int uv, bool hasUvMode, string note)>
            UTSProps = new Dictionary<string, (ATORole, int, bool, string)>
        {
            ["_MainTex"] = (ATORole.Main, 0, false, "albedo"),
            ["_BaseMap"] = (ATORole.Main, 0, false, "albedo"),
            ["_BaseColorMap"] = (ATORole.Main, 0, false, "albedo"),
            ["_BumpMap"] = (ATORole.Normal, 0, false, "normal"),
            ["_NormalMap"] = (ATORole.Normal, 0, false, "normal"),
            ["_MetallicGlossMap"] = (ATORole.Mask, 0, false, "metallic/gloss"),
            ["_OcclusionMap"] = (ATORole.Mask, 0, false, "occlusion"),
            ["_EmissionMap"] = (ATORole.Color, 0, false, "emission"),
            ["_ClippingMask"] = (ATORole.Mask, 0, false, "clip mask"),
            ["_ParallaxMap"] = (ATORole.Mask, 0, false, "height (data)"),
        };

        private static readonly Dictionary<string, (ATORole role, int uv, bool hasUvMode, string note)>
            PoiyomiProps = new Dictionary<string, (ATORole, int, bool, string)>
        {
            ["_MainTex"] = (ATORole.Main, 0, false, "albedo"),
            ["_BumpMap"] = (ATORole.Normal, 0, false, "normal"),
            ["_ClippingMask"] = (ATORole.Mask, 0, false, "clip mask"),
            ["_EmissionMap"] = (ATORole.Color, 0, false, "emission"),
        };

        // ---- lilToon 属性名 → 角色（依据 lilToon 2.3.4 源码 Properties 块人工核对的表，
        //      未列出的 lilToon 属性在源码解析中按命名规则推断）/ lilToon prop → role (verified against
        //      lilToon 2.3.4 Properties; other props are inferred from naming rules during source parsing ----
        private static readonly Dictionary<string, ATORole> LilToonPropRoles = new Dictionary<string, ATORole>
        {
            ["_MainTex"] = ATORole.Main,
            ["_BaseMap"] = ATORole.Main,
            ["_BaseColorMap"] = ATORole.Main,
            ["_Main2ndTex"] = ATORole.Main,
            ["_Main3rdTex"] = ATORole.Main,
            ["_BumpMap"] = ATORole.Normal,
            ["_Bump2ndMap"] = ATORole.Normal,
            ["_Bump3rdMap"] = ATORole.Normal,
            ["_MatCapBumpMap"] = ATORole.Normal,
            ["_MatCap2ndBumpMap"] = ATORole.Normal,
            ["_AnisotropyTangentMap"] = ATORole.Normal,
            ["_MainColorAdjustMask"] = ATORole.Mask,
            ["_Main2ndBlendMask"] = ATORole.Mask,
            ["_Main3rdBlendMask"] = ATORole.Mask,
            ["_MatCapBlendMask"] = ATORole.Mask,
            ["_MatCap2ndBlendMask"] = ATORole.Mask,
            ["_EmissionBlendMask"] = ATORole.Mask,
            ["_Emission2ndBlendMask"] = ATORole.Mask,
            ["_RimColorTex"] = ATORole.Color,
            ["_EmissionMap"] = ATORole.Color,
            ["_Emission2ndMap"] = ATORole.Color,
            ["_MatCapTex"] = ATORole.Color,
            ["_MatCap2ndTex"] = ATORole.Color,
            ["_ShadowColorTex"] = ATORole.Color,
            ["_Shadow2ndColorTex"] = ATORole.Color,
            ["_Shadow3rdColorTex"] = ATORole.Color,
            ["_BacklightColorTex"] = ATORole.Color,
            ["_ReflectionColorTex"] = ATORole.Color,
            ["_GlitterColorTex"] = ATORole.Color,
        };

        /// <summary>分析一个材质，返回其全部贴图用途（无法安全处理的用途标记 whitelisted）。/ Analyze a material; unsafe usages are marked whitelisted.</summary>
        public static List<ATOTextureUsage> Analyze(Material material, Dictionary<string, HashSet<Texture2D>> animatedTextures)
        {
            var result = new List<ATOTextureUsage>();
            if (material == null || material.shader == null) return result;

            var info = GetShaderInfo(material.shader);
            if (!info.supported)
            {
                // 不支持的着色器：全部贴图用途白名单化 + 一次性警告 / unsupported shader: whitelist all + one-time warning
                if (WarnedShaders.Add(material.shader.name))
                    ATOLog.Warning($"Unsupported shader '{material.shader.name}': all its textures are treated as whitelisted and skipped. (不支持的着色器，其全部贴图按白名单跳过优化)");
                var propNames = new List<string>();
                var so = new SerializedObject(material);
                var texProps = so.FindProperty("m_SavedProperties.m_TexEnvs");
                if (texProps != null && texProps.isArray)
                    for (int i = 0; i < texProps.arraySize; i++)
                    {
                        var p = texProps.GetArrayElementAtIndex(i);
                        var nameP = p.FindPropertyRelative("first.name");
                        if (nameP != null) propNames.Add(nameP.stringValue);
                    }
                so.Dispose();
                foreach (var pn in propNames)
                {
                    var t = material.GetTexture(pn) as Texture2D;
                    if (t == null) continue;
                    result.Add(new ATOTextureUsage
                    {
                        texture = t, role = ATORole.Main, uvChannel = 0,
                        material = material, propertyName = pn,
                        whitelisted = true, whitelistReason = "Unsupported shader: " + material.shader.name,
                        shaderName = material.shader.name
                    });
                }
                return result;
            }

            foreach (var kv in info.props)
            {
                if (!kv.Value.isTexture || !kv.Value.recognized) continue;
                var tex = material.GetTexture(kv.Key) as Texture2D;
                if (tex == null) continue;

                var usage = BuildUsage(material, kv.Value, tex);
                // 动画切换的贴图：同一属性多张贴图 / animated texture swaps: multiple textures per property
                if (animatedTextures != null && animatedTextures.TryGetValue(kv.Key, out var set))
                {
                    foreach (var t in set)
                    {
                        if (t == null || t == tex) continue;
                        result.Add(BuildUsage(material, kv.Value, t));
                    }
                }
                result.Add(usage);
            }
            return result;
        }

        private static ATOTextureUsage BuildUsage(Material material, ATOPropInfo prop, Texture2D tex)
        {
            var usage = new ATOTextureUsage
            {
                texture = tex,
                role = prop.role,
                uvChannel = prop.defaultUvChannel,
                material = material,
                propertyName = prop.name,
                shaderName = material.shader.name,
            };

            // UVMode 枚举（lilToon）：非 UV0~UV3 的模式（MatCap/Rim）不走网格 UV → 白名单 / non-mesh-UV modes → whitelist
            if (prop.hasUvMode)
            {
                var modeProp = prop.name + "_UVMode";
                if (material.HasProperty(modeProp))
                {
                    var mode = material.GetInt(modeProp);
                    if (mode >= 0 && mode <= 3) usage.uvChannel = mode;
                    else
                    {
                        usage.whitelisted = true;
                        usage.whitelistReason = $"UVMode of {prop.name} is {mode} (not mesh UV)";
                    }
                }
            }

            // ST 检查：非单位 ST → 白名单 / non-identity ST → whitelist
            if (!string.IsNullOrEmpty(prop.stProp) && material.HasProperty(prop.stProp))
            {
                var st = material.GetVector(prop.stProp);
                usage.st = st;
                if (Mathf.Abs(st.x - 1f) > 1e-5f || Mathf.Abs(st.y - 1f) > 1e-5f ||
                    Mathf.Abs(st.z) > 1e-5f || Mathf.Abs(st.w) > 1e-5f)
                {
                    usage.whitelisted = true;
                    usage.whitelistReason = $"{prop.stProp} is not identity ({st})";
                }
            }

            // 透明度模式与 Cutoff / alpha mode & cutoff
            var (alphaUsage, cutoff) = ResolveAlphaMode(material);
            usage.alphaUsage = alphaUsage;
            usage.cutoffSamples = new[] { cutoff };

            return usage;
        }

        /// <summary>解析材质当前透明度模式与 Cutoff（含关键字动画取最严的组合分析在动画分析阶段补充）。/ Resolve alpha mode & cutoff.</summary>
        private static (ATOAlphaUsage, float) ResolveAlphaMode(Material material)
        {
            var shaderName = material.shader.name.ToLowerInvariant();
            var cutoff = material.HasProperty("_Cutoff") ? material.GetFloat("_Cutoff")
                : material.HasProperty("_AlphaCutoff") ? material.GetFloat("_AlphaCutoff") : 0.5f;

            bool cutout = false, blend = false;

            // 关键字检测 / keyword detection
            var keywords = material.shaderKeywords;
            for (int i = 0; i < keywords.Length; i++)
            {
                var k = keywords[i].ToUpperInvariant();
                if (k.Contains("_ALPHATEST_ON") || k.Contains("_CUTOFF") || k.Contains("_ALPHACLIP") || k.Contains("_CUTOUT"))
                    cutout = true;
                if (k.Contains("_ALPHABLEND_ON") || k.Contains("_ALPHAPREMULTIPLY_ON") ||
                    k.Contains("_SURFACE_TYPE_TRANSPARENT") || k.Contains("_TRANSPARENT"))
                    blend = true;
            }

            // lilToon：渲染模式由着色器文件名区分 / lilToon: render mode from shader file name
            if (shaderName.Contains("liltoon") || shaderName.StartsWith("lts_") || shaderName.Contains("/lts"))
            {
                if (shaderName.Contains("cutout")) cutout = true;
                else if (shaderName.Contains("trans") || shaderName.Contains("fake")) blend = true;
            }

            // 数值模式（Standard _Mode / URP _Surface）/ numeric mode (Standard _Mode / URP _Surface)
            if (material.HasProperty("_Mode"))
            {
                var mode = Mathf.RoundToInt(material.GetFloat("_Mode"));
                if (mode >= 1 && mode <= 2) cutout = true;
                if (mode >= 3) blend = true;
            }
            if (material.HasProperty("_Surface"))
            {
                if (Mathf.Abs(material.GetFloat("_Surface") - 1f) < 0.01f) blend = true;
            }

            if (blend) return (ATOAlphaUsage.Blend, cutoff);
            if (cutout) return (ATOAlphaUsage.Cutout, cutoff);
            return (ATOAlphaUsage.Opaque, cutoff);
        }

        /// <summary>获取（并缓存）着色器解析信息。/ Get (and cache) parsed shader info.</summary>
        private static ATOShaderInfo GetShaderInfo(Shader shader)
        {
            if (ShaderCache.TryGetValue(shader, out var cached)) return cached;

            var info = new ATOShaderInfo { name = shader.name };
            var name = shader.name.ToLowerInvariant();

            // lilToon：解析源码属性表 / lilToon: parse source Properties
            if (name.Contains("liltoon") || name.StartsWith("lts") || name.Contains("/liltoon"))
            {
                ParseLilToon(shader, info);
                info.supported = info.props.Count > 0;
            }
            else if (name.Contains("standard"))
            {
                info.supported = true;
                FillProps(info, StandardProps, keywords: true);
                info.nameBasedAlpha = false;
            }
            else if (name.Contains("urp") && (name.Contains("lit") || name.Contains("simple lit")))
            {
                info.supported = true;
                FillProps(info, URPLitProps, keywords: true);
            }
            else if (name.Contains("utc") || name.Contains("unitychan") || name.Contains("uts"))
            {
                info.supported = true;
                FillProps(info, UTSProps, keywords: true);
            }
            else if (name.Contains("poiyomi"))
            {
                info.supported = true;
                FillProps(info, PoiyomiProps, keywords: true);
            }
            else
            {
                // 未知着色器：尝试通用关键字兼容（_MainTex/_BumpMap 等标准属性名）/
                // unknown shader: try generic keyword compatibility with standard property names
                info.supported = true;
                FillProps(info, StandardProps, keywords: true);
                if (WarnedShaders.Add(shader.name))
                    ATOLog.Warning($"Shader '{shader.name}' is not in the known shader table; using generic keyword-based analysis. Unrecognized textures will be whitelisted. (着色器不在已知表中，使用通用关键字分析，未识别贴图按白名单处理)");
            }

            // 解析关键字集合 / parse keyword set from shader source
            ParseKeywords(shader, info);

            ShaderCache[shader] = info;
            return info;
        }

        private static void FillProps(ATOShaderInfo info,
            Dictionary<string, (ATORole role, int uv, bool hasUvMode, string note)> table, bool keywords)
        {
            info.props = new Dictionary<string, ATOPropInfo>();
            info.keywords = new HashSet<string>();
            foreach (var kv in table)
            {
                info.props[kv.Key] = new ATOPropInfo
                {
                    name = kv.Key, isTexture = true, role = kv.Value.role,
                    defaultUvChannel = kv.Value.uv, hasUvMode = false,
                    stProp = kv.Key + "_ST", recognized = true, note = kv.Value.note,
                };
            }
        }

        private static readonly Regex PropRegex = new Regex(
            @"^\s*(?:\[[^\]]*\]\s*)*_([A-Za-z0-9_]+)\s*\(\s*""[^""]*""\s*,\s*(2D|Color|Vector|Float|Range\([^)]*\)|Int|Cube)\s*\)",
            RegexOptions.Compiled);

        /// <summary>解析 lilToon 着色器源码的属性表（兼容未来版本）。/ Parse lilToon's Properties block from source (future-version friendly).</summary>
        private static void ParseLilToon(Shader shader, ATOShaderInfo info)
        {
            info.props = new Dictionary<string, ATOPropInfo>();
            info.keywords = new HashSet<string>();
            info.nameBasedAlpha = true;
            info.cutoffProp = "_Cutoff";

            var path = AssetDatabase.GetAssetPath(shader);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            string source;
            try { source = File.ReadAllText(path); }
            catch (Exception) { return; }

            var propStart = source.IndexOf("Properties", StringComparison.Ordinal);
            if (propStart < 0) return;
            var brace = source.IndexOf('{', propStart);
            if (brace < 0) return;
            // 找到 Properties 块的匹配大括号 / find matching close brace of the Properties block
            int depth = 0, close = -1;
            for (int i = brace; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0) { close = i; break; }
                }
            }
            if (close < 0) return;
            var block = source.Substring(brace, close - brace);

            foreach (Match m in PropRegex.Matches(block))
            {
                var propName = "_" + m.Groups[1].Value;
                var type = m.Groups[2].Value;
                var isTex = type == "2D";
                if (!isTex)
                {
                    // 检查是否有对应 UVMode 枚举 / check for a matching UVMode enum
                    if (propName.EndsWith("_UVMode", StringComparison.Ordinal))
                    {
                        var baseName = propName.Substring(0, propName.Length - "_UVMode".Length);
                        if (info.props.TryGetValue(baseName, out var pi)) pi.hasUvMode = true;
                    }
                    continue;
                }
                // 推断角色：已知表优先，否则按命名规则 / infer role: known table first, then naming rules
                var role = InferRole(propName, out var recognized);
                info.props[propName] = new ATOPropInfo
                {
                    name = propName, isTexture = true, role = role, defaultUvChannel = 0,
                    hasUvMode = false, stProp = propName + "_ST", recognized = recognized,
                    note = recognized ? "" : "unrecognized lilToon texture"
                };
            }
        }

        /// <summary>按命名规则推断 lilToon 贴图角色。/ Infer lilToon texture role by naming rules.</summary>
        private static ATORole InferRole(string propName, out bool recognized)
        {
            if (LilToonPropRoles.TryGetValue(propName, out var r))
            {
                recognized = true;
                return r;
            }
            var lower = propName.ToLowerInvariant();
            // 法线 / normal maps
            if (lower.Contains("bump") || lower.Contains("normalmap") || lower.Contains("tangentmap"))
            {
                recognized = true;
                return ATORole.Normal;
            }
            // 蒙版/数据 / masks & data
            if (lower.Contains("mask") || lower.Contains("gradation") || lower.Contains("ramp") ||
                lower.Contains("metallic") || lower.Contains("smoothness") || lower.Contains("anisotropy") ||
                lower.Contains("dissolve") || lower.Contains("dither") || lower.Contains("audiolink") ||
                lower.Contains("height") || lower.Contains("parallax") || lower.Contains("outline") ||
                lower.Contains("glitter") || lower.Contains("strength") || lower.Contains("blur") ||
                lower.Contains("alpha"))
            {
                recognized = true;
                return ATORole.Mask;
            }
            // 颜色 / color textures
            if (lower.Contains("tex") || lower.Contains("map") || lower.Contains("color"))
            {
                recognized = true;
                return ATORole.Color;
            }
            recognized = false;
            return ATORole.Mask;
        }

        /// <summary>解析着色器关键字（shader_feature / multi_compile）。/ Parse shader keywords from source.</summary>
        private static void ParseKeywords(Shader shader, ATOShaderInfo info)
        {
            var path = AssetDatabase.GetAssetPath(shader);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            string source;
            try { source = File.ReadAllText(path); }
            catch (Exception) { return; }

            foreach (Match m in Regex.Matches(source, @"#pragma\s+(?:shader_feature|multi_compile)\s+([\w_ ]+)"))
            {
                var parts = m.Groups[1].Value.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var p in parts) info.keywords.Add(p.TrimEnd('_'));
            }
        }

        /// <summary>判断某材质是否可能处于指定透明度模式（考虑关键字可被动画修改 → 由调用方传入可能的关键字集合）。/ Whether a material may be in the given alpha mode.</summary>
        public static bool MaterialMayUseAlphaMode(Material material, ATOShaderInfo info, ATOAlphaUsage mode,
            HashSet<string> possibleKeywords)
        {
            // 保守：无法确定时按"可能"处理 / conservative: assume possible when uncertain
            if (info.nameBasedAlpha)
            {
                var name = material.shader.name.ToLowerInvariant();
                if (mode == ATOAlphaUsage.Cutout) return name.Contains("cutout");
                if (mode == ATOAlphaUsage.Blend) return name.Contains("trans") || name.Contains("fake");
                return !name.Contains("cutout") && !name.Contains("trans") && !name.Contains("fake");
            }
            var kw = new HashSet<string>(material.shaderKeywords);
            if (possibleKeywords != null) kw.UnionWith(possibleKeywords);
            foreach (var k in kw)
            {
                var u = k.ToUpperInvariant();
                bool cut = u.Contains("_ALPHATEST_ON") || u.Contains("_CUTOFF") || u.Contains("_ALPHACLIP");
                bool blend = u.Contains("_ALPHABLEND_ON") || u.Contains("_ALPHAPREMULTIPLY_ON") || u.Contains("_SURFACE_TYPE_TRANSPARENT");
                if (mode == ATOAlphaUsage.Cutout && cut) return true;
                if (mode == ATOAlphaUsage.Blend && blend) return true;
                if (mode == ATOAlphaUsage.Opaque && !cut && !blend) return true;
            }
            return true; // 无法确定 → 按可能（最严苛）处理 / uncertain → assume possible (strictest)
        }
    }
}
