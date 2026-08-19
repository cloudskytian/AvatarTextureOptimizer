using Unity.Collections;
using Unity.Mathematics;

namespace Fosa.AvatarTextureOptimizer.Editor.Quality
{
    // 目标质量算法纯数学核心（供 Burst 作业调用；全部数组使用 NativeArray，无堆分配）。
    // Pure-math core of the target quality algorithm (called from Burst jobs; NativeArray-based, no heap allocation).
    //
    // 度量（依据需求）：
    // - MS-SSIM（包围盒短边 < 176px 的岛回退单尺度 SSIM；< 11px 忽略）；不透明贴图 MS-SSIM + CIEDE2000 ΔE；
    // - Cutout：clip 后轮廓 IoU；Blend：alpha 线性 RMSE（多材质引用逐一评估取最严苛）；
    // - 法线：解码 → 重采样 → 重归一化 → 编码 → 再解码后角度误差 p95；
    // - 灰度：仅被使用通道上的线性 RMSE，逐通道取最差。
    // - 透明贴图预乘 alpha 下采样（重采样在预乘域），SSIM/ΔE 在非预乘线性 RGB 上（alpha>0 像素）。
    public static class QualityMath
    {
        // ---- sRGB ↔ 线性 ----
        private static readonly float[] SrgbToLinearLut = BuildSrgbToLinearLut();
        private static readonly byte[] LinearToSrgbLut = BuildLinearToSrgbLut();

        private static float[] BuildSrgbToLinearLut()
        {
            var lut = new float[256];
            for (int i = 0; i < 256; i++)
            {
                float c = i / 255f;
                lut[i] = c <= 0.04045f ? c / 12.92f : math.pow((c + 0.055f) / 1.055f, 2.4f);
            }
            return lut;
        }

        private static byte[] BuildLinearToSrgbLut()
        {
            var lut = new byte[65536];
            for (int i = 0; i < 65536; i++)
            {
                float c = i / 65535f;
                float s = c <= 0.0031308f ? c * 12.92f : 1.055f * math.pow(c, 1f / 2.4f) - 0.055f;
                lut[i] = (byte)math.clamp((int)math.round(s * 255f), 0, 255);
            }
            return lut;
        }

        public static float SrgbToLinear(byte b) { return SrgbToLinearLut[b]; }

        public static byte LinearToSrgbByte(float c)
        {
            c = math.clamp(c, 0f, 1f);
            return LinearToSrgbLut[(int)(c * 65535f + 0.5f)];
        }

        // ---- CIELAB / CIEDE2000 ----
        public static float3 LinearRgbToLab(float r, float g, float b)
        {
            // sRGB(D65) 线性 → XYZ → Lab。sRGB(D65) linear → XYZ → Lab.
            float x = 0.4124564f * r + 0.3575761f * g + 0.1804375f * b;
            float y = 0.2126729f * r + 0.7151522f * g + 0.0721750f * b;
            float z = 0.0193339f * r + 0.1191920f * g + 0.9503041f * b;
            x /= 0.95047f; z /= 1.08883f;
            float fx = LabF(x), fy = LabF(y), fz = LabF(z);
            return new float3(116f * fy - 16f, 500f * (fx - fy), 200f * (fy - fz));
        }

        private static float LabF(float t)
        {
            const float d = 6f / 29f;
            return t > d * d * d ? math.pow(t, 1f / 3f) : t / (3f * d * d) + 4f / 29f;
        }

