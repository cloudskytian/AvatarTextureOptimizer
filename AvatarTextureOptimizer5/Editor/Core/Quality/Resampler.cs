// Copyright (c) fosa. Licensed under the MIT License.
// Linear-space resampling with premultiplied-alpha downsampling and normal re-normalisation.
// 线性空间重采样，支持预乘 alpha 下采样与法线重归一化。

using System;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Image resampling used by the quality search. All operations happen in linear space; the
    /// caller is responsible for decoding sRGB before and re-encoding after.
    /// 质量搜索所使用的图像重采样。所有操作在线性空间进行，
    /// 调用方负责事前解码 sRGB、事后重新编码。
    /// </summary>
    public static class Resampler
    {
        /// <summary>
        /// Box-filter downsample. Transparent textures are filtered with premultiplied alpha so
        /// fully transparent texels cannot bleed their arbitrary RGB into visible neighbours.
        /// 盒式滤波下采样。透明贴图使用预乘 alpha 滤波，
        /// 使全透明 texel 的任意 RGB 不会渗入可见的相邻像素。
        /// </summary>
        public static ImageBuffer Downsample(ImageBuffer src, int dstW, int dstH, bool premultiplyAlpha)
        {
            dstW = Mathf.Max(1, dstW);
            dstH = Mathf.Max(1, dstH);
            var dst = new ImageBuffer(dstW, dstH);

            var scaleX = (float)src.Width / dstW;
            var scaleY = (float)src.Height / dstH;

            for (var y = 0; y < dstH; y++)
            {
                var y0 = (int)(y * scaleY);
                var y1 = Mathf.Min(src.Height, Mathf.Max(y0 + 1, (int)((y + 1) * scaleY)));

                for (var x = 0; x < dstW; x++)
                {
                    var x0 = (int)(x * scaleX);
                    var x1 = Mathf.Min(src.Width, Mathf.Max(x0 + 1, (int)((x + 1) * scaleX)));

                    // Accumulate both weighted and unweighted sums. The unweighted sum is the
                    // fallback for regions where every contributing texel is fully transparent:
                    // dividing by a zero alpha there would discard the colour entirely and turn
                    // invisible areas black, which both corrupts the quality comparison and
                    // creates dark halos when the texture is later filtered.
                    // 同时累加加权与未加权的和。当所有参与 texel 都完全透明时使用未加权和作为回退：
                    // 此时若除以为零的 alpha 会彻底丢弃颜色、使不可见区域变黑，
                    // 既会破坏质量比较，也会在后续过滤时产生黑边。
                    float pr = 0, pg = 0, pb = 0;
                    float sr = 0, sg = 0, sb = 0;
                    float a = 0;
                    var n = 0;

                    for (var sy = y0; sy < y1; sy++)
                    {
                        for (var sx = x0; sx < x1; sx++)
                        {
                            var c = src.Pixels[sy * src.Width + sx];
                            pr += c.r * c.a;
                            pg += c.g * c.a;
                            pb += c.b * c.a;
                            sr += c.r;
                            sg += c.g;
                            sb += c.b;
                            a += c.a;
                            n++;
                        }
                    }

                    if (n == 0) n = 1;
                    var inv = 1f / n;
                    a *= inv;

                    float r, g, b;
                    if (premultiplyAlpha && a > 1e-6f)
                    {
                        // Undo the premultiplication so the result is straight alpha again.
                        // 撤销预乘，使结果重新变为直通 alpha。
                        var invA = 1f / (a * n);
                        r = pr * invA;
                        g = pg * invA;
                        b = pb * invA;
                    }
                    else
                    {
                        r = sr * inv;
                        g = sg * inv;
                        b = sb * inv;
                    }

                    dst.Pixels[y * dstW + x] = new Color(r, g, b, a);
                }
            }

            return dst;
        }

        /// <summary>
        /// Bilinear upsample, used to bring a shrunk island back to the original resolution so
        /// it can be compared against the source pixel for pixel.
        /// 双线性上采样，用于将缩小后的岛还原到原始分辨率，以便与源图逐像素比较。
        /// </summary>
        public static ImageBuffer UpsampleBilinear(ImageBuffer src, int dstW, int dstH)
        {
            dstW = Mathf.Max(1, dstW);
            dstH = Mathf.Max(1, dstH);
            var dst = new ImageBuffer(dstW, dstH);

            for (var y = 0; y < dstH; y++)
            {
                // Sample at texel centres to avoid a half-texel shift.
                // 在 texel 中心采样，避免半像素偏移。
                var v = (y + 0.5f) * src.Height / dstH - 0.5f;
                var y0 = Mathf.FloorToInt(v);
                var fy = v - y0;
                var y0c = Mathf.Clamp(y0, 0, src.Height - 1);
                var y1c = Mathf.Clamp(y0 + 1, 0, src.Height - 1);

                for (var x = 0; x < dstW; x++)
                {
                    var u = (x + 0.5f) * src.Width / dstW - 0.5f;
                    var x0 = Mathf.FloorToInt(u);
                    var fx = u - x0;
                    var x0c = Mathf.Clamp(x0, 0, src.Width - 1);
                    var x1c = Mathf.Clamp(x0 + 1, 0, src.Width - 1);

                    var c00 = src.Pixels[y0c * src.Width + x0c];
                    var c10 = src.Pixels[y0c * src.Width + x1c];
                    var c01 = src.Pixels[y1c * src.Width + x0c];
                    var c11 = src.Pixels[y1c * src.Width + x1c];

                    var top = Lerp(c00, c10, fx);
                    var bottom = Lerp(c01, c11, fx);
                    dst.Pixels[y * dstW + x] = Lerp(top, bottom, fy);
                }
            }

            return dst;
        }

        private static Color Lerp(Color a, Color b, float t) => new Color(
            a.r + (b.r - a.r) * t,
            a.g + (b.g - a.g) * t,
            a.b + (b.b - a.b) * t,
            a.a + (b.a - a.a) * t);

        /// <summary>
        /// Decodes, resamples and re-encodes a normal map. Normals must be interpolated as
        /// vectors and re-normalised, because averaging the encoded bytes directly shortens the
        /// vector and darkens the lighting.
        /// 解码、重采样并重新编码法线贴图。法线必须作为向量插值并重归一化，
        /// 因为直接平均编码后的字节会缩短向量并使光照变暗。
        /// </summary>
        public static ImageBuffer ResampleNormalMap(ImageBuffer src, int dstW, int dstH)
        {
            // Decode to vectors.
            // 解码为向量。
            var decoded = new ImageBuffer(src.Width, src.Height);
            for (var i = 0; i < src.Pixels.Length; i++)
            {
                var n = ImageMetrics.DecodeNormal(src.Pixels[i]);
                decoded.Pixels[i] = new Color(n.x, n.y, n.z, src.Pixels[i].a);
            }

            // Average in vector space.
            // 在向量空间中平均。
            var resampled = Downsample(decoded, dstW, dstH, false);

            // Re-normalise and re-encode to [0,1].
            // 重归一化并重新编码到 [0,1]。
            var result = new ImageBuffer(resampled.Width, resampled.Height);
            for (var i = 0; i < resampled.Pixels.Length; i++)
            {
                var c = resampled.Pixels[i];
                var v = new Vector3(c.r, c.g, c.b);
                var m = v.magnitude;
                v = m > 1e-6f ? v / m : new Vector3(0f, 0f, 1f);
                result.Pixels[i] = new Color(
                    v.x * 0.5f + 0.5f, v.y * 0.5f + 0.5f, v.z * 0.5f + 0.5f, c.a);
            }

            return result;
        }

        /// <summary>
        /// Re-encodes a decoded normal buffer to the standard [0,1] representation without
        /// resampling, so lossless comparisons operate on the same encoding.
        /// 在不重采样的情况下将解码后的法线缓冲重新编码为标准 [0,1] 表示，
        /// 使无损比较在相同编码下进行。
        /// </summary>
        public static ImageBuffer EncodeNormals(ImageBuffer src)
        {
            var result = new ImageBuffer(src.Width, src.Height);
            for (var i = 0; i < src.Pixels.Length; i++)
            {
                var n = ImageMetrics.DecodeNormal(src.Pixels[i]);
                result.Pixels[i] = new Color(
                    n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f, src.Pixels[i].a);
            }

            return result;
        }

        /// <summary>
        /// Extracts a sub-rectangle from a decoded texture.
        /// 从解码后的贴图中提取子矩形。
        /// </summary>
        public static ImageBuffer Crop(DecodedTexture src, RectInt rect)
        {
            var w = Mathf.Max(1, rect.width);
            var h = Mathf.Max(1, rect.height);
            var dst = new ImageBuffer(w, h);

            for (var y = 0; y < h; y++)
            {
                var sy = Mathf.Clamp(rect.y + y, 0, src.Height - 1);
                for (var x = 0; x < w; x++)
                {
                    var sx = Mathf.Clamp(rect.x + x, 0, src.Width - 1);
                    dst.Pixels[y * w + x] = src.Pixels[sy * src.Width + sx];
                }
            }

            return dst;
        }

        /// <summary>
        /// Detects whether every texel in a buffer is the same colour, which allows the island
        /// to be shrunk to the minimum size immediately.
        /// 检测缓冲中所有 texel 是否同色，若是则该岛可立即缩到最小尺寸。
        /// </summary>
        public static bool IsSolidColor(ImageBuffer img, out Color color, float tolerance = 1e-4f)
        {
            color = img.Pixels.Length > 0 ? img.Pixels[0] : Color.clear;
            if (img.Pixels.Length == 0) return true;

            var first = img.Pixels[0];
            for (var i = 1; i < img.Pixels.Length; i++)
            {
                var c = img.Pixels[i];
                if (Mathf.Abs(c.r - first.r) > tolerance ||
                    Mathf.Abs(c.g - first.g) > tolerance ||
                    Mathf.Abs(c.b - first.b) > tolerance ||
                    Mathf.Abs(c.a - first.a) > tolerance)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
