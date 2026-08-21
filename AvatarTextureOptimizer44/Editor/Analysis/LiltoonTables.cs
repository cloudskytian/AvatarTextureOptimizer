// LiltoonTables.cs - lilToon texture property table, transcribed from lilToon 2.3.4 shader sources
// (lil_common_frag.hlsl: uvMain = lilCalcUV(uv0, _MainTex_ST, _MainTex_ScrollRotate) with _ShiftBackfaceUV)
// and cross-checked against AvatarOptimizer's LiltoonShaderInformation (a faithful transcription).
// lilToon 纹理属性表：转录自 lilToon 2.3.4 着色器源码并与 AAO 的忠实实现交叉核对。
//
// Eligibility rule / 合格规则:
//  - sampled by a mesh UV channel (UVMain==UV0 chain or an explicit channel via *_UVMode int property)
//    with identity ST (no scale/offset/scroll/rotate/decal) -> eligible for atlasing
//  - NonMesh UV (matcap/screen/color/LUT) or any transform -> ineligible (whitelist + warning)
// 以网格UV通道采样且ST为单位变换->可图集化；非网格UV或存在变换->不合格（白名单+警告）。
using System;
using System.Collections.Generic;
using UnityEngine;
using Fosa.ATO.Runtime;

namespace Fosa.ATO.Editor.Analysis
{
    /// <summary>Sampling description of one texture property on one material. / 单材质上单个贴图属性的采样描述。</summary>
    public sealed class TexturePropInfo
    {
        public string prop;
        public Texture2D texture;
        public ATOTextureRole role;
        /// <summary>Mesh UV channel, -1 = non-mesh / unknown -> ineligible. / 网格UV通道；-1=非网格或未知->不合格。</summary>
        public int uvChannel = 0;
        /// <summary>Eligible for atlas optimization? / 是否可参与图集优化？</summary>
        public bool eligible = true;
        /// <summary>Why not eligible / 不合格原因 (for warning / 用于警告)。</summary>
        public string reason = "";
        /// <summary>Gate property off -> texture never sampled. / 功能开关关闭->贴图从未被采样。</summary>
        public bool sampled = true;
    }

    /// <summary>lilToon property analysis. / lilToon属性分析。</summary>
    public static class LiltoonTables
    {
        /// <summary>Is this a lilToon shader? / 是否为lilToon着色器？</summary>
        public static bool IsLiltoon(Shader s)
            => s != null && s.name.Contains("lilToon");

        /// <summary>Guard: returns eligibility, sets a reason when refused. / 守卫：返回合格性，拒绝时给出原因。</summary>
        private delegate bool Guard(Material m, string prop, out string why);

        private enum UvSrc { UvMain, Fixed0, Fixed1, Fixed2, Fixed3, ByIntProp, NonMesh }

        private sealed class Entry
        {
            public string prop; public ATOTextureRole role; public UvSrc uv;
            public string gate;      // int prop that must be nonzero / 必须非0的开关
            public string uvModeProp;// channel-select int prop / 通道选择int属性
            public bool ownSt;       // verify own _ST identity / 校验自身_ST单位性
            public Guard guard;      // extra eligibility guard / 附加守卫
        }

        private static readonly Entry[] Table = BuildTable();

        private static readonly Guard G_UvMain = (Material m, string p, out string w) =>
        {
            w = null;
            if (m.HasProperty("_ShiftBackfaceUV") && m.GetFloat("_ShiftBackfaceUV") != 0f) { w = "_ShiftBackfaceUV on / backface UV shift enabled"; return false; }
            var sr = m.HasProperty("_MainTex_ScrollRotate") ? m.GetVector("_MainTex_ScrollRotate") : Vector4.zero;
            if (sr != Vector4.zero) { w = "main scroll/rotate / main color scroll rotation"; return false; }
            return true;
        };

