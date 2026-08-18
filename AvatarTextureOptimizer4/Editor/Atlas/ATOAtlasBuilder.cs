// Avatar Texture Optimizer (ATO)
// Builds atlas textures: rasterizes each island at its native scaled resolution (with
// premultiplied-alpha filtering for transparent textures, renormalization for normals),
// rotates (bitmask transpose semantics), places at the shared normalized position, and
// applies GPU pull-push style dilation to fill empty space (alpha stays 0 for transparent).
// Additionally, grayscale/mask atlases whose whole-island quality requirement is below the
// main-color gate may be uniformly halved (padding permitting) to save memory.
// 构建图集贴图：按原生缩放分辨率光栅化各岛（透明贴图用预乘 alpha 过滤，法线重归一化），
// 旋转（位掩码转置语义），摆放到共享的归一化位置，并用 pull-push 式外扩填充空白区域
// （透明贴图 alpha 保持 0）。此外，灰度/遮罩图集若整组岛的质量需求低于主色门控，
// 在满足 padding 的前提下可整体减半以节省内存。

using System.Collections.Generic;
using UnityEngine;

namespace NetFosa.ATO
{
    /// <summary>
    /// Stage 6b: assemble atlas textures. / 阶段 6b：组装图集贴图。
    /// </summary>
    public static class ATOAtlasBuilder
    {
        public static void BuildAll(ATOBuildContext build, ATOProgress progress)
        {
            progress.Begin(build.atlases.Count);
            foreach (var atlas in build.atlases)
            {
                BuildOne(build, atlas);
                progress.Advance(1, atlas.name);
                progress.ThrowIfCancelled();
            }
        }

        private static void BuildOne(ATOBuildContext build, ATOAtlas atlas)
        {
            // Type-group downscale for low-demand categories (grayscale/mask). / 低需求分类（灰度/遮罩）的类型组整体缩放。
            float scale = 1f;
            var thr = ATOQualityModel.Resolve(build);
            bool eligible = (atlas.category == ATOTextureCategory.Grayscale || atlas.category == ATOTextureCategory.Mask)
                && !ATOQualityModel.IsLossless(thr)
                && build.profile.padding >= 8; // halving keeps padding >= 4 / 减半后 padding 仍 >= 4
            if (eligible && CanHalve(build, atlas, thr.grayRmseMax))
            {
                scale = 0.5f;
                ATOLogger.Info($"Atlas '{atlas.name}' halved (type group below main-color quality demand). / 图集 '{atlas.name}' 已减半（类型组质量需求低于主色）。");
            }

            BuildScaled(build, atlas, scale);

            if (scale < 1f)
            {
                atlas.width = Mathf.Max(1, Mathf.RoundToInt(atlas.width * scale));
                atlas.height = Mathf.Max(1, Mathf.RoundToInt(atlas.height * scale));
            }
        }

        private static void BuildScaled(ATOBuildContext build, ATOAtlas atlas, float scale)
        {
            int W = Mathf.Max(1, Mathf.RoundToInt(atlas.width * scale));
            int H = Mathf.Max(1, Mathf.RoundToInt(atlas.height * scale));
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, true, false)
            {
                name = atlas.name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            var pixels = new Color[W * H];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color(0f, 0f, 0f, 0f);
            tex.SetPixels(pixels);

            int coveredCells = 0;
            foreach (var isl in atlas.islands)
            {
                var tr = FindSourceFor(atlas, isl);
                if (tr == null || tr.texture == null) continue;

                // Native scaled size in pixels (times the group scale). / 原生缩放像素尺寸（× 组缩放）。
                int w = Mathf.Max(1, Mathf.RoundToInt(isl.scaledSizeUv.x * tr.width * scale));
                int h = Mathf.Max(1, Mathf.RoundToInt(isl.scaledSizeUv.y * tr.height * scale));
                bool premul = tr.hasAlpha && tr.Category == ATOTextureCategory.MainColor;

                ATOTextureSampler.Rasterize(tr.texture, isl, w, h, out var content, out _, premul);

                // Normal maps: renormalize each texel. / 法线贴图：逐纹素重归一化。
                if (tr.Category == ATOTextureCategory.NormalMap)
                {
                    for (int i = 0; i < content.Length; i++)
                    {
                        ATOColorMath.DecodeNormal(content[i].r, content[i].g, content[i].b, out var nx, out var ny, out var nz);
                        ATOColorMath.EncodeNormal(nx, ny, nz, out var r, out var g, out var b);
                        content[i].r = r; content[i].g = g; content[i].b = b; content[i].a = 1f;
                    }
                }

                // Rotate content to match the placement rotation. / 按摆放旋转旋转内容。
                int cw = w, ch = h;
                for (int r = 0; r < (isl.rotation & 3); r++)
                    Rotate90Cw(content, ref cw, ref ch);

                // Pixel offset from the normalized placement (bottom-left anchored). / 由归一化摆放计算像素偏移（左下锚定）。
                int ox = Mathf.RoundToInt(isl.placementMinUv.x * W);
                int oy = Mathf.RoundToInt(isl.placementMinUv.y * H);

                BlitContent(tex, content, cw, ch, ox, oy);
                coveredCells += cw * ch / (ATOConstants.RasterCellSize * ATOConstants.RasterCellSize);
            }

            // Pull-push dilation to fill empty space (bounded by atlas size). / pull-push 外扩填充空白。
            DilateFill(tex, atlas.hasAlpha);

            tex.Apply(true, false); // generate mipmaps, keep readable / 生成 mipmap，保留可读性
            atlas.texture = tex;
            atlas.utilization = coveredCells * (float)(ATOConstants.RasterCellSize * ATOConstants.RasterCellSize) / (atlas.width * atlas.height);
        }