        // CIEDE2000 色差（Sharma et al. 2005）。CIEDE2000 color difference.
        public static float DeltaE2000(float3 lab1, float3 lab2)
        {
            float L1 = lab1.x, a1 = lab1.y, b1 = lab1.z;
            float L2 = lab2.x, a2 = lab2.y, b2 = lab2.z;
            float C1 = math.sqrt(a1 * a1 + b1 * b1);
            float C2 = math.sqrt(a2 * a2 + b2 * b2);
            float Cb = (C1 + C2) * 0.5f;
            float Cb7 = math.pow(Cb, 7f);
            float G = 0.5f * (1f - math.sqrt(Cb7 / (Cb7 + 6103515625f))); // 25^7
            float a1p = (1f + G) * a1, a2p = (1f + G) * a2;
            float C1p = math.sqrt(a1p * a1p + b1 * b1);
            float C2p = math.sqrt(a2p * a2p + b2 * b2);
            float h1p = HueAngle(b1, a1p);
            float h2p = HueAngle(b2, a2p);

            float dL = L2 - L1;
            float dC = C2p - C1p;
            float dhp;
            if (C1p * C2p == 0f) dhp = 0f;
            else
            {
                dhp = h2p - h1p;
                if (dhp > 180f) dhp -= 360f;
                else if (dhp < -180f) dhp += 360f;
            }
            float dH = 2f * math.sqrt(C1p * C2p) * math.sin(math.radians(dhp) * 0.5f);

            float Lbp = (L1 + L2) * 0.5f;
            float Cbp = (C1p + C2p) * 0.5f;
            float Cbp7 = math.pow(Cbp, 7f);
            float hbp;
            if (C1p * C2p == 0f) hbp = h1p + h2p;
            else if (math.abs(h1p - h2p) <= 180f) hbp = (h1p + h2p) * 0.5f;
            else if (h1p + h2p < 360f) hbp = (h1p + h2p + 360f) * 0.5f;
            else hbp = (h1p + h2p - 360f) * 0.5f;

            float T = 1f - 0.17f * math.cos(math.radians(hbp - 30f)) + 0.24f * math.cos(math.radians(2f * hbp))
                      + 0.32f * math.cos(math.radians(3f * hbp + 6f)) - 0.20f * math.cos(math.radians(4f * hbp - 63f));
            float dTheta = 30f * math.exp(-math.pow((hbp - 275f) / 25f, 2f));
            float Rc = 2f * math.sqrt(Cbp7 / (Cbp7 + 6103515625f));
            float Lm50 = (Lbp - 50f) * (Lbp - 50f);
            float Sl = 1f + 0.015f * Lm50 / math.sqrt(20f + Lm50);
            float Sc = 1f + 0.045f * Cbp;
            float Sh = 1f + 0.015f * Cbp * T;
            float Rt = -math.sin(math.radians(2f * dTheta)) * Rc;

            float dl = dL / Sl, dc = dC / Sc, dh = dH / Sh;
            return math.sqrt(dl * dl + dc * dc + dh * dh + Rt * dc * dh);
        }

        private static float HueAngle(float b, float a)
        {
            if (a == 0f && b == 0f) return 0f;
            float h = math.degrees(math.atan2(b, a));
            return h < 0f ? h + 360f : h;
        }

