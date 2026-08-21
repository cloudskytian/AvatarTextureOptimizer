// AtlasPacker.cs - Bitmask bottom-left-fill packing with candidate atlas pool.
// 位掩码BLF装箱 + 候选图集池。
// Spec-faithful behaviors / 按规格实现:
//  - textures sorted by rasterized area desc, queues formed per texture-type group / 贴图按光栅面积降序，按类型组分队列
//  - atomic unit = a texture and ALL islands of its UV groups / 原子操作=单张贴图及其全部UV组的岛
//  - candidate pool filtered by total area, sorted by area asc then squareness / 候选池按面积过滤，面积升序+越方越优先
//  - 90-degree rotation via mask transpose / 90度旋转（位掩码转置）
//  - a whole texture that cannot fit the largest atlas -> its UV group leaves atlas-ization (warning) / 单贴图装不下最大图集->整组放弃图集化并警告
//  - island padding = max(ceil(maxEdge/128), user min), min 4px / 岛间距=max(ceil(边长/128),用户最小值)，至少4px
using System;
using System.Collections.Generic;
using System.Linq;
using Fosa.ATO.Editor.Analysis;
using Fosa.ATO.Editor.Core;
using Fosa.ATO.Runtime;
using UnityEngine;

namespace Fosa.ATO.Editor.Atlas
{
    /// <summary>One packed atlas layout. / 单个装好的图集布局。</summary>
    public sealed class AtlasPlan
    {
        public int id;
        public int width, height, padding;
        public readonly List<Island> islands = new List<Island>();
        public int RasterCells;
        public float Utilization => width <= 0 || height <= 0 ? 0 : RasterCells * (IslandRasterizer.Grain * IslandRasterizer.Grain) / (float)(width * height);
    }

    /// <summary>Overall packing output. / 装箱总输出。</summary>
    public sealed class PackResult
    {
        public readonly List<AtlasPlan> atlases = new List<AtlasPlan>();
        public readonly List<UvGroup> fallbackGroups = new List<UvGroup>();  // leave atlas-ization / 放弃图集化
        public readonly List<string> warnings = new List<string>();
    }

    public static class AtlasPacker
    {
        /// <summary>Rotation mapping shared by renderer & mesh rewriter. / 渲染与网格改写共用的旋转映射。</summary>
        public static Vector2 RotatedUV(Vector2 local01, bool rotated)
            => rotated ? new Vector2(1f - local01.y, local01.x) : local01;

        public static PackResult Pack(UsageGraph g, ATOSettings st, bool mobile, ATOProgress progress)
        {
            using (ATOLog.Scope("AtlasPack"))
            {
                var res = new PackResult();
                var groups = g.groups.Where(x => x.Processable && x.islands.Count > 0 && x.textures.Any(t => !t.whitelisted)).ToList();
                if (groups.Count == 0) return res;
                int maxEdge = mobile ? 4096 : 8192;

                // 1) raster masks + cell areas (cached on islands) / 光栅掩码与面积（缓存到岛）
                int done = 0;
                foreach (var grp in groups)
                {
                    progress?.Report(done++ / (float)groups.Count, "Rasterize islands");
                    foreach (var isl in grp.islands)
                    {
                        if (isl.wrapped) continue;
                        if (isl.mask == null)
                            isl.mask = IslandRasterizer.Raster(isl, grp.key.mesh, grp.key.channel, isl.targetW, isl.targetH);
                        isl.maskCells = isl.mask.CellCount;
                    }
                }

                // 2) clusters: groups linked by shared textures / 由共享贴图连接的簇
                var clusters = BuildClusters(g, groups);

                // 3) per cluster: shard by type key, pack shards / 每簇：按类型组分片装箱
                int ci = 0;
                foreach (var cluster in clusters)
                {
                    progress?.Report(ci++ / (float)clusters.Count, "Packing");
                    PackCluster(cluster, st, maxEdge, res);
                }

                foreach (var a in res.atlases)
                    ATOLog.Info($"atlas #{a.id}: {a.width}x{a.height} pad={a.padding} islands={a.islands.Count} util={a.Utilization:F2}");
                return res;
            }
        }

        // ------------------------------------------------------------------
        // Clusters / 簇
        // ------------------------------------------------------------------

