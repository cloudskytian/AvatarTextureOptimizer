using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace AvatarTextureOptimizer.Burst
{
    /// <summary>
    /// Rasterizes an island (a set of UV triangles) into a coarse bitmask grid
    /// (4px granularity) for atlas packing. / 将岛（一组 UV 三角形）光栅化为粗粒度位掩码网格
    /// （4px 粒度）用于图集装箱。
    /// UV coordinates are normalized to the island bounds ([0,1] local); the island's
    /// on-atlas pixel size (widthPx, heightPx) maps them into pixel space.
    /// UV 坐标已归一化到岛包围盒（局部 [0,1]）；岛的图集像素尺寸（widthPx/heightPx）将其映射到像素空间。
    /// </summary>
    [BurstCompile]
    public struct RasterizeIslandJob : IJob
    {
        /// <summary>Normalized island UVs (local 0..1). / 归一化岛 UV（局部 0..1）。</summary>
        [ReadOnly] public NativeArray<float2> uvs;
        /// <summary>Island pixel size on the atlas. / 岛在图集上的像素尺寸。</summary>
        public int widthPx;
        public int heightPx;
        /// <summary>Cell size in pixels (granularity). / 单元格像素粒度。</summary>
        public int cellSize;
        /// <summary>Grid dimensions in cells. / 网格尺寸（单元格）。</summary>
        public int gridW;
        public int gridH;
        /// <summary>Output mask (1 = filled). / 输出掩码（1 = 填充）。</summary>
        [WriteOnly] public NativeArray<byte> mask;

        public void Execute()
        {
            int triCount = uvs.Length / 3;
            for (int t = 0; t < triCount; t++)
            {
                float2 a = uvs[t * 3];
                float2 b = uvs[t * 3 + 1];
                float2 c = uvs[t * 3 + 2];

                // triangle bounds in pixels / 三角形像素包围盒
                float2 mn = math.min(math.min(a, b), c);
                float2 mx = math.max(math.max(a, b), c);
                int x0 = math.clamp((int)(mn.x * widthPx / cellSize), 0, gridW - 1);
                int y0 = math.clamp((int)(mn.y * heightPx / cellSize), 0, gridH - 1);
                int x1 = math.clamp((int)(mx.x * widthPx / cellSize), 0, gridW - 1);
                int y1 = math.clamp((int)(mx.y * heightPx / cellSize), 0, gridH - 1);

                for (int gy = y0; gy <= y1; gy++)
                for (int gx = x0; gx <= x1; gx++)
                {
                    // cell center in normalized UV / 单元格中心（归一化 UV）
                    float2 p = new float2((gx + 0.5f) * cellSize / widthPx, (gy + 0.5f) * cellSize / heightPx);
                    if (PointInTriangle(p, a, b, c))
                        mask[gy * gridW + gx] = 1;
                }
            }
        }

        private static bool PointInTriangle(float2 p, float2 a, float2 b, float2 c)
        {
            float d1 = Sign(p, a, b);
            float d2 = Sign(p, b, c);
            float d3 = Sign(p, c, a);
            bool neg = (d1 < 0) || (d2 < 0) || (d3 < 0);
            bool pos = (d1 > 0) || (d2 > 0) || (d3 > 0);
            return !(neg && pos);
        }

        private static float Sign(float2 p1, float2 p2, float2 p3) =>
            (p1.x - p3.x) * (p2.y - p3.y) - (p2.x - p3.x) * (p1.y - p3.y);
    }
}
