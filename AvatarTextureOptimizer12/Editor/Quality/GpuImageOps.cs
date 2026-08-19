// SPDX-License-Identifier: MIT
// AvatarTextureOptimizer (ATO) - GPU compute front-end for image ops and quality metrics.
// AvatarTextureOptimizer (ATO) - 图像运算与质量指标的 GPU compute 前端。

using System;
using System.Collections.Generic;
using Net.Fosa.AvatarTextureOptimizer.Editor.Core;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Quality
{
    /// <summary>
    /// EN: Thin, pooled wrapper around the ATO compute shaders. Every entry point degrades to the CPU
    ///     implementation when compute is unavailable (or the image is so small that a dispatch would cost
    ///     more than the work), so behaviour is identical either way - only the speed differs.
    /// ZH: 对 ATO compute shader 的轻量池化封装。当 compute 不可用（或图像小到派发开销超过计算本身）时，
    ///     每个入口都会退化到 CPU 实现，因此两条路径行为完全一致，只有速度不同。
    /// </summary>
    public static class GpuImageOps
    {
        /// <summary>EN: Below this pixel count the CPU path is faster. ZH: 低于该像素数时 CPU 路径更快。</summary>
        public const int GpuThresholdPixels = 64 * 64;

        private const int Group = 8;

        private static ComputeShader _imageOps;
        private static ComputeShader _pullPush;
        private static bool _resolved;
        private static bool _disabled;

        // ---- Buffer pool / 缓冲区池 --------------------------------------------------------------

        private sealed class Pool
        {
            private readonly Dictionary<int, Stack<ComputeBuffer>> _free = new Dictionary<int, Stack<ComputeBuffer>>();
            private readonly List<ComputeBuffer> _all = new List<ComputeBuffer>();
            private readonly int _stride;

            public Pool(int stride) => _stride = stride;

            public ComputeBuffer Rent(int count)
            {
                // EN: Round up to a power of two so a handful of bucket sizes serve every request.
                // ZH: 向上取整到 2 的幂，使少量桶尺寸即可服务全部请求。
                int bucket = Mathf.Max(64, Mathf.NextPowerOfTwo(count));
                if (_free.TryGetValue(bucket, out var stack) && stack.Count > 0) return stack.Pop();

                var buffer = new ComputeBuffer(bucket, _stride, ComputeBufferType.Structured);
                _all.Add(buffer);
                return buffer;
            }

            public void Return(ComputeBuffer buffer)
            {
                if (buffer == null) return;
                if (!_free.TryGetValue(buffer.count, out var stack))
                    _free[buffer.count] = stack = new Stack<ComputeBuffer>();
                stack.Push(buffer);
            }

            public void DisposeAll()
            {
                foreach (var b in _all)
                {
                    try { b?.Release(); }
                    catch (Exception) { /* EN: already released. ZH: 已释放。 */ }
                }
                _all.Clear();
                _free.Clear();
            }
        }

        private static readonly Pool Float4Pool = new Pool(sizeof(float) * 4);
        private static readonly Pool FloatPool = new Pool(sizeof(float));

        /// <summary>EN: Release every pooled GPU buffer. ZH: 释放全部池化的 GPU 缓冲区。</summary>
        public static void ReleaseAll()
        {
            Float4Pool.DisposeAll();
            FloatPool.DisposeAll();
            ATOLog.Debug_("GPU buffer pools released");
        }

        /// <summary>EN: Force the CPU path (used by tests and as a user escape hatch). ZH: 强制走 CPU 路径（测试与用户兜底用）。</summary>
        public static bool ForceCpu { get; set; }

        public static bool Available
        {
            get
            {
                if (ForceCpu || _disabled) return false;
                Resolve();
                return _imageOps != null && SystemInfo.supportsComputeShaders;
            }
        }

        public static bool PullPushAvailable
        {
            get
            {
                if (ForceCpu || _disabled) return false;
                Resolve();
                return _pullPush != null && SystemInfo.supportsComputeShaders;
            }
        }

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;

            _imageOps = Load("ATOImageOps");
            _pullPush = Load("ATOPullPush");

            if (!SystemInfo.supportsComputeShaders)
                ATOLog.Info("compute shaders are unavailable on this device; using the CPU path");
            else if (_imageOps == null || _pullPush == null)
                ATOLog.Warn("ATO compute shaders were not found in the project; using the CPU path");
        }

        private static ComputeShader Load(string name)
        {
            foreach (var guid in AssetDatabase.FindAssets($"{name} t:ComputeShader"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!System.IO.Path.GetFileNameWithoutExtension(path).Equals(name, StringComparison.Ordinal))
                    continue;
                var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
                if (shader != null)
                {
                    ATOLog.Debug_($"loaded compute shader '{name}' from {path}");
                    return shader;
                }
            }
            return null;
        }

        private static int Groups(int size) => Mathf.Max(1, (size + Group - 1) / Group);

        private static void SetSizes(ComputeShader cs, int sw, int sh, int dw, int dh)
        {
            cs.SetInts("_SrcSize", sw, sh, 0, 0);
            cs.SetInts("_DstSize", dw, dh, 0, 0);
        }

        // ---- Resampling / 重采样 ------------------------------------------------------------------

        /// <summary>EN: GPU area-average downsample. ZH: GPU 面积平均下采样。</summary>
        public static bool TryDownsample(LinearImage src, int w, int h, out LinearImage dst)
        {
            dst = null;
            if (!Available || (long)src.Width * src.Height < GpuThresholdPixels) return false;

            try
            {
                var srcBuf = Float4Pool.Rent(src.Pixels.Length);
                var dstBuf = Float4Pool.Rent(w * h);
                try
                {
                    srcBuf.SetData(src.Pixels, 0, 0, src.Pixels.Length);

                    int kernel = _imageOps.FindKernel("CSDownsample");
                    SetSizes(_imageOps, src.Width, src.Height, w, h);
                    _imageOps.SetBuffer(kernel, "_SrcRGBA", srcBuf);
                    _imageOps.SetBuffer(kernel, "_DstRGBA", dstBuf);
                    _imageOps.Dispatch(kernel, Groups(w), Groups(h), 1);

                    dst = new LinearImage(w, h, src.Premultiplied);
                    dstBuf.GetData(dst.Pixels, 0, 0, dst.Pixels.Length);
                    return true;
                }
                finally
                {
                    Float4Pool.Return(srcBuf);
                    Float4Pool.Return(dstBuf);
                }
            }
            catch (Exception e)
            {
                Fail("downsample", e);
                return false;
            }
        }

        /// <summary>EN: GPU bilinear upsample. ZH: GPU 双线性上采样。</summary>
        public static bool TryUpsample(LinearImage src, int w, int h, out LinearImage dst)
        {
            dst = null;
            if (!Available || (long)w * h < GpuThresholdPixels) return false;

            try
            {
                var srcBuf = Float4Pool.Rent(src.Pixels.Length);
                var dstBuf = Float4Pool.Rent(w * h);
                try
                {
                    srcBuf.SetData(src.Pixels, 0, 0, src.Pixels.Length);

                    int kernel = _imageOps.FindKernel("CSUpsample");
                    SetSizes(_imageOps, src.Width, src.Height, w, h);
                    _imageOps.SetBuffer(kernel, "_SrcRGBA", srcBuf);
                    _imageOps.SetBuffer(kernel, "_DstRGBA", dstBuf);
                    _imageOps.Dispatch(kernel, Groups(w), Groups(h), 1);

                    dst = new LinearImage(w, h, src.Premultiplied);
                    dstBuf.GetData(dst.Pixels, 0, 0, dst.Pixels.Length);
                    return true;
                }
                finally
                {
                    Float4Pool.Return(srcBuf);
                    Float4Pool.Return(dstBuf);
                }
            }
            catch (Exception e)
            {
                Fail("upsample", e);
                return false;
            }
        }

        // ---- Metric maps / 指标图 -----------------------------------------------------------------

        /// <summary>EN: Per-pixel CIEDE2000 map. ZH: 逐像素 CIEDE2000 图。</summary>
        public static bool TryDeltaEMap(LinearImage a, LinearImage b, out float[] map)
        {
            map = null;
            if (!Available || (long)a.Pixels.Length < GpuThresholdPixels) return false;

            try
            {
                var bufA = Float4Pool.Rent(a.Pixels.Length);
                var bufB = Float4Pool.Rent(b.Pixels.Length);
                var outBuf = FloatPool.Rent(a.Pixels.Length);
                try
                {
                    bufA.SetData(a.Pixels, 0, 0, a.Pixels.Length);
                    bufB.SetData(b.Pixels, 0, 0, b.Pixels.Length);

                    int kernel = _imageOps.FindKernel("CSDeltaE");
                    SetSizes(_imageOps, a.Width, a.Height, a.Width, a.Height);
                    _imageOps.SetVector("_Params", new Vector4(a.Premultiplied ? 1f : 0f, 0, 0, 0));
                    _imageOps.SetBuffer(kernel, "_SrcRGBA", bufA);
                    _imageOps.SetBuffer(kernel, "_SrcRGBA2", bufB);
                    _imageOps.SetBuffer(kernel, "_DstF", outBuf);
                    _imageOps.Dispatch(kernel, Groups(a.Width), Groups(a.Height), 1);

                    map = new float[a.Pixels.Length];
                    outBuf.GetData(map, 0, 0, map.Length);
                    return true;
                }
                finally
                {
                    Float4Pool.Return(bufA);
                    Float4Pool.Return(bufB);
                    FloatPool.Return(outBuf);
                }
            }
            catch (Exception e)
            {
                Fail("deltaE", e);
                return false;
            }
        }

        /// <summary>EN: Per-pixel normal angular error in degrees. ZH: 逐像素法线角度误差（度）。</summary>
        public static bool TryNormalAngleMap(LinearImage a, LinearImage b, out float[] map)
        {
            map = null;
            if (!Available || (long)a.Pixels.Length < GpuThresholdPixels) return false;

            try
            {
                var bufA = Float4Pool.Rent(a.Pixels.Length);
                var bufB = Float4Pool.Rent(b.Pixels.Length);
                var outBuf = FloatPool.Rent(a.Pixels.Length);
                try
                {
                    bufA.SetData(a.Pixels, 0, 0, a.Pixels.Length);
                    bufB.SetData(b.Pixels, 0, 0, b.Pixels.Length);

                    int kernel = _imageOps.FindKernel("CSNormalAngle");
                    SetSizes(_imageOps, a.Width, a.Height, a.Width, a.Height);
                    _imageOps.SetBuffer(kernel, "_SrcRGBA", bufA);
                    _imageOps.SetBuffer(kernel, "_SrcRGBA2", bufB);
                    _imageOps.SetBuffer(kernel, "_DstF", outBuf);
                    _imageOps.Dispatch(kernel, Groups(a.Width), Groups(a.Height), 1);

                    map = new float[a.Pixels.Length];
                    outBuf.GetData(map, 0, 0, map.Length);
                    return true;
                }
                finally
                {
                    Float4Pool.Return(bufA);
                    Float4Pool.Return(bufB);
                    FloatPool.Return(outBuf);
                }
            }
            catch (Exception e)
            {
                Fail("normalAngle", e);
                return false;
            }
        }

        /// <summary>
        /// EN: Mean SSIM and mean contrast-structure for one scale, computed entirely on the GPU.
        /// ZH: 某一尺度的平均 SSIM 与平均对比度-结构项，全部在 GPU 上计算。
        /// </summary>
        public static bool TrySsimScale(float[] lumaA, float[] lumaB, int w, int h,
            out float meanSsim, out float meanCs)
        {
            meanSsim = 0f;
            meanCs = 0f;
            if (!Available || (long)w * h < GpuThresholdPixels) return false;

            int n = w * h;
            var bufA = FloatPool.Rent(n);
            var bufB = FloatPool.Rent(n);
            var muA = FloatPool.Rent(n);
            var muB = FloatPool.Rent(n);
            var sAA = FloatPool.Rent(n);
            var sBB = FloatPool.Rent(n);
            var sAB = FloatPool.Rent(n);
            var tmp = FloatPool.Rent(n);
            var tmp2 = FloatPool.Rent(n);
            var ssim = FloatPool.Rent(n);
            var cs = FloatPool.Rent(n);

            try
            {
                bufA.SetData(lumaA, 0, 0, n);
                bufB.SetData(lumaB, 0, 0, n);
                SetSizes(_imageOps, w, h, w, h);

                Blur(bufA, muA, tmp, w, h);
                Blur(bufB, muB, tmp, w, h);

                Product(bufA, bufB, tmp2, 0f, w, h);  // aa
                Blur(tmp2, sAA, tmp, w, h);
                Product(bufA, bufB, tmp2, 1f, w, h);  // bb
                Blur(tmp2, sBB, tmp, w, h);
                Product(bufA, bufB, tmp2, 2f, w, h);  // ab
                Blur(tmp2, sAB, tmp, w, h);

                int kernel = _imageOps.FindKernel("CSSsim");
                SetSizes(_imageOps, w, h, w, h);
                _imageOps.SetBuffer(kernel, "_SrcF", muA);
                _imageOps.SetBuffer(kernel, "_SrcF2", muB);
                _imageOps.SetBuffer(kernel, "_SrcF3", sAA);
                _imageOps.SetBuffer(kernel, "_SrcF4", sBB);
                _imageOps.SetBuffer(kernel, "_SrcF5", sAB);
                _imageOps.SetBuffer(kernel, "_DstF", ssim);
                _imageOps.SetBuffer(kernel, "_DstF2", cs);
                _imageOps.Dispatch(kernel, Groups(w), Groups(h), 1);

                var ssimData = new float[n];
                var csData = new float[n];
                ssim.GetData(ssimData, 0, 0, n);
                cs.GetData(csData, 0, 0, n);

                double s1 = 0, s2 = 0;
                for (int i = 0; i < n; i++) { s1 += ssimData[i]; s2 += csData[i]; }
                meanSsim = (float)(s1 / n);
                meanCs = (float)(s2 / n);
                return true;
            }
            catch (Exception e)
            {
                Fail("ssim", e);
                return false;
            }
            finally
            {
                foreach (var b in new[] { bufA, bufB, muA, muB, sAA, sBB, sAB, tmp, tmp2, ssim, cs })
                    FloatPool.Return(b);
            }
        }

        private static void Blur(ComputeBuffer src, ComputeBuffer dst, ComputeBuffer scratch, int w, int h)
        {
            int kh = _imageOps.FindKernel("CSBlurH");
            SetSizes(_imageOps, w, h, w, h);
            _imageOps.SetBuffer(kh, "_SrcF", src);
            _imageOps.SetBuffer(kh, "_DstF", scratch);
            _imageOps.Dispatch(kh, Groups(w), Groups(h), 1);

            int kv = _imageOps.FindKernel("CSBlurV");
            SetSizes(_imageOps, w, h, w, h);
            _imageOps.SetBuffer(kv, "_SrcF", scratch);
            _imageOps.SetBuffer(kv, "_DstF", dst);
            _imageOps.Dispatch(kv, Groups(w), Groups(h), 1);
        }

        private static void Product(ComputeBuffer a, ComputeBuffer b, ComputeBuffer dst, float mode,
            int w, int h)
        {
            int k = _imageOps.FindKernel("CSProducts");
            SetSizes(_imageOps, w, h, w, h);
            _imageOps.SetVector("_Params", new Vector4(mode, 0, 0, 0));
            _imageOps.SetBuffer(k, "_SrcF", a);
            _imageOps.SetBuffer(k, "_SrcF2", b);
            _imageOps.SetBuffer(k, "_DstF", dst);
            _imageOps.Dispatch(k, Groups(w), Groups(h), 1);
        }

        /// <summary>EN: GPU 2x2 box-halve of a scalar image. ZH: 标量图的 GPU 2x2 均值缩半。</summary>
        public static bool TryHalve(float[] src, int w, int h, out float[] dst, out int nw, out int nh)
        {
            nw = Mathf.Max(1, w / 2);
            nh = Mathf.Max(1, h / 2);
            dst = null;
            if (!Available || (long)w * h < GpuThresholdPixels) return false;

            var srcBuf = FloatPool.Rent(w * h);
            var dstBuf = FloatPool.Rent(nw * nh);
            try
            {
                srcBuf.SetData(src, 0, 0, w * h);
                int k = _imageOps.FindKernel("CSHalve");
                SetSizes(_imageOps, w, h, nw, nh);
                _imageOps.SetBuffer(k, "_SrcF", srcBuf);
                _imageOps.SetBuffer(k, "_DstF", dstBuf);
                _imageOps.Dispatch(k, Groups(nw), Groups(nh), 1);

                dst = new float[nw * nh];
                dstBuf.GetData(dst, 0, 0, dst.Length);
                return true;
            }
            catch (Exception e)
            {
                Fail("halve", e);
                return false;
            }
            finally
            {
                FloatPool.Return(srcBuf);
                FloatPool.Return(dstBuf);
            }
        }

        // ---- Pull-push / 边缘外扩 ------------------------------------------------------------------

        /// <summary>
        /// EN: GPU pull-push hole filling over the whole atlas. Returns false if compute is unavailable,
        ///     in which case the caller uses the CPU pyramid.
        /// ZH: 对整张图集做 GPU pull-push 空洞填充。compute 不可用时返回 false，调用方改用 CPU 金字塔。
        /// </summary>
        public static bool TryPullPush(float4[] color, bool[] coverage, int width, int height)
        {
            if (!PullPushAvailable) return false;

            int n = width * height;
            var levelsColor = new List<ComputeBuffer>();
            var levelsWeight = new List<ComputeBuffer>();
            var sizes = new List<(int w, int h)>();

            try
            {
                var c0 = new float4[n];
                var w0 = new float[n];
                for (int i = 0; i < n; i++)
                {
                    w0[i] = coverage[i] ? 1f : 0f;
                    c0[i] = coverage[i] ? color[i] : float4.zero;
                }

                var baseColor = Float4Pool.Rent(n);
                var baseWeight = FloatPool.Rent(n);
                baseColor.SetData(c0, 0, 0, n);
                baseWeight.SetData(w0, 0, 0, n);
                levelsColor.Add(baseColor);
                levelsWeight.Add(baseWeight);
                sizes.Add((width, height));

                int kPull = _pullPush.FindKernel("CSPull");
                int kPush = _pullPush.FindKernel("CSPush");
                int kResolve = _pullPush.FindKernel("CSResolve");

                // ---- Pull ----
                while (sizes[sizes.Count - 1].w > 1 || sizes[sizes.Count - 1].h > 1)
                {
                    var (cw, ch) = sizes[sizes.Count - 1];
                    int nw2 = Mathf.Max(1, cw / 2), nh2 = Mathf.Max(1, ch / 2);

                    var nc = Float4Pool.Rent(nw2 * nh2);
                    var nwt = FloatPool.Rent(nw2 * nh2);

                    _pullPush.SetInts("_SrcSize", cw, ch, 0, 0);
                    _pullPush.SetInts("_DstSize", nw2, nh2, 0, 0);
                    _pullPush.SetBuffer(kPull, "_SrcColor", levelsColor[levelsColor.Count - 1]);
                    _pullPush.SetBuffer(kPull, "_SrcWeight", levelsWeight[levelsWeight.Count - 1]);
                    _pullPush.SetBuffer(kPull, "_DstColor", nc);
                    _pullPush.SetBuffer(kPull, "_DstWeight", nwt);
                    _pullPush.Dispatch(kPull, Groups(nw2), Groups(nh2), 1);

                    levelsColor.Add(nc);
                    levelsWeight.Add(nwt);
                    sizes.Add((nw2, nh2));
                    if (nw2 == 1 && nh2 == 1) break;
                }

                // ---- Push ----
                for (int l = sizes.Count - 1; l > 0; l--)
                {
                    var (cw, ch) = sizes[l];
                    var (fw, fh) = sizes[l - 1];

                    _pullPush.SetInts("_SrcSize", cw, ch, 0, 0);
                    _pullPush.SetInts("_DstSize", fw, fh, 0, 0);
                    _pullPush.SetBuffer(kPush, "_CoarseColor", levelsColor[l]);
                    _pullPush.SetBuffer(kPush, "_CoarseWeight", levelsWeight[l]);
                    _pullPush.SetBuffer(kPush, "_DstColor", levelsColor[l - 1]);
                    _pullPush.SetBuffer(kPush, "_DstWeight", levelsWeight[l - 1]);
                    _pullPush.Dispatch(kPush, Groups(fw), Groups(fh), 1);
                }

                // ---- Resolve ----
                var covBuf = FloatPool.Rent(n);
                var resolved = Float4Pool.Rent(n);
                try
                {
                    var cov = new float[n];
                    for (int i = 0; i < n; i++) cov[i] = coverage[i] ? 1f : 0f;
                    covBuf.SetData(cov, 0, 0, n);
                    resolved.SetData(color, 0, 0, n);

                    _pullPush.SetInts("_SrcSize", width, height, 0, 0);
                    _pullPush.SetInts("_DstSize", width, height, 0, 0);
                    _pullPush.SetBuffer(kResolve, "_SrcColor", levelsColor[0]);
                    _pullPush.SetBuffer(kResolve, "_SrcWeight", levelsWeight[0]);
                    _pullPush.SetBuffer(kResolve, "_Coverage", covBuf);
                    _pullPush.SetBuffer(kResolve, "_Resolved", resolved);
                    _pullPush.Dispatch(kResolve, Groups(width), Groups(height), 1);

                    resolved.GetData(color, 0, 0, n);
                }
                finally
                {
                    FloatPool.Return(covBuf);
                    Float4Pool.Return(resolved);
                }

                ATOLog.Debug_($"GPU pull-push completed over {sizes.Count} pyramid level(s)");
                return true;
            }
            catch (Exception e)
            {
                Fail("pullPush", e);
                return false;
            }
            finally
            {
                foreach (var b in levelsColor) Float4Pool.Return(b);
                foreach (var b in levelsWeight) FloatPool.Return(b);
            }
        }

        private static void Fail(string op, Exception e)
        {
            ATOLog.Warn($"GPU {op} failed ({e.Message}); falling back to the CPU path for the rest of the build");
            _disabled = true;
        }

        /// <summary>EN: Re-enable GPU usage at the start of a new build. ZH: 新构建开始时重新启用 GPU。</summary>
        public static void ResetForNewBuild()
        {
            _disabled = false;
        }
    }
}
