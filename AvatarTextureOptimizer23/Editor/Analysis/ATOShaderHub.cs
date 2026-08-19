using System.Collections.Generic;
using UnityEngine;
using FOSA.AvatarTextureOptimizer;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Dispatches shader analysis: third-party first, then lilToon, then generic keywords.
    /// 分发着色器分析：先第三方，再 lilToon，再通用关键字。
    /// </summary>
    internal static class ATOShaderHub
    {
        public static void AnalyzeAll(ATOContext ctx)
        {
            foreach (var ri in ctx.Renderers)
            {
                if (ri.SharedMaterials == null) continue;
                for (int si = 0; si < ri.SharedMaterials.Length; si++)
                {
                    var mat = ri.SharedMaterials[si];
                    if (mat == null || mat.shader == null) continue;
                    var slots = AnalyzeMaterial(ctx, ri, mat, si);
                    foreach (var slot in slots)
                    {
                        ctx.Uses.Add(new ATOTextureUse { Slot = slot, Renderer = ri });
                    }
                }
            }
            ctx.Log.Info($"Texture slots: {ctx.Uses.Count}");
        }

        public static List<ATOTextureSlotInfo> AnalyzeMaterial(ATOContext ctx, ATORendererInfo ri, Material mat, int submesh)
        {
            var list = new List<ATOTextureSlotInfo>();
            var shader = mat.shader;
            var count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                if (shader.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Texture) continue;
                var prop = shader.GetPropertyName(i);
                var tex = mat.GetTexture(prop) as Texture2D;
                if (tex == null) continue;

                ATOTextureSlotInfo info = null;
                var handled = false;

                foreach (var ext in ATOApi.ShaderAnalyzers)
                {
                    if (ext.TryAnalyze(mat, prop, out info))
                    {
                        handled = true;
                        break;
                    }
                }

                if (!handled && ATOLilToonAnalyzer.IsLilToon(mat))
                {
                    handled = ATOLilToonAnalyzer.TryAnalyze(mat, prop, out info);
                }

                if (!handled)
                {
                    info = ATOGenericShaderAnalyzer.Analyze(mat, prop);
                    handled = true;
                }

                if (info == null)
                {
                    ctx.WarnWhitelist(tex, $"unanalyzable property {prop} on {mat.shader.name}");
                    continue;
                }

                info.material = mat;
                info.renderer = ri.Renderer;
                info.submeshIndex = submesh;
                info.propertyName = prop;
                info.texture = tex;
                info.colorSpace = ATOTextureUtil.GuessLinear(tex) ? ColorSpace.Linear : ColorSpace.Gamma;
                info.filterMode = tex.filterMode;

                if (!info.eligible)
                {
                    ctx.WarnWhitelist(tex, info.ineligibleReason ?? $"ineligible {prop}");
                }

                list.Add(info);
            }

            // Companion flags: if this material has a bump/mask, mark sibling main textures.
            // 伴生标记：若本材质有法线/蒙版，给同材质主色打标。
            var hasNormal = false;
            var hasMask = false;
            foreach (var s in list)
            {
                if (s.category == ATOTextureCategory.Normal) hasNormal = true;
                if (IsMaskProperty(s.propertyName)) hasMask = true;
            }
            foreach (var s in list)
            {
                if (s.category == ATOTextureCategory.OpaqueAlbedo ||
                    s.category == ATOTextureCategory.TransparentAlbedo ||
                    s.category == ATOTextureCategory.Unknown)
                {
                    s.hasNormalCompanion = hasNormal;
                    s.hasMaskCompanion = hasMask;
                }
            }
            return list;
        }

        internal static bool IsMaskProperty(string prop)
        {
            if (string.IsNullOrEmpty(prop)) return false;
            var p = prop.ToLowerInvariant();
            return p.Contains("mask") || p.Contains("occlusion") || p.Contains("metallic") ||
                   p.Contains("smoothness") || p.Contains("specular") || p.Contains("detail");
        }
    }
}
