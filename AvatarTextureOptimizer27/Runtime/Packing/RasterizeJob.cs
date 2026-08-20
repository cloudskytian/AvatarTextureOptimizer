using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Net.Fosa.AvatarTextureOptimizer
{
    /// <summary>Burst raster of triangle coverage at 4px cells. / 4px 粒度 Burst 三角形光栅。</summary>
    [BurstCompile]
    public struct RasterizeJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> A;
        [ReadOnly] public NativeArray<float2> B;
        [ReadOnly] public NativeArray<float2> C;
        public int WidthCells;
        public int HeightCells;
        public int Granularity;
        [NativeDisableParallelForRestriction] public NativeArray<ulong> Bits;
        public int StrideWords;

        public void Execute(int index)
        {
            var a = A[index] / Granularity;
            var b = B[index] / Granularity;
            var c = C[index] / Granularity;
            int minX = (int)math.floor(math.min(a.x, math.min(b.x, c.x)));
            int maxX = (int)math.ceil(math.max(a.x, math.max(b.x, c.x)));
            int minY = (int)math.floor(math.min(a.y, math.min(b.y, c.y)));
            int maxY = (int)math.ceil(math.max(a.y, math.max(b.y, c.y)));
            minX = math.clamp(minX, 0, WidthCells - 1);
            maxX = math.clamp(maxX, 0, WidthCells - 1);
            minY = math.clamp(minY, 0, HeightCells - 1);
            maxY = math.clamp(maxY, 0, HeightCells - 1);
            for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                var p = new float2(x + 0.5f, y + 0.5f);
                if (!Inside(p, a, b, c)) continue;
                int word = x / 64;
                ulong bit = 1UL << (x & 63);
                // atomic-less: caller should not overlap triangles of different jobs on same cell heavily
                Bits[y * StrideWords + word] |= bit;
            }
        }

        static bool Inside(float2 p, float2 a, float2 b, float2 c)
        {
            float s = Sign(p, a, b);
            float t = Sign(p, b, c);
            float u = Sign(p, c, a);
            bool hasNeg = s < 0 || t < 0 || u < 0;
            bool hasPos = s > 0 || t > 0 || u > 0;
            return !(hasNeg && hasPos);
        }

        static float Sign(float2 p1, float2 p2, float2 p3) =>
            (p1.x - p3.x) * (p2.y - p3.y) - (p2.x - p3.x) * (p1.y - p3.y);
    }
}
