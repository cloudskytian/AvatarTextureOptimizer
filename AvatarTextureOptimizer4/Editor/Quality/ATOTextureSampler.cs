// Avatar Texture Optimizer (ATO)
// Rasterizes a UV island into a pixel buffer at an arbitrary resolution, sampling the
// original texture bilinearly. Uses a spatial hash over triangles for speed.
// 以任意分辨率把 UV 岛光栅化为像素缓冲，对原贴图做双线性采样。用三角形空间哈希加速。

using System.Collections.Generic;
using UnityEngine;

namespace NetFosa.ATO
{
    /// <summary>
    /// Island rasterizer + bilinear upsampler. / 岛光栅化器 + 双线性上采样器。
    /// </summary>
    public static class ATOTextureSampler
    {
        private const int BucketSize = 32;

        // Readable-copy cache so non-readable user textures are read back once, not per call.
        // 可读副本缓存：不可读的用户贴图只读回一次，而非每次调用都读回。
        private static readonly Dictionary<Texture2D, Texture2D> _readableCache = new Dictionary<Texture2D, Texture2D>();

        /// <summary>Resolve a readable texture for sampling. / 解析出可采样用的可读贴图。</summary>
        private static Texture2D Readable(Texture2D tex)
        {
            if (tex == null || tex.isReadable) return tex;
            if (_readableCache.TryGetValue(tex, out var r)) return r;
            r = ATOUtil.EnsureReadable(tex);
            _readableCache[tex] = r;
            return r;
        }

        /// <summary>Release cached readable copies (end of build). / 释放缓存的可读副本（构建结束）。</summary>
        public static void ClearCache()
        {
            foreach (var kvp in _readableCache)
                if (kvp.Value != null && kvp.Value.name.EndsWith("_readable"))
                    Object.DestroyImmediate(kvp.Value);
            _readableCache.Clear();
        }

