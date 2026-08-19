// ATO — Avatar Texture Optimizer
// Shader property analysis: classifies texture usages (color / normal / mask / grayscale /
// emission), determines the UV channel and flags ST transforms, scroll-rotate and decal /
// parallax usages. Three tiers: lilToon table, standard-keyword shaders, generic scan.
// 着色器属性分析：分类贴图用途（主色/法线/蒙版/灰度/自发光），确定 UV 通道，
// 标记 ST 变换、滚动旋转、贴花/视差用法。三级策略：lilToon 表、标准关键字着色器、通用扫描。
//
// The lilToon property table is derived from AAO's authoritative ShaderInformation.Liltoon.cs.
// lilToon 属性表源自 AAO 权威的 ShaderInformation.Liltoon.cs。

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// A texture property found on a material. 材质上发现的一个贴图属性。
    /// </summary>
    public class ShaderTextureInfo
    {
        public string propertyName;
        public ATOTextureKind kind = ATOTextureKind.Color;
        /// <summary>UV channel 0..7; -1 = non-mesh / screen-space / color-based (special). UV 通道；-1 表示非网格 UV。</summary>
        public int uvChannel;
        public bool isSpecialUsage;   // decal / parallax / data / non-mesh. 贴花/视差/数据/非网格。
        public bool hasScrollRotate;  // lilToon _X_ScrollRotate animated/present. lilToon 滚动旋转。
        public Vector4 scrollRotateValue;
    }

    /// <summary>
    /// Analyzes a material's shader into texture property info. 分析材质着色器为贴图属性信息。
    /// </summary>
    public static class ShaderPropertyAnalyzer
    {
        /// <summary>
        /// Analyze a material. Returns null when the shader is unsupported.
        /// 分析材质；着色器不受支持时返回 null。
        /// </summary>
        public static List<ShaderTextureInfo> Analyze(Material material)
        {
            if (material == null || material.shader == null) return null;

            // Third-party providers take precedence. 第三方提供者优先。
            var provided = AnalyzeViaProviders(material);
            if (provided != null) return provided;

            var list = IsLilToon(material) ? AnalyzeLilToon(material)
                     : IsStandardKeyword(material) ? AnalyzeStandard(material)
                     : AnalyzeGeneric(material);

            if (list == null) return null;

            // Common rule: any texture with a non-identity ST transform (tiling/offset) is unsafe
            // to remap, because our UV rewrite assumes identity mapping. 通用规则：任何带非单位
            // ST 变换（平铺/偏移）的贴图都不安全，因为我们的 UV 重写假设单位映射。
            foreach (var info in list)
            {
                if (info.isSpecialUsage) continue;
                if (HasNonIdentityST(material, info.propertyName))
                {
                    info.isSpecialUsage = true;
                }
            }
            return list;
        }

        /// <summary>
        /// Query third-party IATOTextureKindProvider implementations (TypeCache).
        /// 查询第三方 IATOTextureKindProvider 实现（TypeCache）。
        /// </summary>
        private static List<ShaderTextureInfo> AnalyzeViaProviders(Material material)
        {
            try
            {
                var types = UnityEditor.TypeCache.GetTypesDerivedFrom<net.fosa.ato.IATOTextureKindProvider>();
                foreach (var t in types)
                {
                    if (t.IsAbstract || t.IsInterface) continue;
                    net.fosa.ato.IATOTextureKindProvider provider;
                    try { provider = (net.fosa.ato.IATOTextureKindProvider)System.Activator.CreateInstance(t); }
                    catch { continue; }
                    if (!provider.Supports(material.shader)) continue;

                    var props = provider.GetTextureProperties(material.shader);
                    if (props == null) continue;
                    var list = new List<ShaderTextureInfo>();
                    foreach (var p in props)
                    {
                        var tex = material.GetTexture(p.propertyName) as Texture2D;
                        if (tex == null) continue;
                        list.Add(new ShaderTextureInfo
                        {
                            propertyName = p.propertyName,
                            kind = (ATOTextureKind)p.kind,
                            uvChannel = p.uvChannel,
                            isSpecialUsage = p.specialUsage || p.uvChannel == -1,
                            hasScrollRotate = p.mayScrollRotate && HasScrollRotateValue(material, p.propertyName),
                        });
                    }
                    ATOLog.Verbose($"[Shader] '{material.shader.name}' analyzed by provider '{provider.DisplayName}'.");
                    return list;
                }
            }
            catch (System.Exception e)
            {
                ATOLog.Verbose($"[Shader] provider query failed: {e.Message}");
            }
            return null;
        }

        private static bool HasScrollRotateValue(Material m, string prop)
        {
            if (!m.HasProperty(prop + "_ScrollRotate")) return false;
            var v = m.GetVector(prop + "_ScrollRotate");
            return Mathf.Abs(v.x) > 1e-4f || Mathf.Abs(v.y) > 1e-4f ||
                   Mathf.Abs(v.z) > 1e-4f || Mathf.Abs(v.w) > 1e-4f;
        }

        /// <summary>True if the material has a non-identity tiling/offset for a property. 属性是否存在非单位平铺/偏移。</summary>
        public static bool HasNonIdentityST(Material m, string prop)
        {
            try
            {
                var scale = m.GetTextureScale(prop);
                var offset = m.GetTextureOffset(prop);
                const float eps = 1e-4f;
                if (Mathf.Abs(scale.x - 1f) > eps || Mathf.Abs(scale.y - 1f) > eps ||
                    Mathf.Abs(offset.x) > eps || Mathf.Abs(offset.y) > eps) return true;
            }
            catch (System.Exception)
            {
                // Property not present or not a texture. 属性不存在或非贴图。
            }
            return false;
        }

        private static bool IsLilToon(Material m)
        {
            string n = m.shader.name;
            return n.Contains("lilToon") || n.Contains("liltoon") || n.Contains("_lil/") ||
                   m.HasProperty("_UseMain2ndTex");
        }

        private static bool IsStandardKeyword(Material m)
        {
            string n = m.shader.name;
            return n.Contains("Standard") || n.Contains("Lit") || n.Contains("Autodesk Interactive") ||
                   (m.HasProperty("_MainTex") && (m.HasProperty("_MetallicGlossMap") || m.HasProperty("_BumpMap")));
        }

        // ------------------------------------------------------------------ lilToon

        private static List<ShaderTextureInfo> AnalyzeLilToon(Material m)
        {
            var list = new List<ShaderTextureInfo>();

            // (property, kind) table. (属性, 类别) 表。
            Add(list, m, "_MainTex", ATOTextureKind.Color, uvMode: 0, isMain: true);
            // _BaseMap / _BaseColorMap are lilToon internal dummy properties that alias the main
            // texture (AAO's table confirms); skip them to avoid duplicate usages of the same texture.
            // _BaseMap / _BaseColorMap 是 lilToon 内部指向主色的假属性（AAO 表确认）；跳过以免同一贴图重复计数。
            Add(list, m, "_Main2ndTex", ATOTextureKind.Color, uvMode: ReadUVMode(m, "_Main2ndTex_UVMode"));
            Add(list, m, "_Main3rdTex", ATOTextureKind.Color, uvMode: ReadUVMode(m, "_Main3rdTex_UVMode"));
            Add(list, m, "_MainColorAdjustMask", ATOTextureKind.Mask, uvMode: 0);
            Add(list, m, "_Main2ndBlendMask", ATOTextureKind.Mask, uvMode: 0);
            Add(list, m, "_Main3rdBlendMask", ATOTextureKind.Mask, uvMode: 0);
            Add(list, m, "_Main2ndDissolveMask", ATOTextureKind.Mask, uvMode: 0);
            Add(list, m, "_Main2ndDissolveNoiseMask", ATOTextureKind.Mask, uvMode: 0);
            Add(list, m, "_Main3rdDissolveMask", ATOTextureKind.Mask, uvMode: 0);
            Add(list, m, "_Main3rdDissolveNoiseMask", ATOTextureKind.Mask, uvMode: 0);

            Add(list, m, "_BumpMap", ATOTextureKind.NormalMap, uvMode: 0);
            Add(list, m, "_Bump2ndMap", ATOTextureKind.NormalMap, uvMode: 0);
            Add(list, m, "_MetallicGlossMap", ATOTextureKind.Mask, uvMode: 0);
            Add(list, m, "_SmoothnessTex", ATOTextureKind.Mask, uvMode: 0);
            Add(list, m, "_AnisotropyTangentMap", ATOTextureKind.NormalMap, uvMode: 0);

            Add(list, m, "_EmissionMap", ATOTextureKind.Emission, uvMode: ReadUVMode(m, "_EmissionMap_UVMode"));
            Add(list, m, "_Emission2ndMap", ATOTextureKind.Emission, uvMode: ReadUVMode(m, "_Emission2ndMap_UVMode"));
            Add(list, m, "_EmissionGradTex", ATOTextureKind.Emission, uvMode: ReadUVMode(m, "_EmissionMap_UVMode"));
            Add(list, m, "_Emission2ndGradTex", ATOTextureKind.Emission, uvMode: ReadUVMode(m, "_Emission2ndMap_UVMode"));
            Add(list, m, "_EmissionBlendMask", ATOTextureKind.Mask, uvMode: 0);
            Add(list, m, "_Emission2ndBlendMask", ATOTextureKind.Mask, uvMode: 0);

            Add(list, m, "_MatCapTex", ATOTextureKind.Color, uvMode: -1);        // matcap = normal-based (special)
            Add(list, m, "_MatCap2ndTex", ATOTextureKind.Color, uvMode: -1);
            Add(list, m, "_MatCapBumpMap", ATOTextureKind.NormalMap, uvMode: 0);
            Add(list, m, "_MatCap2ndBumpMap", ATOTextureKind.NormalMap, uvMode: 0);
            Add(list, m, "_MatCapBlendMask", ATOTextureKind.Mask, uvMode: 0);
            Add(list, m, "_MatCap2ndBlendMask", ATOTextureKind.Mask, uvMode: 0);

            Add(list, m, "_ShadowColorTex", ATOTextureKind.Color, uvMode: 0);
            Add(list, m, "_Shadow2ndColorTex", ATOTextureKind.Color, uvMode: 0);
            Add(list, m, "_Shadow3rdColorTex", ATOTextureKind.Color, uvMode: 0);
            Add(list, m, "_ShadowBorderMask", ATOTextureKind.Mask, uvMode: 0);
            Add(list, m, "_ShadowBlurMask", ATOTextureKind.Mask, uvMode: 0);
            Add(list, m, "_ShadowStrengthMask", ATOTextureKind.Mask, uvMode: 0);
            Add(list, m, "_RimColorTex", ATOTextureKind.Color, uvMode: 0);
            Add(list, m, "_RimShadeMask", ATOTextureKind.Mask, uvMode: 0);
            Add(list, m, "_OutlineTex", ATOTextureKind.Color, uvMode: 0);
            Add(list, m, "_OutlineWidthMask", ATOTextureKind.Mask, uvMode: 0);
            Add(list, m, "_ReflectionColorTex", ATOTextureKind.Color, uvMode: 0);
            Add(list, m, "_BacklightColorTex", ATOTextureKind.Color, uvMode: 0);
            Add(list, m, "_GlitterColorTex", ATOTextureKind.Color, uvMode: 0);
            Add(list, m, "_FurMask", ATOTextureKind.Mask, uvMode: 0);
            Add(list, m, "_FurLengthMask", ATOTextureKind.Mask, uvMode: 0);
            Add(list, m, "_AudioLinkMask", ATOTextureKind.Mask, uvMode: 0);
            Add(list, m, "_AlphaMask", ATOTextureKind.Mask, uvMode: 0);
            Add(list, m, "_DissolveMask", ATOTextureKind.Mask, uvMode: 0);
            Add(list, m, "_DissolveNoiseMask", ATOTextureKind.Mask, uvMode: 0);
            Add(list, m, "_IDMask1", ATOTextureKind.Mask, uvMode: 0);
            Add(list, m, "_IDMask2", ATOTextureKind.Mask, uvMode: 0);
            Add(list, m, "_IDMask3", ATOTextureKind.Mask, uvMode: 0);
            Add(list, m, "_IDMask4", ATOTextureKind.Mask, uvMode: 0);
            Add(list, m, "_IDMask5", ATOTextureKind.Mask, uvMode: 0);
            Add(list, m, "_IDMask6", ATOTextureKind.Mask, uvMode: 0);
            Add(list, m, "_IDMask7", ATOTextureKind.Mask, uvMode: 0);
            Add(list, m, "_IDMask8", ATOTextureKind.Mask, uvMode: 0);

            // Special usages: parallax / screen-space / color-based / data textures.
            // 特殊用途：视差 / 屏幕空间 / 基于颜色 / 数据贴图。
            AddSpecial(list, m, "_ParallaxMap");
            AddSpecial(list, m, "_DitherTex");
            AddSpecial(list, m, "_MainGradationTex");
            AddSpecial(list, m, "_FurVectorTex");
            AddSpecial(list, m, "_OutlineVectorTex");
            AddSpecial(list, m, "_FurNoiseMask");
            AddSpecial(list, m, "_GlitterShapeTex");
            AddSpecial(list, m, "_AnisotropyShiftNoiseMask");
            AddSpecial(list, m, "_AudioLinkLocalMap");

            // Scroll-rotate flags. 滚动旋转标记。
            foreach (var info in list)
            {
                string sr = info.propertyName + "_ScrollRotate";
                if (m.HasProperty(sr))
                {
                    var v = m.GetVector(sr);
                    info.scrollRotateValue = v;
                    if (Mathf.Abs(v.x) > 1e-4f || Mathf.Abs(v.y) > 1e-4f ||
                        Mathf.Abs(v.z) > 1e-4f || Mathf.Abs(v.w) > 1e-4f)
                        info.hasScrollRotate = true;
                }
                if (info.hasScrollRotate) info.isSpecialUsage = true;
            }
            return list;
        }

        private static int ReadUVMode(Material m, string prop)
        {
            if (m.HasProperty(prop))
            {
                int mode = m.GetInt(prop);
                switch (mode)
                {
                    case 0: return 0;
                    case 1: return 1;
                    case 2: return 2;
                    case 3: return 3;
                    case 4: return -1; // NonMesh
                }
            }
            return 0;
        }

        // ------------------------------------------------------------------ Standard

        private static List<ShaderTextureInfo> AnalyzeStandard(Material m)
        {
            var list = new List<ShaderTextureInfo>
            {
                Make(m, "_MainTex", ATOTextureKind.Color, 0, true),
                Make(m, "_BumpMap", ATOTextureKind.NormalMap, 0, false),
                Make(m, "_MetallicGlossMap", ATOTextureKind.Mask, 0, false),
                Make(m, "_OcclusionMap", ATOTextureKind.Mask, 0, false),
                Make(m, "_EmissionMap", ATOTextureKind.Emission, 0, false),
                Make(m, "_DetailMask", ATOTextureKind.Mask, 0, false),
                Make(m, "_DetailAlbedoMap", ATOTextureKind.Color, 1, false),
                Make(m, "_DetailNormalMap", ATOTextureKind.NormalMap, 1, false),
            };
            list.RemoveAll(x => x == null);
            AddSpecial(list, m, "_ParallaxMap");
            return list;
        }

        // ------------------------------------------------------------------ Generic scan

        private static List<ShaderTextureInfo> AnalyzeGeneric(Material m)
        {
            var shader = m.shader;
            int count = ShaderUtil.GetPropertyCount(shader);
            var list = new List<ShaderTextureInfo>();
            bool hasAny = false;
            for (int i = 0; i < count; i++)
            {
                if (ShaderUtil.GetPropertyType(shader, i) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                string name = ShaderUtil.GetPropertyName(shader, i);
                var attrs = ShaderUtil.GetPropertyAttributes(shader, i);
                if (attrs == null) continue;

                bool normal = false, main = false, hideInInspector = false, noScaleOffset = false;
                foreach (var a in attrs)
                {
                    if (a == "Normal") normal = true;
                    else if (a == "MainTexture") main = true;
                    else if (a == "HideInInspector") hideInInspector = true;
                    else if (a == "NoScaleOffset") noScaleOffset = true;
                }

                var tex = m.GetTexture(name) as Texture2D;
                if (tex == null) continue;
                hasAny = true;

                ATOTextureKind kind;
                if (normal || name.Contains("Bump") || name.Contains("Normal")) kind = ATOTextureKind.NormalMap;
                else if (name.Contains("Mask") || name.Contains("Occlusion") ||
                         name.Contains("Metallic") || name.Contains("Smoothness") || name.Contains("DetailMask"))
                    kind = ATOTextureKind.Mask;
                else kind = ATOTextureKind.Color;

                list.Add(new ShaderTextureInfo
                {
                    propertyName = name,
                    kind = kind,
                    uvChannel = name.Contains("Detail") ? 1 : 0,
                    isSpecialUsage = name.Contains("Parallax"),
                });
            }

            // Generic shaders must at least expose _MainTex-like texture to be considered supported.
            // 通用着色器必须至少暴露一个类似 _MainTex 的贴图才被视为受支持。
            return hasAny ? list : null;
        }

        // ------------------------------------------------------------------ helpers

        private static void Add(List<ShaderTextureInfo> list, Material m, string prop, ATOTextureKind kind, int uvMode, bool isMain = false)
        {
            if (!m.HasProperty(prop)) return;
            var tex = m.GetTexture(prop) as Texture2D;
            if (tex == null) return;
            list.Add(new ShaderTextureInfo
            {
                propertyName = prop,
                kind = kind,
                uvChannel = uvMode,
                isSpecialUsage = uvMode == -1,
            });
        }

        private static void AddSpecial(List<ShaderTextureInfo> list, Material m, string prop)
        {
            if (!m.HasProperty(prop)) return;
            var tex = m.GetTexture(prop) as Texture2D;
            if (tex == null) return;
            list.Add(new ShaderTextureInfo { propertyName = prop, kind = ATOTextureKind.Other, uvChannel = 0, isSpecialUsage = true });
        }

        private static ShaderTextureInfo Make(Material m, string prop, ATOTextureKind kind, int uv, bool isMain)
        {
            if (!m.HasProperty(prop)) return null;
            var tex = m.GetTexture(prop) as Texture2D;
            if (tex == null) return null;
            return new ShaderTextureInfo
            {
                propertyName = prop,
                kind = kind,
                uvChannel = uv,
                isSpecialUsage = false,
            };
        }
    }
}
