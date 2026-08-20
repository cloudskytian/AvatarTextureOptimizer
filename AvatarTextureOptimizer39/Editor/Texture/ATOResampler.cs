// Avatar Texture Optimizer (ATO)
// Bilingual comments: English + Simplified Chinese. 双语注释：英文 + 简体中文。

using UnityEngine;

namespace AvatarTextureOptimizer.Editor.Texture
{
    /// <summary>
    /// Resampling primitives shared by the quality evaluator and the atlas builder so the
    /// evaluated result exactly matches the produced result.
    ///  - Downsample: area (box) average in linear space; transparent textures premultiply
    ///    alpha first. 下采样：线性空间面积（盒）平均；透明贴图先预乘 alpha。
    ///  - Upsample: bilinear. 上采样：双线性。
    ///
    /// 供质量评估与图集构建共用的重采样原语，保证评估结果与产物一致。
    /// </summary>
    public static class ATOResampler
    {
        /// <summary>
        /// Area-average downsample of a 2D color buffer (linear space, premultiplied alpha).
        /// 二维颜色缓冲的面积平均下采样（线性空间，预乘 alpha）。
        /// </summary>
        public static Color[] Downsample(Color[] src, int srcW, int srcH, int dstW, int dstH, bool premultiply)
        {
            var dst = new Color[dstW * dstH];
            if (dstW == srcW && dstH == srcH)
            {
                System.Array.Copy(src, dst, src.Length);
                return dst;
            }

            float sx = (float)srcW / dstW;
            float sy = (float)srcH / dstH;

            for (int y = 0; y < dstH; y++)
            {
                float y0 = y * sy, y1 = Mathf.Min((y + 1) * sy, srcH);
                for (int x = 0; x < dstW; x++)
                {
                    float x0 = x * sx, x1 = Mathf.Min((x + 1) * sx, srcW);

                    float r = 0, g = 0, b = 0, a = 0, w = 0;
                    int iy0 = Mathf.FloorToInt(y0), iy1 = Mathf.CeilToInt(y1);
                    int ix0 = Mathf.FloorToInt(x0), ix1 = Mathf.CeilToInt(x1);

                    for (int iy = iy0; iy < iy1; iy++)
                    {
                        int cy = Mathf.Clamp(iy, 0, srcH - 1);
                        float wy = Overlap(y0, y1, iy, iy + 1);
                        for (int ix = ix0; ix < ix1; ix++)
                        {
                            int cx = Mathf.Clamp(ix, 0, srcW - 1);
                            float wx = Overlap(x0, x1, ix, ix + 1);
                            float weight = wx * wy;
                            var c = src[cy * srcW + cx];
                            float ar = premultiply ? c.a : 1f;
                            r += c.r * ar * weight;
                            g += c.g * ar * weight;
                            b += c.b * ar * weight;
                            a += c.a * weight;
                            w += weight;
                        }
                    }

                    if (w <= 0f) { dst[y * dstW + x] = Color.clear; continue; }

                    float invW = 1f / w;
                    r *= invW; g *= invW; b *= invW; a *= invW;

                    if (premultiply && a > 1e-5f)
                    {
                        r /= a; g /= a; b /= a;
                    }

                    dst[y * dstW + x] = new Color(r, g, b, a);
                }
            }

            return dst;
        }

        /// <summary>
        /// Bilinear upsample of a 2D color buffer (linear space, premultiplied alpha).
        /// 二维颜色缓冲的双线性上采样（线性空间，预乘 alpha）。
        /// </summary>
        public static Color[] BilinearUpsample(Color[] src, int srcW, int srcH, int dstW, int dstH,
            bool premultiply)
        {
            var dst = new Color[dstW * dstH];
            if (dstW == srcW && dstH == srcH)
            {
                System.Array.Copy(src, dst, src.Length);
                return dst;
            }

            for (int y = 0; y < dstH; y++)
            {
                float fy = (y + 0.5f) * srcH / dstH - 0.5f;
                int y0 = Mathf.FloorToInt(fy);
                int y1 = y0 + 1;
                float ty = fy - y0;
                y0 = Mathf.Clamp(y0, 0, srcH - 1);
                y1 = Mathf.Clamp(y1, 0, srcH - 1);

                for (int x = 0; x < dstW; x++)
                {
                    float fx = (x + 0.5f) * srcW / dstW - 0.5f;
                    int x0 = Mathf.FloorToInt(fx);
                    int x1 = x0 + 1;
                    float tx = fx - x0;
                    x0 = Mathf.Clamp(x0, 0, srcW - 1);
                    x1 = Mathf.Clamp(x1, 0, srcW - 1);

                    var c00 = src[y0 * srcW + x0];
                    var c01 = src[y0 * srcW + x1];
                    var c10 = src[y1 * srcW + x0];
                    var c11 = src[y1 * srcW + x1];

                    // Premultiplied-aware bilinear. 预乘感知的双线性。
                    Color top = PremulLerp(c00, c01, tx, premultiply);
                    Color bot = PremulLerp(c10, c11, tx, premultiply);
                    dst[y * dstW + x] = PremulLerp(top, bot, ty, premultiply);
                }
            }

            return dst;
        }

        private static Color PremulLerp(Color a, Color b, float t, bool premultiply)
        {
            if (!premultiply) return Color.Lerp(a, b, t);

            float ar = a.r * a.a, ag = a.g * a.a, ab = a.b * a.a;
            float br = b.r * b.a, bg = b.g * b.a, bb = b.b * b.a;

            float r = Mathf.Lerp(ar, br, t);
            float g = Mathf.Lerp(ag, bg, t);
            float bl = Mathf.Lerp(ab, bb, t);
            float al = Mathf.Lerp(a.a, b.a, t);

            if (al > 1e-5f) { r /= al; g /= al; bl /= al; }
            return new Color(r, g, bl, al);
        }

        private static float Overlap(float a0, float a1, float b0, float b1)
        {
            return Mathf.Max(0f, Mathf.Min(a1, b1) - Mathf.Max(a0, b0));
        }
    }
}
