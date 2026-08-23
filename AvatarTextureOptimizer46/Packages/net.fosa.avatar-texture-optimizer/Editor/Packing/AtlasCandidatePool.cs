// SPDX-License-Identifier: MIT
// EN: Generates and orders the candidate atlas sizes.
// ZH: 生成并排序候选图集尺寸。

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Packing
{
    /// <summary>
    /// EN: One candidate atlas size.
    /// ZH: 一个候选图集尺寸。
    /// </summary>
    public readonly struct AtlasCandidate
    {
        /// <summary>EN: Width in texels. ZH: 宽度（像素）。</summary>
        public readonly int Width;
        /// <summary>EN: Height in texels. ZH: 高度（像素）。</summary>
        public readonly int Height;

        /// <summary>EN: Creates a candidate. ZH: 创建一个候选。</summary>
        public AtlasCandidate(int width, int height) { Width = width; Height = height; }

        /// <summary>EN: Total texel count. ZH: 总像素数。</summary>
        public long Area => (long)Width * Height;
        /// <summary>EN: Aspect ratio, always at least 1. ZH: 长宽比，恒不小于 1。</summary>
        public float Aspect => Mathf.Max(Width, Height) / (float)Mathf.Min(Width, Height);
        /// <inheritdoc/>
        public override string ToString() => $"{Width}x{Height}";
    }

    /// <summary>
    /// EN: Builds the candidate pool. Power of two sizes by default; with the experimental NPOT option
    ///     the pool steps in 64 texel increments, which keeps every size a multiple of four so that block
    ///     compression and Crunch remain usable.
    /// ZH: 构建候选池。默认使用二次幂尺寸；启用实验性 NPOT 选项时以 64 像素步进，
    ///     这保证每个尺寸都是 4 的倍数，从而块压缩与 Crunch 仍然可用。
    /// </summary>
    public static class AtlasCandidatePool
    {
        /// <summary>EN: Smallest allowed atlas edge. ZH: 允许的最小图集边长。</summary>
        public const int MinEdge = 64;

        /// <summary>
        /// EN: Builds the ordered candidate list. Ordering is: smaller area first, then closest to square.
        /// ZH: 构建有序候选列表。排序规则：面积小者优先，其次最接近正方形者优先。
        /// </summary>
        /// <param name="maxEdge">EN: Largest allowed edge for the target platform. ZH: 目标平台允许的最大边长。</param>
        /// <param name="allowNpot">EN: Use 64 texel steps instead of powers of two. ZH: 使用 64 像素步进而非二次幂。</param>
        public static List<AtlasCandidate> Build(int maxEdge, bool allowNpot)
        {
            var edges = new List<int>();
            if (allowNpot)
            {
                for (int e = MinEdge; e <= maxEdge; e += 64) edges.Add(e);
            }
            else
            {
                for (int e = MinEdge; e <= maxEdge; e *= 2) edges.Add(e);
            }

            var list = new List<AtlasCandidate>(edges.Count * edges.Count);
            foreach (var w in edges)
            {
                foreach (var h in edges)
                {
                    // EN: Extremely elongated atlases waste memory bandwidth; cap the aspect at 8:1.
                    // ZH: 极端细长的图集浪费显存带宽；将长宽比上限设为 8:1。
                    var c = new AtlasCandidate(w, h);
                    if (c.Aspect > 8f) continue;
                    list.Add(c);
                }
            }

            list.Sort((a, b) =>
            {
                int byArea = a.Area.CompareTo(b.Area);
                if (byArea != 0) return byArea;
                return a.Aspect.CompareTo(b.Aspect);
            });
            return list;
        }

        /// <summary>
        /// EN: Padding for a candidate: ceil(maxEdge / 128) texels, clamped down to at least the
        ///     configured minimum.
        /// ZH: 候选的 padding：ceil(最大边长 / 128) 像素，向下钳制到不小于配置的最小值。
        /// </summary>
        public static int PaddingFor(AtlasCandidate candidate, int minPadding)
        {
            int p = Mathf.CeilToInt(Mathf.Max(candidate.Width, candidate.Height) / 128f);
            return Mathf.Max(p, minPadding);
        }
    }
}
