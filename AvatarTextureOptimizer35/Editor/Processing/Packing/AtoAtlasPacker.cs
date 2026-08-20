using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// Atlas packer: candidate pool (POT / experimental NPOT), 4px-granularity raster bitmask BLF
    /// (bottom-left full scan), 90° rotation steps via bitmask transposition (disabled for
    /// tangent-data groups — tangent data is never recomputed or rotated). Padding =
    /// max(ceil(candidateMaxSide/128), userMin) px. / 图集装箱器：候选池（POT/实验性 NPOT）、
    /// 4px 粒度光栅位掩码 BLF（自底向左全扫描）、90° 旋转步进（位掩码转置；含切线数据的组禁用旋转——
    /// 切线数据绝不重算/旋转）。padding = max(ceil(候选最大边长/128), 用户最小值) px。
    /// </summary>
    internal sealed class AtoAtlasPacker
    {
        private const int CellPx = 4; // raster granularity. / 光栅粒度。
        private const int MinSide = 64;

        private readonly AtoContext _ctx;
        private readonly bool _npot;
        private readonly int _maxSide;
        private readonly int _userMinPad;
        private readonly bool _isMobile;

        public AtoAtlasPacker(AtoContext ctx)
        {
            _ctx = ctx;
            var settings = ctx.State.Settings;
            _npot = settings.experimentalNpot;
            _userMinPad = (int)settings.minPadding;
            // Mobile (Android/iOS current platform): 4096 max. / 移动端（当前平台为 Android/iOS）：上限 4096。
            var currentPlatform = AtoPlatformUtil.CurrentPlatform();
            _isMobile = currentPlatform != AtoTargetPlatform.PC;
            _maxSide = _isMobile ? 4096 : 8192;
        }

        /// <summary>The maximum atlas side for this build. / 本次构建的图集最大边长。</summary>
        public int MaxSide => _maxSide;

        /// <summary>
        /// Enumerate candidate sizes: area ≥ minArea, sides ≥ per-axis requirements; sorted by
        /// (area asc, long/short asc) — closest to square first. / 枚举候选尺寸：面积 ≥ minArea、
        /// 边长 ≥ 逐轴要求；按（面积升序，长/短升序）排序 —— 最接近正方形优先。
        /// </summary>
        public List<(int w, int h)> EnumerateCandidates(long minAreaNeeded, int minW, int minH)
        {
            var candidates = new List<(int, int)>();

            if (_npot)
            {
                // NPOT: 64px steps; band the enumeration around the target to stay fast. /
                // NPOT：64 步进；围绕目标面积做带宽限制枚举以保持速度。
                var target = Math.Max(MinSide, (int)Math.Sqrt(minAreaNeeded));
                var lo = Math.Max(MinSide, RoundTo64(target / 2));
                var hi = Math.Min(_maxSide, RoundTo64(target * 2));
                for (var w = lo; w <= hi; w += 64)
                {
                    for (var h = lo; h <= hi; h += 64)
                    {
                        if ((long)w * h < minAreaNeeded) continue;
                        if (w < minW || h < minH) continue;
                        candidates.Add((w, h));
                    }
                }
                // If the band was too tight (nothing found), widen once. / 若带宽内无结果，再放宽一次。
                if (candidates.Count == 0)
                {
                    for (var w = MinSide; w <= _maxSide; w += 64)
                    {
                        for (var h = MinSide; h <= _maxSide; h += 64)
                        {
                            if ((long)w * h < minAreaNeeded) continue;
                            if (w < minW || h < minH) continue;
                            candidates.Add((w, h));
                        }
                    }
                }
            }
            else
            {
                // POT: powers of two from 64. / POT：64 起的 2 的幂。
                var sides = new List<int>();
                for (var side = MinSide; side <= _maxSide; side *= 2) sides.Add(side);
                foreach (var w in sides)
                {
                    foreach (var h in sides)
                    {
                        if ((long)w * h < minAreaNeeded) continue;
                        if (w < minW || h < minH) continue;
                        candidates.Add((w, h));
                    }
                }
            }

            candidates.Sort((a, b) =>
            {
                var areaCompare = ((long)a.Item1 * a.Item2).CompareTo((long)b.Item1 * b.Item2);
                if (areaCompare != 0) return areaCompare;
                var aspectA = (float)Mathf.Max(a.Item1, a.Item2) / Mathf.Min(a.Item1, a.Item2);
                var aspectB = (float)Mathf.Max(b.Item1, b.Item2) / Mathf.Min(b.Item1, b.Item2);
                return aspectA.CompareTo(aspectB);
            });
            return candidates;
        }

        private static int RoundTo64(int v) => Mathf.Max(MinSide, (int)(Mathf.Ceil(v / 64f) * 64));

        /// <summary>
        /// Try to pack all islands of the given textures (island → its source texture) into a
        /// candidate atlas. Islands whose positions were fixed by an earlier type group are reused. /
        /// 尝试把给定贴图的全部岛（岛 → 其来源贴图）装入候选图集。此前类型组已定位置的岛直接复用。
        /// </summary>
        public bool TryPack(Dictionary<AtoIsland, Texture2D> islandSources, int minSideW, int minSideH,
            bool allowRotation, out int width, out int height,
            out Dictionary<AtoIsland, (Vector2 origin, int rotation)> newPlacements)
        {
            width = 0;
            height = 0;
            newPlacements = new Dictionary<AtoIsland, (Vector2, int)>();

            // Minimum content area (per-island required pixels on its source texture). /
            // 最小内容面积（各岛在其来源贴图上所需像素）。
            long minArea = 0;
            foreach (var kv in islandSources)
            {
                var island = kv.Key;
                var texture = kv.Value;
                var uvSize = island.UvMax - island.UvMin;
                if (island.PerTextureScale.TryGetValue(texture, out var scale))
                {
                    var requiredW = (long)Mathf.Ceil(uvSize.x * texture.width * scale.x);
                    var requiredH = (long)Mathf.Ceil(uvSize.y * texture.height * scale.y);
                    minArea += Math.Max(1, requiredW) * Math.Max(1, requiredH);
                }
                else
                {
                    minArea += Math.Max(1, (long)(uvSize.x * texture.width)) *
                               Math.Max(1, (long)(uvSize.y * texture.height));
                }
            }

            foreach (var (w, h) in EnumerateCandidates(minArea, minSideW, minSideH))
            {
                if (TryPackInto(islandSources, w, h, allowRotation, out newPlacements))
                {
                    width = w;
                    height = h;
                    return true;
                }
            }
            newPlacements = null;
            return false;
        }

        /// <summary>
        /// Try to pack into ONE candidate of size (W,H). / 尝试装入一个 (W,H) 候选。
        /// </summary>
        private bool TryPackInto(Dictionary<AtoIsland, Texture2D> islandSources, int width, int height,
            bool allowRotation, out Dictionary<AtoIsland, (Vector2 origin, int rotation)> newPlacements)
        {
            newPlacements = new Dictionary<AtoIsland, (Vector2, int)>();
            var islands = islandSources.Keys.ToList();
            var gw = Mathf.Max(1, width / CellPx);
            var gh = Mathf.Max(1, height / CellPx);
            var grid = new byte[gw * gh];

            // Padding in cells: padPx = max(ceil(maxSide/128), userMin); inflate by pad/2 per side. /
            // padding 换算成格：padPx = max(ceil(最大边长/128), 用户最小值)；每侧膨胀 pad/2。
            var maxSide = Mathf.Max(width, height);
            var padPx = Mathf.Max(Mathf.CeilToInt(maxSide / 128f), _userMinPad);
            var padCells = Mathf.Max(1, Mathf.CeilToInt(padPx / 2f / CellPx));

            // ---- fixed islands (already placed by an earlier group) ----
            var fixedPlacements = new List<(AtoIsland island, Vector2 origin, int rotation, byte[] mask)>();
            foreach (var island in islands)
            {
                if (!_ctx.PlacedIslands.TryGetValue(island, out var placed)) continue;
                var (mw, mh) = MaskSize(island, placed.Rotation, gw, gh);
                var cx = Mathf.RoundToInt(placed.UvOrigin.x * gw);
                var cy = Mathf.RoundToInt(placed.UvOrigin.y * gh);
                // Bounds check first: a fixed island outside this (smaller) atlas invalidates it. /
                // 先做边界检查：固定岛超出（更小的）图集则此候选无效。
                if (cx < 0 || cy < 0 || cx + mw > gw || cy + mh > gh) return false;
                var mask = BuildMask(island, placed.Rotation, gw, gh);
                if (mask == null) return false;
                if (!PlaceMask(grid, gw, gh, mask, mw, mh, cx, cy, 0, out _))
                {
                    // Fixed islands must not collide; if they do this candidate is invalid. /
                    // 固定岛不得碰撞；碰撞则此候选无效。
                    return false;
                }
                fixedPlacements.Add((island, placed.UvOrigin, placed.Rotation, mask));
            }

            // ---- new islands: area desc, then max-side desc ----
            var pending = islands
                .Where(i => !_ctx.PlacedIslands.ContainsKey(i))
                .OrderByDescending(i => (i.FinalUvMax.x - i.FinalUvMin.x) * (i.FinalUvMax.y - i.FinalUvMin.y))
                .ThenByDescending(i => Mathf.Max(i.FinalUvMax.x - i.FinalUvMin.x, i.FinalUvMax.y - i.FinalUvMin.y))
                .ToList();

            foreach (var island in pending)
            {
                var maxRotations = allowRotation ? 4 : 1;
                var placedOk = false;
                for (var rotation = 0; rotation < maxRotations; rotation++)
                {
                    var mask = BuildMask(island, rotation, gw, gh);
                    if (mask == null) continue;
                    var (mw, mh) = MaskSize(island, rotation, gw, gh);
                    // BLF full scan. / 自底向左全扫描。
                    for (var y = 0; y <= gh - mh; y++)
                    {
                        for (var x = 0; x <= gw - mw; x++)
                        {
                            if (!Collides(grid, gw, mask, mw, mh, x, y))
                            {
                                PlaceMask(grid, gw, gh, mask, mw, mh, x, y, padCells, out _);
                                // Origin = min corner of the island's UV rect (consistent with
                                // GetPixelRect: x = origin.x × atlasWidth). / 原点 = 岛 UV 矩形的最小角
                                // （与 GetPixelRect 一致：x = origin.x × 图集宽）。
                                var origin = new Vector2(x / (float)gw, y / (float)gh);
                                newPlacements[island] = (origin, rotation);
                                placedOk = true;
                                break;
                            }
                        }
                        if (placedOk) break;
                    }
                    if (placedOk) break;
                }
                if (!placedOk) return false; // atomic: the whole texture must fit. / 原子性：整张贴图必须装下。
            }

            return true;
        }

        /// <summary>
        /// Build the 4px-granularity occupancy mask of an island for a candidate grid size and
        /// rotation. / 为候选网格尺寸与旋转构建岛的 4px 粒度占用掩码。
        /// </summary>
        private byte[] BuildMask(AtoIsland island, int rotation, int gw, int gh)
        {
            // Mask built in the island's own final-UV grid, then rotated. /
            // 掩码在岛自身的最终 UV 网格上构建，再旋转。
            var uvSize = island.FinalUvMax - island.FinalUvMin;
            var mw = Mathf.Max(1, Mathf.RoundToInt(uvSize.x * gw));
            var mh = Mathf.Max(1, Mathf.RoundToInt(uvSize.y * gh));
            var mask = new byte[mw * mh];

            var uvs = new List<Vector2>();
            island.UvGroup.Mesh.GetUVs(island.UvGroup.Channel, uvs);
            AtoRasterizer.Rasterize(uvs, island.Triangles, island.FinalUvMin, island.FinalUvMax,
                mw, mh, mask);

            switch (rotation)
            {
                case 0:
                    return mask;
                case 1: // 90°: transpose. / 90°：转置。
                    return TransposeMask(mask, mw, mh);
                case 2: // 180°: flip both. / 180°：双向翻转。
                    return FlipBoth(mask, mw, mh);
                case 3: // 270°: transpose + flip. / 270°：转置+翻转。
                    return FlipBoth(TransposeMask(mask, mw, mh), mh, mw);
                default:
                    return mask;
            }
        }

        /// <summary>Mask size (after rotation). / 掩码尺寸（旋转后）。</summary>
        private (int, int) MaskSize(AtoIsland island, int rotation, int gw, int gh)
        {
            var uvSize = island.FinalUvMax - island.FinalUvMin;
            var mw = Mathf.Max(1, Mathf.RoundToInt(uvSize.x * gw));
            var mh = Mathf.Max(1, Mathf.RoundToInt(uvSize.y * gh));
            return (rotation & 1) == 1 ? (mh, mw) : (mw, mh);
        }

        private static byte[] TransposeMask(byte[] mask, int mw, int mh)
        {
            var result = new byte[mask.Length];
            for (var y = 0; y < mh; y++)
            {
                for (var x = 0; x < mw; x++)
                {
                    result[x * mh + y] = mask[y * mw + x];
                }
            }
            return result;
        }

        private static byte[] FlipBoth(byte[] mask, int mw, int mh)
        {
            var result = new byte[mask.Length];
            for (var y = 0; y < mh; y++)
            {
                for (var x = 0; x < mw; x++)
                {
                    result[(mh - 1 - y) * mw + (mw - 1 - x)] = mask[y * mw + x];
                }
            }
            return result;
        }

        private static bool Collides(byte[] grid, int gw, byte[] mask, int mw, int mh, int px, int py)
        {
            for (var y = 0; y < mh; y++)
            {
                var row = (py + y) * gw + px;
                for (var x = 0; x < mw; x++)
                {
                    if (mask[y * mw + x] != 0 && grid[row + x] != 0) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Write the mask into the grid, inflated by padCells on each side. Returns the rect. /
        /// 把掩码写入网格，每侧膨胀 padCells。返回矩形。
        /// </summary>
        private static bool PlaceMask(byte[] grid, int gw, int gh, byte[] mask, int mw, int mh,
            int px, int py, int padCells, out (int x, int y, int w, int h) rect)
        {
            var x0 = Mathf.Max(0, px - padCells);
            var y0 = Mathf.Max(0, py - padCells);
            var x1 = Mathf.Min(gw - 1, px + mw - 1 + padCells);
            var y1 = Mathf.Min(gh - 1, py + mh - 1 + padCells);
            rect = (x0, y0, x1 - x0 + 1, y1 - y0 + 1);

            for (var y = y0; y <= y1; y++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    grid[y * gw + x] = 1;
                }
            }
            return true;
        }
    }
}
