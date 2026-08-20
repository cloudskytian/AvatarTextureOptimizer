using System.Collections.Generic;
using UnityEngine;

namespace Fosa.ATO.Editor
{
    /// <summary>
    /// 处理阶段：按目标质量算法缩放每个 UV 岛（不生成图集时缩放整贴图）。
    /// 质量评估用线性空间、透明预乘；目标质量不为 1 时纯色岛短路缩到 min(4, 短边)。
    ///
    /// Processing: scale each UV island by the target quality algorithm (whole texture when atlas off).
    /// </summary>
    public class ATOProcessor
    {
        private readonly nadena.dev.ndmf.BuildContext _ctx;
        private readonly ATOBuildData _data;
        private readonly AvatarTextureOptimizer _comp;
        private readonly ATOQualityParams _qp;

        // 贴图像素缓存（线性 RGBA 浮点，预乘 alpha）。Texture pixel cache (linear RGBA float, premultiplied).
        private readonly Dictionary<Texture2D, (float[] px, int w, int h)> _cache = new();

        public ATOProcessor(nadena.dev.ndmf.BuildContext ctx, ATOBuildData data)
        {
            _ctx = ctx;
            _data = data;
            _comp = data.component;
            _qp = _comp.GetEffectiveQualityParams();
        }

        public void Run()
        {
            using var step = ATOLogger.Step("Scale UV islands by target quality");
            ATOLogger.Begin("stage.process");

            bool lossless = _comp.qualityPreset == ATOQualityPreset.Custom &&
                            Mathf.Abs(_qp.msSsimThreshold - 1f) < 1e-4f &&
                            _qp.deltaEThreshold <= 0.01f;

            int index = 0;
            foreach (var island in _data.allIslands)
            {
                ATOLogger.ThrowIfCancelled();
                var tex = island.texture.texture;
                if (!_cache.TryGetValue(tex, out var cached))
                {
                    cached = ReadTextureLinear(tex);
                    _cache[tex] = cached;
                }

                // 检测纯色岛。Detect solid-color island.
                DetectSolidColor(island, cached);

                if (lossless)
                {
                    // 目标质量为 1：跳过缩放，原样拷贝。
                    island.skipScale = true;
                    island.packedScale = Vector2.one;
                }
                else if (island.isSolidColor)
                {
                    // 纯色：直接缩到 min(4, 短边)。
                    float shortSide = Mathf.Min(island.bounds.width * tex.width, island.bounds.height * tex.height);
                    float target = Mathf.Min(4f, shortSide);
                    island.packedScale = new Vector2(
                        target / Mathf.Max(1f, island.bounds.width * tex.width),
                        target / Mathf.Max(1f, island.bounds.height * tex.height));
                }
                else
                {
                    island.packedScale = BinarySearchScale(island, cached);
                }

                // 像素密度钳制：按岛的世界面积与用户密度上下限（无损挡位跳过）。
                if (!island.skipScale) ClampByPixelDensity(island);

                if (++index % 8 == 0)
                    ATOLogger.Report((float)index / Mathf.Max(1, _data.allIslands.Count));
            }

            ATOLogger.Report(1f);
            ATOLogger.Info($"Processed {_data.allIslands.Count} islands (quality={_comp.qualityPreset}, lossless={lossless})");
        }

        // ---- 二分搜索 ----
        private Vector2 BinarySearchScale(ATOIsland island, (float[] px, int w, int h) src)
        {
            // 均匀缩放：二分找最小 scale 使质量达标。
            float lo = 0f, hi = 1f;
            float best = 1f;
            for (int iter = 0; iter < 12; iter++)
            {
                float mid = (lo + hi) * 0.5f;
                if (EvaluateQuality(island, src, new Vector2(mid, mid)))
                {
                    best = mid;
                    hi = mid;
                }
                else
                {
                    lo = mid;
                }
            }
            // 各向异性细化：先均匀达标，再双轴独立二分。
            var scale = RefineAnisotropic(island, src, new Vector2(best, best));
            return scale;
        }

