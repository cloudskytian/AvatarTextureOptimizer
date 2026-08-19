// ============================================================================
// AvatarTextureOptimizer (net.fosa.avatar-texture-optimizer)
// Analysis/ShaderAnalyzer.cs — 着色器贴图属性分析 / Shader texture property analysis
//
// 需求: 自动分析 liltoon 和其他使用标准关键字的着色器的属性表和关键字，尽可能兼容未来版本；
//       无法兼容的视作白名单跳过优化并报 warning。
// 实现 (Coder1/Coder2 共识):
//  1) liltoon 内置属性表（来自 AAO ShaderInformation.Liltoon + liltoon 源码实测数据）。
//  2) 标准关键字启发式（Standard/URP 等通用命名规则）。
//  3) ShaderUtil 枚举 shader 全部贴图属性，避免遗漏。
//  4) 未匹配已知模式的属性 → 白名单 + warning（保守安全）。
//  5) 渲染模式(不透明/Cutout/Blend)检测: 关键字 + renderQueue 兜底。
// ============================================================================
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using api = net.fosa.avatar_texture_optimizer.editor.api;
using Object = UnityEngine.Object;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>透明模式（质量评估用） / Alpha/transparency mode (for quality evaluation)</summary>
    public enum AlphaMode
    {
        Opaque,   // 不透明 / Opaque
        Cutout,   // 裁剪 / Alpha test (uses _Cutoff)
        Blend,    // 混合 / Alpha blend
    }

    /// <summary>
    /// 着色器属性分析结果 / Result of analyzing one texture property.
    /// </summary>
    public struct AnalyzedTexture
    {
        public string property;
        public Texture2D texture;
        public TextureRole role;
        public int uvChannel;
        /// <summary>非网格UV(MatCap/Dither等) / Non-mesh UV sampling</summary>
        public bool nonMesh;
        /// <summary>贴花或特殊采样 / Decal or special sampling</summary>
        public bool decalOrSpecial;
        /// <summary>无法兼容(未知属性等) / Not compatible</summary>
        public bool incompatible;
        /// <summary>由关键字/开关禁用(未使用) / Disabled by keyword/switch</summary>
        public bool disabled;
    }

    /// <summary>
    /// 材质级分析结果 / Material-level analysis result.
    /// </summary>
    public sealed class MaterialAnalysis
    {
        public List<AnalyzedTexture> textures = new List<AnalyzedTexture>();
        public AlphaMode alphaMode = AlphaMode.Opaque;
        public float cutoff = 0.5f;
    }

    /// <summary>
    /// 着色器贴图属性分析器 / Shader texture property analyzer.
    /// </summary>
    public static class ShaderAnalyzer
    {
        // ---- liltoon 内置属性表 (来源: AAO ShaderInformation.Liltoon + liltoon 2.3.4 源码) ----
        // 值: (角色, 特殊标记, UV模式属性)
        // (role, special: 0=常规, 1=NonMesh, 2=Decal/POM, 3=UVMode可切换, uvModeProp)
        private static readonly Dictionary<string, (TextureRole role, int special, string uvModeProp)> LiltoonTable =
            new Dictionary<string, (TextureRole, int, string)>(StringComparer.OrdinalIgnoreCase)
        {
            { "_MainTex",            (TextureRole.MainColor, 0, null) },
            { "_BaseMap",            (TextureRole.MainColor, 0, null) },  // liltoon dummy (共用 _MainTex 采样)
            { "_BaseColorMap",       (TextureRole.MainColor, 0, null) },  // liltoon dummy
            { "_MainColorAdjustMask",(TextureRole.Mask,      0, null) },
            { "_Main2ndTex",         (TextureRole.MainColor, 3, "_Main2ndTex_UVMode") },
            { "_Main3rdTex",         (TextureRole.MainColor, 3, "_Main3rdTex_UVMode") },
            { "_Main2ndBlendMask",   (TextureRole.Mask,      0, null) },
            { "_Main3rdBlendMask",   (TextureRole.Mask,      0, null) },
            { "_Main2ndDissolveMask",   (TextureRole.Mask,   0, null) },
            { "_Main2ndDissolveNoiseMask", (TextureRole.Mask, 0, null) },
            { "_Main3rdDissolveMask",   (TextureRole.Mask,   0, null) },
            { "_Main3rdDissolveNoiseMask", (TextureRole.Mask, 0, null) },
            { "_BumpMap",            (TextureRole.Normal,    0, null) },
            { "_NormalMap",          (TextureRole.Normal,    0, null) },
            { "_Bump2ndMap",         (TextureRole.Normal,    3, "_Bump2ndMap_UVMode") },
            { "_Bump2ndScaleMask",   (TextureRole.Mask,      0, null) },
            { "_MatCapBumpMap",      (TextureRole.Normal,    0, null) },
            { "_MatCap2ndBumpMap",   (TextureRole.Normal,    0, null) },
            { "_MatCapTex",          (TextureRole.MainColor, 1, null) }, // NonMesh (基于法线/屏幕)
            { "_MatCap2ndTex",       (TextureRole.MainColor, 1, null) },
            { "_MatCapBlendMask",    (TextureRole.Mask,      0, null) },
            { "_MatCap2ndBlendMask", (TextureRole.Mask,      0, null) },
            { "_EmissionMap",        (TextureRole.Emission,  3, "_EmissionMap_UVMode") },
            { "_Emission2ndMap",     (TextureRole.Emission,  3, "_Emission2ndMap_UVMode") },
            { "_EmissionBlendMask",  (TextureRole.Mask,      0, null) },
            { "_Emission2ndBlendMask",(TextureRole.Mask,     0, null) },
            { "_EmissionGradTex",    (TextureRole.Mask,      0, null) },
            { "_Emission2ndGradTex", (TextureRole.Mask,      0, null) },
            { "_ShadingGradeTex",    (TextureRole.Mask,      0, null) },
            { "_ShadowColorTex",     (TextureRole.Mask,      0, null) },
            { "_Shadow2ndColorTex",  (TextureRole.Mask,      0, null) },
            { "_Shadow3rdColorTex",  (TextureRole.Mask,      0, null) },
            { "_ShadowStrengthMask", (TextureRole.Mask,      0, null) },
            { "_ShadowBorderMask",   (TextureRole.Mask,      0, null) },
            { "_ShadowBlurMask",     (TextureRole.Mask,      0, null) },
            { "_RimShadeMask",       (TextureRole.Mask,      0, null) },
            { "_RimColorTex",        (TextureRole.Mask,      0, null) },
            { "_ReflectionColorTex", (TextureRole.Mask,      0, null) },
            { "_BacklightColorTex",  (TextureRole.Mask,      0, null) },
            { "_OutlineTex",         (TextureRole.MainColor, 0, null) },
            { "_OutlineWidthMask",   (TextureRole.Mask,      0, null) },
            { "_OutlineVectorTex",   (TextureRole.Mask,      0, null) },
            { "_FurVectorTex",       (TextureRole.Mask,      0, null) },
            { "_FurLengthMask",      (TextureRole.Mask,      0, null) },
            { "_FurMask",            (TextureRole.Mask,      0, null) },
            { "_FurNoiseMask",       (TextureRole.Mask,      0, null) },
            { "_MetallicGlossMap",   (TextureRole.Mask,      0, null) },
            { "_SmoothnessTex",      (TextureRole.Mask,      0, null) },
            { "_AnisotropyTangentMap",(TextureRole.Mask,     0, null) },
            { "_AnisotropyScaleMask",(TextureRole.Mask,      0, null) },
            { "_AnisotropyShiftNoiseMask",(TextureRole.Mask, 0, null) },
            { "_DissolveMask",       (TextureRole.Mask,      0, null) },
            { "_DissolveNoiseMask",  (TextureRole.Mask,      0, null) },
            { "_GlitterColorTex",    (TextureRole.Mask,      0, null) },
            { "_GlitterShapeTex",    (TextureRole.Mask,      0, null) },
            { "_AlphaMask",          (TextureRole.Mask,      0, null) },
            { "_AudioLinkMask",      (TextureRole.Mask,      0, null) },
            { "_AudioLinkLocalMap",  (TextureRole.Mask,      0, null) },
            { "_DitherTex",          (TextureRole.Mask,      1, null) }, // 屏幕空间
            { "_MainGradationTex",   (TextureRole.Mask,      1, null) }, // 基于颜色, 非网格UV
            { "_ParallaxMap",        (TextureRole.Mask,      2, null) }, // POM 特殊采样
            { "_TriMask",            (TextureRole.Mask,      2, null) }, // 特殊用途
        };

        // ---- liltoon 渲染模式关键字 ----
        private static readonly string[] LiltoonCutoutKeywords = { "LIL_RENDER_CUTOUT", "LIL_RENDER_FURCUTOUT" };
        private static readonly string[] LiltoonBlendKeywords = { "LIL_RENDER_TRANSPARENT", "LIL_RENDER_FUR", "LIL_RENDER_FURONLY", "LIL_RENDER_TWOTRANS" };

        // ---- 标准关键字 ----
        private static readonly string[] BuiltinCutoutKeywords = { "_ALPHATEST_ON" };
        private static readonly string[] BuiltinBlendKeywords = { "_ALPHABLEND_ON", "_ALPHAPREMULTIPLY_ON" };

        /// <summary>
        /// 分析一个材质的所有贴图属性 / Analyze all texture properties of a material.
        /// </summary>
        public static MaterialAnalysis AnalyzeMaterial(Material material, LogContext ctx)
        {
            var result = new MaterialAnalysis();
            if (material == null || material.shader == null) return result;

            // 渲染模式 / Render mode
            DetectAlphaMode(material, result);

            // 枚举 shader 上全部贴图属性 / Enumerate all texture properties of the shader
            foreach (var propName in GetTexturePropertyNames(material))
            {
                var tex = material.GetTexture(propName) as Texture2D;
                if (tex == null) continue; // 未赋值 → 跳过（动画切换的贴图由动画分析补充）

                var at = Classify(material, propName, tex, ctx);
                if (at.disabled) continue; // 关键字/开关未启用 → 不使用
                result.textures.Add(at);
            }

            return result;
        }

        /// <summary>
        /// 对单个贴图属性分类 / Classify a single texture property.
        /// </summary>
        public static AnalyzedTexture Classify(Material material, string property, Texture2D tex, LogContext ctx)
        {
            var a = new AnalyzedTexture { property = property, texture = tex };

            if (LiltoonTable.TryGetValue(property, out var entry))
            {
                a.role = entry.role;
                a.uvChannel = 0;
                if (entry.special == 1)
                {
                    a.nonMesh = true;   // MatCap/Dither/Gradation: 非网格UV
                }
                else if (entry.special == 2)
                {
                    a.decalOrSpecial = true; // POM/TriMask: 特殊采样
                }
                else if (entry.special == 3 && !string.IsNullOrEmpty(entry.uvModeProp))
                {
                    // UV 模式可切换: 0..3=网格UV, 4=NonMesh, 其他=多通道→不兼容
                    int mode = material.HasProperty(entry.uvModeProp) ? material.GetInt(entry.uvModeProp) : 0;
                    switch (mode)
                    {
                        case 0: a.uvChannel = 0; break;
                        case 1: a.uvChannel = 1; break;
                        case 2: a.uvChannel = 2; break;
                        case 3: a.uvChannel = 3; break;
                        case 4: a.nonMesh = true; break;
                        default: a.incompatible = true; break; // 多通道 → 白名单
                    }
                }
            }
            else
            {
                ClassifyGeneric(property, ref a);
                // 第三方分类器扩展（内置识别失败时）/ third-party classifier extension
                if (a.incompatible)
                {
                    foreach (var classifier in api.ATOPublicAPI.Classifiers)
                    {
                        var role = classifier.Classify(material, property);
                        if (role.HasValue)
                        {
                            a.role = role.Value;
                            a.incompatible = false;
                            break;
                        }
                    }
                }
            }

            // liltoon 开关: 未启用的特性对应的贴图不使用 / liltoon feature switches
            if (material.shader.name.Contains("lilToon", StringComparison.OrdinalIgnoreCase))
            {
                if (property == "_Main2ndTex" && material.GetInt("_UseMain2ndTex") == 0) a.disabled = true;
                if (property == "_Main3rdTex" && material.GetInt("_UseMain3rdTex") == 0) a.disabled = true;
                if (property == "_BumpMap" && material.GetInt("_UseBumpMap") == 0) a.disabled = true;
                if (property == "_Bump2ndMap" && material.GetInt("_UseBump2ndMap") == 0) a.disabled = true;
                if (property == "_EmissionMap" && material.GetInt("_UseEmission") == 0) a.disabled = true;
                if (property == "_Emission2ndMap" && material.GetInt("_UseEmission2nd") == 0) a.disabled = true;
                if ((property == "_MatCapTex" || property == "_MatCap2ndTex") && material.GetInt("_UseMatCap") == 0 && material.GetInt("_UseMatCap2nd") == 0) a.disabled = true;
            }
            else
            {
                // 标准关键字: 未启用 → 不使用 / Standard keywords
                if (property == "_EmissionMap" && !material.IsKeywordEnabled("_EMISSION")) a.disabled = true;
                if ((property == "_BumpMap" || property == "_DetailNormalMap") && !material.IsKeywordEnabled("_NORMALMAP")) a.disabled = true;
                if (property == "_MetallicGlossMap" && !material.IsKeywordEnabled("_METALLICGLOSSMAP")) a.disabled = true;
                if (property == "_ParallaxMap" && !material.IsKeywordEnabled("_PARALLAXMAP")) a.disabled = true;
                if (property == "_OcclusionMap" && !material.IsKeywordEnabled("_OCCLUSIONMAP")) a.disabled = true;
                if (property == "_DetailAlbedoMap" && !material.IsKeywordEnabled("_DETAIL_MULX2")) a.disabled = true;
            }

            // ST 变换检测（材质静态）/ ST transform detection (static)
            var stProp = property + "_ST";
            if (material.HasProperty(stProp))
            {
                var st = material.GetVector(stProp);
                const float eps = 1e-4f;
                if (Mathf.Abs(st.x - 1f) > eps || Mathf.Abs(st.y - 1f) > eps || Mathf.Abs(st.z) > eps || Mathf.Abs(st.w) > eps)
                {
                    a.incompatible = true; // 有 ST 平移/缩放 → 白名单（动画中的 ST 由动画分析补充）
                }
            }

            return a;
        }

        /// <summary>
        /// 标准关键字启发式分类 / Generic keyword-based classification.
        /// </summary>
        private static void ClassifyGeneric(string property, ref AnalyzedTexture a)
        {
            var p = property.ToLowerInvariant();
            a.uvChannel = 0;

            // 明确的特殊用途名称 → 不兼容 / Known special-purpose names → incompatible
            if (property == "_DitherTex" || property == "_MainGradationTex" || property == "_TriMask" ||
                property.StartsWith("_Decal", StringComparison.OrdinalIgnoreCase) ||
                property.StartsWith("_Projector", StringComparison.OrdinalIgnoreCase) ||
                property.StartsWith("_MatCap", StringComparison.OrdinalIgnoreCase) ||
                p.Contains("gradation") || p.Contains("dither"))
            {
                a.decalOrSpecial = true;
                a.role = TextureRole.Other;
                return;
            }

            if (p.Contains("bump") || p.Contains("normalmap") || p == "_normalmap" || p.Contains("normal"))
            {
                a.role = TextureRole.Normal;
            }
            else if (p.Contains("emission") || p.Contains("emissive"))
            {
                a.role = TextureRole.Emission;
            }
            else if (p.Contains("metallic") || p.Contains("smoothness") || p.Contains("roughness") ||
                     p.Contains("occlusion") || p.Contains("ao") || p.Contains("height") || p.Contains("parallax") ||
                     p.Contains("mask") || p.Contains("detail"))
            {
                a.role = TextureRole.Mask;
                if (p.Contains("parallax") || p.Contains("height")) a.decalOrSpecial = true;
            }
            else if (p.Contains("maintex") || p.Contains("basemap") || p.Contains("basecolor") ||
                     p.Contains("albedo") || p.Contains("diffuse") || p.Contains("colormap") || p.Contains("color_tex"))
            {
                a.role = TextureRole.MainColor;
            }
            else
            {
                // 无法识别的属性 → 不兼容（白名单 + warning） / Unrecognized → incompatible
                a.incompatible = true;
                a.role = TextureRole.Other;
            }
        }

        /// <summary>
        /// 渲染模式检测（关键字优先, renderQueue 兜底）/
        /// Alpha mode detection (keywords first, render queue fallback).
        /// </summary>
        private static void DetectAlphaMode(Material material, MaterialAnalysis result)
        {
            for (int i = 0; i < LiltoonCutoutKeywords.Length; i++)
            {
                if (material.IsKeywordEnabled(LiltoonCutoutKeywords[i])) { result.alphaMode = AlphaMode.Cutout; goto done; }
            }
            for (int i = 0; i < LiltoonBlendKeywords.Length; i++)
            {
                if (material.IsKeywordEnabled(LiltoonBlendKeywords[i])) { result.alphaMode = AlphaMode.Blend; goto done; }
            }
            for (int i = 0; i < BuiltinCutoutKeywords.Length; i++)
            {
                if (material.IsKeywordEnabled(BuiltinCutoutKeywords[i])) { result.alphaMode = AlphaMode.Cutout; goto done; }
            }
            for (int i = 0; i < BuiltinBlendKeywords.Length; i++)
            {
                if (material.IsKeywordEnabled(BuiltinBlendKeywords[i])) { result.alphaMode = AlphaMode.Blend; goto done; }
            }

            // 兜底: render queue / Fallback: render queue
            int q = material.renderQueue;
            if (q >= 3000) result.alphaMode = AlphaMode.Blend;
            else if (q >= 2450) result.alphaMode = AlphaMode.Cutout;

        done:
            if (result.alphaMode == AlphaMode.Cutout && material.HasProperty("_Cutoff"))
            {
                result.cutoff = material.GetFloat("_Cutoff");
            }
        }

        /// <summary>
        /// 获取材质所有贴图属性名（ShaderUtil 枚举 + 常见额外）/
        /// All texture property names of a material's shader.
        /// </summary>
        public static List<string> GetTexturePropertyNames(Material material)
        {
            var list = new List<string>();
            var shader = material.shader;
            if (shader != null)
            {
                int count = ShaderUtil.GetPropertyCount(shader);
                for (int i = 0; i < count; i++)
                {
                    if (ShaderUtil.GetPropertyType(shader, i) == ShaderUtil.ShaderPropertyType.TexEnv)
                    {
                        list.Add(ShaderUtil.GetPropertyName(shader, i));
                    }
                }
            }
            return list;
        }

        /// <summary>
        /// 日志上下文（供 analyzer 内统一打 warning） / Logging context for analyzer warnings.
        /// </summary>
        public sealed class LogContext
        {
            public string avatarName;
        }
    }
}
