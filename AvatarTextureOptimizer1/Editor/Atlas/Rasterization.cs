// Rasterization.cs / Rasterization.cs
// 4-pixel-granularity bitmask rasterization and placement testing for BLF packing.
// 4像素粒度位掩码光栅化和BLF装箱放置测试。

using System;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.Editor.Atlas
{
    public static class Rasterization
    {
        public const int GRAN = 4;
        public const int BITS = 64;

        /// <summary>Words per row for a given grid width.</summary>
        private static int WPR(int gridW) => (gridW + BITS - 1) / BITS;

        public static ulong[] RasterizeTriangles(Vector2[] triVerts, int pixelW, int pixelH, out int gridW, out int gridH)
        {
            gridW = (pixelW + GRAN - 1) / GRAN;
            gridH = (pixelH + GRAN - 1) / GRAN;
            int wpr = WPR(gridW);
            var mask = new ulong[gridH * wpr];
            int triCount = triVerts.Length / 3;
            for (int t = 0; t < triCount; t++)
            {
                Vector2 a = triVerts[t*3];
                Vector2 b = triVerts[t*3+1];
                Vector2 c = triVerts[t*3+2];
                float minX = Mathf.Min(a.x, Mathf.Min(b.x, c.x));
                float maxX = Mathf.Max(a.x, Mathf.Max(b.x, c.x));
                float minY = Mathf.Min(a.y, Mathf.Min(b.y, c.y));
                float maxY = Mathf.Max(a.y, Mathf.Max(b.y, c.y));
                int gx0 = Mathf.Clamp((int)Math.Floor(minX / GRAN), 0, gridW - 1);
                int gx1 = Mathf.Clamp((int)Math.Ceiling(maxX / GRAN), 0, gridW - 1);
                int gy0 = Mathf.Clamp((int)Math.Floor(minY / GRAN), 0, gridH - 1);
                int gy1 = Mathf.Clamp((int)Math.Ceiling(maxY / GRAN), 0, gridH - 1);

                for (int gy = gy0; gy <= gy1; gy++)
                    for (int gx = gx0; gx <= gx1; gx++)
                    {
                        float cx0 = gx*GRAN, cy0 = gy*GRAN, cx1 = cx0+GRAN, cy1 = cy0+GRAN;
                        if (TriRectOverlap(a, b, c, cx0, cy0, cx1, cy1))
                            SetBit(mask, gx, gy, wpr);
                    }
            }
            return mask;
        }

        public static ulong[] DilateMask(ulong[] mask, int gridW, int gridH, int padCells)
        {
            int wpr = WPR(gridW);
            var result = (ulong[])mask.Clone();
            for (int p = 0; p < padCells; p++)
            {
                var prev = (ulong[])result.Clone();
                for (int y = 0; y < gridH; y++)
                    for (int x = 0; x < gridW; x++)
                    {
                        if (GetBit(prev, x, y, wpr))
                        {
                            for (int dy = -1; dy <= 1; dy++)
                                for (int dx = -1; dx <= 1; dx++)
                                    SetBitSafe(result, x+dx, y+dy, gridW, gridH, wpr);
                        }
                    }
            }
            return result;
        }

        private static bool GetBit(ulong[] mask, int x, int y, int wpr)
        {
            if (x < 0 || y < 0) return false;
            int word = y * wpr + x / BITS;
            if (word < 0 || word >= mask.Length) return false;
            return (mask[word] & (1UL << (x % BITS))) != 0;
        }

        private static void SetBit(ulong[] mask, int x, int y, int wpr)
        {
            int word = y * wpr + x / BITS;
            if (word < 0 || word >= mask.Length) return;
            mask[word] |= 1UL << (x % BITS);
        }

        private static void SetBitSafe(ulong[] mask, int x, int y, int gridW, int gridH, int wpr)
        {
            if (x < 0 || y < 0 || x >= gridW || y >= gridH) return;
            SetBit(mask, x, y, wpr);
        }

        public static bool TryPlace(ulong[] atlas, int atlasGridW, int atlasGridH,
                                     ulong[] itemPadded, int itemGridW, int itemGridH,
                                     int gx, int gy)
        {
            int wprAtlas = WPR(atlasGridW);
            int wprItem = WPR(itemGridW);
            for (int y = 0; y < itemGridH; y++)
            {
                int ay = gy + y;
                if (ay < 0 || ay >= atlasGridH) return false;
                for (int x = 0; x < itemGridW; x++)
                {
                    int ax = gx + x;
                    if (ax < 0 || ax >= atlasGridW) return false;
                    int iWord = y * wprItem + x / BITS;
                    if (iWord >= itemPadded.Length) continue;
                    bool itemSet = (itemPadded[iWord] & (1UL << (x % BITS))) != 0;
                    if (!itemSet) continue;
                    int aWord = ay * wprAtlas + ax / BITS;
                    if (aWord >= atlas.Length) return false;
                    if ((atlas[aWord] & (1UL << (ax % BITS))) != 0) return false;
                }
            }
            for (int y = 0; y < itemGridH; y++)
            {
                int ay = gy + y;
                for (int x = 0; x < itemGridW; x++)
                {
                    int ax = gx + x;
                    int iWord = y * wprItem + x / BITS;
                    if (iWord >= itemPadded.Length) continue;
                    bool itemSet = (itemPadded[iWord] & (1UL << (x % BITS))) != 0;
                    if (!itemSet) continue;
                    SetBitSafe(atlas, ax, ay, atlasGridW, atlasGridH, wprAtlas);
                }
            }
            return true;
        }

        public static ulong[] Transpose(ulong[] mask, int gridW, int gridH, out int outW, out int outH)
        {
            int wprSrc = WPR(gridW);
            int newW = gridH; int newH = gridW;
            outW = newW; outH = newH;
            int wprDst = WPR(newW);
            var result = new ulong[newH * wprDst];
            for (int y = 0; y < gridH; y++)
                for (int x = 0; x < gridW; x++)
                {
                    if (GetBit(mask, x, y, wprSrc))
                    {
                        int nx = y;
                        int ny = newH - 1 - x;
                        SetBitSafe(result, nx, ny, newW, newH, wprDst);
                    }
                }
            return result;
        }

        public static int PopCount(ulong[] mask)
        {
            int c = 0;
            foreach (var w in mask)
            {
                ulong v = w;
                while (v != 0) { v &= v - 1; c++; }
            }
            return c;
        }

        private static bool TriRectOverlap(Vector2 a, Vector2 b, Vector2 c, float rx0, float ry0, float rx1, float ry1)
        {
            float minX = Mathf.Min(a.x, Mathf.Min(b.x, c.x));
            float maxX = Mathf.Max(a.x, Mathf.Max(b.x, c.x));
            float minY = Mathf.Min(a.y, Mathf.Min(b.y, c.y));
            float maxY = Mathf.Max(a.y, Mathf.Max(b.y, c.y));
            if (maxX < rx0 || minX > rx1 || maxY < ry0 || minY > ry1) return false;
            if (PointInTri(new Vector2((rx0+rx1)*0.5f, (ry0+ry1)*0.5f), a, b, c)) return true;
            if (PointInTri(new Vector2(rx0, ry0), a, b, c)) return true;
            if (PointInTri(new Vector2(rx1, ry0), a, b, c)) return true;
            if (PointInTri(new Vector2(rx0, ry1), a, b, c)) return true;
            if (PointInTri(new Vector2(rx1, ry1), a, b, c)) return true;
            return false;
        }

        private static float Sign2D(Vector2 p1, Vector2 p2, Vector2 p3)
            => (p1.x - p3.x) * (p2.y - p3.y) - (p2.x - p3.x) * (p1.y - p3.y);

        private static bool PointInTri(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            bool b1 = Sign2D(p, a, b) < 0;
            bool b2 = Sign2D(p, b, c) < 0;
            bool b3 = Sign2D(p, c, a) < 0;
            return b1 == b2 && b2 == b3;
        }
    }
}
