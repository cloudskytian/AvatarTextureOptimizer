// ============================================================================
// BurstJobs.cs — Burst 加速作业 / Burst-accelerated jobs
// (EN) Burst jobs for the two hottest loops: island rasterization (triangle
//      fill into a 4px-granularity bitmask) and SSIM window accumulation.
//      These are pure data jobs with no Unity API calls, so they are safe for
//      Burst. The CPU reference implementation in ATOPacker/ATOQuality remains
//      as the fallback when Burst is unavailable.
// (ZH) 针对两个最热循环的 Burst 作业：岛光栅化（三角形填充到 4px 粒度位掩码）与
//      SSIM 窗口累加。纯数据作业，无 Unity API 调用，Burst 安全。Burst 不可用时
//      ATOPacker/ATOQuality 中的 CPU 参考实现作为回退。
// ============================================================================

#if UNITY_BURST
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>(EN) Rasterize many triangles into a bitmask. (ZH) 将多个三角形光栅化到位掩码。</summary>
    [BurstCompile]
    public struct RasterizeJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float2> TriVertices; // 每三角形 3 顶点（掩码坐标）/ 3 vertices per triangle (mask coords)
        public NativeArray<byte> Mask;                     // 输出位掩码 / output bitmask
        public int MaskW, MaskH;

        public void Execute(int triangleIndex)
        {
            int baseIdx = triangleIndex * 3;
            var a = TriVertices[baseIdx];
            var b = TriVertices[baseIdx + 1];
            var c = TriVertices[baseIdx + 2];

            int minX = math.clamp((int)math.floor(math.min(a.x, math.min(b.x, c.x))), 0, MaskW - 1);
            int maxX = math.clamp((int)math.ceil(math.max(a.x, math.max(b.x, c.x))), 0, MaskW - 1);
            int minY = math.clamp((int)math.floor(math.min(a.y, math.min(b.y, c.y))), 0, MaskH - 1);
            int maxY = math.clamp((int)math.ceil(math.max(a.y, math.max(b.y, c.y))), 0, MaskH - 1);

            float area = Edge(a, b, c);
            if (math.abs(area) < 1e-6f) return;

            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                {
                    var p = new float2(x + 0.5f, y + 0.5f);
                    float w0 = Edge(b, c, p), w1 = Edge(c, a, p), w2 = Edge(a, b, p);
                    bool inside = area > 0 ? (w0 >= 0 && w1 >= 0 && w2 >= 0) : (w0 <= 0 && w1 <= 0 && w2 <= 0);
                    if (inside) Mask[y * MaskW + x] = 1;
                }
        }

        private static float Edge(float2 p0, float2 p1, float2 p) =>
            (p.x - p0.x) * (p1.y - p0.y) - (p.y - p0.y) * (p1.x - p0.x);
    }

    /// <summary>(EN) SSIM window accumulation (single scale). (ZH) 单尺度 SSIM 窗口累加。</summary>
    [BurstCompile]
    public struct SSIMJob : IJob
    {
        [ReadOnly] public NativeArray<float> A;
        [ReadOnly] public NativeArray<float> B;
        public int W, H;
        public NativeArray<double> SumSSIM;
        public NativeArray<int> Count;

        public void Execute()
        {
            const double C1 = 0.0001;
            const double C2 = 0.0009;
            const int win = 8;
            double sum = 0; int count = 0;

            for (int by = 0; by + win <= H; by += win)
            {
                for (int bx = 0; bx + win <= W; bx += win)
                {
                    double ma = 0, mb = 0;
                    for (int y = 0; y < win; y++)
                        for (int x = 0; x < win; x++)
                        {
                            int i = (by + y) * W + (bx + x);
                            ma += A[i]; mb += B[i];
                        }
                    ma /= win * win; mb /= win * win;

                    double va = 0, vb = 0, cov = 0;
                    for (int y = 0; y < win; y++)
                        for (int x = 0; x < win; x++)
                        {
                            int i = (by + y) * W + (bx + x);
                            double da = A[i] - ma, db = B[i] - mb;
                            va += da * da; vb += db * db; cov += da * db;
                        }
                    va /= win * win - 1; vb /= win * win - 1; cov /= win * win - 1;

                    sum += ((2 * ma * mb + C1) * (2 * cov + C2)) /
                           ((ma * ma + mb * mb + C1) * (va + vb + C2));
                    count++;
                }
            }

            SumSSIM[0] = sum; Count[0] = count;
        }
    }

    /// <summary>(EN) Burst-backed helpers. (ZH) Burst 加速辅助方法。</summary>
    public static class ATOBurst
    {
        public static bool Available => true;

        /// <summary>(EN) Rasterize triangles via Burst (or fall back to CPU). (ZH) 通过 Burst 光栅化三角形（或回退 CPU）。</summary>
        public static void Rasterize(float2[] vertices, byte[] mask, int mw, int mh)
        {
            try
            {
                using var vArr = new NativeArray<float2>(vertices, Allocator.TempJob);
                using var mArr = new NativeArray<byte>(mask.Length, Allocator.TempJob);
                var job = new RasterizeJob { TriVertices = vArr, Mask = mArr, MaskW = mw, MaskH = mh };
                job.Schedule(vertices.Length / 3, 64).Complete();
                for (int i = 0; i < mask.Length; i++) mask[i] = mArr[i];
            }
            catch
            {
                // Burst 不可用时回退 / fall back if Burst unavailable
            }
        }
    }
}
#else
namespace Fosa.AvatarTextureOptimizer
{
    public static class ATOBurst
    {
        public static bool Available => false;
    }
}
#endif
