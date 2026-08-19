// AvatarTextureOptimizer
// File: Editor/Quality/MS_SSIM.cs
//
// MS-SSIM orchestrator: drives ATO_SSIM.compute over up to 5 scales, reads
// back per-scale l/c/s maps, averages them and combines with Wang's weights.
// Falls back to single-scale SSIM for small regions (short side < 176 px);
// regions with short side < 11 px ignore this metric entirely (spec).
//
// MS-SSIM 编排器：驱动 ATO_SSIM.compute 遍历最多 5 级，读回逐级 l/c/s 图，
// 求平均并按 Wang 权重合并。小区域（短边 < 176px）回退到单尺度 SSIM；
// 短边 < 11px 的区域完全忽略该指标（规格）。

using System;
using UnityEditor;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor.quality
{
    public static class MS_SSIM
    {
        private const int MaxScales = 5;
        private const int SingleScaleThreshold = 176;  // short side below -> single-scale / 短边低于此值 -> 单尺度
        private const int IgnoreThreshold = 11;        // short side below -> ignore / 短边低于此值 -> 忽略

        // Wang et al. weights for 5 scales / Wang 等人的 5 级权重
        private static readonly float[] Weights = { 0.0448f, 0.2856f, 0.3001f, 0.2363f, 0.1333f };

        private static ComputeShader _shader;
        private static bool _loadFailed;
        private static int _kEncodePair, _kBlurX, _kBlurY, _kStats, _kDown;

        private static ComputeShader Shader
        {
            get
            {
                if (_shader != null || _loadFailed) return _shader;
                _shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                    "Packages/net.fosa.avatar-texture-optimizer/Editor/Quality/Shaders/ATO_SSIM.compute");
                if (_shader == null)
                {
                    _loadFailed = true;
                    return null;
                }
                _kEncodePair = _shader.FindKernel("EncodePair");
                _kBlurX = _shader.FindKernel("BlurX");
                _kBlurY = _shader.FindKernel("BlurY");
                _kStats = _shader.FindKernel("ComputeStats");
                _kDown = _shader.FindKernel("Downsample2");
                return _shader;
            }
        }

        /// <summary>
        /// Compute (MS-)SSIM between two linear RenderTextures of the same size.
        /// Returns NaN when the metric is ignored (tiny regions).
        /// 计算两张同尺寸线性 RenderTexture 之间的 (MS-)SSIM。
        /// 指标被忽略（极小区域）时返回 NaN。
        /// </summary>
        public static float Evaluate(RenderTexture x, RenderTexture y)
        {
            if (Shader == null || x == null || y == null) return 1f; // no GPU -> assume passing / 无 GPU -> 视为通过
            int shortSide = Mathf.Min(x.width, x.height);
            if (shortSide < IgnoreThreshold) return float.NaN; // ignored per spec / 按规格忽略

            int scales = shortSide < SingleScaleThreshold ? 1 : MaxScales;

            var a = GPUImageOps.CreateRT(x.width, x.height);
            var b = GPUImageOps.CreateRT(y.width, y.height);
            var stats = GPUImageOps.CreateRT(x.width, x.height);

            try
            {
                // Encode the pair. / 编码对。
                _shader.SetTexture(_kEncodePair, "InA", x);
                _shader.SetTexture(_kEncodePair, "InB", y);
                _shader.SetTexture(_kEncodePair, "OutA", a);
                _shader.SetTexture(_kEncodePair, "OutB", b);
                _shader.SetVector("TexSize", Size4(x));
                _shader.Dispatch(_kEncodePair, Grp(x.width), Grp(x.height));

                float product = 1f;
                for (int scale = 0; scale < scales; scale++)
                {
                    int w = Mathf.Max(2, x.width >> scale);
                    int h = Mathf.Max(2, y.height >> scale);

                    // Blur both textures (horizontal then vertical, separable).
                    // 模糊两张纹理（可分离：先水平后垂直）。
                    var tmpA = GPUImageOps.CreateRT(w, h);
                    var tmpB = GPUImageOps.CreateRT(w, h);

                    BlurPair(_kBlurX, a, b, tmpA, tmpB);
                    BlurPair(_kBlurY, tmpA, tmpB, a, b);

                    // Compute l/c/s maps. / 计算 l/c/s 图。
                    _shader.SetTexture(_kStats, "InA", a);
                    _shader.SetTexture(_kStats, "InB", b);
                    _shader.SetTexture(_kStats, "OutA", stats);
                    _shader.SetVector("TexSize", Size4(a));
                    _shader.Dispatch(_kStats, Grp(w), Grp(h));

                    // Read back and average. / 读回并求平均。
                    var pixels = GPUImageOps.Readback(stats);
                    double lSum = 0, cSum = 0, sSum = 0;
                    int count = 0;
                    foreach (var p in pixels)
                    {
                        if (float.IsNaN(p.r) || float.IsNaN(p.g) || float.IsNaN(p.b)) continue;
                        lSum += p.r; cSum += p.g; sSum += p.b;
                        count++;
                    }
                    if (count == 0) { tmpA.Release(); tmpB.Release(); break; }
                    float l = (float)(lSum / count);
                    float c = (float)(cSum / count);
                    float s = (float)(sSum / count);

                    // Weight: luminance only at the coarsest scale; contrast and
                    // structure at every scale.
                    // 权重：亮度只在最粗一级；对比度与结构每级都计入。
                    float wl = (scale == scales - 1) ? Weights[scale] : 0f;
                    float wcs = Weights[scale];
                    product *= Mathf.Pow(Mathf.Clamp(l, 0f, 1f), wl);
                    product *= Mathf.Pow(Mathf.Clamp(c, 0f, 1f), wcs);
                    product *= Mathf.Pow(Mathf.Clamp(s, 0f, 1f), wcs);

                    tmpA.Release();
                    tmpB.Release();

                    // Downsample for the next scale. / 为下一级下采样。
                    if (scale < scales - 1)
                    {
                        int nw = Mathf.Max(1, w >> 1);
                        int nh = Mathf.Max(1, h >> 1);
                        var da = GPUImageOps.CreateRT(nw, nh);
                        var db = GPUImageOps.CreateRT(nw, nh);
                        _shader.SetTexture(_kDown, "InA", a);
                        _shader.SetTexture(_kDown, "InB", b);
                        _shader.SetTexture(_kDown, "OutA", da);
                        _shader.SetTexture(_kDown, "OutB", db);
                        _shader.SetVector("TexSize", Size4(a));
                        _shader.SetVector("OutSize", Size4(da));
                        _shader.Dispatch(_kDown, Grp(nw), Grp(nh));
                        a.Release(); b.Release();
                        a = da; b = db;
                    }
                }

                return Mathf.Clamp(product, 0f, 1f);
            }
            finally
            {
                a.Release();
                b.Release();
                stats.Release();
            }
        }

        private static void BlurPair(int kernel, RenderTexture inA, RenderTexture inB, RenderTexture outA, RenderTexture outB)
        {
            _shader.SetTexture(kernel, "InA", inA);
            _shader.SetTexture(kernel, "OutA", outA);
            _shader.SetVector("TexSize", Size4(inA));
            _shader.Dispatch(kernel, Grp(inA.width), Grp(inA.height));

            _shader.SetTexture(kernel, "InA", inB);
            _shader.SetTexture(kernel, "OutA", outB);
            _shader.SetVector("TexSize", Size4(inB));
            _shader.Dispatch(kernel, Grp(inB.width), Grp(inB.height));
        }

        private static int Grp(int v) => Mathf.Max(1, Mathf.CeilToInt(v / 8f));
        private static Vector4 Size4(RenderTexture rt) => new Vector4(rt.width, rt.height, 1f / rt.width, 1f / rt.height);
    }
}
