using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace net.fosa.ato
{
    /// <summary>
    /// 着色器属性表分析 / Shader property-table analysis.
    ///
    /// 通过读取 Shader 资产的序列化属性表(m_PropInfo: 属性名、类型、特性如 [MainTexture]/[Normal]/[NoScaleOffset])
    /// 自动分析贴图属性, 兼容 lilToon 与其他使用标准关键字的着色器, 并尽量兼容未来版本.
    /// Reads the serialized property table of the Shader asset (names, types, attributes such as
    /// [MainTexture]/[Normal]/[NoScaleOffset]) to classify texture properties. This works for lilToon
    /// and other shaders using standard keywords, and aims to stay compatible with future versions.
    /// 无法确认用途的贴图属性 -> 视作白名单并报 warning / Unverifiable usages -> whitelist + warning.
    /// </summary>
    internal static class ATOShaderAnalysis
    {
        /// <summary>属性分析结果 / Analysis result for one texture property.</summary>
        public sealed class PropInfo
        {
            public string name;                        // 属性名 / property name
            public ATOTextureCategory category;
            public int uvChannel = 0;                  // 网格UV通道 / mesh UV channel used
            public bool meshUvSampled = true;          // 是否经网格UV采样 / sampled via mesh UV
            public bool noScaleOffset;                 // 是否有 [NoScaleOffset] / has [NoScaleOffset]
            public string detail;                      // 说明 / description
        }

        // 已知特殊用途(非网格UV采样)的贴图属性 / Known texture props that are NOT mesh-UV sampled.
        // lilToon 的 matcap 使用视角空间坐标, 灯光记忆图使用世界坐标; 其他着色器如有自定义坐标也应加入此表.
        // lilToon matcaps use view-space coords; light-memory map uses world coords.
        private static readonly HashSet<string> SpecialUsageProps = new HashSet<string>
        {
            "_MatCapTex", "_MatCap2ndTex", "_MatCapBumpMap", "_MatCap2ndBumpMap",
            "_MatCapMask", "_MatCap2ndMask", "_LightingMemorandomMap", "_AudioLinkMap",
            "_RampTex" // 通用 toon ramp(uv.y 采样, 通常竖直条带, 缩放会破坏) / generic toon ramps are uv.y-sampled strips; resizing breaks them
        };

        // 已知按网格UV采样的贴图属性(名称关键字) / Known mesh-UV-sampled texture props (name keywords).
        private static readonly (string pattern, ATOTextureCategory cat)[] KnownPatterns =
        {
            ("bump", ATOTextureCategory.Normal),
            ("normal", ATOTextureCategory.Normal),
            ("metallic", ATOTextureCategory.Mask),
            ("mask", ATOTextureCategory.Mask),
            ("strength", ATOTextureCategory.Mask),
            ("width", ATOTextureCategory.Mask),
            ("rim", ATOTextureCategory.Mask),
            ("shadow", ATOTextureCategory.Mask),
            ("outline", ATOTextureCategory.Mask),
            ("ao", ATOTextureCategory.Grayscale),
            ("occlusion", ATOTextureCategory.Grayscale),
            ("smoothness", ATOTextureCategory.Grayscale),
            ("detailmask", ATOTextureCategory.Grayscale),
            ("shininess", ATOTextureCategory.Mask),
            ("glitter", ATOTextureCategory.Mask)
        };

        /// <summary>
        /// 分析着色器全部贴图属性 / Analyze all texture properties of a shader.
        /// 返回属性名 -> 结果; 分析失败(读取不到属性表)返回 null / Returns null when the property table is unreadable.
        /// </summary>
        public static Dictionary<string, PropInfo> Analyze(Shader shader)
        {
            if (shader == null) return null;

            // 第三方扩展优先 / third-party resolvers first
            foreach (var resolver in ATOExtensionRegistry.GetCategoryResolvers())
            {
                if (!string.Equals(resolver.ShaderName, shader.name, StringComparison.OrdinalIgnoreCase)) continue;
                var ext = resolver.Resolve(shader);
                if (ext == null) continue;
                var dict = new Dictionary<string, PropInfo>();
                foreach (var kv in ext)
                {
                    dict[kv.Key] = new PropInfo
                    {
                        name = kv.Key,
                        category = kv.Value.category,
                        uvChannel = kv.Value.uvChannel,
                        meshUvSampled = true,
                        noScaleOffset = false,
                        detail = "extension-resolved"
                    };
                }

                return dict;
            }

            // 读取序列化属性表 / read the serialized property table
            try
            {
                var so = new SerializedObject(shader);
                var parsed = so.FindProperty("m_ParsedForm");
                var props = parsed?.FindPropertyRelative("m_PropInfo");
                if (props == null || !props.isArray)
                {
                    so.Dispose();
                    return null;
                }

                var result = new Dictionary<string, PropInfo>();
                for (int i = 0; i < props.arraySize; i++)
                {
                    var elem = props.GetArrayElementAtIndex(i);
                    var type = elem.FindPropertyRelative("m_Type")?.intValue ?? -1;
                    if (type != 4) continue; // 4 = Texture / texture type only

                    var name = elem.FindPropertyRelative("m_Name")?.stringValue;
                    if (string.IsNullOrEmpty(name)) continue;

                    bool isMain = false, isNormal = false, noST = false;
                    var attrs = elem.FindPropertyRelative("m_Attributes");
                    if (attrs != null && attrs.isArray)
                    {
                        for (int a = 0; a < attrs.arraySize; a++)
                        {
                            var attr = attrs.GetArrayElementAtIndex(a);
                            var attrName = attr.FindPropertyRelative("m_Name")?.stringValue ?? "";
                            if (attrName == "MainTexture") isMain = true;
                            else if (attrName == "Normal") isNormal = true;
                            else if (attrName == "NoScaleOffset") noST = true;
                            else if (attrName == "Decal") { /* 贴花 -> 特殊用途 / decals are special */ }
                        }
                    }

                    var info = new PropInfo { name = name, noScaleOffset = noST };

                    if (SpecialUsageProps.Contains(name))
                    {
                        info.meshUvSampled = false;
                        info.category = ATOTextureCategory.Color;
                        info.detail = "special usage (not mesh-UV sampled)";
                    }
                    else if (isNormal)
                    {
                        info.category = ATOTextureCategory.Normal;
                        info.detail = "[Normal]";
                    }
                    else
                    {
                        // 名称关键字 / name-based heuristics
                        string lower = name.ToLowerInvariant();
                        var match = KnownPatterns.FirstOrDefault(p => lower.Contains(p.pattern));
                        if (match.pattern != null)
                        {
                            info.category = match.cat;
                            info.detail = $"keyword '{match.pattern}'";
                        }
                        else
                        {
                            info.category = ATOTextureCategory.Color;
                            info.detail = isMain ? "[MainTexture]" : "default color";
                        }
                    }

                    // 无任何特性且无匹配关键字且无 NoScaleOffset 的贴图 -> 保守: 视为未知用途
                    // Texture props with no attributes, no keyword match and no [NoScaleOffset] -> conservative whitelist.
                    if (!isMain && !isNormal && !noST && info.meshUvSampled
                        && !KnownPatterns.Any(p => name.ToLowerInvariant().Contains(p.pattern))
                        && !SpecialUsageProps.Contains(name))
                    {
                        // 仅当属性表完全没有特性时才视为未知(有些着色器不给贴图加特性)
                        // Only treat as unknown when the property table carries no attributes at all.
                        info.detail = "no attributes (conservative)";
                    }

                    result[name] = info;
                }

                so.Dispose();
                return result.Count > 0 ? result : null;
            }
            catch (Exception e)
            {
                ATOLog.Warn($"着色器属性表分析失败 / shader property analysis failed for {shader.name}: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// 判断材质属性是否被 ST 变换 / Checks whether a material texture property uses ST transforms.
        /// </summary>
        public static bool HasSTTransform(Material material, string prop)
        {
            if (material == null || string.IsNullOrEmpty(prop)) return false;
            if (!material.HasProperty(prop)) return false;
            var offset = material.GetTextureOffset(prop);
            var scale = material.GetTextureScale(prop);
            return offset != Vector2.zero || scale != Vector2.one;
        }
    }
}
