#if ATO_BURST_AVAILABLE
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

// Burst-accelerated island rasterization (4px block grid). Mirrors Pure.AtoRaster's algorithm exactly;
// used when Burst is available, otherwise the managed pure core handles it.
// Burst 加速的岛光栅化（4px 块网格）。与 Pure.AtoRaster 算法完全一致；Burst 可用时启用，否则由托管纯核心处理。

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    [BurstCompile]
    public struct RasterIslandJob : IJob
    {
        [ReadOnly] public NativeArray<float> UVs;      // 2 floats per vertex. 每顶点 2 个浮点。
        [ReadOnly] public NativeArray<int> Tris;       // triangle indices (absolute). 三角形索引（绝对）。
        public float MinU, MinV, MaxU, MaxV;
        public int PixelW, PixelH;
        public NativeArray<ulong> Bits;                // block grid, row-major words. 块网格，行优先字。

        public void Execute()
        {
            int wordsPerRow = (PixelW + 3) / 4; // block columns -> ulong words. 块列数 → ulong 字数。
            wordsPerRow = (wordsPerRow + 63) / 64;
            int blockW = math.max(1, (PixelW + 3) / 4);
            int blockH = math.max(1, (PixelH + 3) / 4);
            float spanU = MaxU - MinU, spanV = MaxV - MinV;
            if (spanU <= 0f || spanV <= 0f) return;
            float invU = 1f / spanU, invV = 1f / spanV;

            int triCount = Tris.Length / 3;
            for (int t = 0; t < triCount; t++)
            {
                int i0 = Tris[t * 3], i1 = Tris[t * 3 + 1], i2 = Tris[t * 3 + 2];
                float bx0 = (UVs[i0 * 2] - MinU) * invU * blockW, by0 = (UVs[i0 * 2 + 1] - MinV) * invV * blockH;
                float bx1 = (UVs[i1 * 2] - MinU) * invU * blockW, by1 = (UVs[i1 * 2 + 1] - MinV) * invV * blockH;
                float bx2 = (UVs[i2 * 2] - MinU) * invU * blockW, by2 = (UVs[i2 * 2 + 1] - MinV) * invV * blockH;

                int minBX = Clamp((int)math.floor(math.min(bx0, math.min(bx1, bx2))), blockW);
                int maxBX = Clamp((int)math.ceil(math.max(bx0, math.max(bx1, bx2))), blockW);
                int minBY = Clamp((int)math.floor(math.min(by0, math.min(by1, by2))), blockH);
                int maxBY = Clamp((int)math.ceil(math.max(by0, math.max(by1, by2))), blockH);

                for (int by = minBY; by < maxBY; by++)
                {
                    for (int bx = minBX; bx < maxBX; bx++)
                    {
                        if (Get(bx, by)) continue;
                        float cx = bx + 0.5f, cy = by + 0.5f;
                        if (PointInTriangle(cx, cy, bx0, by0, bx1, by1, bx2, by2)) Set(bx, by);
                    }
                }
            }
        }

        private static int Clamp(int v, int n) => v < 0 ? 0 : (v > n ? n : v);

        private static bool PointInTriangle(float px, float py, float ax, float ay, float bx, float by, float cx, float cy)
        {
            float d1 = Cross(px, py, ax, ay, bx, by);
            float d2 = Cross(px, py, bx, by, cx, cy);
            float d3 = Cross(px, py, cx, cy, ax, ay);
            bool hasNeg = (d1 < 0) || (d2 < 0) || (d3 < 0);
            bool hasPos = (d1 > 0) || (d2 > 0) || (d3 > 0);
            return !(hasNeg && hasPos);
        }
        private static float Cross(float px, float py, float ax, float ay, float bx, float by)
            => (bx - ax) * (py - ay) - (by - ay) * (px - ax);

        private int WordsPerRow => ((PixelW + 3) / 4 + 63) / 64;

        private bool Get(int bx, int by)
        {
            int word = by * WordsPerRow + (bx >> 6);
            return (Bits[word] & (1UL << (bx & 63))) != 0;
        }
        private void Set(int bx, int by)
        {
            int word = by * WordsPerRow + (bx >> 6);
            Bits[word] |= 1UL << (bx & 63);
        }
    }

    public static class BurstPacking
    {
        /// <summary>
        /// Rasterizes an island into a managed BitMask via Burst (or null when Burst is unavailable).
        /// 通过 Burst 将岛光栅化为托管 BitMask（Burst 不可用时返回 null）。
        /// </summary>
        public static Pure.BitMask Rasterize(UVIsland island, int pixelW, int pixelH)
        {
            var mask = new Pure.BitMask(Mathf.Max(1, (pixelW + 3) / 4), Mathf.Max(1, (pixelH + 3) / 4));
            using (var uvs = new NativeArray<float>(island.UVs, Allocator.TempJob))
            using (var tris = new NativeArray<int>(island.TriangleArrayIndices, Allocator.TempJob))
            using (var bits = new NativeArray<ulong>(mask.HeightBlocks * ((mask.WidthBlocks + 63) / 64), Allocator.TempJob))
            {
                var job = new RasterIslandJob
                {
                    UVs = uvs, Tris = tris,
                    MinU = island.BoundsMin.x, MinV = island.BoundsMin.y,
                    MaxU = island.BoundsMax.x, MaxV = island.BoundsMax.y,
                    PixelW = pixelW, PixelH = pixelH, Bits = bits,
                };
                job.Run();
                for (int y = 0; y < mask.HeightBlocks; y++)
                    for (int x = 0; x < mask.WidthBlocks; x++)
                        if (job.Get(x, y)) mask.Set(x, y, true);
            }
            return mask;
        }
    }
}
#endif
