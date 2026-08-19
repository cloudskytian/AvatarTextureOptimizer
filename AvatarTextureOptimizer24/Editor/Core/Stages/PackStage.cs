// ============================================================================
// PackStage.cs — 阶段6：装箱与图集生成 / Stage 6: packing & atlas generation
// (EN) Clusters UV groups that share textures (so all islands of one texture
//      stay in a single atlas), rasterizes islands, packs them via BLF into
//      candidate atlases (atomic per cluster, candidate pool), and generates
//      one atlas texture per texture kind (color / normal / mask-gray).
// (ZH) 将共享贴图的 UV 组聚类（保证同一贴图的所有岛在同一图集），光栅化岛，
//      通过 BLF 装箱到候选图集（每聚类原子、候选池），并为每种贴图类型
//      （颜色/法线/蒙版灰度）各生成一张图集。
// ============================================================================

using System.Collections.Generic;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    public class ATOAtlas
    {
        public string Name;
        public int Width, Height;
        public ATOTextureTypeGroup TypeGroup;
        public List<ATOUVGroup> Groups = new List<ATOUVGroup>();
        public bool HasAlpha;
        // 各贴图类型的图集贴图 / atlas texture per kind
        public Dictionary<ATOTextureClass, Texture2D> KindTextures = new Dictionary<ATOTextureClass, Texture2D>();
    }

    public class ATOPackResult
    {
        public List<ATOAtlas> Atlases = new List<ATOAtlas>();
        public void Clear() => Atlases.Clear();
    }

    public class PackStage
    {
        private readonly ATOBuildContext _ctx;
        private readonly ATOIslandResult _islands;
        private readonly ATOPackResult _result = new ATOPackResult();

        public ATOPackResult Result => _result;

        public PackStage(ATOBuildContext ctx, ATOIslandResult islands)
        {
            _ctx = ctx;
            _islands = islands;
        }

        public void Run()
        {
            _result.Clear();
            if (!_ctx.Atlas.enableAtlas) return;

            // 聚类共享贴图的 UV 组 / cluster UV groups sharing textures
            var clusters = ClusterUvGroups();

            bool mobile = ATOBuildContext.DetectPlatform() != ATOBuildPlatform.PC;

            foreach (var typeGroup in _islands.TypeGroups)
            {
                var clusterSet = new List<List<ATOUVGroup>>();
                foreach (var c in clusters)
                {
                    // 该聚类是否属于此类型组 / does this cluster belong to this type group?
                    bool belongs = false;
                    foreach (var g in c)
                        foreach (var t in typeGroup.Textures)
                            if (g.Textures.Contains(t)) { belongs = true; break; }
                    if (belongs) clusterSet.Add(c);
                }
                if (clusterSet.Count == 0) continue;

                PackTypeGroup(typeGroup, clusterSet, mobile);
            }

            ATOLog.VerboseLog($"[pack] {_result.Atlases.Count} atlases generated");
        }

        // ---------------------------------------------------------------------
        // 聚类 / clustering (union-find over UV groups sharing a texture)
        // ---------------------------------------------------------------------
        private List<List<ATOUVGroup>> ClusterUvGroups()
        {
            int n = _islands.UvGroups.Count;
            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;

            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) parent[ra] = rb; }

            // 贴图 → 组索引 / texture -> group indices
            var texToGroups = new Dictionary<ATOTextureRef, List<int>>();
            for (int i = 0; i < n; i++)
                foreach (var t in _islands.UvGroups[i].Textures)
                {
                    if (!texToGroups.TryGetValue(t, out var list)) { list = new List<int>(); texToGroups[t] = list; }
                    list.Add(i);
                }
            foreach (var list in texToGroups.Values)
                for (int k = 1; k < list.Count; k++)
                    Union(list[0], list[k]);

            var clusters = new Dictionary<int, List<ATOUVGroup>>();
            for (int i = 0; i < n; i++)
            {
                int root = Find(i);
                if (!clusters.TryGetValue(root, out var list)) { list = new List<ATOUVGroup>(); clusters[root] = list; }
                list.Add(_islands.UvGroups[i]);
            }
            return new List<List<ATOUVGroup>>(clusters.Values);
        }

        // ---------------------------------------------------------------------
        // 按类型组装箱 / pack one type group
        // ---------------------------------------------------------------------
        private void PackTypeGroup(ATOTextureTypeGroup typeGroup, List<List<ATOUVGroup>> clusters, bool mobile)
        {
            // 展平所有岛并光栅化 / flatten islands & rasterize
            var allIslands = new List<ATOUVIsland>();
            foreach (var c in clusters)
                foreach (var g in c)
                    allIslands.AddRange(g.Islands);

            // 按聚类原子装箱 / atomic packing per cluster
            var pool = ATOPacker.BuildCandidatePool(_ctx.Atlas, mobile);
            int pad = Mathf.Clamp(Mathf.Max(64, Mathf.CeilToInt(pool[pool.Count - 1] / 128f)), 4, 64);
            int padCells = Mathf.Max(1, pad / ATOPacker.Granularity);

            // 每个聚类排序后装箱到图集 / pack each cluster into atlas(es)
            var atlases = new List<(int w, int h, List<List<ATOUVGroup>> groups)>();

            foreach (var cluster in clusters)
            {
                // 光栅化该聚类的岛（跳过不安全引用的岛，它们不图集化）
                // rasterize this cluster's islands (skip unsafe-referenced islands)
                var islands = new List<ATOUVIsland>();
                foreach (var g in cluster)
                    foreach (var island in g.Islands)
                        if (!island.HasUnsafeReference && !island.CrossesWrapSeam)
                            islands.Add(island);

                if (islands.Count == 0) continue;

                foreach (var island in islands)
                {
                    island.ScaledPixelW = Mathf.Max(1, Mathf.RoundToInt(island.PixelWidth * island.ScaleX));
                    island.ScaledPixelH = Mathf.Max(1, Mathf.RoundToInt(island.PixelHeight * island.ScaleY));
                    ATOPacker.Rasterize(island, island.ScaledPixelW, island.ScaledPixelH);
                }
                islands.Sort((a, b) =>
                {
                    long aa = CellArea(a), ab = CellArea(b);
                    if (aa != ab) return ab.CompareTo(aa);
                    return Mathf.Max(b.RasterW, b.RasterH).CompareTo(Mathf.Max(a.RasterW, a.RasterH));
                });

                // 尝试装入现有图集 / try existing atlases
                bool placed = false;
                for (int i = 0; i < atlases.Count && !placed; i++)
                {
                    var (aw, ah, groups) = atlases[i];
                    var tmp = CloneIslands(islands);
                    if (ATOPacker.TryPack(tmp, aw, ah, padCells, out _))
                    {
                        ApplyPlacement(islands, tmp);
                        AddPlacedGroups(groups, cluster);
                        placed = true;
                    }
                }
                if (placed) continue;

                // 尝试新图集（候选池，从小到大）/ try new atlas (candidate pool, smallest first)
                long totalCells = ATOPacker.TotalCellArea(islands);
                foreach (var (w, h) in ATOPacker.BuildCandidateRects(pool, totalCells))
                {
                    var tmp = CloneIslands(islands);
                    if (ATOPacker.TryPack(tmp, w, h, padCells, out _))
                    {
                        ApplyPlacement(islands, tmp);
                        var newGroups = new List<List<ATOUVGroup>>();
                        AddPlacedGroups(newGroups, cluster);
                        atlases.Add((w, h, newGroups));
                        placed = true;
                        break;
                    }
                }

                if (!placed)
                {
                    // 单聚类装不下最大图集 → 放弃图集化 / cannot fit even max atlas → abandon
                    ATOLog.Warn(ATOLocalization.T(_ctx.Language, "ato.warn.atlasAbandoned"));
                    foreach (var g in cluster) MarkAbandoned(g);
                }
            }

            // 生成图集贴图 / generate atlas textures
            foreach (var (w, h, groups) in atlases)
            {
                var atlas = new ATOAtlas
                {
                    Name = "ATO_" + typeGroup.Key + "_" + _result.Atlases.Count,
                    Width = w, Height = h, TypeGroup = typeGroup,
                };
                foreach (var g in groups) atlas.Groups.Add(g);
                GenerateAtlasTextures(atlas);
                _result.Atlases.Add(atlas);
            }
        }

        /// <summary>(EN) Add only the UV groups that contain placed (rasterized) islands. (ZH) 仅添加包含已装箱岛的 UV 组。</summary>
        private void AddPlacedGroups(List<List<ATOUVGroup>> target, List<ATOUVGroup> cluster)
        {
            foreach (var g in cluster)
            {
                bool hasPlaced = false;
                foreach (var island in g.Islands)
                    if (island.RasterizedMask != null) { hasPlaced = true; break; }
                if (hasPlaced && !target.Contains(g)) target.Add(g);
            }
        }

        private void ApplyPlacement(List<ATOUVIsland> src, List<ATOUVIsland> placed)
        {
            for (int i = 0; i < src.Count; i++)
            {
                src[i].RasterX = placed[i].RasterX;
                src[i].RasterY = placed[i].RasterY;
                src[i].Rotated90 = placed[i].Rotated90;
            }
        }

        private List<ATOUVIsland> CloneIslands(List<ATOUVIsland> src)
        {
            var list = new List<ATOUVIsland>();
            foreach (var i in src)
            {
                var c = new ATOUVIsland
                {
                    RasterW = i.RasterW, RasterH = i.RasterH,
                    RasterizedMask = i.RasterizedMask,
                };
                list.Add(c);
            }
            return list;
        }

        private void MarkAbandoned(ATOUVGroup group)
        {
            // 放弃图集化：岛按质量缩放后作为独立贴图保留（由 Apply 处理）
            // abandoned: islands stay standalone (handled in Apply)
            foreach (var island in group.Islands)
            {
                island.RasterizedMask = null; // 标记未装箱 / mark unplaced
            }
        }

        private static long CellArea(ATOUVIsland i)
        {
            long a = 0;
            foreach (var b in i.RasterizedMask) if (b) a++;
            return a;
        }

        // ---------------------------------------------------------------------
        // 图集贴图生成 / atlas texture generation
        // ---------------------------------------------------------------------
        private void GenerateAtlasTextures(ATOAtlas atlas)
        {
            bool hasNormal = atlas.TypeGroup.HasNormalMap;
            bool hasMask = atlas.TypeGroup.HasMaskMap;

            var kinds = new List<ATOTextureClass> { ATOTextureClass.Opaque };
            if (hasNormal) kinds.Add(ATOTextureClass.Normal);
            if (hasMask) kinds.Add(ATOTextureClass.Grayscale);

            foreach (var kind in kinds)
            {
                var tex = new Texture2D(atlas.Width, atlas.Height, TextureFormat.RGBA32, true, !atlas.TypeGroup.Srgb);
                // 初始为透明黑（未被岛覆盖的区域后续由 pull-push 填充）
                // initially transparent black (uncovered areas filled by pull-push later)

                foreach (var g in atlas.Groups)
                    foreach (var island in g.Islands)
                    {
                        if (island.RasterizedMask == null) continue;
                        BlitIsland(tex, island, kind, atlas);
                    }

                tex.Apply(false, false);
                tex.name = atlas.Name + "_" + kind;
                atlas.KindTextures[kind] = tex;
            }

            // 记录 alpha / record alpha presence
            atlas.HasAlpha = false;
            foreach (var g in atlas.Groups)
                foreach (var island in g.Islands)
                    foreach (var t in island.ReferencingTextures)
                        if (t.Classification == ATOTextureClass.Transparent) atlas.HasAlpha = true;

            // pull-push 外扩填充 / pull-push fill empty areas
            foreach (var kv in atlas.KindTextures)
            {
                bool keepAlphaZero = kv.Key == ATOTextureClass.Opaque && atlas.HasAlpha;
                PullPush(kv.Value, atlas, keepAlphaZero);
            }
        }

        /// <summary>(EN) Fill empty atlas pixels with nearest island color (multi-source BFS).
        ///     Keeps alpha 0 for transparent atlases (known bleeding is acceptable). (ZH) 用最近岛颜色填充图集空白（多源 BFS），透明图集 alpha 保持 0（渗色已知可接受）。</summary>
        private void PullPush(Texture2D tex, ATOAtlas atlas, bool keepAlphaZero)
        {
            int w = tex.width, h = tex.height;
            var filled = new bool[w * h];

            // 标记岛覆盖区 / mark island coverage
            foreach (var g in atlas.Groups)
                foreach (var island in g.Islands)
                {
                    if (island.RasterizedMask == null) continue;
                    int pw = island.Rotated90 ? island.ScaledPixelH : island.ScaledPixelW;
                    int ph = island.Rotated90 ? island.ScaledPixelW : island.ScaledPixelH;
                    int px = island.RasterX * ATOPacker.Granularity;
                    int py = island.RasterY * ATOPacker.Granularity;
                    for (int y = py; y < py + ph && y < h; y++)
                        for (int x = px; x < px + pw && x < w; x++)
                            filled[y * w + x] = true;
                }

            var colors = tex.GetPixels();
            var queue = new Queue<int>();

            // 边界种子 / seed boundary filled pixels
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (!filled[i]) continue;
                    bool boundary = false;
                    if (x > 0 && !filled[i - 1]) boundary = true;
                    else if (x < w - 1 && !filled[i + 1]) boundary = true;
                    else if (y > 0 && !filled[i - w]) boundary = true;
                    else if (y < h - 1 && !filled[i + w]) boundary = true;
                    if (boundary) queue.Enqueue(i);
                }

            // BFS 外扩 / BFS expansion
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                int x = i % w, y = i / w;
                var c = colors[i];
                if (keepAlphaZero) c.a = 0f;

                // 4 邻域 / 4-neighbors
                if (x > 0 && !filled[i - 1]) { filled[i - 1] = true; colors[i - 1] = c; queue.Enqueue(i - 1); }
                if (x < w - 1 && !filled[i + 1]) { filled[i + 1] = true; colors[i + 1] = c; queue.Enqueue(i + 1); }
                if (y > 0 && !filled[i - w]) { filled[i - w] = true; colors[i - w] = c; queue.Enqueue(i - w); }
                if (y < h - 1 && !filled[i + w]) { filled[i + w] = true; colors[i + w] = c; queue.Enqueue(i + w); }
            }

            tex.SetPixels(colors);
            tex.Apply(false, false);
        }

        private void BlitIsland(Texture2D atlas, ATOUVIsland island, ATOTextureClass kind, ATOAtlas a)
        {
            // 找到该类型的源贴图 / find source texture of this kind
            ATOTextureRef srcTex = null;
            foreach (var t in island.ReferencingTextures)
            {
                if (KindOf(t) == kind) { srcTex = t; break; }
            }
            if (srcTex == null) return;

            int tw = srcTex.Texture.width, th = srcTex.Texture.height;
            int rx = Mathf.FloorToInt(island.Bounds.xMin * tw);
            int ry = Mathf.FloorToInt(island.Bounds.yMin * th);
            int rw = Mathf.Max(1, Mathf.CeilToInt(island.Bounds.width * tw));
            int rh = Mathf.Max(1, Mathf.CeilToInt(island.Bounds.height * th));
            rw = Mathf.Min(rw, tw - rx); rh = Mathf.Min(rh, th - ry);

            var crop = ATOTextureIO.ReadRegion(srcTex.Texture, rx, ry, rw, rh);
            var resampled = new Color[island.ScaledPixelW * island.ScaledPixelH];
            ATOQuality.ResampleRegion(crop, rw, rh, 0, 0, rw, rh, island.ScaledPixelW, island.ScaledPixelH,
                linearSpace: true, premultiplyAlpha: srcTex.Classification == ATOTextureClass.Transparent, resampled);

            int px = island.RasterX * ATOPacker.Granularity;
            int py = island.RasterY * ATOPacker.Granularity;

            if (island.Rotated90)
            {
                // 旋转 90° 写回 / rotate 90° on write
                for (int y = 0; y < island.ScaledPixelH; y++)
                    for (int x = 0; x < island.ScaledPixelW; x++)
                    {
                        var c = resampled[y * island.ScaledPixelW + x];
                        int nx = px + (island.ScaledPixelH - 1 - y);
                        int ny = py + x;
                        if (nx < atlas.width && ny < atlas.height)
                            atlas.SetPixel(nx, ny, c);
                    }
            }
            else
            {
                atlas.SetPixels(px, py, island.ScaledPixelW, island.ScaledPixelH, resampled);
            }
        }

        private static ATOTextureClass KindOf(ATOTextureRef t)
        {
            switch (t.Usage)
            {
                case ATOTextureUsage.NormalMap: return ATOTextureClass.Normal;
                case ATOTextureUsage.Mask:
                case ATOTextureUsage.Grayscale: return ATOTextureClass.Grayscale;
                default: return ATOTextureClass.Opaque; // 颜色（不透明/透明统一为颜色类）/ color (opaque+transparent → color kind)
            }
        }
    }
}
