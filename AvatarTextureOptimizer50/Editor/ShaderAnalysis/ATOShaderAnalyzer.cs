// -----------------------------------------------------------------------------
// ATOShaderAnalyzer.cs — per-material texture usage analysis (lilToon + generic).
// ATOShaderAnalyzer.cs — 逐材质的贴图使用分析（lilToon + 通用）。
//
// A material is either "supported" (every texture use is understood) or unsupported
// (all its textures get whitelisted with a warning, per spec). Unknown individual
// properties inside a supported shader are conservatively whitelisted.
// 材质要么"受支持"（每个贴图用途都被理解），要么"不受支持"（其全部贴图白名单+警告）。
// 受支持着色器内的未知属性一律保守白名单。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace net.fosa.ato.editor
{
    /// <summary>One analyzed texture use / 一条分析出的贴图使用。</summary>
    internal sealed class TextureUse
    {
        public string property;
        public Texture2D texture;
        public TexRole role;
        public int uvChannel;          // -1 = not mesh UV / 非网格UV
        public bool transformed;       // ST/scroll/angle → unusable for remap / 存在变换→不可重映射
        public string note;            // whitelist reason when unusable / 不可用时作为白名单原因
    }

    /// <summary>Result of analyzing one material / 单材质分析结果。</summary>
    internal sealed class MaterialAnalysis
    {
        public Material material;
        public bool supported = true;
        public string unsupportedReason = "";
        public readonly List<TextureUse> uses = new List<TextureUse>();

        public AlphaMode alphaMode = AlphaMode.Opaque;
        /// <summary>All cutoff values seen (incl. animated key values) / 出现过的全部 cutoff（含动画键值）。</summary>
        public readonly SortedSet<float> cutoffs = new SortedSet<float>();
        /// <summary>Animation changes alpha-related props → strictest evaluation.
        /// 动画修改了 alpha 相关属性 → 按最严苛评估。</summary>
        public bool alphaAmbiguous;

        public Texture2D MainTexture
        {
            get
            {
                var u = uses.FirstOrDefault(x => x.role == TexRole.Main && !x.transformed);
                return u?.texture;
            }
        }
    }

    internal static class ATOShaderAnalyzer
    {
        /// <summary>Analyze one material. Never throws; worst case → unsupported.
        /// 分析单个材质。绝不抛异常；最坏情况标记为不受支持。</summary>
        public static MaterialAnalysis Analyze(Material m)
        {
            var result = new MaterialAnalysis { material = m };
            if (m == null || m.shader == null)
            {
                result.supported = false;
                result.unsupportedReason = "null material/shader";
                return result;
            }

            var shader = m.shader;
            var shaderName = shader.name;

            result.alphaMode = ATOShaderRules.GuessAlphaMode(m);
            ATOShaderRules.CollectCutoffs(m, result.cutoffs);

            try
            {
                if (ATOShaderRules.IsLilToon(shaderName))
                    AnalyzeLilToon(m, result);
                else
                    AnalyzeGeneric(m, result);
            }
            catch (Exception e)
            {
                result.supported = false;
                result.unsupportedReason = $"analysis error: {e.Message}";
            }

            return result;
        }

        // ------------------------------------------------------------------ //

        private static void AnalyzeLilToon(Material m, MaterialAnalysis r)
        {
            if (ATOShaderRules.IsLilToonFurShader(m.shader.name))
            {
                r.supported = false;
                r.unsupportedReason = "lilToon Fur/fakeshadow shader (shell UV shift) / 毛壳UV偏移";
                return;
            }

            // Whole-uvMain parallax shifts UVs of everything bound to uvMain.
            // 整体视差会平移 uvMain 上所有采样的 UV。
            bool parallax = m.HasProperty("_ParallaxMap") && m.GetTexture("_ParallaxMap") != null &&
                            (!m.HasProperty("_ParallaxScale") || Mathf.Abs(m.GetFloat("_ParallaxScale")) > 1e-6f);
            bool backfaceShift = m.HasProperty("_ShiftBackfaceUV") &&
                                 Mathf.Abs(m.GetFloat("_ShiftBackfaceUV")) > 1e-6f;

            bool mainTransformed = IsStNonDefault(m, "_MainTex") || HasNonZeroVector(m, "_MainTex_ScrollRotate");

            foreach (var rule in ATOShaderRules.LilToonUvMain)
                AddUse(m, r, rule, 0, mainTransformed || parallax || backfaceShift);

            foreach (var rule in ATOShaderRules.LilToonUvSelectable)
            {
                int ch = 0;
                bool bad = false;
                if (rule.uvModeProp != null && m.HasProperty(rule.uvModeProp))
                {
                    int mode = Mathf.RoundToInt(m.GetFloat(rule.uvModeProp));
                    if (mode >= 0 && mode <= 3) ch = mode + rule.uvModeOffset;
                    else bad = true; // MatCap / Rim / other / 其他
                }

                bool transformed = IsStNonDefault(m, rule.property) ||
                                   HasAnySuffixNonZero(m, rule.property, ATOShaderRules.LilToonPerTexTransformSuffixes);
                AddUse(m, r, rule, ch, transformed || bad,
                    bad ? "UVMode selects non-mesh UV (MatCap/Rim) / UVMode选择了非网格UV" : null);
            }

            foreach (var p in ATOShaderRules.LilToonNotMeshUV)
                AddNonMeshUse(m, r, p, "not sampled by plain mesh UV (matcap/ramp/etc.) / 非普通网格UV采样");
        }

        // ------------------------------------------------------------------ //

        private static void AnalyzeGeneric(Material m, MaterialAnalysis r)
        {
            var shader = m.shader;
            int count = shader.GetPropertyCount();
            var known = new HashSet<string>(ATOShaderRules.StandardUv0.Select(x => x.property));

            for (int i = 0; i < count; i++)
            {
                if (shader.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                string prop = shader.GetPropertyName(i);
                var flags = shader.GetPropertyFlags(i);
                bool normalFlag = (flags & ShaderPropertyFlags.Normal) != 0;
                bool mainFlag = (flags & ShaderPropertyFlags.MainTexture) != 0;
                var tex = m.GetTexture(prop) as Texture2D;
                if (tex == null) continue;

                if (ATOShaderRules.LooksNonMeshUV(prop))
                {
                    AddUseRaw(r, prop, tex, TexRole.Main, -1, true,
                        "name suggests non-mesh-UV sampling / 命名提示非网格UV采样");
                    continue;
                }

                if (known.Contains(prop) && !normalFlag)
                {
                    var rule = ATOShaderRules.StandardUv0.First(x => x.property == prop);
                    // Unity Standard detail maps may use UV1 via _UVSec / Standard 细节图可能经 _UVSec 用 UV1
                    int ch = 0;
                    if (prop.Contains("Detail"))
                        ch = m.HasProperty("_UVSec") ? Mathf.RoundToInt(m.GetFloat("_UVSec")) : 0;
                    bool transformed = IsStNonDefault(m, prop);
                    AddUse(m, r, rule, ch, transformed);
                    continue;
                }

                var role = ATOShaderRules.GuessRoleByName(prop, normalFlag, mainFlag);
                if (role == null)
                {
                    AddUseRaw(r, prop, tex, TexRole.Main, 0, true,
                        $"unknown property '{prop}' / 未知属性");
                    continue;
                }

                bool st = IsStNonDefault(m, prop);
                AddUseRaw(r, prop, tex, role.Value, 0, st,
                    st ? "material tiling/offset ≠ default / 材质平移缩放非默认" : null);
            }
        }

        // ------------------------------------------------------------------ //
        // helpers
        // ------------------------------------------------------------------ //

        private static void AddUse(Material m, MaterialAnalysis r, PropRule rule, int channel,
            bool transformed, string extraNote = null)
        {
            var tex = m.GetTexture(rule.property) as Texture2D;
            if (tex == null) return;

            string note = extraNote;
            if (transformed && note == null)
                note = $"UV transform active on '{rule.property}' / 该属性存在UV变换";
            AddUseRaw(r, rule.property, tex, rule.role, channel, transformed, note);
        }

        private static void AddNonMeshUse(Material m, MaterialAnalysis r, string prop, string reason)
        {
            var tex = m.GetTexture(prop) as Texture2D;
            if (tex == null) return;
            AddUseRaw(r, prop, tex, TexRole.Main, -1, true, reason);
        }

        private static void AddUseRaw(MaterialAnalysis r, string prop, Texture2D tex, TexRole role,
            int channel, bool transformed, string note)
        {
            r.uses.Add(new TextureUse
            {
                property = prop,
                texture = tex,
                role = role,
                uvChannel = channel,
                transformed = transformed,
                note = note,
            });
        }

        private static bool IsStNonDefault(Material m, string prop)
        {
            if (!m.HasProperty(prop)) return false;
            var scale = m.GetTextureScale(prop);
            var offset = m.GetTextureOffset(prop);
            return scale != Vector2.one || offset != Vector2.zero;
        }

        private static bool HasNonZeroVector(Material m, string prop)
        {
            if (!m.HasProperty(prop)) return false;
            var v = m.GetVector(prop);
            return v.sqrMagnitude > 1e-10f;
        }

        private static bool HasAnySuffixNonZero(Material m, string prop, string[] suffixes)
        {
            foreach (var s in suffixes)
            {
                if (HasNonZeroVector(m, prop + s)) return true;
                if (m.HasProperty(prop + s))
                {
                    var f = m.GetFloat(prop + s);
                    if (Mathf.Abs(f) > 1e-6f) return true;
                }
            }

            return false;
        }

        /// <summary>Is this material fully understood? / 该材质是否完全被理解？</summary>
        public static bool IsFullySupported(MaterialAnalysis r)
        {
            return r.supported && r.uses.All(u => !u.transformed && u.uvChannel >= 0);
        }
    }
}
