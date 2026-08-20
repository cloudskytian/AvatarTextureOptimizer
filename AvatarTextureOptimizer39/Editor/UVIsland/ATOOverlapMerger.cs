// Avatar Texture Optimizer (ATO)
// Bilingual comments: English + Simplified Chinese. 双语注释：英文 + 简体中文。

using System.Collections.Generic;
using UnityEngine;

namespace AvatarTextureOptimizer.Editor.UVIsland
{
    /// <summary>
    /// Merges islands whose UV bounds overlap within the same texture (e.g. mirrored UVs
    /// or two geometry fragments intentionally mapping to the same texture region).
    /// Overlapping islands must be treated as one unit so scaling/packing does not
    /// conflict and so they share a single transform.
    ///
    /// 合并同一贴图内 UV 包围盒重叠的岛（如镜像 UV，或两个几何片段故意映射到贴图同一
    /// 区域）。重叠岛必须作为一个整体处理，避免缩放/装箱冲突，并共享同一变换。
    /// </summary>
    public static class ATOOverlapMerger
    {
        /// <summary>
        /// Merge overlapping islands within the same UV channel. Uses union-find over
        /// bounding-box overlap (iterated until fixpoint since merges expand bounds).
        ///
        /// 合并同一 UV 通道内包围盒重叠的岛。用并查集做包围盒重叠合并（迭代到不动点，
        /// 因合并会扩大包围盒）。
        /// </summary>
        public static List<ATOUVIsland> Merge(List<ATOUVIsland> islands, int channel)
        {
            if (islands.Count <= 1) return new List<ATOUVIsland>(islands);

            int n = islands.Count;
            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;

            bool changed;
            do
            {
                changed = false;
                for (int i = 0; i < n; i++)
                {
                    for (int j = i + 1; j < n; j++)
                    {
                        int ri = Find(parent, i), rj = Find(parent, j);
                        if (ri == rj) continue;
                        if (Overlap(islands[ri], islands[rj], channel))
                        {
                            parent[ri] = rj;
                            changed = true;
                        }
                    }
                }
            } while (changed);

            var groups = new Dictionary<int, ATOUVIsland>();
            var result = new List<ATOUVIsland>();

            for (int i = 0; i < n; i++)
            {
                int root = Find(parent, i);
                if (!groups.TryGetValue(root, out var merged))
                {
                    merged = new ATOUVIsland { Triangles = new List<int>(islands[i].Triangles) };
                    merged.UvBounds = (Rect[])islands[i].UvBounds?.Clone();
                    merged.WorldArea = islands[i].WorldArea;
                    merged.MaxArea = islands[i].MaxArea;
                    merged.MaxAreaFactor = islands[i].MaxAreaFactor;
                    groups[root] = merged;
                    result.Add(merged);
                }
                else
                {
                    merged.Triangles.AddRange(islands[i].Triangles);
                    merged.WorldArea += islands[i].WorldArea;
                    merged.MaxArea = Mathf.Max(merged.MaxArea, islands[i].MaxArea);
                    merged.MaxAreaFactor = Mathf.Max(merged.MaxAreaFactor, islands[i].MaxAreaFactor);
                    UnionBounds(merged, islands[i]);
                }
            }

            return result;
        }

        private static bool Overlap(ATOUVIsland a, ATOUVIsland b, int channel)
        {
            var ra = a.UvBounds?[channel] ?? new Rect();
            var rb = b.UvBounds?[channel] ?? new Rect();
            return ra.Overlaps(rb) || rb.Overlaps(ra);
        }

        private static void UnionBounds(ATOUVIsland merged, ATOUVIsland other)
        {
            if (merged.UvBounds == null || other.UvBounds == null) return;
            int count = Mathf.Min(merged.UvBounds.Length, other.UvBounds.Length);
            for (int ch = 0; ch < count; ch++)
            {
                var a = merged.UvBounds[ch];
                var b = other.UvBounds[ch];
                float xMin = Mathf.Min(a.xMin, b.xMin);
                float yMin = Mathf.Min(a.yMin, b.yMin);
                float xMax = Mathf.Max(a.xMax, b.xMax);
                float yMax = Mathf.Max(a.yMax, b.yMax);
                merged.UvBounds[ch] = new Rect(xMin, yMin, xMax - xMin, yMax - yMin);
            }
        }

        private static int Find(int[] parent, int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }
    }
}