        // ---- 高斯模糊（可分离）----
        public static void GaussianBlur(NativeArray<float> src, NativeArray<float> dst, int w, int h, int channels, int taps, float sigma)
        {
            var tmp = new NativeArray<float>(w * h * channels, Allocator.Temp);
            var kernel = BuildKernel(taps, sigma);
            int r = taps / 2;
            // 水平。Horizontal.
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    for (int c = 0; c < channels; c++)
                    {
                        float sum = 0f;
                        for (int k = -r; k <= r; k++)
                        {
                            int xx = math.clamp(x + k, 0, w - 1);
                            sum += src[(y * w + xx) * channels + c] * kernel[k + r];
                        }
                        tmp[(y * w + x) * channels + c] = sum;
                    }
                }
            }
            // 垂直。Vertical.
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    for (int c = 0; c < channels; c++)
                    {
                        float sum = 0f;
                        for (int k = -r; k <= r; k++)
                        {
                            int yy = math.clamp(y + k, 0, h - 1);
                            sum += tmp[(yy * w + x) * channels + c] * kernel[k + r];
                        }
                        dst[(y * w + x) * channels + c] = sum;
                    }
                }
            }
            tmp.Dispose();
        }

        private static float[] BuildKernel(int taps, float sigma)
        {
            var k = new float[taps];
            int r = taps / 2;
            float sum = 0f;
            for (int i = -r; i <= r; i++)
            {
                float v = math.exp(-(i * i) / (2f * sigma * sigma));
                k[i + r] = v;
                sum += v;
            }
            for (int i = 0; i < taps; i++) k[i] /= sum;
            return k;
        }

        // ---- SSIM / MS-SSIM ----
        public static float Ssim(NativeArray<float> a, NativeArray<float> b, int w, int h)
        {
            // 3 通道（线性 RGB，0~1），逐像素亮度平均后整体平均。3 channels (linear RGB, 0..1).
            const float c1 = 0.0001f; // (0.01)^2
            const float c2 = 0.0009f; // (0.03)^2
            int n = w * h;
            var am = new NativeArray<float>(n, Allocator.Temp);
            var bm = new NativeArray<float>(n, Allocator.Temp);
            var asq = new NativeArray<float>(n, Allocator.Temp);
            var bsq = new NativeArray<float>(n, Allocator.Temp);
            var ab = new NativeArray<float>(n, Allocator.Temp);
            var mu1 = new NativeArray<float>(n, Allocator.Temp);
            var mu2 = new NativeArray<float>(n, Allocator.Temp);
            var m1sq = new NativeArray<float>(n, Allocator.Temp);
            var m2sq = new NativeArray<float>(n, Allocator.Temp);
            var m12 = new NativeArray<float>(n, Allocator.Temp);
            try
            {
                for (int i = 0; i < n; i++)
                {
                    float la = (a[i * 3] + a[i * 3 + 1] + a[i * 3 + 2]) / 3f;
                    float lb = (b[i * 3] + b[i * 3 + 1] + b[i * 3 + 2]) / 3f;
                    am[i] = la; bm[i] = lb; asq[i] = la * la; bsq[i] = lb * lb; ab[i] = la * lb;
                }
                GaussianBlur(am, mu1, w, h, 1, 11, 1.5f);
                GaussianBlur(bm, mu2, w, h, 1, 11, 1.5f);
                GaussianBlur(asq, m1sq, w, h, 1, 11, 1.5f);
                GaussianBlur(bsq, m2sq, w, h, 1, 11, 1.5f);
                GaussianBlur(ab, m12, w, h, 1, 11, 1.5f);
                double sum = 0;
                for (int i = 0; i < n; i++)
                {
                    float v1 = math.max(m1sq[i] - mu1[i] * mu1[i], 0f);
                    float v2 = math.max(m2sq[i] - mu2[i] * mu2[i], 0f);
                    float cov = m12[i] - mu1[i] * mu2[i];
                    sum += (2f * mu1[i] * mu2[i] + c1) * (2f * cov + c2)
                         / ((mu1[i] * mu1[i] + mu2[i] * mu2[i] + c1) * (v1 + v2 + c2));
                }
                return (float)(sum / n);
            }
            finally
            {
                am.Dispose(); bm.Dispose(); asq.Dispose(); bsq.Dispose(); ab.Dispose();
                mu1.Dispose(); mu2.Dispose(); m1sq.Dispose(); m2sq.Dispose(); m12.Dispose();
            }
        }

        // 2x 盒式降采样。2x box downsample.
        public static void Downsample2(NativeArray<float> src, NativeArray<float> dst, int w, int h, int channels)
        {
            int w2 = math.max(1, w / 2), h2 = math.max(1, h / 2);
            for (int y = 0; y < h2; y++)
            {
                for (int x = 0; x < w2; x++)
                {
                    for (int c = 0; c < channels; c++)
                    {
                        float s = src[((y * 2) * w + x * 2) * channels + c]
                                + src[((y * 2) * w + math.min(x * 2 + 1, w - 1)) * channels + c]
                                + src[((math.min(y * 2 + 1, h - 1)) * w + x * 2) * channels + c]
                                + src[((math.min(y * 2 + 1, h - 1)) * w + math.min(x * 2 + 1, w - 1)) * channels + c];
                        dst[(y * w2 + x) * channels + c] = s * 0.25f;
                    }
                }
            }
        }

        // MS-SSIM（5 层，标准权重）。MS-SSIM (5 levels, standard weights).
        public static float MsSsim(NativeArray<float> a, NativeArray<float> b, int w, int h, int channels)
        {
            int minSide = math.min(w, h);
            if (minSide < 11) return 1f; // 忽略。Skip.
            if (minSide < 176) return Ssim(a, b, w, h); // 单尺度回退。Single-scale fallback.

            const int M = 5;
            var weights = new[] { 0.0448f, 0.2856f, 0.3001f, 0.2363f, 0.1333f };
            var levelsA = new NativeArray<float>[M];
            var levelsB = new NativeArray<float>[M];
            try
            {
                levelsA[0] = new NativeArray<float>(a, Allocator.Temp);
                levelsB[0] = new NativeArray<float>(b, Allocator.Temp);
                for (int i = 1; i < M; i++)
                {
                    int pw = math.max(1, w >> (i - 1)), ph = math.max(1, h >> (i - 1));
                    int cw = math.max(1, w >> i), ch = math.max(1, h >> i);
                    levelsA[i] = new NativeArray<float>(cw * ch * channels, Allocator.Temp);
                    levelsB[i] = new NativeArray<float>(cw * ch * channels, Allocator.Temp);
                    Downsample2(levelsA[i - 1], levelsA[i], pw, ph, channels);
                    Downsample2(levelsB[i - 1], levelsB[i], pw, ph, channels);
                }

                double ms = 1.0;
                for (int level = 0; level < M; level++)
                {
                    int lw = math.max(1, w >> level), lh = math.max(1, h >> level);
                    if (lw < 11 || lh < 11) continue;
                    double cs = SsimCs(levelsA[level], levelsB[level], lw, lh, channels);
                    ms *= math.pow(math.max(cs, 1e-12), weights[level]);
                }
                return (float)ms;
            }
            finally
            {
                for (int i = 0; i < M; i++)
                {
                    if (levelsA[i].IsCreated) levelsA[i].Dispose();
                    if (levelsB[i].IsCreated) levelsB[i].Dispose();
                }
            }
        }

        // 对比度×结构项（不含亮度项）。Contrast × structure term (without the luminance term).
        private static double SsimCs(NativeArray<float> a, NativeArray<float> b, int w, int h, int channels)
        {
            const float c2 = 0.0009f;
            int n = w * h;
            var am = new NativeArray<float>(n, Allocator.Temp);
            var bm = new NativeArray<float>(n, Allocator.Temp);
            var asq = new NativeArray<float>(n, Allocator.Temp);
            var bsq = new NativeArray<float>(n, Allocator.Temp);
            var ab = new NativeArray<float>(n, Allocator.Temp);
            var mu1 = new NativeArray<float>(n, Allocator.Temp);
            var mu2 = new NativeArray<float>(n, Allocator.Temp);
            var m1sq = new NativeArray<float>(n, Allocator.Temp);
            var m2sq = new NativeArray<float>(n, Allocator.Temp);
            var m12 = new NativeArray<float>(n, Allocator.Temp);
            try
            {
                for (int i = 0; i < n; i++)
                {
                    float la = 0f, lb = 0f;
                    for (int c = 0; c < channels; c++)
                    {
                        la += a[i * channels + c];
                        lb += b[i * channels + c];
                    }
                    la /= channels; lb /= channels;
                    am[i] = la; bm[i] = lb; asq[i] = la * la; bsq[i] = lb * lb; ab[i] = la * lb;
                }
                GaussianBlur(am, mu1, w, h, 1, 11, 1.5f);
                GaussianBlur(bm, mu2, w, h, 1, 11, 1.5f);
                GaussianBlur(asq, m1sq, w, h, 1, 11, 1.5f);
                GaussianBlur(bsq, m2sq, w, h, 1, 11, 1.5f);
                GaussianBlur(ab, m12, w, h, 1, 11, 1.5f);
                double sum = 0;
                for (int i = 0; i < n; i++)
                {
                    float v1 = math.max(m1sq[i] - mu1[i] * mu1[i], 0f);
                    float v2 = math.max(m2sq[i] - mu2[i] * mu2[i], 0f);
                    float cov = m12[i] - mu1[i] * mu2[i];
                    sum += (2f * cov + c2) / (v1 + v2 + c2);
                }
                return sum / n;
            }
            finally
            {
                am.Dispose(); bm.Dispose(); asq.Dispose(); bsq.Dispose(); ab.Dispose();
                mu1.Dispose(); mu2.Dispose(); m1sq.Dispose(); m2sq.Dispose(); m12.Dispose();
            }
        }

        // ---- 双线性重采样往返（预乘 alpha 域输入）----
        // 将 src(w,h) 降采样到 (tw,th) 再上采样回 (w,h)，返回候选图。Downsample then upsample back; returns the candidate.
        public static void ResampleRoundTrip(NativeArray<float> src, NativeArray<float> candidate, int w, int h, int channels, int tw, int th)
        {
            var small = new NativeArray<float>(tw * th * channels, Allocator.Temp);
            try
            {
                // 降采样（双线性）。Downsample (bilinear).
                for (int y = 0; y < th; y++)
                {
                    float fy = ((y + 0.5f) * h / th) - 0.5f;
                    int y0 = math.clamp((int)math.floor(fy), 0, h - 1);
                    int y1 = math.clamp(y0 + 1, 0, h - 1);
                    float wy = fy - y0;
                    for (int x = 0; x < tw; x++)
                    {
                        float fx = ((x + 0.5f) * w / tw) - 0.5f;
                        int x0 = math.clamp((int)math.floor(fx), 0, w - 1);
                        int x1 = math.clamp(x0 + 1, 0, w - 1);
                        float wx = fx - x0;
                        for (int c = 0; c < channels; c++)
                        {
                            float v = src[(y0 * w + x0) * channels + c] * (1f - wx) * (1f - wy)
                                    + src[(y0 * w + x1) * channels + c] * wx * (1f - wy)
                                    + src[(y1 * w + x0) * channels + c] * (1f - wx) * wy
                                    + src[(y1 * w + x1) * channels + c] * wx * wy;
                            small[(y * tw + x) * channels + c] = v;
                        }
                    }
                }
                // 上采样回原尺寸（双线性）。Upsample back (bilinear).
                for (int y = 0; y < h; y++)
                {
                    float fy = ((y + 0.5f) * th / h) - 0.5f;
                    int y0 = math.clamp((int)math.floor(fy), 0, th - 1);
                    int y1 = math.clamp(y0 + 1, 0, th - 1);
                    float wy = fy - y0;
                    for (int x = 0; x < w; x++)
                    {
                        float fx = ((x + 0.5f) * tw / w) - 0.5f;
                        int x0 = math.clamp((int)math.floor(fx), 0, tw - 1);
                        int x1 = math.clamp(x0 + 1, 0, tw - 1);
                        float wx = fx - x0;
                        for (int c = 0; c < channels; c++)
                        {
                            candidate[(y * w + x) * channels + c] =
                                  small[(y0 * tw + x0) * channels + c] * (1f - wx) * (1f - wy)
                                + small[(y0 * tw + x1) * channels + c] * wx * (1f - wy)
                                + small[(y1 * tw + x0) * channels + c] * (1f - wx) * wy
                                + small[(y1 * tw + x1) * channels + c] * wx * wy;
                        }
                    }
                }
            }
            finally
            {
                small.Dispose();
            }
        }

        // ---- 法线编解码 ----
        public static float3 DecodeNormalByte(byte r, byte g, byte b, byte a, bool dxt5nm)
        {
            if (dxt5nm)
            {
                float x = a / 255f * 2f - 1f;
                float y = g / 255f * 2f - 1f;
                float z = math.sqrt(math.max(1f - x * x - y * y, 0f));
                return math.normalize(new float3(x, y, z));
            }
            float x2 = r / 255f * 2f - 1f;
            float y2 = g / 255f * 2f - 1f;
            float z2 = b / 255f * 2f - 1f;
            return math.normalize(new float3(x2, y2, z2));
        }

        // 编码回 xyz 字节（用于写入法线图集；导入时由 Unity 平台压缩处理）。Encodes back to xyz bytes.
        public static void EncodeNormalXyz(float3 n, out byte r, out byte g, out byte b)
        {
            r = LinearToSrgbByte(n.x * 0.5f + 0.5f);
            g = LinearToSrgbByte(n.y * 0.5f + 0.5f);
            b = LinearToSrgbByte(n.z * 0.5f + 0.5f);
        }

        // 角度误差（度）。Angle error in degrees.
        public static float AngleDeg(float3 a, float3 b)
        {
            float d = math.clamp(math.dot(a, b), -1f, 1f);
            return math.degrees(math.acos(d));
        }

        // ---- 简单指标 ----
        public static float Rmse(NativeArray<float> a, NativeArray<float> b, int count)
        {
            double sum = 0;
            for (int i = 0; i < count; i++)
            {
                float d = a[i] - b[i];
                sum += (double)d * d;
            }
            return (float)math.sqrt(sum / math.max(1, count));
        }

        public static float IoU(NativeArray<bool> a, NativeArray<bool> b, int count)
        {
            int inter = 0, union = 0;
            for (int i = 0; i < count; i++)
            {
                if (a[i] && b[i]) inter++;
                if (a[i] || b[i]) union++;
            }
            if (union == 0) return 1f;
            return (float)inter / union;
        }
    }
}
