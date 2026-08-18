// Avatar Texture Optimizer (ATO)
// Pure color math used by the target-quality algorithm:
//   sRGB<->linear, premultiplied alpha, CIEDE2000, SSIM / MS-SSIM, RMSE,
//   normal-map decode/re-encode, angular error, p95.
// 目标质量算法使用的纯颜色数学：
//   sRGB<->线性、预乘 alpha、CIEDE2000、SSIM/MS-SSIM、RMSE、法线解码/重编码、角度误差、p95。
//
// All hot-path functions operate on float[] (Burst-friendly) rather than Color objects.
// 热路径函数均操作 float[]（利于 Burst），而非 Color 对象。

using System;

namespace NetFosa.ATO
{
    public static class ATOColorMath
    {
        // ---------------- color space / 色彩空间 ----------------

        public static float SrgbToLinear(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);

        public static float LinearToSrgb(float v) => v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f;

        /// <summary>Rec.709 luma of linear RGB. / 线性 RGB 的 Rec.709 亮度。</summary>
        public static float Luma(float r, float g, float b) => 0.2126f * r + 0.7152f * g + 0.0722f * b;

        // ---------------- CIEDE2000 / ΔE ----------------

        private static readonly float[] D65 = { 0.95047f, 1f, 1.08883f };

        public static void RgbToLab(float r, float g, float b, out float L, out float a, out float bb)
        {
            // linear RGB -> XYZ (sRGB D65) / 线性 RGB -> XYZ
            float x = 0.4124564f * r + 0.3575761f * g + 0.1804375f * b;
            float y = 0.2126729f * r + 0.7151522f * g + 0.0721750f * b;
            float z = 0.0193339f * r + 0.1191920f * g + 0.9503041f * b;
            x /= D65[0]; y /= D65[1]; z /= D65[2];
            x = LabF(x); y = LabF(y); z = LabF(z);
            L = 116f * y - 16f;
            a = 500f * (x - y);
            bb = 200f * (y - z);
        }

        private static float LabF(float t) => t > 0.008856f ? MathF.Pow(t, 1f / 3f) : (7.787f * t + 16f / 116f);

        /// <summary>CIEDE2000 color difference between two linear RGB colors. / 两个线性 RGB 颜色间的 CIEDE2000 色差。</summary>
        public static float Ciede2000(float r1, float g1, float b1, float r2, float g2, float b2)
        {
            RgbToLab(r1, g1, b1, out float L1, out float a1, out float b1l);
            RgbToLab(r2, g2, b2, out float L2, out float a2, out float b2l);

            float C1 = MathF.Sqrt(a1 * a1 + b1l * b1l);
            float C2 = MathF.Sqrt(a2 * a2 + b2l * b2l);
            float Cbar = (C1 + C2) * 0.5f;
            float Cbar7 = MathF.Pow(Cbar, 7f);
            float G = 0.5f * (1f - MathF.Sqrt(Cbar7 / (Cbar7 + MathF.Pow(25f, 7f))));
            float a1p = (1f + G) * a1, a2p = (1f + G) * a2;
            float C1p = MathF.Sqrt(a1p * a1p + b1l * b1l);
            float C2p = MathF.Sqrt(a2p * a2p + b2l * b2l);
            float h1p = HueDeg(a1p, b1l), h2p = HueDeg(a2p, b2l);

            float dLp = L2 - L1;
            float dCp = C2p - C1p;
            float dhp;
            float dhabs = MathF.Abs(h1p - h2p);
            if (C1p * C2p == 0f) dhp = 0f;
            else if (dhabs <= 180f) dhp = h2p - h1p;
            else if (h2p <= h1p) dhp = h2p - h1p + 360f;
            else dhp = h2p - h1p - 360f;
            float dHp = 2f * MathF.Sqrt(C1p * C2p) * MathF.Sin(DegToRad(dhp) * 0.5f);

            float Lbar = (L1 + L2) * 0.5f;
            float Cpbar = (C1p + C2p) * 0.5f;
            float hpbar;
            if (C1p * C2p == 0f) hpbar = h1p + h2p;
            else if (MathF.Abs(h1p - h2p) <= 180f) hpbar = (h1p + h2p) * 0.5f;
            else if (h1p + h2p < 360f) hpbar = (h1p + h2p + 360f) * 0.5f;
            else hpbar = (h1p + h2p - 360f) * 0.5f;

            float T = 1f - 0.17f * MathF.Cos(DegToRad(hpbar - 30f)) + 0.24f * MathF.Cos(DegToRad(2f * hpbar))
                      + 0.32f * MathF.Cos(DegToRad(3f * hpbar + 6f)) - 0.20f * MathF.Cos(DegToRad(4f * hpbar - 63f));
            float dTheta = 30f * MathF.Exp(-MathF.Pow((hpbar - 275f) / 25f, 2f));
            float Cpbar7 = MathF.Pow(Cpbar, 7f);
            float Rc = 2f * MathF.Sqrt(Cpbar7 / (Cpbar7 + MathF.Pow(25f, 7f)));
            float Sl = 1f + 0.015f * MathF.Pow(Lbar - 50f, 2f) / MathF.Sqrt(20f + MathF.Pow(Lbar - 50f, 2f));
            float Sc = 1f + 0.045f * Cpbar;
            float Sh = 1f + 0.015f * Cpbar * T;
            float Rt = -MathF.Sin(DegToRad(2f * dTheta)) * Rc;

            float dl = dLp / Sl, dc = dCp / Sc, dh = dHp / Sh;
            return MathF.Sqrt(dl * dl + dc * dc + dh * dh + Rt * dc * dh);
        }

