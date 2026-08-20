// ============================================================================
// ATO - quality stage image buffers + rasterizer
// ATO - 质量阶段图像缓冲 + 三角形光栅化
//
// Region-based decoding: instead of caching whole textures (memory blowup on
// 8K maps), each island decodes its source region once (linear space) and
// the binary search reuses it. Regions are disposed at stage end.
// 按岛区域解码：不整图缓存（8K 图内存爆炸），每个岛解码一次源区域（线性空
// 间），二分搜索复用。区域在阶段结束时释放。
// ============================================================================

#region

using System.Collections.Generic;
using net.fosa.AvatarTextureOptimizer.Editor.Core;
using UnityEngine;

#endregion

namespace net.fosa.AvatarTextureOptimizer.Editor.Quality
{
    /// <summary>Decoded region (linear space RGBA float). 解码区域（线性 RGBA
    /// float）。</summary>
    public sealed class ATORegion : System.IDisposable
    {
        public int W, H;
        public float[] RGBA; // [y * W + x] * 4  按行主序
        public bool IsNormal;

        public void Dispose()
        {
            RGBA = null;
        }

        public void Get(int x, int y, out float r, out float g, out float b, out float a)
        {
            int i = (y * W + x) * 4;
            r = RGBA[i];
            g = RGBA[i + 1];
            b = RGBA[i + 2];
            a = RGBA[i + 3];
        }
    }

    /// <summary>Decodes island source regions from textures.
    /// 从贴图解码岛源区域。</summary>
    public sealed class RegionDecoder
    {
        private readonly Dictionary<(int island, int tex), ATORegion> _cache = new();
        private readonly ATOAnalysis an;

        public RegionDecoder(ATOAnalysis analysis)
        {
            an = analysis;
        }

        public void DisposeAll()
        {
            foreach (var r in _cache.Values) r.Dispose();
            _cache.Clear();
        }

        public void Dispose((int island, int tex) key)
        {
            if (_cache.TryGetValue(key, out var r))
            {
                r.Dispose();
                _cache.Remove(key);
            }
        }

        public long PinnedBytes
        {
            get
            {
                long n = 0;
                foreach (var r in _cache.Values)
                {
                    if (r.RGBA != null) n += (long) r.RGBA.Length * 4;
                }
                return n;
            }
        }

        /// <summary>Decodes the island's source region for one texture
        /// (source UV = stored + shift). 解码岛在某贴图上的源区域。</summary>
        public ATORegion Decode(ATOUVIsland island, int tid)
        {
            var key = (island.Id, tid);
            if (_cache.TryGetValue(key, out var cached)) return cached;

            var tref = an.Textures[tid];
            var tex = tref.Texture;
            var shift = island.ShiftUV;
            // source bbox in UV (V=0 at bottom in Unity)  源包围盒（Unity V=0 在底部）
            float minX = island.MinUV.x + shift.x;
            float minY = island.MinUV.y + shift.y;
            float w = island.MaxUV.x - island.MinUV.x;
            float h = island.MaxUV.y - island.MinUV.y;
            float maxY = minY + h;

            // pixel rows are top-down: UV y=max -> top row  像素行自上而下
            int x0 = Mathf.Max(0, Mathf.FloorToInt(minX * tex.width));
            int y0 = Mathf.Max(0, Mathf.FloorToInt((1f - maxY) * tex.height));
            int x1 = Mathf.Min(tex.width, x0 + Mathf.Max(1, Mathf.RoundToInt(w * tex.width)));
            int y1 = Mathf.Min(tex.height, y0 + Mathf.Max(1, Mathf.RoundToInt(h * tex.height)));
            if (x1 <= x0 || y1 <= y0)
            {
                return new ATORegion { W = 1, H = 1, RGBA = new float[] { 0, 0, 0, 1 } };
            }

            var colors = tex.GetPixels(x0, y0, x1 - x0, y1 - y0);
            var region = new ATORegion
            {
                W = x1 - x0,
                H = y1 - y0,
                RGBA = new float[(x1 - x0) * (y1 - y0) * 4],
                IsNormal = RoleOf(island, tid) == Api.ATOTextureRole.Normal,
            };
            for (int i = 0; i < colors.Length; i++)
            {
                int p = i * 4;
                region.RGBA[p] = SrgbToLinear(colors[i].r);
                region.RGBA[p + 1] = SrgbToLinear(colors[i].g);
                region.RGBA[p + 2] = SrgbToLinear(colors[i].b);
                region.RGBA[p + 3] = colors[i].a;
                if (region.IsNormal)
                {
                    // tangent-space decode + renormalize  切线空间解码 + 重归一化
                    float nx = region.RGBA[p] * 2f - 1f;
                    float ny = region.RGBA[p + 1] * 2f - 1f;
                    float nz = Mathf.Sqrt(Mathf.Max(0f, 1f - nx * nx - ny * ny));
                    float len = Mathf.Sqrt(nx * nx + ny * ny + nz * nz);
                    if (len > 1e-6f)
                    {
                        region.RGBA[p] = nx / len;
                        region.RGBA[p + 1] = ny / len;
                        region.RGBA[p + 2] = nz / len;
                    }
                }
            }
            _cache[key] = region;
            return region;
        }

