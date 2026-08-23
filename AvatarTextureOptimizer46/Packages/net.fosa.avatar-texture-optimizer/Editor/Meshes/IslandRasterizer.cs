// SPDX-License-Identifier: MIT
// EN: Burst accelerated conservative triangle rasterization into a 4 texel granularity bit mask.
// ZH: 使用 Burst 加速的保守三角形光栅化，输出 4 像素粒度的位掩码。

using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Meshes
{
    /// <summary>
    /// EN: A UV triangle expressed in cell coordinates.
    /// ZH: 以单元坐标表示的 UV 三角形。
    /// </summary>
    public struct RasterTriangle
    {
        /// <summary>EN: First corner. ZH: 第一个顶点。</summary>
        public float2 A;
        /// <summary>EN: Second corner. ZH: 第二个顶点。</summary>
        public float2 B;
        /// <summary>EN: Third corner. ZH: 第三个顶点。</summary>
        public float2 C;
    }

    /// <summary>
    /// EN: Conservative rasterizer. Every cell whose square overlaps the triangle is marked, so no texel
    ///     that the GPU could ever sample is dropped.
    /// ZH: 保守光栅化器。凡是方格与三角形有重叠的单元都会被标记，
    ///     因此不会丢失任何 GPU 可能采样到的像素。
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Standard)]
    public struct ConservativeRasterJob : IJobParallelFor
    {
        /// <summary>EN: Triangles to rasterize, already in cell space. ZH: 待光栅化的三角形，已处于单元空间。</summary>
        [ReadOnly] public NativeArray<RasterTriangle> Triangles;
        /// <summary>EN: Grid width in cells. ZH: 网格宽度（单元数）。</summary>
        public int GridWidth;
        /// <summary>EN: Grid height in cells. ZH: 网格高度（单元数）。</summary>
        public int GridHeight;

        /// <summary>
        /// EN: Output coverage, one byte per cell. Parallel writes only ever store 1, so the benign race
        ///     between threads cannot produce a wrong value.
        /// ZH: 输出覆盖度，每单元一字节。并行写入只会写 1，
        ///     因此线程间的良性竞争不可能产生错误值。
        /// </summary>
        [NativeDisableParallelForRestriction] public NativeArray<byte> Coverage;

        /// <inheritdoc/>
        public void Execute(int index)
        {
            var t = Triangles[index];

            float minX = math.min(t.A.x, math.min(t.B.x, t.C.x));
            float maxX = math.max(t.A.x, math.max(t.B.x, t.C.x));
            float minY = math.min(t.A.y, math.min(t.B.y, t.C.y));
            float maxY = math.max(t.A.y, math.max(t.B.y, t.C.y));

            int x0 = math.clamp((int)math.floor(minX), 0, GridWidth - 1);
            int x1 = math.clamp((int)math.ceil(maxX), 0, GridWidth - 1);
            int y0 = math.clamp((int)math.floor(minY), 0, GridHeight - 1);
            int y1 = math.clamp((int)math.ceil(maxY), 0, GridHeight - 1);

            // EN: Degenerate triangles still occupy their texels, so mark the bounding box.
            // ZH: 退化三角形仍然占用其像素，因此直接标记其包围盒。
            float area = Cross(t.B - t.A, t.C - t.A);
            bool degenerate = math.abs(area) < 1e-9f;

            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    if (degenerate || BoxOverlapsTriangle(new float2(x, y), new float2(x + 1, y + 1), t))
                        Coverage[y * GridWidth + x] = 1;
                }
            }
        }

        private static float Cross(float2 a, float2 b) => a.x * b.y - a.y * b.x;

        /// <summary>
        /// EN: Separating axis test between an axis aligned square and a triangle.
        /// ZH: 轴对齐方格与三角形之间的分离轴测试。
        /// </summary>
        private static bool BoxOverlapsTriangle(float2 bmin, float2 bmax, RasterTriangle t)
        {
            // EN: Trivial reject on the box axes.
            // ZH: 先在方格轴向上做平凡剔除。
            if (math.max(t.A.x, math.max(t.B.x, t.C.x)) < bmin.x) return false;
            if (math.min(t.A.x, math.min(t.B.x, t.C.x)) > bmax.x) return false;
            if (math.max(t.A.y, math.max(t.B.y, t.C.y)) < bmin.y) return false;
            if (math.min(t.A.y, math.min(t.B.y, t.C.y)) > bmax.y) return false;

            // EN: Test the three triangle edge normals.
            // ZH: 再测试三条三角形边的法线。
            return EdgeTest(t.A, t.B, t.C, bmin, bmax)
                   && EdgeTest(t.B, t.C, t.A, bmin, bmax)
                   && EdgeTest(t.C, t.A, t.B, bmin, bmax);
        }

        private static bool EdgeTest(float2 p0, float2 p1, float2 other, float2 bmin, float2 bmax)
        {
            float2 n = new float2(-(p1.y - p0.y), p1.x - p0.x);
            float d0 = math.dot(n, other - p0);
            if (d0 > 0) n = -n;
            // EN: Project the box onto the edge normal and keep the far corner.
            // ZH: 将方格投影到边法线上并取最远角点。
            float2 far = new float2(n.x >= 0 ? bmin.x : bmax.x, n.y >= 0 ? bmin.y : bmax.y);
            return math.dot(n, far - p0) <= 0;
        }
    }
}