        private static float HueDeg(float a, float b)
        {
            if (a == 0f && b == 0f) return 0f;
            float h = RadToDeg(MathF.Atan2(b, a));
            return h < 0f ? h + 360f : h;
        }

        private static float DegToRad(float d) => d * (MathF.PI / 180f);
        private static float RadToDeg(float r) => r * (180f / MathF.PI);

        // ---------------- SSIM / MS-SSIM ----------------

        /// <summary>
        /// Precomputed 11x11 Gaussian (sigma=1.5) kernel, unnormalized sum ~1.
        /// 预计算的 11x11 高斯核（σ=1.5）。
        /// </summary>
        public static readonly float[] Gauss11 = BuildGauss11();
        public const int GaussRadius = 5;

        private static float[] BuildGauss11()
        {
            var k = new float[11];
            float sum = 0f;
            for (int i = 0; i < 11; i++)
            {
                float x = i - 5;
                k[i] = MathF.Exp(-(x * x) / (2f * 1.5f * 1.5f));
                sum += k[i];
            }
            for (int i = 0; i < 11; i++) k[i] /= sum;
            return k;
        }

        /// <summary>
        /// Separable 2D convolution of a single-channel buffer.
        /// 单通道缓冲的可分离二维卷积。
        /// </summary>
        public static void GaussBlur(float[] src, float[] dst, int w, int h)
        {
            // Burst fast path with managed fallback. / Burst 快速路径 + 托管兜底。
            if (ATOBurst.TryGaussBlur(src, w, h, dst)) return;
            var tmp = new float[w * h];
            // horizontal / 水平
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    float acc = 0f;
                    for (int k = -GaussRadius; k <= GaussRadius; k++)
                    {
                        int xx = x + k;
                        if (xx < 0) xx = 0; else if (xx >= w) xx = w - 1;
                        acc += src[row + xx] * Gauss11[k + GaussRadius];
                    }
                    tmp[row + x] = acc;
                }
            }
            // vertical / 垂直
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    float acc = 0f;
                    for (int k = -GaussRadius; k <= GaussRadius; k++)
                    {
                        int yy = y + k;
                        if (yy < 0) yy = 0; else if (yy >= h) yy = h - 1;
                        acc += tmp[yy * w + x] * Gauss11[k + GaussRadius];
                    }
                    dst[row + x] = acc;
                }
            }
        }

        /// <summary>
        /// Mean SSIM between two luma buffers over a validity mask.
        /// 两个亮度缓冲在有效掩码上的平均 SSIM。
        /// </summary>
        public static float Ssim(float[] x, float[] y, byte[] mask, int w, int h)
        {
            int n = w * h;
            var mx = new float[n]; var my = new float[n];
            var mxx = new float[n]; var myy = new float[n]; var mxy = new float[n];
            var x2 = new float[n]; var y2 = new float[n]; var xy = new float[n];
            for (int i = 0; i < n; i++) { x2[i] = x[i] * x[i]; y2[i] = y[i] * y[i]; xy[i] = x[i] * y[i]; }
            GaussBlur(x, mx, w, h); GaussBlur(y, my, w, h);
            GaussBlur(x2, mxx, w, h); GaussBlur(y2, myy, w, h); GaussBlur(xy, mxy, w, h);

            const float C1 = 0.0001f, C2 = 0.0009f; // L=1 / 归一化范围 [0,1]
            float sum = 0f; int count = 0;
            for (int i = 0; i < n; i++)
            {
                if (mask != null && mask[i] == 0) continue;
                float ux = mx[i], uy = my[i];
                float vx = mxx[i] - ux * ux, vy = myy[i] - uy * uy;
                float vxy = mxy[i] - ux * uy;
                float num = (2f * ux * uy + C1) * (2f * vxy + C2);
                float den = (ux * ux + uy * uy + C1) * (vx + vy + C2);
                sum += den > 0f ? num / den : 0f;
                count++;
            }
            return count > 0 ? sum / count : 0f;
        }

        /// <summary>
        /// Downsample by 2 with 2x2 box averaging. / 2x2 盒平均下采样（降 2 倍）。
        /// </summary>
        public static void Downsample2(float[] src, int w, int h, out float[] dst, out int w2, out int h2)
        {
            w2 = Math.Max(1, w / 2); h2 = Math.Max(1, h / 2);
            dst = new float[w2 * h2];
            for (int y = 0; y < h2; y++)
                for (int x = 0; x < w2; x++)
                {
                    int x0 = Math.Min(x * 2, w - 1), x1 = Math.Min(x * 2 + 1, w - 1);
                    int y0 = Math.Min(y * 2, h - 1), y1 = Math.Min(y * 2 + 1, h - 1);
                    dst[y * w2 + x] = (src[y0 * w + x0] + src[y1 * w + x0] + src[y0 * w + x1] + src[y1 * w + x1]) * 0.25f;
                }
        }

        /// <summary>
        /// MS-SSIM (5 scales, standard weights). Small buffers (<16px) degrade gracefully.
        /// MS-SSIM（5 尺度，标准权重）。极小缓冲（&lt;16px）自然退化为单尺度。
        /// </summary>
        public static float MsSsim(float[] x, float[] y, byte[] mask, int w, int h)
        {
            float[] a = x, b = y; byte[] m = mask;
            int wa = w, ha = h;
            float[] weights = { 0.0448f, 0.2856f, 0.3001f, 0.2363f, 0.1333f };
            float csTotal = 0f;
            float lastL = 1f;

            for (int scale = 0; scale < 5; scale++)
            {
                if (wa < 3 || ha < 3)
                {
                    // Too small for further decomposition: use luminance only. / 过小无法继续分解：仅用亮度。
                    lastL = Ssim(a, b, m, wa, ha);
                    break;
                }
                float cs = ContrastStructure(a, b, m, wa, ha);
                if (scale == 4)
                {
                    lastL = LuminanceTerm(a, b, m, wa, ha);
                    csTotal += weights[scale] * MathF.Log(Math.Max(cs, 1e-6f));
                }
                else
                {
                    csTotal += weights[scale] * MathF.Log(Math.Max(cs, 1e-6f));
                    Downsample2(a, wa, ha, out var a2, out int wa2, out int ha2);
                    Downsample2(b, wa, ha, out var b2, out _, out _);
                    a = a2; b = b2; wa = wa2; ha = ha2;
                    if (m != null)
                    {
                        Downsample2Mask(m, w, h, out m, out _, out _);
                    }
                }
            }
            return MathF.Exp(csTotal) * lastL;
        }

        private static void Downsample2Mask(byte[] src, int w, int h, out byte[] dst, out int w2, out int h2)
        {
            w2 = Math.Max(1, w / 2); h2 = Math.Max(1, h / 2);
            dst = new byte[w2 * h2];
            for (int y = 0; y < h2; y++)
                for (int x = 0; x < w2; x++)
                {
                    int x0 = Math.Min(x * 2, w - 1), x1 = Math.Min(x * 2 + 1, w - 1);
                    int y0 = Math.Min(y * 2, h - 1), y1 = Math.Min(y * 2 + 1, h - 1);
                    byte v = (byte)Math.Min(1, src[y0 * w + x0] + src[y1 * w + x0] + src[y0 * w + x1] + src[y1 * w + x1]);
                    dst[y * w2 + x] = v;
                }
        }

        /// <summary>Contrast-structure term = (2σxy + C2)/(σx² + σy² + C2). / 对比度-结构项。</summary>
        private static float ContrastStructure(float[] x, float[] y, byte[] mask, int w, int h)
        {
            int n = w * h;
            var mx = new float[n]; var my = new float[n];
            var mxx = new float[n]; var myy = new float[n]; var mxy = new float[n];
            var x2 = new float[n]; var y2 = new float[n]; var xy = new float[n];
            for (int i = 0; i < n; i++) { x2[i] = x[i] * x[i]; y2[i] = y[i] * y[i]; xy[i] = x[i] * y[i]; }
            GaussBlur(x, mx, w, h); GaussBlur(y, my, w, h);
            GaussBlur(x2, mxx, w, h); GaussBlur(y2, myy, w, h); GaussBlur(xy, mxy, w, h);
            const float C2 = 0.0009f;
            float sum = 0f; int count = 0;
            for (int i = 0; i < n; i++)
            {
                if (mask != null && mask[i] == 0) continue;
                float vx = mxx[i] - mx[i] * mx[i];
                float vy = myy[i] - my[i] * my[i];
                float vxy = mxy[i] - mx[i] * my[i];
                float den = vx + vy + C2;
                sum += den > 0f ? (2f * vxy + C2) / den : 0f;
                count++;
            }
            return count > 0 ? sum / count : 0f;
        }

        private static float LuminanceTerm(float[] x, float[] y, byte[] mask, int w, int h)
        {
            int n = w * h;
            float ux = 0f, uy = 0f; int count = 0;
            for (int i = 0; i < n; i++)
            {
                if (mask != null && mask[i] == 0) continue;
                ux += x[i]; uy += y[i]; count++;
            }
            if (count == 0) return 0f;
            ux /= count; uy /= count;
            const float C1 = 0.0001f;
            return (2f * ux * uy + C1) / (ux * ux + uy * uy + C1);
        }

        // ---------------- RMSE / p95 / IoU ----------------

        public static float Rmse(float[] a, float[] b, byte[] mask)
        {
            if (ATOBurst.TryRmse(a, b, mask, out var burstResult)) return burstResult;
            float sum = 0f; int count = 0;
            for (int i = 0; i < a.Length; i++)
            {
                if (mask != null && mask[i] == 0) continue;
                float d = a[i] - b[i];
                sum += d * d; count++;
            }
            return count > 0 ? MathF.Sqrt(sum / count) : 0f;
        }

        /// <summary>p95 of a value array (sorted in place). / 值数组的 p95（原地排序）。</summary>
        public static float Percentile95(float[] values, int count)
        {
            if (count <= 0) return 0f;
            Array.Sort(values, 0, count);
            return values[Math.Min(count - 1, (int)(count * 0.95f))];
        }

        /// <summary>
        /// IoU of two binary masks (clip outlines). / 两个二值掩码的 IoU（clip 轮廓）。
        /// </summary>
        public static float IoU(byte[] a, byte[] b, byte[] valid)
        {
            long inter = 0, union = 0;
            for (int i = 0; i < a.Length; i++)
            {
                if (valid != null && valid[i] == 0) continue;
                bool ba = a[i] != 0, bb = b[i] != 0;
                if (ba && bb) inter++;
                if (ba || bb) union++;
            }
            return union > 0 ? (float)inter / union : 1f;
        }

        // ---------------- normal maps / 法线贴图 ----------------

        /// <summary>Decode a tangent-space normal from RGBA (0..1). / 从 RGBA 解码切线空间法线。</summary>
        public static void DecodeNormal(float r, float g, float b, out float nx, out float ny, out float nz)
        {
            nx = r * 2f - 1f; ny = g * 2f - 1f; nz = b * 2f - 1f;
            float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len > 1e-6f) { nx /= len; ny /= len; nz /= len; }
            else { nx = 0f; ny = 0f; nz = 1f; }
        }

        /// <summary>Angular error in degrees between two decoded normals. / 两个解码法线间的角度误差（度）。</summary>
        public static float AngleDeg(float nx1, float ny1, float nz1, float nx2, float ny2, float nz2)
        {
            float dot = nx1 * nx2 + ny1 * ny2 + nz1 * nz2;
            dot = Math.Clamp(dot, -1f, 1f);
            return RadToDeg(MathF.Acos(dot));
        }

        /// <summary>Re-encode a normal into 0..1. / 将法线重新编码到 0..1。</summary>
        public static void EncodeNormal(float nx, float ny, float nz, out float r, out float g, out float b)
        {
            r = nx * 0.5f + 0.5f; g = ny * 0.5f + 0.5f; b = nz * 0.5f + 0.5f;
        }
    }
}
