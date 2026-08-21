using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// Shader property table analysis. Uses ShaderUtil to enumerate texture properties and their attributes
// so future shader versions are supported without hard-coded tables; a small known-name fallback table
// covers common conventions.
// 着色器属性表分析。使用 ShaderUtil 枚举贴图属性及其标记以兼容未来版本；另有常见命名兜底表。

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// One texture property of a shader, as resolved for a specific material.
    /// 着色器的一个贴图属性（针对某个材质解析）。
    /// </summary>
    public sealed class TexturePropertyInfo
    {
        public string PropertyName;
        public TextureKind Kind = TextureKind.Unknown;
        /// <summary>True if the property declares [Normal]. 是否声明 [Normal]。</summary>
        public bool IsNormalAttr;
        /// <summary>True if the property declares [MainTexture]. 是否声明 [MainTexture]。</summary>
        public bool IsMainAttr;
        /// <summary>True if the property declares [NoScaleOffset] (ST is identity by declaration). 是否声明 [NoScaleOffset]。</summary>
        public bool NoScaleOffset;
        /// <summary>True if the property carries [lilUVAnim] or similar (ST transform by declaration). 是否声明 [lilUVAnim] 类（ST 变换）。</summary>
        public bool DeclaresUVTransform;
        /// <summary>True if the property is a normal-map typed import or [Normal] attrs. 是否法线贴图类型导入或 [Normal] 标记。</summary>
        public bool NormalByImport;
        /// <summary>UV channel index resolved from <Prop>_UVMode material int (0..3; -1 = unresolved). 由 <Prop>_UVMode 解析的 UV 通道。</summary>
        public int UVChannel = 0;
        /// <summary>True when UVMode resolves to MatCap / view space (cannot repack). UVMode 为 MatCap（视图空间）时不可重排。</summary>
        public bool IsMatCap;
        /// <summary>Toggle property name (e.g. _UseBumpMap) that gates this texture; null if none. 控制该贴图启用的开关属性名。</summary>
        public string ToggleProperty;
        /// <summary>ST transform property name (e.g. _MainTex_ST / _MainTex_ScrollRotate); null if none. ST 变换属性名。</summary>
        public string STProperty;
    }

    /// <summary>
    /// Per-shader cached analysis. 每个着色器的缓存分析。
    /// </summary>
    public sealed class ShaderInfo
    {
        public Shader Shader;
        public List<TexturePropertyInfo> TextureProperties = new List<TexturePropertyInfo>();
    }

    public static class ShaderPropertyTable
    {
        private static readonly Dictionary<Shader, ShaderInfo> Cache = new Dictionary<Shader, ShaderInfo>();

        public static ShaderInfo Get(Shader shader)
        {
            if (shader == null) return null;
            if (Cache.TryGetValue(shader, out var info)) return info;
            info = Analyze(shader);
            Cache[shader] = info;
            return info;
        }

        public static void ClearCache() => Cache.Clear();

        /// <summary>Known normal-map property names (built-in & lilToon conventions). 常见法线贴图属性名（内置与 lilToon 惯例）。</summary>
        private static readonly HashSet<string> KnownNormalProps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "_BumpMap", "_Bump2ndMap", "_NormalMap", "_DetailNormalMap", "_DetailNormalMapScale",
            "_Normal", "_Bump", "_NMap", "_AnisotropyTangentMap", "_MatCapBumpMap", "_MatCap2ndBumpMap",
            "_OutlineVectorTex", "_FurVectorTex", "_TriplanarNormalMap", "_NORMALMAP",
        };

        /// <summary>Known main-color property names. 常见主色属性名。</summary>
        private static readonly HashSet<string> KnownMainProps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "_MainTex", "_BaseMap", "_BaseColorMap", "_AlbedoMap", "_Albedo", "_ColorMap", "_DiffuseTex", "_MainTex2",
        };

        /// <summary>Known mask/grayscale property names. 常见蒙版/灰度属性名。</summary>
        private static readonly HashSet<string> KnownMaskProps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "_ShadingGradeMap", "_MetallicGlossMap", "_MetallicMap", "_SmoothnessMap", "_RoughnessMap", "_MaskMap",
            "_OcclusionMap", "_AOMap", "_DetailMask", "_MainColorAdjustMask", "_MatCapMask", "_Main2ndBlendMask",
            "_Main3rdBlendMask", "_Bump2ndScaleMask", "_AnisotropyScaleMask", "_AnisotropyShiftNoiseMask",
            "_AlphaMask", "_DissolveMask", "_DissolveNoiseMask", "_MainGradationTex", "_BacklightColorTex",
            "_DitherTex", "_OutlineMask", "_EmissionMap", "_DetailAlbedoMap", "_DetailNormalMap", "_DetailMask",
        };

        private static ShaderInfo Analyze(Shader shader)
        {
            var info = new ShaderInfo { Shader = shader };
            int count = ShaderUtil.GetPropertyCount(shader);
            for (int i = 0; i < count; i++)
            {
                var type = ShaderUtil.GetPropertyType(shader, i);
                if (type != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                string name = ShaderUtil.GetPropertyName(shader, i);
                var attrs = ShaderUtil.GetPropertyAttributes(shader, i) ?? Array.Empty<string>();

                var tp = new TexturePropertyInfo { PropertyName = name };
                foreach (var a in attrs)
                {
                    if (string.Equals(a, "Normal", StringComparison.OrdinalIgnoreCase)) tp.IsNormalAttr = true;
                    else if (string.Equals(a, "MainTexture", StringComparison.OrdinalIgnoreCase)) tp.IsMainAttr = true;
                    else if (string.Equals(a, "NoScaleOffset", StringComparison.OrdinalIgnoreCase)) tp.NoScaleOffset = true;
                    else if (a.IndexOf("lilUVAnim", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             a.IndexOf("lilDecalAnim", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             a.IndexOf("lilAngle", StringComparison.OrdinalIgnoreCase) >= 0) tp.DeclaresUVTransform = true;
                }

                // Kind resolution: attribute first, then import type, then name table, else Unknown.
                // 种类判定：先属性，再导入类型，再名称表，否则 Unknown。
                if (tp.IsNormalAttr) tp.Kind = TextureKind.Normal;
                else if (tp.IsMainAttr) tp.Kind = TextureKind.Color;
                if (tp.Kind == TextureKind.Unknown && KnownNormalProps.Contains(name)) tp.Kind = TextureKind.Normal;
                if (tp.Kind == TextureKind.Unknown && KnownMainProps.Contains(name)) tp.Kind = TextureKind.Color;
                if (tp.Kind == TextureKind.Unknown && KnownMaskProps.Contains(name)) tp.Kind = TextureKind.Mask;
                if (tp.Kind == TextureKind.Unknown)
                {
                    // Heuristic on name. 按名称启发式。
                    if (name.IndexOf("normal", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("bump", StringComparison.OrdinalIgnoreCase) >= 0) tp.Kind = TextureKind.Normal;
                    else if (name.IndexOf("mask", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             name.IndexOf("map", StringComparison.OrdinalIgnoreCase) >= 0) tp.Kind = TextureKind.Mask;
                    else tp.Kind = TextureKind.Color;
                }

                // ST property: for lilToon conventions "<Prop>_ScrollRotate" or standard "<Prop>_ST".
                // ST 属性：lilToon "<Prop>_ScrollRotate" 或标准 "<Prop>_ST"。
                string scrollName = name + "_ScrollRotate";
                string stName = name + "_ST";
                if (HasProperty(shader, scrollName)) tp.STProperty = scrollName;
                else if (HasProperty(shader, stName)) tp.STProperty = stName;
                else if (tp.DeclaresUVTransform) tp.STProperty = scrollName; // declared but maybe missing; checked at material level. 声明但可能缺失，材质级再查。

                // Toggle gate: lilToon "_Use<Prop>" / "_UseBumpMap" etc. 开关门：lilToon "_Use<Prop>"。
                string toggle = "_Use" + name.TrimStart('_');
                if (HasProperty(shader, toggle)) tp.ToggleProperty = toggle;

                info.TextureProperties.Add(tp);
            }
            return info;
        }

        private static bool HasProperty(Shader shader, string name)
        {
            int count = ShaderUtil.GetPropertyCount(shader);
            for (int i = 0; i < count; i++)
                if (ShaderUtil.GetPropertyName(shader, i) == name) return true;
            return false;
        }

        /// <summary>
        /// Resolves the UV channel for a texture property on a material (lilToon "<Prop>_UVMode", 0..3, 4=MatCap).
        /// 解析材质上某贴图属性的 UV 通道（lilToon "<Prop>_UVMode"，0..3，4=MatCap）。
        /// </summary>
        public static int ResolveUVChannel(Material mat, TexturePropertyInfo prop, out bool isMatCap)
        {
            isMatCap = false;
            if (mat == null || prop == null) return 0;
            string uvMode = prop.PropertyName + "_UVMode";
            if (mat.HasProperty(uvMode))
            {
                int v = mat.GetInt(uvMode);
                if (v == 4) { isMatCap = true; return 0; }
                if (v >= 0 && v <= 3) return v;
            }
            return 0;
        }

        /// <summary>
        /// True if the material's static ST for this property is identity (no scale/offset/rotation).
        /// 该属性在此材质上的静态 ST 是否为恒等（无缩放/平移/旋转）。
        /// </summary>
        public static bool HasIdentityST(Material mat, TexturePropertyInfo prop)
        {
            if (mat == null || prop == null) return true;
            if (mat.HasProperty(prop.PropertyName + "_ST"))
            {
                var s = mat.GetTextureScale(prop.PropertyName);
                var o = mat.GetTextureOffset(prop.PropertyName);
                if (s.x != 1f || s.y != 1f || o.x != 0f || o.y != 0f) return false;
            }
            if (!string.IsNullOrEmpty(prop.STProperty) && mat.HasProperty(prop.STProperty))
            {
                var v = mat.GetVector(prop.STProperty);
                // lilToon ScrollRotate = (rotation_deg, scrollX, scrollY, ...); identity only when all zero.
                // lilToon ScrollRotate=(旋转角, 滚动X, 滚动Y, ...)；全 0 才是恒等。
                if (v.x != 0f || v.y != 0f || v.z != 0f || v.w != 0f) return false;
            }
            return true;
        }

        /// <summary>
        /// True if the property is definitively disabled on this material (toggle int == 0, keyword off, no animation).
        /// 该属性是否在此材质上确定被禁用（开关为 0 且关键字关闭且无动画）。
        /// </summary>
        public static bool IsDefinitivelyDisabled(Material mat, TexturePropertyInfo prop)
        {
            if (string.IsNullOrEmpty(prop.ToggleProperty)) return false;
            if (!mat.HasProperty(prop.ToggleProperty)) return false;
            if (mat.GetInt(prop.ToggleProperty) != 0) return false;
            if (mat.IsKeywordEnabled(prop.ToggleProperty)) return false;
            return true;
        }
    }
}
