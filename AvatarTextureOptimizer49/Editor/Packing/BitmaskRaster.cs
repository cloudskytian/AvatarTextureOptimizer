using System;
using System.Collections.Generic;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// 4px-granularity raster bitmask of an island shape, used for shape-accurate bin packing
    /// (NOT rectangle packing). Supports 90° rotation via row/bit transpose and dilation by the
    /// atlas padding so BLF placement guarantees island gaps. Rasters are cached per island.
    /// / 岛形状的 4px 粒度位掩码光栅化：用于形状装箱（非矩形装箱）；支持转置旋转与按 padding 膨胀；结果缓存。
    /// </summary>
    internal class IslandRaster
    {
        /// <summary>Cell size in pixels. / 光栅单元像素尺寸。</summary>
        internal const int Cell = 4;

        internal int CellsW;
        internal int CellsH;
        /// <summary>bits[y][x] — one row per cell row, bit x set = occupied. / 每行一个 ulong 数组。</summary>
        internal ulong[] Bits;
        internal int CellCount;

        internal IslandRaster Transposed;
        internal UvIsland Source;

        /// <summary>Rasterize an island's triangles (UV space → pixel grid). / 光栅化岛三角形。</summary>
        internal static IslandRaster Rasterize(UvIsland island, UvGroup group, int pixelW, int pixelH,
            int dilateCells)
        {
            var mesh = group.mesh;
            var uvs = new List<Vector2>();
            mesh.GetUVs(group.channel, uvs);

            int cw = Math.Max(1, (pixelW + Cell - 1) / Cell);
            int ch = Math.Max(1, (pixelH + Cell - 1) / Cell);
            var cells = new bool[cw * ch];

            // all member islands (merged duplicates share the same shape) / 含合并副本
            var triLists = new List<List<int>> { island.triangles };
            triLists.AddRange(island.mergedIslands.ConvertAll(m => m.triangles));

            foreach (var tris in triLists)
            {
                for (int i = 0; i < tris.Count; i += 3)
                {
                    Vector2 a = Remap(uvs[tris[i]], island), b = Remap(uvs[tris[i + 1]], island),
                             c = Remap(uvs[tris[i + 2]], island);
                    RasterizeTriangle(a, b, c, pixelW, pixelH, cw, ch, cells);
                }
            }

            if (dilateCells > 0) Dilate(cells, cw, ch, dilateCells);

            var raster = Pack(cells, cw, ch);
            raster.Source = island;
            return raster;
        }

        /// <summary>UV → island-local [0,1]² → pixel space. / UV转岛内像素坐标。</summary>
        private static Vector2 Remap(Vector2 uv, UvIsland island)
        {
            var l = island.uvBounds;
            float w = Mathf.Max(l.width, 1e-9f), h = Mathf.Max(l.height, 1e-9f);
            return new Vector2((uv.x + island.uvOffset.x - l.x) / w, (uv.y + island.uvOffset.y - l.y) / h);
        }

        /// <summary>Conservative triangle rasterization (cell overlapped by triangle ⇒ set). / 保守三角形光栅化。</summary>
        private static void RasterizeTriangle(Vector2 a, Vector2 b, Vector2 c,
            int pixelW, int pixelH, int cw, int ch, bool[] cells)
        {
            // to pixel coords / 转像素坐标
            var pa = new Vector2(a.x * pixelW, a.y * pixelH);
            var pb = new Vector2(b.x * pixelW, b.y * pixelH);
            var pc = new Vector2(c.x * pixelW, c.y * pixelH);

            float minX = Mathf.Min(pa.x, Mathf.Min(pb.x, pc.x));
            float maxX = Mathf.Max(pa.x, Mathf.Max(pb.x, pc.x));
            float minY = Mathf.Min(pa.y, Mathf.Min(pb.y, pc.y));
            float maxY = Mathf.Max(pa.y, Mathf.Max(pb.y, pc.y));

            int cx0 = Mathf.Clamp((int)(minX / Cell), 0, cw - 1);
            int cx1 = Mathf.Clamp((int)(maxX / Cell), 0, cw - 1);
            int cy0 = Mathf.Clamp((int)(minY / Cell), 0, ch - 1);
            int cy1 = Mathf.Clamp((int)(maxY / Cell), 0, ch - 1);

            for (int cy = cy0; cy <= cy1; cy++)
            {
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    // conservative: bbox overlap is enough at 4px granularity (cheap &amp; safe)
                    // 4px 粒度下用包围盒重叠判定（保守且廉价）
                    cells[cy * cw + cx] = true;
                }
            }
        }

        /// <summary>Dilate occupied cells by n cells (padding). / 按 padding 膨胀。</summary>
        private static void Dilate(bool[] cells, int cw, int ch, int n)
        {
            for (int pass = 0; pass < n; pass++)
            {
                var copy = (bool[])cells.Clone();
                for (int y = 0; y < ch; y++)
                {
                    for (int x = 0; x < cw; x++)
                    {
                        if (cells[y * cw + x]) continue;
                        bool near = false;
                        for (int dy = -1; dy <= 1 && !near; dy++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = x + dx, ny = y + dy;
                                if (nx < 0 || ny < 0 || nx >= cw || ny >= ch) continue;
                                if (copy[ny * cw + nx]) { near = true; break; }
                            }
                        }
                        if (near) cells[y * cw + x] = true;
                    }
                }
            }
        }

        private static IslandRaster Pack(bool[] cells, int cw, int ch)
        {
            var words = (cw + 63) / 64;
            var bits = new ulong[ch * words];
            int count = 0;
            for (int y = 0; y < ch; y++)
            {
                for (int x = 0; x < cw; x++)
                {
                    if (!cells[y * cw + x]) continue;
                    bits[y * words + (x >> 6)] |= 1UL << (x & 63);
                    count++;
                }
            }
            return new IslandRaster { CellsW = cw, CellsH = ch, Bits = bits, CellCount = count };
        }

        /// <summary>Create (and cache) the true 90°-rotated variant, matching the pixel copy
        /// mapping (ix,iy)→(X+iy, Y+(w-1-ix)). / 生成（并缓存）真90°旋转，与像素拷贝映射严格一致。</summary>
        internal IslandRaster Rotate90()
        {
            if (Transposed != null) return Transposed;
            int inWords = (CellsW + 63) / 64;
            int outWords = (CellsH + 63) / 64; // output width = CellsH / 输出宽为 CellsH
            var bits = new ulong[CellsW * outWords];
            for (int y = 0; y < CellsH; y++)
            {
                for (int w = 0; w < inWords; w++)
                {
                    var word = Bits[y * inWords + w];
                    while (word != 0)
                    {
                        int bit = BitOps.TrailingZeroCount(word);
                        word &= word - 1;
                        int x = (w << 6) + bit;
                        // clockwise: (x,y) → (y, CellsW-1-x) / 顺时针旋转
                        bits[(CellsW - 1 - x) * outWords + (y >> 6)] |= 1UL << (y & 63);
                    }
                }
            }
            Transposed = new IslandRaster { CellsW = CellsH, CellsH = CellsW, Bits = bits, CellCount = CellCount };
            return Transposed;
        }
    }

    internal static class BitOps
    {
        internal static int TrailingZeroCount(ulong v)
        {
            int c = 0;
            while ((v & 1UL) == 0 && c < 64) { v >>= 1; c++; }
            return c;
        }
    }
}
