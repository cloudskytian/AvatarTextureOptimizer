// English: 4px-granularity bitmask BLF packer. Island-shape packing, 90° rotate (normals not rebuilt).
// 中文：4px 粒度位掩码 BLF 装箱。按岛形状装箱，可旋转 90°（法线切线不重算）。
using System;
using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.ato.editor
{
    public sealed class AtoPackedIsland
    {
        public AtoIsland Island;
        public Texture2D Source;
        public int X, Y, W, H;
        public bool Rotated;
        public ulong[] Mask; // rows of bitmasks, W bits, H rows (granule space)
        public int GW, GH;
    }

    public sealed class AtoAtlasResult
    {
        public int Width, Height;
        public readonly List<AtoPackedIsland> Items = new List<AtoPackedIsland>();
        public float Utilization;
        public string TypeKey;
        public bool Abandoned;
    }

    public static class AtoPacker
    {
        public const int Granule = 4;

        public static List<int> CandidateSizes(bool npot, int maxEdge)
        {
            var list = new List<int>();
            if (npot)
            {
                for (int s = 64; s <= maxEdge; s += 64) list.Add(s);
            }
            else
            {
                for (int s = 64; s <= maxEdge; s *= 2) list.Add(s);
            }
            return list;
        }

        public static List<Vector2Int> CandidatePool(bool npot, int maxEdge)
        {
            var sides = CandidateSizes(npot, maxEdge);
            var pool = new List<Vector2Int>();
            foreach (var w in sides)
            foreach (var h in sides)
                pool.Add(new Vector2Int(w, h));
            pool.Sort((a, b) =>
            {
                long aa = (long)a.x * a.y, bb = (long)b.x * b.y;
                int c = aa.CompareTo(bb);
                if (c != 0) return c;
                float ra = Aspect(a), rb = Aspect(b);
                return ra.CompareTo(rb);
            });
            return pool;
        }

        private static float Aspect(Vector2Int s) => Mathf.Max(s.x, s.y) / (float)Mathf.Max(1, Mathf.Min(s.x, s.y));

        public static int PaddingFor(int maxEdge, int minPad)
        {
            int p = Mathf.CeilToInt(maxEdge / 128f);
            return Mathf.Max(minPad, Mathf.Max(4, p));
        }

        public static AtoPackedIsland Rasterize(AtoIsland island, Texture2D src, int texW, int texH, int padPx)
        {
            int w = Mathf.Max(1, island.PixelRect.width);
            int h = Mathf.Max(1, island.PixelRect.height);
            int gw = Mathf.Max(1, Mathf.CeilToInt((w + padPx * 2) / (float)Granule));
            int gh = Mathf.Max(1, Mathf.CeilToInt((h + padPx * 2) / (float)Granule));
            var mask = new ulong[Math.Max(1, (gw + 63) / 64 * gh)];
            // Island-shape raster at granule resolution (4px). Padding is a Minkowski expand of the silhouette.
            if (island.UvTris != null && island.UvTris.Length >= 3)
            {
                float u0 = island.Min.x, v0 = island.Min.y;
                float du = Mathf.Max(1e-8f, island.Max.x - island.Min.x);
                float dv = Mathf.Max(1e-8f, island.Max.y - island.Min.y);
                for (int t = 0; t + 2 < island.UvTris.Length; t += 3)
                {
                    Vector2 a = island.UvTris[t], b = island.UvTris[t + 1], c = island.UvTris[t + 2];
                    Vector2 pa = new Vector2((a.x - u0) / du * w + padPx, (a.y - v0) / dv * h + padPx);
                    Vector2 pb = new Vector2((b.x - u0) / du * w + padPx, (b.y - v0) / dv * h + padPx);
                    Vector2 pc = new Vector2((c.x - u0) / du * w + padPx, (c.y - v0) / dv * h + padPx);
                    FillTri(mask, gw, gh, pa / Granule, pb / Granule, pc / Granule);
                }
                Dilate(mask, gw, gh, Mathf.Max(1, padPx / Granule));
            }
            else
            {
                for (int y = 0; y < gh; y++)
                for (int x = 0; x < gw; x++)
                    Set(mask, gw, x, y);
            }
            return new AtoPackedIsland
            {
                Island = island, Source = src, W = w, H = h, GW = gw, GH = gh, Mask = mask
            };
        }

        private static void FillTri(ulong[] mask, int gw, int gh, Vector2 a, Vector2 b, Vector2 c)
        {
            int minx = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x))), 0, gw - 1);
            int maxx = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x))), 0, gw - 1);
            int miny = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y))), 0, gh - 1);
            int maxy = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y))), 0, gh - 1);
            for (int y = miny; y <= maxy; y++)
            for (int x = minx; x <= maxx; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                if (Inside(p, a, b, c)) Set(mask, gw, x, y);
            }
        }

        private static bool Inside(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float s = Sign(p, a, b) + Sign(p, b, c) + Sign(p, c, a);
            return Mathf.Abs(s) >= 2.5f || SameSide(p, a, b, c) && SameSide(p, b, c, a) && SameSide(p, c, a, b);
        }

        private static float Sign(Vector2 p, Vector2 a, Vector2 b) =>
            Mathf.Sign((p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y));

        private static bool SameSide(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            var ab = b - a;
            float z1 = ab.x * (p.y - a.y) - ab.y * (p.x - a.x);
            float z2 = ab.x * (c.y - a.y) - ab.y * (c.x - a.x);
            return z1 * z2 >= -1e-5f;
        }

        private static void Dilate(ulong[] mask, int gw, int gh, int r)
        {
            if (r <= 0) return;
            try
            {
                var src = new Unity.Collections.NativeArray<ulong>(mask, Unity.Collections.Allocator.TempJob);
                var dst = new Unity.Collections.NativeArray<ulong>(mask.Length, Unity.Collections.Allocator.TempJob);
                new AtoDilateJob { Gw = gw, Gh = gh, Radius = r, Src = src, Dst = dst }.Schedule().Complete();
                dst.CopyTo(mask);
                src.Dispose();
                dst.Dispose();
                return;
            }
            catch (System.Exception e)
            {
                AtoLog.VerboseInfo("Burst dilate fallback: " + e.Message);
            }
            var copy = (ulong[])mask.Clone();
            for (int y = 0; y < gh; y++)
            for (int x = 0; x < gw; x++)
            {
                if (!Get(copy, gw, x, y)) continue;
                for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if ((uint)nx < (uint)gw && (uint)ny < (uint)gh) Set(mask, gw, nx, ny);
                }
            }
        }

        public static AtoPackedIsland Transpose(AtoPackedIsland p)
        {
            var m = new ulong[(p.GH + 63) / 64 * p.GW];
            for (int y = 0; y < p.GH; y++)
            for (int x = 0; x < p.GW; x++)
                if (Get(p.Mask, p.GW, x, y)) Set(m, p.GH, y, x);
            return new AtoPackedIsland
            {
                Island = p.Island, Source = p.Source, W = p.H, H = p.W,
                GW = p.GH, GH = p.GW, Mask = m, Rotated = !p.Rotated
            };
        }

        public static bool TryPack(List<AtoPackedIsland> atoms, int atlasW, int atlasH, int padPx, List<AtoPackedIsland> dest)
        {
            dest.Clear();
            int gw = atlasW / Granule, gh = atlasH / Granule;
            if (gw <= 0 || gh <= 0) return false;
            var occ = new ulong[(gw + 63) / 64 * gh];

            // area desc + long side desc
            var order = new List<AtoPackedIsland>(atoms);
            order.Sort((a, b) =>
            {
                int c = (b.GW * b.GH).CompareTo(a.GW * a.GH);
                return c != 0 ? c : Mathf.Max(b.GW, b.GH).CompareTo(Mathf.Max(a.GW, a.GH));
            });

            foreach (var atom in order)
            {
                if (!BlfPlace(occ, gw, gh, atom, out var placed) &&
                    !BlfPlace(occ, gw, gh, Transpose(atom), out placed))
                    return false;
                dest.Add(placed);
            }
            return true;
        }

        private static bool BlfPlace(ulong[] occ, int gw, int gh, AtoPackedIsland atom, out AtoPackedIsland placed)
        {
            placed = null;
            for (int y = 0; y + atom.GH <= gh; y++)
            for (int x = 0; x + atom.GW <= gw; x++)
            {
                if (!Fits(occ, gw, x, y, atom)) continue;
                Stamp(occ, gw, x, y, atom);
                placed = new AtoPackedIsland
                {
                    Island = atom.Island, Source = atom.Source,
                    X = x * Granule, Y = y * Granule, W = atom.W, H = atom.H,
                    Rotated = atom.Rotated, GW = atom.GW, GH = atom.GH, Mask = atom.Mask
                };
                return true;
            }
            return false;
        }

        private static bool Fits(ulong[] occ, int gw, int x, int y, AtoPackedIsland a)
        {
            for (int j = 0; j < a.GH; j++)
            for (int i = 0; i < a.GW; i++)
            {
                if (!Get(a.Mask, a.GW, i, j)) continue;
                if (Get(occ, gw, x + i, y + j)) return false;
            }
            return true;
        }

        private static void Stamp(ulong[] occ, int gw, int x, int y, AtoPackedIsland a)
        {
            for (int j = 0; j < a.GH; j++)
            for (int i = 0; i < a.GW; i++)
                if (Get(a.Mask, a.GW, i, j)) Set(occ, gw, x + i, y + j);
        }

        private static void Set(ulong[] m, int w, int x, int y)
        {
            int rowWords = (w + 63) / 64;
            int idx = y * rowWords + (x >> 6);
            m[idx] |= 1UL << (x & 63);
        }
        private static bool Get(ulong[] m, int w, int x, int y)
        {
            int rowWords = (w + 63) / 64;
            int idx = y * rowWords + (x >> 6);
            return (m[idx] & (1UL << (x & 63))) != 0;
        }
    }
}
