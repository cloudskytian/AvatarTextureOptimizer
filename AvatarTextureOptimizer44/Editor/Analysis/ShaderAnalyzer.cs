// ShaderAnalyzer.cs - Generic shader texture-property analysis + alpha mode detection; dispatches to lilToon table.
// 通用着色器贴图属性分析与透明模式检测；lilToon走专用表。
// Generic path uses Unity Shader property API (Shader.GetPropertyCount/Name/Type/Flags incl. Normal/MainTexture flags)
// plus keyword heuristics, so future shader versions with standard keywords keep working.
// 通用路径使用Unity着色器属性API（含Normal/MainTexture标志）+关键字启发，从而尽可能兼容未来版本。
using System;
using System.Collections.Generic;
using Fosa.ATO.Runtime;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Fosa.ATO.Editor.Analysis
{
    public static class ShaderAnalyzer
    {
        /// <summary>Analyzed texture usages of one material on one renderer. / 单材质在单渲染器上的分析结果。</summary>
        public sealed class MaterialAnalysis
        {
            public Material material;
            public readonly List<TexturePropInfo> props = new List<TexturePropInfo>();
            public ATOAlphaMode alphaMode = ATOAlphaMode.Opaque;
            public float cutoff = 0.5f;
            /// <summary>All cutoff values to evaluate (materials + animated values), strictest set. / 全部需评估的cutoff值（材质+动画），取最严。</summary>
            public readonly List<float> cutoffCandidates = new List<float>();
            public bool shaderUnderstood = true;
            public string shaderNote = "";
        }

        /// <summary>Analyze a material in renderer context. / 在渲染器上下文分析材质。</summary>
        public static MaterialAnalysis Analyze(Material mat, AvatarScan scan, string rendererPath)
        {
            var ma = new MaterialAnalysis { material = mat };
            if (mat == null || mat.shader == null) { ma.shaderUnderstood = false; ma.shaderNote = "null material/shader"; return ma; }

            // lilToon fast path / lilToon 专用路径
            if (LiltoonTables.IsLiltoon(mat.shader))
            {
                var list = LiltoonTables.Analyze(mat, scan, rendererPath);
                if (list != null) { ma.props.AddRange(list); DetectAlphaLiltoon(mat, ma); return ma; }
            }

            // ---- generic path / 通用路径 ----
            var sh = mat.shader;
            int n = sh.GetPropertyCount();
            for (int i = 0; i < n; i++)
            {
                if (sh.GetPropertyType(i) != ShaderPropertyType.Texture) continue;
                if (sh.GetPropertyTextureDimension(i) != TextureDimension.Tex2D) continue; // only 2D / 仅2D
                string prop = sh.GetPropertyName(i);
                var flags = sh.GetPropertyFlags(i);
                var tex = mat.GetTexture(prop) as Texture2D;
                if (tex == null) continue;

                var info = new TexturePropInfo { prop = prop, texture = tex, uvChannel = 0 };
                info.role = DetectRole(prop, flags);

                if ((flags & ShaderPropertyFlags.Normal) != 0) info.role = ATOTextureRole.Normal;

                // name-based special use detection / 按名称检测特殊用途
                string lp = prop.ToLowerInvariant();
                if (lp.Contains("parallax") || lp.Contains("decal") || lp.Contains("matcap") || lp.Contains("panorama"))
                { info.eligible = false; info.reason = "special usage by name / 名称判断为特殊用途"; }

                if (info.eligible && !LiltoonTables.STIdentity(mat, prop))
                { info.eligible = false; info.reason = "ST scale/offset changed / 存在平移缩放"; }

                if (info.eligible && LiltoonTables.IsAnimatedTransform(scan, rendererPath, prop))
                { info.eligible = false; info.reason = "ST animated / 动画修改ST"; }

                ma.props.Add(info);
            }

            DetectAlphaGeneric(mat, ma);
            return ma;
        }

        /// <summary>Role from property name (fallback when shader flags absent). / 依属性名推断角色（无标志时兜底）。</summary>
        private static ATOTextureRole DetectRole(string prop, ShaderPropertyFlags flags)
        {
            if ((flags & ShaderPropertyFlags.MainTexture) != 0) return ATOTextureRole.MainColor;
            string p = prop.ToLowerInvariant();
            if (p.Contains("bump") || p.Contains("normal")) return ATOTextureRole.Normal;
            if (p.Contains("mask") || p.Contains("metallic") || p.Contains("occlusion") || p.Contains("smooth") || p.Contains("rough") || p.Contains("ao")) return ATOTextureRole.Mask;
            if (p.Contains("emission") || p.Contains("emissive")) return ATOTextureRole.Emission;
            if (p == "_maintex" || p.Contains("albedo") || p.Contains("basecolor") || p.Contains("colormap")) return ATOTextureRole.MainColor;
            return ATOTextureRole.Data;
        }

        // ------------------------------------------------------------------
        // Alpha handling / 透明处理
        // ------------------------------------------------------------------

        private static void DetectAlphaLiltoon(Material m, MaterialAnalysis ma)
        {
            string sh = m.shader.name; // Hidden/lilToonCutout etc. / 含Cutout等字样
            if (sh.Contains("Cutout")) ma.alphaMode = ATOAlphaMode.Cutout;
            else if (sh.Contains("Transparent") || sh.Contains("Overlay")) ma.alphaMode = ATOAlphaMode.Blend;
            else if (m.HasProperty("_TransparentMode"))
            {
                switch ((int)m.GetFloat("_TransparentMode"))
                {
                    case 1: ma.alphaMode = ATOAlphaMode.Cutout; break;
                    case 2: case 3: ma.alphaMode = ATOAlphaMode.Blend; break;
                }
            }
            ma.cutoff = m.HasProperty("_Cutoff") ? m.GetFloat("_Cutoff") : 0.5f;
            // animated cutoff values are merged by the caller with the renderer path / 动画cutoff由调用方按渲染器路径合并
        }

        private static void DetectAlphaGeneric(Material m, MaterialAnalysis ma)
        {
            if (m.IsKeywordEnabled("_ALPHATEST_ON") || m.renderQueue >= 2450 && m.renderQueue < 2750) ma.alphaMode = ATOAlphaMode.Cutout;
            else if (m.IsKeywordEnabled("_ALPHABLEND_ON") || m.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON") || m.renderQueue >= 3000) ma.alphaMode = ATOAlphaMode.Blend;
            else
            {
                // shader-name based (many toon shaders) / 依着色器名（不少卡通着色器）
                string s = m.shader.name.ToLowerInvariant();
                if (s.Contains("cutout") || s.Contains("clip")) ma.alphaMode = ATOAlphaMode.Cutout;
                else if (s.Contains("transparent") || s.Contains("fade") || s.Contains("glass")) ma.alphaMode = ATOAlphaMode.Blend;
            }
            ma.cutoff = m.HasProperty("_Cutoff") ? m.GetFloat("_Cutoff") : 0.5f;
        }

        /// <summary>Merge animated _Cutoff values into the candidate set (strictest). / 合并动画修改的_Cutoff值（取最严）。</summary>
        public static void AddAnimatedCutoffs(AvatarScan scan, Material mat, MaterialAnalysis ma, string rendererPath)
        {
            ma.cutoffCandidates.Add(ma.cutoff);
            if (scan == null) return;
            foreach (var kv in scan.floatProps)
            {
                if (kv.Key.path != rendererPath) continue;
                string p = kv.Key.prop;
                // material._Cutoff or material.<name>._Cutoff / 两种绑定形态
                const string suffix = "_Cutoff";
                if (p.EndsWith(suffix, StringComparison.Ordinal) && (p == "material." + suffix || p.StartsWith("material.", StringComparison.Ordinal)))
                {
                    ma.cutoffCandidates.Add(kv.Value.x);
                    ma.cutoffCandidates.Add(kv.Value.y);
                }
            }
            // dedupe / 去重
            var set = new HashSet<float>(ma.cutoffCandidates);
            ma.cutoffCandidates.Clear(); ma.cutoffCandidates.AddRange(set);
        }

        /// <summary>Animated float props of a material on a renderer (for render-mode change detection). / 渲染器上材质被动画修改的浮点属性。</summary>
        public static bool IsRenderModeAnimated(AvatarScan scan, string rendererPath)
        {
            foreach (var kv in scan.floatProps)
            {
                if (kv.Key.path != rendererPath) continue;
                string p = kv.Key.prop;
                if (p.EndsWith("_Cutoff") || p.EndsWith("_TransparentMode") || p.EndsWith("_BlendMode") || p.EndsWith("_Mode") || p.EndsWith("_SurfaceType"))
                    return true;
            }
            return false;
        }
    }
}
