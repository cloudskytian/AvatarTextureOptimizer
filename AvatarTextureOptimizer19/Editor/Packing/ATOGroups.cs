// English: Build UV groups and type groups (companions / color space / filter).
// 中文：建立 UV 组与类型组（伴侣贴图 / 色彩空间 / filterMode）。
using System.Collections.Generic;
using UnityEngine;
using Net.Fosa.AvatarTextureOptimizer;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    internal static class ATOGroups
    {
        public static void Build(ATOState state)
        {
            var parent = new Dictionary<Texture2D, Texture2D>();
            foreach (var use in state.Uses)
            {
                if (use.Texture == null || !use.Eligible) continue;
                Ensure(parent, use.Texture);
            }

            // Same renderer + UV channel => same UV group.
            var byUv = new Dictionary<string, List<Texture2D>>();
            foreach (var use in state.Uses)
            {
                if (use.Texture == null || !use.Eligible) continue;
                var key = (use.Renderer != null && use.Renderer.Renderer != null
                              ? use.Renderer.Renderer.GetInstanceID()
                              : 0) + "|" + use.UvChannel;
                List<Texture2D> list;
                if (!byUv.TryGetValue(key, out list))
                {
                    list = new List<Texture2D>();
                    byUv[key] = list;
                }

                list.Add(use.Texture);
            }

            foreach (var kv in byUv)
            {
                if (kv.Value.Count == 0) continue;
                var root = kv.Value[0];
                for (var i = 1; i < kv.Value.Count; i++) Union(parent, root, kv.Value[i]);
            }

            // Upgrade companions: if a texture is used both with and without a companion, keep the richer set.
            var companions = new Dictionary<Texture2D, ATOCompanionKind>();
            var linear = new Dictionary<Texture2D, bool>();
            var filter = new Dictionary<Texture2D, FilterMode>();
            foreach (var use in state.Uses)
            {
                if (use.Texture == null || !use.Eligible) continue;
                ATOCompanionKind c;
                if (!companions.TryGetValue(use.Texture, out c)) c = ATOCompanionKind.None;
                companions[use.Texture] = c | use.Companions;
                bool lin;
                if (!linear.TryGetValue(use.Texture, out lin)) lin = use.Linear;
                linear[use.Texture] = lin || use.Linear;
                FilterMode f;
                if (!filter.TryGetValue(use.Texture, out f)) f = use.Filter;
                if (use.Filter == FilterMode.Trilinear || f == FilterMode.Trilinear) f = FilterMode.Trilinear;
                else if (use.Filter == FilterMode.Bilinear || f == FilterMode.Bilinear) f = FilterMode.Bilinear;
                filter[use.Texture] = f;
            }

            // Push companion upgrade across the UV group.
            foreach (var tex in new List<Texture2D>(companions.Keys))
            {
                var r = Find(parent, tex);
                ATOCompanionKind c;
                companions.TryGetValue(r, out c);
                companions[r] = c | companions[tex];
            }

            foreach (var tex in new List<Texture2D>(companions.Keys))
            {
                var r = Find(parent, tex);
                companions[tex] = companions[r];
            }

            var groups = new Dictionary<Texture2D, ATOUvGroup>();
            var next = 0;
            foreach (var isl in state.Islands)
            {
                if (isl.Source == null) continue;
                if (!parent.ContainsKey(isl.Source)) Ensure(parent, isl.Source);
                var root = Find(parent, isl.Source);
                ATOUvGroup g;
                if (!groups.TryGetValue(root, out g))
                {
                    g = new ATOUvGroup { Id = next++ };
                    ATOCompanionKind c;
                    companions.TryGetValue(isl.Source, out c);
                    g.Companions = c;
                    bool lin;
                    linear.TryGetValue(isl.Source, out lin);
                    g.Linear = lin;
                    FilterMode f;
                    filter.TryGetValue(isl.Source, out f);
                    g.Filter = f;
                    groups[root] = g;
                    state.UvGroups.Add(g);
                }

                g.Textures.Add(isl.Source);
                g.Islands.Add(isl);
                g.MasterWidth = Mathf.Max(g.MasterWidth, isl.Source.width);
                g.MasterHeight = Mathf.Max(g.MasterHeight, isl.Source.height);
            }

            state.Log.Info("UV groups=" + state.UvGroups.Count);
        }

        private static void Ensure(Dictionary<Texture2D, Texture2D> p, Texture2D t)
        {
            if (!p.ContainsKey(t)) p[t] = t;
        }

        private static Texture2D Find(Dictionary<Texture2D, Texture2D> p, Texture2D t)
        {
            var r = t;
            while (p[r] != r) r = p[r];
            var x = t;
            while (p[x] != r)
            {
                var n = p[x];
                p[x] = r;
                x = n;
            }

            return r;
        }

        private static void Union(Dictionary<Texture2D, Texture2D> p, Texture2D a, Texture2D b)
        {
            a = Find(p, a);
            b = Find(p, b);
            if (a != b) p[b] = a;
        }
    }
}
