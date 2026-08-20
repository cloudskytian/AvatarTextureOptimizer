// ============================================================================
// ATO - candidate atlas pool
// ATO - 候选图集池
//
// POT mode (default): square + rectangular powers of two, 64..8192
// (4092 on mobile targets). NPOT mode (experimental): 64-step sizes up to
// the same maximum. Both modes allow non-square pages; when areas are equal
// the most-square page wins.
// POT 模式（默认）：2 的幂（正方形+长方形），64..8192（移动端 4096）。NPOT
// 模式（实验性）：步进 64 直至同一上限。两模式均允许非正方形页；面积相同时
// 最接近正方形的优先。
// ============================================================================

#region

using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;

#endregion

namespace net.fosa.AvatarTextureOptimizer.Editor.Packing
{
    public struct ATOPoolEntry
    {
        public int W, H;
        public long Area => (long) W * H;
        /// <summary>Aspect distance from 1 (0 = square). 与正方形的边长比距。</summary>
        public float AspectDist => Mathf.Abs((float) Mathf.Max(W, H) / Mathf.Max(1, Mathf.Min(W, H)) - 1f);
    }

    public static class AtlasPool
    {
        public static bool IsMobileTarget()
        {
            var t = EditorUserBuildSettings.activeBuildTarget;
            return t == BuildTarget.Android || t == BuildTarget.iOS;
        }

        public static int MaxSize() => IsMobileTarget() ? 4096 : 8192;

        /// <summary>Builds the candidate pool (sorted: area asc, then most
        /// square first). 构建候选池（排序：面积升序，其次最接近正方形）。</summary>
        public static List<ATOPoolEntry> BuildPool(bool npot)
        {
            int max = MaxSize();
            var sides = new List<int>();
            if (npot)
            {
                for (int s = 64; s <= max; s += 64) sides.Add(s);
            }
            else
            {
                for (int s = 64; s <= max; s *= 2) sides.Add(s);
            }

            var pool = new List<ATOPoolEntry>();
            var seen = new HashSet<(int, int)>();
            foreach (var a in sides)
            {
                foreach (var b in sides)
                {
                    // normalize orientation  归一化方向
                    int w = Mathf.Max(a, b), h = Mathf.Min(a, b);
                    if (seen.Add((w, h)))
                    {
                        pool.Add(new ATOPoolEntry { W = w, H = h });
                    }
                }
            }

            pool.Sort((x, y) =>
            {
                int c = x.Area.CompareTo(y.Area);
                if (c != 0) return c;
                return x.AspectDist.CompareTo(y.AspectDist);
            });

            // cap the pool (NPOT generates many sizes): keep the smallest
            // candidates (preferred by the spec's "smallest first" rule) plus
            // the largest ones; packing rebuilds per candidate, so a huge
            // pool is a performance hazard.
            // 限制候选池规模（NPOT 尺寸很多）：保留最小的候选（规范"先小后
            // 大"）与最大的候选。
            const int keepSmall = 256;
            const int keepLarge = 16;
            if (pool.Count > keepSmall + keepLarge)
            {
                var capped = new List<ATOPoolEntry>(pool.Take(keepSmall));
                foreach (var e in pool.TakeLast(keepLarge))
                {
                    if (!capped.Contains(e)) capped.Add(e);
                }
                capped.Sort((x, y) =>
                {
                    int c = x.Area.CompareTo(y.Area);
                    if (c != 0) return c;
                    return x.AspectDist.CompareTo(y.AspectDist);
                });
                return capped;
            }
            return pool;
        }
    }
}