        /// <summary>
        /// True when halving every island in this grayscale/mask atlas keeps the per-island
        /// worst-channel linear RMSE within the grayscale threshold.
        /// 当把该灰度/遮罩图集每个岛减半后、逐岛最差通道线性 RMSE 仍在灰度阈值内时返回真。
        /// </summary>
        private static bool CanHalve(ATOBuildContext build, ATOAtlas atlas, float grayRmseMax)
        {
            foreach (var isl in atlas.islands)
            {
                var tr = FindSourceFor(atlas, isl);
                if (tr == null || tr.texture == null) continue;
                int w = Mathf.Max(1, Mathf.RoundToInt(isl.scaledSizeUv.x * tr.width));
                int h = Mathf.Max(1, Mathf.RoundToInt(isl.scaledSizeUv.y * tr.height));
                int hw = Mathf.Max(1, w / 2), hh = Mathf.Max(1, h / 2);

                ATOTextureSampler.Rasterize(tr.texture, isl, w, h, out var full, out var mask);
                ATOTextureSampler.Rasterize(tr.texture, isl, hw, hh, out var small, out _);
                var up = new Color[w * h];
                ATOTextureSampler.BilinearUpsample(small, hw, hh, up, w, h);

                for (int ch = 0; ch < 3; ch++)
                {
                    var a = new float[w * h];
                    var b = new float[w * h];
                    for (int i = 0; i < w * h; i++)
                    {
                        a[i] = tr.isSRGB ? ATOUtil.SrgbToLinear(full[i][ch]) : full[i][ch];
                        b[i] = tr.isSRGB ? ATOUtil.SrgbToLinear(up[i][ch]) : up[i][ch];
                    }
                    if (ATOColorMath.Rmse(a, b, mask) > grayRmseMax) return false;
                }
            }
            return true;
        }

        private static ATOTextureRef FindSourceFor(ATOAtlas atlas, ATOIsland isl)
        {
            foreach (var t in atlas.sources)
                if (t.Category == atlas.category) return t;
            return atlas.sources.Count > 0 ? atlas.sources[0] : null;
        }

        private static void Rotate90Cw(Color[] src, ref int w, ref int h)
        {
            var dst = new Color[src.Length];
            int nw = h, nh = w;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    dst[x * nw + (nw - 1 - y)] = src[y * w + x];
            ArrayCopy(dst, src);
            w = nw; h = nh;
        }

        private static void ArrayCopy(Color[] from, Color[] to)
        {
            for (int i = 0; i < from.Length; i++) to[i] = from[i];
        }

        private static void BlitContent(Texture2D tex, Color[] content, int w, int h, int ox, int oy)
        {
            // Clamp to texture bounds to survive rounding at edges. / 钳制到贴图边界以容忍边缘取整误差。
            int tw = tex.width, th = tex.height;
            if (ox < 0) ox = 0; if (oy < 0) oy = 0;
            if (ox + w > tw) w = tw - ox;
            if (oy + h > th) h = th - oy;
            if (w <= 0 || h <= 0) return;
            var pix = tex.GetPixels(ox, oy, w, h);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var c = content[y * w + x];
                    var existing = pix[y * w + x];
                    float a = c.a;
                    var merged = new Color(
                        c.r * a + existing.r * (1f - a),
                        c.g * a + existing.g * (1f - a),
                        c.b * a + existing.b * (1f - a),
                        Mathf.Max(a, existing.a));
                    pix[y * w + x] = merged;
                }
            tex.SetPixels(ox, oy, w, h, pix);
        }

        /// <summary>
        /// Multi-source BFS dilation: fill empty texels with the average of their filled
        /// neighbors. For transparent textures the alpha channel stays 0.
        /// 多源 BFS 外扩：用已填充邻域的平均色填充空白纹素。透明贴图 alpha 保持 0。
        /// </summary>
        private static void DilateFill(Texture2D tex, bool keepAlphaZero)
        {
            int w = tex.width, h = tex.height;
            var px = tex.GetPixels();
            var filled = new bool[w * h];
            var queue = new Queue<int>();

            for (int i = 0; i < px.Length; i++)
            {
                filled[i] = px[i].a > 1e-6f;
                if (filled[i]) queue.Enqueue(i);
            }

            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                int x = i % w, y = i / w;
                var c = px[i];
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        int ni = ny * w + nx;
                        if (filled[ni]) continue;
                        var nc = px[ni];
                        nc.r = c.r; nc.g = c.g; nc.b = c.b;
                        nc.a = keepAlphaZero ? 0f : c.a;
                        px[ni] = nc;
                        filled[ni] = true;
                        queue.Enqueue(ni);
                    }
            }
            tex.SetPixels(px);
        }
    }
}
