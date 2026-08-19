using System.Collections.Generic;
using UnityEngine;
using FOSA.AvatarTextureOptimizer;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Builds UV groups (union-find over shared mesh UVs) and type groups
    /// (normal/mask companions + color space + filterMode).
    /// 用并查集按共享网格 UV 建 UV 组，再按法线/蒙版伴生 + 色彩空间 + filterMode 建类型组。
    /// </summary>
    internal static class ATOGroupBuilder
    {
        public static void Run(ATOContext ctx)
        {
            var n = ctx.Uses.Count;
            var uf = new int[n];
            for (int i = 0; i < n; i++) uf[i] = i;
            int Find(int x) { while (uf[x] != x) { uf[x] = uf[uf[x]]; x = uf[x]; } return x; }
            void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) uf[b] = a; }

            // Same renderer + submesh + uv channel → same UV group.
            // 同一 Renderer + 子网格 + UV 通道 → 同一 UV 组。
            var keyToIndex = new Dictionary<string, int>();
            for (int i = 0; i < n; i++)
            {
                var u = ctx.Uses[i];
                if (u.Slot.texture == null) continue;
                var key = $"{u.Renderer.Renderer.GetInstanceID()}:{u.Slot.submeshIndex}:{u.Slot.uvChannel}";
                if (keyToIndex.TryGetValue(key, out var other)) Union(i, other);
                else keyToIndex[key] = i;
            }

            // Same texture → same UV group (all islands of one texture stay together).
            // 同一张贴图 → 同一 UV 组（一张贴图的全部岛必须在同一图集）。
            var texToIndex = new Dictionary<int, int>();
            for (int i = 0; i < n; i++)
            {
                var tex = ctx.Uses[i].Slot.texture;
                if (tex == null) continue;
                var id = tex.GetInstanceID();
                if (texToIndex.TryGetValue(id, out var other)) Union(i, other);
                else texToIndex[id] = i;
            }

            var groups = new Dictionary<int, ATOUvGroup>();
            for (int i = 0; i < n; i++)
            {
                var root = Find(i);
                if (!groups.TryGetValue(root, out var g))
                {
                    g = new ATOUvGroup { Id = groups.Count };
                    groups[root] = g;
                    ctx.UvGroups.Add(g);
                }
                var use = ctx.Uses[i];
                use.UvGroupId = g.Id;
                g.Uses.Add(use);
                if (use.Slot.texture != null) g.Textures.Add(use.Slot.texture);
                if (ctx.WhitelistedTextures.Contains(use.Slot.texture) ||
                    ctx.SkipAtlasTextures.Contains(use.Slot.texture))
                    g.SkipAtlas = true;
            }

            foreach (var g in ctx.UvGroups)
            {
                var texCount = g.Textures.Count;
                g.HasAlternates = texCount > 1 && HasMultipleAlbedo(g);
                foreach (var use in g.Uses)
                {
                    if (use.Renderer == null) continue;
                    foreach (var island in use.Renderer.Islands)
                    {
                        if (island.Submesh == use.Slot.submeshIndex &&
                            island.UvChannel == use.Slot.uvChannel &&
                            !g.Islands.Contains(island))
                            g.Islands.Add(island);
                    }
                }
            }

            // Type groups. "has companion" wins. / 类型组。“有伴生”优先。
            var typeMap = new Dictionary<ATOTypeKey, ATOTypeGroup>();
            foreach (var g in ctx.UvGroups)
            {
                var key = BuildTypeKey(g);
                if (!typeMap.TryGetValue(key, out var tg))
                {
                    tg = new ATOTypeGroup { Id = typeMap.Count, Key = key };
                    typeMap[key] = tg;
                    ctx.TypeGroups.Add(tg);
                }
                tg.UvGroups.Add(g);
                foreach (var use in g.Uses) use.TypeGroupId = tg.Id;
            }

            ctx.Log.Info($"UV groups: {ctx.UvGroups.Count}, type groups: {ctx.TypeGroups.Count}");
            foreach (var tg in ctx.TypeGroups)
                ctx.Log.Detail($"  Type[{tg.Id}] {tg.Key} uvGroups={tg.UvGroups.Count}");
        }

        private static bool HasMultipleAlbedo(ATOUvGroup g)
        {
            Texture2D first = null;
            foreach (var use in g.Uses)
            {
                if (use.Slot.category == ATOTextureCategory.Normal) continue;
                if (use.Slot.texture == null) continue;
                if (first == null) first = use.Slot.texture;
                else if (first != use.Slot.texture) return true;
            }
            return false;
        }

        private static ATOTypeKey BuildTypeKey(ATOUvGroup g)
        {
            var key = new ATOTypeKey
            {
                ColorSpace = ColorSpace.Gamma,
                Filter = FilterMode.Bilinear
            };
            foreach (var use in g.Uses)
            {
                if (use.Slot.hasNormalCompanion || use.Slot.category == ATOTextureCategory.Normal)
                    key.HasNormal = true;
                if (use.Slot.hasMaskCompanion || use.Slot.category == ATOTextureCategory.Gray)
                    key.HasMask = true;
                key.ColorSpace = use.Slot.colorSpace;
                key.Filter = use.Slot.filterMode;
            }
            return key;
        }
    }
}
