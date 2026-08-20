using System;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// EN: A rectangular linear-RGBA tile extracted from a decoded texture.
    /// ZH: 从解码贴图中截取的一块线性 RGBA 矩形区域。
    /// </summary>
    public sealed class Tile
    {
        /// <summary>EN: Width in pixels. ZH: 像素宽度。</summary>
        public int W;
        /// <summary>EN: Height in pixels. ZH: 像素高度。</summary>
        public int H;
        /// <summary>EN: Linear RGBA pixels, row major. ZH: 线性 RGBA 像素，行主序。</summary>
        public Color[] P;

        /// <summary>EN: Allocate a tile. ZH: 分配一块区域。</summary>
        public Tile(int w, int h) { W = Mathf.Max(1, w); H = Mathf.Max(1, h); P = new Color[W * H]; }

        /// <summary>EN: Indexer with clamped coordinates. ZH: 带钳制坐标的索引器。</summary>
        public Color At(int x, int y) => P[Mathf.Clamp(y, 0, H - 1) * W + Mathf.Clamp(x, 0, W - 1)];
    }

    /// <summary>
    /// EN: Resampling primitives shared by the quality solver and the atlas compositor.
    ///
    ///     Two rules from the specification are implemented here and nowhere else:
    ///       * All resampling happens in linear space (the decode step already linearised sRGB).
    ///       * Downsampling of textures with meaningful alpha is alpha-premultiplied, so fully
    ///         transparent texels never bleed their (usually black or garbage) RGB into their
    ///         neighbours.
    /// ZH: 质量求解器与图集合成器共用的重采样原语。
    ///
    ///     需求中的两条规则只在这里实现：
    ///       * 所有重采样都在线性空间进行（解码阶段已完成 sRGB 线性化）。
    ///       * 对含有效 alpha 的贴图，降采样采用预乘 alpha，
    ///         这样全透明纹素绝不会把它们（通常是黑色或垃圾值的）RGB 渗给邻居。
    /// </summary>
    public static class ImageOps
    {
        /// <summary>EN: Extract a pixel rectangle from a decoded texture, clamping at the edges. ZH: 从解码贴图中截取像素矩形，边缘钳制。</summary>
        public static Tile Extract(DecodedTexture src, RectInt rect)
        {
            var tile = new Tile(rect.width, rect.height);
            var px = src.Pixels;
            Parallel.For(0, tile.H, y =>
            {
                int sy = Mathf.Clamp(rect.y + y, 0, src.Height - 1);
                for (int x = 0; x < tile.W; x++)
                {
                    int sx = Mathf.Clamp(rect.x + x, 0, src.Width - 1);
                    tile.P[y * tile.W + x] = px[sy * src.Width + sx];
                }
            });
            return tile;
        }

        /// <summary>
        /// EN: Area-average downsample. When <paramref name="premultiply"/> is set the RGB channels are
        ///     weighted by alpha and un-premultiplied afterwards.
        /// ZH: 面积平均降采样。<paramref name="premultiply"/> 为真时 RGB 通道按 alpha 加权，之后再反预乘。
        /// </summary>
        public static Tile Downsample(Tile src, int dstW, int dstH, bool premultiply)
        {
            dstW = Mathf.Max(1, dstW);
            dstH = Mathf.Max(1, dstH);
            if (dstW == src.W && dstH == src.H) return src;

            var dst = new Tile(dstW, dstH);
            double sx = (double)src.W / dstW;
            double sy = (double)src.H / dstH;

            Parallel.For(0, dstH, y =>
            {
                int y0 = (int)(y * sy);
                int y1 = Mathf.Max(y0 + 1, (int)((y + 1) * sy));
                y1 = Mathf.Min(y1, src.H);
                for (int x = 0; x < dstW; x++)
                {
                    int x0 = (int)(x * sx);
                    int x1 = Mathf.Max(x0 + 1, (int)((x + 1) * sx));
                    x1 = Mathf.Min(x1, src.W);

                    double r = 0, g = 0, b = 0, a = 0, wsum = 0;
                    for (int yy = y0; yy < y1; yy++)
                    for (int xx = x0; xx < x1; xx++)
                    {
                        var c = src.P[yy * src.W + xx];
                        double w = premultiply ? c.a : 1.0;
                        r += c.r * w; g += c.g * w; b += c.b * w;
                        a += c.a;
                        wsum += w;
                    }
                    int n = Mathf.Max(1, (y1 - y0) * (x1 - x0));
                    Color outc;
                    if (premultiply)
                    {
                        if (wsum > 1e-8)
                            outc = new Color((float)(r / wsum), (float)(g / wsum), (float)(b / wsum), (float)(a / n));
                        else
                            outc = new Color(0, 0, 0, (float)(a / n));
                    }
                    else
                    {
                        outc = new Color((float)(r / n), (float)(g / n), (float)(b / n), (float)(a / n));
                    }
                    dst.P[y * dstW + x] = outc;
                }
            });
            return dst;
        }

        /// <summary>EN: Bilinear upsample back to a reference size. ZH: 双线性上采样回参考尺寸。</summary>
        public static Tile UpsampleBilinear(Tile src, int dstW, int dstH)
        {
            if (dstW == src.W && dstH == src.H) return src;
            var dst = new Tile(dstW, dstH);
            float sx = (float)src.W / dstW;
            float sy = (float)src.H / dstH;

            Parallel.For(0, dstH, y =>
            {
                float fy = (y + 0.5f) * sy - 0.5f;
                int y0 = Mathf.FloorToInt(fy);
                float ty = fy - y0;
                for (int x = 0; x < dstW; x++)
                {
                    float fx = (x + 0.5f) * sx - 0.5f;
                    int x0 = Mathf.FloorToInt(fx);
                    float tx = fx - x0;

                    var c00 = src.At(x0, y0);
                    var c10 = src.At(x0 + 1, y0);
                    var c01 = src.At(x0, y0 + 1);
                    var c11 = src.At(x0 + 1, y0 + 1);

                    var top = Color.LerpUnclamped(c00, c10, tx);
                    var bot = Color.LerpUnclamped(c01, c11, tx);
                    dst.P[y * dstW + x] = Color.LerpUnclamped(top, bot, ty);
                }
            });
            return dst;
        }

        /// <summary>
        /// EN: Round-trip a tile through a candidate scale: downsample then upsample back to the
        ///     original size. This is exactly the comparison the specification asks for.
        /// ZH: 让区域按候选缩放走一个来回：先降采样再上采样回原尺寸。这正是需求要求的比较方式。
        /// </summary>
        public static Tile RoundTrip(Tile src, int w, int h, bool premultiply)
        {
            var small = Downsample(src, w, h, premultiply);
            return UpsampleBilinear(small, src.W, src.H);
        }

        /// <summary>
        /// EN: Decode a tangent-space normal map tile from its stored encoding into unit vectors.
        ///     Handles both RGB(xyz) and DXT5nm-style AG encodings by testing whether B carries data.
        /// ZH: 把切线空间法线贴图区域从存储编码解码为单位向量。
        ///     通过检测 B 通道是否承载数据，同时支持 RGB(xyz) 与 DXT5nm 式的 AG 编码。
        /// </summary>
        public static Vector3[] DecodeNormals(Tile t, bool agEncoded)
        {
            var result = new Vector3[t.W * t.H];
            Parallel.For(0, t.H, y =>
            {
                for (int x = 0; x < t.W; x++)
                {
                    var c = t.P[y * t.W + x];
                    Vector3 n;
                    if (agEncoded)
                        n = new Vector3(c.a * 2f - 1f, c.g * 2f - 1f, 0f);
                    else
                        n = new Vector3(c.r * 2f - 1f, c.g * 2f - 1f, c.b * 2f - 1f);
                    var xy = n.x * n.x + n.y * n.y;
                    n.z = Mathf.Sqrt(Mathf.Max(0f, 1f - xy));
                    result[y * t.W + x] = n.sqrMagnitude > 1e-8f ? n.normalized : Vector3.forward;
                }
            });
            return result;
        }

        /// <summary>
        /// EN: Re-encode unit normals back into RGB, re-normalising first. Used after any resample of a
        ///     normal map so that the stored vectors stay unit length.
        /// ZH: 把单位法线重新归一化后编码回 RGB。任何对法线贴图的重采样之后都要调用，
        ///     以保证存储的向量保持单位长度。
        /// </summary>
        public static Tile EncodeNormals(Vector3[] normals, int w, int h)
        {
            var t = new Tile(w, h);
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    var n = normals[y * w + x];
                    n = n.sqrMagnitude > 1e-8f ? n.normalized : Vector3.forward;
                    t.P[y * w + x] = new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f, 1f);
                }
            });
            return t;
        }

        /// <summary>EN: True when the tile is a single constant colour. ZH: 该区域是否为单一恒定颜色。</summary>
        public static bool IsSolid(Tile t, out Color color)
        {
            color = t.P.Length > 0 ? t.P[0] : Color.clear;
            var c0 = color;
            for (int i = 1; i < t.P.Length; i++)
            {
                var c = t.P[i];
                if (Mathf.Abs(c.r - c0.r) > 1e-5f || Mathf.Abs(c.g - c0.g) > 1e-5f ||
                    Mathf.Abs(c.b - c0.b) > 1e-5f || Mathf.Abs(c.a - c0.a) > 1e-5f) return false;
            }
            return true;
        }
    }
}