        /// <summary>Role of texture tid as used by the island's material.
        /// 贴图 tid 在岛材质中的角色。</summary>
        public static Api.ATOTextureRole RoleOf(ATOUVIsland island, int tid)
        {
            var mat = island.UVSet.Material;
            if (mat == null) return Api.ATOTextureRole.Albedo;
            // find through the analysis  通过分析结果查找
            foreach (var matEntry in an.Materials)
            {
                if (!ReferenceEquals(matEntry.Key, mat)) continue;
                foreach (var (prop, pref) in matEntry.Value.PropertyRefs)
                {
                    if (!matEntry.Value.Textures.TryGetValue(prop, out var tex)) continue;
                    if (!(tex is Texture2D t2d)) continue;
                    if (!an.TextureDedupMap.TryGetValue(t2d, out var did)) continue;
                    if (did != tid) continue;
                    return pref.Role;
                }
            }
            return Api.ATOTextureRole.Albedo;
        }

        public static float SrgbToLinear(float c)
        {
            return c <= 0.04045f ? c / 12.92f : Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);
        }

        public static float LinearToSrgb(float c)
        {
            c = Mathf.Clamp01(c);
            return c <= 0.0031308f ? c * 12.92f : 1.055f * Mathf.Pow(c, 1f / 2.4f) - 0.055f;
        }
    }

    /// <summary>2D bilinear resampling (CPU). 双线性重采样（CPU）。</summary>
    public static class Bilinear
    {
        /// <summary>Resamples src to (dw, dh). 将 src 重采样到 (dw,dh)。</summary>
        public static float[] Resample(float[] src, int sw, int sh, int dw, int dh)
        {
            var dst = new float[dw * dh * 4];
            if (dw <= 0 || dh <= 0) return dst;
            float sx = (float) sw / dw;
            float sy = (float) sh / dh;
            for (int y = 0; y < dh; y++)
            {
                float fy = (y + 0.5f) * sy - 0.5f;
                int y0 = Mathf.Clamp(Mathf.FloorToInt(fy), 0, sh - 1);
                int y1 = Mathf.Clamp(y0 + 1, 0, sh - 1);
                float ty = fy - y0;
                for (int x = 0; x < dw; x++)
                {
                    float fx = (x + 0.5f) * sx - 0.5f;
                    int x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, sw - 1);
                    int x1 = Mathf.Clamp(x0 + 1, 0, sw - 1);
                    float tx = fx - x0;
                    int d = (y * dw + x) * 4;
                    for (int c = 0; c < 4; c++)
                    {
                        float a = src[(y0 * sw + x0) * 4 + c];
                        float b = src[(y0 * sw + x1) * 4 + c];
                        float e = src[(y1 * sw + x0) * 4 + c];
                        float f = src[(y1 * sw + x1) * 4 + c];
                        dst[d + c] = (a * (1 - tx) + b * tx) * (1 - ty) + (e * (1 - tx) + f * tx) * ty;
                    }
                }
            }
            return dst;
        }
    }

    /// <summary>Rasterizes UV triangles into a byte coverage mask
    /// (edge function, 2x supersampling).
    /// 将 UV 三角形光栅化为字节覆盖掩码（边函数，2x 超采样）。</summary>
    public static class CoverageRasterizer
    {
        public static byte[] Rasterize(ATOUVIsland island, int w, int h)
        {
            var mask = new byte[w * h];
            if (w <= 0 || h <= 0) return mask;
            var tris = island.Triangles;
            var uvSet = island.UVSet;
            var mesh = uvSet.Mesh;
            Vector2[] uvs = UVIslandExtractor.GetUVs(mesh, uvSet.Channel);
            if (uvs == null) return mask;

            float invW = 1f / w;
            float invH = 1f / h;

            float uvW = Mathf.Max(1e-6f, island.MaxUV.x - island.MinUV.x);
            float uvH = Mathf.Max(1e-6f, island.MaxUV.y - island.MinUV.y);

            for (int t = 0; t < tris.Length; t += 3)
            {
                var u0 = uvs[tris[t]];
                var u1 = uvs[tris[t + 1]];
                var u2 = uvs[tris[t + 2]];
                // normalize into island-local [0,1] (V=0 at top of region)
                // 归一化到岛本地 [0,1]（区域顶部 = 岛 UV 顶部）
                Vector2 p0 = new Vector2(
                    (u0.x - island.MinUV.x) / uvW * w,
                    (island.MaxUV.y - u0.y) / uvH * h);
                Vector2 p1 = new Vector2(
                    (u1.x - island.MinUV.x) / uvW * w,
                    (island.MaxUV.y - u1.y) / uvH * h);
                Vector2 p2 = new Vector2(
                    (u2.x - island.MinUV.x) / uvW * w,
                    (island.MaxUV.y - u2.y) / uvH * h);

                int minX = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(p0.x, Mathf.Min(p1.x, p2.x))));
                int maxX = Mathf.Min(w - 1, Mathf.CeilToInt(Mathf.Max(p0.x, Mathf.Max(p1.x, p2.x))));
                int minY = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(p0.y, Mathf.Min(p1.y, p2.y))));
                int maxY = Mathf.Min(h - 1, Mathf.CeilToInt(Mathf.Max(p0.y, Mathf.Max(p1.y, p2.y))));
                if (maxX < minX || maxY < minY) continue;

                Vector2 e0 = p1 - p0, e1 = p2 - p0;
                for (int y = minY; y <= maxY; y++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        bool covered = false;
                        // 2x2 supersample  2x2 超采样
                        for (int sy = 0; sy < 2 && !covered; sy++)
                        {
                            for (int sx = 0; sx < 2; sx++)
                            {
                                var pt = new Vector2(x + (0.25f + 0.25f * sx), y + (0.25f + 0.25f * sy));
                                Vector2 v = pt - p0;
                                float a = e0.x * v.y - e0.y * v.x;
                                float b = e1.x * v.y - e1.y * v.x;
                                float c = e0.x * (p2.y - p0.y) - e0.y * (p2.x - p0.x);
                                if (c == 0f) continue;
                                // consistent sign test  符号一致性测试
                                if ((a >= 0f) == (c >= 0f) && (b >= 0f) == (c >= 0f))
                                {
                                    covered = true;
                                    break;
                                }
                            }
                        }
                        if (covered) mask[y * w + x] = 1;
                    }
                }
            }
            return mask;
        }
    }
}