        private static readonly Guard G_Decal = (Material m, string p, out string w) =>
        {
            w = null;
            foreach (var suf in new[] { "Angle", "IsDecal", "IsLeftOnly", "IsRightOnly", "ShouldCopy", "ShouldFlipMirror", "ShouldFlipCopy", "IsMSDF" })
            {
                if (m.HasProperty(p + suf) && m.GetFloat(p + suf) != 0f) { w = p + suf + " on / decal-family transform enabled"; return false; }
            }
            var da = m.HasProperty(p + "DecalAnimation") ? m.GetVector(p + "DecalAnimation") : new Vector4(1, 1, 1, 30);
            if (da != new Vector4(1, 1, 1, 30)) { w = "decal animation / decal animation"; return false; }
            var sr = m.HasProperty(p + "_ScrollRotate") ? m.GetVector(p + "_ScrollRotate") : Vector4.zero;
            if (sr != Vector4.zero) { w = "scroll/rotate / scroll rotation"; return false; }
            return true;
        };

        private static readonly Guard G_ScrollRotated = (Material m, string p, out string w) =>
        {
            w = null;
            var sr = m.HasProperty(p + "_ScrollRotate") ? m.GetVector(p + "_ScrollRotate") : Vector4.zero;
            if (sr != Vector4.zero) { w = "scroll/rotate / scroll rotation"; return false; }
            return true;
        };

        private static readonly Guard G_EmissionParallax = (Material m, string p, out string w) =>
        {
            if (m.HasProperty("_EmissionParallaxDepth") && m.GetFloat("_EmissionParallaxDepth") != 0f)
            { w = "emission parallax / emission parallax"; return false; }
            if (!STIdentity(m, p)) { w = "ST scale/offset changed / scale/offset transform exists"; return false; }
            return NoScroll(m, p, out w);
        };

        private static readonly Guard G_Emission2Parallax = (Material m, string p, out string w) =>
        {
            if (m.HasProperty("_Emission2ndParallaxDepth") && m.GetFloat("_Emission2ndParallaxDepth") != 0f)
            { w = "emission2 parallax / emission2 parallax"; return false; }
            if (!STIdentity(m, p)) { w = "ST scale/offset changed / scale/offset transform exists"; return false; }
            return NoScroll(m, p, out w);
        };

        private static readonly Guard G_AudioLinkLocal = (Material m, string p, out string w) =>
        {
            w = null;
            if (m.HasProperty("_AudioLinkAsLocal") && m.GetFloat("_AudioLinkAsLocal") != 0f) { w = "audiolink local mode / local audio mode"; return false; }
            return true;
        };

        private static readonly Guard G_Never = (Material m, string p, out string w) =>
        { w = "view-dependent sampling / view-dependent sampling"; return false; };

        private static Entry E(string prop, ATOTextureRole role, UvSrc uv, string gate = null, string uvMode = null, bool ownSt = false, Guard guard = null)
            => new Entry { prop = prop, role = role, uv = uv, gate = gate, uvModeProp = uvMode, ownSt = ownSt, guard = guard };

