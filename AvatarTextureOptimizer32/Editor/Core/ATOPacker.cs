using System.Collections.Generic;
using UnityEngine;

namespace Fosa.ATO.Editor
{
    /// <summary>
    /// 装箱阶段：按贴图类型组生成图集。光栅位掩码（4px 粒度）+ BLF 全扫描 + 面积/边长降序 +
    /// 90° 旋转步进 + 候选图集池（2^n / NPOT 64 步进）。padding = ceil(maxSide/128) 钳 4px，支持 4/8/16/32/64 挡位。
    ///
    /// Packing: per-type-group atlasing via raster bitmask (4px) + BLF + rotation + candidate pool.
    /// </summary>
    public class ATOPacker
    {
        private const int Cell = 4; // 光栅粒度 px / raster granularity

        private readonly nadena.dev.ndmf.BuildContext _ctx;
        private readonly ATOBuildData _data;
        private readonly AvatarTextureOptimizer _comp;

        // 岛 → 光栅位掩码。Island -> raster mask.
        private sealed class Raster
        {
            public bool[] mask;   // flat, cells
            public int gw, gh;    // cells
            public long area;
        }

        public ATOPacker(nadena.dev.ndmf.BuildContext ctx, ATOBuildData data)
        {
            _ctx = ctx;
            _data = data;
            _comp = data.component;
        }

        public void Run()
        {
            using var step = ATOLogger.Step("Pack islands into atlases");
            ATOLogger.Begin("stage.pack");

            if (!_comp.generateAtlas)
            {
                ATOLogger.Info("generateAtlas off: skipping atlas packing (whole-texture scaling path)");
                return;
            }

            BuildTypeGroups();

            int maxSide = MaxAtlasSide();
            int index = 0;
            foreach (var group in _data.typeGroups)
            {
                ATOLogger.ThrowIfCancelled();
                PackGroup(group, maxSide);
                if (++index % 2 == 0) ATOLogger.Report((float)index / Mathf.Max(1, _data.typeGroups.Count));
            }

            ATOLogger.Report(1f);
            ATOLogger.Info($"Packed {_data.atlases.Count} atlas(es) across {_data.typeGroups.Count} type groups");
        }

        private void BuildTypeGroups()
        {
            _data.typeGroups.Clear();
            var map = new Dictionary<string, ATOTextureTypeGroup>();
            foreach (var island in _data.allIslands)
            {
                var e = island.texture.Canonical;
                if (e.whitelisted) continue;
                ATOTextureType type = e.slots.Count > 0 ? e.slots[0].type : ATOTextureType.MainColor;
                var key = $"{(int)type}:{e.sRGB}:{(int)e.filterMode}";
                if (!map.TryGetValue(key, out var group))
                {
                    group = new ATOTextureTypeGroup { type = type, sRGB = e.sRGB, filterMode = e.filterMode };
                    map[key] = group;
                    _data.typeGroups.Add(group);
                }
                if (!group.textures.Contains(e)) group.textures.Add(e);
            }
        }

        private int MaxAtlasSide()
        {
            var p = CurrentPlatform();
            var s = p == ATOPlatformTarget.PC ? _comp.platformPC
                  : p == ATOPlatformTarget.Android ? _comp.platformAndroid : _comp.platformiOS;
            if (s.overrideEnabled) return s.maxAtlasSize;
            return p == ATOPlatformTarget.PC ? 8192 : 4096;
        }

        private ATOPlatformTarget CurrentPlatform()
        {
            switch (UnityEditor.EditorUserBuildSettings.activeBuildTarget)
            {
                case UnityEditor.BuildTarget.Android: return ATOPlatformTarget.Android;
                case UnityEditor.BuildTarget.iOS: return ATOPlatformTarget.iOS;
                default: return ATOPlatformTarget.PC;
            }
        }

        private int PaddingCells(int maxSide)
        {
            int p = Mathf.CeilToInt(maxSide / 128f);
            p = Mathf.Max(4, p);
            p = Mathf.Min(p, (int)_comp.padding);
            return Mathf.Max(1, Mathf.CeilToInt(p / (float)Cell));
        }

