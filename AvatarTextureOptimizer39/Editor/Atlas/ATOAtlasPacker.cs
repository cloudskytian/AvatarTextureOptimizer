// Avatar Texture Optimizer (ATO)
// Bilingual comments: English + Simplified Chinese. 双语注释：英文 + 简体中文。

using System;
using System.Collections.Generic;
using System.Linq;
using AvatarTextureOptimizer.Editor.Core;
using AvatarTextureOptimizer.Editor.UVIsland;
using UnityEngine;

namespace AvatarTextureOptimizer.Editor.Atlas
{
    /// <summary>A rasterized island bitmap at 4px granularity. 4px 粒度岛位图。</summary>
    public sealed class ATOIslandBitmap
    {
        public ATOUVIslandEntry Entry;
        public int PixelW, PixelH;   // target pixel size after quality scaling. 质量缩放后的目标像素尺寸。
        public int CellW, CellH;     // size in 4px cells. 4px 单元尺寸。
        public bool[,] Cells;        // occupancy. 占用。
        public long Area => (long)CellW * CellH;

        /// <summary>
        /// False for normal-map islands (tangent data is kept as-is and must not rotate).
        /// 法线贴图岛为 false（切线数据保持原样、不得旋转）。
        /// </summary>
        public bool AllowRotation = true;
    }

    /// <summary>Placement of one island in an atlas. 一个岛在图集中的摆放。</summary>
    public sealed class ATOPlacement
    {
        public ATOUVIslandEntry Entry;
        public int PixelX, PixelY;   // position in atlas pixels. 图集像素位置。
        public int PixelW, PixelH;   // target pixel size. 目标像素尺寸。
        public int Rotation;         // 0/90/180/270.
        public int AtlasSize;        // edge length of the atlas. 图集边长。
    }

    /// <summary>A single atlas: its size and placements. 单个图集：尺寸与摆放。</summary>
    public sealed class ATOAtlasResult
    {
        public int Size;
        public List<ATOPlacement> Placements = new List<ATOPlacement>();
    }

    /// <summary>Packing result for one type group. 一个类型组的装箱结果。</summary>
    public sealed class ATOAtlasGroupResult
    {
        public string TypeGroupKey;
        public List<ATOAtlasResult> Atlases = new List<ATOAtlasResult>();
        public List<ATOUVIslandEntry> Dropped = new List<ATOUVIslandEntry>();
    }

    /// <summary>
    /// Atlas packer: BLF with 90°-step rotation, 4px bitmask, candidate pool.
    /// All islands of one texture are placed atomically into the same atlas.
    /// 图集装箱：BLF + 90° 步进旋转 + 4px 位掩码 + 候选池。同贴图岛原子装箱。
    /// </summary>
    public static class ATOAtlasPacker
    {
        private const int CellSize = 4;

        public static ATOAtlasGroupResult Pack(List<ATOUVIslandEntry> entries, string key,
            int maxEdge, bool npot, int minPadding, ATOBuildState state)
        {
            var result = new ATOAtlasGroupResult { TypeGroupKey = key };

            // Convert pixel padding → cell padding (4px cells). 像素 padding → 单元 padding。
            int paddingCells = Mathf.Max(1, Mathf.CeilToInt(minPadding / (float)CellSize));

            var bitmaps = new List<ATOIslandBitmap>();
            foreach (var e in entries)
            {
                var bm = Rasterize(e, state);
                if (bm != null) bitmaps.Add(bm);
            }

            // Group by texture; sort textures by total area desc. 按贴图分组、按总面积降序。
            var byTexture = new Dictionary<Texture2D, List<ATOIslandBitmap>>();
            foreach (var bm in bitmaps)
            {
                var keyTex = bm.Entry.Textures.FirstOrDefault(t => t != null)?.Texture;
                if (keyTex == null) continue;
                if (!byTexture.TryGetValue(keyTex, out var l)) { l = new List<ATOIslandBitmap>(); byTexture[keyTex] = l; }
                l.Add(bm);
            }

            var queues = byTexture.Values.OrderByDescending(l => l.Sum(b => b.Area)).ToList();
            var pool = BuildPool(maxEdge, npot);

            foreach (var queue in queues)
            {
                var islands = queue.OrderByDescending(b => b.Area).ToList();

                if (!TryPackTexture(islands, result, pool, paddingCells))
                {
                    foreach (var b in islands) result.Dropped.Add(b.Entry);
                    ATOLog.Warning($"Texture {islands[0].Entry.Textures.FirstOrDefault(t => t != null)?.Texture?.name} " +
                                   $"could not fit; UV group skips atlas-ization. / 无法装入最大图集，该 UV 组跳过图集化。");
                }
            }

            return result;
        }

