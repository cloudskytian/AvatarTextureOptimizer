// Shader property catalogs: curated lilToon 2.3.4 table (extracted from Shader/lts.shader
// and Shader/Includes/lil_common_frag.hlsl - see docs/ThirdPartyNotes.md) + generic
// attribute/name-based analysis for other shaders, designed to also cover future lilToon
// versions whose new properties follow the same naming conventions (_XxxTex + _XxxTex_UVMode etc.).
//
// 着色器属性表：lilToon 2.3.4 精选表（提取自 lts.shader 与 lil_common_frag.hlsl，
// 见 docs/ThirdPartyNotes.md）+ 通用属性/命名启发式分析（兼容遵循同样命名规律的未来 lilToon 版本与其他着色器）。

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace net.fosa.ato.editor
{
    /// <summary>How a texture property samples UVs. / 贴图属性的 UV 采样方式。</summary>
    internal enum UvMode
    {
        Uv0,        // plain uv0 (may be uvMain-based: see uvMainBased) / 普通 uv0
        Uv0Main,    // lilToon uvMain: uv0 through _MainTex_ST + _MainTex_ScrollRotate + _ShiftBackfaceUV
        UvSelector, // explicit channel selector prop (0..3) / 有通道选择属性
        NonMeshUv,  // matcap/rim/LUT/parallax/etc -> special use / 非网格UV -> 特殊用途
    }

    /// <summary>Curated knowledge about one shader texture property. / 单个纹理属性的知识。</summary>
    internal class PropRule
    {
        internal string prop;
        internal TexKind kind;
        internal UvMode uv = UvMode.Uv0Main;
        internal string uvSelectorProp;  // int prop, 0..3 = channel, >=4 non-mesh / 通道选择属性
        internal string scrollProp;      // vector prop; non-zero => transformed / 滚动属性
        internal string angleProp;       // float prop; non-zero => rotated / 旋转属性
        internal string decalProp;       // bool prop; 1 => special use / 贴花标志
        internal bool stChecked = true;  // texture's own ST must be identity / 自身ST须为恒等
        internal string[] grayChannels = { "r" }; // used channels for masks / 蒙版使用通道
    }

    internal static class ShaderCatalog
    {
        // ------------------------------------------------------------------
        // lilToon curated table (verified against source). / lilToon 精选表。
        // ------------------------------------------------------------------
        private static readonly Dictionary<string, PropRule> LilToon = BuildLilToon();

        private static Dictionary<string, PropRule> BuildLilToon()
        {
            var m = new Dictionary<string, PropRule>();

            PropRule R(string prop, TexKind kind, UvMode uv = UvMode.Uv0Main,
                string sel = null, string scroll = null, string angle = null, string decal = null,
                string[] gray = null)
            {
                var r = new PropRule { prop = prop, kind = kind, uv = uv, uvSelectorProp = sel, scrollProp = scroll, angleProp = angle, decalProp = decal };
                if (gray != null) r.grayChannels = gray;
                m[prop] = r;
                return r;
            }

            // ---- main colors / 主色 ----
            R("_MainTex", TexKind.Color); // own ST via LIL_SAMPLE_2D_ST + uvMain
            R("_Main2ndTex", TexKind.Color, UvMode.UvSelector, "_Main2ndTex_UVMode",
                "_Main2ndTex_ScrollRotate", "_Main2ndTexAngle", "_Main2ndTexIsDecal");
            R("_Main3rdTex", TexKind.Color, UvMode.UvSelector, "_Main3rdTex_UVMode",
                "_Main3rdTex_ScrollRotate", "_Main3rdTexAngle", "_Main3rdTexIsDecal");
            R("_OutlineTex", TexKind.Color, UvMode.Uv0Main, null, "_OutlineTex_ScrollRotate");
            R("_ShadowColorTex", TexKind.Color, UvMode.Uv0Main, stChecked: false);
            R("_Shadow2ndColorTex", TexKind.Color, UvMode.Uv0Main, stChecked: false);
            R("_Shadow3rdColorTex", TexKind.Color, UvMode.Uv0Main, stChecked: false);
            R("_BacklightColorTex", TexKind.Color);
            R("_ReflectionColorTex", TexKind.Color);
            R("_RimColorTex", TexKind.Color);
            R("_EmissionMap", TexKind.Color, UvMode.UvSelector, "_EmissionMap_UVMode", "_EmissionMap_ScrollRotate");
            R("_Emission2ndMap", TexKind.Color, UvMode.UvSelector, "_Emission2ndMap_UVMode", "_Emission2ndMap_ScrollRotate");
            R("_BaseColorMap", TexKind.Color); // alias on some custom shaders / 部分着色器别名
            R("_BaseMap", TexKind.Color);

            // ---- normals / 法线 ----
            R("_BumpMap", TexKind.Normal);
            R("_Bump2ndMap", TexKind.Normal, UvMode.UvSelector, "_Bump2ndMap_UVMode");
            R("_OutlineVectorTex", TexKind.Normal, UvMode.Uv0Main);
            R("_AnisotropyTangentMap", TexKind.Normal);

            // ---- grayscale masks (channels verified in lil_common_frag.hlsl) / 灰度蒙版 ----
            R("_MainColorAdjustMask", TexKind.GrayMask, UvMode.Uv0Main, stChecked: false, gray: new[] { "r" });
            R("_AlphaMask", TexKind.GrayMask, UvMode.Uv0Main, stChecked: false, gray: new[] { "r" });
            R("_SmoothnessTex", TexKind.GrayMask, gray: new[] { "r" });
            R("_MetallicGlossMap", TexKind.GrayMask, gray: new[] { "r" });
            R("_ShadowStrengthMask", TexKind.GrayMask, UvMode.Uv0Main, stChecked: false, gray: new[] { "r" });
            R("_ShadowBorderMask", TexKind.GrayMask, UvMode.Uv0Main, stChecked: false, gray: new[] { "r" });
            R("_ShadowBlurMask", TexKind.GrayMask, UvMode.Uv0Main, stChecked: false, gray: new[] { "r" });
            R("_OutlineWidthMask", TexKind.GrayMask, UvMode.Uv0Main, stChecked: false, gray: new[] { "r" });
            R("_RimShadeMask", TexKind.GrayMask, UvMode.Uv0Main, stChecked: false, gray: new[] { "r" });
            R("_Main2ndBlendMask", TexKind.GrayMask, UvMode.UvSelector, "_Main2ndTex_UVMode", stChecked: false, gray: new[] { "r" });
            R("_Main3rdBlendMask", TexKind.GrayMask, UvMode.UvSelector, "_Main3rdTex_UVMode", stChecked: false, gray: new[] { "r" });
            R("_EmissionBlendMask", TexKind.GrayMask, UvMode.UvSelector, "_EmissionMap_UVMode", "_EmissionBlendMask_ScrollRotate", stChecked: false, gray: new[] { "r" });
            R("_Emission2ndBlendMask", TexKind.GrayMask, UvMode.UvSelector, "_Emission2ndMap_UVMode", "_Emission2ndBlendMask_ScrollRotate", stChecked: false, gray: new[] { "r" });
            R("_AudioLinkMask", TexKind.GrayMask, UvMode.UvSelector, "_AudioLinkMask_UVMode", "_AudioLinkMask_ScrollRotate", gray: new[] { "r" });
            R("_DissolveMask", TexKind.GrayMask, UvMode.Uv0Main, stChecked: false, gray: new[] { "r" });
            R("_DissolveNoiseMask", TexKind.GrayMask, UvMode.Uv0Main, scroll: "_DissolveNoiseMask_ScrollRotate", gray: new[] { "r" });
            R("_Main2ndDissolveMask", TexKind.GrayMask, UvMode.UvSelector, "_Main2ndTex_UVMode", stChecked: false, gray: new[] { "r" });
            R("_Main2ndDissolveNoiseMask", TexKind.GrayMask, UvMode.UvSelector, "_Main2ndTex_UVMode", scroll: "_Main2ndDissolveNoiseMask_ScrollRotate", gray: new[] { "r" });
            R("_Main3rdDissolveMask", TexKind.GrayMask, UvMode.UvSelector, "_Main3rdTex_UVMode", stChecked: false, gray: new[] { "r" });
            R("_Main3rdDissolveNoiseMask", TexKind.GrayMask, UvMode.UvSelector, "_Main3rdTex_UVMode", scroll: "_Main3rdDissolveNoiseMask_ScrollRotate", gray: new[] { "r" });
            R("_AnisotropyShiftNoiseMask", TexKind.GrayMask, UvMode.Uv0Main, gray: new[] { "r" });
            R("_AnisotropyScaleMask", TexKind.GrayMask, UvMode.Uv0Main, stChecked: false, gray: new[] { "r" });
            R("_Bump2ndScaleMask", TexKind.GrayMask, UvMode.UvSelector, "_Bump2ndMap_UVMode", stChecked: false, gray: new[] { "r" });

            // ---- special use (non mesh-UV or LUT) -> whitelist / 特殊用途 -> 白名单 ----
            foreach (var p in new[]
            {
                "_MatCapTex", "_MatCap2ndTex", "_MatCapBumpMap", "_MatCap2ndBumpMap",
                "_MatCapBlendMask", "_MatCap2ndBlendMask", // view-space / 视空间
                "_ParallaxMap",                            // parallax / 视差
                "_DitherTex", "_AudioLinkLocalMap",        // fixed utility / 固定用途
                "_Ramp", "_EmissionGradTex", "_Emission2ndGradTex", "_MainGradationTex", // LUT
                "_GlitterColorTex", "_GlitterShapeTex",    // glitter generated UV / 闪烁生成UV
            })
                R(p, TexKind.Special, UvMode.NonMeshUv, stChecked: false);

            return m;
        }

        internal static bool IsLilToon(Shader shader)
        {
            if (shader == null) return false;
            string n = shader.name.ToLowerInvariant();
            if (n.Contains("liltoon") || n.Contains("_lil/")) return true;
            // signature: lilToon-specific props present on shader / 签名属性
            return HasProperty(shader, "_MainTex_ScrollRotate") || HasProperty(shader, "_ShiftBackfaceUV");
        }

        private static bool HasProperty(Shader shader, string name)
        {
            int id = Shader.PropertyToID(name);
            return shader.HasProperty(id);
        }

        /// <summary>Look up a rule for a shader property; null = unknown.
        /// 查询属性规则；null = 未知。</summary>
        internal static PropRule Resolve(Shader shader, string prop)
        {
            if (IsLilToon(shader) && LilToon.TryGetValue(prop, out var rule)) return rule;

            // Generic fallback for non-lilToon shaders & unknown lilToon props:
            // flag- and name-based heuristics. / 通用兜底：标志+命名启发式。
            return ResolveGeneric(shader, prop);
        }

        private static readonly Dictionary<string, PropRule> _genericCache = new Dictionary<string, PropRule>();

        private static PropRule ResolveGeneric(Shader shader, string prop)
        {
            // cached per (shader-instance, prop) via string key / 按着色器+属性缓存
            string key = shader.GetInstanceID() + "|" + prop;
            if (_genericCache.TryGetValue(key, out var cached)) return cached;

            PropRule r = null;
            for (int i = 0; i < shader.GetPropertyCount(); i++)
            {
                if (shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                if (shader.GetPropertyName(i) != prop) continue;

                var flags = shader.GetPropertyFlags(i);
                var dim = shader.GetPropertyTextureDimension(i);
                if (dim != TextureDimension.Tex2D) return Cache(key, null); // cube/3d -> skip / 跳过

                bool isNormal = (flags & ShaderPropertyFlags.Normal) != 0;
                bool isMain = (flags & ShaderPropertyFlags.MainTexture) != 0;
                string low = prop.ToLowerInvariant();

                if (isNormal || low.Contains("bump") || low.Contains("normal"))
                    r = new PropRule { prop = prop, kind = TexKind.Normal, uv = UvMode.Uv0, stChecked = true };
                else if (low.Contains("parallax") || low.Contains("decal"))
                    r = new PropRule { prop = prop, kind = TexKind.Special, uv = UvMode.NonMeshUv, stChecked = false };
                else if (low.Contains("matcap"))
                    r = new PropRule { prop = prop, kind = TexKind.Special, uv = UvMode.NonMeshUv, stChecked = false };
                else if (isMain || low.EndsWith("maintex") || low.EndsWith("albedo")
                         || low.EndsWith("colortex") || low.EndsWith("basecolormap") || low.EndsWith("basemap"))
                    r = new PropRule { prop = prop, kind = TexKind.Color, uv = UvMode.Uv0 };
                else if (low.Contains("mask") || low.Contains("smooth") || low.Contains("metallic")
                         || low.Contains("occlusion") || low.Contains("ao") || low.Contains("rough")
                         || low.Contains("specular"))
                {
                    var gray = low.Contains("metallic") || low.Contains("occlusion")
                        ? new[] { "r", "g", "b", "a" } // content-decided later / 内容兜底
                        : new[] { "r" };
                    r = new PropRule { prop = prop, kind = TexKind.GrayMask, uv = UvMode.Uv0, grayChannels = gray };
                }
                else
                    r = new PropRule { prop = prop, kind = TexKind.Color, uv = UvMode.Uv0 };

                break;
            }

            return Cache(key, r);
        }

        private static PropRule Cache(string key, PropRule r)
        {
            _genericCache[key] = r;
            return r;
        }

        internal static void ClearCache() => _genericCache.Clear();
    }
}