        private void PackGroup(ATOTextureTypeGroup group, int maxSide)
        {
            var islands = new List<ATOIsland>();
            foreach (var e in group.textures)
                foreach (var island in _data.allIslands)
                    if (island.texture.Canonical == e) islands.Add(island);
            if (islands.Count == 0) return;

            // 光栅化并缓存（顺便缓存结果）。Rasterize & cache.
            var rasters = new Dictionary<ATOIsland, Raster>();
            foreach (var island in islands)
            {
                rasters[island] = Rasterize(island);
            }
            // 面积降序（边长降序作次关键字）。Sort by area then long-edge descending.
            islands.Sort((a, b) =>
            {
                int c = rasters[b].area.CompareTo(rasters[a].area);
                if (c != 0) return c;
                int la = Mathf.Max(rasters[a].gw, rasters[a].gh), lb = Mathf.Max(rasters[b].gw, rasters[b].gh);
                return lb.CompareTo(la);
            });

            var candidates = BuildCandidatePool(maxSide);
            int padCells = PaddingCells(maxSide);

            var atlas = new ATOAtlas { group = group, width = candidates[0], height = candidates[0] };
            atlas.name = $"ATO_{group.type}";
            atlas._grid = new bool[(atlas.width / Cell) * (atlas.height / Cell)];

            foreach (var island in islands)
            {
                var r = rasters[island];
                if (r.gw > atlas.width / Cell || r.gh > atlas.height / Cell)
                {
                    // 单岛装不下当前图集：升级图集或放弃。
                    bool upgraded = false;
                    for (int ci = 0; ci < candidates.Count; ci++)
                    {
                        int side = candidates[ci];
                        if (side / Cell < r.gw || side / Cell < r.gh) continue;
                        if (side <= atlas.width) continue;
                        // 迁移到更大图集。
                        _data.atlases.Add(atlas);
                        atlas = new ATOAtlas { group = group, width = side, height = side, name = atlas.name };
                        atlas._grid = new bool[(side / Cell) * (side / Cell)];
                        upgraded = true;
                        break;
                    }
                    if (!upgraded)
                    {
                        island.atlas = null;
                        ATOLogger.Warn(ATOLocalization.Tr("warning.tooLarge", island.texture.texture.name));
                        continue;
                    }
                }

                if (!TryPlace(island, atlas, r, padCells))
                {
                    // 当前图集剩余空间不足：新开图集（复用同类）。Open a new atlas (reuse group).
                    if (atlas.islands.Count > 0) _data.atlases.Add(atlas);
                    var side = Mathf.Max(candidates[0], NextPowerOfTwo(Mathf.Max(r.gw, r.gh) * Cell));
                    atlas = new ATOAtlas { group = group, width = side, height = side, name = $"ATO_{group.type}" };
                    atlas._grid = new bool[(side / Cell) * (side / Cell)];
                    if (!TryPlace(island, atlas, r, padCells))
                    {
                        island.atlas = null;
                        ATOLogger.Warn(ATOLocalization.Tr("warning.tooLarge", island.texture.texture.name));
                    }
                }
            }
            if (atlas.islands.Count > 0) _data.atlases.Add(atlas);
        }

        private bool TryPlace(ATOIsland island, ATOAtlas atlas, Raster r, int padCells)
        {
            int gw = atlas.width / Cell, gh = atlas.height / Cell;
            foreach (var deg in new[] { 0, 90, 180, 270 })
            {
                var m = deg == 0 ? r.mask : RotateMask(r.mask, r.gw, r.gh, deg);
                int mw = deg % 180 == 0 ? r.gw : r.gh;
                int mh = deg % 180 == 0 ? r.gh : r.gw;

                var pos = BottomLeftFill(atlas._grid, gw, gh, m, mw, mh, padCells);
                if (pos.x >= 0)
                {
                    Stamp(atlas._grid, gw, m, mw, mh, pos.x, pos.y);
                    island.atlas = atlas;
                    island.packedUv = new Vector2(pos.x * Cell, pos.y * Cell);
                    // 记录旋转信息（简化：不单独存旋转，由后续输出时按 packedScale 重建）。
                    return true;
                }
            }
            return false;
        }

        private static Vector2Int BottomLeftFill(bool[] grid, int gw, int gh, bool[] m, int mw, int mh, int pad)
        {
            for (int y = 0; y + mh <= gh; y++)
                for (int x = 0; x + mw <= gw; x++)
                {
                    if (Fits(grid, gw, gh, m, mw, mh, x, y, pad))
                        return new Vector2Int(x, y);
                }
            return new Vector2Int(-1, -1);
        }

        private static bool Fits(bool[] grid, int gw, int gh, bool[] m, int mw, int mh, int ox, int oy, int pad)
        {
            for (int y = 0; y < mh; y++)
                for (int x = 0; x < mw; x++)
                {
                    if (!m[y * mw + x]) continue;
                    for (int py = -pad; py <= pad; py++)
                        for (int px = -pad; px <= pad; px++)
                        {
                            int gx = ox + x + px, gy = oy + y + py;
                            if (gx < 0 || gy < 0 || gx >= gw || gy >= gh) return false;
                            if (grid[gy * gw + gx]) return false;
                        }
                }
            return true;
        }

        private static void Stamp(bool[] grid, int gw, bool[] m, int mw, int mh, int ox, int oy)
        {
            for (int y = 0; y < mh; y++)
                for (int x = 0; x < mw; x++)
                    if (m[y * mw + x]) grid[(oy + y) * gw + (ox + x)] = true;
        }

