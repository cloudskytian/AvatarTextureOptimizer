// AvatarTextureOptimizer
// File: Editor/Analysis/ShaderAnalyzer.cs
//
// Analyzes shader property tables and keywords to classify texture slots
// (main color / normal / mask / unknown) and determine which mesh UV channel
// each texture samples. lilToon and other standard-keyword shaders are covered
// by property-table scanning, so future shader versions are supported as long
// as they keep standard conventions. Properties that cannot be classified
// safely are reported so the caller can treat them as whitelisted.
//
// 分析着色器属性表与关键字，对贴图槽位分类（主色/法线/蒙版/未知），并确定
// 每张贴图采样哪个网格 UV 通道。通过属性表扫描覆盖 lilToon 与其他使用标准
// 关键字的着色器，只要它们保持标准约定即可兼容未来版本。无法安全分类的
// 属性会被报告，调用方可将其视作白名单。

using System;
using System.Collections.Generic;
using net.fosa.avatar_texture_optimizer.editor.logging;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor.analysis
{
    /// <summary>Result of analyzing one texture property. / 分析一个贴图属性的结果。</summary>
    public readonly struct TexturePropertyInfo
    {
        public readonly string PropertyName;
        public readonly model.TextureUsageType Type;
        public readonly int UVChannel;      // -1 = unknown / not mesh UV
        public readonly bool NoScaleOffset; // property can never have ST
        public readonly bool IsRisky;       // cannot classify safely -> whitelist
        public readonly string RiskReason;

        public TexturePropertyInfo(string propertyName, model.TextureUsageType type, int uvChannel,
            bool noScaleOffset, bool risky, string riskReason)
        {
            PropertyName = propertyName;
            Type = type;
            UVChannel = uvChannel;
            NoScaleOffset = noScaleOffset;
            IsRisky = risky;
            RiskReason = riskReason;
        }
    }

    public static class ShaderAnalyzer
    {
        // ---- Standard keyword tables (extendable) ----
        // ---- 标准关键字表（可扩展） ----

        /// <summary>Property name fragments indicating a normal map. / 表示法线贴图的属性名片段。</summary>
        private static readonly string[] NormalKeywords =
        {
            "_NormalMap", "_BumpMap", "_NormalTex", "_Normal", "NormalMap",
        };

        /// <summary>Property name fragments indicating a grayscale/mask texture. / 表示灰度/蒙版贴图的属性名片段。</summary>
        private static readonly string[] MaskKeywords =
        {
            "_MaskMap", "_DetailMask", "_MetallicGlossMap", "_OcclusionMap", "_AoMap",
            "MaskTex", "_MainColorAdjustMask", "_Main2ndBlendMask", "_Main3rdBlendMask",
            "_ShadowBorderColorTex", "_OutlineWidthTexture", "_OutlineWidthMask",
        };

        /// <summary>Property name fragments indicating a main color (sRGB) texture. / 表示主色（sRGB）贴图的属性名片段。</summary>
        private static readonly string[] MainColorKeywords =
        {
            "_MainTex", "_BaseMap", "_BaseColorMap", "_ShadeTexture", "_EmissionMap",
            "_MatCapTex", "_RimLightMap", "_OutlineColorTex",
        };

        /// <summary>
        /// lilToon UV-mode property values that map to a mesh UV channel.
        /// 映射到网格 UV 通道的 lilToon UV 模式取值。
        /// </summary>
        private const int UVModeUV0 = 0, UVModeUV1 = 1, UVModeUV2 = 2, UVModeUV3 = 3;

        /// <summary>
        /// Analyze a texture property on a shader (property table based).
        /// 基于属性表分析着色器上的一个贴图属性。
        /// </summary>
        public static TexturePropertyInfo AnalyzeProperty(Shader shader, string propertyName)
        {
            bool noScaleOffset = HasNoScaleOffset(shader, propertyName);
            var type = ClassifyName(propertyName);
            int uvChannel = 0; // default: UV0 / 默认 UV0
            bool risky = false;
            string risk = null;

            // UV mode overrides (lilToon style): look for a companion
            // "_PropertyName_UVMode" property on the shader.
            // UV 模式覆写（lilToon 风格）：查找伴随的 "_PropertyName_UVMode" 属性。
            string uvModeProp = propertyName + "_UVMode";
            int idx = FindProperty(shader, uvModeProp);
            if (idx >= 0)
            {
                uvChannel = ClassifyUVMode(uvModeProp, out risky, out risk);
            }

            // MatCap/Panorama/Screen-space textures are not sampled by mesh UV.
            // 屏幕空间（MatCap/Panorama）贴图不由网格 UV 采样。
            if (propertyName.Contains("MatCap") || propertyName.Contains("Panorama") ||
                propertyName.Contains("ScreenTex") || propertyName.Contains("_DitherTex"))
            {
                risky = true;
                risk = "texture is sampled in screen space, not mesh UV / 贴图由屏幕空间采样，非网格 UV";
            }

            if (type == model.TextureUsageType.Unknown)
            {
                risky = true;
                risk = $"unclassified property {propertyName} / 无法分类的属性 {propertyName}";
            }

            return new TexturePropertyInfo(propertyName, type, uvChannel, noScaleOffset, risky, risk);
        }

        /// <summary>
        /// Enumerate all Texture2D properties of a shader's property table.
        /// 枚举着色器属性表中的全部 Texture2D 属性。
        /// </summary>
        public static List<string> EnumerateTextureProperties(Shader shader)
        {
            var result = new List<string>();
            if (shader == null) return result;
            try
            {
                int count = shader.GetPropertyCount();
                for (int i = 0; i < count; i++)
                {
                    var name = shader.GetPropertyName(i);
                    var type = shader.GetPropertyType(i);
                    if (type == ShaderPropertyType.Texture)
                    {
                        var dim = shader.GetPropertyTextureDimension(i);
                        if (dim == UnityEngine.Rendering.TextureDimension.Tex2D)
                            result.Add(name);
                    }
                }
            }
            catch (Exception e)
            {
                ATOLog.Warn($"[ATO] Failed to enumerate shader properties of {shader.name}: {e.Message}");
            }
            return result;
        }

        private static bool HasNoScaleOffset(Shader shader, string propertyName)
        {
            try
            {
                int idx = FindProperty(shader, propertyName);
                if (idx < 0) return false;
                var flags = shader.GetPropertyFlags(idx);
                return (flags & ShaderPropertyFlags.NoScaleOffset) != 0;
            }
            catch
            {
                return false;
            }
        }

        private static int FindProperty(Shader shader, string propertyName)
        {
            try
            {
                int count = shader.GetPropertyCount();
                for (int i = 0; i < count; i++)
                    if (shader.GetPropertyName(i) == propertyName) return i;
            }
            catch
            {
                // Property table access can throw on some edge-case shaders;
                // treat as not-found. 属性表访问在个别边界着色器上可能抛异常；
                // 视为未找到。
            }
            return -1;
        }

        /// <summary>
        /// Read the material's UV-mode value (lilToon "_Xxx_UVMode") and map it
        /// to a mesh UV channel. Values outside 0..3 are not mesh UV.
        /// 读取材质的 UV 模式值（lilToon "_Xxx_UVMode"）并映射到网格 UV 通道。
        /// 0..3 之外的值不是网格 UV。
        /// </summary>
        private static int ClassifyUVMode(string uvModeProperty, out bool risky, out string risk)
        {
            risky = false;
            risk = null;
            // The caller passes the shader-level property; the actual VALUE is
            // read per material in the collector. Here we only validate the
            // property exists, which we already know (idx >= 0).
            // 调用方传入着色器级属性；实际值在收集器中按材质读取。这里只
            // 验证属性存在（已知 idx >= 0）。
            return -2; // sentinel: read per material / 哨兵值：按材质读取
        }

        /// <summary>
        /// Map a material's UV-mode float to a mesh UV channel, or -1 when the
        /// texture is not sampled by mesh UV (MatCap etc.).
        /// 将材质的 UV 模式浮点值映射为网格 UV 通道；非网格 UV 采样返回 -1。
        /// </summary>
        public static int ResolveUVModeValue(float uvModeValue, string propertyName, out bool risky, out string risk)
        {
            risky = false;
            risk = null;
            int mode = Mathf.RoundToInt(uvModeValue);
            switch (mode)
            {
                case UVModeUV0: return 0;
                case UVModeUV1: return 1;
                case UVModeUV2: return 2;
                case UVModeUV3: return 3;
                default:
                    risky = true;
                    risk = $"{propertyName}: UV mode {mode} is not a mesh UV channel / UV 模式 {mode} 不是网格 UV 通道";
                    return -1;
            }
        }

        /// <summary>Classify a property by its name against the keyword tables. / 按名字对照关键字表分类属性。</summary>
        private static model.TextureUsageType ClassifyName(string propertyName)
        {
            foreach (var kw in NormalKeywords)
                if (propertyName.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                    return model.TextureUsageType.NormalMap;
            foreach (var kw in MaskKeywords)
                if (propertyName.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                    return model.TextureUsageType.Mask;
            foreach (var kw in MainColorKeywords)
                if (propertyName.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                    return model.TextureUsageType.MainColor;
            return model.TextureUsageType.Unknown;
        }
    }
}
