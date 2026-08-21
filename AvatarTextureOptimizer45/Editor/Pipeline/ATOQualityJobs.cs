using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace net.fosa.ato
{
    // ============================================================================
    // Burst 质量评估作业(行并行流水线) / Burst quality-evaluation jobs (row-parallel pipeline).
    //
    // 架构 / Architecture:
    //   * 阶段1 ATOEvalResampleJob: 每个评估项独立执行 预乘线性下采样 + 上采样回原尺寸
    //     (IJobParallelFor over evals, 内部串行) / per-eval premultiplied-linear down + up sampling;
    //   * 阶段2 ATOGaussMomentsJob: 每个评估项每行独立计算 5 个高斯矩(3通道×5矩),
    //     (IJobParallelFor over evals×rows) / per-row Gaussian moments (5 moments × 3 channels);
    //   * 阶段3 ATOEvalReduceJob: 每个评估项独立汇总 SSIM/ΔE/IoU/RMSE/法线/灰度 指标
    //     (IJobParallelFor over evals) / per-eval metric reduction;
    //   * 阶段4 二分搜索调度在托管端完成(批量调度, 每个调度回合对全部活跃岛并行评估).
    //     Bisection scheduling happens in managed code (batch: all active islands evaluated in parallel per round).
    //
    // 缓冲池: 每个岛的比较分辨率在其搜索期间不变, 缓冲按岛一次性分配并跨全部二分轮次复用.
    // Buffers: comparison resolution is fixed per island during its search; buffers are allocated
    // once per island and reused across all bisection rounds.
    // ============================================================================

    /// <summary>评估项描述(索引->数据切片) / Evaluation item description (index -> buffer slices).</summary>
    internal struct ATOEvalItem
    {
        public int srcOffset;        // 源缓冲切片偏移 / source buffer slice offset
        public int upOffset;         // 上采样缓冲偏移 / upsample buffer offset
        public int momentsOffset;    // 矩缓冲偏移(每像素5矩) / moments buffer offset (5 per pixel)
        public int deSumOffset;      // ΔE 逐行部分和 / per-row partial sums for ΔE
        public int w, h;             // 比较分辨率 / comparison resolution
        public int category;         // 0=Color 1=Normal 2=Mask 3=Grayscale
        public int hasAlpha;
        public int cutout;
        public int blend;
        public int renderModeAnimated;
        public int usedChannels;
        public float sx, sy;
        public int gpu;              // 1=GPU重采样(阶段1跳过) / GPU resample (stage 1 skips this item)
    }

    /// <summary>
    /// 阶段1: 预乘线性下采样 + 上采样 / Stage 1: premultiplied linear down + up sampling.
    /// </summary>
    [BurstCompile]
    internal struct ATOEvalResampleJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<ATOEvalItem> items;
        [ReadOnly] public NativeArray<float> src;       // 预乘线性RGBA / premultiplied linear RGBA
        [ReadOnly] public NativeArray<float> srcAlpha;
        [ReadOnly] public NativeArray<float> srcSrgb;
        [ReadOnly] public NativeArray<float> srcNormal;
        [ReadOnly] public NativeArray<byte> mask;
        public NativeArray<float> up;                   // 上采样输出 / upsample output
        public NativeArray<float> upAlpha;
        public NativeArray<float> upNormal;

        public void Execute(int idx)
        {
            var item = items[idx];
            if (item.sx < 0 || item.gpu != 0) return; // 跳过/GPU路径 / skip / GPU path

            int n = item.w * item.h;
            int tw = (int)System.Math.Round(item.w * item.sx);
            int th = (int)System.Math.Round(item.h * item.sy);
            if (tw < 1) tw = 1;
            if (th < 1) th = 1;

            var upSlice = up.Slice(item.upOffset * 4, n * 4);
            var upASlice = upAlpha.Slice(item.upOffset, n);
            if (tw == item.w && th == item.h)
            {
                upSlice.CopyFrom(src.Slice(item.srcOffset, n * 4));
                upASlice.CopyFrom(srcAlpha.Slice(item.srcOffset, n));
                return;
            }

            var srcSlice = src.Slice(item.srcOffset * 4, n * 4);
            var srcASlice = srcAlpha.Slice(item.srcOffset, n);

            // 下采样(临时, 尺寸≤目标) / downsample (temp, bounded size)
            var small = new NativeArray<float>(tw * th * 4, Allocator.Temp);
            var smallA = new NativeArray<float>(tw * th, Allocator.Temp);
            Bilinear(srcSlice, item.w, item.h, small, tw, th);
            Bilinear1(srcASlice, item.w, item.h, smallA, tw, th);

            Bilinear(small, tw, th, upSlice, item.w, item.h);
            Bilinear1(smallA, tw, th, upASlice, item.w, item.h);

            if (item.category == 1 && srcNormal.Length > 0)
            {
                var srcNSlice = srcNormal.Slice(item.srcOffset * 3, n * 3);
                var smallN = new NativeArray<float>(tw * th * 3, Allocator.Temp);
                Bilinear3(srcNSlice, item.w, item.h, smallN, tw, th);
                for (int i = 0; i < tw * th; i++)
                {
                    float l = Sqrt(smallN[i * 3] * smallN[i * 3] + smallN[i * 3 + 1] * smallN[i * 3 + 1] + smallN[i * 3 + 2] * smallN[i * 3 + 2]);
                    if (l < 1e-6f) l = 1f;
                    smallN[i * 3] /= l;
                    smallN[i * 3 + 1] /= l;
                    smallN[i * 3 + 2] /= l;
                }

                var upNSlice = upNormal.Slice(item.upOffset * 3, n * 3);
                Bilinear3(smallN, tw, th, upNSlice, item.w, item.h);
                smallN.Dispose();
            }

            smallA.Dispose();
            small.Dispose();
        }

        // ---- 双线性 / bilinear ----
        public static void Bilinear(NativeArray<float> src, int sw, int sh, NativeArray<float> dst, int dw, int dh)
        {
            float rx = sw / (float)dw, ry = sh / (float)dh;
            for (int y = 0; y < dh; y++)
            {
                float fy = (y + 0.5f) * ry - 0.5f;
                int y0 = (int)System.Math.Floor(fy);
                float ty = fy - y0;
                int y0c = y0 < 0 ? 0 : (y0 >= sh ? sh - 1 : y0);
                int y1c = y0 + 1 < 0 ? 0 : (y0 + 1 >= sh ? sh - 1 : y0 + 1);
                for (int x = 0; x < dw; x++)
                {
                    float fx = (x + 0.5f) * rx - 0.5f;
                    int x0 = (int)System.Math.Floor(fx);
                    float tx = fx - x0;
                    int x0c = x0 < 0 ? 0 : (x0 >= sw ? sw - 1 : x0);
                    int x1c = x0 + 1 < 0 ? 0 : (x0 + 1 >= sw ? sw - 1 : x0 + 1);
                    for (int c = 0; c < 4; c++)
                    {
                        float v00 = src[(y0c * sw + x0c) * 4 + c];
                        float v10 = src[(y0c * sw + x1c) * 4 + c];
                        float v01 = src[(y1c * sw + x0c) * 4 + c];
                        float v11 = src[(y1c * sw + x1c) * 4 + c];
                        dst[(y * dw + x) * 4 + c] = (v00 * (1 - tx) + v10 * tx) * (1 - ty) + (v01 * (1 - tx) + v11 * tx) * ty;
                    }
                }
            }
        }

        public static void Bilinear1(NativeArray<float> src, int sw, int sh, NativeArray<float> dst, int dw, int dh)
        {
            float rx = sw / (float)dw, ry = sh / (float)dh;
            for (int y = 0; y < dh; y++)
            {
                float fy = (y + 0.5f) * ry - 0.5f;
                int y0 = (int)System.Math.Floor(fy);
                float ty = fy - y0;
                int y0c = y0 < 0 ? 0 : (y0 >= sh ? sh - 1 : y0);
                int y1c = y0 + 1 < 0 ? 0 : (y0 + 1 >= sh ? sh - 1 : y0 + 1);
                for (int x = 0; x < dw; x++)
                {
                    float fx = (x + 0.5f) * rx - 0.5f;
                    int x0 = (int)System.Math.Floor(fx);
                    float tx = fx - x0;
                    int x0c = x0 < 0 ? 0 : (x0 >= sw ? sw - 1 : x0);
                    int x1c = x0 + 1 < 0 ? 0 : (x0 + 1 >= sw ? sw - 1 : x0 + 1);
                    float v00 = src[y0c * sw + x0c];
                    float v10 = src[y0c * sw + x1c];
                    float v01 = src[y1c * sw + x0c];
                    float v11 = src[y1c * sw + x1c];
                    dst[y * dw + x] = (v00 * (1 - tx) + v10 * tx) * (1 - ty) + (v01 * (1 - tx) + v11 * tx) * ty;
                }
            }
        }

        public static void Bilinear3(NativeArray<float> src, int sw, int sh, NativeArray<float> dst, int dw, int dh)
        {
            float rx = sw / (float)dw, ry = sh / (float)dh;
            for (int y = 0; y < dh; y++)
            {
                float fy = (y + 0.5f) * ry - 0.5f;
                int y0 = (int)System.Math.Floor(fy);
                float ty = fy - y0;
                int y0c = y0 < 0 ? 0 : (y0 >= sh ? sh - 1 : y0);
                int y1c = y0 + 1 < 0 ? 0 : (y0 + 1 >= sh ? sh - 1 : y0 + 1);
                for (int x = 0; x < dw; x++)
                {
                    float fx = (x + 0.5f) * rx - 0.5f;
                    int x0 = (int)System.Math.Floor(fx);
                    float tx = fx - x0;
                    int x0c = x0 < 0 ? 0 : (x0 >= sw ? sw - 1 : x0);
                    int x1c = x0 + 1 < 0 ? 0 : (x0 + 1 >= sw ? sw - 1 : x0 + 1);
                    for (int c = 0; c < 3; c++)
                    {
                        float v00 = src[(y0c * sw + x0c) * 3 + c];
                        float v10 = src[(y0c * sw + x1c) * 3 + c];
                        float v01 = src[(y1c * sw + x0c) * 3 + c];
                        float v11 = src[(y1c * sw + x1c) * 3 + c];
                        dst[(y * dw + x) * 3 + c] = (v00 * (1 - tx) + v10 * tx) * (1 - ty) + (v01 * (1 - tx) + v11 * tx) * ty;
                    }
                }
            }
        }

        static float Sqrt(float v) { return (float)System.Math.Sqrt(v); }
    }

    /// <summary>
    /// 阶段2a: 掩码加权高斯矩水平趟, 每(评估项×行)并行 / Stage 2a: horizontal pass of the
    /// mask-weighted Gaussian moments, parallel over (eval × row). 输出 hMoments[每像素5矩].
    /// </summary>
    [BurstCompile]
    internal struct ATOGaussHJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<ATOEvalItem> items;
        [ReadOnly] public NativeArray<float> src;
        [ReadOnly] public NativeArray<float> up;
        [ReadOnly] public NativeArray<byte> mask;
        public NativeArray<float> hMoments;  // 5 float per pixel
        public int maxH;                     // 行跨度 / row stride

        public void Execute(int idx)
        {
            int eval = idx / maxH;
            int row = idx % maxH;
            if (eval >= items.Length) return;
            var item = items[eval];
            if (row >= item.h) return;

            int w = item.w, h = item.h;
            var srcSlice = src.Slice(item.srcOffset * 4, w * h * 4);
            var upSlice = up.Slice(item.upOffset * 4, w * h * 4);
            var maskSlice = mask.Slice(item.srcOffset, w * h);
            var hSlice = hMoments.Slice(item.momentsOffset * 5, w * h * 5);

            for (int x = 0; x < w; x++)
            {
                float wA = 0, wB = 0, wAA = 0, wBB = 0, wAB = 0, weight = 0;
                for (int t = 0; t < 11; t++)
                {
                    int xx = x + t - 5;
                    if (xx < 0) xx = 0;
                    if (xx >= w) xx = w - 1;
                    int pi = row * w + xx;
                    if (maskSlice[pi] == 0) continue;
                    float k = GaussK(t);
                    float aR = srcSlice[pi * 4], aG = srcSlice[pi * 4 + 1], aB = srcSlice[pi * 4 + 2];
                    float bR = upSlice[pi * 4], bG = upSlice[pi * 4 + 1], bB = upSlice[pi * 4 + 2];
                    wA += k * (aR + aG + aB) / 3f;
                    wB += k * (bR + bG + bB) / 3f;
                    wAA += k * (aR * aR + aG * aG + aB * aB) / 3f;
                    wBB += k * (bR * bR + bG * bG + bB * bB) / 3f;
                    wAB += k * (aR * bR + aG * bG + aB * bB) / 3f;
                    weight += k;
                }

                if (weight > 0)
                {
                    float inv = 1f / weight;
                    wA *= inv;
                    wB *= inv;
                    wAA *= inv;
                    wBB *= inv;
                    wAB *= inv;
                }

                int oi = (row * w + x) * 5;
                hSlice[oi] = wA;
                hSlice[oi + 1] = wB;
                hSlice[oi + 2] = wAA;
                hSlice[oi + 3] = wBB;
                hSlice[oi + 4] = wAB;
            }
        }

        static float GaussK(int t)
        {
            float x = t - 5;
            return (float)System.Math.Exp(-(x * x) / 4.5f);
        }
    }

    /// <summary>
    /// 阶段2b: 垂直趟 + 各指标逐行部分和, 每(评估项×行)并行 / Stage 2b: vertical pass plus
    /// per-row partial sums of all metrics, parallel over (eval × row).
    /// </summary>
    [BurstCompile]
    internal struct ATOGaussVJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<ATOEvalItem> items;
        [ReadOnly] public NativeArray<float> hMoments;
        public NativeArray<float> moments;   // 5 float per pixel
        [ReadOnly] public NativeArray<byte> mask;
        [ReadOnly] public NativeArray<float> src;
        [ReadOnly] public NativeArray<float> up;
        [ReadOnly] public NativeArray<float> srcSrgb;
        [ReadOnly] public NativeArray<float> srcAlpha;
        [ReadOnly] public NativeArray<float> upAlpha;
        [ReadOnly] public NativeArray<float> srcNormal;
        [ReadOnly] public NativeArray<float> upNormal;
        [ReadOnly] public NativeArray<float> cutoffs; // 每评估项16个 / 16 per eval (index 0 = min; -1 = unused)
        public int cutoffCount;
        public int maxH;                     // 部分和的统一行跨度 / uniform row stride for partial sums
        public NativeArray<float> deSums;    // [deSum, deCnt] per (eval, row)
        public NativeArray<float> iouInter;   // [inter, union] per (eval, row, cutoff)
        public NativeArray<float> alphaSum;   // [rmseSum, rmseCnt] per (eval, row)
        public NativeArray<float> normalSum;  // [angleSum, angleCnt] per (eval, row)
        public NativeArray<float> graySum;    // [sum, cnt] per (eval, row, channel)

        public void Execute(int idx)
        {
            int eval = idx / maxH;
            int row = idx % maxH;
            if (eval >= items.Length) return;
            var item = items[eval];
            if (row >= item.h) return;

            int w = item.w, h = item.h;
            var hSlice = hMoments.Slice(item.momentsOffset * 5, w * h * 5);
            var maskSlice = mask.Slice(item.srcOffset, w * h);
            var momentsSlice = moments.Slice(item.momentsOffset * 5, w * h * 5);

            // 垂直趟 / vertical pass
            for (int x = 0; x < w; x++)
            {
                float m1 = 0, m2 = 0, m3 = 0, m4 = 0, m5 = 0, weight = 0;
                for (int t = 0; t < 11; t++)
                {
                    int yy = row + t - 5;
                    if (yy < 0) yy = 0;
                    if (yy >= h) yy = h - 1;
                    int pi = yy * w + x;
                    if (maskSlice[pi] == 0) continue;
                    float k = GaussK(t);
                    m1 += k * hSlice[pi * 5];
                    m2 += k * hSlice[pi * 5 + 1];
                    m3 += k * hSlice[pi * 5 + 2];
                    m4 += k * hSlice[pi * 5 + 3];
                    m5 += k * hSlice[pi * 5 + 4];
                    weight += k;
                }

                int oi = (row * w + x) * 5;
                if (weight > 0)
                {
                    float inv = 1f / weight;
                    momentsSlice[oi] = m1 * inv;
                    momentsSlice[oi + 1] = m2 * inv;
                    momentsSlice[oi + 2] = m3 * inv;
                    momentsSlice[oi + 3] = m4 * inv;
                    momentsSlice[oi + 4] = m5 * inv;
                }
            }

            // ΔE 部分和(每行) / ΔE partial sums per row
            if (item.category == 0)
            {
                float deSum = 0;
                int deCnt = 0;
                // 每评估项的 cutoff 列表: 下标0为最小值(ΔE排除用) / per-eval cutoffs: index 0 is the min (ΔE exclusion)
                float cutoffMin = cutoffCount > 0 ? cutoffs[eval * 16] : 0f;
                int rowBase = eval * maxH;
                var srcSlice = src.Slice(item.srcOffset * 4, w * h * 4);
                var upSlice = up.Slice(item.upOffset * 4, w * h * 4);
                var srcSrgbSlice = srcSrgb.Slice(item.srcOffset * 4, w * h * 4);
                for (int x = 0; x < w; x++)
                {
                    int pi = row * w + x;
                    if (maskSlice[pi] == 0) continue;
                    float a0 = srcAlpha[item.srcOffset + pi];
                    float a1 = upAlpha[item.upOffset + pi];
                    if (item.cutout != 0 && (a0 <= cutoffMin || a1 <= cutoffMin)) continue;
                    float ur = Clamp01(upSlice[pi * 4] / (a1 < 1e-6f ? 1e-6f : a1));
                    float ug = Clamp01(upSlice[pi * 4 + 1] / (a1 < 1e-6f ? 1e-6f : a1));
                    float ub = Clamp01(upSlice[pi * 4 + 2] / (a1 < 1e-6f ? 1e-6f : a1));
                    ur = Clamp01(LinearToSrgb(ur));
                    ug = Clamp01(LinearToSrgb(ug));
                    ub = Clamp01(LinearToSrgb(ub));
                    deSum += DeltaE2000(srcSrgbSlice[pi * 4], srcSrgbSlice[pi * 4 + 1], srcSrgbSlice[pi * 4 + 2], ur, ug, ub);
                    deCnt++;
                }

                deSums[(rowBase + row) * 2] = deSum;
                deSums[(rowBase + row) * 2 + 1] = deCnt;
            }

            // alpha 指标部分和 / alpha partial sums
            int rowBase = eval * maxH;
            if (item.hasAlpha != 0 && (item.cutout != 0 || item.renderModeAnimated != 0))
            {
                for (int x = 0; x < w; x++)
                {
                    int pi = row * w + x;
                    if (maskSlice[pi] == 0) continue;
                    float a0 = srcAlpha[item.srcOffset + pi];
                    float a1 = upAlpha[item.upOffset + pi];
                    for (int c = 0; c < cutoffCount; c++)
                    {
                        float cut = cutoffs[eval * 16 + c];
                        if (cut < 0f) continue; // 未使用槽 / unused slot
                        bool ca = a0 >= cut, cb = a1 >= cut;
                        if (ca && cb) iouInter[((rowBase + row) * 16 + c) * 2] += 1;
                        if (ca || cb) iouInter[((rowBase + row) * 16 + c) * 2 + 1] += 1;
                    }
                }
            }

            if (item.hasAlpha != 0 && (item.blend != 0 || item.renderModeAnimated != 0))
            {
                for (int x = 0; x < w; x++)
                {
                    int pi = row * w + x;
                    if (maskSlice[pi] == 0) continue;
                    float d = srcAlpha[item.srcOffset + pi] - upAlpha[item.upOffset + pi];
                    alphaSum[(rowBase + row) * 2] += d * d;
                    alphaSum[(rowBase + row) * 2 + 1] += 1;
                }
            }

            // 法线角度部分和 / normal angle partial sums
            if (item.category == 1 && srcNormal.Length > 0)
            {
                var srcNSlice = srcNormal.Slice(item.srcOffset * 3, w * h * 3);
                var upNSlice = upNormal.Slice(item.upOffset * 3, w * h * 3);
                for (int x = 0; x < w; x++)
                {
                    int pi = row * w + x;
                    if (maskSlice[pi] == 0) continue;
                    float d = srcNSlice[pi * 3] * upNSlice[pi * 3] + srcNSlice[pi * 3 + 1] * upNSlice[pi * 3 + 1] + srcNSlice[pi * 3 + 2] * upNSlice[pi * 3 + 2];
                    d = d < -1f ? -1f : (d > 1f ? 1f : d);
                    normalSum[(rowBase + row) * 2] += Acos(d) * 57.29578f;
                    normalSum[(rowBase + row) * 2 + 1] += 1;
                }
            }

            // 灰度部分和(逐通道) / grayscale partial sums per channel
            if (item.category == 2 || item.category == 3)
            {
                var srcSlice = src.Slice(item.srcOffset * 4, w * h * 4);
                var upSlice = up.Slice(item.upOffset * 4, w * h * 4);
                for (int x = 0; x < w; x++)
                {
                    int pi = row * w + x;
                    if (maskSlice[pi] == 0) continue;
                    for (int ch = 0; ch < 4; ch++)
                    {
                        if ((item.usedChannels & (1 << ch)) == 0) continue;
                        float d = srcSlice[pi * 4 + ch] - upSlice[pi * 4 + ch];
                        graySum[((rowBase + row) * 4 + ch) * 2] += d * d;
                        graySum[((rowBase + row) * 4 + ch) * 2 + 1] += 1;
                    }
                }
            }
        }

        static float GaussK(int t)
        {
            float x = t - 5;
            return (float)System.Math.Exp(-(x * x) / 4.5f);
        }

        static float Clamp01(float v) { return v < 0 ? 0 : (v > 1 ? 1 : v); }
        static float LinearToSrgb(float c) { return c <= 0.0031308f ? c * 12.92f : 1.055f * Pow(c, 1f / 2.4f) - 0.055f; }
        static float Pow(float a, float b) { return (float)System.Math.Pow(a, b); }
        static float Acos(float v) { return (float)System.Math.Acos(v); }
        static float Sqrt(float v) { return (float)System.Math.Sqrt(v); }
        static float Abs(float v) { return System.Math.Abs(v); }
        static float Exp(float v) { return (float)System.Math.Exp(v); }
        static float Sin(float v) { return (float)System.Math.Sin(v); }
        static float Cos(float v) { return (float)System.Math.Cos(v); }
        static float Atan2(float y, float x) { return (float)System.Math.Atan2(y, x); }

        static float DeltaE2000(float r1, float g1, float b1, float r2, float g2, float b2)
        {
            float l1, a1, bb1, l2, a2, bb2;
            SrgbToLab(r1, g1, b1, out l1, out a1, out bb1);
            SrgbToLab(r2, g2, b2, out l2, out a2, out bb2);
            const float deg2rad = 0.017453292f;
            float C1 = Sqrt(a1 * a1 + bb1 * bb1);
            float C2 = Sqrt(a2 * a2 + bb2 * bb2);
            float Cbar = (C1 + C2) * 0.5f;
            float Cbar7 = Pow(Cbar, 7);
            float G = 0.5f * (1f - Sqrt(Cbar7 / (Cbar7 + 6103515625f)));
            float a1p = (1f + G) * a1;
            float a2p = (1f + G) * a2;
            float C1p = Sqrt(a1p * a1p + bb1 * bb1);
            float C2p = Sqrt(a2p * a2p + bb2 * bb2);
            float h1p = Hue(a1p, bb1);
            float h2p = Hue(a2p, bb2);
            float dLp = l2 - l1;
            float dCp = C2p - C1p;
            float dhp;
            if (C1p * C2p == 0) dhp = 0;
            else if (Abs(h2p - h1p) <= 180) dhp = h2p - h1p;
            else if (h2p - h1p > 180) dhp = h2p - h1p - 360;
            else dhp = h2p - h1p + 360;
            float dHp = 2f * Sqrt(C1p * C2p) * Sin(dhp * 0.5f * deg2rad);
            float Lbar = (l1 + l2) * 0.5f;
            float Cbarp = (C1p + C2p) * 0.5f;
            float hbarp;
            if (C1p * C2p == 0) hbarp = h1p + h2p;
            else if (Abs(h1p - h2p) <= 180) hbarp = (h1p + h2p) * 0.5f;
            else if (h1p + h2p < 360) hbarp = (h1p + h2p + 360) * 0.5f;
            else hbarp = (h1p + h2p - 360) * 0.5f;
            float T = 1f - 0.17f * Cos((hbarp - 30) * deg2rad) + 0.24f * Cos(2 * hbarp * deg2rad)
                          + 0.32f * Cos((3 * hbarp + 6) * deg2rad) - 0.20f * Cos((4 * hbarp - 63) * deg2rad);
            float dTheta = 30f * Exp(-Pow((hbarp - 275) / 25, 2));
            float Rc = 2f * Sqrt(Cbar7 / (Cbar7 + 6103515625f));
            float Rt = -Sin(2 * dTheta * deg2rad) * Rc;
            float Lm50sq = (Lbar - 50) * (Lbar - 50);
            float Sl = 1f + 0.015f * Lm50sq / Sqrt(20 + Lm50sq);
            float Sc = 1f + 0.045f * Cbarp;
            float Sh = 1f + 0.015f * Cbarp * T;
            float dL = dLp / Sl, dC = dCp / Sc, dH = dHp / Sh;
            return Sqrt(dL * dL + dC * dC + dH * dH + Rt * dC * dH);
        }

        static void SrgbToLab(float r, float g, float b, out float L, out float a, out float bb)
        {
            float x = 0.4124564f * r + 0.3575761f * g + 0.1804375f * b;
            float y = 0.2126729f * r + 0.7151522f * g + 0.0721750f * b;
            float z = 0.0193339f * r + 0.1191920f * g + 0.9503041f * b;
            float fx = F(x / 0.95047f);
            float fy = F(y);
            float fz = F(z / 1.08883f);
            L = 116f * fy - 16f;
            a = 500f * (fx - fy);
            bb = 200f * (fy - fz);
        }

        static float F(float t)
        {
            const float eps = 216f / 24389f;
            const float kappa = 24389f / 27f;
            return t > eps ? Pow(t, 1f / 3f) : (kappa * t + 16f) / 116f;
        }

        static float Hue(float a, float b)
        {
            if (a == 0 && b == 0) return 0;
            float h = Atan2(b, a) * 57.29578f;
            return h < 0 ? h + 360 : h;
        }
    }

    /// <summary>阶段3: 每评估项汇总指标 / Stage 3: per-eval metric reduction.</summary>
    [BurstCompile]
    internal struct ATOEvalReduceJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<ATOEvalItem> items;
        [ReadOnly] public NativeArray<float> moments;
        [ReadOnly] public NativeArray<byte> mask;
        [ReadOnly] public NativeArray<float> deSums;
        [ReadOnly] public NativeArray<float> iouInter;
        public int cutoffCount;
        public int maxH;
        [ReadOnly] public NativeArray<float> alphaSum;
        [ReadOnly] public NativeArray<float> normalSum;
        [ReadOnly] public NativeArray<float> graySum;
        [ReadOnly] public NativeArray<float> p95Work; // 工作区 / scratch (reserved)
        public NativeArray<float> results;             // 16 float per eval (ATOQResultLayout)

        public void Execute(int eval)
        {
            var item = items[eval];
            int n = item.w * item.h;
            int off = eval * ATOQResultLayout.PerIsland;
            for (int i = 0; i < ATOQResultLayout.PerIsland; i++) results[off + i] = 0;
            results[off + ATOQResultLayout.Ssim] = 1f;
            results[off + ATOQResultLayout.IoU] = 1f;

            if (item.category == 0)
            {
                int shortSide = item.w < item.h ? item.w : item.h;
                if (shortSide >= 11)
                {
                    // 单尺度SSIM(矩已含亮度项) / single-scale SSIM (moments include luminance)
                    float total = 0;
                    int cnt = 0;
                    var momentsSlice = moments.Slice(item.momentsOffset * 5, n * 5);
                    var maskSlice = mask.Slice(item.srcOffset, n);
                    const float c1 = 0.0001f, c2 = 0.0009f;
                    for (int i = 0; i < n; i++)
                    {
                        if (maskSlice[i] == 0) continue;
                        float ux = momentsSlice[i * 5], uy = momentsSlice[i * 5 + 1];
                        float sxx = momentsSlice[i * 5 + 2] - ux * ux;
                        float syy = momentsSlice[i * 5 + 3] - uy * uy;
                        float sxy = momentsSlice[i * 5 + 4] - ux * uy;
                        total += (2 * ux * uy + c1) * (2 * sxy + c2) / ((ux * ux + uy * uy + c1) * (sxx + syy + c2));
                        cnt++;
                    }

                    results[off + ATOQResultLayout.Ssim] = cnt > 0 ? total / cnt : 1f;
                }
                else
                {
                    results[off + ATOQResultLayout.Ssim] = -1f; // 不适用 / not applicable
                }

                // ΔE 汇总 / ΔE reduction
                float deSum = 0;
                int deCnt = 0;
                for (int r = 0; r < item.h; r++)
                {
                    deSum += deSums[(eval * maxH + r) * 2];
                    deCnt += (int)deSums[(eval * maxH + r) * 2 + 1];
                }

                results[off + ATOQResultLayout.De2000] = deCnt > 0 ? deSum / deCnt : 0f;
            }

            // alpha IoU / alpha RMSE
            if (item.hasAlpha != 0 && (item.cutout != 0 || item.renderModeAnimated != 0))
            {
                float best = 1f;
                for (int c = 0; c < cutoffCount; c++)
                {
                    float inter = 0, union = 0;
                    for (int r = 0; r < item.h; r++)
                    {
                        inter += iouInter[((eval * maxH + r) * 16 + c) * 2];
                        union += iouInter[((eval * maxH + r) * 16 + c) * 2 + 1];
                    }

                    float iou = union > 0 ? inter / union : 1f;
                    if (iou < best) best = iou;
                }

                results[off + ATOQResultLayout.IoU] = best;
            }

            if (item.hasAlpha != 0 && (item.blend != 0 || item.renderModeAnimated != 0))
            {
                float sum = 0;
                int cnt = 0;
                for (int r = 0; r < item.h; r++)
                {
                    sum += alphaSum[(eval * maxH + r) * 2];
                    cnt += (int)alphaSum[(eval * maxH + r) * 2 + 1];
                }

                results[off + ATOQResultLayout.AlphaRmse] = cnt > 0 ? (float)System.Math.Sqrt(sum / cnt) : 0f;
            }

            // 法线 / normals
            if (item.category == 1)
            {
                float sum = 0;
                int cnt = 0;
                for (int r = 0; r < item.h; r++)
                {
                    sum += normalSum[(eval * maxH + r) * 2];
                    cnt += (int)normalSum[(eval * maxH + r) * 2 + 1];
                }

                results[off + ATOQResultLayout.NormalMean] = cnt > 0 ? sum / cnt : 0f;
                // p95 由 ATOP95Job 计算 / p95 computed by ATOP95Job
                results[off + ATOQResultLayout.NormalP95] = 0f;
            }

            // 灰度 / grayscale
            if (item.category == 2 || item.category == 3)
            {
                float worst = 0;
                for (int ch = 0; ch < 4; ch++)
                {
                    if ((item.usedChannels & (1 << ch)) == 0) continue;
                    float sum = 0;
                    int cnt = 0;
                    for (int r = 0; r < item.h; r++)
                    {
                        sum += graySum[((eval * maxH + r) * 4 + ch) * 2];
                        cnt += (int)graySum[((eval * maxH + r) * 4 + ch) * 2 + 1];
                    }

                    if (cnt > 0)
                    {
                        float rmse = (float)System.Math.Sqrt(sum / cnt);
                        if (rmse > worst) worst = rmse;
                    }
                }

                results[off + ATOQResultLayout.GrayRmse] = worst;
            }

            results[off + ATOQResultLayout.Evaluated] = 1f;
        }
    }

    /// <summary>p95 角度(并行, 每评估项独立排序) / p95 angle (parallel, per-eval sort).</summary>
    [BurstCompile]
    internal struct ATOP95Job : IJobParallelFor
    {
        [ReadOnly] public NativeArray<ATOEvalItem> items;
        [ReadOnly] public NativeArray<float> srcNormal;
        [ReadOnly] public NativeArray<float> upNormal;
        [ReadOnly] public NativeArray<byte> mask;
        public NativeArray<float> results;

        public void Execute(int eval)
        {
            var item = items[eval];
            if (item.category != 1 || srcNormal.Length == 0) return;
            int n = item.w * item.h;
            var srcNSlice = srcNormal.Slice(item.srcOffset * 3, n * 3);
            var upNSlice = upNormal.Slice(item.upOffset * 3, n * 3);
            var maskSlice = mask.Slice(item.srcOffset, n);
            var angles = new NativeList<float>(n, Allocator.Temp);
            for (int i = 0; i < n; i++)
            {
                if (maskSlice[i] == 0) continue;
                float d = srcNSlice[i * 3] * upNSlice[i * 3] + srcNSlice[i * 3 + 1] * upNSlice[i * 3 + 1] + srcNSlice[i * 3 + 2] * upNSlice[i * 3 + 2];
                d = d < -1f ? -1f : (d > 1f ? 1f : d);
                angles.Add((float)System.Math.Acos(d) * 57.29578f);
            }

            if (angles.Length > 0)
            {
                var arr = new NativeArray<float>(angles.Length, Allocator.Temp);
                for (int i = 0; i < angles.Length; i++) arr[i] = angles[i];
                arr.Sort();
                int p95 = (int)System.Math.Floor(angles.Length * 0.95f);
                if (p95 >= angles.Length) p95 = angles.Length - 1;
                results[eval * ATOQResultLayout.PerIsland + ATOQResultLayout.NormalP95] = arr[p95];
                arr.Dispose();
            }

            angles.Dispose();
        }
    }

    /// <summary>质量结果布局 / Quality result layout.</summary>
    internal static class ATOQResultLayout
    {
        public const int PerIsland = 16;
        public const int Ssim = 0;
        public const int De2000 = 1;
        public const int IoU = 2;
        public const int AlphaRmse = 3;
        public const int NormalMean = 4;
        public const int NormalP95 = 5;
        public const int GrayRmse = 6;
        public const int Evaluated = 7;
    }
}