        private static bool TryPackTexture(List<ATOIslandBitmap> islands, ATOAtlasGroupResult result,
            List<int> pool, int paddingCells)
        {
            long required = islands.Sum(b => b.Area);

            // Reuse existing atlases. 复用已有图集。
            foreach (var atlas in result.Atlases)
            {
                var grid = new AtlasGrid(atlas.Size);
                grid.RestoreFrom(atlas);
                if (TryPlaceAll(islands, grid, paddingCells, atlas.Placements))
                    return true;
            }

            // New atlas: smallest candidate that fits. 新图集：最小可行候选。
            foreach (var size in pool)
            {
                long cells = (long)(size / CellSize) * (size / CellSize);
                if (cells < required) continue;
                int maxCellDim = islands.Max(b => Mathf.Max(b.CellW, b.CellH));
                if (size / CellSize < maxCellDim) continue;

                var atlas = new ATOAtlasResult { Size = size };
                var grid = new AtlasGrid(size);
                if (TryPlaceAll(islands, grid, paddingCells, atlas.Placements))
                {
                    result.Atlases.Add(atlas);
                    return true;
                }
            }

            return false;
        }

        private static bool TryPlaceAll(List<ATOIslandBitmap> islands, AtlasGrid grid, int paddingCells,
            List<ATOPlacement> placements)
        {
            var snapshot = grid.Snapshot();
            var temp = new List<ATOPlacement>();
            foreach (var island in islands)
            {
                var p = grid.TryPlace(island, paddingCells);
                if (p == null) { grid.Restore(snapshot); return false; }
                temp.Add(p);
            }
            // Commit only on full success. 仅全部成功才提交。
            placements.AddRange(temp);
            return true;
        }

        private static List<int> BuildPool(int maxEdge, bool npot)
        {
            var pool = new List<int>();
            if (npot) for (int s = 64; s <= maxEdge; s += 64) pool.Add(s);
            else for (int s = 64; s <= maxEdge; s *= 2) pool.Add(s);
            return pool;
        }

        private static ATOIslandBitmap Rasterize(ATOUVIslandEntry entry, ATOBuildState state)
        {
            int maxRes = 1;
            foreach (var t in entry.Textures) if (t != null) maxRes = Mathf.Max(maxRes, Mathf.Max(t.Width, t.Height));

            int srcW = Mathf.Max(1, Mathf.CeilToInt(entry.NormalizedBounds.width * maxRes));
            int srcH = Mathf.Max(1, Mathf.CeilToInt(entry.NormalizedBounds.height * maxRes));

            int pxW = Mathf.Max(1, Mathf.RoundToInt(srcW * entry.AnisoScale.x));
            int pxH = Mathf.Max(1, Mathf.RoundToInt(srcH * entry.AnisoScale.y));

            var bm = new ATOIslandBitmap { Entry = entry, PixelW = pxW, PixelH = pxH };

            // Normal maps keep tangent data as-is → never rotate. 法线贴图切线保持原样 → 禁止旋转。
            var anyTex = entry.Textures.Find(t => t != null);
            if (anyTex != null && anyTex.Category == ATOTextureCategory.Normal)
                bm.AllowRotation = false;

            int cellW = Mathf.Max(1, Mathf.CeilToInt(pxW / (float)CellSize));
            int cellH = Mathf.Max(1, Mathf.CeilToInt(pxH / (float)CellSize));
            bm.CellW = cellW; bm.CellH = cellH;
            bm.Cells = new bool[cellH, cellW];

            var (uvs, tris) = GetUvData(entry);
            if (uvs == null) return null;

            var fine = ATOTriangleRasterizer.Rasterize(uvs, tris, entry.Island.Triangles,
                entry.NormalizedBounds, cellW, cellH);
            for (int y = 0; y < cellH; y++)
                for (int x = 0; x < cellW; x++)
                    bm.Cells[y, x] = fine[y * cellW + x];

            return bm;
        }

