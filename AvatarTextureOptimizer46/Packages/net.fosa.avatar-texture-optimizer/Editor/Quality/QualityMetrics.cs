// SPDX-License-Identifier: MIT
// EN: The target quality algorithm's metric implementations.
// ZH: 目标质量算法的各项度量实现。

using System;
using Net.Fosa.AvatarTextureOptimizer.Editor.Textures;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Quality
{
    /// <summary>
    /// EN: How the two channels of a tangent space normal map are stored.
    /// ZH: 切线空间法线贴图的两个通道的存储方式。
    /// </summary>
    public enum NormalEncoding
    {
        /// <summary>EN: X in R, Y in G, Z in B. ZH: X 在 R，Y 在 G，Z 在 B。</summary>
        Rgb,
        /// <summary>EN: X in A, Y in G (DXT5nm). ZH: X 在 A，Y 在 G（DXT5nm）。</summary>
        AlphaGreen,
        /// <summary>EN: X in R, Y in G, Z reconstructed (BC5). ZH: X 在 R，Y 在 G，Z 重建（BC5）。</summary>
        RedGreen,
    }

    /// <summary>
    /// EN: One evaluation of a candidate downscale against the original.
    /// ZH: 一次候选缩放相对原图的评估结果。
    /// </summary>
    public struct QualityScores
    {
        /// <summary>EN: MS-SSIM, or single scale SSIM for small islands, or 1 when skipped. ZH: MS-SSIM；小岛为单尺度 SSIM；跳过时为 1。</summary>
        public float MsSsim;
        /// <summary>EN: 95th percentile CIEDE2000. ZH: CIEDE2000 的 95 分位数。</summary>
        public float DeltaE95;
        /// <summary>EN: Silhouette IoU after clipping, for Cutout materials. ZH: Cutout 材质裁剪后的轮廓 IoU。</summary>
        public float AlphaIoU;
        /// <summary>EN: Linear RMSE of the alpha channel, for Blend materials. ZH: Blend 材质 alpha 通道的线性 RMSE。</summary>
        public float AlphaRmse;
        /// <summary>EN: 95th percentile angular deviation of normals, in degrees. ZH: 法线角度偏差的 95 分位数（度）。</summary>
        public float NormalAngleP95;
        /// <summary>EN: Worst per-channel linear RMSE for grayscale textures. ZH: 灰度贴图逐通道 RMSE 中的最差值。</summary>
        public float GrayscaleRmse;
    }

    /// <summary>
    /// EN: Static metric evaluation. All maths happens on linear RGBA float data.
    /// ZH: 静态度量计算。所有运算都在线性 RGBA 浮点数据上进行。
    /// </summary>
    public static class QualityMetrics
    {
        /// <summary>EN: Below this bounding box short side, MS-SSIM degrades to single scale SSIM. ZH: 包围盒短边低于该值时，MS-SSIM 降级为单尺度 SSIM。</summary>
        public const int MsSsimMinShortSide = 176;
        /// <summary>EN: Below this bounding box short side, structural similarity is ignored entirely. ZH: 包围盒短边低于该值时，完全忽略结构相似度。</summary>
        public const int SsimMinShortSide = 11;

        // EN: Standard MS-SSIM scale weights from Wang, Simoncelli and Bovik (2003).
        // ZH: 来自 Wang/Simoncelli/Bovik（2003）的标准 MS-SSIM 各尺度权重。
        private static readonly float[] MsSsimWeights = { 0.0448f, 0.2856f, 0.3001f, 0.2363f, 0.1333f };

        /// <summary>
        /// EN: Computes every metric that applies to the given texture kind.
        /// ZH: 计算适用于给定贴图分类的全部度量。
        /// </summary>
        /// <param name="original">EN: Original island crop. ZH: 原始岛裁剪。</param>
        /// <param name="candidate">EN: Downscaled then bilinearly upsampled back to the original size. ZH: 缩小后再双线性上采样回原尺寸的结果。</param>
        /// <param name="kind">EN: Texture kind. ZH: 贴图分类。</param>
        /// <param name="alphaMode">EN: Strictest alpha mode among all referencing materials. ZH: 所有引用材质中最严格的 alpha 模式。</param>
        /// <param name="cutoff">EN: Strictest cutoff among all referencing materials. ZH: 所有引用材质中最严格的裁剪阈值。</param>
        /// <param name="usedChannels">EN: RGBA mask of channels the shader consumes. ZH: 着色器使用的 RGBA 通道掩码。</param>
        /// <param name="normalEncoding">EN: Normal map encoding when applicable. ZH: 适用时的法线编码方式。</param>
        public static QualityScores Evaluate(LinearImage original, LinearImage candidate,
            AtoTextureKind kind, AtoAlphaMode alphaMode, float cutoff, int usedChannels, NormalEncoding normalEncoding)
        {
            if (original.Width != candidate.Width || original.Height != candidate.Height)
                throw new ArgumentException("[ATO] metric inputs must have the same size");

            var scores = new QualityScores
            {
                MsSsim = 1f,
                DeltaE95 = 0f,
                AlphaIoU = 1f,
                AlphaRmse = 0f,
                NormalAngleP95 = 0f,
                GrayscaleRmse = 0f,
            };

            int shortSide = Mathf.Min(original.Width, original.Height);

            switch (kind)
            {
                case AtoTextureKind.Normal:
                    scores.NormalAngleP95 = NormalAnglePercentile(original, candidate, normalEncoding, 0.95f);
                    break;

                case AtoTextureKind.Grayscale:
                    scores.GrayscaleRmse = WorstChannelRmse(original, candidate, usedChannels);
                    break;

                default:
                    if (shortSide >= SsimMinShortSide)
                        scores.MsSsim = shortSide >= MsSsimMinShortSide
                            ? MultiScaleSsim(original, candidate)
                            : Ssim(original, candidate);
                    scores.DeltaE95 = DeltaEPercentile(original, candidate, 0.95f);
                    if (alphaMode == AtoAlphaMode.Cutout)
                        scores.AlphaIoU = SilhouetteIoU(original, candidate, cutoff);
                    else if (alphaMode == AtoAlphaMode.Blend)
                        scores.AlphaRmse = ChannelRmse(original, candidate, 3);
                    break;
            }

            return scores;
        }

        /// <summary>
        /// EN: Checks a score set against the configured thresholds. Every applicable metric must pass.
        /// ZH: 用配置的阈值检查一组评分。所有适用的度量都必须通过。
        /// </summary>
        public static bool Passes(in QualityScores s, AtoQualityParameters q, AtoTextureKind kind, AtoAlphaMode alphaMode)
        {
            switch (kind)
            {
                case AtoTextureKind.Normal:
                    return s.NormalAngleP95 <= q.maxNormalAngleP95;
                case AtoTextureKind.Grayscale:
                    return s.GrayscaleRmse <= q.maxGrayscaleRmse;
                default:
                    if (s.MsSsim < q.minMsSsim) return false;
                    if (s.DeltaE95 > q.maxDeltaE2000) return false;
                    if (alphaMode == AtoAlphaMode.Cutout && s.AlphaIoU < q.minAlphaIoU) return false;
                    if (alphaMode == AtoAlphaMode.Blend && s.AlphaRmse > q.maxAlphaRmse) return false;
                    return true;
            }
        }

        #region SSIM

        /// <summary>
        /// EN: Single scale SSIM on Rec.709 luminance with an 8x8 uniform window.
        /// ZH: 在 Rec.709 亮度上使用 8x8 均匀窗口的单尺度 SSIM。
        /// </summary>
        public static float Ssim(LinearImage a, LinearImage b)
        {
            using var lumA = ToLuminance(a);
            using var lumB = ToLuminance(b);
            return SsimOnLuminance(lumA, a.Width, a.Height, lumB);
        }

        /// <summary>
        /// EN: MS-SSIM over up to five octaves, weighted with the reference implementation's weights.
        ///     Scales that would fall below the window size are dropped and the weights renormalized.
        /// ZH: 最多五个倍频程的 MS-SSIM，使用参考实现的权重。
        ///     低于窗口尺寸的尺度会被丢弃并重新归一化权重。
        /// </summary>
        public static float MultiScaleSsim(LinearImage a, LinearImage b)
        {
            var lumA = ToLuminance(a);
            var lumB = ToLuminance(b);
            int w = a.Width, h = a.Height;

            float product = 1f;
            float weightSum = 0f;
            try
            {
                for (int scale = 0; scale < MsSsimWeights.Length; scale++)
                {
                    if (Mathf.Min(w, h) < 8) break;
                    float s = SsimOnLuminance(lumA, w, h, lumB);
                    float weight = MsSsimWeights[scale];
                    product *= Mathf.Pow(Mathf.Max(s, 1e-6f), weight);
                    weightSum += weight;

                    if (scale == MsSsimWeights.Length - 1) break;
                    var nextA = HalveLuminance(lumA, w, h, out int nw, out int nh);
                    var nextB = HalveLuminance(lumB, w, h, out _, out _);
                    lumA.Dispose(); lumB.Dispose();
                    lumA = nextA; lumB = nextB;
                    w = nw; h = nh;
                }
            }
            finally
            {
                lumA.Dispose();
                lumB.Dispose();
            }

            if (weightSum <= 0f) return 1f;
            // EN: Renormalize so a truncated pyramid is still comparable to a full one.
            // ZH: 重新归一化，使被截断的金字塔仍可与完整金字塔比较。
            return Mathf.Pow(product, 1f / weightSum);
        }

        private static NativeArray<float> ToLuminance(LinearImage img)
        {
            var lum = new NativeArray<float>(img.Width * img.Height, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            new LuminanceJob { Src = img.Pixels, Dst = lum }.Schedule(lum.Length, 512).Complete();
            return lum;
        }

        private static NativeArray<float> HalveLuminance(NativeArray<float> src, int w, int h, out int nw, out int nh)
        {
            nw = Mathf.Max(1, w / 2);
            nh = Mathf.Max(1, h / 2);
            var dst = new NativeArray<float>(nw * nh, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            new HalveJob { Src = src, SrcW = w, SrcH = h, Dst = dst, DstW = nw, DstH = nh }.Schedule(dst.Length, 512).Complete();
            return dst;
        }

        private static float SsimOnLuminance(NativeArray<float> a, int w, int h, NativeArray<float> b)
        {
            const int win = 8;
            int bx = Mathf.Max(1, w / win);
            int by = Mathf.Max(1, h / win);
            var results = new NativeArray<float>(bx * by, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            try
            {
                new SsimBlockJob
                {
                    A = a, B = b, Width = w, Height = h, Window = win, BlocksX = bx, Result = results,
                }.Schedule(results.Length, 32).Complete();

                double sum = 0;
                for (int i = 0; i < results.Length; i++) sum += results[i];
                return (float)(sum / results.Length);
            }
            finally
            {
                results.Dispose();
            }
        }

        [BurstCompile]
        private struct LuminanceJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Color> Src;
            public NativeArray<float> Dst;
            public void Execute(int i)
            {
                var c = Src[i];
                Dst[i] = ColorMath.Luminance(new float3(c.r, c.g, c.b));
            }
        }

        [BurstCompile]
        private struct HalveJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> Src;
            public int SrcW, SrcH, DstW, DstH;
            public NativeArray<float> Dst;
            public void Execute(int i)
            {
                int x = i % DstW, y = i / DstW;
                int x0 = math.min(x * 2, SrcW - 1), x1 = math.min(x * 2 + 1, SrcW - 1);
                int y0 = math.min(y * 2, SrcH - 1), y1 = math.min(y * 2 + 1, SrcH - 1);
                Dst[i] = 0.25f * (Src[y0 * SrcW + x0] + Src[y0 * SrcW + x1] + Src[y1 * SrcW + x0] + Src[y1 * SrcW + x1]);
            }
        }

        [BurstCompile]
        private struct SsimBlockJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> A;
            [ReadOnly] public NativeArray<float> B;
            public int Width, Height, Window, BlocksX;
            public NativeArray<float> Result;

            public void Execute(int index)
            {
                int bx = index % BlocksX;
                int by = index / BlocksX;
                int x0 = bx * Window, y0 = by * Window;
                int x1 = math.min(x0 + Window, Width);
                int y1 = math.min(y0 + Window, Height);

                float n = 0, sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0;
                for (int y = y0; y < y1; y++)
                {
                    for (int x = x0; x < x1; x++)
                    {
                        float va = A[y * Width + x];
                        float vb = B[y * Width + x];
                        sa += va; sb += vb;
                        saa += va * va; sbb += vb * vb; sab += va * vb;
                        n++;
                    }
                }
                if (n < 1f) { Result[index] = 1f; return; }

                float ma = sa / n, mb = sb / n;
                float va2 = math.max(0f, saa / n - ma * ma);
                float vb2 = math.max(0f, sbb / n - mb * mb);
                float cov = sab / n - ma * mb;

                // EN: Stabilizing constants for a dynamic range of 1.0 (linear float data).
                // ZH: 针对动态范围 1.0（线性浮点数据）的稳定常数。
                const float c1 = 0.01f * 0.01f;
                const float c2 = 0.03f * 0.03f;

                float ssim = ((2f * ma * mb + c1) * (2f * cov + c2)) /
                             ((ma * ma + mb * mb + c1) * (va2 + vb2 + c2));
                Result[index] = math.clamp(ssim, -1f, 1f);
            }
        }

        #endregion

        #region Colour difference

        /// <summary>
        /// EN: Returns the requested percentile of the per texel CIEDE2000 difference.
        /// ZH: 返回逐像素 CIEDE2000 色差的指定分位数。
        /// </summary>
        public static float DeltaEPercentile(LinearImage a, LinearImage b, float percentile)
        {
            int n = a.Pixels.Length;
            var diffs = new NativeArray<float>(n, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            try
            {
                new DeltaEJob { A = a.Pixels, B = b.Pixels, Out = diffs }.Schedule(n, 256).Complete();
                return Percentile(diffs, percentile);
            }
            finally
            {
                diffs.Dispose();
            }
        }

        [BurstCompile]
        private struct DeltaEJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Color> A;
            [ReadOnly] public NativeArray<Color> B;
            public NativeArray<float> Out;
            public void Execute(int i)
            {
                var ca = A[i]; var cb = B[i];
                var la = ColorMath.LinearToLab(new float3(ca.r, ca.g, ca.b));
                var lb = ColorMath.LinearToLab(new float3(cb.r, cb.g, cb.b));
                Out[i] = ColorMath.DeltaE2000(la, lb);
            }
        }

        #endregion

        #region Alpha

        /// <summary>
        /// EN: Intersection over union of the clipped silhouettes. When both silhouettes are empty the
        ///     result is 1, which is the correct "no difference" answer.
        /// ZH: 裁剪后轮廓的交并比。两个轮廓都为空时结果为 1，这是正确的“无差异”答案。
        /// </summary>
        public static float SilhouetteIoU(LinearImage a, LinearImage b, float cutoff)
        {
            int inter = 0, union = 0;
            for (int i = 0; i < a.Pixels.Length; i++)
            {
                bool pa = a.Pixels[i].a >= cutoff;
                bool pb = b.Pixels[i].a >= cutoff;
                if (pa && pb) inter++;
                if (pa || pb) union++;
            }
            return union == 0 ? 1f : (float)inter / union;
        }

        #endregion

        #region RMSE

        /// <summary>EN: Linear RMSE of a single channel (0=R,1=G,2=B,3=A). ZH: 单个通道（0=R,1=G,2=B,3=A）的线性 RMSE。</summary>
        public static float ChannelRmse(LinearImage a, LinearImage b, int channel)
        {
            double sum = 0;
            int n = a.Pixels.Length;
            for (int i = 0; i < n; i++)
            {
                float va = Get(a.Pixels[i], channel);
                float vb = Get(b.Pixels[i], channel);
                double d = va - vb;
                sum += d * d;
            }
            return (float)Math.Sqrt(sum / Math.Max(1, n));
        }

        /// <summary>
        /// EN: Worst RMSE across the channels the shader actually consumes.
        /// ZH: 着色器实际使用的各通道中最差的 RMSE。
        /// </summary>
        public static float WorstChannelRmse(LinearImage a, LinearImage b, int usedChannels)
        {
            if (usedChannels == 0) usedChannels = 0xF;
            float worst = 0f;
            for (int c = 0; c < 4; c++)
                if ((usedChannels & (1 << c)) != 0)
                    worst = Mathf.Max(worst, ChannelRmse(a, b, c));
            return worst;
        }

        private static float Get(Color c, int channel)
            => channel == 0 ? c.r : channel == 1 ? c.g : channel == 2 ? c.b : c.a;

        #endregion

        #region Normals

        /// <summary>
        /// EN: Percentile of the angle between decoded, renormalized normals.
        /// ZH: 解码并重归一化后的法线之间夹角的分位数。
        /// </summary>
        public static float NormalAnglePercentile(LinearImage a, LinearImage b, NormalEncoding encoding, float percentile)
        {
            int n = a.Pixels.Length;
            var angles = new NativeArray<float>(n, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            try
            {
                new NormalAngleJob { A = a.Pixels, B = b.Pixels, Encoding = (int)encoding, Out = angles }
                    .Schedule(n, 256).Complete();
                return Percentile(angles, percentile);
            }
            finally
            {
                angles.Dispose();
            }
        }

        [BurstCompile]
        private struct NormalAngleJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Color> A;
            [ReadOnly] public NativeArray<Color> B;
            public int Encoding;
            public NativeArray<float> Out;

            public void Execute(int i)
            {
                var na = Decode(A[i], Encoding);
                var nb = Decode(B[i], Encoding);
                float d = math.clamp(math.dot(na, nb), -1f, 1f);
                Out[i] = math.degrees(math.acos(d));
            }

            private static float3 Decode(Color c, int encoding)
            {
                float x, y;
                switch (encoding)
                {
                    case 1: x = c.a * 2f - 1f; y = c.g * 2f - 1f; break; // AlphaGreen (DXT5nm)
                    case 2: x = c.r * 2f - 1f; y = c.g * 2f - 1f; break; // RedGreen (BC5)
                    default:
                        {
                            var v = new float3(c.r * 2f - 1f, c.g * 2f - 1f, c.b * 2f - 1f);
                            float len = math.length(v);
                            return len > 1e-6f ? v / len : new float3(0, 0, 1);
                        }
                }
                float z = math.sqrt(math.max(0f, 1f - x * x - y * y));
                var n = new float3(x, y, z);
                float l = math.length(n);
                return l > 1e-6f ? n / l : new float3(0, 0, 1);
            }
        }

        #endregion

        /// <summary>
        /// EN: Percentile of an unsorted array. Sorts a copy; islands are small so this is cheap.
        /// ZH: 未排序数组的分位数。会排序一份副本；岛很小，因此开销很低。
        /// </summary>
        private static float Percentile(NativeArray<float> values, float percentile)
        {
            if (values.Length == 0) return 0f;
            var copy = new float[values.Length];
            values.CopyTo(copy);
            Array.Sort(copy);
            int idx = Mathf.Clamp(Mathf.CeilToInt(percentile * (copy.Length - 1)), 0, copy.Length - 1);
            return copy[idx];
        }
    }
}
