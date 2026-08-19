using System;
using System.Collections.Generic;
using System.Linq;
using NetFosa.AvatarTextureOptimizer.Editor.Logging;
using UnityEditor;
using UnityEngine;
using NetFosa.AvatarTextureOptimizer;

namespace NetFosa.AvatarTextureOptimizer.Editor.Analysis
{
    /// <summary>
    /// 着色器贴图属性描述。通过对 Shader 属性表的运行时分析得出（兼容 lilToon 与使用标准属性的着色器）。
    /// </summary>
    public sealed class ShaderTextureProperty
    {
        public string name;
        public int id;
        public bool isMainTexture;   // [MainTexture]
        public bool isNormal;        // [Normal]
        public bool hidden;          // [HideInInspector]
        public bool noScaleOffset;   // [NoScaleOffset]
        public ATOUsageKind kind;

        /// <summary>伴侣 UV 模式属性（如 _Main2ndTex_UVMode），无则 null。</summary>
        public string uvModeProperty;

        /// <summary>伴侣 ST 属性（如 _MainTex_ST），无则 null。</summary>
        public string stProperty;

        /// <summary>是否属于贴花/MatCap/屏幕空间等特殊用途（应白名单）。</summary>
        public bool specialUse;
    }

    /// <summary>
    /// 着色器分析器：枚举贴图属性、判定种类/特殊用途/UV 通道选择器/ST 伴侣。
    /// 依据 lilToon 源码（Default.lilblock 属性表、lilPropertyNameChecker.cs 命名规则）与
    /// Unity Shader 标准属性 attribute（[MainTexture]/[Normal]/[HideInInspector]/[NoScaleOffset]）。
    /// </summary>
    public static class ShaderAnalyzer
    {
        private static readonly Dictionary<Shader, ShaderTextureProperty[]> _cache =
            new Dictionary<Shader, ShaderTextureProperty[]>();

        // lilToon 已知特殊用途贴图（非纯 UV 采样）
        private static readonly HashSet<string> SpecialUseSuffixes = new HashSet<string>
        {
            "_MatCapTex", "_MatCap2ndBumpMap", "_MatCapBlendMask", "_MatCap2ndBlendMask",
            "_RimMap", "_RimColorTex", "_RimShadeMask",
            "_EmissionMap", "_EmissionGradTex", "_Emission2ndGradTex",
            "_OutlineVectorTex", "_DitherTex", "_AudioLinkLocalMap",
            "_MainGradationTex", "_ShadowColorTex", "_Shadow2ndColorTex", "_Shadow3rdColorTex",
            "_BacklightColorTex", "_ReflectionColorTex",
        };

        // lilToon 已知 UV 采样但属"主色类"的贴图
        private static readonly HashSet<string> KnownMainTextures = new HashSet<string>
        {
            "_MainTex", "_Main2ndTex", "_Main3rdTex",
        };

        private static readonly HashSet<string> KnownMaskTextures = new HashSet<string>
        {
            "_Main2ndBlendMask", "_Main3rdBlendMask", "_MainColorAdjustMask",
            "_ShadowBlurMask", "_ShadowBorderMask", "_ShadowStrengthMask",
            "_OutlineWidthMask", "_AnisotropyScaleMask", "_Bump2ndScaleMask",
        };

