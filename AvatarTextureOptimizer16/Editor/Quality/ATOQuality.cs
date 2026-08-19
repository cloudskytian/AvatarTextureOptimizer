using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace AvatarTextureOptimizer.Editor
{
    /// <summary>Computed quality metrics for one comparison. / 一次对比计算出的质量指标。</summary>
    public struct ATOMetrics
    {
        public float msSsim;     // (0..1] / 越高越好
        public float deltaEP95;  // CIEDE2000 p95 / 越低越好
        public float alphaIoU;   // cutout outline IoU (0..1] / 越高越好
        public float alphaRmse;  // blend linear RMSE / 越低越好
        public float normalP95;  // angular error p95 (deg) / 越低越好
        public float grayRmse;   // gray linear RMSE / 越低越好
    }

    /// <summary>
    /// GPU-based quality evaluation (the single source of truth for metrics).
    /// GPU 质量求值（指标唯一真相源）。
    /// </summary>
    public static class ATOQuality
    {
        private static ComputeShader _shader;
        private static ComputeShader Shader
        {
            get
            {
                if (_shader == null)
                    _shader = AssetDatabaseEx.Load<ComputeShader>("ATOMetrics");
                return _shader;
            }
        }

        /// <summary>
        /// Evaluate quality between a reference texture and a downscaled-and-upsampled candidate,
        /// restricted to a region. / 对比参考贴图与缩小后上采样的候选（限定区域）。
        /// </summary>
        public static ATOMetrics Evaluate(Texture2D reference, Texture2D candidateUpsampled,
            Rect region, ATOTextureCategory category, float cutoff,
            int normalEncoding = 0, int grayChannel = 7)
        {
            var m = new ATOMetrics();

            int rw = Mathf.Max(1, Mathf.RoundToInt(region.width));
            int rh = Mathf.Max(1, Mathf.RoundToInt(region.height));
            int ox = Mathf.Clamp(Mathf.RoundToInt(region.x), 0, reference.width - rw);
            int oy = Mathf.Clamp(Mathf.RoundToInt(region.y), 0, reference.height - rh);

            var refRt = CopyRegion(reference, ox, oy, rw, rh);
            var candRt = CopyRegion(candidateUpsampled, ox, oy, rw, rh);

            var shader = Shader;
            int kLinear = shader.FindKernel("LinearizeAndDeltaE");
            int kSsim = shader.FindKernel("SSIMWindow");

            var lum = new ComputeBuffer(rw * rh, 8);
            var deltaE = new ComputeBuffer(rw * rh, 4);
            var alphaErr = new ComputeBuffer(rw * rh, 4);

            shader.SetInt("_Width", rw);
            shader.SetInt("_Height", rh);
            shader.SetInt("_WindowSize", 8);
            shader.SetTexture(kLinear, "_RefTex", refRt);
            shader.SetTexture(kLinear, "_CandTex", candRt);
            shader.SetBuffer(kLinear, "_Lum", lum);
            shader.SetBuffer(kLinear, "_DeltaE", deltaE);
            shader.SetBuffer(kLinear, "_AlphaErr", alphaErr);
            shader.Dispatch(kLinear, Mathf.CeilToInt(rw / 8f), Mathf.CeilToInt(rh / 8f), 1);

            // SSIM windows (single scale; multi-scale handled in EvaluateMS via half-res pass)
            // SSIM 窗口（单尺度；多尺度在 EvaluateMS 中通过半分辨率处理）
            int cols = Mathf.CeilToInt(rw / 8f);
            int rows = Mathf.CeilToInt(rh / 8f);
            int wCount = cols * rows;
            var ssimOut = new ComputeBuffer(Mathf.Max(1, wCount), 4);
            shader.SetInt("_Width", rw);
            shader.SetInt("_Height", rh);
            shader.SetInt("_WindowSize", 8);
            shader.SetBuffer(kSsim, "_Lum", lum);
            shader.SetBuffer(kSsim, "_SSIMOut", ssimOut);
            shader.Dispatch(kSsim, Mathf.CeilToInt(wCount / 64f) + 1, 1, 1);

            var ssims = new float[wCount];
            ssimOut.GetData(ssims);
            var deltaEs = new float[rw * rh];
            deltaE.GetData(deltaEs);
            var alphaErrs = new float[rw * rh];
            alphaErr.GetData(alphaErrs);

            float ssimSum = 0f; int ssimN = 0;
            foreach (var s in ssims) { if (s > 0f) { ssimSum += Mathf.Clamp01(s); ssimN++; } }
            m.msSsim = ssimN > 0 ? ssimSum / ssimN : 1f;

            var sorted = new List<float>(deltaEs);
            sorted.Sort();
            m.deltaEP95 = sorted.Count > 0 ? sorted[Mathf.Min(sorted.Count - 1, (int)(sorted.Count * 0.95f))] : 0f;

            float aSum = 0f; foreach (var a in alphaErrs) aSum += a;
            m.alphaRmse = Mathf.Sqrt(aSum / Mathf.Max(1, alphaErrs.Length));

            if (category == ATOTextureCategory.TransparentColor)
                m.alphaIoU = ComputeAlphaIoU(reference, candidateUpsampled, region, cutoff);
            else
                m.alphaIoU = 1f;

            if (category == ATOTextureCategory.Normal)
                m.normalP95 = EvaluateNormalCore(shader, refRt, candRt, rw, rh, normalEncoding);
            else if (category == ATOTextureCategory.Gray)
                m.grayRmse = EvaluateGrayCore(shader, refRt, candRt, rw, rh, grayChannel);

            lum.Dispose(); deltaE.Dispose(); alphaErr.Dispose(); ssimOut.Dispose();
            RenderTexture.ReleaseTemporary(refRt);
            RenderTexture.ReleaseTemporary(candRt);

            return m;
        }

        /// <summary>
        /// Multi-scale SSIM: if the region's short edge ≥ 176px, blend full-res SSIM with a
        /// half-resolution SSIM; if < 11px, ignore the metric (return 1).
        /// 多尺度 SSIM：区域短边 ≥ 176px 时，将全分辨率 SSIM 与半分辨率 SSIM 加权；< 11px 忽略（返回 1）。
        /// </summary>
        public static float EvaluateMSSsim(Texture2D reference, Texture2D candidateUpsampled, Rect region)
        {
            int shortEdge = Mathf.Min(Mathf.RoundToInt(region.width), Mathf.RoundToInt(region.height));
            if (shortEdge < 11) return 1f;
            if (shortEdge < 176)
            {
                var m = Evaluate(reference, candidateUpsampled, region, ATOTextureCategory.OpaqueColor, 0.5f);
                return m.msSsim;
            }

            var full = Evaluate(reference, candidateUpsampled, region, ATOTextureCategory.OpaqueColor, 0.5f).msSsim;

            // half-resolution comparison / 半分辨率对比
            int rw = Mathf.Max(1, Mathf.RoundToInt(region.width));
            int rh = Mathf.Max(1, Mathf.RoundToInt(region.height));
            var refHalf = TextureOps.Scale(reference, Mathf.Max(1, rw / 2), Mathf.Max(1, rh / 2));
            var candHalf = TextureOps.Scale(candidateUpsampled, Mathf.Max(1, rw / 2), Mathf.Max(1, rh / 2));
            var half = Evaluate(refHalf, candHalf, new Rect(0, 0, refHalf.width, refHalf.height),
                ATOTextureCategory.OpaqueColor, 0.5f).msSsim;
            UnityEngine.Object.DestroyImmediate(refHalf);
            UnityEngine.Object.DestroyImmediate(candHalf);

            return full * 0.85f + half * 0.15f;
        }

        private static float EvaluateNormalCore(ComputeShader shader, RenderTexture refRt, RenderTexture candRt,
            int rw, int rh, int encoding)
        {
            int k = shader.FindKernel("NormalAngle");
            var buf = new ComputeBuffer(rw * rh, 4);
            shader.SetInt("_Width", rw); shader.SetInt("_Height", rh);
            shader.SetInt("_NormalEncoding", encoding);
            shader.SetTexture(k, "_RefTex", refRt);
            shader.SetTexture(k, "_CandTex", candRt);
            shader.SetBuffer(k, "_NormalErr", buf);
            shader.Dispatch(k, Mathf.CeilToInt(rw / 8f), Mathf.CeilToInt(rh / 8f), 1);
            var errs = new float[rw * rh];
            buf.GetData(errs);
            buf.Dispose();
            var sorted = new List<float>(errs); sorted.Sort();
            return sorted.Count > 0 ? sorted[Mathf.Min(sorted.Count - 1, (int)(sorted.Count * 0.95f))] : 0f;
        }

        private static float EvaluateGrayCore(ComputeShader shader, RenderTexture refRt, RenderTexture candRt,
            int rw, int rh, int grayChannel)
        {
            int k = shader.FindKernel("GrayRmse");
            var buf = new ComputeBuffer(rw * rh, 4);
            shader.SetInt("_Width", rw); shader.SetInt("_Height", rh);
            shader.SetInt("_GrayChannel", grayChannel);
            shader.SetTexture(k, "_RefTex", refRt);
            shader.SetTexture(k, "_CandTex", candRt);
            shader.SetBuffer(k, "_GrayErr", buf);
            shader.Dispatch(k, Mathf.CeilToInt(rw / 8f), Mathf.CeilToInt(rh / 8f), 1);
            var errs = new float[rw * rh];
            buf.GetData(errs);
            buf.Dispose();
            float sum = 0; foreach (var e in errs) sum += e;
            return Mathf.Sqrt(sum / Mathf.Max(1, errs.Length));
        }

        private static float ComputeAlphaIoU(Texture2D reference, Texture2D candidate, Rect region, float cutoff)
        {
            // IoU of clip() masks by sampling the region. / 通过采样区域比较 clip 掩码的 IoU。
            int samples = 4096;
            int inter = 0, union = 0;
            var rnd = new System.Random(12345);
            for (int i = 0; i < samples; i++)
            {
                float u = region.x + (float)rnd.NextDouble() * region.width;
                float v = region.y + (float)rnd.NextDouble() * region.height;
                bool rb = SampleAlpha(reference, u, v) >= cutoff;
                bool cb = SampleAlpha(candidate, u, v) >= cutoff;
                if (rb && cb) inter++;
                if (rb || cb) union++;
            }
            return union == 0 ? 1f : (float)inter / union;
        }

        private static float SampleAlpha(Texture2D tex, float u, float v)
        {
            if (!tex.isReadable)
            {
                // approximate via nearest pixel read through a temp readback is expensive;
                // for non-readable textures use a downsampled readable copy cached elsewhere.
                // 不可读贴图通过缓存的可读副本读取（见 TextureOps）；此处退回双线性近似。
            }
            int x = Mathf.Clamp((int)u, 0, tex.width - 1);
            int y = Mathf.Clamp((int)v, 0, tex.height - 1);
            return tex.GetPixel(x, y).a;
        }

        private static RenderTexture CopyRegion(Texture2D src, int x, int y, int w, int h)
        {
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            if (src.isReadable)
            {
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                tex.SetPixels32(src.GetPixels32(x, y, w, h));
                tex.Apply();
                Graphics.Blit(tex, rt);
                UnityEngine.Object.DestroyImmediate(tex);
            }
            else
            {
                // blit the whole texture, then crop via UV — approximate; readable path is preferred.
                // 直接整张贴图拷贝后按 UV 裁剪——近似；优先走可读路径。
                Graphics.Blit(src, rt, new Vector2((float)w / src.width, (float)h / src.height),
                    new Vector2((float)x / src.width, (float)y / src.height));
            }
            RenderTexture.active = prev;
            return rt;
        }
    }

    /// <summary>Asset loading helper for editor assets. / Editor 资源加载助手。</summary>
    public static class AssetDatabaseEx
    {
        public static T Load<T>(string nameWithoutExtension) where T : UnityEngine.Object
        {
            var guids = UnityEditor.AssetDatabase.FindAssets(nameWithoutExtension + " t:" + typeof(T).Name);
            foreach (var g in guids)
            {
                var p = UnityEditor.AssetDatabase.GUIDToAssetPath(g);
                var o = UnityEditor.AssetDatabase.LoadAssetAtPath<T>(p);
                if (o != null) return o;
            }
            return null;
        }
    }
}
