// QualityJobs — Burst jobs & metric aggregation / Burst 作业与指标聚合
// Linear-space resampling (alpha-premultiplied for transparent), CIEDE2000, SSIM/MS-SSIM,
// cutout IoU / blend alpha RMSE, normal angle error, mask linear RMSE.<br>
// 线性空间重采样（透明预乘alpha）、CIEDE2000、SSIM/MS-SSIM、Cutout轮廓IoU/Blend alpha线性RMSE、
// 法线角度误差、蒙版线性RMSE。所有差异像素写入数组后用直方图求 P95。
using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Fosa.ATO.Editor
{
    internal static class QualityJobs
    {
        // ------------------------------------------------------------ resample
        /// <summary>Bilinear resample in the given (already linear, maybe premultiplied) domain. / 双线性重采样。</summary>
        [BurstCompile]
        internal struct ResampleJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> src; // rgba interleaved
            public int srcW, srcH;
            public NativeArray<float> dst;
            public int dstW, dstH;

            public void Execute(int index)
            {
                int dy = index / dstW, dx = index - dy * dstW;
                float fx = (dx + 0.5f) * srcW / dstW - 0.5f;
                float fy = (dy + 0.5f) * srcH / dstH - 0.5f;
                int x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, srcW - 1);
                int y0 = Mathf.Clamp(Mathf.FloorToInt(fy), 0, srcH - 1);
                int x1 = Mathf.Min(x0 + 1, srcW - 1), y1 = Mathf.Min(y0 + 1, srcH - 1);
                float tx = Mathf.Clamp01(fx - x0), ty = Mathf.Clamp01(fy - y0);
                int i00 = (y0 * srcW + x0) * 4, i10 = (y0 * srcW + x1) * 4, i01 = (y1 * srcW + x0) * 4, i11 = (y1 * srcW + x1) * 4;
                int o = index * 4;
                for (int c = 0; c < 4; c++)
                {
                    float v0 = Mathf.Lerp(src[i00 + c], src[i10 + c], tx);
                    float v1 = Mathf.Lerp(src[i01 + c], src[i11 + c], tx);
                    dst[o + c] = Mathf.Lerp(v0, v1, ty);
                }
            }
        }

        /// <summary>Premultiply / unpremultiply alpha in place. / 原地预乘/解预乘 alpha。</summary>
        [BurstCompile]
        internal struct PremultiplyJob : IJobParallelFor
        {
            public NativeArray<float> buf;
            public bool unpremultiply;
            public void Execute(int i)
            {
                int o = i * 4;
                float a = buf[o + 3];
                if (unpremultiply)
                {
                    if (a > 1e-5f) { buf[o] /= a; buf[o + 1] /= a; buf[o + 2] /= a; }
                }
                else { buf[o] *= a; buf[o + 1] *= a; buf[o + 2] *= a; }
            }
        }

        // ------------------------------------------------------------ ΔE2000
        /// <summary>CIEDE2000 between two linear-RGB images (alpha ignored here). / 两线性RGB图间 CIEDE2000。</summary>
        [BurstCompile]
        internal struct DeltaEJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> a;
            [ReadOnly] public NativeArray<float> b;
            public NativeArray<float> outDE;
            public void Execute(int i)
            {
                int o = i * 4;
                Lab(a[o], a[o + 1], a[o + 2], out float L1, out float a1, out float b1);
                Lab(b[o], b[o + 1], b[o + 2], out float L2, out float a2, out float b2);
                outDE[i] = DeltaE2000(L1, a1, b1, L2, a2, b2);
            }

            internal static void Lab(float lr, float lg, float lb, out float L, out float A, out float B)
            {
                // linear RGB (D65 sRGB primaries) → XYZ → Lab / 线性RGB → XYZ → Lab
                float x = lr * 0.4124f + lg * 0.3576f + lb * 0.1805f;
                float y = lr * 0.2126f + lg * 0.7152f + lb * 0.0722f;
                float z = lr * 0.0193f + lg * 0.1192f + lb * 0.9505f;
                x = F(x / 0.95047f); y = F(y); z = F(z / 1.08883f);
                L = 116f * y - 16f; A = 500f * (x - y); B = 200f * (y - z);
            }
            private static float F(float t) => t > 0.008856f ? Mathf.Pow(t, 1f / 3f) : 7.787f * t + 16f / 116f;

            internal static float DeltaE2000(float L1, float a1, float b1, float L2, float a2, float b2)
            {
                // Sharma et al. CIEDE2000 / Sharma 等 CIEDE2000 公式
                float c1 = Mathf.Sqrt(a1 * a1 + b1 * b1), c2 = Mathf.Sqrt(a2 * a2 + b2 * b2);
                float cBar = (c1 + c2) * 0.5f;
                float cBar7 = Mathf.Pow(cBar, 7f);
                float g = 0.5f * (1f - Mathf.Sqrt(cBar7 / (cBar7 + 6103515625f))); // 25^7
                float a1p = a1 * (1f + g), a2p = a2 * (1f + g);
                float c1p = Mathf.Sqrt(a1p * a1p + b1 * b1), c2p = Mathf.Sqrt(a2p * a2p + b2 * b2);
                float h1p = HueAngle(b1, a1p), h2p = HueAngle(b2, a2p);
                float dLp = L2 - L1, dCp = c2p - c1p;
                float dhp;
                if (c1p * c2p < 1e-6f) dhp = 0f;
                else
                {
                    dhp = h2p - h1p;
                    if (dhp > 180f) dhp -= 360f; else if (dhp < -180f) dhp += 360f;
                }
                float dHp = 2f * Mathf.Sqrt(c1p * c2p) * Mathf.Sin(dhp * Mathf.PI / 360f);
                float lBar = (L1 + L2) * 0.5f, cBarP = (c1p + c2p) * 0.5f;
                float hBarP;
                if (c1p * c2p < 1e-6f) hBarP = h1p + h2p;
                else
                {
                    float sum = h1p + h2p;
                    if (Mathf.Abs(h1p - h2p) > 180f) sum += sum < 360f ? 360f : -360f;
                    hBarP = sum * 0.5f;
                }
                float t = 1f - 0.17f * Mathf.Cos((hBarP - 30f) * Mathf.Deg2Rad)
                          + 0.24f * Mathf.Cos(2f * hBarP * Mathf.Deg2Rad)
                          + 0.32f * Mathf.Cos((3f * hBarP + 6f) * Mathf.Deg2Rad)
                          - 0.20f * Mathf.Cos((4f * hBarP - 63f) * Mathf.Deg2Rad);
                float dTheta = 30f * Mathf.Exp(-Mathf.Pow((hBarP - 275f) / 25f, 2f));
                float cBarP7 = Mathf.Pow(cBarP, 7f);
                float rC = 2f * Mathf.Sqrt(cBarP7 / (cBarP7 + 6103515625f));
                float sL = 1f + 0.015f * (lBar - 50f) * (lBar - 50f) / Mathf.Sqrt(20f + (lBar - 50f) * (lBar - 50f));
                float sC = 1f + 0.045f * cBarP;
                float sH = 1f + 0.015f * cBarP * t;
                float rT = -Mathf.Sin(2f * dTheta * Mathf.Deg2Rad) * rC;
                float vL = dLp / sL, vC = dCp / sC, vH = dHp / sH;
                return Mathf.Sqrt(vL * vL + vC * vC + vH * vH + rT * vC * vH);
            }

            private static float HueAngle(float b, float ap)
            {
                if (Mathf.Abs(ap) < 1e-6f && Mathf.Abs(b) < 1e-6f) return 0f;
                float h = Mathf.Atan2(b, ap) * Mathf.Rad2Deg;
                return h < 0 ? h + 360f : h;
            }
        }

        // ------------------------------------------------------------ SSIM core
        /// <summary>Windowed SSIM/CS map over luminance (11×11 gaussian). / 亮度上加窗 SSIM/CS。</summary>
        [BurstCompile]
        internal struct SsimJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> lumA; // w*h luminance (premultiplied if transparent)
            [ReadOnly] public NativeArray<float> lumB;
            public int w, h;
            public bool csOnly;                    // true → output contrast*structure, false → full SSIM / CS或全SSIM
            public NativeArray<float> outMap;

            public void Execute(int index)
            {
                int y = index / w, x = index - y * w;
                float muA = 0, muB = 0, wsum = 0;
                const int R = 5; // 11×11 window radius / 窗口半径
                for (int wy = -R; wy <= R; wy++)
                {
                    int yy = Mathf.Clamp(y + wy, 0, h - 1);
                    for (int wx = -R; wx <= R; wx++)
                    {
                        int xx = Mathf.Clamp(x + wx, 0, w - 1);
                        float gw = Gauss(wx) * Gauss(wy);
                        muA += gw * lumA[yy * w + xx];
                        muB += gw * lumB[yy * w + xx];
                        wsum += gw;
                    }
                }
                muA /= wsum; muB /= wsum;
                float va = 0, vb = 0, cov = 0;
                for (int wy = -R; wy <= R; wy++)
                {
                    int yy = Mathf.Clamp(y + wy, 0, h - 1);
                    for (int wx = -R; wx <= R; wx++)
                    {
                        int xx = Mathf.Clamp(x + wx, 0, w - 1);
                        float gw = Gauss(wx) * Gauss(wy);
                        float da = lumA[yy * w + xx] - muA, db = lumB[yy * w + xx] - muB;
                        va += gw * da * da; vb += gw * db * db; cov += gw * da * db;
                    }
                }
                va /= wsum; vb /= wsum; cov /= wsum;
                const float C2 = 0.0009f, C3 = 0.00045f; // (0.03L)^2 & C2/2, L=1 / 动态范围1
                if (csOnly)
                {
                    outMap[index] = (2f * cov + C3) / (va + vb + C3);
                }
                else
                {
                    const float C1 = 0.0001f; // (0.01L)^2
                    outMap[index] = ((2f * muA * muB + C1) * (2f * cov + C2)) / ((muA * muA + muB * muB + C1) * (va + vb + C2));
                }
            }
            internal static float Gauss(int d) => Mathf.Exp(-(d * d) / (2f * 1.5f * 1.5f));
        }

        /// <summary>Luminance map (Rec.709 over linear RGB, alpha-premultiplied content as provided). / 亮度图。</summary>
        [BurstCompile]
        internal struct LuminanceJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> rgba;
            public NativeArray<float> lum;
            public void Execute(int i)
            {
                int o = i * 4;
                lum[i] = 0.2126f * rgba[o] + 0.7152f * rgba[o + 1] + 0.0722f * rgba[o + 2];
            }
        }

        /// <summary>2× box downsample of float map (MS-SSIM pyramid). / 浮点图2倍盒式下采样。</summary>
        [BurstCompile]
        internal struct HalfJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> src;
            public int srcW, srcH;
            public NativeArray<float> dst; // (w+1)/2 × (h+1)/2
            public int dstW, dstH;
            public void Execute(int i)
            {
                int y = i / dstW, x = i - y * dstW;
                int sx = x * 2, sy = y * 2;
                float sum = 0; int n = 0;
                for (int dy = 0; dy < 2; dy++)
                for (int dx = 0; dx < 2; dx++)
                {
                    int xx = Mathf.Min(sx + dx, srcW - 1), yy = Mathf.Min(sy + dy, srcH - 1);
                    sum += src[yy * srcW + xx]; n++;
                }
                dst[i] = sum / n;
            }
        }

        // ------------------------------------------------------------ squared errors / angles / IoU
        /// <summary>Per-pixel squared error summed over flagged channels. / 逐像素平方误差（按通道位掩码）。</summary>
        [BurstCompile]
        internal struct SqErrorJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> a;
            [ReadOnly] public NativeArray<float> b;
            public int channelFlags;  // bits 0..3 / 通道位
            public NativeArray<float> outErr; // per pixel: sum over flagged channels / 逐像素：按通道求和
            public void Execute(int i)
            {
                int o = i * 4;
                float s = 0;
                for (int c = 0; c < 4; c++)
                {
                    if ((channelFlags & (1 << c)) == 0) continue;
                    float d = a[o + c] - b[o + c];
                    s += d * d;
                }
                outErr[i] = s;
            }
        }

        /// <summary>Normal map angle error (deg) between encoded normal textures. / 法线贴图角度误差(度)。</summary>
        [BurstCompile]
        internal struct NormalAngleJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> a; // encoded rgb normals (linear buffer but raw data)
            [ReadOnly] public NativeArray<float> b;
            public NativeArray<float> outDeg;
            public void Execute(int i)
            {
                int o = i * 4;
                float ax = a[o] * 2f - 1f, ay = a[o + 1] * 2f - 1f, az = a[o + 2] * 2f - 1f;
                float bx = b[o] * 2f - 1f, by = b[o + 1] * 2f - 1f, bz = b[o + 2] * 2f - 1f;
                float la = Mathf.Sqrt(ax * ax + ay * ay + az * az), lb = Mathf.Sqrt(bx * bx + by * by + bz * bz);
                if (la < 1e-5f || lb < 1e-5f) { outDeg[i] = 0f; return; }
                float dot = Mathf.Clamp((ax * bx + ay * by + az * bz) / (la * lb), -1f, 1f);
                outDeg[i] = Mathf.Acos(dot) * Mathf.Rad2Deg;
            }
        }

        /// <summary>Renormalize encoded normals in place (after linear resample). / 线性重采样后重归一化并编码。</summary>
        [BurstCompile]
        internal struct RenormalizeJob : IJobParallelFor
        {
            public NativeArray<float> buf;
            public void Execute(int i)
            {
                int o = i * 4;
                float x = buf[o] * 2f - 1f, y = buf[o + 1] * 2f - 1f, z = buf[o + 2] * 2f - 1f;
                float l = Mathf.Sqrt(x * x + y * y + z * z);
                if (l > 1e-5f) { x /= l; y /= l; z /= l; }
                buf[o] = x * 0.5f + 0.5f; buf[o + 1] = y * 0.5f + 0.5f; buf[o + 2] = z * 0.5f + 0.5f;
            }
        }

        /// <summary>Cutout silhouette per-pixel mask bits (bit0: a solid, bit1: b solid). / Cutout 轮廓逐像素位标记。</summary>
        [BurstCompile]
        internal struct CutoutMaskJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> a;
            [ReadOnly] public NativeArray<float> b;
            public float cutoff;
            public NativeArray<byte> masks;
            public void Execute(int i)
            {
                byte v = 0;
                if (a[i * 4 + 3] >= cutoff) v |= 1;
                if (b[i * 4 + 3] >= cutoff) v |= 2;
                masks[i] = v;
            }
        }

        /// <summary>IoU from per-pixel mask bits (managed aggregation, no races). / 由位标记聚合 IoU（无竞态）。</summary>
        internal static float IoU(NativeArray<byte> masks)
        {
            long inter = 0, uni = 0;
            for (int i = 0; i < masks.Length; i++)
            {
                if (masks[i] == 3) inter++;
                if (masks[i] != 0) uni++;
            }
            return uni > 0 ? inter / (float)uni : 1f;
        }

        // ------------------------------------------------------------ aggregation helpers (managed)
        /// <summary>Mean of float map. / 浮点图均值。</summary>
        internal static float Mean(NativeArray<float> map)
        {
            double s = 0;
            for (int i = 0; i < map.Length; i++) s += map[i];
            return map.Length > 0 ? (float)(s / map.Length) : 1f;
        }

        /// <summary>P95 via 1024-bin histogram (values ≥ 0, magnitude scaled by caller). / 1024桶直方图求P95。</summary>
        internal static float P95(NativeArray<float> map, float scaleHint)
        {
            int n = map.Length;
            if (n == 0) return 0f;
            // find max for bin range / 先扫最大值定桶范围
            float max = 0f;
            for (int i = 0; i < n; i++) if (map[i] > max) max = map[i];
            if (max <= 0f) return 0f;
            var bins = new int[1024];
            for (int i = 0; i < n; i++)
            {
                int b = Mathf.Clamp((int)(map[i] / max * (bins.Length - 1)), 0, bins.Length - 1);
                bins[b]++;
            }
            int need = Mathf.CeilToInt(n * 0.95f), cum = 0;
            for (int b = 0; b < bins.Length; b++)
            {
                cum += bins[b];
                if (cum >= need) return (b + 0.5f) / (bins.Length - 1) * max;
            }
            return max;
        }

        internal static float MeanSqToRmse(double sumSq, long count) => count > 0 ? Mathf.Sqrt((float)(sumSq / count)) : 0f;

        /// <summary>Run a resample (down→up-back-compare choreography lives in Stage3). / 主流程在 Stage3 中编排。</summary>
        internal static NativeArray<float> Resample(NativeArray<float> src, int sw, int sh, int dw, int dh, Allocator alloc)
        {
            var dst = new NativeArray<float>(dw * dh * 4, alloc);
            new ResampleJob { src = src, srcW = sw, srcH = sh, dst = dst, dstW = dw, dstH = dh }
                .Schedule(dw * dh, 64).Complete();
            return dst;
        }

        internal static float[] GaussianKernel1D()
        {
            var k = new float[11];
            for (int i = -5; i <= 5; i++) k[i + 5] = SsimJob.Gauss(i);
            return k;
        }
    }
}