        public static ShaderTextureProperty[] GetTextureProperties(Shader shader)
        {
            if (shader == null) return Array.Empty<ShaderTextureProperty>();
            if (_cache.TryGetValue(shader, out var cached)) return cached;

            var result = new List<ShaderTextureProperty>();
            try
            {
                int count = shader.GetPropertyCount();
                for (int i = 0; i < count; i++)
                {
                    if (shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                    string name = shader.GetPropertyName(i);
                    int id = Shader.PropertyToID(name);
                    var flags = shader.GetPropertyFlags(i);
                    var attrs = shader.GetPropertyAttributes(i) ?? Array.Empty<string>();

                    var prop = new ShaderTextureProperty
                    {
                        name = name,
                        id = id,
                        isMainTexture = attrs.Contains("MainTexture"),
                        isNormal = attrs.Contains("Normal"),
                        hidden = (flags & ShaderPropertyFlags.HideInInspector) != 0,
                        noScaleOffset = attrs.Contains("NoScaleOffset"),
                    };

                    // 伴侣属性探测
                    if (shader.GetPropertyIndex(name + "_UVMode") >= 0) prop.uvModeProperty = name + "_UVMode";
                    if (shader.GetPropertyIndex(name + "_ST") >= 0) prop.stProperty = name + "_ST";
                    if (string.IsNullOrEmpty(prop.stProperty) && shader.GetPropertyIndex(name + "_Offset") >= 0
                        && shader.GetPropertyIndex(name + "_Scale") >= 0)
                        prop.stProperty = name + "_Offset"; // 宽松匹配（仅用于检测是否存在变换）

                    // 种类与特殊用途判定
                    prop.kind = ClassifyKind(prop, shader);

                    result.Add(prop);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ATO] ShaderAnalyzer failed on shader {shader.name}: {e.Message}");
                return Array.Empty<ShaderTextureProperty>();
            }

            _cache[shader] = result.ToArray();
            return _cache[shader];
        }

        public static void ClearCache() => _cache.Clear();

        private static ATOUsageKind ClassifyKind(ShaderTextureProperty prop, Shader shader)
        {
            if (prop.isNormal || NameContains(prop.name, "normal", "bump", "nrm")) return ATOUsageKind.Normal;
            if (SpecialUseSuffixes.Contains(prop.name) || NameContains(prop.name, "matcap", "screen", "decal", "rim", "emission"))
            {
                prop.specialUse = true;
                return ATOUsageKind.Other;
            }
            if (KnownMaskTextures.Contains(prop.name) || NameContains(prop.name, "mask", "ao", "metallic", "smoothness", "occlusion", "height"))
            {
                // 蒙版类贴图若带 [Normal] 除外（上面已处理）
                return ATOUsageKind.GrayMask;
            }
            if (KnownMainTextures.Contains(prop.name) || prop.isMainTexture || NameContains(prop.name, "base", "diffuse", "albedo", "maintex", "color"))
                return ATOUsageKind.Main;
            return ATOUsageKind.Other;
        }

        private static bool NameContains(string name, params string[] tokens)
        {
            var lower = name.ToLowerInvariant();
            foreach (var t in tokens)
            {
                if (lower.Contains(t)) return true;
            }
            return false;
        }

        // ---------------- 材质级判定 ----------------

        /// <summary>
        /// 读取材质某贴图属性的 UV 通道（0=UV0..7）。依赖 _UVMode 伴侣属性；值 0 表示 UV0。
        /// 返回 -1 表示"非 UV 采样"（MatCap/Rim/Screen 等特殊模式）→ 应白名单。
        /// </summary>
        public static int ResolveUvChannel(Material material, ShaderTextureProperty prop)
        {
            if (prop.specialUse) return -1;
            if (string.IsNullOrEmpty(prop.uvModeProperty)) return 0;

            if (material.HasProperty(prop.uvModeProperty))
            {
                int mode = Mathf.RoundToInt(material.GetFloat(prop.uvModeProperty));
                // lilToon 枚举约定：0=UV0,1=UV1,2=UV2,3=UV3,4=MatCap,5=Rim(部分),6=Screen
                if (mode <= 3) return mode;
                return -1; // MatCap / Rim / Screen 等
            }
            return 0;
        }

        /// <summary>
        /// 检查 ST（offset/scale/rotation）是否存在变换（含标准 _ST、_Offset/_Scale、_ScrollRotate、HSVG）。
        /// 存在任何变换 → 返回 true（应白名单）。
        /// </summary>
        public static bool HasSTTransform(Material material, ShaderTextureProperty prop)
        {
            if (material == null) return false;

            if (!string.IsNullOrEmpty(prop.stProperty))
            {
                if (material.HasProperty(prop.stProperty))
                {
                    if (prop.stProperty.EndsWith("_ST"))
                    {
                        var st = material.GetVector(prop.stProperty);
                        // 旋转/平移/缩放检测：offset 非 0 或 scale 非 1
                        if (Mathf.Abs(st.x) > 1e-4f || Mathf.Abs(st.y) > 1e-4f) return true;
                        if (Mathf.Abs(st.z - 1f) > 1e-4f || Mathf.Abs(st.w - 1f) > 1e-4f) return true;
                    }
                    else
                    {
                        // _Offset / _Scale 组合
                        if (material.HasProperty(prop.name + "_Scale"))
                        {
                            var s = material.GetVector(prop.name + "_Scale");
                            if (Mathf.Abs(s.x - 1f) > 1e-4f || Mathf.Abs(s.y - 1f) > 1e-4f) return true;
                        }
                        var o = material.GetVector(prop.stProperty);
                        if (Mathf.Abs(o.x) > 1e-4f || Mathf.Abs(o.y) > 1e-4f) return true;
                    }
                }
            }

            // lilToon 滚动/旋转
            var scrollProp = prop.name + "_ScrollRotate";
            if (material.HasProperty(scrollProp))
            {
                var v = material.GetVector(scrollProp);
                if (Mathf.Abs(v.x) > 1e-4f || Mathf.Abs(v.y) > 1e-4f || Mathf.Abs(v.z) > 1e-4f) return true;
            }
            var hsvgProp = prop.name + "HSVG";
            if (material.HasProperty(hsvgProp))
            {
                var v = material.GetVector(hsvgProp);
                if (Mathf.Abs(v.x) > 1e-4f || Mathf.Abs(v.y - 1f) > 1e-4f || Mathf.Abs(v.z - 1f) > 1e-4f || Mathf.Abs(v.w - 1f) > 1e-4f) return true;
            }
            return false;
        }

        /// <summary>获取材质的全部着色器关键字（供特殊用途判定）。</summary>
        public static HashSet<string> GetShaderKeywords(Material material)
        {
            var set = new HashSet<string>();
            try
            {
                var shader = material.shader;
                if (shader == null) return set;
                var local = ShaderUtil.GetShaderLocalKeywords(shader);
                foreach (var k in local) set.Add(k);
                var global = ShaderUtil.GetShaderGlobalKeywords(shader);
                foreach (var k in global) set.Add(k);
            }
            catch (Exception) { }
            return set;
        }
    }
}
