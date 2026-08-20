// ATOGpuMetrics.cs — GPU 质量评估驱动（ComputeShader）/ GPU quality evaluation driver (ComputeShader).
// 说明：与 Burst CPU 参考实现（ATOMetrics）逐项对应：线性空间重采样、SSIM/MS-SSIM、CIEDE2000、
// 法线角度、灰度平方差、alpha 对。统计归约（p95/均值/RMSE/IoU）在 CPU 侧完成，保证两路径阈值判定一致。
// GPU 不可用时自动回退 CPU。所有 RenderTexture 经对象池复用，会话结束时统一释放，避免泄漏。
// Note: mirrors the Burst CPU reference (ATOMetrics) item by item: linear-space resampling, SSIM/MS-SSIM,
// CIEDE2000, normal angles, grayscale squared diffs, alpha pairs. Statistical reductions run on the CPU side
// so both paths share identical threshold semantics. Falls back to CPU when GPU is unavailable. RenderTextures
// are pooled and released at session end to avoid leaks.

using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>GPU 质量评估器。/ GPU quality evaluator.</summary>
    internal sealed class ATOGpuMetrics : IDisposable
    {
        private ComputeShader _shader;
        private readonly Dictionary<(int, int), RenderTexture> _pool = new Dictionary<(int, int), RenderTexture>();
        private readonly Dictionary<(int, int), RenderTexture> _floatPool = new Dictionary<(int, int), RenderTexture>();
        private bool _available;

        // 内核 ID 缓存 / kernel id cache
        private int _kBoxHalf, _kBilinear, _kGaussH, _kGaussV, _kLuma, _kSsim, _kFds, _kFpd, _kDeltaE, _kNormalAngle, _kGray, _kAlpha, _kPullPush;

        public bool Available => _available;

        /// <summary>加载 ComputeShader 并检测可用性。/ Load the ComputeShader and detect availability.</summary>
        public ATOGpuMetrics()
        {
            try
            {
                _available = SystemInfo.supportsComputeShaders && SystemInfo.supportsAsyncGPUReadback;
                if (!_available) return;
                var path = ATOAssetLocator.Find("Editor/ATOCompute.compute");
                if (string.IsNullOrEmpty(path)) { _available = false; return; }
                _shader = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
                if (_shader == null) { _available = false; return; }
                _kBoxHalf = _shader.FindKernel("CSBoxHalf");
                _kBilinear = _shader.FindKernel("CSBilinear");
                _kGaussH = _shader.FindKernel("CSGaussianH");
                _kGaussV = _shader.FindKernel("CSGaussianV");
                _kLuma = _shader.FindKernel("CSLuma");
                _kSsim = _shader.FindKernel("CSSsimCombine");
                _kFds = _shader.FindKernel("CSFloatDiffSq");
                _kFpd = _shader.FindKernel("CSFloatProdDiff");
                _kDeltaE = _shader.FindKernel("CSDeltaE");
                _kNormalAngle = _shader.FindKernel("CSNormalAngle");
                _kGray = _shader.FindKernel("CSGraySqDiff");
                _kAlpha = _shader.FindKernel("CSAlphaPair");
                _kPullPush = _shader.FindKernel("CSPullPush");
            }
            catch (Exception e)
            {
                _available = false;
                ATOLog.Warning($"GPU metrics unavailable, falling back to CPU: {e.Message} (GPU 不可用，回退 CPU)");
            }
        }

        private RenderTexture GetRgba(int w, int h)
        {
            var key = (w, h);
            if (!_pool.TryGetValue(key, out var rt) || rt == null)
            {
                rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
                {
                    enableRandomWrite = true, useMipMap = false, autoGenerateMips = false,
                    filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave,
                };
                rt.Create();
                _pool[key] = rt;
            }
            return rt;
        }

        private RenderTexture GetFloat(int w, int h)
        {
            var key = (w, h);
            if (!_floatPool.TryGetValue(key, out var rt) || rt == null)
            {
                rt = new RenderTexture(w, h, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear)
                {
                    enableRandomWrite = true, useMipMap = false, autoGenerateMips = false,
                    filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave,
                };
                rt.Create();
                _floatPool[key] = rt;
            }
            return rt;
        }

        private void Upload(float4[] data, int w, int h, RenderTexture rt)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBAFloat, false, true);
            tex.hideFlags = HideFlags.HideAndDontSave;
            tex.SetPixelData(data, 0);
            tex.Apply(false, false);
            Graphics.Blit(tex, rt);
            UnityEngine.Object.DestroyImmediate(tex);
        }

        private NativeArray<T> Readback<T>(RenderTexture rt, Allocator alloc) where T : struct
        {
            var req = AsyncGPUReadback.Request(rt, 0, typeof(T) == typeof(float4) ? TextureFormat.RGBAFloat : TextureFormat.RFloat, null);
            req.WaitForCompletion();
            if (req.hasError)
                throw new InvalidOperationException("GPU readback failed");
            var arr = req.GetData<T>();
            return new NativeArray<T>(arr, alloc);
        }

        /// <summary>GPU 评估（与 ATOMetrics.Evaluate 同语义）。/ GPU evaluation (same semantics as ATOMetrics.Evaluate).</summary>
        public ATOEvalResult Evaluate(ATOEvalInput input, Allocator alloc)
        {
            var result = new ATOEvalResult { pass = true };
            if (!_available) return ATOMetrics.Evaluate(ref input, alloc);

            try
            {
                // 上传源图 / upload source
                var srcData = new float4[input.srcW * input.srcH];
                input.source.CopyTo(srcData);
                var srcRT = GetRgba(input.srcW, input.srcH);
                Upload(srcData, input.srcW, input.srcH, srcRT);

                // 缩放到候选尺寸 / resize to candidate
                var scaledRT = GpuResize(srcRT, input.srcW, input.srcH, input.dstW, input.dstH);
                // 上采样回原尺寸 / upsample back to original size
                var upRT = GpuResize(scaledRT, input.dstW, input.dstH, input.srcW, input.srcH);

                if (input.normalMap)
                {
                    var angles = GpuNormalAngle(srcRT, upRT, input.srcW, input.srcH, alloc);
                    result.normalAngleMean = ATOMetrics.Mean(angles);
                    result.normalAngleP95 = ATOMetrics.Percentile95(angles);
                    angles.Dispose();
                    var limit = input.thresholds.lossless ? 0f : input.thresholds.normalAngleP95;
                    if (result.normalAngleP95 > limit + 1e-6f) { result.pass = false; result.failReason = "normal angle"; }
                }
                else if (input.grayEval)
                {
                    var diffs = GpuGrayDiff(srcRT, upRT, input.srcW, input.srcH, alloc);
                    result.grayRmse = GrayRmseFromSquares(diffs);
                    diffs.Dispose();
                    var limit = input.thresholds.lossless ? 0f : input.thresholds.grayLinearRmse;
                    if (result.grayRmse > limit + 1e-6f) { result.pass = false; result.failReason = "gray rmse"; }
                }
                else
                {
                    if (input.srcW >= 11 && input.srcH >= 11)
                    {
                        var multi = input.srcW >= 176 && input.srcH >= 176;
                        result.msSsim = multi
                            ? GpuMsSsim(srcRT, upRT, input.srcW, input.srcH, alloc)
                            : GpuSsim(srcRT, upRT, input.srcW, input.srcH, alloc);
                        if (input.thresholds.lossless)
                        {
                            if (result.msSsim < 1f - 1e-5f) { result.pass = false; result.failReason = "ms-ssim"; }
                        }
                        else if (result.msSsim < input.thresholds.msSsim)
                        {
                            result.pass = false;
                            result.failReason = "ms-ssim";
                        }
                    }
                    var de = GpuDeltaE(srcRT, upRT, input.srcW, input.srcH, alloc);
                    result.deltaEP95 = ATOMetrics.Percentile95(de);
                    de.Dispose();
                    var deLimit = input.thresholds.lossless ? 0f : input.thresholds.deltaEP95;
                    if (result.deltaEP95 > deLimit + 1e-6f) { result.pass = false; result.failReason = "ΔE"; }
                }

                // 透明度评估 / alpha evaluation
                if ((input.alphaFlags & ATOAlphaUsage.Cutout) != 0 && input.cutoffs.Length > 0)
                {
                    var pairs = GpuAlphaPairs(srcRT, upRT, input.srcW, input.srcH, alloc);
                    result.alphaIoU = 1f;
                    for (int i = 0; i < input.cutoffs.Length; i++)
                    {
                        var iou = AlphaIoU(pairs, input.cutoffs[i]);
                        if (iou < result.alphaIoU) result.alphaIoU = iou;
                    }
                    if ((input.alphaFlags & ATOAlphaUsage.Blend) != 0)
                        result.alphaRmse = AlphaRmse(pairs);
                    pairs.Dispose();
                    if (input.thresholds.lossless)
                    {
                        if (result.alphaIoU < 1f - 1e-5f) { result.pass = false; result.failReason = "alpha IoU"; }
                    }
                    else if (result.alphaIoU < input.thresholds.alphaIoU)
                    {
                        result.pass = false;
                        result.failReason = "alpha IoU";
                    }
                    if ((input.alphaFlags & ATOAlphaUsage.Blend) != 0)
                    {
                        var limit = input.thresholds.lossless ? 0f : input.thresholds.alphaLinearRmse;
                        if (result.alphaRmse > limit + 1e-6f) { result.pass = false; result.failReason = "alpha rmse"; }
                    }
                }
                else if ((input.alphaFlags & ATOAlphaUsage.Blend) != 0)
                {
                    var pairs = GpuAlphaPairs(srcRT, upRT, input.srcW, input.srcH, alloc);
                    result.alphaRmse = AlphaRmse(pairs);
                    pairs.Dispose();
                    var limit = input.thresholds.lossless ? 0f : input.thresholds.alphaLinearRmse;
                    if (result.alphaRmse > limit + 1e-6f) { result.pass = false; result.failReason = "alpha rmse"; }
                }
            }
            catch (Exception e)
            {
                ATOLog.Warning($"GPU evaluation failed ({e.Message}); retrying on CPU. (GPU 评估失败，改用 CPU)");
                return ATOMetrics.Evaluate(ref input, alloc);
            }
            return result;
        }

        // ---------------- 子步骤 / sub-steps ----------------

        private RenderTexture GpuResize(RenderTexture src, int sw, int sh, int dw, int dh)
        {
            var cur = src;
            var cw = sw;
            var ch = sh;
            while (cw / 2 >= dw && ch / 2 >= dh && cw > 2 && ch > 2)
            {
                var half = GetRgba(cw / 2, ch / 2);
                _shader.SetTexture(_kBoxHalf, "_BoxSrc", cur);
                _shader.SetTexture(_kBoxHalf, "_BoxSrcRead", cur);
                _shader.SetTexture(_kBoxHalf, "_BoxDst", half);
                _shader.Dispatch(_kBoxHalf, Mathf.CeilToInt(cw / 2f / 8f), Mathf.CeilToInt(ch / 2f / 8f), 1);
                cur = half;
                cw /= 2;
                ch /= 2;
            }
            if (cw != dw || ch != dh)
            {
                var dst = GetRgba(dw, dh);
                _shader.SetTexture(_kBilinear, "_BiSrc", cur);
                _shader.SetVector(_kBilinear, "_BiSrcSize", new Vector4(cw, ch, 0, 0));
                _shader.SetVector(_kBilinear, "_BiInvSrcSize", new Vector4(1f / cw, 1f / ch, 0, 0));
                _shader.SetTexture(_kBilinear, "_BiDst", dst);
                _shader.Dispatch(_kBilinear, Mathf.CeilToInt(dw / 8f), Mathf.CeilToInt(dh / 8f), 1);
                return dst;
            }
            return cur;
        }

        private float GpuSsim(RenderTexture a, RenderTexture b, int w, int h, Allocator alloc)
        {
            var la = GetFloat(w, h);
            var lb = GetFloat(w, h);
            BlitLuma(a, la, w, h);
            BlitLuma(b, lb, w, h);
            return SsimOnLumaGpu(la, lb, w, h, alloc);
        }

        private float GpuMsSsim(RenderTexture a, RenderTexture b, int w, int h, Allocator alloc)
        {
            float acc = 0f;
            float sum = 0f;
            var curA = a;
            var curB = b;
            var cw = w;
            var ch = h;
            var la = GetFloat(w, h);
            var lb = GetFloat(w, h);
            BlitLuma(curA, la, cw, ch);
            BlitLuma(curB, lb, cw, ch);
            for (int level = 0; level < 5; level++)
            {
                var s = SsimOnLumaGpu(la, lb, cw, ch, alloc);
                var weight = level < 4 ? new[] { 0.0448f, 0.2856f, 0.3001f, 0.2363f }[level] : 0.1333f;
                acc += s * weight;
                sum += weight;
                level++;
                if (level >= 5 || cw <= 16 || ch <= 16) break;
                var hw = cw / 2;
                var hh = ch / 2;
                var nla = GetFloat(hw, hh);
                var nlb = GetFloat(hw, hh);
                GpuBoxHalfFloat(la, nla, cw, ch);
                GpuBoxHalfFloat(lb, nlb, cw, ch);
                la = nla;
                lb = nlb;
                cw = hw;
                ch = hh;
            }
            return sum > 0f ? acc / sum : 1f;
        }

        private void GpuBoxHalfFloat(RenderTexture src, RenderTexture dst, int sw, int sh)
        {
            // 使用 CSBilinear 双线性缩放 1/2（盒式近似）/ use bilinear half-scale as the box approximation
            _shader.SetTexture(_kBilinear, "_BiSrc", src);
            _shader.SetVector(_kBilinear, "_BiSrcSize", new Vector4(sw, sh, 0, 0));
            _shader.SetVector(_kBilinear, "_BiInvSrcSize", new Vector4(1f / sw, 1f / sh, 0, 0));
            _shader.SetTexture(_kBilinear, "_BiDst", dst);
            _shader.Dispatch(_kBilinear, Mathf.CeilToInt(dst.width / 8f), Mathf.CeilToInt(dst.height / 8f), 1);
        }

        private void BlitLuma(RenderTexture src, RenderTexture dst, int w, int h)
        {
            _shader.SetTexture(_kLuma, "_LumaSrc", src);
            _shader.SetTexture(_kLuma, "_LumaDst", dst);
            _shader.Dispatch(_kLuma, Mathf.CeilToInt(w / 8f), Mathf.CeilToInt(h / 8f), 1);
        }

        private void GpuGaussian(RenderTexture src, RenderTexture tmp, RenderTexture dst, int w, int h)
        {
            _shader.SetTexture(_kGaussH, "_GaussSrc", src);
            _shader.SetTexture(_kGaussH, "_GaussSrcRead", src);
            _shader.SetTexture(_kGaussH, "_GaussDst", tmp);
            _shader.Dispatch(_kGaussH, Mathf.CeilToInt(w / 8f), Mathf.CeilToInt(h / 8f), 1);
            _shader.SetTexture(_kGaussV, "_GaussSrc", tmp);
            _shader.SetTexture(_kGaussV, "_GaussSrcRead", tmp);
            _shader.SetTexture(_kGaussV, "_GaussDst", dst);
            _shader.Dispatch(_kGaussV, Mathf.CeilToInt(w / 8f), Mathf.CeilToInt(h / 8f), 1);
        }

        private float SsimOnLumaGpu(RenderTexture la, RenderTexture lb, int w, int h, Allocator alloc)
        {
            var ma = GetFloat(w, h);
            var mb = GetFloat(w, h);
            var tmp = GetFloat(w, h);
            GpuGaussian(la, tmp, ma, w, h);
            GpuGaussian(lb, tmp, mb, w, h);

            var va = GetFloat(w, h);
            var vb = GetFloat(w, h);
            var cov = GetFloat(w, h);
            FloatDiffSq(la, ma, va, w, h);
            FloatDiffSq(lb, mb, vb, w, h);
            FloatProdDiff(la, ma, lb, mb, cov, w, h);
            GpuGaussian(va, tmp, va, w, h);
            GpuGaussian(vb, tmp, vb, w, h);
            GpuGaussian(cov, tmp, cov, w, h);

            var ssimRT = GetFloat(w, h);
            _shader.SetTexture(_kSsim, "_SsimMa", ma);
            _shader.SetTexture(_kSsim, "_SsimMb", mb);
            _shader.SetTexture(_kSsim, "_SsimVa", va);
            _shader.SetTexture(_kSsim, "_SsimVb", vb);
            _shader.SetTexture(_kSsim, "_SsimCov", cov);
            _shader.SetTexture(_kSsim, "_SsimDst", ssimRT);
            _shader.Dispatch(_kSsim, Mathf.CeilToInt(w / 8f), Mathf.CeilToInt(h / 8f), 1);

            var values = Readback<float>(ssimRT, alloc);
            try
            {
                return ATOMetrics.Mean(values);
            }
            finally
            {
                values.Dispose();
            }
        }

        private void FloatDiffSq(RenderTexture a, RenderTexture b, RenderTexture dst, int w, int h)
        {
            _shader.SetTexture(_kFds, "_FdsA", a);
            _shader.SetTexture(_kFds, "_FdsB", b);
            _shader.SetTexture(_kFds, "_FdsDst", dst);
            _shader.Dispatch(_kFds, Mathf.CeilToInt(w / 8f), Mathf.CeilToInt(h / 8f), 1);
        }

        private void FloatProdDiff(RenderTexture a, RenderTexture ma, RenderTexture b, RenderTexture mb, RenderTexture dst, int w, int h)
        {
            _shader.SetTexture(_kFpd, "_FpdA", a);
            _shader.SetTexture(_kFpd, "_FpdMa", ma);
            _shader.SetTexture(_kFpd, "_FpdB", b);
            _shader.SetTexture(_kFpd, "_FpdMb", mb);
            _shader.SetTexture(_kFpd, "_FpdDst", dst);
            _shader.Dispatch(_kFpd, Mathf.CeilToInt(w / 8f), Mathf.CeilToInt(h / 8f), 1);
        }

        private NativeArray<float> GpuDeltaE(RenderTexture a, RenderTexture b, int w, int h, Allocator alloc)
        {
            var dst = GetFloat(w, h);
            _shader.SetTexture(_kDeltaE, "_DeA", a);
            _shader.SetTexture(_kDeltaE, "_DeB", b);
            _shader.SetTexture(_kDeltaE, "_DeDst", dst);
            _shader.Dispatch(_kDeltaE, Mathf.CeilToInt(w / 8f), Mathf.CeilToInt(h / 8f), 1);
            return Readback<float>(dst, alloc);
        }

        private NativeArray<float> GpuNormalAngle(RenderTexture a, RenderTexture b, int w, int h, Allocator alloc)
        {
            var dst = GetFloat(w, h);
            _shader.SetTexture(_kNormalAngle, "_NaA", a);
            _shader.SetTexture(_kNormalAngle, "_NaB", b);
            _shader.SetTexture(_kNormalAngle, "_NaDst", dst);
            _shader.Dispatch(_kNormalAngle, Mathf.CeilToInt(w / 8f), Mathf.CeilToInt(h / 8f), 1);
            return Readback<float>(dst, alloc);
        }

        private NativeArray<float4> GpuGrayDiff(RenderTexture a, RenderTexture b, int w, int h, Allocator alloc)
        {
            var dst = GetRgba(w, h);
            _shader.SetTexture(_kGray, "_GrA", a);
            _shader.SetTexture(_kGray, "_GrB", b);
            _shader.SetTexture(_kGray, "_GrDst", dst);
            _shader.Dispatch(_kGray, Mathf.CeilToInt(w / 8f), Mathf.CeilToInt(h / 8f), 1);
            return Readback<float4>(dst, alloc);
        }

        private NativeArray<float2> GpuAlphaPairs(RenderTexture a, RenderTexture b, int w, int h, Allocator alloc)
        {
            var dst = GetRgba(w, h);
            _shader.SetTexture(_kAlpha, "_ApA", a);
            _shader.SetTexture(_kAlpha, "_ApB", b);
            _shader.SetTexture(_kAlpha, "_ApDst", dst);
            _shader.Dispatch(_kAlpha, Mathf.CeilToInt(w / 8f), Mathf.CeilToInt(h / 8f), 1);
            return Readback<float2>(dst, alloc);
        }

        private static float GrayRmseFromSquares(NativeArray<float4> squares)
        {
            double sr = 0, sg = 0, sb = 0, sa = 0;
            for (int i = 0; i < squares.Length; i++)
            {
                var v = squares[i];
                sr += v.x;
                sg += v.y;
                sb += v.z;
                sa += v.w;
            }
            var n = squares.Length;
            return Mathf.Max(
                Mathf.Max(Mathf.Sqrt((float)(sr / n)), Mathf.Sqrt((float)(sg / n))),
                Mathf.Max(Mathf.Sqrt((float)(sb / n)), Mathf.Sqrt((float)(sa / n))));
        }

        private static float AlphaIoU(NativeArray<float2> pairs, float cutoff)
        {
            long inter = 0, union = 0;
            for (int i = 0; i < pairs.Length; i++)
            {
                var a = pairs[i].x >= cutoff ? 1 : 0;
                var b = pairs[i].y >= cutoff ? 1 : 0;
                inter += (a & b);
                union += (a | b);
            }
            return union == 0 ? 1f : (float)inter / union;
        }

        private static float AlphaRmse(NativeArray<float2> pairs)
        {
            double sum = 0;
            for (int i = 0; i < pairs.Length; i++)
            {
                var d = pairs[i].x - pairs[i].y;
                sum += (double)d * d;
            }
            return (float)Math.Sqrt(sum / pairs.Length);
        }

        /// <summary>pull-push 外扩填图集空白（透明贴图 alpha 保持 0）。/ Pull-push dilation filling atlas gaps (alpha stays 0 for transparent).</summary>
        public void PullPush(RenderTexture atlas, bool preserveAlphaZero)
        {
            if (!_available) return;
            var maxSide = Mathf.Max(atlas.width, atlas.height);
            _shader.SetTexture(_kPullPush, "_PpTex", atlas);
            _shader.SetInt(_kPullPush, "_PpPreserveAlpha", preserveAlphaZero ? 1 : 0);
            for (int step = 1; step < maxSide; step *= 2)
            {
                _shader.SetInts(_kPullPush, "_PpOffset", new[] { step, step });
                _shader.Dispatch(_kPullPush, Mathf.CeilToInt(atlas.width / 8f), Mathf.CeilToInt(atlas.height / 8f), 1);
            }
        }

        public void Dispose()
        {
            foreach (var rt in _pool.Values)
                if (rt != null) rt.Release();
            foreach (var rt in _floatPool.Values)
                if (rt != null) rt.Release();
            _pool.Clear();
            _floatPool.Clear();
        }
    }
}
