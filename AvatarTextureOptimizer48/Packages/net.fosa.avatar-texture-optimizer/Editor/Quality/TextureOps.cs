// Texture region sampling and resampling (linear space, premultiplied alpha).
// / 贴图区域采样与重采样（线性空间、预乘 alpha）。

using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor.quality
{
    /// <summary>
    /// Region extraction from cached Color32 data and bilinear resampling in linear space.
    /// / 从缓存的 Color32 数据提取区域，并在线性空间做双线性重采样。
    /// </summary>
    public static class TextureOps
    {
        /// <summary>Extract a region as linear RGBA (unpremultiplied). / 提取区域为线性 RGBA（非预乘）。</summary>
        public static float[] RegionRgbaLinear(byte[] srcRgba, int fullW, int fullH, int x0, int y0, int w, int h)
        {
            var dst = new float[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                int sy = Mathf.Clamp(y0 + y, 0, fullH - 1);
                for (int x = 0; x < w; x++)
                {
                    int sx = Mathf.Clamp(x0 + x, 0, fullW - 1);
                    int si = (sy * fullW + sx) * 4;
                    int di = (y * w + x) * 4;
                    float a = MetricMath.SrgbByteToLinear(srcRgba[si + 3]);
                    dst[di] = MetricMath.SrgbByteToLinear(srcRgba[si]) * a;      // premultiply / 预乘
                    dst[di + 1] = MetricMath.SrgbByteToLinear(srcRgba[si + 1]) * a;
                    dst[di + 2] = MetricMath.SrgbByteToLinear(srcRgba[si + 2]) * a;
                    dst[di + 3] = a;
                }
            }
            return dst;
        }

        /// <summary>Resize bilinearly with premultiplied alpha; returns unpremultiplied linear RGBA. / 预乘 alpha 双线性缩放，返回非预乘线性 RGBA。</summary>
        public static float[] ResizeBilinearPremultiplied(float[] src, int sw, int sh, int dw, int dh)
        {
            var dst = new float[dw * dh * 4];
            float sx = sw / (float)dw;
            float sy = sh / (float)dh;
            for (int y = 0; y < dh; y++)
            {
                float fy = (y + 0.5f) * sy - 0.5f;
                int y0 = Mathf.Clamp(Mathf.FloorToInt(fy), 0, sh - 1);
                int y1 = Mathf.Clamp(y0 + 1, 0, sh - 1);
                float ty = Mathf.Clamp01(fy - y0);
                for (int x = 0; x < dw; x++)
                {
                    float fx = (x + 0.5f) * sx - 0.5f;
                    int x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, sw - 1);
                    int x1 = Mathf.Clamp(x0 + 1, 0, sw - 1);
                    float tx = Mathf.Clamp01(fx - x0);
                    int di = (y * dw + x) * 4;
                    for (int c = 0; c < 4; c++)
                    {
                        float v = (src[(y0 * sw + x0) * 4 + c] * (1 - tx) + src[(y0 * sw + x1) * 4 + c] * tx) * (1 - ty) +
                                  (src[(y1 * sw + x0) * 4 + c] * (1 - tx) + src[(y1 * sw + x1) * 4 + c] * tx) * ty;
                        dst[di + c] = v;
                    }
                    // unpremultiply / 反预乘
                    float a = dst[di + 3];
                    if (a > 1e-6f)
                    {
                        dst[di] = Mathf.Min(1f, dst[di] / a);
                        dst[di + 1] = Mathf.Min(1f, dst[di + 1] / a);
                        dst[di + 2] = Mathf.Min(1f, dst[di + 2] / a);
                    }
                }
            }
            return dst;
        }

        /// <summary>RGBA linear (unpremultiplied) -> RGB linear luma image for SSIM/MS-SSIM. / 线性 RGBA 转 RGB 线性图（用于 SSIM）。</summary>
        public static float[] RgbaToRgb(float[] rgba, int w, int h)
        {
            var rgb = new float[w * h * 3];
            for (int i = 0; i < w * h; i++)
            {
                rgb[i * 3] = rgba[i * 4];
                rgb[i * 3 + 1] = rgba[i * 4 + 1];
                rgb[i * 3 + 2] = rgba[i * 4 + 2];
            }
            return rgb;
        }

        /// <summary>Extract alpha channel as linear floats. / 提取线性 alpha 通道。</summary>
        public static float[] Alpha(float[] rgba, int w, int h)
        {
            var a = new float[w * h];
            for (int i = 0; i < w * h; i++) a[i] = rgba[i * 4 + 3];
            return a;
        }

        /// <summary>Decode normal map RGBA (rgb*2-1) into a float array. / 解码法线贴图（rgb*2-1）。</summary>
        public static float[] DecodeNormals(float[] rgba, int w, int h)
        {
            var n = new float[w * h * 3];
            for (int i = 0; i < w * h; i++)
            {
                n[i * 3] = rgba[i * 4] * 2f - 1f;
                n[i * 3 + 1] = rgba[i * 4 + 1] * 2f - 1f;
                n[i * 3 + 2] = rgba[i * 4 + 2] * 2f - 1f;
            }
            return n;
        }

        /// <summary>Convert linear RGB back to sRGB floats (for CIEDE2000 which expects sRGB). / 线性 RGB 转回 sRGB 浮点（CIEDE2000 需要）。</summary>
        public static float[] LinearRgbToSrgb(float[] rgb, int w, int h)
        {
            var srgb = new float[w * h * 3];
            for (int i = 0; i < w * h; i++)
            {
                srgb[i * 3] = SrgbFromLinear(rgb[i * 3]);
                srgb[i * 3 + 1] = SrgbFromLinear(rgb[i * 3 + 1]);
                srgb[i * 3 + 2] = SrgbFromLinear(rgb[i * 3 + 2]);
            }
            return srgb;
        }

        private static float SrgbFromLinear(float l)
        {
            int idx = Mathf.Clamp((int)(l * 65535f), 0, 65535);
            // reuse the LUT via MetricMath by computing directly (LUT is internal)
            return l <= 0.0031308f ? l * 12.92f : 1.055f * Mathf.Pow(l, 1f / 2.4f) - 0.055f;
        }
    }
}
