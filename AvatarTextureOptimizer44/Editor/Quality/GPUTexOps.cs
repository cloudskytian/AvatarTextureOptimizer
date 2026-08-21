// GPUTexOps.cs - Compute-shader driven resampling & metric reduction (plus Burst CPU metric fallback).
// 计算着色器驱动的重采样与指标归约（附Burst CPU指标兜底）。
// GPU memory discipline: per-evaluation RTs are GetTemporary/ReleaseTemporary immediately; the
// linear-source cache is bounded so long bisection loops never accumulate VRAM. / GPU内存纪律：
// 每次评估的RT即用即还；线性源缓存有界，长二分循环不会累积显存。
using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Fosa.ATO.Editor.Core;
using Fosa.ATO.Runtime;

namespace Fosa.ATO.Editor.Quality
{
    /// <summary>Aggregated comparison metrics of (reference, candidate). / （参考,候选）的汇总指标。</summary>
    public struct MetricResult
    {
        public float ssim;        // single- or multi-scale / 单尺度或多尺度
        public float dEMean, dEP95;
        public float alphaRmse;   // linear 0..1 / 线性
        public float alphaIou;    // cutout contour IoU / 剪影IoU
        public float nMeanDeg, nP95Deg;
        public float4 grayRmse;   // per channel / 每通道
    }

    /// <summary>One island/texture evaluation request. / 单个岛/贴图评估请求。</summary>
    public struct EvalTask
    {
        public Texture2D tex;          // source / 源贴图
        public RectInt region;         // island bbox in source pixels / 源像素中的岛包围盒
        public int dstW, dstH;         // candidate size / 候选尺寸
        public bool isNormal;
        public bool transparent;
        public float cutoff;           // cutout threshold / clip阈值
        public int ssimScales;         // 1 or 5 (MS) / 1或5（多尺度）
    }

    public sealed class GPUTexOps : IDisposable
    {
        private readonly GPUContext _gpu;
        private ComputeShader _cs;
        private ComputeBuffer _partials;
        private ComputeBuffer _scanBuf;
        private readonly Dictionary<Texture2D, RenderTexture> _linCache = new Dictionary<Texture2D, RenderTexture>();
        private readonly Queue<Texture2D> _linOrder = new Queue<Texture2D>();
        private const int LinCacheMax = 12;

        // MS-SSIM scale weights (Wang 2004) / 多尺度权重
        private static readonly float[] MsWeights = { 0.0448f, 0.2856f, 0.3001f, 0.2363f, 0.1333f };

        public GPUTexOps(GPUContext gpu) { _gpu = gpu; }

        private ComputeShader CS => _cs ??= _gpu.Compute("ATOQuality");

        /// <summary>Bounded cache of linearized source textures. / 有界的线性化源贴图缓存。</summary>
        public RenderTexture ToLinearRT(Texture2D tex)
        {
            if (_linCache.TryGetValue(tex, out var cached)) return cached;
            var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            rt.name = "ATO_lin_" + tex.name;
            var prev = RenderTexture.active;
            Graphics.Blit(tex, rt);
            RenderTexture.active = prev;
            _linCache[tex] = rt; _linOrder.Enqueue(tex);
            if (_linOrder.Count > LinCacheMax)
            {
                var evict = _linOrder.Dequeue();
                if (_linCache.TryGetValue(evict, out var old) && !ReferenceEquals(old, rt)) RenderTexture.ReleaseTemporary(old);
                _linCache.Remove(evict);
            }
            return rt;
        }

