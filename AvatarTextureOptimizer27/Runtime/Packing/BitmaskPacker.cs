using System;
using System.Collections.Generic;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>
    /// 4px-granularity raster bitmask + full-scan BLF + 90° rotate (transpose).
    /// 4px 粒度光栅位掩码 + 全扫描 BLF + 90 度转置旋转。法线切线数据不重算。
    /// </summary>
    public static class BitmaskPacker
    {
        public const int Granularity = 4;

        public struct IslandMask
        {
            public int Id;
            public int W, H;
            public ulong[] Bits; // rows of bit packs
            public int StrideWords;
            public bool Rotated;
        }

        public struct Placement
        {
            public int Id;
            public int X, Y;
            public int W, H;
            public bool Rotated;
        }

        public static IslandMask Rasterize(IList<Vector2> uvPixels, int texW, int texH)
        {
            int minX = int.MaxValue, minY = int.MaxValue, maxX = 0, maxY = 0;
            var pts = new List<Vector2Int>(uvPixels.Count);
            for (int i = 0; i < uvPixels.Count; i++)
            {
                int x = Mathf.Clamp(Mathf.FloorToInt(uvPixels[i].x), 0, Math.Max(0, texW - 1));
                int y = Mathf.Clamp(Mathf.FloorToInt(uvPixels[i].y), 0, Math.Max(0, texH - 1));
                pts.Add(new Vector2Int(x, y));
                minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
            }
            if (pts.Count == 0)
                return new IslandMask { W = 1, H = 1, Bits = new ulong[1], StrideWords = 1 };

            int gw = Math.Max(1, (maxX - minX + Granularity) / Granularity);
            int gh = Math.Max(1, (maxY - minY + Granularity) / Granularity);
            int words = (gw + 63) / 64;
            var bits = new ulong[words * gh];
            for (int i = 0; i < pts.Count; i++)
            {
                int gx = (pts[i].x - minX) / Granularity;
                int gy = (pts[i].y - minY) / Granularity;
                bits[gy * words + gx / 64] |= 1UL << (gx & 63);
            }
            return new IslandMask { W = gw, H = gh, Bits = bits, StrideWords = words };
        }

        public static IslandMask Transpose(in IslandMask m)
        {
            int nw = m.H, nh = m.W;
            int words = (nw + 63) / 64;
            var bits = new ulong[words * nh];
            for (int y = 0; y < m.H; y++)
            for (int x = 0; x < m.W; x++)
            {
                int word = x / 64;
                if ((m.Bits[y * m.StrideWords + word] & (1UL << (x & 63))) == 0) continue;
                int nx = y;
                int ny = m.W - 1 - x;
                bits[ny * words + nx / 64] |= 1UL << (nx & 63);
            }
            return new IslandMask { Id = m.Id, W = nw, H = nh, Bits = bits, StrideWords = words, Rotated = !m.Rotated };
        }

        public static bool TryPack(List<IslandMask> islands, int atlasW, int atlasH, int paddingCells, List<Placement> outPlacements)
        {
            outPlacements.Clear();
            int aw = atlasW / Granularity;
            int ah = atlasH / Granularity;
            int words = (aw + 63) / 64;
            var occ = new ulong[words * ah];

            islands.Sort((a, b) =>
            {
                int aa = a.W * a.H, bb = b.W * b.H;
                int c = bb.CompareTo(aa);
                return c != 0 ? c : Math.Max(b.W, b.H).CompareTo(Math.Max(a.W, a.H));
            });

            foreach (var raw in islands)
            {
                IslandMask[] orients = { raw, Transpose(raw) };
                bool placed = false;
                Placement best = default;
                int bestY = int.MaxValue, bestX = int.MaxValue;

                foreach (var isl in orients)
                {
                    int pw = isl.W + paddingCells;
                    int ph = isl.H + paddingCells;
                    if (pw > aw || ph > ah) continue;
                    for (int y = 0; y <= ah - ph; y++)
                    {
                        for (int x = 0; x <= aw - pw; x++)
                        {
                            if (!Fits(occ, words, aw, isl, x, y, paddingCells)) continue;
                            if (y < bestY || (y == bestY && x < bestX))
                            {
                                bestY = y; bestX = x;
                                best = new Placement
                                {
                                    Id = isl.Id, X = x * Granularity, Y = y * Granularity,
                                    W = isl.W * Granularity, H = isl.H * Granularity,
                                    Rotated = isl.Rotated
                                };
                                placed = true;
                            }
                            break;
                        }
                        if (placed && bestY == y) { /* still scan lower x on this row only via inner */ }
                    }
                }

                if (!placed) return false;
                var chosen = best.Rotated ? Transpose(raw) : raw;
                Stamp(occ, words, chosen, best.X / Granularity, best.Y / Granularity, paddingCells);
                outPlacements.Add(best);
            }
            return true;
        }

        static bool Fits(ulong[] occ, int words, int aw, in IslandMask isl, int x, int y, int pad)
        {
            for (int row = 0; row < isl.H + pad; row++)
            {
                int oy = y + row;
                for (int col = 0; col < isl.W + pad; col++)
                {
                    int ox = x + col;
                    if (ox >= aw) return false;
                    if ((occ[oy * words + ox / 64] & (1UL << (ox & 63))) != 0)
                    {
                        if (row < isl.H && col < isl.W)
                        {
                            int word = col / 64;
                            bool solid = (isl.Bits[row * isl.StrideWords + word] & (1UL << (col & 63))) != 0;
                            if (solid) return false;
                        }
                        else return false;
                    }
                }
            }
            return true;
        }

        static void Stamp(ulong[] occ, int words, in IslandMask isl, int x, int y, int pad)
        {
            for (int row = 0; row < isl.H + pad; row++)
            for (int col = 0; col < isl.W + pad; col++)
            {
                int ox = x + col;
                int oy = y + row;
                occ[oy * words + ox / 64] |= 1UL << (ox & 63);
            }
        }

        public static List<Vector2Int> CandidateSizes(bool npot, int maxEdge, int minEdge = 64)
        {
            var list = new List<Vector2Int>();
            if (!npot)
            {
                for (int w = minEdge; w <= maxEdge; w <<= 1)
                for (int h = minEdge; h <= maxEdge; h <<= 1)
                    list.Add(new Vector2Int(w, h));
            }
            else
            {
                for (int w = minEdge; w <= maxEdge; w += 64)
                for (int h = minEdge; h <= maxEdge; h += 64)
                    list.Add(new Vector2Int(w, h));
            }
            list.Sort((a, b) =>
            {
                long aa = (long)a.x * a.y, bb = (long)b.x * b.y;
                int c = aa.CompareTo(bb);
                if (c != 0) return c;
                float ra = Aspect(a), rb = Aspect(b);
                return ra.CompareTo(rb);
            });
            return list;
        }

        static float Aspect(Vector2Int s) => Mathf.Max(s.x, s.y) / (float)Mathf.Max(1, Mathf.Min(s.x, s.y));
    }
}
