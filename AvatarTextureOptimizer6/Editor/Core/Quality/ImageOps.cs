using System;
using System.Threading.Tasks;
using UnityEngine;

namespace NetFosa.AvatarTextureOptimizer.Editor.Quality
{
    /// <summary>
    /// 图像运算：线性空间双线性重采样、预乘 alpha、区域提取、法线编解码。
    /// 像素格式约定：float 数组 [r,g,b,a] 交错，值域 0..1，线性空间。
    /// </summary>
    public static class ImageOps
    {
        // ---------------- sRGB <-> linear ----------------

        public static float SrgbToLinear(float c) => Utils.ColorSpace.SrgbToLinear(c);
        public static float LinearToSrgb(float c) => Utils.ColorSpace.LinearToSrgb(c);

        // ---------------- 提取区域（线性） ----------------

        /// <summary>
        /// 从 ARGB32 像素中提取 [x,y,w,h] 区域并转为线性 RGBA 交错数组。
        /// </summary>
        public static float[] ExtractRegionLinear(Color32[] px, int texW, int texH, int x, int y, int w, int h, bool srgb)
        {
            var dst = new float[w * h * 4];
            int i = 0;
            for (int py = 0; py < h; py++)
            {
                int sy = y + py;
                if (sy < 0 || sy >= texH) { i += w * 4; continue; }
                int rowBase = sy * texW + x;
                for (int px_ = 0; px_ < w; px_++)
                {
                    int sx = x + px_;
                    Color32 c = default;
                    if (sx >= 0 && sx < texW) c = px[rowBase + px_];
                    float r = c.r / 255f, g = c.g / 255f, b = c.b / 255f, a = c.a / 255f;
                    if (srgb)
                    {
                        r = SrgbToLinear(r); g = SrgbToLinear(g); b = SrgbToLinear(b);
                    }
                    dst[i++] = r; dst[i++] = g; dst[i++] = b; dst[i++] = a;
                }
            }
            return dst;
        }

        // ---------------- 双线性重采样 ----------------

        /// <summary>
        /// 双线性重采样（线性空间；premultiply 时先乘 alpha 再采样，用于透明贴图下采样）。
        /// srcW/H 源尺寸，dstW/H 目标尺寸。返回目标 RGBA 交错数组。
        /// </summary>
        public static float[] ResampleBilinear(float[] src, int srcW, int srcH, int dstW, int dstH, bool premultiply)
        {
            if (dstW <= 0 || dstH <= 0) return new float[dstW * dstH * 4];
            var dst = new float[dstW * dstH * 4];
            Parallel.For(0, dstH, py =>
            {
                float syf = (py + 0.5f) * srcH / dstH - 0.5f;
                int sy0 = Mathf.Clamp(Mathf.FloorToInt(syf), 0, srcH - 1);
                int sy1 = Mathf.Min(sy0 + 1, srcH - 1);
                float fy = syf - sy0;
                int row = py * dstW;
                for (int px = 0; px < dstW; px++)
                {
                    float sxf = (px + 0.5f) * srcW / dstW - 0.5f;
                    int sx0 = Mathf.Clamp(Mathf.FloorToInt(sxf), 0, srcW - 1);
                    int sx1 = Mathf.Min(sx0 + 1, srcW - 1);
                    float fx = sxf - sx0;

                    SampleLinear(src, srcW, sx0, sy0, fx, fy, sx1, sy1, out float r, out float g, out float b, out float a);

                    if (premultiply && a > 0f)
                    {
                        r *= a; g *= a; b *= a;
                    }
                    int o = (row + px) * 4;
                    dst[o] = r; dst[o + 1] = g; dst[o + 2] = b; dst[o + 3] = a;
                }
            });
            return dst;
        }

        private static void SampleLinear(float[] src, int srcW, int sx0, int sy0, float fx, float fy, int sx1, int sy1,
            out float r, out float g, out float b, out float a)
        {
            int o00 = (sy0 * srcW + sx0) * 4;
            int o10 = (sy0 * srcW + sx1) * 4;
            int o01 = (sy1 * srcW + sx0) * 4;
            int o11 = (sy1 * srcW + sx1) * 4;
            float w00 = (1 - fx) * (1 - fy), w10 = fx * (1 - fy), w01 = (1 - fx) * fy, w11 = fx * fy;
            r = src[o00] * w00 + src[o10] * w10 + src[o01] * w01 + src[o11] * w11;
            g = src[o00 + 1] * w00 + src[o10 + 1] * w10 + src[o01 + 1] * w01 + src[o11 + 1] * w11;
            b = src[o00 + 2] * w00 + src[o10 + 2] * w10 + src[o01 + 2] * w01 + src[o11 + 2] * w11;
            a = src[o00 + 3] * w00 + src[o10 + 3] * w10 + src[o01 + 3] * w01 + src[o11 + 3] * w11;
        }

        /// <summary>不透明贴图无需预乘；透明贴图预乘下采样后再反预乘。</summary>
        public static float[] DownscaleWithAlpha(float[] src, int srcW, int srcH, int dstW, int dstH, bool hasAlpha)
        {
            if (!hasAlpha) return ResampleBilinear(src, srcW, srcH, dstW, dstH, false);

            // 预乘 → 采样 → 反预乘
            var premul = ResampleBilinear(src, srcW, srcH, dstW, dstH, true);
            int n = dstW * dstH;
            for (int i = 0; i < n; i++)
            {
                float a = premul[i * 4 + 3];
                if (a > 1e-6f)
                {
                    premul[i * 4] /= a;
                    premul[i * 4 + 1] /= a;
                    premul[i * 4 + 2] /= a;
                }
            }
            return premul;
        }

        // ---------------- 法线 ----------------

        /// <summary>解码法线（RGB → 切线向量，线性）。</summary>
        public static Vector3 DecodeNormal(float r, float g, float b)
        {
            var v = new Vector3(r * 2f - 1f, g * 2f - 1f, b * 2f - 1f);
            float len = v.magnitude;
            if (len > 1e-6f) v /= len;
            return v;
        }

        public static void EncodeNormal(Vector3 n, out float r, out float g, out float b)
        {
            r = n.x * 0.5f + 0.5f;
            g = n.y * 0.5f + 0.5f;
            b = n.z * 0.5f + 0.5f;
        }

        // ---------------- 亮度 ----------------

        public static float Luminance(float r, float g, float b) => 0.2126f * r + 0.7152f * g + 0.0722f * b;
    }
}
