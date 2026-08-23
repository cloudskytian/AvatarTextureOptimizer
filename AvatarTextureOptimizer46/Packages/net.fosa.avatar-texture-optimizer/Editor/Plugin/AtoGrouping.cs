// SPDX-License-Identifier: MIT
// EN: Stage 2 - group textures that must share one island layout.
// ZH: 阶段 2 —— 将必须共享同一套岛布局的贴图分组。

using System.Collections.Generic;
using System.Linq;
using Net.Fosa.AvatarTextureOptimizer.Editor.Core;
using Net.Fosa.AvatarTextureOptimizer.Editor.Model;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Plugin
{
    /// <summary>
    /// EN: Builds UV groups. Two textures belong to the same group when they are sampled through the
    ///     same UV slot; the relation is transitive, so a connected component search gives the groups.
    ///     This is what guarantees that a normal map lands at exactly the same atlas coordinates as the
    ///     colour map it belongs to.
    /// ZH: 构建 UV 组。两张贴图通过同一个 UV 槽采样时属于同一组；该关系具有传递性，
    ///     因此连通分量搜索即可得到分组。这正是保证法线贴图与其对应主色贴图落在
    ///     完全相同的图集坐标上的机制。
    /// </summary>
    public static class AtoGrouping
    {
        private const string Stage = "Group";

        /// <summary>
        /// EN: Groups the optimizable textures of a collection.
        /// ZH: 对集合中可优化的贴图进行分组。
        /// </summary>
        public static void Build(AtoCollection collection)
        {
            var entries = collection.Textures.Values.Where(e => e.IsOptimizable).ToList();

            var parent = new Dictionary<TextureEntry, TextureEntry>();
            foreach (var e in entries) parent[e] = e;

            TextureEntry Find(TextureEntry e)
            {
                while (!ReferenceEquals(parent[e], e)) { parent[e] = parent[parent[e]]; e = parent[e]; }
                return e;
            }
            void Union(TextureEntry a, TextureEntry b)
            {
                var ra = Find(a); var rb = Find(b);
                if (!ReferenceEquals(ra, rb)) parent[ra] = rb;
            }

            var bySlot = new Dictionary<UvSlot, List<TextureEntry>>();
            foreach (var e in entries)
            {
                foreach (var slot in e.Usages.Select(u => u.Slot).Distinct())
                {
                    if (!bySlot.TryGetValue(slot, out var list)) bySlot[slot] = list = new List<TextureEntry>();
                    if (!list.Contains(e)) list.Add(e);
                }
            }
            foreach (var list in bySlot.Values)
                for (int i = 1; i < list.Count; i++)
                    Union(list[0], list[i]);

            var groups = new Dictionary<TextureEntry, UvGroup>();
            foreach (var e in entries)
            {
                var root = Find(e);
                if (!groups.TryGetValue(root, out var g))
                {
                    g = new UvGroup { Index = groups.Count };
                    groups[root] = g;
                    collection.Groups.Add(g);
                }
                g.Textures.Add(e);
                e.Group = g;
                foreach (var u in e.Usages) g.Slots.Add(u.Slot);
            }

            foreach (var g in collection.Groups)
            {
                g.Channel = g.Slots.Count > 0 ? g.Slots.First().Channel : 0;

                // EN: The reference resolution is the largest member, so no member is ever upsampled.
                //     Islands are expressed in this space and every member is resampled into it.
                // ZH: 参考分辨率取最大的成员，这样不会有任何成员被上采样。
                //     岛在该空间中表示，所有成员都会被重采样到该空间。
                int w = 0, h = 0;
                foreach (var t in g.Textures) { w = Mathf.Max(w, t.Width); h = Mathf.Max(h, t.Height); }
                g.ReferenceSize = new Vector2Int(Mathf.Max(1, w), Mathf.Max(1, h));

                AtoLog.Debug_(Stage,
                    $"group {g.Index}: {g.Textures.Count} textures, {g.Slots.Count} UV slots, uv{g.Channel}, reference {g.ReferenceSize.x}x{g.ReferenceSize.y}");
            }

            AtoLog.Info(Stage, $"{collection.Groups.Count} UV groups built from {entries.Count} optimizable textures");
        }
    }
}
