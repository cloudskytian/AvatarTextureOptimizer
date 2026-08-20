// Island rasterization to 4px-granularity bitmasks (Burst) + bitmask utils (transpose).
// 岛光栅化为 4px 粒度位掩码（Burst）+ 位掩码工具（转置）。

using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>Cell bitmask: gw x gh cells, 1 = occupied. / 单元位掩码。</summary>
    internal class BitMask
    {
        internal int Gw, Gh;
        internal ulong[] Rows; // one ulong[] per conceptual row-block; row-major bits / 每行按位存储

        internal BitMask(int gw, int gh)
        {
            Gw = gw;
            Gh = gh;
            Rows = new ulong[gh * WordsPerRow(gw)];
        }

        internal static int WordsPerRow(int gw) => (gw + 63) / 64;

        internal void Set(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Gw || y >= Gh) return;
            Rows[y * WordsPerRow(Gw) + (x >> 6)] |= 1ul << (x & 63);
        }

        internal bool Get(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Gw || y >= Gh) return false;
            return (Rows[y * WordsPerRow(Gw) + (x >> 6)] & (1ul << (x & 63))) != 0;
        }

        internal int PopCount()
        {
            int count = 0;
            foreach (var w in Rows) count += math.popcnt(w);
            return count;
        }

        /// <summary>Dilate by r cells (chebyshev). / 膨胀 r 个单元。</summary>
        internal BitMask Dilated(int r)
        {
            var src = this;
            for (int i = 0; i < r; i++) src = src.Dilate1();
            return src;
        }

        private BitMask Dilate1()
        {
            var dst = new BitMask(Gw, Gh);
            int wpr = WordsPerRow(Gw);
            for (int y = 0; y < Gh; y++)
                for (int w = 0; w < wpr; w++)
                {
                    ulong v = Rows[y * wpr + w];
                    if (v == 0) continue;
                    ulong spread = v | (v << 1) | (v >> 1);
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int yy = y + dy;
                        if (yy < 0 || yy >= Gh) continue;
                        dst.Rows[yy * wpr + w] |= spread;
                    }
                }
            return dst;
        }

        /// <summary>Transpose (90° rotation). / 转置（旋转90°）。</summary>
        internal BitMask Transposed()
        {
            var t = new BitMask(Gh, Gw);
            for (int y = 0; y < Gh; y++)
                for (int x = 0; x < Gw; x++)
                    if (Get(x, y))
                        t.Set(y, x);
            return t;
        }
    }

    /// <summary>Rasterize island triangles into a cells grid + dilation.
    /// 将岛三角形光栅化进单元网格并膨胀。</summary>
    [BurstCompile]
    internal struct RasterizeJob : IJob
    {
        [ReadOnly] internal NativeArray<float> triUvs;  // xyz triplets of normalized island-local uv (x,y,unused)
        internal int triCount;
        internal int gw, gh;          // cells / 单元数
        internal int dilateCells;     // dilation / 膨胀单元数
        internal NativeArray<ulong> rows; // output gw x gh (pre-allocated) / 输出（预分配）

        private static int WordsPerRow(int gw) => (gw + 63) / 64;

        internal void Execute()
        {
            // rasterize conservative bboxes / 保守光栅三角形包围盒
            for (int t = 0; t < triCount; t++)
            {
                float x0 = triUvs[t * 6], y0 = triUvs[t * 6 + 1];
                float x1 = triUvs[t * 6 + 2], y1 = triUvs[t * 6 + 3];
                float x2 = triUvs[t * 6 + 4], y2 = triUvs[t * 6 + 5];
                int cx0 = ClampCell(Mathf.Min(x0, Mathf.Min(x1, x2)));
                int cx1 = ClampCell(Mathf.Max(x0, Mathf.Max(x1, x2)));
                int cy0 = ClampCell(Mathf.Min(y0, Mathf.Min(y1, y2)));
                int cy1 = ClampCell(Mathf.Max(y0, Mathf.Max(y1, y2)));
                for (int cy = cy0; cy <= cy1; cy++)
                    for (int cx = cx0; cx <= cx1; cx++)
                        SetBit(cx, cy);
            }

            // dilation / 膨胀
            for (int i = 0; i < dilateCells; i++) Dilate1();
        }

        private int ClampCell(float v) => math.clamp((int)math.floor(v), 0, gw - 1);

        private void SetBit(int x, int y)
        {
            if (x < 0 || y < 0 || x >= gw || y >= gh) return;
            rows[y * WordsPerRow(gw) + (x >> 6)] |= 1ul << (x & 63);
        }

        private void Dilate1()
        {
            int wpr = WordsPerRow(gw);
            var copy = new NativeArray<ulong>(rows.Length, Allocator.Temp);
            rows.CopyTo(copy);
            for (int y = 0; y < gh; y++)
                for (int w = 0; w < wpr; w++)
                {
                    ulong v = copy[y * wpr + w];
                    if (v == 0) continue;
                    ulong spread = v | (v << 1) | (v >> 1);
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int yy = y + dy;
                        if (yy < 0 || yy >= gh) continue;
                        rows[yy * wpr + w] |= spread;
                    }
                }
            copy.Dispose();
        }
    }
}