        /// <summary>
        /// Rasterize an island into (outW x outH) straight sRGB colors + coverage mask.
        /// 把岛光栅化为 (outW x outH) 的直通 sRGB 颜色 + 覆盖掩码。
        /// </summary>
        public static void Rasterize(Texture2D tex, ATOIsland isl, int outW, int outH,
            out Color[] pixels, out byte[] mask, bool premultiplyAlpha = false)
        {
            tex = Readable(tex);
            int texW = tex.width, texH = tex.height;
            outW = Mathf.Max(1, outW); outH = Mathf.Max(1, outH);
            pixels = new Color[outW * outH];
            mask = new byte[outW * outH];

            var span = isl.maxUV - isl.minUV;
            if (span.x <= 0f || span.y <= 0f) return;

            // Read only the bounding region of the texture. / 只读取贴图的包围区域。
            int x0 = Mathf.Clamp(Mathf.FloorToInt(isl.minUV.x * texW) - 1, 0, texW - 1);
            int x1 = Mathf.Clamp(Mathf.CeilToInt(isl.maxUV.x * texW) + 1, 0, texW - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(isl.minUV.y * texH) - 1, 0, texH - 1);
            int y1 = Mathf.Clamp(Mathf.CeilToInt(isl.maxUV.y * texH) + 1, 0, texH - 1);
            int rw = x1 - x0 + 1, rh = y1 - y0 + 1;

            Color[] region;
            try { region = tex.GetPixels(x0, y0, rw, rh); }
            catch (UnityException)
            {
                ATOLogger.Warn($"Texture '{tex.name}' is not readable; falling back to full read. / 贴图 '{tex.name}' 不可读，回退为整图读取。");
                region = tex.GetPixels();
                x0 = 0; y0 = 0; rw = texW; rh = texH;
            }

            // Spatial hash of triangles (in pixel space). / 三角形空间哈希（像素空间）。
            var buckets = new Dictionary<long, List<int>>();
            int triCount = isl.triangles.Length / 3;
            for (int t = 0; t < triCount; t++)
            {
                var a = UvToRegionPixel(isl.uv[isl.triangles[t * 3]], texW, texH, x0, y0);
                var b = UvToRegionPixel(isl.uv[isl.triangles[t * 3 + 1]], texW, texH, x0, y0);
                var c = UvToRegionPixel(isl.uv[isl.triangles[t * 3 + 2]], texW, texH, x0, y0);
                int bx0 = Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x)) / BucketSize);
                int bx1 = Mathf.FloorToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x)) / BucketSize);
                int by0 = Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y)) / BucketSize);
                int by1 = Mathf.FloorToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y)) / BucketSize);
                for (int by = by0; by <= by1; by++)
                    for (int bx = bx0; bx <= bx1; bx++)
                    {
                        long key = (long)by << 32 | (uint)bx;
                        if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = new List<int>();
                        list.Add(t);
                    }
            }

            float invW = 1f / outW, invH = 1f / outH;
            for (int py = 0; py < outH; py++)
            {
                for (int px = 0; px < outW; px++)
                {
                    // UV of this pixel center within the island bbox. / 该像素中心在岛包围盒内的 UV。
                    float u = isl.minUV.x + (px + 0.5f) * invW * span.x;
                    float v = isl.minUV.y + (py + 0.5f) * invH * span.y;
                    if (!InsideIsland(isl, u, v, buckets, texW, texH, x0, y0))
                    {
                        pixels[py * outW + px] = Color.clear;
                        mask[py * outW + px] = 0;
                        continue;
                    }
                    mask[py * outW + px] = 1;
                    pixels[py * outW + px] = SampleRegion(region, rw, rh, u, v, texW, texH, x0, y0, premultiplyAlpha);
                }
            }
        }

        private static Vector2 UvToRegionPixel(Vector2 uv, int texW, int texH, int x0, int y0)
        {
            return new Vector2(uv.x * texW - x0, uv.y * texH - y0);
        }

        private static bool InsideIsland(ATOIsland isl, float u, float v,
            Dictionary<long, List<int>> buckets, int texW, int texH, int x0, int y0)
        {
            var px = UvToRegionPixel(new Vector2(u, v), texW, texH, x0, y0);
            int bx = Mathf.FloorToInt(px.x / BucketSize);
            int by = Mathf.FloorToInt(px.y / BucketSize);
            long key = (long)by << 32 | (uint)bx;
            if (!buckets.TryGetValue(key, out var list)) return false;

            const float eps = 1e-6f;
            foreach (var t in list)
            {
                var a = isl.uv[isl.triangles[t * 3]];
                var b = isl.uv[isl.triangles[t * 3 + 1]];
                var c = isl.uv[isl.triangles[t * 3 + 2]];
                if (PointInTriangle(u, v, a, b, c, eps)) return true;
            }
            return false;
        }

        private static bool PointInTriangle(float u, float v, Vector2 a, Vector2 b, Vector2 c, float eps)
        {
            float d1 = Sign(u, v, a, b), d2 = Sign(u, v, b, c), d3 = Sign(u, v, c, a);
            bool neg = d1 < -eps || d2 < -eps || d3 < -eps;
            bool pos = d1 > eps || d2 > eps || d3 > eps;
            return !(neg && pos);
        }

        private static float Sign(float u, float v, Vector2 p1, Vector2 p2)
            => (u - p2.x) * (p1.y - p2.y) - (p1.x - p2.x) * (v - p2.y);

        private static Color SampleRegion(Color[] region, int rw, int rh, float u, float v, int texW, int texH,
            int x0, int y0, bool premultiplyAlpha)
        {
            float fx = u * texW - x0 - 0.5f;
            float fy = v * texH - y0 - 0.5f;
            int x = Mathf.FloorToInt(fx), y = Mathf.FloorToInt(fy);
            float tx = fx - x, ty = fy - y;
            int xc = Mathf.Clamp(x, 0, rw - 1), xc1 = Mathf.Clamp(x + 1, 0, rw - 1);
            int yc = Mathf.Clamp(y, 0, rh - 1), yc1 = Mathf.Clamp(y + 1, 0, rh - 1);
            var c00 = region[yc * rw + xc];
            var c10 = region[yc * rw + xc1];
            var c01 = region[yc1 * rw + xc];
            var c11 = region[yc1 * rw + xc1];
            if (premultiplyAlpha)
            {
                c00 = Premul(c00); c10 = Premul(c10); c01 = Premul(c01); c11 = Premul(c11);
                var r = Color.Lerp(Color.Lerp(c00, c10, tx), Color.Lerp(c01, c11, tx), ty);
                return Unpremul(r);
            }
            return Color.Lerp(Color.Lerp(c00, c10, tx), Color.Lerp(c01, c11, tx), ty);
        }

        private static Color Premul(Color c) => new Color(c.r * c.a, c.g * c.a, c.b * c.a, c.a);
        private static Color Unpremul(Color c) => c.a > 1e-6f ? new Color(c.r / c.a, c.g / c.a, c.b / c.a, c.a) : Color.clear;

        /// <summary>Bilinear upsample a buffer to a new size. / 双线性上采样缓冲到新尺寸。</summary>
        public static void BilinearUpsample(Color[] src, int sw, int sh, Color[] dst, int dw, int dh)
        {
            for (int y = 0; y < dh; y++)
            {
                float fy = (y + 0.5f) * sh / dh - 0.5f;
                int y0 = Mathf.FloorToInt(fy); float ty = fy - y0;
                int yc = Mathf.Clamp(y0, 0, sh - 1), yc1 = Mathf.Clamp(y0 + 1, 0, sh - 1);
                for (int x = 0; x < dw; x++)
                {
                    float fx = (x + 0.5f) * sw / dw - 0.5f;
                    int x0 = Mathf.FloorToInt(fx); float tx = fx - x0;
                    int xc = Mathf.Clamp(x0, 0, sw - 1), xc1 = Mathf.Clamp(x0 + 1, 0, sw - 1);
                    var c00 = src[yc * sw + xc]; var c10 = src[yc * sw + xc1];
                    var c01 = src[yc1 * sw + xc]; var c11 = src[yc1 * sw + xc1];
                    dst[y * dw + x] = Color.Lerp(Color.Lerp(c00, c10, tx), Color.Lerp(c01, c11, tx), ty);
                }
            }
        }

        /// <summary>Downsample with premultiplied-alpha weighting (for transparent textures). / 预乘 alpha 加权下采样（用于透明贴图）。</summary>
        public static void PremultipliedDownsample(Color[] src, int sw, int sh, Color[] dst, int dw, int dh)
        {
            for (int y = 0; y < dh; y++)
            {
                for (int x = 0; x < dw; x++)
                {
                    float ar = 0f, ag = 0f, ab = 0f, aa = 0f, cnt = 0f;
                    int sx0 = Mathf.FloorToInt(x * (float)sw / dw);
                    int sx1 = Mathf.Max(sx0 + 1, Mathf.FloorToInt((x + 1) * (float)sw / dw));
                    int sy0 = Mathf.FloorToInt(y * (float)sh / dh);
                    int sy1 = Mathf.Max(sy0 + 1, Mathf.FloorToInt((y + 1) * (float)sh / dh));
                    for (int sy = sy0; sy < sy1; sy++)
                        for (int sx = sx0; sx < sx1; sx++)
                        {
                            var c = src[Mathf.Clamp(sy, 0, sh - 1) * sw + Mathf.Clamp(sx, 0, sw - 1)];
                            ar += c.r * c.a; ag += c.g * c.a; ab += c.b * c.a; aa += c.a; cnt++;
                        }
                    if (cnt > 0)
                    {
                        ar /= cnt; ag /= cnt; ab /= cnt; aa /= cnt;
                        dst[y * dw + x] = aa > 1e-6f ? new Color(ar / aa, ag / aa, ab / aa, aa) : Color.clear;
                    }
                }
            }
        }
    }
}
