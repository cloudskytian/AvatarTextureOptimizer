// Shader / material analysis: finds texture properties, classifies their roles,
// and decides whether each texture is safe to remap (no ST transform, no special-purpose sampling).
// / 着色器/材质分析：查找纹理属性、归类用途，并判断每张贴图是否可安全重映射（无 ST 变换、非特殊采样用途）。
// lilToon property names were verified against jp.lilxyzw.liltoon 2.3.4 source.
// / lilToon 属性名已对照 jp.lilxyzw.liltoon 2.3.4 源码核实。

using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace net.fosa.avatar_texture_optimizer.editor.analysis
{
    /// <summary>A texture property found on a material. / 材质上发现的一个纹理属性。</summary>
    public sealed class TexturePropInfo
    {
        public string Name;
        public TextureRole Role;
        public Texture2D Texture;
        public bool HasSTTransform;       // material applies scale/offset -> unsafe / 材质施加了 ST 变换 → 不安全
        public string SpecialPurpose;     // non-mesh-UV sampling (matcap, reflection...) -> whitelist / 非网格 UV 采样用途
        public bool HasNoScaleOffset;     // shader property flagged NoScaleOffset / 属性带 NoScaleOffset 标记
    }

    /// <summary>
    /// Analyzes one material. Returns texture props; textures with transforms or special purposes are
    /// reported and must be treated as whitelist. / 分析单个材质；返回纹理属性列表；带变换或特殊用途的贴图必须按白名单处理。
    /// </summary>
    public static class ShaderAnalyzer
    {
        // Substrings that classify a texture property as a mask / grayscale texture. / 判定为蒙版/灰度贴图的子串。
        private static readonly string[] MaskHints =
        {
            "Mask", "Gloss", "Smoothness", "ShadingGrade", "Tri", "AlphaTex", "Dissolve"
        };

        // Substrings that classify a texture property as a normal map. / 判定为法线贴图的子串。
        private static readonly string[] NormalHints =
        {
            "Bump", "Normal"
        };

        // Property names that are sampled in view space / special spaces, NOT via mesh UV. / 非网格 UV 采样的特殊用途属性。
        private static readonly Dictionary<string, string> SpecialPurposeProps = new Dictionary<string, string>
        {
            { "_MatCapTex", "MatCap (view-space sampling / 视空间采样)" },
            { "_MatCap2ndTex", "MatCap (view-space sampling / 视空间采样)" },
            { "_BacklightColorTex", "Backlight (special sampling / 特殊采样)" },
            { "_ReflectionColorTex", "Reflection (special sampling / 特殊采样)" },
            { "_AudioLinkMask", "AudioLink (special sampling / 特殊采样)" },
        };

        // lilToon decal-animation properties; nonzero means the texture is deformed at runtime. / lilToon 贴花动画属性。
        private static readonly string[] DecalAnimationProps =
        {
            "_Main2ndTexDecalAnimation", "_Main3rdTexDecalAnimation", "_Main2ndTexDecalSubParam", "_Main3rdTexDecalSubParam"
        };

        /// <summary>
        /// Analyze a material. / 分析一个材质。
        /// </summary>
        public static List<TexturePropInfo> Analyze(Material material)
        {
            var result = new List<TexturePropInfo>();
            if (material == null) return result;

            var shader = material.shader;
            if (shader == null) return result;

            var count = ShaderUtil.GetPropertyCount(shader);
            for (int i = 0; i < count; i++)
            {
                var propType = ShaderUtil.GetPropertyType(shader, i);
                if (propType != ShaderUtil.ShaderPropertyType.TexEnv) continue;

                var name = ShaderUtil.GetPropertyName(shader, i);
                if (!material.HasProperty(name)) continue;

                var tex = material.GetTexture(name) as Texture2D;
                if (tex == null) continue; // not a Texture2D (e.g. cubemap) or unset -> skip / 非 Texture2D 或未设置

                var info = new TexturePropInfo
                {
                    Name = name,
                    Texture = tex,
                    HasNoScaleOffset = (ShaderUtil.GetPropertyFlags(shader, i) & ShaderUtil.ShaderPropertyFlags.NoScaleOffset) != 0,
                };

                // ST transform check (scale/offset). / ST 变换检查。
                if (!info.HasNoScaleOffset)
                {
                    var scale = material.GetTextureScale(name);
                    var offset = material.GetTextureOffset(name);
                    if (scale.x != 1f || scale.y != 1f || offset.x != 0f || offset.y != 0f)
                    {
                        info.HasSTTransform = true;
                    }
                }

                // Special purpose check. / 特殊用途检查。
                if (SpecialPurposeProps.TryGetValue(name, out var reason))
                {
                    info.SpecialPurpose = reason;
                }
                else
                {
                    // lilToon decal animation check. / lilToon 贴花动画检查。
                    foreach (var decalProp in DecalAnimationProps)
                    {
                        if (!material.HasProperty(decalProp)) continue;
                        var v = material.GetVector(decalProp);
                        if (v.x != 0 || v.y != 0 || v.z != 0 || v.w != 0)
                        {
                            info.SpecialPurpose = "decal animation (" + decalProp + ") / 贴花动画";
                            break;
                        }
                    }
                }

                // Role classification / 用途分类
                info.Role = ClassifyRole(name);

                result.Add(info);
            }

            return result;
        }

        /// <summary>
        /// Classify a texture property name into a role. / 按属性名归类用途。
        /// </summary>
        public static TextureRole ClassifyRole(string propertyName)
        {
            string n = propertyName;
            // Mask hints take priority over normal hints (e.g. _Bump2ndScaleMask). / 蒙版优先于法线。
            foreach (var m in MaskHints)
            {
                if (n.IndexOf(m, System.StringComparison.OrdinalIgnoreCase) >= 0) return TextureRole.Mask;
            }
            foreach (var nm in NormalHints)
            {
                if (n.IndexOf(nm, System.StringComparison.OrdinalIgnoreCase) >= 0) return TextureRole.Normal;
            }
            // Color-like textures / 颜色类贴图
            return TextureRole.MainColor;
        }
    }
}
