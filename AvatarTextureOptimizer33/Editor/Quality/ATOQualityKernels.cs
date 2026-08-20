// SPDX-License-Identifier: MIT
// EN: Burst compiled image kernels used by the target quality algorithm: resampling, SSIM / MS-SSIM,
//     CIEDE2000, alpha metrics, normal angular error and grayscale RMSE.
// ZH: 目标质量算法使用的 Burst 图像内核：重采样、SSIM / MS-SSIM、CIEDE2000、alpha 度量、
//     法线角度误差与灰度 RMSE。

using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// EN: Copies an axis aligned region out of a full texture, optionally premultiplying alpha.
    /// ZH: 从整张贴图中拷贝一个轴对齐区域，可选地做 alpha 预乘。
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast, CompileSynchronously = false)]
    public struct ATOExtractRegionJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<half4> Source;
        public NativeArray<float4> Destination;
        public int SourceWidth;
        public int SourceHeight;
        public int X0;
        public int Y0;
        public int Width;
        public int Height;
        public bool PremultiplyAlpha;

        public void Execute(int row)
        {
            var sy = math.clamp(Y0 + row, 0, SourceHeight - 1);
            for (var x = 0; x < Width; x++)
            {
                var sx = math.clamp(X0 + x, 0, SourceWidth - 1);
                var c = (float4)Source[sy * SourceWidth + sx];
                if (PremultiplyAlpha) c = new float4(c.xyz * c.w, c.w);
                Destination[row * Width + x] = c;
            }
        }
    }

    /// <summary>
    /// EN: Area (box) downsample with arbitrary, non integer ratios. Input may be premultiplied.
    /// ZH: 支持任意非整数比例的面积（box）降采样。输入可以是预乘 alpha 的。
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast, CompileSynchronously = false)]
    public struct ATODownsampleJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float4> Source;
        public NativeArray<float4> Destination;
        public int SrcWidth;
        public int SrcHeight;
        public int DstWidth;
        public int DstHeight;

        public void Execute(int dy)
        {
            var scaleX = (float)SrcWidth / DstWidth;
            var scaleY = (float)SrcHeight / DstHeight;

            var y0 = dy * scaleY;
            var y1 = (dy + 1) * scaleY;
            var iy0 = (int)math.floor(y0);
            var iy1 = math.min(SrcHeight, (int)math.ceil(y1));

            for (var dx = 0; dx < DstWidth; dx++)
            {
                var x0 = dx * scaleX;
                var x1 = (dx + 1) * scaleX;
                var ix0 = (int)math.floor(x0);
                var ix1 = math.min(SrcWidth, (int)math.ceil(x1));

                var sum = float4.zero;
                var weight = 0f;

                for (var sy = iy0; sy < iy1; sy++)
                {
                    var wy = math.min(y1, sy + 1f) - math.max(y0, (float)sy);
                    if (wy <= 0f) continue;

                    for (var sx = ix0; sx < ix1; sx++)
                    {
                        var wx = math.min(x1, sx + 1f) - math.max(x0, (float)sx);
                        if (wx <= 0f) continue;

                        var w = wx * wy;
                        sum += Source[sy * SrcWidth + sx] * w;
                        weight += w;
                    }
                }

                Destination[dy * DstWidth + dx] = weight > 0f ? sum / weight : float4.zero;
            }
        }
    }

    /// <summary>
    /// EN: Bilinear upsample back to the reference resolution, mirroring what the GPU sampler does.
    /// ZH: 双线性上采样回参考分辨率，与 GPU 采样器的行为一致。
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast, CompileSynchronously = false)]
    public struct ATOUpsampleJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float4> Source;
        public NativeArray<float4> Destination;
        public int SrcWidth;
        public int SrcHeight;
        public int DstWidth;
        public int DstHeight;
        public bool UnpremultiplyAlpha;

        public void Execute(int dy)
        {
            var scaleX = (float)SrcWidth / DstWidth;
            var scaleY = (float)SrcHeight / DstHeight;

            var sy = (dy + 0.5f) * scaleY - 0.5f;
            var y0 = (int)math.floor(sy);
            var fy = sy - y0;

            for (var dx = 0; dx < DstWidth; dx++)
            {
                var sx = (dx + 0.5f) * scaleX - 0.5f;
                var x0 = (int)math.floor(sx);
                var fx = sx - x0;

                var c00 = Sample(x0, y0);
                var c10 = Sample(x0 + 1, y0);
                var c01 = Sample(x0, y0 + 1);
                var c11 = Sample(x0 + 1, y0 + 1);

                var c = math.lerp(math.lerp(c00, c10, fx), math.lerp(c01, c11, fx), fy);
                if (UnpremultiplyAlpha) c = new float4(c.w > 1e-5f ? c.xyz / c.w : float3.zero, c.w);
                Destination[dy * DstWidth + dx] = c;
            }
        }

        private float4 Sample(int x, int y)
        {
            x = math.clamp(x, 0, SrcWidth - 1);
            y = math.clamp(y, 0, SrcHeight - 1);
            return Source[y * SrcWidth + x];
        }
    }

    /// <summary>
    /// EN: Converts linear RGB to CIE Lab, keeping alpha in the w component.
    /// ZH: 把线性 RGB 转成 CIE Lab，alpha 保留在 w 分量。
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast, CompileSynchronously = false)]
    public struct ATOLinearToLabJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float4> Source;
        public NativeArray<float4> Destination;

        public void Execute(int i)
        {
            var c = Source[i];
            Destination[i] = new float4(ATOColor.LinearToLab(c.xyz), c.w);
        }
    }

    /// <summary>
    /// EN: Per row SSIM accumulation over an 8x8 window on the linear luminance channel.
    /// ZH: 在线性亮度通道上按 8x8 窗口逐行累加 SSIM。
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast, CompileSynchronously = false)]
    public struct ATOSsimJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float4> A;
        [ReadOnly] public NativeArray<float4> B;
        [ReadOnly] public NativeArray<byte> Coverage;
        public NativeArray<double> SumPerRow;

        /// <summary>EN: contrast-structure term only, needed by MS-SSIM. ZH: 仅对比度-结构项，MS-SSIM 需要。</summary>
        public NativeArray<double> CsPerRow;

        public NativeArray<int> CountPerRow;
        public int Width;
        public int Height;
        public int Window;

        public void Execute(int wy)
        {
            const float c1 = 0.0001f;   // (0.01 * L)^2 with L = 1
            const float c2 = 0.0009f;   // (0.03 * L)^2 with L = 1

            var y0 = wy * Window;
            double sum = 0;
            double csSum = 0;
            var count = 0;

            for (var x0 = 0; x0 + Window <= Width; x0 += Window)
            {
                if (y0 + Window > Height) break;

                float meanA = 0, meanB = 0;
                var n = 0;
                var covered = 0;

                for (var y = y0; y < y0 + Window; y++)
                for (var x = x0; x < x0 + Window; x++)
                {
                    var i = y * Width + x;
                    if (Coverage.Length > 0 && Coverage[i] == 0) continue;
                    covered++;
                    meanA += ATOColor.Luminance(A[i].xyz);
                    meanB += ATOColor.Luminance(B[i].xyz);
                    n++;
                }

                if (n < Window * Window / 2 || covered == 0) continue;

                meanA /= n;
                meanB /= n;

                float varA = 0, varB = 0, cov = 0;
                for (var y = y0; y < y0 + Window; y++)
                for (var x = x0; x < x0 + Window; x++)
                {
                    var i = y * Width + x;
                    if (Coverage.Length > 0 && Coverage[i] == 0) continue;
                    var da = ATOColor.Luminance(A[i].xyz) - meanA;
                    var db = ATOColor.Luminance(B[i].xyz) - meanB;
                    varA += da * da;
                    varB += db * db;
                    cov += da * db;
                }

                varA /= math.max(1, n - 1);
                varB /= math.max(1, n - 1);
                cov /= math.max(1, n - 1);

                // EN: luminance term and contrast-structure term of the SSIM index.
                // ZH: SSIM 指标的亮度项与对比度-结构项。
                var luminance = (2 * meanA * meanB + c1) / (meanA * meanA + meanB * meanB + c1);
                var cs = (2 * cov + c2) / (varA + varB + c2);

                sum += luminance * cs;
                csSum += cs;
                count++;
            }

            SumPerRow[wy] = sum;
            CsPerRow[wy] = csSum;
            CountPerRow[wy] = count;
        }
    }

    /// <summary>
    /// EN: CIEDE2000 colour difference statistics (mean + 1024 bin histogram for the p95).
    /// ZH: CIEDE2000 色差统计（均值 + 1024 桶直方图用于计算 p95）。
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Default, CompileSynchronously = false)]
    public struct ATODeltaE2000Job : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float4> LabA;
        [ReadOnly] public NativeArray<float4> LabB;
        [ReadOnly] public NativeArray<byte> Coverage;
        public NativeArray<double> SumPerRow;
        public NativeArray<int> CountPerRow;

        /// <summary>EN: Row major histogram, 1024 bins per row, bin = dE * 10 clamped. ZH: 行优先直方图，每行 1024 桶，桶 = dE*10 钳制。</summary>
        [NativeDisableParallelForRestriction] public NativeArray<int> Histogram;

        public int Width;

        public void Execute(int y)
        {
            double sum = 0;
            var count = 0;
            var histBase = y * 1024;

            for (var x = 0; x < Width; x++)
            {
                var i = y * Width + x;
                if (Coverage.Length > 0 && Coverage[i] == 0) continue;

                var d = ATOColor.DeltaE2000(LabA[i].xyz, LabB[i].xyz);
                sum += d;
                count++;

                var bin = math.clamp((int)(d * 10f), 0, 1023);
                Histogram[histBase + bin]++;
            }

            SumPerRow[y] = sum;
            CountPerRow[y] = count;
        }
    }

    /// <summary>
    /// EN: Alpha metrics: silhouette IoU against a cutoff and linear RMSE.
    /// ZH: alpha 度量：按 cutoff 计算轮廓 IoU，以及线性 RMSE。
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast, CompileSynchronously = false)]
    public struct ATOAlphaJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float4> A;
        [ReadOnly] public NativeArray<float4> B;
        [ReadOnly] public NativeArray<byte> Coverage;
        public NativeArray<int> IntersectionPerRow;
        public NativeArray<int> UnionPerRow;
        public NativeArray<double> SqErrPerRow;
        public NativeArray<int> CountPerRow;
        public int Width;
        public float Cutoff;

        public void Execute(int y)
        {
            var inter = 0;
            var uni = 0;
            double sq = 0;
            var count = 0;

            for (var x = 0; x < Width; x++)
            {
                var i = y * Width + x;
                if (Coverage.Length > 0 && Coverage[i] == 0) continue;

                var a = A[i].w;
                var b = B[i].w;
                var ka = a >= Cutoff;
                var kb = b >= Cutoff;
                if (ka && kb) inter++;
                if (ka || kb) uni++;

                var d = a - b;
                sq += d * d;
                count++;
            }

            IntersectionPerRow[y] = inter;
            UnionPerRow[y] = uni;
            SqErrPerRow[y] = sq;
            CountPerRow[y] = count;
        }
    }

    /// <summary>
    /// EN: Normal map angular error in degrees (mean + histogram for p95). Both inputs must be decoded and
    ///     renormalised unit vectors.
    /// ZH: 法线贴图的角度误差（度，均值 + 直方图求 p95）。两侧输入都必须是已解码并重新归一化的单位向量。
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Default, CompileSynchronously = false)]
    public struct ATONormalAngleJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float4> A;
        [ReadOnly] public NativeArray<float4> B;
        [ReadOnly] public NativeArray<byte> Coverage;
        public NativeArray<double> SumPerRow;
        public NativeArray<int> CountPerRow;

        /// <summary>EN: 1024 bins per row, bin = degrees * 5 clamped. ZH: 每行 1024 桶，桶 = 角度*5 钳制。</summary>
        [NativeDisableParallelForRestriction] public NativeArray<int> Histogram;

        public int Width;

        public void Execute(int y)
        {
            double sum = 0;
            var count = 0;
            var histBase = y * 1024;

            for (var x = 0; x < Width; x++)
            {
                var i = y * Width + x;
                if (Coverage.Length > 0 && Coverage[i] == 0) continue;

                var na = math.normalizesafe(A[i].xyz, new float3(0, 0, 1));
                var nb = math.normalizesafe(B[i].xyz, new float3(0, 0, 1));
                var d = math.degrees(math.acos(math.clamp(math.dot(na, nb), -1f, 1f)));

                sum += d;
                count++;
                Histogram[histBase + math.clamp((int)(d * 5f), 0, 1023)]++;
            }

            SumPerRow[y] = sum;
            CountPerRow[y] = count;
        }
    }

    /// <summary>
    /// EN: Per channel linear RMSE, used for grayscale / mask textures.
    /// ZH: 逐通道线性 RMSE，用于灰度/蒙版贴图。
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast, CompileSynchronously = false)]
    public struct ATOChannelRmseJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float4> A;
        [ReadOnly] public NativeArray<float4> B;
        [ReadOnly] public NativeArray<byte> Coverage;
        public NativeArray<double4> SqErrPerRow;
        public NativeArray<int> CountPerRow;
        public int Width;

        public void Execute(int y)
        {
            var sq = double4.zero;
            var count = 0;

            for (var x = 0; x < Width; x++)
            {
                var i = y * Width + x;
                if (Coverage.Length > 0 && Coverage[i] == 0) continue;

                var d = A[i] - B[i];
                sq += new double4(d.x * d.x, d.y * d.y, d.z * d.z, d.w * d.w);
                count++;
            }

            SqErrPerRow[y] = sq;
            CountPerRow[y] = count;
        }
    }

    /// <summary>
    /// EN: Colour space helpers shared by the kernels (Burst friendly, no managed state).
    /// ZH: 内核共用的色彩空间辅助函数（Burst 友好，无托管状态）。
    /// </summary>
    [BurstCompile]
    public static class ATOColor
    {
        /// <summary>EN: Rec.709 relative luminance of a linear colour. ZH: 线性颜色的 Rec.709 相对亮度。</summary>
        public static float Luminance(float3 linearRgb) =>
            math.dot(linearRgb, new float3(0.2126f, 0.7152f, 0.0722f));

        /// <summary>EN: Linear sRGB (D65) to CIE Lab. ZH: 线性 sRGB (D65) 转 CIE Lab。</summary>
        public static float3 LinearToLab(float3 c)
        {
            var x = math.dot(c, new float3(0.4124564f, 0.3575761f, 0.1804375f));
            var y = math.dot(c, new float3(0.2126729f, 0.7151522f, 0.0721750f));
            var z = math.dot(c, new float3(0.0193339f, 0.1191920f, 0.9503041f));

            // D65 white point
            x /= 0.95047f;
            z /= 1.08883f;

            var fx = LabF(x);
            var fy = LabF(y);
            var fz = LabF(z);

            return new float3(116f * fy - 16f, 500f * (fx - fy), 200f * (fy - fz));
        }

        private static float LabF(float t)
        {
            const float d = 6f / 29f;
            return t > d * d * d ? math.pow(t, 1f / 3f) : t / (3f * d * d) + 4f / 29f;
        }

        /// <summary>
        /// EN: CIEDE2000 colour difference (Sharma, Wu &amp; Dalal 2005 formulation).
        /// ZH: CIEDE2000 色差（Sharma、Wu 与 Dalal 2005 年的公式）。
        /// </summary>
        public static float DeltaE2000(float3 lab1, float3 lab2)
        {
            const float kL = 1f, kC = 1f, kH = 1f;

            float l1 = lab1.x, a1 = lab1.y, b1 = lab1.z;
            float l2 = lab2.x, a2 = lab2.y, b2 = lab2.z;

            var c1 = math.sqrt(a1 * a1 + b1 * b1);
            var c2 = math.sqrt(a2 * a2 + b2 * b2);
            var cBar = (c1 + c2) * 0.5f;

            var cBar7 = math.pow(cBar, 7f);
            var g = 0.5f * (1f - math.sqrt(cBar7 / (cBar7 + 6103515625f))); // 25^7

            var a1p = (1f + g) * a1;
            var a2p = (1f + g) * a2;

            var c1p = math.sqrt(a1p * a1p + b1 * b1);
            var c2p = math.sqrt(a2p * a2p + b2 * b2);

            var h1p = HueAngle(b1, a1p);
            var h2p = HueAngle(b2, a2p);

            var dLp = l2 - l1;
            var dCp = c2p - c1p;

            float dhp;
            if (c1p * c2p < 1e-8f) dhp = 0f;
            else
            {
                dhp = h2p - h1p;
                if (dhp > 180f) dhp -= 360f;
                else if (dhp < -180f) dhp += 360f;
            }

            var dHp = 2f * math.sqrt(c1p * c2p) * math.sin(math.radians(dhp) * 0.5f);

            var lBarp = (l1 + l2) * 0.5f;
            var cBarp = (c1p + c2p) * 0.5f;

            float hBarp;
            if (c1p * c2p < 1e-8f) hBarp = h1p + h2p;
            else
            {
                var diff = math.abs(h1p - h2p);
                if (diff <= 180f) hBarp = (h1p + h2p) * 0.5f;
                else if (h1p + h2p < 360f) hBarp = (h1p + h2p + 360f) * 0.5f;
                else hBarp = (h1p + h2p - 360f) * 0.5f;
            }

            var t = 1f
                    - 0.17f * math.cos(math.radians(hBarp - 30f))
                    + 0.24f * math.cos(math.radians(2f * hBarp))
                    + 0.32f * math.cos(math.radians(3f * hBarp + 6f))
                    - 0.20f * math.cos(math.radians(4f * hBarp - 63f));

            var dTheta = 30f * math.exp(-((hBarp - 275f) / 25f) * ((hBarp - 275f) / 25f));
            var cBarp7 = math.pow(cBarp, 7f);
            var rC = 2f * math.sqrt(cBarp7 / (cBarp7 + 6103515625f));
            var rT = -rC * math.sin(math.radians(2f * dTheta));

            var lBarp50 = (lBarp - 50f) * (lBarp - 50f);
            var sL = 1f + 0.015f * lBarp50 / math.sqrt(20f + lBarp50);
            var sC = 1f + 0.045f * cBarp;
            var sH = 1f + 0.015f * cBarp * t;

            var termL = dLp / (kL * sL);
            var termC = dCp / (kC * sC);
            var termH = dHp / (kH * sH);

            return math.sqrt(termL * termL + termC * termC + termH * termH + rT * termC * termH);
        }

        private static float HueAngle(float b, float ap)
        {
            if (math.abs(b) < 1e-8f && math.abs(ap) < 1e-8f) return 0f;
            var h = math.degrees(math.atan2(b, ap));
            return h < 0f ? h + 360f : h;
        }
    }
}
