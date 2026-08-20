// Avatar Texture Optimizer (ATO)
// Bilingual comments: English + Simplified Chinese. 双语注释：英文 + 简体中文。

using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace AvatarTextureOptimizer.Editor.Burst
{
    /// <summary>
    /// Burst-compiled jobs for the hot paths (resampling and statistics). Input pixels are
    /// packed as RGBA float planes with premultiplied alpha (so transparent downsampling is
    /// correct). These mirror the CPU reference implementation in ATOResampler; the CPU
    /// path remains the correctness baseline.
    ///
    /// Burst 编译的热路径 Job（重采样与统计）。输入像素打包为 RGBA float 平面（预乘
    /// alpha，透明下采样正确）。与 ATOResampler 的 CPU 参考实现一致；CPU 路径仍是正确性基准。
    /// </summary>
    public static class ATOBurstJobs
    {
        public static bool Available => Unity.Burst.BurstCompiler.IsEnabled;

        /// <summary>
        /// Area-average downsample (linear space, premultiplied alpha). One job per row.
        /// 面积平均下采样（线性空间，预乘 alpha）。每行一个 job。
        /// </summary>
        [BurstCompile]
        public struct AreaDownsampleJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> src;
            [WriteOnly] public NativeArray<float> dst;
            public int srcW, srcH, dstW, dstH;
            public float sx, sy;

            public void Execute(int y)
            {
                float y0 = y * sy;
                float y1 = math.min((y + 1) * sy, (float)srcH);

                for (int x = 0; x < dstW; x++)
                {
                    float x0 = x * sx;
                    float x1 = math.min((x + 1) * sx, (float)srcW);

                    float r = 0, g = 0, b = 0, a = 0, w = 0;
                    int iy0 = (int)math.floor(y0), iy1 = (int)math.ceil(y1);
                    int ix0 = (int)math.floor(x0), ix1 = (int)math.ceil(x1);

                    for (int iy = iy0; iy < iy1; iy++)
                    {
                        int cy = math.clamp(iy, 0, srcH - 1);
                        float wy = Overlap(y0, y1, iy, iy + 1);
                        for (int ix = ix0; ix < ix1; ix++)
                        {
                            int cx = math.clamp(ix, 0, srcW - 1);
                            float wx = Overlap(x0, x1, ix, ix + 1);
                            float weight = wx * wy;
                            int si = (cy * srcW + cx) * 4;
                            // premultiplied: multiply rgb by alpha. 预乘：rgb 乘 alpha。
                            r += src[si] * src[si + 3] * weight;
                            g += src[si + 1] * src[si + 3] * weight;
                            b += src[si + 2] * src[si + 3] * weight;
                            a += src[si + 3] * weight;
                            w += weight;
                        }
                    }

                    int di = (y * dstW + x) * 4;
                    if (w <= 1e-6f)
                    {
                        dst[di] = dst[di + 1] = dst[di + 2] = dst[di + 3] = 0f;
                        continue;
                    }
                    float invW = 1f / w;
                    r *= invW; g *= invW; b *= invW; a *= invW;
                    if (a > 1e-5f) { r /= a; g /= a; b /= a; } // un-premultiply. 反预乘。
                    dst[di] = r; dst[di + 1] = g; dst[di + 2] = b; dst[di + 3] = a;
                }
            }

            private static float Overlap(float a0, float a1, float b0, float b1)
            {
                return math.max(0f, math.min(a1, b1) - math.max(a0, b0));
            }
        }

        /// <summary>
        /// Bilinear upsample (linear space, premultiplied alpha). One job per row.
        /// 双线性上采样（线性空间，预乘 alpha）。每行一个 job。
        /// </summary>
        [BurstCompile]
        public struct BilinearUpsampleJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> src;
            [WriteOnly] public NativeArray<float> dst;
            public int srcW, srcH, dstW, dstH;

            public void Execute(int y)
            {
                float fy = (y + 0.5f) * srcH / dstH - 0.5f;
                int y0 = math.clamp((int)math.floor(fy), 0, srcH - 1);
                int y1 = math.clamp(y0 + 1, 0, srcH - 1);
                float ty = fy - math.floor(fy);

                for (int x = 0; x < dstW; x++)
                {
                    float fx = (x + 0.5f) * srcW / dstW - 0.5f;
                    int x0 = math.clamp((int)math.floor(fx), 0, srcW - 1);
                    int x1 = math.clamp(x0 + 1, 0, srcW - 1);
                    float tx = fx - math.floor(fx);

                    int i00 = (y0 * srcW + x0) * 4;
                    int i01 = (y0 * srcW + x1) * 4;
                    int i10 = (y1 * srcW + x0) * 4;
                    int i11 = (y1 * srcW + x1) * 4;

                    float4 c00 = new float4(src[i00], src[i00 + 1], src[i00 + 2], src[i00 + 3]);
                    float4 c01 = new float4(src[i01], src[i01 + 1], src[i01 + 2], src[i01 + 3]);
                    float4 c10 = new float4(src[i10], src[i10 + 1], src[i10 + 2], src[i10 + 3]);
                    float4 c11 = new float4(src[i11], src[i11 + 1], src[i11 + 2], src[i11 + 3]);

                    float4 top = PremulLerp(c00, c01, tx);
                    float4 bot = PremulLerp(c10, c11, tx);
                    float4 r = PremulLerp(top, bot, ty);

                    int di = (y * dstW + x) * 4;
                    dst[di] = r.x; dst[di + 1] = r.y; dst[di + 2] = r.z; dst[di + 3] = r.w;
                }
            }

            private static float4 PremulLerp(float4 a, float4 b, float t)
            {
                float4 pa = new float4(a.x * a.w, a.y * a.w, a.z * a.w, a.w);
                float4 pb = new float4(b.x * b.w, b.y * b.w, b.z * b.w, b.w);
                float4 r = math.lerp(pa, pb, t);
                if (r.w > 1e-5f) { r.x /= r.w; r.y /= r.w; r.z /= r.w; }
                return r;
            }
        }

        /// <summary>
        /// Compute per-pixel squared difference between two planes (for RMSE / ΔE preprocessing).
        /// 计算两平面的逐像素平方差（用于 RMSE / ΔE 预处理）。
        /// </summary>
        [BurstCompile]
        public struct SquaredDiffJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> a;
            [ReadOnly] public NativeArray<float> b;
            [WriteOnly] public NativeArray<float> outSqDiff;

            public void Execute(int i)
            {
                float d = a[i] - b[i];
                outSqDiff[i] = d * d;
            }
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Pack a Color[] into a premultiplied RGBA float plane. 将 Color[] 打包为预乘 RGBA float 平面。</summary>
        public static NativeArray<float> Pack(Color[] pixels)
        {
            var arr = new NativeArray<float>(pixels.Length * 4, Allocator.TempJob);
            for (int i = 0; i < pixels.Length; i++)
            {
                var c = pixels[i];
                arr[i * 4] = c.r * c.a;
                arr[i * 4 + 1] = c.g * c.a;
                arr[i * 4 + 2] = c.b * c.a;
                arr[i * 4 + 3] = c.a;
            }
            return arr;
        }

        /// <summary>Unpack a premultiplied RGBA float plane into Color[]. 将预乘 RGBA float 平面解包为 Color[]。</summary>
        public static Color[] Unpack(NativeArray<float> arr, int count)
        {
            var pixels = new Color[count];
            for (int i = 0; i < count; i++)
            {
                float r = arr[i * 4], g = arr[i * 4 + 1], b = arr[i * 4 + 2], a = arr[i * 4 + 3];
                if (a > 1e-5f) { r /= a; g /= a; b /= a; }
                pixels[i] = new Color(r, g, b, a);
            }
            return pixels;
        }
    }
}