        private static Entry[] BuildTable() => new Entry[]
        {
            // main / main color
            E("_MainTex", ATOTextureRole.MainColor, UvSrc.UvMain, guard: G_UvMain),
            E("_BaseMap", ATOTextureRole.MainColor, UvSrc.UvMain, guard: G_UvMain),
            E("_BaseColorMap", ATOTextureRole.MainColor, UvSrc.UvMain, guard: G_UvMain),
            E("_MainColorAdjustMask", ATOTextureRole.Mask, UvSrc.UvMain, guard: G_UvMain),
            E("_AlphaMask", ATOTextureRole.Mask, UvSrc.UvMain, null, null, true, G_UvMain),
            // 2nd/3rd layers / 2nd and 3rd layers
            E("_Main2ndTex", ATOTextureRole.MainColor, UvSrc.ByIntProp, "_UseMain2ndTex", "_Main2ndTex_UVMode", true, G_Decal),
            E("_Main3rdTex", ATOTextureRole.MainColor, UvSrc.ByIntProp, "_UseMain3rdTex", "_Main3rdTex_UVMode", true, G_Decal),
            E("_Main2ndBlendMask", ATOTextureRole.Mask, UvSrc.UvMain, "_UseMain2ndTex", null, false, G_UvMain),
            E("_Main3rdBlendMask", ATOTextureRole.Mask, UvSrc.UvMain, "_UseMain3rdTex", null, false, G_UvMain),
            E("_Main2ndDissolveMask", ATOTextureRole.Mask, UvSrc.Fixed0, "_UseMain2ndTex", null, true, null),
            E("_Main2ndDissolveNoiseMask", ATOTextureRole.Mask, UvSrc.Fixed0, "_UseMain2ndTex", null, true, G_ScrollRotated),
            E("_Main3rdDissolveMask", ATOTextureRole.Mask, UvSrc.Fixed0, "_UseMain3rdTex", null, true, null),
            E("_Main3rdDissolveNoiseMask", ATOTextureRole.Mask, UvSrc.Fixed0, "_UseMain3rdTex", null, true, G_ScrollRotated),
            // normal / normal
            E("_BumpMap", ATOTextureRole.Normal, UvSrc.UvMain, "_UseBumpMap", null, true, G_UvMain),
            E("_Bump2ndMap", ATOTextureRole.Normal, UvSrc.ByIntProp, "_UseBump2ndMap", "_Bump2ndMap_UVMode", true, null),
            E("_Bump2ndScaleMask", ATOTextureRole.Mask, UvSrc.UvMain, "_UseBump2ndMap", null, true, G_UvMain),
            // anisotropy / anisotropy
            E("_AnisotropyTangentMap", ATOTextureRole.Data, UvSrc.UvMain, "_UseAnisotropy", null, true, G_UvMain),
            E("_AnisotropyScaleMask", ATOTextureRole.Mask, UvSrc.UvMain, "_UseAnisotropy", null, true, G_UvMain),
            E("_AnisotropyShiftNoiseMask", ATOTextureRole.Mask, UvSrc.UvMain, "_UseAnisotropy", null, true, G_UvMain),
            // backlight / shadow / backlight and shadow
            E("_BacklightColorTex", ATOTextureRole.MainColor, UvSrc.UvMain, "_UseBacklight", null, true, G_UvMain),
            E("_ShadowStrengthMask", ATOTextureRole.Mask, UvSrc.UvMain, "_UseShadow", null, true, G_UvMain),
            E("_ShadowBorderMask", ATOTextureRole.Mask, UvSrc.UvMain, "_UseShadow", null, true, G_UvMain),
            E("_ShadowBlurMask", ATOTextureRole.Mask, UvSrc.UvMain, "_UseShadow", null, true, G_UvMain),
            E("_ShadowColorTex", ATOTextureRole.MainColor, UvSrc.UvMain, "_UseShadow", null, false, G_UvMain),
            E("_Shadow2ndColorTex", ATOTextureRole.MainColor, UvSrc.UvMain, "_UseShadow", null, false, G_UvMain),
            E("_Shadow3rdColorTex", ATOTextureRole.MainColor, UvSrc.UvMain, "_UseShadow", null, false, G_UvMain),
            E("_RimShadeMask", ATOTextureRole.Mask, UvSrc.UvMain, "_UseRimShade", null, false, G_UvMain),
            // reflection / reflection
            E("_SmoothnessTex", ATOTextureRole.Mask, UvSrc.UvMain, "_UseReflection", null, true, G_UvMain),
            E("_MetallicGlossMap", ATOTextureRole.Mask, UvSrc.UvMain, "_UseReflection", null, true, G_UvMain),
            E("_ReflectionColorTex", ATOTextureRole.MainColor, UvSrc.UvMain, "_UseReflection", null, true, G_UvMain),
            // matcap / matcap
            E("_MatCapTex", ATOTextureRole.MatCap, UvSrc.NonMesh, "_UseMatCap", null, false, null),
            E("_MatCapBlendMask", ATOTextureRole.Mask, UvSrc.UvMain, "_UseMatCap", null, true, G_UvMain),
            E("_MatCapBumpMap", ATOTextureRole.Normal, UvSrc.UvMain, "_UseMatCap", null, true, G_UvMain),
            E("_MatCap2ndTex", ATOTextureRole.MatCap, UvSrc.NonMesh, "_UseMatCap2nd", null, false, null),
            E("_MatCap2ndBlendMask", ATOTextureRole.Mask, UvSrc.UvMain, "_UseMatCap2nd", null, true, G_UvMain),
            E("_MatCap2ndBumpMap", ATOTextureRole.Normal, UvSrc.UvMain, "_UseMatCap2nd", null, true, G_UvMain),
            // rim / glitter / rim light and glitter
            E("_RimColorTex", ATOTextureRole.MainColor, UvSrc.UvMain, "_UseRim", null, true, G_UvMain),
            E("_GlitterColorTex", ATOTextureRole.MainColor, UvSrc.UvMain, "_UseGlitter", null, true, G_UvMain),
            E("_GlitterShapeTex", ATOTextureRole.MatCap, UvSrc.NonMesh, "_UseGlitter", null, false, null),
            // emission / emission
            E("_EmissionMap", ATOTextureRole.Emission, UvSrc.ByIntProp, "_UseEmission", "_EmissionMap_UVMode", true, G_EmissionParallax),
            E("_EmissionBlendMask", ATOTextureRole.Mask, UvSrc.UvMain, "_UseEmission", null, true, G_UvMain),
            E("_Emission2ndMap", ATOTextureRole.Emission, UvSrc.ByIntProp, "_UseEmission2nd", "_Emission2ndMap_UVMode", true, G_Emission2Parallax),
            E("_Emission2ndBlendMask", ATOTextureRole.Mask, UvSrc.UvMain, "_UseEmission2nd", null, true, G_UvMain),
            E("_EmissionGradTex", ATOTextureRole.MatCap, UvSrc.NonMesh, "_UseEmission", null, false, null),
            E("_Emission2ndGradTex", ATOTextureRole.MatCap, UvSrc.NonMesh, "_UseEmission2nd", null, false, null),
            // parallax / audio / dissolve / fur / parallax, audio, dissolve, fur
            E("_ParallaxMap", ATOTextureRole.Data, UvSrc.Fixed0, "_UseParallax", null, true, G_Never),
            E("_AudioLinkMask", ATOTextureRole.Mask, UvSrc.ByIntProp, "_UseAudioLink", "_AudioLinkMask_UVMode", true, G_AudioLinkLocal),
            E("_AudioLinkLocalMap", ATOTextureRole.MatCap, UvSrc.NonMesh, "_UseAudioLink", null, false, null),
            E("_DissolveMask", ATOTextureRole.Mask, UvSrc.Fixed0, null, null, true, null),
            E("_DissolveNoiseMask", ATOTextureRole.Mask, UvSrc.Fixed0, null, null, true, G_ScrollRotated),
            E("_FurNoiseMask", ATOTextureRole.Mask, UvSrc.Fixed0, null, null, true, null),
            E("_FurMask", ATOTextureRole.Mask, UvSrc.UvMain, null, null, true, G_UvMain),
            E("_FurLengthMask", ATOTextureRole.Mask, UvSrc.UvMain, null, null, true, G_UvMain),
            E("_FurVectorTex", ATOTextureRole.Data, UvSrc.UvMain, null, null, true, G_UvMain),
            // outline / outline
            E("_OutlineTex", ATOTextureRole.MainColor, UvSrc.UvMain, null, null, false, G_UvMain),
            E("_OutlineWidthMask", ATOTextureRole.Mask, UvSrc.UvMain, null, null, false, G_UvMain),
            E("_OutlineVectorTex", ATOTextureRole.Data, UvSrc.ByIntProp, null, "_OutlineVectorUVMode", false, null),
            // always non-mesh / always non-mesh UV
            E("_DitherTex", ATOTextureRole.MatCap, UvSrc.NonMesh, null, null, false, null),          // screen space / screen space
            E("_MainGradationTex", ATOTextureRole.MatCap, UvSrc.NonMesh, null, null, false, null),   // color LUT / color LUT
        };

