// Avatar Texture Optimizer (ATO)
// Burst-accelerated hot loops (separable Gaussian blur, masked RMSE, luma) with a safe
// managed fallback. The managed path is the reference implementation; Burst is used only
// when scheduling succeeds.
// Burst 加速热循环（可分离高斯模糊、掩码 RMSE、亮度），带安全的托管兜底。
// 托管路径是参考实现；仅在 Burst 调度成功时才使用 Burst。

using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace NetFosa.ATO
{
    [BurstCompile]
    internal struct GaussRowJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> src;
        [WriteOnly] public NativeArray<float> dst;
        [ReadOnly] public NativeArray<float> kernel;
        public int w;
        public int radius;

        public void Execute(int y)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                float acc = 0f;
                for (int k = -radius; k <= radius; k++)
                {
                    int xx = x + k;
                    if (xx < 0) xx = 0; else if (xx >= w) xx = w - 1;
                    acc += src[row + xx] * kernel[k + radius];
                }
                dst[row + x] = acc;
            }
        }
    }

    [BurstCompile]
    internal struct GaussColJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> src;
        [WriteOnly] public NativeArray<float> dst;
        [ReadOnly] public NativeArray<float> kernel;
        public int w;
        public int h;
        public int radius;

        public void Execute(int x)
        {
            for (int y = 0; y < h; y++)
            {
                float acc = 0f;
                for (int k = -radius; k <= radius; k++)
                {
                    int yy = y + k;
                    if (yy < 0) yy = 0; else if (yy >= h) yy = h - 1;
                    acc += src[yy * w + x] * kernel[k + radius];
                }
                dst[y * w + x] = acc;
            }
        }
    }

    [BurstCompile]
    internal struct RmseJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float> a;
        [ReadOnly] public NativeArray<float> b;
        [ReadOnly] public NativeArray<byte> mask;
        [WriteOnly] public NativeArray<float> partial;

        public void Execute(int i)
        {
            if (mask.Length > 0 && mask[i] == 0) { partial[i] = 0f; return; }
            float d = a[i] - b[i];
            partial[i] = d * d;
        }
    }

    /// <summary>
    /// Burst dispatcher with managed fallback. / 带托管兜底的 Burst 调度器。
    /// </summary>
    public static class ATOBurst
    {
        public static bool TryGaussBlur(float[] src, int w, int h, float[] dst)
        {
            try
            {
                int r = ATOColorMath.GaussRadius;
                var kernel = new NativeArray<float>(ATOColorMath.Gauss11, Allocator.TempJob);
                var s = new NativeArray<float>(src, Allocator.TempJob);
                var tmp = new NativeArray<float>(w * h, Allocator.TempJob);
                var d = new NativeArray<float>(w * h, Allocator.TempJob);

                new GaussRowJob { src = s, dst = tmp, kernel = kernel, w = w, radius = r }.Schedule(h, 64).Complete();
                new GaussColJob { src = tmp, dst = d, kernel = kernel, w = w, h = h, radius = r }.Schedule(w, 64).Complete();

                d.CopyTo(dst);
                kernel.Dispose(); s.Dispose(); tmp.Dispose(); d.Dispose();
                return true;
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        public static bool TryRmse(float[] a, float[] b, byte[] mask, out float result)
        {
            result = 0f;
            try
            {
                int n = a.Length;
                var na = new NativeArray<float>(a, Allocator.TempJob);
                var nb = new NativeArray<float>(b, Allocator.TempJob);
                var nm = mask != null ? new NativeArray<byte>(mask, Allocator.TempJob) : new NativeArray<byte>(0, Allocator.TempJob);
                var partial = new NativeArray<float>(n, Allocator.TempJob);

                new RmseJob { a = na, b = nb, mask = nm, partial = partial }.Schedule(n, 1024).Complete();

                float sum = 0f; int count = 0;
                for (int i = 0; i < n; i++)
                {
                    if (mask != null && mask[i] == 0) continue;
                    sum += partial[i]; count++;
                }
                result = count > 0 ? Unity.Mathematics.math.sqrt(sum / count) : 0f;

                na.Dispose(); nb.Dispose(); nm.Dispose(); partial.Dispose();
                return true;
            }
            catch (System.Exception)
            {
                return false;
            }
        }
    }
}