        private Vector2 RefineAnisotropic(ATOIsland island, (float[] px, int w, int h) src, Vector2 start)
        {
            var result = start;
            for (int axis = 0; axis < 2; axis++)
            {
                float lo = 0f, hi = result[axis];
                float best = result[axis];
                for (int iter = 0; iter < 8; iter++)
                {
                    float mid = (lo + hi) * 0.5f;
                    var test = result;
                    test[axis] = mid;
                    if (EvaluateQuality(island, src, test)) { best = mid; hi = mid; }
                    else lo = mid;
                }
                result[axis] = best;
            }
            return result;
        }

        private bool EvaluateQuality(ATOIsland island, (float[] px, int w, int h) src, Vector2 scale)
        {
            // 用缩小后的岛覆盖区双线性下采样，再上采样回原尺寸比较。
            var bounds = island.bounds;
            int sx = Mathf.Max(1, Mathf.RoundToInt(bounds.width * src.w));
            int sy = Mathf.Max(1, Mathf.RoundToInt(bounds.height * src.h));
            int dx = Mathf.Max(1, Mathf.RoundToInt(sx * scale.x));
            int dy = Mathf.Max(1, Mathf.RoundToInt(sy * scale.y));

            // 裁剪岛区域（线性 RGBA 预乘）。
            var crop = CropRegion(src, bounds);
            var down = Resample(crop.px, crop.w, crop.h, dx, dy, island.isNormalMap ? ResampleMode.Normal : ResampleMode.Color);
            var up = Resample(down.px, dx, dy, crop.w, crop.h, island.isNormalMap ? ResampleMode.Normal : ResampleMode.Color);

            return CompareQuality(island, crop, up);
        }

        public enum ResampleMode { Color, Normal }

        private bool CompareQuality(ATOIsland island, (float[] px, int w, int h) orig, (float[] px, int w, int h) resampled)
        {
            int n = orig.px.Length;
            var a = orig.px;
            var b = resampled.px;

            float shortSide = Mathf.Min(orig.w, orig.h);

            if (island.isNormalMap)
            {
                // 法线：角度误差 + p95。
                var angles = new float[n / 4];
                int ai = 0;
                for (int i = 0; i < n; i += 4)
                {
                    var n1 = new Vector3(a[i] * 2 - 1, a[i + 1] * 2 - 1, a[i + 2] * 2 - 1);
                    var n2 = new Vector3(b[i] * 2 - 1, b[i + 1] * 2 - 1, b[i + 2] * 2 - 1);
                    angles[ai++] = ATOQualityMetrics.NormalAngleErrorDeg(n1, n2);
                }
                float p95 = ATOQualityMetrics.Percentile95(angles);
                return p95 <= _qp.normalAngleThresholdDeg;
            }

            // 灰度：逐通道线性 RMSE 取最差。
            if (island.type == ATOTextureType.Grayscale || island.type == ATOTextureType.Mask ||
                island.type == ATOTextureType.Occlusion || island.type == ATOTextureType.MetallicGloss)
            {
                float worst = 0;
                for (int c = 0; c < 3; c++)
                {
                    var ca = new float[n / 4]; var cb = new float[n / 4];
                    for (int i = 0, j = c; i < ca.Length; i++, j += 4) { ca[i] = a[j]; cb[i] = b[j]; }
                    worst = Mathf.Max(worst, ATOQualityMetrics.ChannelRMSE(ca, cb));
                }
                return worst <= _qp.grayscaleRmseThreshold;
            }

            // 主色：MS-SSIM + ΔE。视短边是否 <11 忽略 SSIM。
            float dEsum = 0; int dEn = 0;
            var lumA = new float[n / 4]; var lumB = new float[n / 4];
            for (int i = 0, j = 0; i < lumA.Length; i++, j += 4)
            {
                var ca = new Vector3(a[j], a[j + 1], a[j + 2]);
                var cb = new Vector3(b[j], b[j + 1], b[j + 2]);
                // 注意：a/b 是线性空间，SSIM 用亮度（线性）。
                lumA[i] = Luminance(ca); lumB[i] = Luminance(cb);
                dEsum += ATOQualityMetrics.DeltaE2000(
                    ATOQualityMetrics.LinearRGBToLab(ca),
                    ATOQualityMetrics.LinearRGBToLab(cb));
                dEn++;
            }
            float dE = dEsum / Mathf.Max(1, dEn);
            if (dE > _qp.deltaEThreshold) return false;

            if (shortSide >= 11)
            {
                double ssim = shortSide < 176
                    ? ATOQualityMetrics.SSIM(lumA, lumB, orig.w, orig.h)
                    : ATOQualityMetrics.MSSSIM(lumA, lumB, orig.w, orig.h);
                if (ssim < _qp.msSsimThreshold) return false;
            }

            // alpha 评估（取最严苛）。Alpha evaluation (strictest).
            if (island.texture.hasAlpha)
            {
                var aa = new float[n / 4]; var ab = new float[n / 4];
                for (int i = 0, j = 3; i < aa.Length; i++, j += 4) { aa[i] = a[j]; ab[i] = b[j]; }
                float iou = ATOQualityMetrics.CutoutIoU(aa, ab, 0.5f);
                if (iou < _qp.alphaThreshold) return false;
            }
            return true;
        }

