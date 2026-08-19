using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace NetFosa.AvatarTextureOptimizer.Editor.Analysis
{
    /// <summary>材质的渲染模式（决定 alpha 质量指标类型）。</summary>
    public enum RenderMode
    {
        Opaque = 0,
        Cutout = 1,
        Blend = 2, // 半透明
        Unknown = 3,
    }

    /// <summary>材质渲染模式解析结果。</summary>
    public struct RenderModeInfo
    {
        public RenderMode mode;
        public float cutoff; // Cutout 的 _Cutoff 值
    }

    /// <summary>
    /// 渲染模式解析：先查 lilToon 关键字（LIL_RENDER_1/2，见 lil_common_frag_alpha.hlsl：
    /// RENDER==1 时 clip(a-_Cutoff)，RENDER==2 为半透明混合），再回退 RenderType tag，
    /// 再回退混合状态属性，最后根据 alpha 内容兜底（由调用方做像素兜底）。
    /// </summary>
    public static class RenderModeResolver
    {
        public static RenderModeInfo Resolve(Material material)
        {
            var info = new RenderModeInfo { mode = RenderMode.Opaque, cutoff = 0.5f };

            if (material == null) return info;

            // 1) lilToon 关键字
            try
            {
                if (material.IsKeywordEnabled("LIL_RENDER_2")) { info.mode = RenderMode.Blend; }
                else if (material.IsKeywordEnabled("LIL_RENDER_1")) { info.mode = RenderMode.Cutout; }
            }
            catch (Exception) { /* keyword API 异常则继续回退 */ }

            if (info.mode != RenderMode.Opaque)
            {
                info.cutoff = material.HasProperty("_Cutoff") ? material.GetFloat("_Cutoff") : 0.5f;
                return info;
            }

            // 2) RenderType tag
            var renderType = material.GetTag("RenderType", false, "Opaque");
            switch (renderType.ToLowerInvariant())
            {
                case "transparentcutout":
                case "cutout":
                    info.mode = RenderMode.Cutout;
                    info.cutoff = material.HasProperty("_Cutoff") ? material.GetFloat("_Cutoff") : 0.5f;
                    return info;
                case "transparent":
                case "fade":
                    info.mode = RenderMode.Blend;
                    return info;
            }

            // 3) 混合状态属性
            if (material.HasProperty("_SrcBlend") && material.HasProperty("_DstBlend"))
            {
                var src = (BlendMode)Mathf.RoundToInt(material.GetFloat("_SrcBlend"));
                var dst = (BlendMode)Mathf.RoundToInt(material.GetFloat("_DstBlend"));
                if (src == BlendMode.SrcAlpha || dst != BlendMode.Zero || dst == BlendMode.OneMinusSrcAlpha)
                {
                    info.mode = RenderMode.Blend;
                    return info;
                }
            }

            // 4) 常见 alpha 关键字
            if (material.HasProperty("_AlphaClip") && material.GetFloat("_AlphaClip") > 0.5f)
            {
                info.mode = RenderMode.Cutout;
                info.cutoff = material.HasProperty("_Cutoff") ? material.GetFloat("_Cutoff") : 0.5f;
                return info;
            }

            return info;
        }

        /// <summary>是否属于"透明/裁剪"类（需要 alpha 质量指标）。</summary>
        public static bool RequiresAlphaMetrics(RenderMode mode) => mode == RenderMode.Cutout || mode == RenderMode.Blend;
    }
}
