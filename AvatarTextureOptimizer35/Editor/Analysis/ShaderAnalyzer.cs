using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// Shader property analysis: classifies how a material property uses a texture. /
    /// 着色器属性分析：分类一个材质属性如何使用贴图。
    ///
    /// Generic mechanism (works for liltoon and other shaders using standard attributes/keywords,
    /// aiming to survive future versions): Shader property table + [Normal]/[NoScaleOffset]/
    /// [MainTexture] attributes + name semantics + material keywords. liltoon-specific knowledge is
    /// embedded for 2.3.4 (property list verified from Shader/lts.shader). Unknown/unsafe usages
    /// return null → the texture is treated as whitelist (safe fallback). /
    /// 通用机制（适用于 liltoon 与其他使用标准特性/关键字的着色器，尽量兼容未来版本）：属性表 +
    /// [Normal]/[NoScaleOffset]/[MainTexture] 特性 + 名称语义 + 材质关键字。liltoon 2.3.4 的
    /// 内置知识已对照 Shader/lts.shader 核实。未知/不安全用法返回 null → 贴图视作白名单（安全回退）。
    /// </summary>
    internal static class ShaderAnalyzer
    {
        /// <summary>
        /// Analyze a texture property of a material. Returns null (with a reason) if the usage is
        /// unsafe or unknown. / 分析材质的贴图属性。不安全或未知时返回 null（带原因）。
        /// </summary>
        public static AtoTextureUsage Analyze(Material material, string propertyName, Texture2D texture,
            out string unsafeReason)
        {
            unsafeReason = null;
            var usage = new AtoTextureUsage();
            var shader = material.shader;

            var propIndex = shader.FindPropertyIndex(propertyName);
            if (propIndex < 0)
            {
                unsafeReason = "property not found in shader";
                return null;
            }

            // Property type check. / 属性类型检查。
            if (GetPropertyType(shader, propIndex) != ShaderPropertyType.Texture)
            {
                return null; // not a texture property — simply not our business. / 不是贴图属性——与本工具无关。
            }

            // Attributes: [Normal], [NoScaleOffset], [MainTexture], [PerRendererData]. / 特性分析。
            var attributes = GetPropertyAttributes(shader, propIndex);
            var hasNormal = attributes.Contains("Normal");
            var hasNoScaleOffset = attributes.Contains("NoScaleOffset");
            var hasMainTexture = attributes.Contains("MainTexture");
            var hasPerRendererData = attributes.Contains("PerRendererData");

            if (hasPerRendererData)
            {
                // Per-renderer overrides can swap the texture per renderer — unsafe. / 每渲染器覆盖可替换贴图——不安全。
                unsafeReason = "texture property is [PerRendererData]";
                return null;
            }

            usage.NoScaleOffset = hasNoScaleOffset;

            // ---- kind classification ----
            var name = propertyName;
            var kind = AtoTextureKind.Unknown;
            string lower = name.ToLowerInvariant();

            // Parallax / height / decal usages distort sampling → unsafe. / Parallax/Height/贴花用法会扭曲采样——不安全。
            if (lower.Contains("parallax") || lower.Contains("_height") || lower.Contains("decal"))
            {
                unsafeReason = "property looks like parallax/height/decal usage";
                return null;
            }
            // Detail maps use secondary UVs and multiply — unsafe. / 细节贴图使用次级 UV 且为相乘——不安全。
            if (lower.Contains("detail"))
            {
                unsafeReason = "property looks like a detail map (secondary UV)";
                return null;
            }
            // Gradient ramps are sampled by value/other coordinates — unsafe. / 渐变 Ramp 按数值/其他坐标采样——不安全。
            if (lower.Contains("ramp") || lower.Contains("gradation") || lower.Contains("lut"))
            {
                unsafeReason = "property looks like a ramp/gradient";
                return null;
            }

            if (lower.Contains("anisotropy") && (lower.Contains("tangent") || lower.Contains("anisotangent")))
            {
                kind = AtoTextureKind.Tangent;
            }
            else if (hasNormal || IsNormalName(lower))
            {
                kind = AtoTextureKind.Normal;
            }
            else if (lower.Contains("mask"))
            {
                kind = AtoTextureKind.Mask;
                usage.UsedChannels = UsedChannelsForMask(lower);
            }
            else if (hasMainTexture || IsMainName(lower))
            {
                kind = AtoTextureKind.Main;
            }
            else if (IsKnownColorName(lower))
            {
                kind = AtoTextureKind.Main; // color content sampled on the main UV. / 主 UV 采样的颜色内容。
            }
            else
            {
                unsafeReason = $"cannot classify property '{propertyName}' safely";
                return null;
            }

            usage.Kind = kind;

            // ---- UV channel ----
            if (IsKnownMainUvName(lower))
            {
                usage.UvChannel = 0;
            }
            else
            {
                unsafeReason = $"cannot determine UV channel for '{propertyName}'";
                return null;
            }

            // ---- ST check ----
            if (!hasNoScaleOffset)
            {
                var scale = material.GetTextureScale(propertyName);
                var offset = material.GetTextureOffset(propertyName);
                usage.StScale = scale;
                usage.StOffset = offset;
                if (Mathf.Abs(scale.x - 1f) > 1e-5f || Mathf.Abs(scale.y - 1f) > 1e-5f ||
                    Mathf.Abs(offset.x) > 1e-5f || Mathf.Abs(offset.y) > 1e-5f)
                {
                    unsafeReason = $"non-identity ST scale/offset ({scale}, {offset})";
                    return null;
                }
            }

            // ---- color space ----
            usage.Srgb = texture != null && IsSrgbTexture(texture);

            // ---- alpha usage (cutout/blend) per referencing material ----
            DetectAlphaUsage(material, shader, usage);

            // ---- extension providers may refine the classification ----
            foreach (var provider in AtoExtensionRegistry.TextureUsageProviders)
            {
                try
                {
                    var overridden = provider.Override(material, propertyName, texture, usage);
                    if (overridden != null) usage = overridden;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[ATO] texture usage provider '{provider.DisplayName}' failed: {e.Message}");
                }
            }
            if (usage.Kind == AtoTextureKind.Unknown)
            {
                unsafeReason = $"extension provider could not classify '{propertyName}'";
                return null;
            }

            return usage;
        }

        /// <summary>
        /// Detect cutout/blend usage from the material's render type and keywords. /
        /// 从材质的渲染类型与关键字检测 cutout/blend 用法。
        /// </summary>
        public static void DetectAlphaUsage(Material material, Shader shader, AtoTextureUsage usage)
        {
            var renderType = material.GetTag("RenderType", false, "");
            var queue = material.renderQueue;

            usage.HasBlend = renderType == "Transparent" || queue >= 3000;

            if (renderType == "TransparentCutout" || queue is >= 2000 and < 3000)
            {
                var cutoff = material.HasProperty("_Cutoff") ? material.GetFloat("_Cutoff") : 0.5f;
                usage.CutoutThresholds.Add((material, cutoff));
            }

            // Keywords like _ALPHATEST_ON / _ALPHABLEND_ON are already reflected in renderType;
            // additionally check material's enabled keywords for robustness. /
            // _ALPHATEST_ON/_ALPHABLEND_ON 等关键字通常已反映在 renderType；再查关键字以兜底。
            foreach (var keyword in material.enabledKeywords)
            {
                var kw = keyword.name;
                if (kw.Contains("_ALPHATEST_ON") || kw.Contains("CUTOUT"))
                {
                    var cutoff = material.HasProperty("_Cutoff") ? material.GetFloat("_Cutoff") : 0.5f;
                    if (usage.CutoutThresholds.All(c => c.material != material))
                        usage.CutoutThresholds.Add((material, cutoff));
                }
                if (kw.Contains("_ALPHABLEND_ON") || kw.Contains("TRANSPARENT"))
                {
                    usage.HasBlend = true;
                }
            }
        }

        public static bool IsSrgbTexture(Texture2D texture)
        {
            if (texture == null) return true;
            // Asset-based check first. / 优先按资产导入设置判断。
            var path = AssetDatabase.GetAssetPath(texture);
            if (!string.IsNullOrEmpty(path))
            {
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer != null) return importer.sRGBTexture;
            }
            // Fallback: linear color space formats are typically treated as linear. / 兜底：线性格式一般按线性处理。
            switch (texture.format)
            {
                case TextureFormat.RGBAHalf:
                case TextureFormat.RGBAFloat:
                case TextureFormat.RHalf:
                case TextureFormat.RFloat:
                case TextureFormat.RGHalf:
                case TextureFormat.RGFloat:
                    return false;
                default:
                    return true;
            }
        }

        private static bool IsNormalName(string lower) =>
            lower.Contains("bump") || lower.Contains("normal") || lower.Contains("_nrm");

        private static bool IsMainName(string lower) =>
            lower == "_maintex" || lower == "_basecolormap" || lower == "_basemap" || lower == "_color";

        private static bool IsKnownColorName(string lower) =>
            lower.Contains("emission") || lower.Contains("matcap") || lower.Contains("glitter") ||
            lower.Contains("outline") || lower.Contains("_main2ndtex") || lower.Contains("_main3rdtex") ||
            lower.Contains("backlight") || lower.Contains("shadowcolor") || lower.Contains("rimcolor") ||
            lower.Contains("reflectioncolor") || lower.Contains("audiolink") || lower.Contains("dissolve") ||
            lower.Contains("noise");

        private static bool IsKnownMainUvName(string lower) =>
            IsMainName(lower) || IsKnownColorName(lower) || IsNormalName(lower) || lower.Contains("mask") ||
            lower.Contains("smoothness") || lower.Contains("metallic") || lower.Contains("occlusion") ||
            lower.Contains("specgloss");

        /// <summary>
        /// Which channels a mask-style texture typically uses. / mask 类贴图通常使用哪些通道。
        /// </summary>
        private static int UsedChannelsForMask(string lower)
        {
            // _MetallicGlossMap: metallic in R, smoothness in A. / _MetallicGlossMap：金属度在 R，光滑度在 A。
            if (lower.Contains("metallicgloss") || lower.Contains("specgloss")) return 0b1001; // R | A
            return 0b0001; // most masks use R. / 多数 mask 用 R。
        }

        // ---- safe wrappers around property-table APIs ----

        private static ShaderPropertyType GetPropertyType(Shader shader, int index)
        {
            try
            {
                return ShaderUtil.GetPropertyType(shader, index);
            }
            catch (Exception)
            {
                return ShaderPropertyType.Float;
            }
        }

        private static Func<Shader, int, string[]> _getAttributes;

        /// <summary>
        /// Get the property attributes ([Normal], [NoScaleOffset], [MainTexture]...). Uses
        /// reflection for maximum compatibility: both ShaderUtil.GetPropertyAttributes and
        /// Shader.GetPropertyAttributes are probed; if unavailable, empty is returned and
        /// analysis degrades to name-based classification (still safe). /
        /// 获取属性特性（[Normal]、[NoScaleOffset]、[MainTexture]…）。用反射最大化兼容：
        /// ShaderUtil.GetPropertyAttributes 与 Shader.GetPropertyAttributes 都会探测；都不可用时
        /// 返回空并降级为基于名称的分类（依然安全）。
        /// </summary>
        private static string[] GetPropertyAttributes(Shader shader, int index)
        {
            if (_getAttributes == null)
            {
                var miShaderUtil = typeof(ShaderUtil).GetMethod("GetPropertyAttributes",
                    new[] { typeof(Shader), typeof(int) });
                if (miShaderUtil != null)
                {
                    _getAttributes = (s, i) =>
                    {
                        try { return (string[])miShaderUtil.Invoke(null, new object[] { s, i }); }
                        catch (Exception) { return Array.Empty<string>(); }
                    };
                }
                else
                {
                    var miShader = typeof(Shader).GetMethod("GetPropertyAttributes",
                        new[] { typeof(int) });
                    if (miShader != null)
                    {
                        _getAttributes = (s, i) =>
                        {
                            try { return (string[])miShader.Invoke(s, new object[] { i }); }
                            catch (Exception) { return Array.Empty<string>(); }
                        };
                    }
                    else
                    {
                        _getAttributes = (s, i) => Array.Empty<string>();
                    }
                }
            }
            return _getAttributes(shader, index) ?? Array.Empty<string>();
        }
    }
}