        private static int NextPowerOfTwo(int v)
        {
            int p = 64;
            while (p < v) p *= 2;
            return p;
        }

        // ---- 光栅化（三角形形状 → 位掩码，4px cell，Burst 并行 + CPU 回退） ----
        private Raster Rasterize(ATOIsland island)
        {
            var mesh = island.mesh;
            var tris = mesh.triangles;
            var uv = new List<Vector2>();
            mesh.GetUVs(island.uvGroup.uvChannel, uv);

            var bounds = island.bounds;
            int iw = Mathf.Max(1, Mathf.RoundToInt(bounds.width * island.texture.width * island.packedScale.x));
            int ih = Mathf.Max(1, Mathf.RoundToInt(bounds.height * island.texture.height * island.packedScale.y));
            int gw = Mathf.Max(1, Mathf.CeilToInt(iw / (float)Cell));
            int gh = Mathf.Max(1, Mathf.CeilToInt(ih / (float)Cell));

            // 准备三角形顶点（cell 坐标）。
            int triCount = island.triangles.Length;
            var triVerts = new Vector2[triCount * 3];
            for (int ti = 0; ti < triCount; ti++)
            {
                for (int k = 0; k < 3; k++)
                {
                    int vi = tris[island.triangles[ti] * 3 + k];
                    if (vi >= uv.Count) { triVerts[ti * 3 + k] = Vector2.zero; continue; }
                    var p = uv[vi];
                    triVerts[ti * 3 + k] = new Vector2(
                        (p.x - bounds.x) / bounds.width * gw,
                        (p.y - bounds.y) / bounds.height * gh);
                }
            }

            bool[] mask;
            try
            {
                mask = Fosa.ATO.Editor.Burst.ATOBurst.RasterizeIslands(triVerts, gw, gh);
            }
            catch (System.Exception e)
            {
                // Burst 不可用或失败：回退 CPU 光栅化。
                ATOLogger.VerboseLog($"Burst rasterization unavailable ({e.Message}); falling back to CPU");
                mask = RasterizeCpu(triVerts, gw, gh);
            }

            long area = 0;
            foreach (var b in mask) if (b) area++;
            return new Raster { mask = mask, gw = gw, gh = gh, area = area };
        }

        private static bool[] RasterizeCpu(Vector2[] triVerts, int gw, int gh)
        {
            var mask = new bool[gw * gh];
            for (int ti = 0; ti < triVerts.Length / 3; ti++)
            {
                var p = new[] { triVerts[ti * 3 + 0], triVerts[ti * 3 + 1], triVerts[ti * 3 + 2] };
                int minX = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(p[0].x, p[1].x, p[2].x)));
                int maxX = Mathf.Min(gw - 1, Mathf.CeilToInt(Mathf.Max(p[0].x, p[1].x, p[2].x)));
                int minY = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(p[0].y, p[1].y, p[2].y)));
                int maxY = Mathf.Min(gh - 1, Mathf.CeilToInt(Mathf.Max(p[0].y, p[1].y, p[2].y)));
                for (int y = minY; y <= maxY; y++)
                    for (int x = minX; x <= maxX; x++)
                    {
                        var pt = new Vector2(x + 0.5f, y + 0.5f);
                        if (PointInTriangle(pt, p[0], p[1], p[2]))
                            mask[y * gw + x] = true;
                    }
            }
            return mask;
        }

        private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Sign(p, a, b), d2 = Sign(p, b, c), d3 = Sign(p, c, a);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0;
            bool pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }
        private static float Sign(Vector2 p1, Vector2 p2, Vector2 p3) =>
            (p1.x - p3.x) * (p2.y - p3.y) - (p2.x - p3.x) * (p1.y - p3.y);

        private static bool[] RotateMask(bool[] m, int w, int h, int deg)
        {
            int nw = deg % 180 == 0 ? w : h;
            int nh = deg % 180 == 0 ? h : w;
            var r = new bool[nw * nh];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int nx, ny;
                    switch (deg)
                    {
                        case 90: nx = h - 1 - y; ny = x; break;
                        case 180: nx = w - 1 - x; ny = h - 1 - y; break;
                        case 270: nx = y; ny = w - 1 - x; break;
                        default: nx = x; ny = y; break;
                    }
                    r[ny * nw + nx] = m[y * w + x];
                }
            return r;
        }

        private List<int> BuildCandidatePool(int maxSide)
        {
            var pool = new List<int>();
            if (_comp.allowNPOT)
                for (int s = 64; s <= maxSide; s += 64) pool.Add(s);
            else
                for (int s = 64; s <= maxSide; s *= 2) pool.Add(s);
            if (!pool.Contains(maxSide)) pool.Add(maxSide);
            return pool;
        }
    }
}