        private static List<List<UvGroup>> BuildClusters(UsageGraph g, List<UvGroup> groups)
        {
            var index = new Dictionary<UvGroup, int>();
            for (int i = 0; i < groups.Count; i++) index[groups[i]] = i;
            var parent = Enumerable.Range(0, groups.Count).ToArray();
            int Find(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
            void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[b] = a; }
            foreach (var e in g.textures)
            {
                var cov = g.Coverage(e).Where(x => index.ContainsKey(x)).ToList();
                for (int i = 1; i < cov.Count; i++) Union(index[cov[0]], index[cov[i]]);
            }
            return groups.GroupBy(x => Find(index[x])).Select(grp => grp.ToList()).ToList();
        }

        // ------------------------------------------------------------------
        // Cluster -> shards -> atlases / 簇->分片->图集
        // ------------------------------------------------------------------

        private static void PackCluster(List<UvGroup> cluster, ATOSettings st, int maxEdge, PackResult res)
        {
            // dominant type key per group / 每组主类型键
            TypeGroupKey KeyOf(UvGroup grp)
            {
                var aux = ATOTextureRole.None;
                bool srgb = true; var filter = FilterMode.Bilinear;
                foreach (var t in grp.textures.Where(t => !t.whitelisted))
                {
                    // aux signature = union of aux roles of textures covering this group / 辅助签名=覆盖本组贴图的辅助角色并集
                    aux |= AuxRole(t);
                    srgb = t.import.sRGB; filter = t.texture.filterMode;
                }
                return new TypeGroupKey(aux, srgb, filter);
            }

            var ordered = cluster
                .Select(grp => (grp, cells: grp.islands.Where(i => !i.wrapped).Sum(i => i.maskCells)))
                .Where(x => x.cells > 0)
                .OrderBy(x => KeyOf(x.grp).GetHashCode())   // keep same type groups together / 同类型组相邻
                .ThenByDescending(x => x.cells)
                .ToList();

            // build shards: type-consistent & area-capped / 分片：类型一致且面积受限
            var shards = new List<List<(UvGroup grp, int cells)>>();
            var cur = new List<(UvGroup, int)>();
            long curCells = 0;
            var curKey = (TypeGroupKey)null;
            long capCells = (long)(maxEdge / IslandRasterizer.Grain) * (maxEdge / IslandRasterizer.Grain) / 2; // half of max atlas / 最大图集一半
            foreach (var item in ordered)
            {
                var key = KeyOf(item.grp);
                bool typeBreak = curKey != null && !key.Equals(curKey);
                if (cur.Count > 0 && (typeBreak || curCells + item.cells > capCells))
                {
                    shards.Add(cur); cur = new List<(UvGroup, int)>(); curCells = 0;
                }
                cur.Add(item); curCells += item.cells; curKey = key;
            }
            if (cur.Count > 0) shards.Add(cur);

            foreach (var shard in shards)
            {
                if (!PackShard(shard.Select(x => x.grp).ToList(), st, maxEdge, res))
                {
                    // give up the largest group of the shard / 放弃分片内最大组
                    var biggest = shard.OrderByDescending(x => x.cells).First().grp;
                    res.fallbackGroups.Add(biggest);
                    res.warnings.Add($"[ATO] group {biggest.key} too large for max atlas {maxEdge}; atlas-ization skipped / 组过大，放弃图集化");
                    var rest = shard.Select(x => x.grp).Where(x => x != biggest).ToList();
                    if (rest.Count > 0 && !PackShard(rest, st, maxEdge, res))
                        foreach (var r in rest) { res.fallbackGroups.Add(r); res.warnings.Add($"[ATO] group {r.key} could not be packed / 无法装箱"); }
                }
            }
        }

        /// <summary>Aux roles of a texture (everything beyond MainColor) plus own role. / 贴图的辅助角色（除主色外）及自身角色。</summary>
        internal static ATOTextureRole AuxRole(TexEntry t)
        {
            var r = t.StrictestRole & ~ATOTextureRole.MainColor;
            return r == ATOTextureRole.None ? ATOTextureRole.None : r;
        }

        // ------------------------------------------------------------------
        // BLF packing / BLF装箱
        // ------------------------------------------------------------------

