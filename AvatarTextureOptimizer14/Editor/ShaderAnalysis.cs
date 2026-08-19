// ShaderAnalysis — shader property analysis & safety guards / 着色器属性分析与安全守卫
// lilToon rules derived from liltoon-2.3.4 sources (Default.lilblock / lilMaterialProperties.cs):
// generic companion-prop pattern "X" ⇔ "X_ScrollRotate", "X_UVMode", "XIsDecal" -> future-proof.<br>
// lilToon 规则取自 liltoon-2.3.4 源码；通用伴生属性模式（X_ScrollRotate / X_UVMode / XIsDecal）可兼容未来版本。
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Fosa.ATO.Editor
{
    internal static class ShaderAnalysis
    {
        // structured unsafe reason codes / 结构化不安全原因码
        internal const string R_ST = "ST";
        internal const string R_ROT = "ROT";
        internal const string R_UVMODE = "UVMODE";
        internal const string R_DECAL = "DECAL";
        internal const string R_SPECIAL = "SPECIAL";
        internal const string R_UNKNOWN = "UNKNOWN";
        internal const string R_SHIFT = "SHIFT";
        internal const string R_ANIM = "ANIM";
        internal const string R_USER = "USER";

        internal sealed class SlotAnalysis
        {
            internal string property;
            internal TexClass cls;
            internal int uvChannel;
            internal bool safe;
            internal string unsafeReason = "";
            internal string code = "";                 // structured reason (R_*) / 结构化原因码
            internal int maskChannelFlags = 0xF;
        }

        /// <summary>Analyze one material; null-material → empty. / 分析一个材质的全部贴图槽。</summary>
        internal static List<SlotAnalysis> Analyze(Material mat)
        {
            var output = new List<SlotAnalysis>();
            if (mat == null || mat.shader == null) return output;

            // 3rd-party analyzers first (extension point) / 第三方分析器优先
            foreach (var a in ATOShaderAnalyzerRegistry.Custom)
            {
                try
                {
                    if (a.CanAnalyze(mat.shader))
                    {
                        var tmp = new List<ATOTextureSlot>();
                        a.Analyze(mat, tmp);
                        foreach (var t in tmp)
                        {
                            output.Add(new SlotAnalysis
                            {
                                property = t.property, uvChannel = t.uvChannel,
                                cls = (TexClass)(int)t.cls, safe = t.safe, unsafeReason = t.unsafeReason ?? "",
                                maskChannelFlags = t.maskChannelFlags == 0 ? 0xF : t.maskChannelFlags,
                            });
                        }
                        ApplyGuards(mat, output);
                        return output;
                    }
                }
                catch (Exception e) { ATOLog.Warn($"custom analyzer failed on {mat.shader.name}: {e.Message}"); }
            }

            if (IsLilToon(mat)) LilToonAnalyze(mat, output);
            else if (LooksStandard(mat)) StandardAnalyze(mat, output);
            else UnknownAnalyze(mat, output);

            ApplyGuards(mat, output);
            return output;
        }

        // ---------------------------------------------------------------- lilToon
        internal static bool IsLilToon(Material m)
        {
            var n = m.shader.name;
            return n.IndexOf("liltoon", StringComparison.OrdinalIgnoreCase) >= 0
                   || (m.HasProperty("_MainTexHSVG") && m.HasProperty("_TransparentMode"));
        }

        // Safe name tables (from lilToon property sources); everything else is unsafe. / 安全名称表（源自lilToon源码）
        private static void LilToonAnalyze(Material m, List<SlotAnalysis> output)
        {
            bool shiftBackface = m.HasProperty("_ShiftBackfaceUV") && m.GetFloat("_ShiftBackfaceUV") != 0f;

            foreach (TexProp p in TextureProps(m))
            {
                var name = p.name;
                var s = new SlotAnalysis { property = name };

                if (name == "_MainTex") { s.cls = TexClass.Albedo; s.uvChannel = 0; s.safe = !shiftBackface; s.unsafeReason = shiftBackface ? "ShiftBackfaceUV" : ""; }
                else if (name == "_BumpMap" || name == "_Bump2ndMap" || name == "_AnisotropyTangentMap") { s.cls = TexClass.Normal; s.safe = true; }
                else if (name == "_EmissionMap" || name == "_Emission2ndMap") { s.cls = TexClass.Albedo; s.safe = true; } // opaque-colored plane / 视同不透明彩色平面
                else if (IsPlainMaskName(name)) { s.cls = TexClass.Mask; s.safe = true; s.maskChannelFlags = 0xF; } // evaluated on worst channel (safe) / 逐通道取最差
                else { s.cls = TexClass.Mask; s.safe = false; s.unsafeReason = "special/unknown lilToon slot"; s.code = R_SPECIAL; } // MatCap/Rim/AudioLink/Dissolve… 特殊用途
                output.Add(s);
            }
        }

        private static bool IsPlainMaskName(string n)
        {
            if (n.EndsWith("BlendMask", StringComparison.Ordinal)) return true;
            if (n.EndsWith("Mask", StringComparison.Ordinal))
            {
                // exclude obviously transformed/procedural masks / 排除明确带变换或程序化语义的
                string[] deny = { "DistanceFade", "AudioLink", "Dissolve", "Glitter", "Gradation" };
                foreach (var d in deny) if (n.IndexOf(d, StringComparison.OrdinalIgnoreCase) >= 0) return false;
                return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- standard keywords & friends
        private static bool LooksStandard(Material m)
        {
            var n = m.shader.name;
            if (n.IndexOf("Standard", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (n.IndexOf("Universal Render Pipeline", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (n.IndexOf("/Lit", StringComparison.OrdinalIgnoreCase) >= 0 && m.HasProperty("_BaseMap")) return true;
            if (m.HasProperty("_Mode") && m.HasProperty("_MainTex")) return true;
            return false;
        }

        private static void StandardAnalyze(Material m, List<SlotAnalysis> output)
        {
            foreach (TexProp p in TextureProps(m))
            {
                var name = p.name;
                var s = new SlotAnalysis { property = name };
                switch (name)
                {
                    case "_MainTex": case "_BaseMap": case "_BaseColorMap":
                        s.cls = TexClass.Albedo; s.safe = true; break;
                    case "_BumpMap":
                        s.cls = TexClass.Normal; s.safe = true; break;
                    case "_MetallicGlossMap":
                        s.cls = TexClass.Mask; s.safe = true; s.maskChannelFlags = (1 << 0) | (1 << 3); break; // R + A
                    case "_SpecGlossMap":
                        s.cls = TexClass.Mask; s.safe = true; s.maskChannelFlags = 0xF; break; // rgb + a
                    case "_OcclusionMap":
                        s.cls = TexClass.Mask; s.safe = true; s.maskChannelFlags = 1 << 1; break; // G
                    case "_EmissionMap":
                        s.cls = TexClass.Albedo; s.safe = true; break;
                    case "_DetailMask":
                        s.cls = TexClass.Mask; s.safe = true; s.maskChannelFlags = 1 << 0; break;
                    default:
                        s.safe = false; s.unsafeReason = "unrecognized standard slot"; s.code = R_SPECIAL; break; // DetailAlbedo 等有独立ST/平铺语义
                }
                output.Add(s);
            }
        }

        // ---------------------------------------------------------------- unknown shaders → whitelist all
        private static void UnknownAnalyze(Material m, List<SlotAnalysis> output)
        {
            foreach (TexProp p in TextureProps(m))
            {
                output.Add(new SlotAnalysis
                {
                    property = p.name, safe = false,
                    unsafeReason = "unknown shader (cannot prove UV safety) / 未知着色器，无法证明UV安全",
                    code = R_UNKNOWN,
                });
            }
        }

        // ---------------------------------------------------------------- shared guards
        /// <summary>
        /// Apply ST / scroll-rotate / UV-mode / decal guards. Any violation → slot unsafe.<br/>
        /// 应用 ST/滚动旋转/UV模式/贴花守卫；任一违反 → 该槽按白名单处理。
        /// </summary>
        private static void ApplyGuards(Material m, List<SlotAnalysis> slots)
        {
            foreach (var s in slots)
            {
                if (!s.safe) continue;
                var prop = s.property;

                var sc = m.GetTextureScale(prop);
                var of = m.GetTextureOffset(prop);
                if (Mathf.Abs(sc.x - 1f) > 1e-6f || Mathf.Abs(sc.y - 1f) > 1e-6f || of.sqrMagnitude > 1e-9f)
                { s.safe = false; s.unsafeReason = "non-identity ST"; s.code = R_ST; continue; }

                string sr = prop + "_ScrollRotate";
                if (m.HasProperty(sr) && m.GetVector(sr) != Vector4.zero)
                { s.safe = false; s.unsafeReason = "scroll/rotate"; s.code = R_ROT; continue; }

                string uvm = prop + "_UVMode";
                if (m.HasProperty(uvm))
                {
                    int v = Mathf.RoundToInt(m.GetFloat(uvm));
                    if (v < 0 || v > 7) { s.safe = false; s.unsafeReason = "UVMode " + v; s.code = R_UVMODE; continue; }
                    s.uvChannel = v; // multi-UV support / 多通道UV支持
                }

                if (m.HasProperty(prop + "IsDecal") && m.GetFloat(prop + "IsDecal") != 0f)
                { s.safe = false; s.unsafeReason = "decal"; s.code = R_DECAL; continue; }

                if (prop.IndexOf("Decal", StringComparison.OrdinalIgnoreCase) >= 0)
                { s.safe = false; s.unsafeReason = "decal"; s.code = R_DECAL; continue; }
            }
        }

        /// <summary>Alpha semantics of a material for Albedo evaluation. / 材质的主色透明语义（含动画取值集合由发现阶段合并）。</summary>
        internal static AlphaMode GetAlphaMode(Material m, out float cutoff)
        {
            cutoff = m.HasProperty("_Cutoff") ? m.GetFloat("_Cutoff") : 0.5f;
            if (IsLilToon(m))
            {
                int v = m.HasProperty("_TransparentMode") ? Mathf.RoundToInt(m.GetFloat("_TransparentMode")) : 0;
                return v switch { 0 => AlphaMode.Opaque, 1 => AlphaMode.Cutout, _ => AlphaMode.Blend };
            }
            if (m.HasProperty("_Mode"))
            {
                int v = Mathf.RoundToInt(m.GetFloat("_Mode"));
                return v switch { 0 => AlphaMode.Opaque, 1 => AlphaMode.Cutout, _ => AlphaMode.Blend };
            }
            if (m.HasProperty("_Surface"))
            {
                // URP: _Surface 0 opaque 1 transparent; _AlphaClip toggles cutout. / URP 判定
                bool clip = m.HasProperty("_AlphaClip") && m.GetFloat("_AlphaClip") > 0.5f;
                bool transp = m.GetFloat("_Surface") > 0.5f;
                if (transp) return AlphaMode.Blend;
                return clip ? AlphaMode.Cutout : AlphaMode.Opaque;
            }
            if (m.IsKeywordEnabled("_ALPHATEST_ON")) return AlphaMode.Cutout;
            if (m.IsKeywordEnabled("_ALPHABLEND_ON") || m.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON")) return AlphaMode.Blend;
            return AlphaMode.Opaque;
        }

        /// <summary>Map an animated render-mode float value to AlphaMode for a material. / 动画 render mode 取值映射。</summary>
        internal static AlphaMode ModeValueToAlpha(Material m, float v)
        {
            if (IsLilToon(m) || m.HasProperty("_Mode")) { int i = Mathf.RoundToInt(v); return i == 0 ? AlphaMode.Opaque : i == 1 ? AlphaMode.Cutout : AlphaMode.Blend; }
            if (m.HasProperty("_Surface")) return v > 0.5f ? AlphaMode.Blend : (m.HasProperty("_AlphaClip") && m.GetFloat("_AlphaClip") > 0.5f ? AlphaMode.Cutout : AlphaMode.Opaque);
            return AlphaMode.Blend; // conservative / 保守
        }

        // ---------------------------------------------------------------- utilities
        internal struct TexProp { internal string name; }

        internal static IEnumerable<TexProp> TextureProps(Material m)
        {
            var sh = m.shader;
            int count = ShaderUtil.GetPropertyCount(sh);
            for (int i = 0; i < count; i++)
            {
                if (ShaderUtil.GetPropertyType(sh, i) == ShaderUtil.ShaderPropertyType.TexEnv)
                    yield return new TexProp { name = ShaderUtil.GetPropertyName(sh, i) };
            }
        }

        /// <summary>Property names animated on this texture would break UV safety. / 被动画触碰即破坏UV安全的属性名集合。</summary>
        internal static bool IsUvGuardProperty(string baseProp, string animatedProp)
        {
            if (!animatedProp.Contains("_ST") && !animatedProp.Contains("ScrollRotate") && !animatedProp.Contains("UVMode")
                && !animatedProp.Contains("Decal") && !animatedProp.Contains("_ShiftBackfaceUV")) return false;
            if (animatedProp == "_ShiftBackfaceUV") return baseProp == "_MainTex";
            return animatedProp == baseProp + "_ST" || animatedProp.StartsWith(baseProp + "_ScrollRotate", StringComparison.Ordinal)
                   || animatedProp == baseProp + "IsDecal" || animatedProp.StartsWith(baseProp + "_UVMode", StringComparison.Ordinal);
        }
    }
}
