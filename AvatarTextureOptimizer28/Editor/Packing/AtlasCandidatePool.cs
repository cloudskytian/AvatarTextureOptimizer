using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>EN: One candidate atlas size. ZH: 一个候选图集尺寸。</summary>
    public readonly struct AtlasCandidate
    {
        /// <summary>EN: Width in pixels. ZH: 像素宽度。</summary>
        public readonly int Width;
        /// <summary>EN: Height in pixels. ZH: 像素高度。</summary>
        public readonly int Height;

        /// <summary>EN: Construct. ZH: 构造。</summary>
        public AtlasCandidate(int w, int h) { Width = w; Height = h; }

        /// <summary>EN: Pixel area. ZH: 像素面积。</summary>
        public long Area => (long)Width * Height;

        /// <summary>EN: Aspect ratio, long side over short side, 1 for a square. ZH: 长宽比（长边/短边），正方形为 1。</summary>
        public float Aspect => Mathf.Max(Width, Height) / (float)Mathf.Min(Width, Height);

        /// <inheritdoc/>
        public override string ToString() => $"{Width}x{Height}";
    }

    /// <summary>
    /// EN: Builds and orders the pool of candidate atlas sizes. Ordering is exactly the specification's:
    ///     area ascending first, then aspect ratio ascending, so the smallest and most square atlas that
    ///     can hold the queue is always tried first.
    /// ZH: 构建并排序候选图集尺寸池。排序完全遵循需求：先按面积升序，再按长宽比升序，
    ///     从而总是优先尝试能装下当前队列的、最小且最接近正方形的图集。
    /// </summary>
    public static class AtlasCandidatePool
    {
        /// <summary>EN: Build the pool for a platform. ZH: 为某平台构建候选池。</summary>
        public static List<AtlasCandidate> Build(ATOPlatform platform, bool npot)
        {
            int max = platform == ATOPlatform.PC ? ATOConstants.MaxAtlasSidePC : ATOConstants.MaxAtlasSideMobile;
            var sides = new List<int>();

            if (npot)
            {
                // EN: 64 px stepping. Every value is a multiple of 4, so BCn / ETC / ASTC block
                //     constraints are satisfied and Crunch stays usable.
                // ZH: 64 像素步进。所有取值都是 4 的倍数，因此 BCn / ETC / ASTC 的块约束都满足，
                //     Crunch 也仍然可用。
                for (int s = ATOConstants.MinAtlasSide; s <= max; s += 64) sides.Add(s);
            }
            else
            {
                for (int s = ATOConstants.MinAtlasSide; s <= max; s *= 2) sides.Add(s);
            }

            var list = new List<AtlasCandidate>();
            foreach (var w in sides)
            foreach (var h in sides)
            {
                // EN: Extremely elongated atlases waste memory on padding and confuse streaming; cap the
                //     aspect ratio at 4:1, which still allows the useful 2:1 and 4:1 strips.
                // ZH: 极端狭长的图集会在 padding 上浪费显存并干扰流式加载；
                //     把长宽比上限设为 4:1，仍然保留有用的 2:1 与 4:1 条带。
                var c = new AtlasCandidate(w, h);
                if (c.Aspect > 4f) continue;
                list.Add(c);
            }

            return list
                .OrderBy(c => c.Area)
                .ThenBy(c => c.Aspect)
                .ThenBy(c => c.Width)
                .ToList();
        }

        /// <summary>
        /// EN: Padding for a candidate: ceil(maxSide / 128), floored to the user's minimum.
        /// ZH: 候选图集的 padding：ceil(最大边长 / 128)，并向下钳制到用户设定的最小值。
        /// </summary>
        public static int PaddingFor(AtlasCandidate c, ATOPadding minimum)
        {
            int computed = Mathf.CeilToInt(Mathf.Max(c.Width, c.Height) / 128f);
            return Mathf.Max((int)minimum, computed);
        }
    }
}