        private static float Luminance(Vector3 lin) =>
            0.2126f * lin.x + 0.7152f * lin.y + 0.0722f * lin.z;

        // ---- 像素密度钳制 ----
        private void ClampByPixelDensity(ATOIsland island)
        {
            if (island.worldArea <= 1e-6f) return;
            // 当前分辨率（px/m）与上下限比较。
            float pxPerMeterX = island.bounds.width * island.texture.width / Mathf.Sqrt(island.worldArea);
            float minD = _comp.minPixelDensity, maxD = _comp.maxPixelDensity;
            if (pxPerMeterX > maxD)
            {
                float k = maxD / pxPerMeterX;
                island.packedScale *= k;
            }
            else if (pxPerMeterX < minD)
            {
                float k = Mathf.Min(1f, minD / pxPerMeterX);
                island.packedScale *= k;
            }
            // 同时受原贴图物理尺寸钳制：不能超过原尺寸。
            island.packedScale = new Vector2(
                Mathf.Min(1f, island.packedScale.x),
                Mathf.Min(1f, island.packedScale.y));
        }

        private void DetectSolidColor(ATOIsland island, (float[] px, int w, int h) src)
        {
            var bounds = island.bounds;
            int sx = Mathf.Max(1, Mathf.RoundToInt(bounds.width * src.w));
            int sy = Mathf.Max(1, Mathf.RoundToInt(bounds.height * src.h));
            int x0 = Mathf.Clamp(Mathf.RoundToInt(bounds.x * src.w), 0, src.w - 1);
            int y0 = Mathf.Clamp(Mathf.RoundToInt(bounds.y * src.h), 0, src.h - 1);

            float r0 = -1, g0 = -1, b0 = -1, a0 = -1; bool first = true;
            for (int y = y0; y < Mathf.Min(src.h, y0 + sy); y++)
                for (int x = x0; x < Mathf.Min(src.w, x0 + sx); x++)
                {
                    int i = (y * src.w + x) * 4;
                    if (first) { r0 = src.px[i]; g0 = src.px[i + 1]; b0 = src.px[i + 2]; a0 = src.px[i + 3]; first = false; }
                    else if (Mathf.Abs(src.px[i] - r0) > 1e-4f || Mathf.Abs(src.px[i + 1] - g0) > 1e-4f ||
                             Mathf.Abs(src.px[i + 2] - b0) > 1e-4f || Mathf.Abs(src.px[i + 3] - a0) > 1e-4f)
                    { return; }
                }
            island.isSolidColor = true;
            island.solidColor = new Color(r0, g0, b0, a0);
        }

        // ---- 纹理读取（线性 RGBA 预乘） ----
        public static (float[] px, int w, int h) ReadTextureLinear(Texture2D tex)
        {
            int w = tex.width, h = tex.height;
            // 通过 Linear RT 读取：线性色彩空间工程中已自动做 sRGB→线性转换。
            // 仅在 Gamma 工程且贴图为 sRGB 时，才需要手动转换。
            Color[] cols = ReadPixelsMip0(tex, w, h);
            bool needConvert = UnityEditor.PlayerSettings.colorSpace == ColorSpace.Gamma && IsSRGB(tex);
            var px = new float[w * h * 4];
            for (int i = 0; i < cols.Length; i++)
            {
                var c = cols[i];
                float r = c.r, g = c.g, b = c.b;
                if (needConvert)
                {
                    r = ATOQualityMetrics.SRGBToLinear(r);
                    g = ATOQualityMetrics.SRGBToLinear(g);
                    b = ATOQualityMetrics.SRGBToLinear(b);
                }
                float a = c.a;
                // 预乘 alpha。
                px[i * 4 + 0] = r * a; px[i * 4 + 1] = g * a; px[i * 4 + 2] = b * a; px[i * 4 + 3] = a;
            }
            return (px, w, h);
        }

