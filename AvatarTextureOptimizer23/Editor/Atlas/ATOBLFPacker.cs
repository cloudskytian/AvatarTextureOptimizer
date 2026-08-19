using System;
using System.Collections.Generic;
using UnityEngine;
using FOSA.AvatarTextureOptimizer;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Full-scan Bottom-Left-Fill on rasterized island shapes, with 90° rotation (bitmask transpose).
    /// 在光栅化后的岛形状上做全扫描 BLF，支持 90° 旋转（位掩码转置）。
    /// </summary>
    internal static class ATOBLFPacker
    {
        public sealed class Item
        {
            public ATOIsland Island;
            public ATOBitmask Mask;
            public ATOBitmask MaskRot;
            public int PixelW;
            public int PixelH;
            public int Area;
        }

        public struct Placement
        {
            public ATOIsland Island;
            public int X;
            public int Y;
            public bool Rotated;
            public int PixelW;
            public int PixelH;
        }

        public static bool Pack(IList<Item> items, int atlasW, int atlasH, int paddingPx, out List<Placement> result)
        {
            result = new List<Placement>(items.Count);
            if (items.Count == 0) return true;

            // Sort area desc, then long side desc. / 面积降序，再长边降序。
            var order = new List<Item>(items);
            order.Sort((a, b) =>
            {
                var c = b.Area.CompareTo(a.Area);
                if (c != 0) return c;
                var la = Math.Max(a.PixelW, a.PixelH);
                var lb = Math.Max(b.PixelW, b.PixelH);
                return lb.CompareTo(la);
            });

            var gran = ATORasterizer.Granularity;
            var padCells = Math.Max(0, (paddingPx + gran - 1) / gran);
            var mw = Math.Max(1, atlasW / gran);
            var mh = Math.Max(1, atlasH / gran);
            var occ = ATOBitmask.Allocate(mw, mh);

            foreach (var it in order)
            {
                if (!TryPlace(it, occ, padCells, atlasW, atlasH, out var pl))
                    return false;
                result.Add(pl);
            }
            return true;
        }

        private static bool TryPlace(Item it, ATOBitmask occ, int padCells, int atlasW, int atlasH, out Placement pl)
        {
            pl = default;
            var candidates = new[]
            {
                (mask: it.Mask, rot: false, pw: it.PixelW, ph: it.PixelH),
                (mask: it.MaskRot, rot: true, pw: it.PixelH, ph: it.PixelW)
            };

            var bestX = int.MaxValue;
            var bestY = int.MaxValue;
            var bestRot = false;
            var found = false;

            foreach (var c in candidates)
            {
                if (c.mask.Bits == null) continue;
                var iw = c.mask.Width + padCells;
                var ih = c.mask.Height + padCells;
                if (iw > occ.Width || ih > occ.Height) continue;

                for (int y = 0; y <= occ.Height - ih; y++)
                {
                    for (int x = 0; x <= occ.Width - iw; x++)
                    {
                        if (!Fits(occ, c.mask, x, y, padCells)) continue;
                        // Bottom-left: smaller y, then smaller x. / 先下再左。
                        if (!found || y < bestY || (y == bestY && x < bestX))
                        {
                            found = true;
                            bestX = x;
                            bestY = y;
                            bestRot = c.rot;
                            pl = new Placement
                            {
                                Island = it.Island,
                                X = x * ATORasterizer.Granularity,
                                Y = y * ATORasterizer.Granularity,
                                Rotated = c.rot,
                                PixelW = c.pw,
                                PixelH = c.ph
                            };
                        }
                        // First X on this row that fits is the leftmost; skip rest of the row if we only want BL.
                        // 这一行第一个能放下的就是最左；为了全扫描找更优，这里仍继续扫。
                    }
                }
            }

            if (!found) return false;
            var used = bestRot ? it.MaskRot : it.Mask;
            Stamp(occ, used, bestX, bestY, padCells);
            return true;
        }

        private static bool Fits(ATOBitmask occ, ATOBitmask mask, int x, int y, int pad)
        {
            for (int my = 0; my < mask.Height; my++)
            for (int mx = 0; mx < mask.Width; mx++)
            {
                if (!mask[mx, my]) continue;
                // Occupy the cell plus padding halo. / 占用该格以及 padding 光晕。
                for (int dy = -pad; dy <= pad; dy++)
                for (int dx = -pad; dx <= pad; dx++)
                {
                    var ox = x + mx + dx;
                    var oy = y + my + dy;
                    if ((uint)ox >= (uint)occ.Width || (uint)oy >= (uint)occ.Height) return false;
                    if (occ[ox, oy]) return false;
                }
            }
            return true;
        }

        private static void Stamp(ATOBitmask occ, ATOBitmask mask, int x, int y, int pad)
        {
            for (int my = 0; my < mask.Height; my++)
            for (int mx = 0; mx < mask.Width; mx++)
            {
                if (!mask[mx, my]) continue;
                for (int dy = -pad; dy <= pad; dy++)
                for (int dx = -pad; dx <= pad; dx++)
                {
                    var ox = x + mx + dx;
                    var oy = y + my + dy;
                    if ((uint)ox < (uint)occ.Width && (uint)oy < (uint)occ.Height)
                        occ[ox, oy] = true;
                }
            }
        }

        public static List<(int w, int h)> BuildCandidatePool(ATOResolvedSettings s)
        {
            var list = new List<(int, int)>();
            var max = s.MaxAtlasEdge;
            const int min = 64;
            if (s.experimentalNpot)
            {
                for (int e = min; e <= max; e += 64)
                {
                    for (int w = min; w <= e; w += 64)
                    {
                        list.Add((w, e));
                        if (w != e) list.Add((e, w));
                    }
                }
            }
            else
            {
                for (int e = min; e <= max; e <<= 1)
                {
                    for (int w = min; w <= e; w <<= 1)
                    {
                        list.Add((w, e));
                        if (w != e) list.Add((e, w));
                    }
                }
            }

            // Area asc, then long/short ratio asc (closer to square first).
            // 面积升序，再长/短比升序（越接近正方形越优先）。
            list.Sort((a, b) =>
            {
                var aa = a.Item1 * a.Item2;
                var ba = b.Item1 * b.Item2;
                var c = aa.CompareTo(ba);
                if (c != 0) return c;
                var ra = Math.Max(a.Item1, a.Item2) / (float)Math.Max(1, Math.Min(a.Item1, a.Item2));
                var rb = Math.Max(b.Item1, b.Item2) / (float)Math.Max(1, Math.Min(b.Item1, b.Item2));
                return ra.CompareTo(rb);
            });
            return list;
        }

        public static int PaddingFor(int atlasMaxSide, ATOMinPadding min)
        {
            var computed = Mathf.CeilToInt(atlasMaxSide / 128f);
            return Math.Max(computed, (int)min);
        }
    }
}
