// Candidate atlas pool: POT by default (64..8192 PC / 4096 mobile), experimental NPOT in
// 64px steps; ordered by (area asc, closeness-to-square asc); non-square allowed.
// 候选图集池：默认 2^n（64..8192 PC / 4096 移动端），实验性 NPOT 64px 步进；
// 按（面积升序、接近正方形优先）排序，允许非正方形。

using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.ato.editor
{
    internal static class CandidatePool
    {
        internal static int MaxEdge(AtoPlatform platform)
        {
            return platform == AtoPlatform.PC ? 8192 : 4096; // mobile cap / 移动端上限
        }

        /// <summary>Ordered candidate stream with area >= minArea.
        /// 面积不小于 minArea 的有序候选流。</summary>
        internal static IEnumerable<Vector2Int> Candidates(bool npot, int minArea, AtoPlatform platform)
        {
            int maxEdge = MaxEdge(platform);
            var list = new List<Vector2Int>();

            if (npot)
            {
                for (int w = 64; w <= maxEdge; w += 64)
                    for (int h = 64; h <= maxEdge; h += 64)
                        if (w * h >= minArea)
                            list.Add(new Vector2Int(w, h));
            }
            else
            {
                for (int w = 64; w <= maxEdge; w <<= 1)
                    for (int h = 64; h <= maxEdge; h <<= 1)
                        if (w * h >= minArea)
                            list.Add(new Vector2Int(w, h));
            }

            list.Sort((a, b) =>
            {
                long areaA = (long)a.x * a.y, areaB = (long)b.x * b.y;
                if (areaA != areaB) return areaA.CompareTo(areaB);
                // closer to square first / 最接近正方形优先
                float ra = Mathf.Max(a.x, a.y) / (float)Mathf.Min(a.x, a.y);
                float rb = Mathf.Max(b.x, b.y) / (float)Mathf.Min(b.x, b.y);
                return ra.CompareTo(rb);
            });

            foreach (var c in list) yield return c;
        }
    }
}