        private static Color[] ReadPixelsMip0(Texture2D tex, int w, int h)
        {
            var tmp = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var prev = RenderTexture.active;
            RenderTexture.active = tmp;
            try
            {
                // 用 Graphics.Blit 读取（绕过 readable 限制）。Read via blit (bypasses readable).
                Graphics.Blit(tex, tmp);
                var result = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
                result.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                var cols = result.GetPixels();
                Object.DestroyImmediate(result);
                return cols;
            }
            finally
            {
                RenderTexture.active = prev;
                tmp.Release();
                Object.DestroyImmediate(tmp);
            }
        }

        private static bool IsSRGB(Texture2D tex)
        {
            var path = UnityEditor.AssetDatabase.GetAssetPath(tex);
            var imp = UnityEditor.AssetImporter.GetAtPath(path) as UnityEditor.TextureImporter;
            return imp != null ? imp.sRGBTexture : true;
        }

        // ---- 裁剪 + 双线性重采样 ----
        private static (float[] px, int w, int h) CropRegion((float[] px, int w, int h) src, Rect bounds)
        {
            int w = Mathf.Max(1, Mathf.RoundToInt(bounds.width * src.w));
            int h = Mathf.Max(1, Mathf.RoundToInt(bounds.height * src.h));
            int x0 = Mathf.Clamp(Mathf.RoundToInt(bounds.x * src.w), 0, src.w - 1);
            int y0 = Mathf.Clamp(Mathf.RoundToInt(bounds.y * src.h), 0, src.h - 1);
            var outPx = new float[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int sx = Mathf.Clamp(x0 + x, 0, src.w - 1);
                    int sy = Mathf.Clamp(y0 + y, 0, src.h - 1);
                    int si = (sy * src.w + sx) * 4;
                    int di = (y * w + x) * 4;
                    outPx[di] = src.px[si]; outPx[di + 1] = src.px[si + 1];
                    outPx[di + 2] = src.px[si + 2]; outPx[di + 3] = src.px[si + 3];
                }
            return (outPx, w, h);
        }

        public static (float[] px, int w, int h) Resample(float[] src, int sw, int sh, int dw, int dh, ResampleMode mode)
        {
            var outPx = new float[dw * dh * 4];
            for (int y = 0; y < dh; y++)
                for (int x = 0; x < dw; x++)
                {
                    float u = (x + 0.5f) / dw * sw - 0.5f;
                    float v = (y + 0.5f) / dh * sh - 0.5f;
                    int x0 = Mathf.FloorToInt(u), y0 = Mathf.FloorToInt(v);
                    float fx = u - x0, fy = v - y0;
                    int x1 = Mathf.Min(sw - 1, x0 + 1), y1 = Mathf.Min(sh - 1, y0 + 1);
                    x0 = Mathf.Max(0, x0); y0 = Mathf.Max(0, y0);

                    int i00 = (y0 * sw + x0) * 4, i01 = (y0 * sw + x1) * 4;
                    int i10 = (y1 * sw + x0) * 4, i11 = (y1 * sw + x1) * 4;

                    int di = (y * dw + x) * 4;
                    for (int c = 0; c < 4; c++)
                    {
                        float top = src[i00 + c] * (1 - fx) + src[i01 + c] * fx;
                        float bot = src[i10 + c] * (1 - fx) + src[i11 + c] * fx;
                        outPx[di + c] = top * (1 - fy) + bot * fy;
                    }
                    if (mode == ResampleMode.Normal)
                    {
                        // 法线：重归一化。
                        var n = new Vector3(outPx[di] * 2 - 1, outPx[di + 1] * 2 - 1, outPx[di + 2] * 2 - 1);
                        n.Normalize();
                        outPx[di] = n.x * 0.5f + 0.5f; outPx[di + 1] = n.y * 0.5f + 0.5f; outPx[di + 2] = n.z * 0.5f + 0.5f;
                    }
                }
            return (outPx, dw, dh);
        }
    }
}