        private static (Vector2[], int[]) GetUvData(ATOUVIslandEntry entry)
        {
            var mesh = entry.Renderer is SkinnedMeshRenderer smr ? smr.sharedMesh
                : entry.Renderer is MeshRenderer mr ? mr.GetComponent<MeshFilter>()?.sharedMesh : null;
            if (mesh == null) return (null, null);
            if (entry.UVChannel == 0) return (mesh.uv, mesh.GetTriangles(entry.SubMeshIndex));
            if (entry.UVChannel == 1) return (mesh.uv2, mesh.GetTriangles(entry.SubMeshIndex));
            var l = new List<Vector2>();
            if (!mesh.GetUVs(entry.UVChannel, l)) return (null, null);
            return (l.ToArray(), mesh.GetTriangles(entry.SubMeshIndex));
        }

        private sealed class AtlasGrid
        {
            public readonly int Size;
            public readonly int Cells;
            private readonly bool[,] _occ;

            public AtlasGrid(int size)
            {
                Size = size;
                Cells = size / CellSize;
                _occ = new bool[Cells, Cells];
            }

            public void RestoreFrom(ATOAtlasResult atlas)
            {
                foreach (var p in atlas.Placements)
                {
                    // Rebuild occupancy from committed placements. 从已提交摆放重建占用。
                    Stamp(p);
                }
            }

            public bool[,] Snapshot() => (bool[,])_occ.Clone();
            public void Restore(bool[,] s) => Array.Copy(s, _occ, s.Length);

            public ATOPlacement TryPlace(ATOIslandBitmap island, int pad)
            {
                var rotations = island.AllowRotation ? new[] { 0, 90, 180, 270 } : new[] { 0 };
                foreach (int rot in rotations)
                {
                    int w = island.CellW, h = island.CellH;
                    if (rot == 90 || rot == 270) (w, h) = (h, w);
                    if (w > Cells || h > Cells) continue;

                    for (int y = 0; y + h <= Cells; y++)
                    for (int x = 0; x + w <= Cells; x++)
                    {
                        if (Fits(island, rot, x, y, pad))
                        {
                            Stamp(island, rot, x, y);
                            return new ATOPlacement
                            {
                                Entry = island.Entry,
                                Rotation = rot,
                                PixelX = x * CellSize,
                                PixelY = y * CellSize,
                                PixelW = island.PixelW,
                                PixelH = island.PixelH,
                                AtlasSize = Size,
                            };
                        }
                    }
                }
                return null;
            }

            private bool Fits(ATOIslandBitmap island, int rot, int ox, int oy, int pad)
            {
                int w = island.CellW, h = island.CellH;
                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (!island.Cells[y, x]) continue;
                    (int px, int py) = Map(rot, x, y, w, h, ox, oy);
                    for (int dy = -pad; dy <= pad; dy++)
                    for (int dx = -pad; dx <= pad; dx++)
                    {
                        int nx = px + dx, ny = py + dy;
                        if (nx < 0 || ny < 0 || nx >= Cells || ny >= Cells) return false;
                        if (_occ[ny, nx]) return false;
                    }
                }
                return true;
            }

            private void Stamp(ATOIslandBitmap island, int rot, int ox, int oy)
            {
                int w = island.CellW, h = island.CellH;
                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (!island.Cells[y, x]) continue;
                    (int px, int py) = Map(rot, x, y, w, h, ox, oy);
                    _occ[py, px] = true;
                }
            }

            private void Stamp(ATOPlacement p)
            {
                // Rebuild occupancy for a committed placement. 重建已提交摆放的占用。
                int cellW = Mathf.Max(1, Mathf.CeilToInt(p.PixelW / (float)CellSize));
                int cellH = Mathf.Max(1, Mathf.CeilToInt(p.PixelH / (float)CellSize));
                int ox = p.PixelX / CellSize, oy = p.PixelY / CellSize;
                int w = cellW, h = cellH;
                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    (int px, int py) = Map(p.Rotation, x, y, w, h, ox, oy);
                    _occ[py, px] = true;
                }
            }

            private static (int, int) Map(int rot, int x, int y, int w, int h, int ox, int oy)
            {
                return rot switch
                {
                    90 => (ox + (h - 1 - y), oy + x),
                    180 => (ox + (w - 1 - x), oy + (h - 1 - y)),
                    270 => (ox + y, oy + (w - 1 - x)),
                    _ => (ox + x, oy + y),
                };
            }
        }
    }
}
