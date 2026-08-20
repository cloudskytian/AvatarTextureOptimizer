using System;
using System.Collections.Generic;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Burst-friendly 4px bitmask BLF packer with 90° rotation. Not rectangle packing.
    /// 4px 位掩码全扫描 BLF + 90° 旋转。不是矩形装箱。
    /// Candidate pool: POT or NPOT 64-step. Sorted by area then aspect (closer to square first).
    /// 候选池：2 的幂或 64 步进 NPOT。按面积升序、长/短比升序（越接近正方形越优先）。
    /// </summary>
    public static class AtlasPacker
    {
        public struct Place
        {
            public int X, Y, W, H;
            public bool Rot90;
            public bool Ok;
        }

        public static List<Vector2Int> BuildPool(bool npot, int maxSide)
        {
            var sizes = new List<int>();
            if (npot)
            {
                for (int s = 64; s <= maxSide; s += 64) sizes.Add(s);
            }
            else
            {
                for (int s = 64; s <= maxSide; s *= 2) sizes.Add(s);
            }

            var pool = new List<Vector2Int>();
            foreach (var w in sizes)
            foreach (var h in sizes)
            {
                if (Math.Max(w, h) > maxSide) continue;
                float aspect = Math.Max(w, h) / (float)Math.Max(1, Math.Min(w, h));
                if (aspect > 4.01f) continue;
                pool.Add(new Vector2Int(w, h));
            }
            pool.Sort((a, b) =>
            {
                long aa = (long)a.x * a.y, ba = (long)b.x * b.y;
                int c = aa.CompareTo(ba);
                if (c != 0) return c;
                float aspA = Math.Max(a.x, a.y) / (float)Math.Max(1, Math.Min(a.x, a.y));
                float aspB = Math.Max(b.x, b.y) / (float)Math.Max(1, Math.Min(b.x, b.y));
                return aspA.CompareTo(aspB);
            });
            return pool;
        }

        public static int PaddingFor(int maxSide, int minPad)
        {
            int p = Mathf.CeilToInt(maxSide / 128f);
            return Mathf.Max(4, Mathf.Max(minPad, p));
        }

        /// <summary>
        /// Try to pack islands into atlas of size (aw,ah) at 4px grid. / 在 4px 网格上把岛装进 (aw,ah)。
        /// </summary>
        public static bool TryPack(List<UvIsland> islands, int aw, int ah, int padding, List<Place> places)
        {
            places.Clear();
            int gw = aw / 4, gh = ah / 4;
            int pad = Math.Max(1, (padding + 3) / 4);
            var occ = Bitmask2D.Create(gw, gh);

            // Sort: raster area desc, then long side desc. / 光栅面积降序，然后长边降序。
            var order = new List<int>(islands.Count);
            for (int i = 0; i < islands.Count; i++) order.Add(i);
            order.Sort((i, j) =>
            {
                int ai = islands[i].Shape != null ? islands[i].Shape.CountBits() : islands[i].OrigPixelW * islands[i].OrigPixelH;
                int aj = islands[j].Shape != null ? islands[j].Shape.CountBits() : islands[j].OrigPixelW * islands[j].OrigPixelH;
                int c = aj.CompareTo(ai);
                if (c != 0) return c;
                int li = Math.Max(islands[i].OrigPixelW, islands[i].OrigPixelH);
                int lj = Math.Max(islands[j].OrigPixelW, islands[j].OrigPixelH);
                return lj.CompareTo(li);
            });

            foreach (var idx in order)
            {
                var isl = islands[idx];
                var shape = isl.Shape ?? Bitmask2D.Create(Math.Max(1, (isl.OrigPixelW + 3) / 4), Math.Max(1, (isl.OrigPixelH + 3) / 4));
                var rot = shape.Rotated90();
                if (!TryPlaceOne(occ, shape, rot, pad, gw, gh, out var p))
                    return false;
                places.Add(p);
                // Write back later by idx. Keep parallel to islands order via remap.
            }

            // Reorder places to original island index. / 按原岛下标重排。
            var mapped = new Place[islands.Count];
            for (int k = 0; k < order.Count; k++) mapped[order[k]] = places[k];
            places.Clear();
            places.AddRange(mapped);
            return true;
        }

        private static bool TryPlaceOne(Bitmask2D occ, Bitmask2D shape, Bitmask2D rot, int pad, int gw, int gh, out Place place)
        {
            place = default;
            var candidates = new[] { (shape, false), (rot, true) };
            // Prefer the orientation with larger long side first already encoded in shape vs rot.
            foreach (var (m, r90) in candidates)
            {
                int mw = m.Width + pad;
                int mh = m.Height + pad;
                if (mw > gw || mh > gh) continue;
                for (int y = 0; y <= gh - mh; y++)
                for (int x = 0; x <= gw - mw; x++)
                {
                    if (!Fits(occ, m, x, y)) continue;
                    Stamp(occ, m, x, y);
                    place = new Place { X = x * 4, Y = y * 4, W = m.Width * 4, H = m.Height * 4, Rot90 = r90, Ok = true };
                    return true;
                }
            }
            return false;
        }

        private static bool Fits(Bitmask2D occ, Bitmask2D m, int ox, int oy)
        {
            for (int y = 0; y < m.Height; y++)
            {
                int oy2 = oy + y;
                for (int x = 0; x < m.Width; x++)
                {
                    if (!m.Get(x, y)) continue;
                    if (occ.Get(ox + x, oy2)) return false;
                }
            }
            return true;
        }

        private static void Stamp(Bitmask2D occ, Bitmask2D m, int ox, int oy)
        {
            for (int y = 0; y < m.Height; y++)
            for (int x = 0; x < m.Width; x++)
                if (m.Get(x, y)) occ.Set(ox + x, oy + y);
        }
    }
}
