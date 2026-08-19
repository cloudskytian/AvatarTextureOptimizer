// AvatarTextureOptimizer
// File: Editor/Quality/CIEDE2000.cs
//
// CIEDE2000 color difference (Sharma, Wu & Dalal 2005) implemented in Burst
// for parallel evaluation, with a scalar CPU fallback.
// Pipeline: linear RGB -> XYZ (D65) -> Lab -> CIEDE2000.
//
// CIEDE2000 色差（Sharma, Wu & Dalal 2005）的 Burst 并行实现，附标量 CPU
// 兜底。流水线：线性 RGB -> XYZ（D65）-> Lab -> CIEDE2000。

using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor.quality
{
    public static class CIEDE2000
    {
        // ---- Color space conversions (static, Burst-compatible) ----
        // ---- 色彩空间转换（静态、Burst 兼容） ----

        /// <summary>Linear RGB (D65) -> Lab. / 线性 RGB（D65）-> Lab。</summary>
        public static Vector3 RGBToLab(Vector3 linearRGB)
        {
            // sRGB D65 matrix / sRGB D65 矩阵
            float X = 0.4124564f * linearRGB.x + 0.3575761f * linearRGB.y + 0.1804375f * linearRGB.z;
            float Y = 0.2126729f * linearRGB.x + 0.7151522f * linearRGB.y + 0.0721750f * linearRGB.z;
            float Z = 0.0193339f * linearRGB.x + 0.1191920f * linearRGB.y + 0.9503041f * linearRGB.z;

            // D65 white point / D65 白点
            const float Xn = 0.95047f, Yn = 1.00000f, Zn = 1.08883f;
            float fx = F(X / Xn);
            float fy = F(Y / Yn);
            float fz = F(Z / Zn);

            float L = 116f * fy - 16f;
            float a = 500f * (fx - fy);
            float b = 200f * (fy - fz);
            return new Vector3(L, a, b);
        }

        private static float F(float t)
        {
            const float delta = 6f / 29f;
            return t > delta * delta * delta ? Mathf.Pow(t, 1f / 3f) : t / (3f * delta * delta) + 4f / 29f;
        }

        /// <summary>CIEDE2000 between two Lab colors. / 两个 Lab 颜色之间的 CIEDE2000。</summary>
        public static float DeltaE2000(Vector3 lab1, Vector3 lab2)
        {
            float L1 = lab1.x, a1 = lab1.y, b1 = lab1.z;
            float L2 = lab2.x, a2 = lab2.y, b2 = lab2.z;

            float C1 = Mathf.Sqrt(a1 * a1 + b1 * b1);
            float C2 = Mathf.Sqrt(a2 * a2 + b2 * b2);
            float Cbar = (C1 + C2) * 0.5f;
            float Cbar7 = Mathf.Pow(Cbar, 7f);
            float G = 0.5f * (1f - Mathf.Sqrt(Cbar7 / (Cbar7 + 6103515625f))); // 25^7 = 6103515625
            float a1p = (1f + G) * a1;
            float a2p = (1f + G) * a2;

            float C1p = Mathf.Sqrt(a1p * a1p + b1 * b1);
            float C2p = Mathf.Sqrt(a2p * a2p + b2 * b2);
            float h1p = Mathf.Atan2(b1, a1p) * Mathf.Rad2Deg;
            float h2p = Mathf.Atan2(b2, a2p) * Mathf.Rad2Deg;
            if (h1p < 0) h1p += 360f;
            if (h2p < 0) h2p += 360f;

            float dLp = L2 - L1;
            float dCp = C2p - C1p;
            float dhp;
            if (C1p * C2p == 0f) dhp = 0f;
            else
            {
                dhp = h2p - h1p;
                if (dhp > 180f) dhp -= 360f;
                else if (dhp < -180f) dhp += 360f;
            }
            float dHp = 2f * Mathf.Sqrt(C1p * C2p) * Mathf.Sin(dhp * 0.5f * Mathf.Deg2Rad);

            float Lbarp = (L1 + L2) * 0.5f;
            float Cbarp = (C1p + C2p) * 0.5f;
            float hbarp;
            if (C1p * C2p == 0f) hbarp = h1p + h2p;
            else
            {
                hbarp = (h1p + h2p) * 0.5f;
                if (Mathf.Abs(h1p - h2p) > 180f)
                {
                    if (h1p + h2p < 360f) hbarp += 180f;
                    else hbarp -= 180f;
                }
            }

            float T = 1f - 0.17f * Mathf.Cos((hbarp - 30f) * Mathf.Deg2Rad)
                          + 0.24f * Mathf.Cos(2f * hbarp * Mathf.Deg2Rad)
                          + 0.32f * Mathf.Cos((3f * hbarp + 6f) * Mathf.Deg2Rad)
                          - 0.20f * Mathf.Cos((4f * hbarp - 63f) * Mathf.Deg2Rad);

            float dTheta = 30f * Mathf.Exp(-Mathf.Pow((hbarp - 275f) / 25f, 2f));
            float RC = 2f * Mathf.Sqrt(Mathf.Pow(Cbarp, 7f) / (Mathf.Pow(Cbarp, 7f) + 6103515625f));
            float SL = 1f + 0.015f * Mathf.Pow(Lbarp - 50f, 2f) / Mathf.Sqrt(20f + Mathf.Pow(Lbarp - 50f, 2f));
            float SC = 1f + 0.045f * Cbarp;
            float SH = 1f + 0.015f * Cbarp * T;
            float RT = -Mathf.Sin(2f * dTheta * Mathf.Deg2Rad) * RC;

            float lTerm = dLp / SL;
            float cTerm = dCp / SC;
            float hTerm = dHp / SH;

            return Mathf.Sqrt(lTerm * lTerm + cTerm * cTerm + hTerm * hTerm + RT * cTerm * hTerm);
        }

        // ---- Burst job: mean ΔE over two linear-color arrays ----
        // ---- Burst 任务：两个线性颜色数组的平均 ΔE ----

        [BurstCompile]
        public struct MeanDeltaEJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Color> A;
            [ReadOnly] public NativeArray<Color> B;
            [WriteOnly] public NativeArray<float> Result;

            public void Execute(int i)
            {
                var lab1 = RGBToLab(new Vector3(A[i].r, A[i].g, A[i].b));
                var lab2 = RGBToLab(new Vector3(B[i].r, B[i].g, B[i].b));
                Result[i] = DeltaE2000(lab1, lab2);
            }
        }

        /// <summary>Compute the mean CIEDE2000 between two linear color arrays. / 计算两个线性颜色数组的平均 CIEDE2000。</summary>
        public static float ComputeMean(Color[] a, Color[] b)
        {
            if (a == null || b == null || a.Length == 0 || a.Length != b.Length) return 0f;
            int n = a.Length;
            if (n > 65536)
            {
                // Cap per-job size to stay within Burst limits; process in
                // chunks. 限制每个任务大小以保持在 Burst 限制内；分块处理。
                int chunk = 65536;
                double total = 0;
                for (int offset = 0; offset < n; offset += chunk)
                {
                    int len = Mathf.Min(chunk, n - offset);
                    total += ComputeMeanChunk(a, b, offset, len);
                }
                return (float)(total / n);
            }
            return ComputeMeanChunk(a, b, 0, n);
        }

        private static float ComputeMeanChunk(Color[] a, Color[] b, int offset, int len)
        {
            using var na = new NativeArray<Color>(len, Allocator.TempJob);
            using var nb = new NativeArray<Color>(len, Allocator.TempJob);
            using var result = new NativeArray<float>(len, Allocator.TempJob);
            for (int i = 0; i < len; i++)
            {
                na[i] = a[offset + i];
                nb[i] = b[offset + i];
            }
            var job = new MeanDeltaEJob { A = na, B = nb, Result = result };
            job.Schedule(len, 64).Complete();
            double sum = 0;
            for (int i = 0; i < len; i++) sum += result[i];
            return (float)(sum / len);
        }
    }
}
