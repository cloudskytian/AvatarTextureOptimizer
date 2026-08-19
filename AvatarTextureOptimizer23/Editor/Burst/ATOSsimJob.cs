using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Burst SSIM window accumulator. Used as the CPU/Burst path when GPU blit is unavailable.
    /// Burst 版 SSIM 窗口累加。GPU Blit 不可用时走这条路径。
    /// </summary>
    [BurstCompile]
    internal struct ATOSsimWindowJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> LumaA;
        [ReadOnly] public NativeArray<float> LumaB;
        public int Width;
        public int Height;
        public int Win;
        [WriteOnly] public NativeArray<float> WindowSsim;

        public void Execute(int index)
        {
            var windowsX = math.max(1, (Width - Win) / Win + ((Width - Win) % Win == 0 ? 1 : 0));
            if (Width < Win || Height < Win)
            {
                WindowSsim[index] = 1f;
                return;
            }
            windowsX = (Width / Win);
            var wy = index / windowsX;
            var wx = index - wy * windowsX;
            var x = wx * Win;
            var y = wy * Win;
            if (x + Win > Width || y + Win > Height)
            {
                WindowSsim[index] = 1f;
                return;
            }

            const float C1 = 0.0001f;
            const float C2 = 0.0009f;
            var inv = 1f / (Win * Win);
            float ma = 0f, mb = 0f;
            for (int j = 0; j < Win; j++)
            for (int i = 0; i < Win; i++)
            {
                var p = (y + j) * Width + (x + i);
                ma += LumaA[p];
                mb += LumaB[p];
            }
            ma *= inv; mb *= inv;
            float va = 0f, vb = 0f, cab = 0f;
            for (int j = 0; j < Win; j++)
            for (int i = 0; i < Win; i++)
            {
                var p = (y + j) * Width + (x + i);
                var la = LumaA[p] - ma;
                var lb = LumaB[p] - mb;
                va += la * la;
                vb += lb * lb;
                cab += la * lb;
            }
            va *= inv; vb *= inv; cab *= inv;
            WindowSsim[index] = ((2f * ma * mb + C1) * (2f * cab + C2)) /
                                ((ma * ma + mb * mb + C1) * (va + vb + C2) + 1e-12f);
        }
    }

    internal static class ATOSsimBurst
    {
        public static float Evaluate(float[] a, float[] b, int w, int h, int win = 8)
        {
            if (w < win || h < win) return 1f;
            var wx = w / win;
            var wy = h / win;
            var n = wx * wy;
            if (n <= 0) return 1f;

            var na = new NativeArray<float>(a.Length, Allocator.TempJob);
            var nb = new NativeArray<float>(b.Length, Allocator.TempJob);
            var ns = new NativeArray<float>(n, Allocator.TempJob);
            for (int i = 0; i < a.Length; i++) { na[i] = a[i]; nb[i] = b[i]; }

            var job = new ATOSsimWindowJob
            {
                LumaA = na, LumaB = nb, Width = w, Height = h, Win = win, WindowSsim = ns
            };
            job.Schedule(n, 8).Complete();

            double sum = 0;
            for (int i = 0; i < n; i++) sum += ns[i];

            na.Dispose();
            nb.Dispose();
            ns.Dispose();
            return (float)(sum / n);
        }
    }
}
