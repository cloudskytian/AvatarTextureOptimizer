// IslandRasterizer.cs - Burst raster of island triangles to a 4px-granularity occupancy bitmask.
// Burst 将岛三角形光栅化为 4px 粒度占用位掩码。
// Row layout: ceil(cols/64) ulongs per row so islands up to atlas size are supported.
// 行布局：每行 ceil(cols/64) 个 ulong，支持最大图集尺寸的岛。
using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Fosa.ATO.Editor.Analysis;

namespace Fosa.ATO.Editor.Atlas
{
    public static class IslandRasterizer
    {
        public const int Grain = 4; // 4px granularity / 4px粒度

        /// <summary>Rasterized occupancy mask. / 占用掩码。</summary>
        public sealed class Mask
        {
            public int Cols, Rows;
            public int Words;             // ulongs per row / 每行ulong数
            public ulong[] Bits;          // row-major / 行主序
            public int CellCount;

            public bool Get(int c, int r)
            {
                int w = c >> 6, b = c & 63;
                return (Bits[r * Words + w] & (1ul << b)) != 0;
            }

            public void Set(int c, int r)
            {
                int w = c >> 6, b = c & 63;
                Bits[r * Words + w] |= 1ul << b;
            }

            public Mask Transposed()
            {
                var t = new Mask { Cols = Rows, Rows = Cols, Words = (Rows + 63) >> 6, Bits = new ulong[((Rows + 63) >> 6) * Cols], CellCount = CellCount };
                for (int r = 0; r < Rows; r++)
                    for (int c = 0; c < Cols; c++)
                        if (Get(c, r)) t.Set(r, c);
                return t;
            }
        }

        /// <summary>Rasterize island triangles (UV space) into a mask at pixel size w x h. / 将岛三角形（UV空间）光栅化为 w x h 像素掩码。</summary>
        public static Mask Raster(Island isl, Mesh mesh, int uvChannel, int w, int h)
        {
            int cols = Math.Max(1, (w + Grain - 1) / Grain);
            int rows = Math.Max(1, (h + Grain - 1) / Grain);
            int words = (cols + 63) >> 6;
            var uvList = new System.Collections.Generic.List<Vector2>(mesh.vertexCount);
            mesh.GetUVs(uvChannel, uvList);

            var tris = new NativeArray<int>(isl.triangles, Allocator.TempJob);
            var uvs = new NativeArray<float2>(uvList.Count, Allocator.TempJob);
            float sx = 1f / Mathf.Max(1e-9f, isl.uvMax.x - isl.uvMin.x);
            float sy = 1f / Mathf.Max(1e-9f, isl.uvMax.y - isl.uvMin.y);
            for (int i = 0; i < uvList.Count; i++)
            {
                var uv = uvList[i] + isl.uvShift; // normalization shift / 归一平移
                uvs[i] = new float2((uv.x - isl.uvMin.x) * sx, (uv.y - isl.uvMin.y) * sy);
            }
            var bits = new NativeArray<ulong>(words * rows, Allocator.TempJob);
            var job = new RasterJob { Tris = tris, Uvs = uvs, Bits = bits, Cols = cols, Rows = rows, Words = words, W = w, H = h };
            job.Schedule().Complete();
            var m = new Mask { Cols = cols, Rows = rows, Words = words, Bits = bits.ToArray(), CellCount = 0 };
            int cnt = 0;
            foreach (var b in m.Bits) cnt += math.countbits(b);
            m.CellCount = cnt;
            tris.Dispose(); uvs.Dispose(); bits.Dispose();
            return m;
        }

        /// <summary>Rect cell area for comparisons. / 纯矩形面积（用于比较）。</summary>
        public static int RectCells(int w, int h) => Math.Max(1, (w + Grain - 1) / Grain) * Math.Max(1, (h + Grain - 1) / Grain);

        /// <summary>Rasterize triangles into row bitsets with 4px cells. / 光栅化到行位图（4px单元）。</summary>
        [BurstCompile]
        private struct RasterJob : IJob
        {
            [ReadOnly] public NativeArray<int> Tris;
            [ReadOnly] public NativeArray<float2> Uvs;
            public NativeArray<ulong> Bits;
            public int Cols, Rows, Words, W, H;

            public void Execute()
            {
                for (int t = 0; t < Tris.Length; t += 3)
                {
                    float2 a = Uvs[Tris[t]], b = Uvs[Tris[t + 1]], c = Uvs[Tris[t + 2]];
                    float2 mn = math.min(a, math.min(b, c)), mx = math.max(a, math.max(b, c));
                    int x0 = math.max(0, (int)math.floor(mn.x * W / Grain) - 1), x1 = math.min(Cols - 1, (int)math.ceil(mx.x * W / Grain));
                    int y0 = math.max(0, (int)math.floor(mn.y * H / Grain) - 1), y1 = math.min(Rows - 1, (int)math.ceil(mx.y * H / Grain));
                    for (int y = y0; y <= y1; y++)
                        for (int x = x0; x <= x1; x++)
                        {
                            float2 p = new float2((x + 0.5f) * Grain / W, (y + 0.5f) * Grain / H);
                            if (PointInTri(p, a, b, c))
                            {
                                int w = x >> 6, bit = x & 63;
                                Bits[y * Words + w] |= 1ul << bit;
                            }
                        }
                }
            }

            private static bool PointInTri(float2 p, float2 a, float2 b, float2 c)
            {
                float d1 = Cross(p - a, b - a), d2 = Cross(p - b, c - b), d3 = Cross(p - c, a - c);
                bool neg = d1 < 0 || d2 < 0 || d3 < 0;
                bool pos = d1 > 0 || d2 > 0 || d3 > 0;
                return !(neg && pos);
            }

            private static float Cross(float2 a, float2 b) => a.x * b.y - a.y * b.x;
        }
    }
}
