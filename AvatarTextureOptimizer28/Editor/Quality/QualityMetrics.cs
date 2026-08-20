using System;
using System.Threading.Tasks;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>EN: All metric values produced for one comparison. ZH: 一次比较产生的全部度量值。</summary>
    public struct MetricResult
    {
        /// <summary>EN: MS-SSIM or single-scale SSIM, or 1 when not applicable. ZH: MS-SSIM 或单尺度 SSIM，不适用时为 1。</summary>
        public float MsSsim;
        /// <summary>EN: Mean CIEDE2000. ZH: 平均 CIEDE2000。</summary>
        public float DeltaEMean;
        /// <summary>EN: 95th percentile CIEDE2000. ZH: 95 分位 CIEDE2000。</summary>
        public float DeltaEP95;
        /// <summary>EN: Cutout silhouette IoU. ZH: Cutout 轮廓 IoU。</summary>
        public float AlphaIoU;
        /// <summary>EN: Blend alpha linear RMSE. ZH: Blend alpha 线性 RMSE。</summary>
        public float AlphaRmse;
        /// <summary>EN: Mean normal angular error in degrees. ZH: 平均法线角度误差（度）。</summary>
        public float NormalMeanDeg;
        /// <summary>EN: 95th percentile normal angular error in degrees. ZH: 95 分位法线角度误差（度）。</summary>
        public float NormalP95Deg;
        /// <summary>EN: Worst per-channel linear RMSE for data textures. ZH: 数据贴图逐通道最差的线性 RMSE。</summary>
        public float GrayRmse;
    }

    /// <summary>
    /// EN: The target quality algorithm's measurement layer.
    ///
    ///     Engineering note on where this runs: decoding and atlas composition happen on the GPU, but
    ///     the metrics themselves run on the CPU across all cores. Islands are small (typically a few
    ///     thousand texels) and the binary search issues thousands of tiny comparisons; a GPU dispatch
    ///     plus readback per comparison would be latency-bound and measurably slower than a parallel
    ///     CPU pass, besides being non-deterministic across drivers. Determinism matters here because
    ///     the accepted scale directly changes the baked output.
    ///
    /// ZH: 目标质量算法的度量层。
    ///
    ///     关于执行位置的工程说明：解码与图集合成在 GPU 上进行，但度量本身在 CPU 上多核并行执行。
    ///     岛通常很小（几千个纹素），而二分搜索会发出成千上万次微小比较；
    ///     每次比较都做一次 GPU dispatch + 回读会受延迟主导，实测比 CPU 并行更慢，
    ///     而且结果会随驱动而变。这里确定性很重要，因为被接受的缩放比例会直接改变烘焙产物。
    /// </summary>
    public static class QualityMetrics
    {
        // EN: Standard SSIM stabilisation constants for data in [0,1] (Wang et al. 2004).
        // ZH: 数据范围 [0,1] 时的标准 SSIM 稳定常数（Wang et al. 2004）。
        private const float C1 = 0.01f * 0.01f;
        private const float C2 = 0.03f * 0.03f;

        // EN: The canonical five-scale MS-SSIM exponents from Wang, Simoncelli & Bovik 2003.
        // ZH: Wang, Simoncelli & Bovik 2003 给出的经典五尺度 MS-SSIM 指数。
        private static readonly float[] MsSsimWeights = { 0.0448f, 0.2856f, 0.3001f, 0.2363f, 0.1333f };

        /// <summary>
        /// EN: Compare a reference tile against a round-tripped candidate and produce every metric that
        ///     applies to <paramref name="cls"/>. Metrics that do not apply are returned as passing values.
        /// ZH: 比较参考区域与来回重采样后的候选区域，产出适用于 <paramref name="cls"/> 的全部度量。
        ///     不适用的度量返回"通过"值。
        /// </summary>
        public static MetricResult Compare(Tile reference, Tile candidate, TextureClass cls,
            AlphaMode alphaMode, float cutoff, bool4Mask usedChannels, bool agEncodedNormal)
        {
            var r = new MetricResult
            {
                MsSsim = 1f, DeltaEMean = 0f, DeltaEP95 = 0f,
                AlphaIoU = 1f, AlphaRmse = 0f,
                NormalMeanDeg = 0f, NormalP95Deg = 0f, GrayRmse = 0f,
            };

            switch (cls)
            {
                case TextureClass.Normal:
                {
                    var a = ImageOps.DecodeNormals(reference, agEncodedNormal);
                    var b = ImageOps.DecodeNormals(candidate, agEncodedNormal);
                    var angles = new float[a.Length];
                    Parallel.For(0, a.Length, i =>
                    {
                        var d = Mathf.Clamp(Vector3.Dot(a[i], b[i]), -1f, 1f);
                        angles[i] = Mathf.Acos(d) * Mathf.Rad2Deg;
                    });
                    r.NormalMeanDeg = Mean(angles);
                    r.NormalP95Deg = Percentile(angles, 0.95f);
                    return r;
                }

                case TextureClass.Grayscale:
                {
                    r.GrayRmse = WorstChannelRmse(reference, candidate, usedChannels);
                    return r;
                }

                default:
                {
                    bool hasAlpha = cls == TextureClass.TransparentColor;
                    r.MsSsim = MsSsimOrSsim(reference, candidate);

                    var de = DeltaE2000Field(reference, candidate);
                    r.DeltaEMean = Mean(de);
                    r.DeltaEP95 = Percentile(de, 0.95f);

                    if (hasAlpha)
                    {
                        if (alphaMode == AlphaMode.Cutout) r.AlphaIoU = SilhouetteIoU(reference, candidate, cutoff);
                        if (alphaMode == AlphaMode.Blend) r.AlphaRmse = AlphaRmse(reference, candidate);
                    }
                    return r;
                }
            }
        }

        /// <summary>
        /// EN: True when every applicable metric meets the profile. Small islands drop SSIM entirely and
        ///     mid-sized islands fall back from MS-SSIM to single-scale SSIM, exactly as specified.
        /// ZH: 所有适用度量都满足配置时返回 true。
        ///     小岛完全放弃 SSIM，中等尺寸的岛从 MS-SSIM 回退到单尺度 SSIM，与需求一致。
        /// </summary>
        public static bool Passes(in MetricResult m, in QualityProfile q, TextureClass cls,
            AlphaMode alphaMode, int originalShortSide)
        {
            switch (cls)
            {
                case TextureClass.Normal:
                    return m.NormalMeanDeg <= q.maxNormalAngleMeanDeg && m.NormalP95Deg <= q.maxNormalAngleP95Deg;
                case TextureClass.Grayscale:
                    return m.GrayRmse <= q.maxGrayscaleRmse;
                default:
                    if (originalShortSide >= ATOConstants.SsimIgnoreShortSide && m.MsSsim < q.minMsSsim) return false;
                    if (m.DeltaEMean > q.maxDeltaE2000Mean) return false;
                    if (m.DeltaEP95 > q.maxDeltaE2000P95) return false;
                    if (cls == TextureClass.TransparentColor)
                    {
                        if (alphaMode == AlphaMode.Cutout && m.AlphaIoU < q.minAlphaCutoutIoU) return false;
                        if (alphaMode == AlphaMode.Blend && m.AlphaRmse > q.maxAlphaBlendRmse) return false;
                    }
                    return true;
            }
        }

        // ---- SSIM ------------------------------------------------------------------------------------

        private static float MsSsimOrSsim(Tile a, Tile b)
        {
            int shortSide = Mathf.Min(a.W, a.H);
            if (shortSide < ATOConstants.SsimIgnoreShortSide) return 1f;
            if (shortSide < ATOConstants.MsSsimMinShortSide) return Ssim(a, b);

            // EN: Five-scale MS-SSIM; each scale contributes contrast/structure, only the last also
            //     contributes luminance, per the original formulation.
            // ZH: 五尺度 MS-SSIM；按原始定义，每个尺度贡献对比度/结构，只有最后一个尺度额外贡献亮度。
            float product = 1f;
            var ca = a; var cb = b;
            for (int s = 0; s < MsSsimWeights.Length; s++)
            {
                bool last = s == MsSsimWeights.Length - 1;
                var v = last ? Ssim(ca, cb) : SsimContrastStructure(ca, cb);
                product *= Mathf.Pow(Mathf.Max(1e-6f, v), MsSsimWeights[s]);
                if (last) break;
                int nw = Mathf.Max(1, ca.W / 2);
                int nh = Mathf.Max(1, ca.H / 2);
                if (nw < 8 || nh < 8) break;
                ca = ImageOps.Downsample(ca, nw, nh, false);
                cb = ImageOps.Downsample(cb, nw, nh, false);
            }
            return Mathf.Clamp01(product);
        }

        private static float Luma(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;

        /// <summary>EN: Global SSIM over an 8x8 windowed grid. ZH: 基于 8x8 窗口网格的全局 SSIM。</summary>
        public static float Ssim(Tile a, Tile b) => SsimCore(a, b, includeLuminance: true);

        private static float SsimContrastStructure(Tile a, Tile b) => SsimCore(a, b, includeLuminance: false);

        private static float SsimCore(Tile a, Tile b, bool includeLuminance)
        {
            const int win = 8;
            int wx = Mathf.Max(1, a.W / win);
            int wy = Mathf.Max(1, a.H / win);
            var partial = new double[wy];

            Parallel.For(0, wy, by =>
            {
                double acc = 0;
                for (int bx = 0; bx < wx; bx++)
                {
                    int x0 = bx * win, y0 = by * win;
                    int x1 = Mathf.Min(x0 + win, a.W), y1 = Mathf.Min(y0 + win, a.H);
                    int n = (x1 - x0) * (y1 - y0);
                    if (n <= 1) continue;

                    double ma = 0, mb = 0;
                    for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                    {
                        ma += Luma(a.P[y * a.W + x]);
                        mb += Luma(b.P[y * b.W + x]);
                    }
                    ma /= n; mb /= n;

                    double va = 0, vb = 0, cov = 0;
                    for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                    {
                        double da = Luma(a.P[y * a.W + x]) - ma;
                        double db = Luma(b.P[y * b.W + x]) - mb;
                        va += da * da; vb += db * db; cov += da * db;
                    }
                    va /= n - 1; vb /= n - 1; cov /= n - 1;

                    double cs = (2 * cov + C2) / (va + vb + C2);
                    double v = includeLuminance
                        ? ((2 * ma * mb + C1) / (ma * ma + mb * mb + C1)) * cs
                        : cs;
                    acc += v;
                }
                partial[by] = acc;
            });

            double sum = 0;
            foreach (var p in partial) sum += p;
            return (float)Mathf.Clamp01((float)(sum / (wx * wy)));
        }

        // ---- CIEDE2000 -------------------------------------------------------------------------------

        private static float[] DeltaE2000Field(Tile a, Tile b)
        {
            var result = new float[a.P.Length];
            Parallel.For(0, a.H, y =>
            {
                for (int x = 0; x < a.W; x++)
                {
                    int i = y * a.W + x;
                    LinearToLab(a.P[i], out var l1, out var a1, out var b1);
                    LinearToLab(b.P[i], out var l2, out var a2, out var b2);
                    result[i] = DeltaE2000(l1, a1, b1, l2, a2, b2);
                }
            });
            return result;
        }

        /// <summary>EN: Linear sRGB to CIE L*a*b* under D65. ZH: 线性 sRGB 在 D65 下转 CIE L*a*b*。</summary>
        public static void LinearToLab(Color c, out float L, out float A, out float B)
        {
            float r = Mathf.Max(0f, c.r), g = Mathf.Max(0f, c.g), bl = Mathf.Max(0f, c.b);
            float X = r * 0.4124564f + g * 0.3575761f + bl * 0.1804375f;
            float Y = r * 0.2126729f + g * 0.7151522f + bl * 0.0721750f;
            float Z = r * 0.0193339f + g * 0.1191920f + bl * 0.9503041f;

            const float Xn = 0.95047f, Yn = 1.0f, Zn = 1.08883f;
            float fx = LabF(X / Xn), fy = LabF(Y / Yn), fz = LabF(Z / Zn);
            L = 116f * fy - 16f;
            A = 500f * (fx - fy);
            B = 200f * (fy - fz);
        }

        private static float LabF(float t)
        {
            const float d = 6f / 29f;
            return t > d * d * d ? Mathf.Pow(t, 1f / 3f) : t / (3f * d * d) + 4f / 29f;
        }

        /// <summary>EN: CIEDE2000 colour difference (Luo, Cui &amp; Rigg 2001). ZH: CIEDE2000 色差（Luo, Cui &amp; Rigg 2001）。</summary>
        public static float DeltaE2000(float L1, float a1, float b1, float L2, float a2, float b2)
        {
            const float kL = 1f, kC = 1f, kH = 1f;
            float C1s = Mathf.Sqrt(a1 * a1 + b1 * b1);
            float C2s = Mathf.Sqrt(a2 * a2 + b2 * b2);
            float Cbar = (C1s + C2s) * 0.5f;
            float Cbar7 = Mathf.Pow(Cbar, 7f);
            float G = 0.5f * (1f - Mathf.Sqrt(Cbar7 / (Cbar7 + 6103515625f)));  // 25^7

            float a1p = (1f + G) * a1, a2p = (1f + G) * a2;
            float C1p = Mathf.Sqrt(a1p * a1p + b1 * b1);
            float C2p = Mathf.Sqrt(a2p * a2p + b2 * b2);

            float h1p = Mathf.Abs(a1p) + Mathf.Abs(b1) < 1e-8f ? 0f : Mathf.Atan2(b1, a1p) * Mathf.Rad2Deg;
            if (h1p < 0) h1p += 360f;
            float h2p = Mathf.Abs(a2p) + Mathf.Abs(b2) < 1e-8f ? 0f : Mathf.Atan2(b2, a2p) * Mathf.Rad2Deg;
            if (h2p < 0) h2p += 360f;

            float dLp = L2 - L1;
            float dCp = C2p - C1p;

            float dhp;
            if (C1p * C2p < 1e-8f) dhp = 0f;
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
            if (C1p * C2p < 1e-8f) hbarp = h1p + h2p;
            else
            {
                float diff = Mathf.Abs(h1p - h2p);
                if (diff <= 180f) hbarp = (h1p + h2p) * 0.5f;
                else hbarp = h1p + h2p < 360f ? (h1p + h2p + 360f) * 0.5f : (h1p + h2p - 360f) * 0.5f;
            }

            float T = 1f
                      - 0.17f * Mathf.Cos((hbarp - 30f) * Mathf.Deg2Rad)
                      + 0.24f * Mathf.Cos((2f * hbarp) * Mathf.Deg2Rad)
                      + 0.32f * Mathf.Cos((3f * hbarp + 6f) * Mathf.Deg2Rad)
                      - 0.20f * Mathf.Cos((4f * hbarp - 63f) * Mathf.Deg2Rad);

            float dTheta = 30f * Mathf.Exp(-Mathf.Pow((hbarp - 275f) / 25f, 2f));
            float Cbarp7 = Mathf.Pow(Cbarp, 7f);
            float Rc = 2f * Mathf.Sqrt(Cbarp7 / (Cbarp7 + 6103515625f));
            float Sl = 1f + 0.015f * Mathf.Pow(Lbarp - 50f, 2f) / Mathf.Sqrt(20f + Mathf.Pow(Lbarp - 50f, 2f));
            float Sc = 1f + 0.045f * Cbarp;
            float Sh = 1f + 0.015f * Cbarp * T;
            float Rt = -Mathf.Sin(2f * dTheta * Mathf.Deg2Rad) * Rc;

            float term1 = dLp / (kL * Sl);
            float term2 = dCp / (kC * Sc);
            float term3 = dHp / (kH * Sh);
            return Mathf.Sqrt(term1 * term1 + term2 * term2 + term3 * term3 + Rt * term2 * term3);
        }

        // ---- Alpha -----------------------------------------------------------------------------------

        private static float SilhouetteIoU(Tile a, Tile b, float cutoff)
        {
            long inter = 0, union = 0;
            var lockObj = new object();
            Parallel.For(0, a.H, () => (i: 0L, u: 0L), (y, _, local) =>
            {
                long i2 = local.i, u2 = local.u;
                for (int x = 0; x < a.W; x++)
                {
                    bool pa = a.P[y * a.W + x].a >= cutoff;
                    bool pb = b.P[y * b.W + x].a >= cutoff;
                    if (pa && pb) i2++;
                    if (pa || pb) u2++;
                }
                return (i2, u2);
            }, local => { lock (lockObj) { inter += local.i; union += local.u; } });

            return union == 0 ? 1f : (float)inter / union;
        }

        private static float AlphaRmse(Tile a, Tile b)
        {
            double sum = 0;
            var lockObj = new object();
            Parallel.For(0, a.H, () => 0.0, (y, _, local) =>
            {
                for (int x = 0; x < a.W; x++)
                {
                    double d = a.P[y * a.W + x].a - b.P[y * b.W + x].a;
                    local += d * d;
                }
                return local;
            }, local => { lock (lockObj) sum += local; });
            return (float)Math.Sqrt(sum / Math.Max(1, a.P.Length));
        }

        private static float WorstChannelRmse(Tile a, Tile b, bool4Mask used)
        {
            var sums = new double[4];
            var lockObj = new object();
            Parallel.For(0, a.H, () => new double[4], (y, _, local) =>
            {
                for (int x = 0; x < a.W; x++)
                {
                    var ca = a.P[y * a.W + x];
                    var cb = b.P[y * b.W + x];
                    if (used.R) { var d = ca.r - cb.r; local[0] += d * d; }
                    if (used.G) { var d = ca.g - cb.g; local[1] += d * d; }
                    if (used.B) { var d = ca.b - cb.b; local[2] += d * d; }
                    if (used.A) { var d = ca.a - cb.a; local[3] += d * d; }
                }
                return local;
            }, local => { lock (lockObj) for (int i = 0; i < 4; i++) sums[i] += local[i]; });

            int n = Math.Max(1, a.P.Length);
            float worst = 0f;
            for (int i = 0; i < 4; i++) worst = Mathf.Max(worst, (float)Math.Sqrt(sums[i] / n));
            return worst;
        }

        // ---- Helpers ---------------------------------------------------------------------------------

        private static float Mean(float[] v)
        {
            if (v.Length == 0) return 0f;
            double s = 0;
            for (int i = 0; i < v.Length; i++) s += v[i];
            return (float)(s / v.Length);
        }

        private static float Percentile(float[] v, float p)
        {
            if (v.Length == 0) return 0f;
            var copy = (float[])v.Clone();
            Array.Sort(copy);
            int idx = Mathf.Clamp(Mathf.CeilToInt(p * (copy.Length - 1)), 0, copy.Length - 1);
            return copy[idx];
        }
    }
}
