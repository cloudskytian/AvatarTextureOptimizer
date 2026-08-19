using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Fosa.AvatarTextureOptimizer.Editor.Quality
{
    // Burst 质量评估作业：逐岛重采样往返 + 指标归约（MS-SSIM/ΔE2000/IoU/RMSE/角度 p95）。
    // Burst quality evaluation jobs: per-island resample round-trip + metric reduction.
    // 结果 = 最差指标比（value/threshold）；≤ 1 表示全部达标。Result = worst metric ratio; ≤ 1 means all pass.

    // 每个使用的静态数据。Static data of one use.
    public struct UseEvalData
    {
        public int texOffset, texW, texH;
        public int cropX, cropY, cropW, cropH;
        public int kind;       // ATOTextureKind
        public int alphaMode;  // ATOAlphaMode
        public int usedChannels; // 灰度通道掩码。Grayscale channel mask.
        public bool dxt5nm;
        public int cutoffCount;
        public unsafe fixed float cutoffs[8]; // 多材质引用时逐一评估。Evaluated per referencing material.
    }

    // 指标阈值（Burst 友好）。Metric thresholds (Burst-friendly).
    public struct BurstMetrics
    {
        public float msSsim, deltaE, alphaIoU, alphaRMSE, normalAngle, grayRMSE;
    }

    // 均匀缩放评估。Uniform-scale evaluation.
    [BurstCompile]
    public struct UniformEvalJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<half4> pool;
        [ReadOnly] public NativeArray<UseEvalData> uses;
        [ReadOnly] public NativeArray<float> scales;
        public BurstMetrics m;
        [WriteOnly] public NativeArray<float> margins;

        public void Execute(int i)
        {
            margins[i] = EvalUse(in pool, in uses[i], scales[i], scales[i], in m);
        }
    }

    // 活跃索引映射作业（外层并行调用单使用评估）。Active-index mapping job (parallel wrapper over single-use eval).
    [BurstCompile]
    public struct ActiveEvalJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<int> active;
        [ReadOnly] public NativeArray<half4> pool;
        [ReadOnly] public NativeArray<UseEvalData> uses;
        [ReadOnly] public NativeArray<float> scales;
        public BurstMetrics m;
        [WriteOnly] public NativeArray<float> margins;

        public void Execute(int i)
        {
            int idx = active[i];
            margins[idx] = EvalUse(in pool, in uses[idx], scales[idx], scales[idx], in m);
        }
    }

    // 双轴（各向异性）评估。Two-axis (anisotropic) evaluation.
    [BurstCompile]
    public struct AnisoEvalJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<half4> pool;
        [ReadOnly] public NativeArray<UseEvalData> uses;
        [ReadOnly] public NativeArray<int> useStart;   // 每岛使用的起始索引。Per-island use start index.
        [ReadOnly] public NativeArray<int> useCount;
        [ReadOnly] public NativeArray<float> scalesX, scalesY;
        public BurstMetrics m;
        [WriteOnly] public NativeArray<float> margins; // 每岛最差比值。Per-island worst ratio.

        public void Execute(int i)
        {
            float worst = 0f;
            int start = useStart[i], count = useCount[i];
            for (int u = 0; u < count; u++)
            {
                float margin = EvalUse(in pool, in uses[start + u], scalesX[i], scalesY[i], in m);
                worst = math.max(worst, margin);
            }
            margins[i] = worst;
        }
    }

    // 单使用评估：重采样往返 + 按类别/透明模式计算全部指标，返回最差比值。
    // Evaluates one use: resample round-trip + all metrics by kind/alpha mode; returns the worst ratio.
    internal static float EvalUse(in NativeArray<half4> pool, in UseEvalData u, float sx, float sy, in BurstMetrics m)
    {
        int w = u.cropW, h = u.cropH;
        int n = w * h;
        int tw = math.max(1, (int)(w * sx + 0.5f));
        int th = math.max(1, (int)(h * sy + 0.5f));

        float worst = 0f;

        if (u.kind == (int)Analysis.ATOTextureKind.NormalMap)
        {
            // 法线：解码 → 重采样 → 重归一化 → 角度误差 p95。
            // Normal: decode → resample → renormalize → angle error p95.
            var nrm = new NativeArray<float>(n * 3, Allocator.Temp);
            for (int i = 0; i < n; i++)
            {
                var p = pool[u.texOffset + (u.cropY + i / w) * u.texW + u.cropX + i % w];
                var v = QualityMath.DecodeNormalByte(
                    LinearByte(p.x), LinearByte(p.y), LinearByte(p.z), LinearByte(p.w), u.dxt5nm);
                nrm[i * 3] = v.x; nrm[i * 3 + 1] = v.y; nrm[i * 3 + 2] = v.z;
            }
            var cand = new NativeArray<float>(n * 3, Allocator.Temp);
            QualityMath.ResampleRoundTrip(nrm, cand, w, h, 3, tw, th);
            var angles = new NativeArray<float>(n, Allocator.Temp);
            for (int i = 0; i < n; i++)
            {
                var v = math.normalize(new float3(cand[i * 3], cand[i * 3 + 1], cand[i * 3 + 2]));
                var r = new float3(nrm[i * 3], nrm[i * 3 + 1], nrm[i * 3 + 2]);
                angles[i] = QualityMath.AngleDeg(r, v);
            }
            NativeSortExtension.Sort(angles);
            float p95 = angles[math.min(n - 1, (int)(n * 0.95f))];
            if (m.normalAngle > 0f) worst = math.max(worst, p95 / m.normalAngle);
            nrm.Dispose(); cand.Dispose(); angles.Dispose();
            return worst;
        }

        if (u.kind == (int)Analysis.ATOTextureKind.Grayscale || u.kind == (int)Analysis.ATOTextureKind.Mask)
        {
            // 灰度：仅被使用通道上的线性 RMSE，逐通道取最差。
            // Grayscale: linear RMSE on used channels only; the worst channel wins.
            var src = new NativeArray<float>(n * 3, Allocator.Temp);
            for (int i = 0; i < n; i++)
            {
                var p = pool[u.texOffset + (u.cropY + i / w) * u.texW + u.cropX + i % w];
                src[i * 3] = p.x; src[i * 3 + 1] = p.y; src[i * 3 + 2] = p.z;
            }
            var cand = new NativeArray<float>(n * 3, Allocator.Temp);
            QualityMath.ResampleRoundTrip(src, cand, w, h, 3, tw, th);
            float worstRmse = 0f;
            for (int c = 0; c < 3; c++)
            {
                if ((u.usedChannels & (1 << c)) == 0) continue;
                var ra = new NativeArray<float>(n, Allocator.Temp);
                var rb = new NativeArray<float>(n, Allocator.Temp);
                for (int i = 0; i < n; i++) { ra[i] = src[i * 3 + c]; rb[i] = cand[i * 3 + c]; }
                float rmse = QualityMath.Rmse(ra, rb, n);
                worstRmse = math.max(worstRmse, rmse);
                ra.Dispose(); rb.Dispose();
            }
            if (m.grayRMSE > 0f) worst = math.max(worst, worstRmse / m.grayRMSE);
            src.Dispose(); cand.Dispose();
            return worst;
        }

        // 颜色类（含透明）：预乘 alpha 线性域。Color kinds (incl. alpha): linear premultiplied domain.
        var src4 = new NativeArray<float>(n * 4, Allocator.Temp);
        for (int i = 0; i < n; i++)
        {
            var p = pool[u.texOffset + (u.cropY + i / w) * u.texW + u.cropX + i % w];
            src4[i * 4] = p.x; src4[i * 4 + 1] = p.y; src4[i * 4 + 2] = p.z; src4[i * 4 + 3] = p.w;
        }
        var cand4 = new NativeArray<float>(n * 4, Allocator.Temp);
        QualityMath.ResampleRoundTrip(src4, cand4, w, h, 4, tw, th);

        // MS-SSIM（RGB；短边 < 11px 忽略）。MS-SSIM on RGB; islands with short side < 11px skip.
        if (m.msSsim < 1f && math.min(w, h) >= 11)
        {
            var ra = new NativeArray<float>(n * 3, Allocator.Temp);
            var rb = new NativeArray<float>(n * 3, Allocator.Temp);
            for (int i = 0; i < n; i++)
            {
                ra[i * 3] = src4[i * 4]; ra[i * 3 + 1] = src4[i * 4 + 1]; ra[i * 3 + 2] = src4[i * 4 + 2];
                rb[i * 3] = cand4[i * 4]; rb[i * 3 + 1] = cand4[i * 4 + 1]; rb[i * 3 + 2] = cand4[i * 4 + 2];
            }
            float v = QualityMath.MsSsim(ra, rb, w, h, 3);
            worst = math.max(worst, (1f - v) / (1f - m.msSsim));
            ra.Dispose(); rb.Dispose();
        }

        // ΔE2000（alpha ≤ 1/255 的像素跳过）。ΔE2000 (pixels with alpha ≤ 1/255 are skipped).
        if (m.deltaE > 0f)
        {
            double sum = 0; int count = 0;
            for (int i = 0; i < n; i++)
            {
                float a0 = src4[i * 4 + 3];
                if (a0 <= 1f / 255f) continue;
                float3 l1 = QualityMath.LinearRgbToLab(src4[i * 4] / a0, src4[i * 4 + 1] / a0, src4[i * 4 + 2] / a0);
                float a1 = cand4[i * 4 + 3];
                if (a1 <= 1f / 255f) continue;
                float3 l2 = QualityMath.LinearRgbToLab(cand4[i * 4] / a1, cand4[i * 4 + 1] / a1, cand4[i * 4 + 2] / a1);
                sum += QualityMath.DeltaE2000(l1, l2);
                count++;
            }
            if (count > 0) worst = math.max(worst, (float)(sum / count) / m.deltaE);
        }

        // Alpha 指标：Cutout → 每个 cutoff 逐一评估 IoU；Blend → 线性 RMSE。
        // Alpha metrics: cutout → IoU per cutoff; blend → linear RMSE.
        if (u.alphaMode == (int)Analysis.ATOAlphaMode.Cutout)
        {
            unsafe
            {
                for (int k = 0; k < u.cutoffCount && k < 8; k++)
                {
                    float cut = u.cutoffs[k];
                    var ma = new NativeArray<bool>(n, Allocator.Temp);
                    var mb = new NativeArray<bool>(n, Allocator.Temp);
                    for (int i = 0; i < n; i++)
                    {
                        ma[i] = src4[i * 4 + 3] >= cut;
                        mb[i] = cand4[i * 4 + 3] >= cut;
                    }
                    float iou = QualityMath.IoU(ma, mb, n);
                    if (m.alphaIoU < 1f) worst = math.max(worst, (1f - iou) / (1f - m.alphaIoU));
                    ma.Dispose(); mb.Dispose();
                }
            }
        }
        else if (u.alphaMode == (int)Analysis.ATOAlphaMode.Blend)
        {
            var aa = new NativeArray<float>(n, Allocator.Temp);
            var ab = new NativeArray<float>(n, Allocator.Temp);
            for (int i = 0; i < n; i++) { aa[i] = src4[i * 4 + 3]; ab[i] = cand4[i * 4 + 3]; }
            float rmse = QualityMath.Rmse(aa, ab, n);
            if (m.alphaRMSE > 0f) worst = math.max(worst, rmse / m.alphaRMSE);
            aa.Dispose(); ab.Dispose();
        }

        src4.Dispose(); cand4.Dispose();
        return worst;
    }

    // half → 线性字节近似（法线解码用 8bit 输入）。half → linear byte approximation (for normal decode).
    private static byte LinearByte(float v)
    {
        return (byte)math.clamp((int)(v * 255f + 0.5f), 0, 255);
    }
}