        private static bool PackShard(List<UvGroup> groups, ATOSettings st, int maxEdge, PackResult res)
        {
            var islands = groups.SelectMany(x => x.islands).Where(i => !i.wrapped && i.maskCells > 0).ToList();
            if (islands.Count == 0) return true;
            long neededPx = islands.Sum(i => (long)i.maskCells) * IslandRasterizer.Grain * IslandRasterizer.Grain;
            neededPx = (long)(neededPx * 1.15f); // fragmentation headroom / 碎片余量

            foreach (var cand in Candidates(maxEdge, st.experimentalNpot, neededPx))
            {
                int padding = Mathf.Max(4, Mathf.CeilToInt(Mathf.Max(cand.w, cand.h) / 128f), (int)st.minPadding);
                var plan = TryPack(islands, cand.w, cand.h, padding, res.atlases.Count);
                if (plan != null)
                {
                    plan.id = res.atlases.Count;
                    res.atlases.Add(plan);
                    foreach (var i in plan.islands) { i.placed = true; i.atlasId = plan.id; }
                    return true;
                }
            }
            return false;
        }

        /// <summary>Candidate sizes: area asc, then squareness. / 候选尺寸：面积升序，越方越优先。</summary>
        internal static IEnumerable<(int w, int h)> Candidates(int maxEdge, bool npot, long minAreaPx)
        {
            var sizes = new List<int>();
            if (npot) for (int s = 64; s <= maxEdge; s += 64) sizes.Add(s);
            else for (int s = 64; s <= maxEdge; s *= 2) sizes.Add(s);
            var cands = new List<(int w, int h)>();
            foreach (var w in sizes)
                foreach (var h in sizes)
                    if ((long)w * h >= minAreaPx) cands.Add((w, h));
            return cands.OrderBy(c => (long)c.w * c.h).ThenBy(c => Mathf.Max(c.w, c.h) / (float)Mathf.Min(c.w, c.h));
        }

        /// <summary>Bottom-left-fill placement with padding dilation and 90-degree rotation. / BLF放置（边距膨胀+90度旋转）。</summary>
        private static AtlasPlan TryPack(List<Island> islands, int W, int H, int padding, int idHint)
        {
            var plan = new AtlasPlan { width = W, height = H, padding = padding };
            int gc = W / IslandRasterizer.Grain, gr = H / IslandRasterizer.Grain;
            var words = (gc + 63) >> 6;
            var grid = new ulong[words * gr];
            bool Get(int c, int r) => (grid[r * words + (c >> 6)] & (1ul << (c & 63))) != 0;
            void Mark(int c, int r) { if (c >= 0 && c < gc && r >= 0 && r < gr) grid[r * words + (c >> 6)] |= 1ul << (c & 63); }

            foreach (var isl in islands.OrderByDescending(i => i.maskCells).ThenByDescending(i => Mathf.Max(i.mask.Cols, i.mask.Rows)))
            {
                var m = isl.mask;
                int padCells = padding / IslandRasterizer.Grain;
                // scan positions / 扫描位置
                bool ok = false;
                for (int rot = 0; rot < 2 && !ok; rot++)
                {
                    var mm = rot == 0 ? m : m.Transposed();
                    int mw = mm.Cols, mh = mm.Rows;
                    if (mw > gc || mh > gr) continue;
                    for (int y = 0; y <= gr - mh && !ok; y++)
                        for (int x = 0; x <= gc - mw && !ok; x++)
                        {
                            if (Fits(mm, x, y)) { Place(mm, x, y, padCells); ok = true; }
                        }
                }
                if (!ok) return null;
                void Place(IslandRasterizer.Mask mm, int x, int y, int pc)
                {
                    // mark mask + padding dilation / 标记掩码+边距膨胀
                    for (int r = 0; r < mm.Rows; r++)
                        for (int c = 0; c < mm.Cols; c++)
                            if (mm.Get(c, r))
                                for (int dr = -pc; dr <= pc; dr++)
                                    for (int dc = -pc; dc <= pc; dc++)
                                        Mark(c + x + dc, r + y + dr);
                    isl.rotated = mm != m;
                    isl.atlasRect = new Rect(x * IslandRasterizer.Grain, y * IslandRasterizer.Grain,
                                             (mm == m ? isl.targetW : isl.targetH), (mm == m ? isl.targetH : isl.targetW));
                    plan.islands.Add(isl);
                    plan.RasterCells += isl.maskCells;
                }
                bool Fits(IslandRasterizer.Mask mm, int x, int y)
                {
                    for (int r = 0; r < mm.Rows; r++)
                    {
                        int rowBase = (y + r) * words;
                        for (int c = 0; c < mm.Cols; c++)
                        {
                            if (!mm.Get(c, r)) continue;
                            int gx = x + c;
                            if (Get(gx, y + r)) return false;
                        }
                    }
                    return true;
                }
            }
            return plan;
        }
    }
}
