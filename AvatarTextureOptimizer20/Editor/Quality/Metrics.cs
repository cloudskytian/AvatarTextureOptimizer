// Burst-compiled quality metrics: MS-SSIM / SSIM, CIEDE2000 p95, alpha IoU/RMSE,
// normal angular error p95, per-channel gray RMSE.
// Burst 编译的质量指标：MS-SSIM/SSIM、CIEDE2000 p95、alpha IoU/RMSE、法线角度误差 p95、灰度逐通道 RMSE。
using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace net.fosa.ato.editor
{
    public struct MetricInputs
    {
        public int Width, Height;              // bbox crop size / 包围盒裁剪尺寸
        public int Role;                       // TexRole
        public byte UsedChannels;              // gray channels / 灰度通道
        public bool EvalCutout, EvalBlend;
        public float Cutoff1, Cutoff2, Cutoff3, Cutoff4; // up to 4 strictest cutoffs / 至多4个cutoff
        public int CutoffCount;
        public bool SkipSsim;                  // short side < 11 / 短边过小
        public bool SingleScaleSsim;           // short side < 176
    }

    public struct MetricOutputs
    {
        public float MsSsim;        // 1 when skipped / 跳过时为1
        public float DeltaE00P95;
        public float AlphaIoU;      // min over cutoffs; 1 when not evaluated
        public float AlphaRmse;     // 0 when not evaluated
        public float NormalAngleP95Deg;
        public float GrayRmseWorst;
    }

    [BurstCompile(CompileSynchronously = false)]
    public struct MetricsJob : IJob
    {
        [ReadOnly] public NativeArray<float4> Original;   // linear RGBA / 线性RGBA
        [ReadOnly] public NativeArray<float4> Degraded;
        [ReadOnly] public NativeArray<byte> Mask;         // 1 = inside island / 岛内
        public MetricInputs In;
        public NativeArray<float> Out; // [msssim, deP95, iou, aRmse, nAngleP95, grayRmse]

        public void Execute()
        {
            int n = In.Width * In.Height;
            float msssim = 1f, deP95 = 0f, iou = 1f, aRmse = 0f, nP95 = 0f, gray = 0f;

            if (In.Role == 1) // Normal / 法线：角度误差 p95
            {
                nP95 = NormalAngleP95(n);
            }
            else if (In.Role == 2) // Gray / 灰度：逐通道线性 RMSE 取最差
            {
                gray = GrayWorstRmse(n);
            }
            else // Color / 颜色：MS-SSIM + dE00 (+ alpha)
            {
                if (!In.SkipSsim) msssim = In.SingleScaleSsim ? Ssim(1) : MsSsim();
                deP95 = DeltaEP95(n);
            }

            if (In.EvalCutout) iou = CutoutIoU(n);
            if (In.EvalBlend) aRmse = AlphaRmse(n);

            Out[0] = msssim; Out[1] = deP95; Out[2] = iou;
            Out[3] = aRmse; Out[4] = nP95; Out[5] = gray;
        }

        // ---- luma helpers (perceptual gamma luma for SSIM stability) ----
        private float Luma(float4 c)
        {
            float3 srgb = math.pow(math.saturate(c.xyz), 1f / 2.2f);
            return math.dot(srgb, new float3(0.2126f, 0.7152f, 0.0722f));
        }

        /// <summary>Full 5-scale MS-SSIM (Wang et al. 2003 weights). / 标准5尺度MS-SSIM。</summary>
        private float MsSsim()
        {
            // weights / 权重
            var w = new float4x2(new float4(0.0448f, 0.2856f, 0.3001f, 0.2363f), new float4(0.1333f, 0, 0, 0));
            int scales = 5;
            int shortSide = math.min(In.Width, In.Height);
            while (scales > 1 && (shortSide >> (scales - 1)) < 11) scales--;

            float result = 1f;
            for (int s = 0; s < scales; s++)
            {
                float2 cs = SsimCs(s, s == scales - 1);
                float weight = s < 4 ? w.c0[s] : w.c1[0];
                // renormalize weights when fewer scales / 尺度减少时权重重归一
                result *= math.pow(math.max(cs.x * (s == scales - 1 ? cs.y : 1f), 1e-6f), weight);
            }
            // normalize weight sum to 1 across used scales / 权重和归一
            float wsum = 0;
            for (int s = 0; s < scales; s++) wsum += s < 4 ? w.c0[s] : w.c1[0];
            return math.pow(result, 1f / math.max(wsum, 1e-6f));
        }

        private float Ssim(int _) { var cs = SsimCs(0, true); return cs.x * cs.y; }

        /// <summary>Contrast*structure (x) and luminance (y) mean SSIM at scale s. / 尺度s的SSIM分量。</summary>
        private float2 SsimCs(int scale, bool withLuminance)
        {
            int w = math.max(1, In.Width >> scale), h = math.max(1, In.Height >> scale);
            var a = new NativeArray<float>(w * h, Allocator.Temp);
            var b = new NativeArray<float>(w * h, Allocator.Temp);
            var m = new NativeArray<byte>(w * h, Allocator.Temp);
            int step = 1 << scale;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    // box downsample / 盒式下采样
                    float sa = 0, sb = 0; int cnt = 0; byte anyMask = 0;
                    for (int dy = 0; dy < step; dy++)
                        for (int dx = 0; dx < step; dx++)
                        {
                            int sx = x * step + dx, sy = y * step + dy;
                            if (sx >= In.Width || sy >= In.Height) continue;
                            int si = sy * In.Width + sx;
                            sa += Luma(Original[si]); sb += Luma(Degraded[si]); cnt++;
                            if (Mask[si] != 0) anyMask = 1;
                        }
                    int di = y * w + x;
                    a[di] = cnt > 0 ? sa / cnt : 0;
                    b[di] = cnt > 0 ? sb / cnt : 0;
                    m[di] = anyMask;
                }

            const float C1 = 0.01f * 0.01f, C2 = 0.03f * 0.03f;
            const int R = 5; // 11x11 window / 11×11窗口
            float sumCs = 0, sumL = 0; int windows = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (m[y * w + x] == 0) continue;
                    float ma = 0, mb = 0, wsum = 0;
                    for (int dy = -R; dy <= R; dy++)
                        for (int dx = -R; dx <= R; dx++)
                        {
                            int sx = x + dx, sy = y + dy;
                            if (sx < 0 || sy < 0 || sx >= w || sy >= h) continue;
                            float g = math.exp(-(dx * dx + dy * dy) / (2f * 1.5f * 1.5f));
                            int i = sy * w + sx;
                            ma += a[i] * g; mb += b[i] * g; wsum += g;
                        }
                    ma /= wsum; mb /= wsum;
                    float va = 0, vb = 0, cov = 0;
                    for (int dy = -R; dy <= R; dy++)
                        for (int dx = -R; dx <= R; dx++)
                        {
                            int sx = x + dx, sy = y + dy;
                            if (sx < 0 || sy < 0 || sx >= w || sy >= h) continue;
                            float g = math.exp(-(dx * dx + dy * dy) / (2f * 1.5f * 1.5f));
                            int i = sy * w + sx;
                            va += (a[i] - ma) * (a[i] - ma) * g;
                            vb += (b[i] - mb) * (b[i] - mb) * g;
                            cov += (a[i] - ma) * (b[i] - mb) * g;
                        }
                    va /= wsum; vb /= wsum; cov /= wsum;
                    float cs = (2 * cov + C2) / (va + vb + C2);
                    float l = (2 * ma * mb + C1) / (ma * ma + mb * mb + C1);
                    sumCs += cs; sumL += l; windows++;
                }
            a.Dispose(); b.Dispose(); m.Dispose();
            if (windows == 0) return new float2(1, 1);
            return new float2(sumCs / windows, withLuminance ? sumL / windows : 1f);
        }

        // ---- CIEDE2000 p95 via histogram / 直方图求 p95 ----
        private float DeltaEP95(int n)
        {
            var hist = new NativeArray<int>(1024, Allocator.Temp);
            const float maxDe = 25f;
            int total = 0;
            for (int i = 0; i < n; i++)
            {
                if (Mask[i] == 0) continue;
                float de = Ciede2000(LinearToLab(Original[i].xyz), LinearToLab(Degraded[i].xyz));
                int bin = (int)math.clamp(de / maxDe * 1023f, 0, 1023);
                hist[bin]++; total++;
            }
            if (total == 0) { hist.Dispose(); return 0; }
            int target = (int)(total * 0.95f), acc = 0; float p95 = maxDe;
            for (int b = 0; b < 1024; b++)
            {
                acc += hist[b];
                if (acc >= target) { p95 = (b + 0.5f) * maxDe / 1024f; break; }
            }
            hist.Dispose();
            return p95;
        }

        private static float3 LinearToLab(float3 rgb)
        {
            rgb = math.max(rgb, 0f);
            // linear sRGB -> XYZ (D65)
            float X = math.dot(rgb, new float3(0.4124f, 0.3576f, 0.1805f));
            float Y = math.dot(rgb, new float3(0.2126f, 0.7152f, 0.0722f));
            float Z = math.dot(rgb, new float3(0.0193f, 0.1192f, 0.9505f));
            float3 xyz = new float3(X / 0.95047f, Y, Z / 1.08883f);
            float3 f = math.select(
                (7.787f * xyz) + 16f / 116f,
                math.pow(xyz, 1f / 3f),
                xyz > 0.008856f);
            return new float3(116f * f.y - 16f, 500f * (f.x - f.y), 200f * (f.y - f.z));
        }

        private static float Ciede2000(float3 lab1, float3 lab2)
        {
            float L1 = lab1.x, a1 = lab1.y, b1 = lab1.z;
            float L2 = lab2.x, a2 = lab2.y, b2 = lab2.z;
            float C1 = math.sqrt(a1 * a1 + b1 * b1), C2 = math.sqrt(a2 * a2 + b2 * b2);
            float Cb = (C1 + C2) * 0.5f;
            float G = 0.5f * (1f - math.sqrt(math.pow(Cb, 7) / (math.pow(Cb, 7) + math.pow(25f, 7))));
            float ap1 = (1 + G) * a1, ap2 = (1 + G) * a2;
            float Cp1 = math.sqrt(ap1 * ap1 + b1 * b1), Cp2 = math.sqrt(ap2 * ap2 + b2 * b2);
            float hp1 = math.atan2(b1, ap1), hp2 = math.atan2(b2, ap2);
            if (hp1 < 0) hp1 += 2 * math.PI;
            if (hp2 < 0) hp2 += 2 * math.PI;
            float dL = L2 - L1, dC = Cp2 - Cp1;
            float dhp = hp2 - hp1;
            if (dhp > math.PI) dhp -= 2 * math.PI;
            if (dhp < -math.PI) dhp += 2 * math.PI;
            if (Cp1 * Cp2 == 0) dhp = 0;
            float dH = 2 * math.sqrt(Cp1 * Cp2) * math.sin(dhp * 0.5f);
            float Lb = (L1 + L2) * 0.5f, Cpb = (Cp1 + Cp2) * 0.5f;
            float hpb = (hp1 + hp2) * 0.5f;
            if (math.abs(hp1 - hp2) > math.PI) hpb += math.PI;
            if (Cp1 * Cp2 == 0) hpb = hp1 + hp2;
            float T = 1 - 0.17f * math.cos(hpb - math.radians(30f)) + 0.24f * math.cos(2 * hpb)
                      + 0.32f * math.cos(3 * hpb + math.radians(6f)) - 0.20f * math.cos(4 * hpb - math.radians(63f));
            float dTheta = math.radians(30f) * math.exp(-math.pow((math.degrees(hpb) - 275f) / 25f, 2));
            float Rc = 2 * math.sqrt(math.pow(Cpb, 7) / (math.pow(Cpb, 7) + math.pow(25f, 7)));
            float Sl = 1 + 0.015f * math.pow(Lb - 50, 2) / math.sqrt(20 + math.pow(Lb - 50, 2));
            float Sc = 1 + 0.045f * Cpb;
            float Sh = 1 + 0.015f * Cpb * T;
            float Rt = -math.sin(2 * dTheta) * Rc;
            float dl = dL / Sl, dc = dC / Sc, dh = dH / Sh;
            return math.sqrt(dl * dl + dc * dc + dh * dh + Rt * dc * dh);
        }

        // ---- alpha metrics / 透明指标 ----
        private float CutoutIoU(int n)
        {
            float worst = 1f;
            for (int c = 0; c < In.CutoffCount; c++)
            {
                float cutoff = c == 0 ? In.Cutoff1 : c == 1 ? In.Cutoff2 : c == 2 ? In.Cutoff3 : In.Cutoff4;
                int inter = 0, union = 0;
                for (int i = 0; i < n; i++)
                {
                    if (Mask[i] == 0) continue;
                    bool a = Original[i].w >= cutoff, b = Degraded[i].w >= cutoff;
                    if (a && b) inter++;
                    if (a || b) union++;
                }
                float iou = union == 0 ? 1f : inter / (float)union;
                worst = math.min(worst, iou);
            }
            return worst;
        }

        private float AlphaRmse(int n)
        {
            double sum = 0; int cnt = 0;
            for (int i = 0; i < n; i++)
            {
                if (Mask[i] == 0) continue;
                float d = Original[i].w - Degraded[i].w;
                sum += d * d; cnt++;
            }
            return cnt == 0 ? 0 : (float)math.sqrt(sum / cnt);
        }

        // ---- normal / 法线 ----
        private float NormalAngleP95(int n)
        {
            var hist = new NativeArray<int>(1024, Allocator.Temp);
            const float maxDeg = 45f;
            int total = 0;
            for (int i = 0; i < n; i++)
            {
                if (Mask[i] == 0) continue;
                float3 n1 = math.normalizesafe(Original[i].xyz * 2f - 1f, new float3(0, 0, 1));
                float3 n2 = math.normalizesafe(Degraded[i].xyz * 2f - 1f, new float3(0, 0, 1));
                float ang = math.degrees(math.acos(math.clamp(math.dot(n1, n2), -1f, 1f)));
                hist[(int)math.clamp(ang / maxDeg * 1023f, 0, 1023)]++; total++;
            }
            if (total == 0) { hist.Dispose(); return 0; }
            int target = (int)(total * 0.95f), acc = 0; float p95 = maxDeg;
            for (int b = 0; b < 1024; b++)
            {
                acc += hist[b];
                if (acc >= target) { p95 = (b + 0.5f) * maxDeg / 1024f; break; }
            }
            hist.Dispose();
            return p95;
        }

        // ---- gray / 灰度 ----
        private float GrayWorstRmse(int n)
        {
            float worst = 0;
            for (int ch = 0; ch < 4; ch++)
            {
                if ((In.UsedChannels & (1 << ch)) == 0) continue;
                double sum = 0; int cnt = 0;
                for (int i = 0; i < n; i++)
                {
                    if (Mask[i] == 0) continue;
                    float d = Original[i][ch] - Degraded[i][ch];
                    sum += d * d; cnt++;
                }
                if (cnt > 0) worst = math.max(worst, (float)math.sqrt(sum / cnt));
            }
            return worst;
        }
    }
}
