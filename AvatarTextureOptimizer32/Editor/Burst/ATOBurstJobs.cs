using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Fosa.ATO.Editor.Burst
{
    /// <summary>
    /// Burst 加速作业：三角形光栅化（位掩码）与 SSIM 计算。
    ///
    /// 关键设计：光栅化只"置位"（mask=1），多个三角形并行写同一 cell 时写入的都是相同的值 1，
    /// 因此天然并行安全（无读-改-写竞争）。
    ///
    /// Burst jobs: triangle rasterization (bitmask) & SSIM.
    /// Rasterization only SETS bits (mask=1); parallel writes of the same value are race-free.
    /// </summary>

    /// <summary>并行光栅化：每个三角形一个 job，只置位，无竞争。</summary>
    [BurstCompile]
    public struct RasterizeIslandsJob : IJobParallelFor
    {
        // 每个三角形的三个顶点（已换算为 cell 坐标），长度 = triCount * 3。
        [ReadOnly] public NativeArray<Vector2> triVerts;
        public int gw;
        public int gh;
        // 输出位掩码（0/1），并行只写 1。
        [NativeDisableParallelForRestriction] public NativeArray<byte> mask;

        public void Execute(int tri)
        {
            var a = triVerts[tri * 3 + 0];
            var b = triVerts[tri * 3 + 1];
            var c = triVerts[tri * 3 + 2];

            float minX = math.min(a.x, math.min(b.x, c.x));
            float maxX = math.max(a.x, math.max(b.x, c.x));
            float minY = math.min(a.y, math.min(b.y, c.y));
            float maxY = math.max(a.y, math.max(b.y, c.y));

            int x0 = math.max(0, (int)math.floor(minX));
            int x1 = math.min(gw - 1, (int)math.ceil(maxX));
            int y0 = math.max(0, (int)math.floor(minY));
            int y1 = math.min(gh - 1, (int)math.ceil(maxY));

            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    if (PointInTriangle(p, a, b, c))
                        mask[y * gw + x] = 1;
                }
        }

        private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Sign(p, a, b), d2 = Sign(p, b, c), d3 = Sign(p, c, a);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0;
            bool pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }

        private static float Sign(Vector2 p1, Vector2 p2, Vector2 p3) =>
            (p1.x - p3.x) * (p2.y - p3.y) - (p2.x - p3.x) * (p1.y - p3.y);
    }

    /// <summary>单尺度 SSIM（亮度），Burst 加速的纯计算。</summary>
    [BurstCompile]
    public struct SSIMJob : IJob
    {
        [ReadOnly] public NativeArray<float> a;
        [ReadOnly] public NativeArray<float> b;
        public double result;

        public void Execute()
        {
            int n = a.Length;
            double muA = 0, muB = 0;
            for (int i = 0; i < n; i++) { muA += a[i]; muB += b[i]; }
            muA /= n; muB /= n;

            double va = 0, vb = 0, cov = 0;
            for (int i = 0; i < n; i++)
            {
                double da = a[i] - muA, db = b[i] - muB;
                va += da * da; vb += db * db; cov += da * db;
            }
            va /= n; vb /= n; cov /= n;

            const double C1 = 6.5025;  // (0.01*255)^2
            const double C2 = 58.5225; // (0.03*255)^2
            result = ((2 * muA * muB + C1) * (2 * cov + C2)) /
                     ((muA * muA + muB * muB + C1) * (va + vb + C2));
        }
    }

    /// <summary>并行分块均值/方差（供 MS-SSIM 多尺度使用）。</summary>
    [BurstCompile]
    public struct MeanVarianceJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> a;
        [ReadOnly] public NativeArray<float> b;
        [ReadOnly] public NativeArray<int> blockStarts;   // 每个块的起始索引
        [ReadOnly] public NativeArray<int> blockLengths;  // 每个块的长度
        [NativeDisableParallelForRestriction] public NativeArray<double> sumA;
        [NativeDisableParallelForRestriction] public NativeArray<double> sumB;
        [NativeDisableParallelForRestriction] public NativeArray<double> sumA2;
        [NativeDisableParallelForRestriction] public NativeArray<double> sumB2;
        [NativeDisableParallelForRestriction] public NativeArray<double> sumAB;

        public void Execute(int block)
        {
            int start = blockStarts[block];
            int len = blockLengths[block];
            double sa = 0, sb = 0, sa2 = 0, sb2 = 0, sab = 0;
            for (int i = start; i < start + len; i++)
            {
                sa += a[i]; sb += b[i];
                sa2 += (double)a[i] * a[i]; sb2 += (double)b[i] * b[i];
                sab += (double)a[i] * b[i];
            }
            sumA[block] = sa; sumB[block] = sb;
            sumA2[block] = sa2; sumB2[block] = sb2; sumAB[block] = sab;
        }
    }

    /// <summary>
    /// Burst 光栅化入口（静态封装）：把岛三角形光栅化为位掩码。
    /// 供 ATOPacker 调用；失败时返回 null 以便回退 CPU 实现。
    /// </summary>
    public static class ATOBurst
    {
        public static bool[] RasterizeIslands(Vector2[] triVerts, int gw, int gh)
        {
            using var verts = new NativeArray<Vector2>(triVerts, Allocator.TempJob);
            using var mask = new NativeArray<byte>(gw * gh, Allocator.TempJob);

            var job = new RasterizeIslandsJob
            {
                triVerts = verts,
                gw = gw,
                gh = gh,
                mask = mask,
            };
            job.Schedule(triVerts.Length / 3, 64).Complete();

            var result = new bool[gw * gh];
            for (int i = 0; i < result.Length; i++) result[i] = mask[i] != 0;
            return result;
        }

        public static double SSIM(float[] a, float[] b)
        {
            using var na = new NativeArray<float>(a, Allocator.TempJob);
            using var nb = new NativeArray<float>(b, Allocator.TempJob);
            var job = new SSIMJob { a = na, b = nb, result = 0 };
            job.Run();
            return job.result;
        }
    }
}
