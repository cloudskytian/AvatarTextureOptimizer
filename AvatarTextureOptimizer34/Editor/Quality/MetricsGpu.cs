// AvatarTextureOptimizer - MetricsGpu
// EN: GPU metric path (compute shader) with a built-in self-test against the CPU implementation. The GPU path is
// only enabled when the self-test passes; otherwise the CPU (Burst-parallel) path is used and a warning is shown.
// GPU computes the heavy masked SSIM chain; CPU combines MS-SSIM weights and evaluates ΔE / alpha / normal / gray.
// CN: GPU 度量路径（compute shader），内置与 CPU 实现的自检。仅当自检通过时启用 GPU；
//     否则回退 CPU（Burst 并行）路径并告警。GPU 计算重负载的掩码 SSIM 链；CPU 组合 MS-SSIM 权重并评估 ΔE/alpha/法线/灰度。
using System;
using UnityEditor;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer
{
    public static class MetricsGpu
    {
        private static ComputeShader _shader;
        private static bool _searched;
        private static int? _selfTestResult; // null=未测, true=通过

        // EN: Upload cache (ref/mask textures reused across binary-search iterations).
        // CN: 上传缓存（参考/掩码贴图在二分迭代间复用）。
        private static readonly Dictionary<LinearImage, Texture2D> _lumaCache = new Dictionary<LinearImage, Texture2D>();
        private static readonly Dictionary<byte[], Texture2D> _maskCache = new Dictionary<byte[], Texture2D>();
        private static int _cacheCount;

        /// <summary>EN: Clears the upload cache (called once per build). / CN: 清空上传缓存（每次构建调用一次）。</summary>
        public static void ResetCache()
        {
            foreach (var kv in _lumaCache) UnityEngine.Object.DestroyImmediate(kv.Value);
            foreach (var kv in _maskCache) UnityEngine.Object.DestroyImmediate(kv.Value);
            _lumaCache.Clear();
            _maskCache.Clear();
            _cacheCount = 0;
        }

        public static ComputeShader FindShader()
        {
            if (_searched) return _shader;
            _searched = true;
            var guids = AssetDatabase.FindAssets("ATOQualityMetrics t:ComputeShader");
            if (guids.Length > 0)
                _shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(AssetDatabase.GUIDToAssetPath(guids[0]));
            return _shader;
        }

        /// <summary>EN: Whether the GPU path is available AND self-test passed. / CN: GPU 路径可用且自检通过。</summary>
        public static bool IsUsable(AtoBuildState state)
        {
            if (!state.Component.useGpuMetrics) return false;
            if (_selfTestResult == null)
            {
                _selfTestResult = SelfTestPasses();
                if (_selfTestResult == false)
                    AtoLog.Warn(I18n.T("warn.gpu.selfcheck", "N/A"));
            }
            return _selfTestResult == true;
        }

        /// <summary>
        /// EN: Self-test: SSIM of a synthetic pair via GPU vs CPU; enables GPU only when within tolerance.
        /// CN: 自检：合成图对的 GPU/CPU SSIM 对比；容差内才启用 GPU。
        /// </summary>
        private static bool SelfTestPasses()
        {
            var shader = FindShader();
            if (shader == null) return false;
            try
            {
                int w = 64, h = 64;
                var refImg = new LinearImage(w, h);
                var candImg = new LinearImage(w, h);
                var rnd = new System.Random(42);
                for (int i = 0; i < w * h; i++)
                {
                    refImg.rgba[i * 4] = (float)rnd.NextDouble();
                    refImg.rgba[i * 4 + 1] = (float)rnd.NextDouble();
                    refImg.rgba[i * 4 + 2] = (float)rnd.NextDouble();
                    refImg.rgba[i * 4 + 3] = 1f;
                }
                // EN: Slight perturbation = mild quality loss.
                // CN: 轻微扰动 = 轻度质量损失。
                for (int i = 0; i < w * h; i++)
                {
                    candImg.rgba[i * 4] = Mathf.Clamp01(refImg.rgba[i * 4] * 0.9f + 0.02f);
                    candImg.rgba[i * 4 + 1] = Mathf.Clamp01(refImg.rgba[i * 4 + 1] * 0.9f + 0.02f);
                    candImg.rgba[i * 4 + 2] = Mathf.Clamp01(refImg.rgba[i * 4 + 2] * 0.9f + 0.02f);
                    candImg.rgba[i * 4 + 3] = 1f;
                }
                var mask = new byte[w * h];
                for (int i = 0; i < w * h; i++) mask[i] = 1;

                float cpu = MetricsCpu.SsimMasked(refImg, candImg, mask);
                float gpu = SsimGpu(shader, refImg, candImg, mask);
                if (gpu < 0) return false;
                float diff = Mathf.Abs(cpu - gpu);
                AtoLog.Detail($"GPU metric self-test: cpu={cpu:F4} gpu={gpu:F4} diff={diff:F4}");
                return diff < 0.02f;
            }
            catch (Exception e)
            {
                AtoLog.Detail($"GPU self-test exception: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// EN: Masked SSIM at a single scale via compute (returns -1 on failure).
        /// CN: 经 compute 的单尺度掩码 SSIM（失败返回 -1）。
        /// </summary>
        public static float SsimGpu(ComputeShader shader, LinearImage refImg, LinearImage candImg, byte[] mask)
        {
            try
            {
                int w = refImg.width, h = refImg.height;
                var refTex = ToTexture(refImg);
                var candTex = ToTexture(candImg);
                var maskTex = ToMaskTexture(mask, w, h);

                int kernel = shader.FindKernel("SSIM");
                var result = new ComputeBuffer(1, sizeof(uint));
                var cnt = new ComputeBuffer(1, sizeof(int));
                result.SetData(new uint[] { 0 });
                cnt.SetData(new[] { 0 });
                shader.SetTexture(kernel, "RefTex", refTex);
                shader.SetTexture(kernel, "CandTex", candTex);
                shader.SetTexture(kernel, "MaskTex", maskTex);
                // EN: Reference & mask textures are cached across iterations; the candidate is always fresh.
                // CN: 参考与掩码贴图跨迭代缓存；候选每次新建。
                shader.SetBuffer(kernel, "Result", result);
                shader.SetBuffer(kernel, "Count", cnt);
                shader.SetInt("W", w);
                shader.SetInt("H", h);
                shader.Dispatch(kernel, (w + 7) / 8, (h + 7) / 8, 1);

                var data = new uint[1];
                result.GetData(data);
                var counts = new int[1];
                cnt.GetData(counts);
                float fsum = BitConverter.ToSingle(BitConverter.GetBytes(data[0]), 0);

                // EN: Only the candidate is ours to destroy; ref/mask are cached.
                // CN: 仅候选贴图由我们销毁；参考/掩码为缓存。
                UnityEngine.Object.DestroyImmediate(candTex);
                result.Release();
                cnt.Release();
                return counts[0] > 0 ? fsum / counts[0] : 1f;
            }
            catch (Exception)
            {
                return -1f;
            }
        }

        /// <summary>
        /// EN: Multi-scale SSIM: CPU downsample chain + per-scale GPU SSIM, combined with standard weights.
        /// CN: 多尺度 SSIM：CPU 下采样链 + 每尺度 GPU SSIM，按标准权重组合。
        /// </summary>
        public static float MsSsimGpu(ComputeShader shader, LinearImage refImg, LinearImage candImg, byte[] mask)
        {
            int w = Mathf.Min(refImg.width, candImg.width);
            int h = Mathf.Min(refImg.height, candImg.height);
            if (w < 8 || h < 8) return SsimGpu(shader, refImg, candImg, mask);

            float[] weights = { 0.0448f, 0.2856f, 0.3001f, 0.2363f, 0.1333f };
            double product = 1.0;
            int scales = 0;
            int cw = w, ch = h;
            var rCur = w == refImg.width && h == refImg.height ? refImg
                : Resampler.Bilinear(refImg, w, h, false);
            var cCur = w == candImg.width && h == candImg.height ? candImg
                : Resampler.Bilinear(candImg, w, h, false);
            var mCur = mask;
            int mw = w, mh = h;

            while (scales < 5 && cw >= 8 && ch >= 8)
            {
                float ssim = SsimGpu(shader, rCur, cCur, mCur);
                if (ssim < 0) return -1f;
                product *= Math.Pow(Math.Max(0, ssim), weights[scales]);
                scales++;
                if (cw / 2 < 8 || ch / 2 < 8) break;
                int nw = Mathf.Max(1, cw / 2), nh = Mathf.Max(1, ch / 2);
                var rDown = Resampler.Bilinear(rCur, nw, nh, false);
                var cDown = Resampler.Bilinear(cCur, nw, nh, false);
                rCur = rDown;
                cCur = cDown;
                mCur = MetricsCpu.DownscaleMaskPublic(mCur, mw, mh, nw, nh);
                mw = nw; mh = nh;
                cw = nw;
                ch = nh;
            }
            return (float)Math.Max(0, product);
        }

        /// <summary>EN: Uploads a LinearImage as an R8 luma texture (GPU path), cached by image reference. / CN: 把 LinearImage 上传为 R8 亮度贴图（按图像引用缓存）。</summary>
        private static Texture2D ToTexture(LinearImage img)
        {
            if (_lumaCache.TryGetValue(img, out var cached) && cached != null) return cached;
            int w = img.width, h = img.height;
            var tex = new Texture2D(w, h, TextureFormat.R8, false, false);
            var luma = new byte[w * h];
            for (int i = 0; i < w * h; i++)
                luma[i] = (byte)Mathf.RoundToInt(Mathf.Clamp01(
                    0.2126f * img.rgba[i * 4] + 0.7152f * img.rgba[i * 4 + 1] + 0.0722f * img.rgba[i * 4 + 2]) * 255f);
            tex.SetPixelData(luma, 0);
            tex.Apply();
            if (_cacheCount < 48)
            {
                _lumaCache[img] = tex;
                _cacheCount++;
            }
            return tex;
        }

        /// <summary>EN: Uploads a mask as an R8 texture, cached by array reference. / CN: 把掩码上传为 R8 贴图（按数组引用缓存）。</summary>
        private static Texture2D ToMaskTexture(byte[] mask, int w, int h)
        {
            if (_maskCache.TryGetValue(mask, out var cached) && cached != null) return cached;
            var tex = new Texture2D(w, h, TextureFormat.R8, false, false);
            tex.SetPixelData(mask, 0);
            tex.Apply();
            if (_maskCache.Count < 48) _maskCache[mask] = tex;
            return tex;
        }
    }
}