        /// <summary>Analyze one material; returns null when not lilToon. / 分析单个材质；非lilToon返回null。</summary>
        public static List<TexturePropInfo> Analyze(Material mat, AvatarScan scan, string rendererPath)
        {
            if (mat == null || !IsLiltoon(mat.shader)) return null;
            var result = new List<TexturePropInfo>();
            foreach (var e in Table)
            {
                if (!mat.HasProperty(e.prop)) continue;
                var tex = mat.GetTexture(e.prop) as Texture2D;
                if (tex == null) continue;
                var info = new TexturePropInfo { prop = e.prop, texture = tex, role = e.role };

                // gate / 开关
                if (e.gate != null && mat.HasProperty(e.gate) && mat.GetFloat(e.gate) == 0f)
                { info.sampled = false; info.eligible = false; info.reason = "feature off / 功能未开启"; }

                // UV channel / UV通道
                switch (e.uv)
                {
                    case UvSrc.NonMesh:
                        info.uvChannel = -1; info.eligible = false;
                        info.reason = "non-mesh UV (matcap/screen/LUT) / 非网格UV（matcap/屏幕/查找表）";
                        break;
                    case UvSrc.UvMain:
                    case UvSrc.Fixed0: info.uvChannel = 0; break;
                    case UvSrc.Fixed1: info.uvChannel = 1; break;
                    case UvSrc.Fixed2: info.uvChannel = 2; break;
                    case UvSrc.Fixed3: info.uvChannel = 3; break;
                    case UvSrc.ByIntProp:
                    {
                        int mode = mat.HasProperty(e.uvModeProp) ? (int)mat.GetFloat(e.uvModeProp) : 0;
                        if (mode >= 0 && mode <= 3) info.uvChannel = mode;
                        else { info.uvChannel = -1; info.eligible = false; info.reason = $"UVMode={mode} non-mesh / 非网格UV"; }
                        break;
                    }
                }

                if (info.eligible && e.ownSt && !STIdentity(mat, e.prop))
                { info.eligible = false; info.reason = "ST scale/offset changed / 存在平移缩放"; }

                if (info.eligible && IsAnimatedTransform(scan, rendererPath, e.prop))
                { info.eligible = false; info.reason = "ST/scroll animated / 动画修改ST/滚动"; }

                if (info.eligible && e.guard != null && !e.guard(mat, e.prop, out string why))
                { info.eligible = false; info.reason = why; }

                result.Add(info);
            }
            return result;
        }

        /// <summary>Material-level ST is scale(1) offset(0)? / 材质级ST是否为单位？</summary>
        public static bool STIdentity(Material m, string prop)
            => m.GetTextureScale(prop) == Vector2.one && m.GetTextureOffset(prop) == Vector2.zero;

        private static bool NoScroll(Material m, string p, out string why)
        {
            why = null;
            var sr = m.HasProperty(p + "_ScrollRotate") ? m.GetVector(p + "_ScrollRotate") : Vector4.zero;
            if (sr != Vector4.zero) { why = "scroll/rotate / 滚动旋转"; return false; }
            return true;
        }

        /// <summary>Any animation moves this texture's ST/scroll/angle on the renderer? / 动画是否修改此贴图ST/滚动/角度？</summary>
        public static bool IsAnimatedTransform(AvatarScan scan, string rendererPath, string prop)
        {
            foreach (var kv in scan.floatProps)
            {
                if (kv.Key.path != rendererPath) continue;
                string p = kv.Key.prop;
                if (p.Contains(prop + "_ST") || p.Contains(prop + "_ScrollRotate") || p.EndsWith(prop + "Angle"))
                    return true;
            }
            return false;
        }
    }
}
