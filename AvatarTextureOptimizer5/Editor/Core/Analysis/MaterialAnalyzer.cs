// Copyright (c) fosa. Licensed under the MIT License.
// Resolves a material's alpha behaviour, which selects the alpha quality metric.
// 解析材质的 alpha 行为，用于选择 alpha 质量指标。

using System;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Determines render mode and cutoff for arbitrary shaders, with a verified fast path for
    /// lilToon. When nothing can be proven, the most demanding interpretation is assumed so the
    /// quality search errs towards preserving detail.
    /// 判定任意着色器的渲染模式与 cutoff，并为 lilToon 提供经验证的快速路径。
    /// 无法证明时采用要求最高的解释，使质量搜索偏向保留细节。
    /// </summary>
    public static class MaterialAnalyzer
    {
        /// <summary>
        /// lilToon encodes its rendering mode in _TransparentMode.
        /// Verified against lilToon 2.3.4 Editor/lilMaterialUtils.cs lines 26-36:
        /// 0=Opaque 1=Cutout 2=Transparent 3=Refraction 4=Fur 5=FurCutout 6=Gem.
        /// lilToon 将渲染模式编码在 _TransparentMode 中。
        /// 依据 lilToon 2.3.4 Editor/lilMaterialUtils.cs 第 26-36 行验证。
        /// </summary>
        private const string LilTransparentMode = "_TransparentMode";

        /// <summary>
        /// Resolves how a material treats alpha.
        /// 解析材质如何处理 alpha。
        /// </summary>
        public static AlphaMode ResolveAlphaMode(Material material)
        {
            if (material == null) return AlphaMode.Opaque;

            // lilToon: authoritative and cheap.
            // lilToon：权威且开销低。
            if (material.HasProperty(LilTransparentMode))
            {
                var mode = Mathf.RoundToInt(material.GetFloat(LilTransparentMode));
                switch (mode)
                {
                    case 0: return AlphaMode.Opaque;
                    case 1: return AlphaMode.Cutout;
                    case 5: return AlphaMode.Cutout;   // FurCutout / 毛发裁剪
                    case 2: return AlphaMode.Blend;
                    case 3: return AlphaMode.Blend;    // Refraction / 折射
                    case 4: return AlphaMode.Blend;    // Fur / 毛发
                    case 6: return AlphaMode.Blend;    // Gem / 宝石
                }
            }

            // Unity standard convention: the RenderType tag.
            // Unity 标准约定：RenderType 标签。
            var renderType = material.GetTag("RenderType", true, string.Empty);
            if (!string.IsNullOrEmpty(renderType))
            {
                if (renderType.IndexOf("TransparentCutout", StringComparison.OrdinalIgnoreCase) >= 0)
                    return AlphaMode.Cutout;
                if (renderType.IndexOf("Transparent", StringComparison.OrdinalIgnoreCase) >= 0)
                    return AlphaMode.Blend;
                if (renderType.IndexOf("Opaque", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // An Opaque tag combined with an alpha-test keyword still means cutout.
                    // Opaque 标签配合 alpha 测试关键字时仍然是 cutout。
                    if (material.IsKeywordEnabled("_ALPHATEST_ON")) return AlphaMode.Cutout;
                    return AlphaMode.Opaque;
                }
            }

            if (material.IsKeywordEnabled("_ALPHATEST_ON")) return AlphaMode.Cutout;
            if (material.IsKeywordEnabled("_ALPHABLEND_ON") ||
                material.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON")) return AlphaMode.Blend;

            // Render queue is the last resort.
            // 渲染队列作为最后手段。
            var queue = material.renderQueue;
            if (queue >= 3000) return AlphaMode.Blend;
            if (queue >= 2450) return AlphaMode.Cutout;

            return AlphaMode.Opaque;
        }

        /// <summary>
        /// Reads the alpha cutoff threshold, falling back to the common 0.5 default.
        /// 读取 alpha 裁剪阈值，回退到常见默认值 0.5。
        /// </summary>
        public static float ResolveCutoff(Material material)
        {
            if (material == null) return 0.5f;

            // Property names used by lilToon, Unity Standard, Poiyomi and most toon shaders.
            // lilToon、Unity Standard、Poiyomi 及多数卡通着色器使用的属性名。
            string[] candidates = { "_Cutoff", "_AlphaCutoff", "_Cutout", "_AlphaClip" };
            foreach (var c in candidates)
            {
                if (material.HasProperty(c)) return Mathf.Clamp01(material.GetFloat(c));
            }

            return 0.5f;
        }

        /// <summary>
        /// Picks the stricter of two alpha modes. Blend is treated as the most demanding because
        /// it needs the full alpha ramp preserved, then Cutout, then Opaque.
        /// 取两个 alpha 模式中更严苛者。Blend 要求保留完整 alpha 渐变，因此最严苛，其次 Cutout，最后 Opaque。
        /// </summary>
        public static AlphaMode Stricter(AlphaMode a, AlphaMode b)
        {
            if (a == AlphaMode.Blend || b == AlphaMode.Blend) return AlphaMode.Blend;
            if (a == AlphaMode.Cutout || b == AlphaMode.Cutout) return AlphaMode.Cutout;
            return AlphaMode.Opaque;
        }
    }
}
