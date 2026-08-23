using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Burst-compiled quality metrics. All color math happens in linear space.
    /// MS-SSIM: Wang, Simoncelli &amp; Bovik 2004 (5 scales, 11×11 σ=1.6, standard weights).
    /// ΔE: CIEDE2000 (Sharma, Wu &amp; Dalal 2005 reference implementation).
    /// / Burst 质量度量作业。颜色计算全部在线性空间。MS-SSIM 与 CIEDE2000 均按论文标准实现。
    /// </summary>
    [BurstCompile]
    internal static class MetricJobs
    {
        // ============================================================ shared helpers / 共用工具
        internal static float SrgbToLinear(byte v)
        {
            float c = v / 255f;
            return c <= 0.04045f ? c / 12.92f : math.pow((c + 0.055f) / 1.055f, 2.4f);
        }

        internal static float LinearToSrgb(float c)
        {
            c = math.saturate(c);
            return c <= 0.0031308f ? c * 12.92f : 1.055f * math.pow(c, 1f / 2.4f) - 0.055f;
        }

        internal static float Luma(float3 linRgb) => math.dot(linRgb, new float3(0.2126f, 0.7152f, 0.0722f));

        private static readonly float[] Gaussian11 = InitGaussian();

        private static float[] InitGaussian()
        {
            // 11-tap gaussian, σ = 1.6 (MS-SSIM reference) / 11抽头高斯核
            var g = new float[11];
            float sum = 0f;
            for (int i = 0; i < 11; i++)
            {
                float d = i - 5;
                g[i] = math.exp(-(d * d) / (2f * 1.6f * 1.6f));
                sum += g[i];
            }
            for (int i = 0; i < 11; i++) g[i] /= sum;
            return g;
        }

        private static NativeArray<float> GaussianNative()
        {
            var a = new NativeArray<float>(11, Allocator.TempJob);
            for (int i = 0; i < 11; i++) a[i] = Gaussian11[i];
            return a;
        }

        // ============================================================ MS-SSIM
        /// <summary>
        /// MS-SSIM of two pixel buffers compared on linear luma. x is raw source bytes (sRGB or
        /// linear by flag); y is the reconstructed buffer. Returns NaN when the short side is &lt;11
        /// (metric skipped). / 线性亮度 MS-SSIM；短边小于11返回NaN（跳过该指标）。
        /// </summary>
        internal static float Msssim(Color32[] xBytes, Color32[] yBytes, int w, int h, bool xSrgb, bool ySrgbBytes)
        {
            int n = w * h;
            if (w < 11 || h < 11) return float.NaN;

            var owned = new List<IDisposable>();
            try
            {
                var lx = new NativeArray<float>(n, Allocator.TempJob);
                var ly = new NativeArray<float>(n, Allocator.TempJob);
                owned.Add(lx); owned.Add(ly);
                var nx = new NativeArray<Color32>(xBytes, Allocator.TempJob);
                var ny = new NativeArray<Color32>(yBytes, Allocator.TempJob);
                owned.Add(nx); owned.Add(ny);
                new LumaJob { X = nx, Y = ny, LX = lx, LY = ly, XSrgb = xSrgb, YSrgbBytes = ySrgbBytes }
                    .Schedule(n, 8192).Complete();

                var weights = new[] { 0.0448f, 0.2856f, 0.3001f, 0.2363f, 0.1333f };
                var curX = lx; var curY = ly;
                int cw = w, ch = h;
                float total = 0f, wsum = 0f;

                for (int s = 0; s < weights.Length; s++)
                {
                    if (cw < 11 || ch < 11) break;
                    float m = SingleScaleSsim(curX, curY, cw, ch, owned);
                    if (float.IsNaN(m)) break;
                    total += weights[s] * m;
                    wsum += weights[s];
                    if (s == weights.Length - 1 || cw / 2 < 11 || ch / 2 < 11) break;

                    int nw = cw / 2, nh = ch / 2;
                    var dx = new NativeArray<float>(nw * nh, Allocator.TempJob);
                    var dy = new NativeArray<float>(nw * nh, Allocator.TempJob);
                    owned.Add(dx); owned.Add(dy);
                    new Downsample2xJob { Src = curX, Dst = dx, W = cw, H = ch }.Schedule(nw * nh, 4096).Complete();
                    new Downsample2xJob { Src = curY, Dst = dy, W = cw, H = ch }.Schedule(nw * nh, 4096).Complete();
                    curX = dx; curY = dy; cw = nw; ch = nh;
                }

                return wsum > 0f ? total / wsum : float.NaN;
            }
            finally
            {
                foreach (var d in owned) d.Dispose();
            }
        }

        private static float SingleScaleSsim(NativeArray<float> x, NativeArray<float> y, int w, int h,
            List<IDisposable> owned)
        {
            const float C1 = 0.01f * 0.01f, C2 = 0.03f * 0.03f;
            int ow = w - 10, oh = h - 10;
            if (ow < 1 || oh < 1) return float.NaN;

            var g = GaussianNative();
            owned.Add(g);

            // signals to blur: x, y, x², y², xy / 需要模糊的信号
            var srcXX = new NativeArray<float>(w * h, Allocator.TempJob);
            var srcYY = new NativeArray<float>(w * h, Allocator.TempJob);
            var srcXY = new NativeArray<float>(w * h, Allocator.TempJob);
            owned.Add(srcXX); owned.Add(srcYY); owned.Add(srcXY);
            new SquareJob { X = x, Y = y, XX = srcXX, YY = srcYY, XY = srcXY }.Schedule(w * h, 8192).Complete();

            var bX = BlurValid(x, w, h, g, owned);
            var bY = BlurValid(y, w, h, g, owned);
            var bXX = BlurValid(srcXX, w, h, g, owned);
            var bYY = BlurValid(srcYY, w, h, g, owned);
            var bXY = BlurValid(srcXY, w, h, g, owned);

            var sum = new NativeArray<double>(1, Allocator.TempJob);
            owned.Add(sum);
            new SsimMeanJob
            {
                MX = bX, MY = bY, MXX = bXX, MYY = bYY, MXY = bXY,
                W = ow, H = oh, C1 = C1, C2 = C2, Sum = sum,
            }.Schedule().Complete();

            return (float)(sum[0] / (ow * oh));
        }

        private static NativeArray<float> BlurValid(NativeArray<float> src, int w, int h,
            NativeArray<float> gaussian, List<IDisposable> owned)
        {
            int ow = w - 10;
            var rows = new NativeArray<float>(ow * h, Allocator.TempJob);
            owned.Add(rows);
            new BlurXJob { Src = src, Dst = rows, W = w, H = h, G = gaussian }
                .Schedule(h, 1).Complete();

            int oh = h - 10;
            var dst = new NativeArray<float>(ow * oh, Allocator.TempJob);
            owned.Add(dst);
            new BlurYJob { Src = rows, Dst = dst, W = ow, H = h, G = gaussian }
                .Schedule(ow, 1).Complete();
            return dst;
        }

        // ============================================================ ΔE CIEDE2000 (mean)
        /// <summary>
        /// Mean CIEDE2000 over (stride-sampled) pixels. x: raw source bytes; yLinear: reconstructed
        /// linear premultiplied (unpremultiplied inside). / 采样的 CIEDE2000 平均色差。
        /// </summary>
        internal static float DeltaE2000Mean(Color32[] xBytes, bool xSrgb, Color32[] yLinear, bool yHasAlpha)
        {
            int count = xBytes.Length;
            int stride = Math.Max(1, (int)Math.Ceiling(count / 16384.0));
            int samples = 0;

            using var sx = new NativeArray<Color32>(xBytes, Allocator.TempJob);
            using var sy = new NativeArray<Color32>(yLinear, Allocator.TempJob);
            using var acc = new NativeArray<double>(1, Allocator.TempJob);
            new DeltaEJob { X = sx, Y = sy, XSrgb = xSrgb, YHasAlpha = yHasAlpha, Stride = stride, N = count, Acc = acc }
                .Schedule().Complete();

            samples = (count + stride - 1) / stride;
            return (float)(acc[0] / Math.Max(1, samples));
        }

        // ============================================================ alpha metrics
        /// <summary>
        /// Compute alpha metrics: cutout silhouette IoU at `cutoff` (out[0]) and linear alpha RMSE
        /// (out[1]). / alpha 指标：Cutout 轮廓 IoU 与线性 RMSE。
        /// </summary>
        internal static void AlphaMetrics(Color32[] xBytes, Color32[] yBytes, float cutoff,
            out float iou, out float rmse)
        {
            using var sx = new NativeArray<Color32>(xBytes, Allocator.TempJob);
            using var sy = new NativeArray<Color32>(yBytes, Allocator.TempJob);
            using var acc = new NativeArray<double>(4, Allocator.TempJob);
            new AlphaJob { X = sx, Y = sy, Cutoff = cutoff, Acc = acc }.Schedule().Complete();
            double inter = acc[0], union = acc[1], sq = acc[2], cnt = acc[3];
            iou = union > 0 ? (float)(inter / union) : 1f;
            rmse = cnt > 0 ? (float)Math.Sqrt(sq / cnt) : 0f;
        }

        // ============================================================ normal map metrics
        /// <summary>Angular error stats of decoded normals (RGorAG unpack). out: mean, p95. / 法线角度误差：均值与 p95。</summary>
        internal static void NormalAngleStats(Color32[] xBytes, Color32[] yBytes,
            out float meanDeg, out float p95Deg)
        {
            using var sx = new NativeArray<Color32>(xBytes, Allocator.TempJob);
            using var sy = new NativeArray<Color32>(yBytes, Allocator.TempJob);
            using var outArr = new NativeArray<float>(2, Allocator.TempJob);
            new NormalAngleJob { X = sx, Y = sy, Out = outArr }.Schedule().Complete();
            meanDeg = outArr[0];
            p95Deg = outArr[1];
        }

        // ============================================================ grayscale RMSE
        /// <summary>Per-channel linear RMSE; caller takes the worst used channel. / 逐通道线性RMSE，由调用方取使用通道最差值。</summary>
        internal static void GrayChannelRmse(Color32[] xBytes, Color32[] yBytes, float[] outRmse)
        {
            using var sx = new NativeArray<Color32>(xBytes, Allocator.TempJob);
            using var sy = new NativeArray<Color32>(yBytes, Allocator.TempJob);
            using var acc = new NativeArray<double>(4, Allocator.TempJob);
            new GrayRmseJob { X = sx, Y = sy, Acc = acc }.Schedule().Complete();
            for (int i = 0; i < 4; i++) outRmse[i] = (float)Math.Sqrt(acc[i] / Math.Max(1, xBytes.Length));
        }

        // ============================================================ pure color test
        /// <summary>True when every pixel is identical (short-circuit candidate). / 纯色检测（短路缩放候选）。</summary>
        internal static bool IsPureColor(Color32[] bytes)
        {
            if (bytes.Length <= 1) return true;
            using var sx = new NativeArray<Color32>(bytes, Allocator.TempJob);
            using var res = new NativeArray<int>(1, Allocator.TempJob);
            new PureColorJob { X = sx, Res = res }.Schedule().Complete();
            return res[0] != 0;
        }

        // ============================================================ jobs
        [BurstCompile]
        private struct LumaJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Color32> X;
            [ReadOnly] public NativeArray<Color32> Y;
            public NativeArray<float> LX;
            public NativeArray<float> LY;
            public bool XSrgb;
            public bool YSrgbBytes; // true: y holds raw sRGB bytes; false: y holds linear values / y是否为原始sRGB字节

            public void Execute(int i)
            {
                var cx = X[i];
                var cy = Y[i];
                float3 a = XSrgb
                    ? new float3(SrgbToLinear(cx.r), SrgbToLinear(cx.g), SrgbToLinear(cx.b))
                    : new float3(cx.r, cx.g, cx.b) / 255f;
                float3 b = YSrgbBytes
                    ? new float3(SrgbToLinear(cy.r), SrgbToLinear(cy.g), SrgbToLinear(cy.b))
                    : new float3(cy.r, cy.g, cy.b) / 255f;
                LX[i] = Luma(a);
                LY[i] = Luma(b);
            }
        }

        [BurstCompile]
        private struct SquareJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> X;
            [ReadOnly] public NativeArray<float> Y;
            public NativeArray<float> XX;
            public NativeArray<float> YY;
            public NativeArray<float> XY;

            public void Execute(int i)
            {
                XX[i] = X[i] * X[i];
                YY[i] = Y[i] * Y[i];
                XY[i] = X[i] * Y[i];
            }
        }

        [BurstCompile]
        private struct BlurXJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> Src;
            [ReadOnly] public NativeArray<float> G;
            [WriteOnly] public NativeArray<float> Dst;
            public int W;
            public int H;

            public void Execute(int row)
            {
                int ow = W - 10;
                for (int ox = 0; ox < ow; ox++)
                {
                    float s = 0f;
                    for (int k = 0; k < 11; k++) s += Src[row * W + ox + k] * G[k];
                    Dst[row * ow + ox] = s;
                }
            }
        }

        [BurstCompile]
        private struct BlurYJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> Src;
            [ReadOnly] public NativeArray<float> G;
            [WriteOnly] public NativeArray<float> Dst;
            public int W;   // width of Src and Dst (already reduced) / 已缩减后的宽度
            public int H;   // height of Src / Src 高度

            public void Execute(int col)
            {
                int oh = H - 10;
                for (int oy = 0; oy < oh; oy++)
                {
                    float s = 0f;
                    for (int k = 0; k < 11; k++) s += Src[(oy + k) * W + col] * G[k];
                    Dst[oy * W + col] = s;
                }
            }
        }

        [BurstCompile]
        private struct Downsample2xJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> Src;
            [WriteOnly] public NativeArray<float> Dst;
            public int W;
            public int H;

            public void Execute(int i)
            {
                int nw = W / 2;
                int x = i % nw, y = i / nw;
                int sx = x * 2, sy = y * 2;
                int x1 = math.min(sx + 1, W - 1), y1 = math.min(sy + 1, H - 1);
                Dst[i] = 0.25f * (Src[sy * W + sx] + Src[sy * W + x1] + Src[y1 * W + sx] + Src[y1 * W + x1]);
            }
        }

        [BurstCompile]
        private struct SsimMeanJob : IJob
        {
            [ReadOnly] public NativeArray<float> MX;
            [ReadOnly] public NativeArray<float> MY;
            [ReadOnly] public NativeArray<float> MXX;
            [ReadOnly] public NativeArray<float> MYY;
            [ReadOnly] public NativeArray<float> MXY;
            public int W;
            public int H;
            public float C1;
            public float C2;
            public NativeArray<double> Sum;

            public void Execute()
            {
                double sum = 0;
                int n = W * H;
                for (int i = 0; i < n; i++)
                {
                    float mx = MX[i], my = MY[i];
                    float vx = MXX[i] - mx * mx;
                    float vy = MYY[i] - my * my;
                    float cxy = MXY[i] - mx * my;
                    float a = (2 * mx * my + C1) * (2 * cxy + C2);
                    float b = (mx * mx + my * my + C1) * (vx + vy + C2);
                    sum += b > 0 ? a / b : 1.0;
                }
                Sum[0] = sum;
            }
        }

        [BurstCompile]
        private struct DeltaEJob : IJob
        {
            [ReadOnly] public NativeArray<Color32> X;
            [ReadOnly] public NativeArray<Color32> Y;
            public bool XSrgb;
            public bool YHasAlpha; // y premultiplied? / y 是否预乘
            public int Stride;
            public int N;
            public NativeArray<double> Acc;

            public void Execute()
            {
                double sum = 0;
                long cnt = 0;
                for (int i = 0; i < N; i += Stride)
                {
                    var cx = X[i];
                    var cy = Y[i];
                    float3 a = XSrgb
                        ? new float3(SrgbToLinear(cx.r), SrgbToLinear(cx.g), SrgbToLinear(cx.b))
                        : new float3(cx.r, cx.g, cx.b) / 255f;
                    float3 b = new float3(cy.r, cy.g, cy.b) / 255f;
                    if (YHasAlpha && cy.a > 0)
                    {
                        float inv = 255f / cy.a;
                        b *= inv;
                    }

                    sum += Ciede2000(RgbToLab(a), RgbToLab(math.saturate(b)));
                    cnt++;
                }
                Acc[0] = sum;
            }
        }

        [BurstCompile]
        private struct AlphaJob : IJob
        {
            [ReadOnly] public NativeArray<Color32> X;
            [ReadOnly] public NativeArray<Color32> Y;
            public float Cutoff;
            public NativeArray<double> Acc; // inter, union, sumSq, count

            public void Execute()
            {
                double inter = 0, union = 0, sq = 0;
                for (int i = 0; i < X.Length; i++)
                {
                    float ao = X[i].a / 255f, at = Y[i].a / 255f;
                    bool o = ao >= Cutoff, t = at >= Cutoff;
                    if (o && t) inter++;
                    if (o || t) union++;
                    float d = ao - at;
                    sq += d * d;
                }
                Acc[0] = inter; Acc[1] = union; Acc[2] = sq; Acc[3] = X.Length;
            }
        }

        [BurstCompile]
        private struct NormalAngleJob : IJob
        {
            [ReadOnly] public NativeArray<Color32> X;
            [ReadOnly] public NativeArray<Color32> Y;
            public NativeArray<float> Out; // mean, p95

            public void Execute()
            {
                // histogram of 0.05° bins over [0,180°] / 直方图 0.05° 一格
                var hist = new NativeArray<int>(3601, Allocator.Temp);
                double sum = 0;
                long cnt = 0;
                for (int i = 0; i < X.Length; i++)
                {
                    var nx = DecodeNormal(X[i]);
                    var ny = DecodeNormal(Y[i]);
                    float l1 = math.length(nx), l2 = math.length(ny);
                    if (l1 < 1e-4f || l2 < 1e-4f) continue;
                    float d = math.clamp(math.dot(nx / l1, ny / l2), -1f, 1f);
                    float deg = math.degrees(math.acos(d));
                    sum += deg;
                    int bin = math.clamp((int)(deg / 0.05f), 0, 3600);
                    hist[bin]++;
                    cnt++;
                }

                if (cnt == 0)
                {
                    Out[0] = 0f; Out[1] = 0f;
                    hist.Dispose();
                    return;
                }

                double mean = sum / cnt;
                long target = (long)(cnt * 0.95);
                long acc = 0;
                float p95 = 180f;
                for (int b = 0; b <= 3600; b++)
                {
                    acc += hist[b];
                    if (acc >= target) { p95 = (b + 1) * 0.05f; break; }
                }
                Out[0] = (float)mean;
                Out[1] = p95;
                hist.Dispose();
            }

            private static float3 DecodeNormal(Color32 c)
            {
                // Unity UnpackNormalmapRGorAG semantics. / 与 Unity 解包逻辑一致
                float x = (c.r / 255f) * (c.a / 255f) * 2f - 1f;
                float y = (c.g / 255f) * 2f - 1f;
                float z = math.sqrt(math.saturate(1f - x * x - y * y));
                return new float3(x, y, z);
            }
        }

        [BurstCompile]
        private struct GrayRmseJob : IJob
        {
            [ReadOnly] public NativeArray<Color32> X;
            [ReadOnly] public NativeArray<Color32> Y;
            public NativeArray<double> Acc;

            public void Execute()
            {
                double sr = 0, sg = 0, sb = 0, sa = 0;
                for (int i = 0; i < X.Length; i++)
                {
                    float dr = X[i].r / 255f - Y[i].r / 255f;
                    float dg = X[i].g / 255f - Y[i].g / 255f;
                    float db = X[i].b / 255f - Y[i].b / 255f;
                    float da = X[i].a / 255f - Y[i].a / 255f;
                    sr += dr * dr; sg += dg * dg; sb += db * db; sa += da * da;
                }
                Acc[0] = sr; Acc[1] = sg; Acc[2] = sb; Acc[3] = sa;
            }
        }

        [BurstCompile]
        private struct PureColorJob : IJob
        {
            [ReadOnly] public NativeArray<Color32> X;
            public NativeArray<int> Res;

            public void Execute()
            {
                var first = X[0];
                for (int i = 1; i < X.Length; i++)
                {
                    var c = X[i];
                    if (c.r != first.r || c.g != first.g || c.b != first.b || c.a != first.a)
                    {
                        Res[0] = 0;
                        return;
                    }
                }
                Res[0] = 1;
            }
        }

        // ============================================================ color science
        private static float3 RgbToLab(float3 rgb)
        {
            // sRGB (linear) → XYZ (D65) → Lab / 线性RGB转Lab
            float X = math.dot(rgb, new float3(0.4124564f, 0.3575761f, 0.1804375f));
            float Y = math.dot(rgb, new float3(0.2126729f, 0.7151522f, 0.0721750f));
            float Z = math.dot(rgb, new float3(0.0193339f, 0.1191920f, 0.9503041f));
            const float xn = 0.95047f, yn = 1.0f, zn = 1.08883f;
            float fx = Pivot(X / xn), fy = Pivot(Y / yn), fz = Pivot(Z / zn);
            return new float3(116f * fy - 16f, 500f * (fx - fy), 200f * (fy - fz));
        }

        private static float Pivot(float t) =>
            t > 0.008856f ? math.pow(t, 1f / 3f) : (7.787f * t + 16f / 116f);

        /// <summary>CIEDE2000 (Sharma 2005 reference). / CIEDE2000 标准实现。</summary>
        private static float Ciede2000(float3 lab1, float3 lab2)
        {
            float L1 = lab1.x, a1 = lab1.y, b1 = lab1.z;
            float L2 = lab2.x, a2 = lab2.y, b2 = lab2.z;

            float C1 = math.sqrt(a1 * a1 + b1 * b1);
            float C2 = math.sqrt(a2 * a2 + b2 * b2);
            float Cb = 0.5f * (C1 + C2);
            float Cb7 = math.pow(Cb, 7f);
            float G = 0.5f * (1f - math.sqrt(Cb7 / (Cb7 + 6103515625f))); // 25^7
            float ap1 = (1f + G) * a1, ap2 = (1f + G) * a2;
            float Cp1 = math.sqrt(ap1 * ap1 + b1 * b1);
            float Cp2 = math.sqrt(ap2 * ap2 + b2 * b2);
            float hp1 = (ap1 == 0f && b1 == 0f) ? 0f : math.degrees(math.atan2(b1, ap1));
            if (hp1 < 0) hp1 += 360f;
            float hp2 = (ap2 == 0f && b2 == 0f) ? 0f : math.degrees(math.atan2(b2, ap2));
            if (hp2 < 0) hp2 += 360f;

            float dL = L2 - L1;
            float dC = Cp2 - Cp1;
            float dhp;
            if (Cp1 * Cp2 == 0f) dhp = 0f;
            else
            {
                dhp = hp2 - hp1;
                if (dhp > 180f) dhp -= 360f;
                else if (dhp < -180f) dhp += 360f;
            }
            float dH = 2f * math.sqrt(Cp1 * Cp2) * math.sin(math.radians(dhp) * 0.5f);

            float Lbp = 0.5f * (L1 + L2);
            float Cbp = 0.5f * (Cp1 + Cp2);
            float hbp;
            if (Cp1 * Cp2 == 0f) hbp = hp1 + hp2;
            else
            {
                float diff = math.abs(hp1 - hp2);
                if (diff <= 180f) hbp = 0.5f * (hp1 + hp2);
                else if (hp1 + hp2 < 360f) hbp = 0.5f * (hp1 + hp2 + 360f);
                else hbp = 0.5f * (hp1 + hp2 - 360f);
            }

            float T = 1f - 0.17f * math.cos(math.radians(hbp - 30f))
                       + 0.24f * math.cos(math.radians(2f * hbp))
                       + 0.32f * math.cos(math.radians(3f * hbp + 6f))
                       - 0.20f * math.cos(math.radians(4f * hbp - 63f));
            float dTheta = 30f * math.exp(-math.pow((hbp - 275f) / 25f, 2f));
            float Cbp7 = math.pow(Cbp, 7f);
            float Rc = 2f * math.sqrt(Cbp7 / (Cbp7 + 6103515625f));
            float Lm50sq = (Lbp - 50f) * (Lbp - 50f);
            float Sl = 1f + 0.015f * Lm50sq / math.sqrt(20f + Lm50sq);
            float Sc = 1f + 0.045f * Cbp;
            float Sh = 1f + 0.015f * Cbp * T;
            float Rt = -math.sin(math.radians(2f * dTheta)) * Rc;

            float tl = dL / Sl, tc = dC / Sc, th = dH / Sh;
            return math.sqrt(tl * tl + tc * tc + th * th + Rt * tc * th);
        }
    }
}