        /// <summary>Downsample a rect of src to dstW x dstH into a TEMPORARY RT (release yourself). / 将src内矩形降采样到临时RT（调用方负责释放）。</summary>
        public RenderTexture Downsample(RenderTexture src, RectInt region, int dstW, int dstH, bool normal, bool premult)
        {
            var dst = RenderTexture.GetTemporary(dstW, dstH, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            dst.name = "ATO_down";
            int gx = (dstW + 7) / 8, gy = (dstH + 7) / 8;
            var prev = RenderTexture.active;
            RenderTexture.active = dst;
            CS.SetTexture(0, "_Src", src);
            CS.SetTexture(0, "_Dst", dst);
            CS.SetVector("_SrcRect", new Vector4(region.x, region.y, region.width, region.height));
            CS.SetVector("_SrcTexSize", new Vector4(src.width, src.height, 0, 0));
            CS.SetVector("_DstSize", new Vector4(dstW, dstH, 0, 0));
            CS.SetFloat("_Mode", normal ? 1f : 0f);
            CS.SetFloat("_Premultiply", premult ? 1f : 0f);
            CS.Dispatch(0, gx, gy, 1);
            RenderTexture.active = prev;
            return dst;
        }

        /// <summary>Bilinear upsample a small RT back onto a rect of a full-size RT. / 将小RT双线性放回全尺寸RT的矩形。</summary>
        public void Upsample(RenderTexture small, RenderTexture dstFull, RectInt dstRect, bool normal)
        {
            int gx = (dstRect.width + 7) / 8, gy = (dstRect.height + 7) / 8;
            var prev = RenderTexture.active;
            RenderTexture.active = dstFull;
            CS.SetTexture(2, "_UpSrc", small);
            CS.SetTexture(2, "_UpDst", dstFull);
            CS.SetVector("_SrcRect", new Vector4(dstRect.x, dstRect.y, dstRect.width, dstRect.height));
            CS.SetFloat("_Mode", normal ? 1f : 0f);
            CS.Dispatch(2, gx, gy, 1);
            RenderTexture.active = prev;
        }

        /// <summary>Half-size pyramid step into a TEMPORARY RT. / 半尺寸金字塔步骤到临时RT。</summary>
        public RenderTexture PyrDown(RenderTexture src)
        {
            var dst = RenderTexture.GetTemporary(Mathf.Max(1, src.width / 2), Mathf.Max(1, src.height / 2), 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            dst.name = "ATO_pyr";
            int gx = (dst.width + 7) / 8, gy = (dst.height + 7) / 8;
            var prev = RenderTexture.active;
            RenderTexture.active = dst;
            CS.SetTexture(1, "_PyrSrc", src);
            CS.SetTexture(1, "_PyrDst", dst);
            CS.Dispatch(1, gx, gy, 1);
            RenderTexture.active = prev;
            return dst;
        }

        // ------------------------------------------------------------------
        // Metrics / 指标
        // ------------------------------------------------------------------

        /// <summary>Evaluate one task: down-up resample the region and compare. / 评估一个任务：区域降采样再升采样后比较。</summary>
        public MetricResult Evaluate(in EvalTask t, ATOTextureCategory cat)
        {
            if (!_gpu.IsAvailable) return CpuFallbackEvaluate(t);
            var src = ToLinearRT(t.tex);
            bool transparent = t.transparent && cat != ATOTextureCategory.NormalMap && cat != ATOTextureCategory.Grayscale;
            var down = Downsample(src, t.region, t.dstW, t.dstH, t.isNormal, transparent);
            var cmp = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            cmp.name = "ATO_cmp";
            Graphics.Blit(src, cmp);
            Upsample(down, cmp, t.region, t.isNormal);

            int mask = 0;
            if (t.ssimScales >= 1) mask |= 1;
            if (cat == ATOTextureCategory.Opaque || cat == ATOTextureCategory.Transparent) mask |= 2;
            if (cat == ATOTextureCategory.Transparent) mask |= 4;
            if (cat == ATOTextureCategory.NormalMap) mask |= 8;
            if (cat == ATOTextureCategory.Grayscale) mask |= 16;

            var r = Reduce(src, cmp, t.region, mask, t.cutoff, transparent);

            // MS-SSIM over pyramids / 金字塔多尺度
            if (t.ssimScales > 1)
            {
                float acc = 0, wsum = 0;
                RenderTexture refP = null, cmpP = null;
                var pyrRefs = new List<RenderTexture>();
                var pyrCmps = new List<RenderTexture>();
                try
                {
                    for (int s = 0; s < t.ssimScales; s++)
                    {
                        var rp = RectMin(t.region, 1 << s);
                        if (s > 0)
                        {
                            if (Mathf.Min(rp.width, rp.height) < 11) break; // window floor / 窗口下限
                            refP = PyrDown(refP ?? src); pyrRefs.Add(refP);
                            cmpP = PyrDown(cmpP ?? cmp); pyrCmps.Add(cmpP);
                        }
                        var m = s == 0 ? r.ssim : Reduce(refP, cmpP, rp, 1, 0.5f, false).ssim;
                        acc += MsWeights[s] * m; wsum += MsWeights[s];
                    }
                    if (wsum > 0) r.ssim = acc / wsum;
                }
                finally
                {
                    foreach (var x in pyrRefs) RenderTexture.ReleaseTemporary(x);
                    foreach (var x in pyrCmps) RenderTexture.ReleaseTemporary(x);
                }
            }
            RenderTexture.ReleaseTemporary(down);
            RenderTexture.ReleaseTemporary(cmp);
            return r;
        }

        private static RectInt RectMin(RectInt r, int div)
            => new RectInt(r.x / div, r.y / div, Mathf.Max(1, r.width / div), Mathf.Max(1, r.height / div));

        private MetricResult Reduce(RenderTexture re, RenderTexture cm, RectInt region, int mask, float cutoff, bool alphaWeight)
        {
            int w = region.width, h = region.height;
            int tx = (w + 7) / 8 * 8, ty = (h + 7) / 8 * 8;
            int count = tx * ty;
            if (_partials == null || _partials.count < count)
            {
                _partials?.Dispose();
                _partials = new ComputeBuffer(count, 292); // sizeof(Partials)=73 floats / 结构大小
            }
            CS.SetTexture(3, "_Ref", re);
            CS.SetTexture(3, "_Cmp", cm);
            CS.SetBuffer(3, "_Partials", _partials);
            CS.SetInts("_RegionOffset", region.x, region.y);
            CS.SetInts("_TexSize", w, h);
            CS.SetInt("_MetricMask", mask);
            CS.SetFloat("_Cutoff", cutoff);
            CS.SetInt("_AlphaWeight", alphaWeight ? 1 : 0);
            CS.SetInt("_ThreadCols", tx);
            CS.SetInt("_ThreadRows", ty);
            CS.SetInt("_PixelCount", w * h);
            CS.Dispatch(3, tx / 8, ty / 8, 1);
            var data = new float[count * 73];
            _partials.GetData(data);
            return MergePartials(data, count);
        }

        /// <summary>Merge thread partials on CPU. / CPU合并线程部分和。</summary>
        internal static MetricResult MergePartials(float[] d, int count)
        {
            var r = new MetricResult();
            double ssim = 0, cnt = 0, dE = 0, dEW = 0, aSq = 0, iouA = 0, iouB = 0, iouAB = 0;
            double g0 = 0, g1 = 0, g2 = 0, g3 = 0, nS = 0, nC = 0;
            var hDE = new double[24]; var hN = new double[36];
            const int F = 73;
            for (int i = 0; i < count; i++)
            {
                int o = i * F;
                ssim += d[o + 0]; cnt += d[o + 1];
                dE += d[o + 2]; dEW += d[o + 3];
                aSq += d[o + 4]; iouA += d[o + 5]; iouB += d[o + 6]; iouAB += d[o + 7];
                g0 += d[o + 8]; g1 += d[o + 9]; g2 += d[o + 10]; g3 += d[o + 11];
                nS += d[o + 12]; nC += d[o + 13];
                for (int b = 0; b < 24; b++) hDE[b] += d[o + 13 + b];
                for (int b = 0; b < 36; b++) hN[b] += d[o + 37 + b];
            }
            float n = Mathf.Max(1f, (float)cnt);
            r.ssim = (float)(ssim / n);
            r.dEMean = dEW > 0 ? (float)(dE / dEW) : 0;
            r.dEP95 = Percentile(hDE, 0.95, 1f);
            r.alphaRmse = Mathf.Sqrt((float)(aSq / n));
            r.alphaIou = (iouA + iouB - iouAB) > 0 ? (float)(iouAB / (iouA + iouB - iouAB)) : 1f;
            r.nMeanDeg = nC > 0 ? (float)(nS / nC) : 0;
            r.nP95Deg = Percentile(hN, 0.95, 1f);
            r.grayRmse = new float4(Mathf.Sqrt((float)(g0 / n)), Mathf.Sqrt((float)(g1 / n)), Mathf.Sqrt((float)(g2 / n)), Mathf.Sqrt((float)(g3 / n)));
            return r;
        }

        private static float Percentile(double[] hist, double p, float binWidth)
        {
            double total = 0; foreach (var v in hist) total += v;
            if (total <= 0) return 0;
            double target = total * p, acc = 0;
            for (int i = 0; i < hist.Length; i++)
            {
                acc += hist[i];
                if (acc >= target) return i * binWidth;
            }
            return hist.Length * binWidth;
        }

        // ------------------------------------------------------------------
        // Texture scans / 整图扫描
        // ------------------------------------------------------------------

        /// <summary>Scan alpha usage & grayscale-ness. / 扫描alpha使用与灰度性。</summary>
        public (bool usesAlpha, bool isGray) ScanTexture(Texture2D tex)
        {
            if (!_gpu.IsAvailable) return CpuScan(tex);
            var rt = ToLinearRT(tex);
            var cs = CS;
            int tx = (rt.width + 7) / 8 * 8, ty = (rt.height + 7) / 8 * 8;
            int count = tx * ty;
            if (_scanBuf == null || _scanBuf.count < count) { _scanBuf?.Dispose(); _scanBuf = new ComputeBuffer(count, 16); }
            cs.SetTexture(4, "_ScanSrc", rt);
            cs.SetBuffer(4, "_ScanOut", _scanBuf);
            cs.SetInts("_ScanThreadCols", tx);
            cs.SetInts("_ScanThreadRows", ty);
            cs.SetInt("_ScanPixelCount", rt.width * rt.height);
            cs.Dispatch(4, tx / 8, ty / 8, 1);
            var data = new float4[count];
            _scanBuf.GetData(data);
            float maxA = 0, maxD = 0;
            for (int i = 0; i < count; i++) { maxA = Mathf.Max(maxA, data[i].x); maxD = Mathf.Max(maxD, data[i].y); }
            return (maxA > 0.0038f, maxD <= 0.0038f);
        }

        private (bool, bool) CpuScan(Texture2D tex)
        {
            try
            {
                var px = tex.GetPixels32();
                bool alpha = false, gray = true;
                foreach (var p in px)
                {
                    if (p.a > 1) alpha = true;
                    if (Mathf.Abs(p.r - p.g) > 2 || Mathf.Abs(p.r - p.b) > 2) gray = false;
                    if (alpha && !gray) break;
                }
                return (alpha, gray);
            }
            catch { return (tex.format.ToString().Contains("Alpha"), true); }
        }

        /// <summary>Is an island region a pure color? Compare 1x1 mean spread. / 岛区域是否纯色？以1x1均值方差判断。</summary>
        public bool IsPureColor(Texture2D tex, RectInt region)
        {
            var src = ToLinearRT(tex);
            var one = Downsample(src, region, 1, 1, false, true);
            var back = RenderTexture.GetTemporary(region.width, region.height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            back.name = "ATO_pure";
            var prev = RenderTexture.active;
            RenderTexture.active = back;
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = prev;
            Upsample(one, back, new RectInt(0, 0, region.width, region.height), false);
            var d = Reduce(src, back, new RectInt(0, 0, region.width, region.height), 16, 0.5f, false);
            RenderTexture.ReleaseTemporary(one);
            RenderTexture.ReleaseTemporary(back);
            float rmse = math.cmax(d.grayRmse);
            return rmse < 1f / 255f; // flat within 1 LSB / 1 LSB内视为纯色
        }

        public void Dispose()
        {
            _partials?.Dispose(); _scanBuf?.Dispose(); _partials = null; _scanBuf = null;
            foreach (var kv in _linCache) RenderTexture.ReleaseTemporary(kv.Value);
            _linCache.Clear(); _linOrder.Clear();
        }

        // ------------------------------------------------------------------
        // CPU fallback / CPU兜底
        // ------------------------------------------------------------------

        private MetricResult CpuFallbackEvaluate(in EvalTask t)
        {
            var rt = RenderTexture.GetTemporary(t.tex.width, t.tex.height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            Graphics.Blit(t.tex, rt);
            var prev = RenderTexture.active;
            var tex = new Texture2D(t.tex.width, t.tex.height, TextureFormat.RGBAFloat, false, true);
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply(false);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            var px = tex.GetPixels();
            var result = new NativeArray<MetricResult>(1, Allocator.TempJob);
            var job = new CpuMetricJob
            {
                Px = new NativeArray<Color>(px, Allocator.TempJob),
                W = t.tex.width, H = t.tex.height,
                RegionX = t.region.x, RegionY = t.region.y, RegionW = t.region.width, RegionH = t.region.height,
                DstW = t.dstW, DstH = t.dstH,
                Result = result,
            };
            job.Schedule().Complete();
            var r = result[0];
            job.Px.Dispose(); result.Dispose(); UnityEngine.Object.DestroyImmediate(tex);
            return r;
        }

        /// <summary>Burst CPU metric job (simplified SSIM; used only without compute support). / Burst CPU指标作业（简化SSIM；仅在无计算着色器时使用）。</summary>
        [BurstCompile]
        private struct CpuMetricJob : IJob
        {
            public NativeArray<Color> Px; // full original linear / full original linear
            public int W, H, RegionX, RegionY, RegionW, RegionH, DstW, DstH;
            public NativeArray<MetricResult> Result;

            public void Execute()
            {
                int n = RegionW * RegionH;
                var cand = new NativeArray<Color>(n, Allocator.Temp);
                for (int i = 0; i < n; i++)
                {
                    int x = i % RegionW, y = i / RegionW;
                    // bilinear sample of the resampled image / sample the resampled image bilinearly
                    float u = (x + 0.5f) / RegionW * DstW - 0.5f;
                    float v = (y + 0.5f) / RegionH * DstH - 0.5f;
                    cand[i] = SampleRegion(u, v);
                }
                double ssim = 0, dE = 0, dEW = 0, aSq = 0, iouA = 0, iouB = 0, iouAB = 0;
                double g0 = 0, g1 = 0, g2 = 0, g3 = 0, nS = 0, nC = 0;
                var hDE = new NativeArray<double>(24, Allocator.Temp);
                var hN = new NativeArray<double>(36, Allocator.Temp);
                for (int i = 0; i < n; i++)
                {
                    var o = Px[(RegionY + i / RegionW) * W + (RegionX + i % RegionW)];
                    var c = cand[i];
                    float w = math.saturate(o.a * 4f);
                    ssim += LocalSSIM(cand, i);
                    float de = DeltaE2000(RgbToLab(new float3(o.r, o.g, o.b)), RgbToLab(new float3(c.r, c.g, c.b)));
                    dE += de * w; dEW += w;
                    hDE[math.min(23, (int)math.floor((float)de))] += w;
                    float da = o.a - c.a;
                    aSq += da * da;
                    bool ca = o.a > 0.5f, cb = c.a > 0.5f; // default cutoff / default threshold
                    iouA += ca ? 1 : 0; iouB += cb ? 1 : 0; iouAB += (ca && cb) ? 1 : 0;
                    g0 += (o.r - c.r) * (o.r - c.r); g1 += (o.g - c.g) * (o.g - c.g);
                    g2 += (o.b - c.b) * (o.b - c.b); g3 += da * da;
                    float3 rn = math.normalize(new float3(o.r, o.g, o.b) * 2f - 1f);
                    float3 cn = math.normalize(new float3(c.r, c.g, c.b) * 2f - 1f);
                    float ang = math.degrees(math.acos(math.clamp(math.dot(rn, cn), -1f, 1f)));
                    nS += ang; nC += 1;
                    hN[math.min(35, (int)math.floor(ang))] += 1;
                }
                float N = math.max(1, n);
                Result[0] = new MetricResult
                {
                    ssim = (float)(ssim / N),
                    dEMean = dEW > 0 ? (float)(dE / dEW) : 0,
                    dEP95 = HistP95(hDE, N),
                    alphaRmse = math.sqrt((float)(aSq / N)),
                    alphaIou = (iouA + iouB - iouAB) > 0 ? (float)(iouAB / (iouA + iouB - iouAB)) : 1f,
                    nMeanDeg = nC > 0 ? (float)(nS / nC) : 0,
                    nP95Deg = HistP95(hN, (float)nC),
                    grayRmse = new float4(math.sqrt((float)(g0 / N)), math.sqrt((float)(g1 / N)), math.sqrt((float)(g2 / N)), math.sqrt((float)(g3 / N))),
                };
                cand.Dispose(); hDE.Dispose(); hN.Dispose();
            }

            private static float HistP95(NativeArray<double> h, float total)
            {
                double target = total * 0.95, acc = 0;
                for (int i = 0; i < h.Length; i++) { acc += h[i]; if (acc >= target) return i; }
                return h.Length;
            }

            /// <summary>Single-scale SSIM (11x11 gaussian, sigma 1.5) at index i of the region. / Region index i's single-scale SSIM (11x11 Gaussian).</summary>
            private Color SampleRegion(float u, float v)
            {
                // sample the DstW x DstH version implicitly via rate scaling / implicitly sample the downscaled image via scaling ratio
                float rx = (u + 0.5f) / DstW * RegionW, ry = (v + 0.5f) / DstH * RegionH;
                int x0 = math.clamp((int)rx, 0, RegionW - 1), y0 = math.clamp((int)ry, 0, RegionH - 1);
                int x1 = math.min(x0 + 1, RegionW - 1), y1 = math.min(y0 + 1, RegionH - 1);
                float fx = math.saturate(rx - x0), fy = math.saturate(ry - y0);
                var a = Px[(RegionY + y0) * W + (RegionX + x0)];
                var b = Px[(RegionY + y0) * W + (RegionX + x1)];
                var c = Px[(RegionY + y1) * W + (RegionX + x0)];
                var d = Px[(RegionY + y1) * W + (RegionX + x1)];
                return Color.Lerp(Color.Lerp(a, b, fx), Color.Lerp(c, d, fx), fy);
            }

            private float LocalSSIM(NativeArray<Color> cand, int i)
            {
                const float C1 = 0.01f * 0.01f, C2 = 0.03f * 0.03f;
                int x = i % RegionW, y = i / RegionW;
                double muX = 0, muY = 0, sXX = 0, sYY = 0, sXY = 0, wsum = 0;
                for (int dy = -5; dy <= 5; dy++)
                    for (int dx = -5; dx <= 5; dx++)
                    {
                        int qx = math.clamp(x + dx, 0, RegionW - 1), qy = math.clamp(y + dy, 0, RegionH - 1);
                        var o = Px[(RegionY + qy) * W + (RegionX + qx)];
                        var c = cand[qy * RegionW + qx];
                        float lx = o.r * 0.2126f + o.g * 0.7152f + o.b * 0.0722f;
                        float ly = c.r * 0.2126f + c.g * 0.7152f + c.b * 0.0722f;
                        float gx = math.exp(-(dx * dx + dy * dy) / 4.5f); // sigma^2*2 = 4.5 / 2*sigma^2=4.5
                        muX += gx * lx; muY += gx * ly;
                        sXX += gx * lx * lx; sYY += gx * ly * ly; sXY += gx * lx * ly;
                        wsum += gx;
                    }
                muX /= wsum; muY /= wsum;
                sXX = sXX / wsum - muX * muX; sYY = sYY / wsum - muY * muY; sXY = sXY / wsum - muX * muY;
                return (float)(((2 * muX * muY + C1) * (2 * sXY + C2)) / ((muX * muX + muY * muY + C1) * (sXX + sYY + C2)));
            }

            /// <summary>CIEDE2000 (Sharma 2005). / CIEDE2000（Sharma 2005）。</summary>
            internal static float DeltaE2000(float3 lab1, float3 lab2)
            {
                float L1 = lab1.x, a1 = lab1.y, b1 = lab1.z, L2 = lab2.x, a2 = lab2.y, b2 = lab2.z;
                float C1 = math.sqrt(a1 * a1 + b1 * b1), C2 = math.sqrt(a2 * a2 + b2 * b2);
                float Cb = 0.5f * (C1 + C2);
                float Cb7 = math.pow(Cb, 7);
                float G = 0.5f * (1 - math.sqrt(Cb7 / (Cb7 + 6103515625f)));
                float ap1 = (1 + G) * a1, ap2 = (1 + G) * a2;
                float Cp1 = math.sqrt(ap1 * ap1 + b1 * b1), Cp2 = math.sqrt(ap2 * ap2 + b2 * b2);
                float hp1 = (ap1 == 0 && b1 == 0) ? 0 : math.degrees(math.atan2(b1, ap1));
                if (hp1 < 0) hp1 += 360;
                float hp2 = (ap2 == 0 && b2 == 0) ? 0 : math.degrees(math.atan2(b2, ap2));
                if (hp2 < 0) hp2 += 360;
                float dLp = L2 - L1, dCp = Cp2 - Cp1, dhp = 0;
                if (Cp1 * Cp2 != 0)
                {
                    dhp = hp2 - hp1;
                    if (dhp > 180) dhp -= 360; else if (dhp < -180) dhp += 360;
                }
                float dHp = 2 * math.sqrt(Cp1 * Cp2) * math.sin(math.radians(dhp) / 2);
                float Lbp = 0.5f * (L1 + L2), Cbp = 0.5f * (Cp1 + Cp2), hbp;
                if (Cp1 * Cp2 == 0) hbp = hp1 + hp2;
                else
                {
                    hbp = 0.5f * (hp1 + hp2);
                    if (math.abs(hp1 - hp2) > 180) { if (hp1 + hp2 < 360) hbp += 180; else hbp -= 180; }
                }
                float T = 1 - 0.17f * math.cos(math.radians(hbp - 30)) + 0.24f * math.cos(math.radians(2 * hbp))
                        + 0.32f * math.cos(math.radians(3 * hbp + 6)) - 0.20f * math.cos(math.radians(4 * hbp - 63));
                float dTheta = 30 * math.exp(-((hbp - 275) / 25) * ((hbp - 275) / 25));
                float Cbp7 = math.pow(Cbp, 7);
                float Rc = 2 * math.sqrt(Cbp7 / (Cbp7 + 6103515625f));
                float Lm50 = (Lbp - 50) * (Lbp - 50);
                float Sl = 1 + 0.015f * Lm50 / math.sqrt(20 + Lm50);
                float Sc = 1 + 0.045f * Cbp;
                float Sh = 1 + 0.015f * Cbp * T;
                float Rt = -math.sin(math.radians(2 * dTheta)) * Rc;
                float tL = dLp / Sl, tC = dCp / Sc, tH = dHp / Sh;
                return math.sqrt(math.max(0, tL * tL + tC * tC + tH * tH + Rt * tC * tH));
            }

            /// <summary>linear sRGB -&gt; Lab (D65). / linear sRGB to Lab (D65).</summary>
            internal static float3 RgbToLab(float3 c)
            {
                float X = math.dot(c, new float3(0.4124f, 0.3576f, 0.1805f)) / 0.95047f;
                float Y = math.dot(c, new float3(0.2126f, 0.7152f, 0.0722f));
                float Z = math.dot(c, new float3(0.0193f, 0.1192f, 0.9505f)) / 1.08883f;
                float3 v = new float3(X, Y, Z);
                float3 f = v > 0.008856f ? math.pow(v, 1f / 3f) : 7.787f * v + 16f / 116f;
                return new float3(116 * f.y - 16, 500 * (f.x - f.y), 200 * (f.y - f.z));
            }
        }
    }
}
