using UnityEngine;

namespace AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Composites an atlas texture from its placed islands and applies pull-push padding
    /// (bounded dilation) to fill empty space, preventing bleeding. / 从已放置的岛合成图集贴图，
    /// 并施加 pull-push 填充（有界外扩）填满空白，防止渗色。
    /// </summary>
    public static class AtlasCompositor
    {
        public static void Compose(AtlasResult atlas, ATOPlatformSettings settings, nadena.dev.ndmf.BuildContext ctx)
        {
            int w = atlas.width, h = atlas.height;
            var pixels = new Color32[w * h];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(0, 0, 0, 0);

            foreach (var p in atlas.islands)
            {
                var src = p.source;
                if (src == null || src.texture == null) continue;
                BlitIsland(pixels, w, h, atlas, p);
            }

            bool linear = atlas.sources.Count > 0 && atlas.sources[0].isLinear;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, linear);
            tex.SetPixels32(pixels);
            DilationPullPush(tex);
            tex.name = atlas.name;
            ctx.AssetSaver.SaveAsset(tex);
            atlas.texture = tex;

            ATOLogger.Info($"atlas {atlas.name}: {w}x{h}, {atlas.islands.Count} islands, " +
                           $"utilization {atlas.Utilization:P1}");
        }

        private static void BlitIsland(Color32[] dst, int dw, int dh, AtlasResult atlas, AtlasPlacedIsland p)
        {
            var src = p.source;
            var island = p.island;
            var srcTex = src.readable ?? src.texture;

            // source region in source-texture pixels / 源贴图上的源区域（像素）
            int sx0 = Mathf.Clamp(Mathf.RoundToInt(island.bounds.x * src.width), 0, src.width - 1);
            int sy0 = Mathf.Clamp(Mathf.RoundToInt(island.bounds.y * src.height), 0, src.height - 1);
            int sw = Mathf.Clamp(Mathf.RoundToInt(island.bounds.width * src.width), 1, src.width - sx0);
            int sh = Mathf.Clamp(Mathf.RoundToInt(island.bounds.height * src.height), 1, src.height - sy0);

            var crop = TextureOps.Crop(srcTex, sx0, sy0, sw, sh);

            // destination size in atlas pixels (from dstRect in UV space) / 图集上的目标像素（由 UV 空间 dstRect）
            int tw = Mathf.Max(1, Mathf.RoundToInt(p.dstRect.width * dw));
            int th = Mathf.Max(1, Mathf.RoundToInt(p.dstRect.height * dh));
            var scaled = TextureOps.Scale(crop, tw, th);
            if (scaled != crop) Object.DestroyImmediate(crop);

            int dx = Mathf.RoundToInt(p.dstRect.x * dw);
            int dy = Mathf.RoundToInt(p.dstRect.y * dh);
            var sp = scaled.GetPixels32();

            for (int y = 0; y < th; y++)
            for (int x = 0; x < tw; x++)
            {
                int sx, sy;
                switch (p.rotation)
                {
                    case 90: sx = th - 1 - y; sy = x; break;
                    case 180: sx = tw - 1 - x; sy = th - 1 - y; break;
                    case 270: sx = y; sy = tw - 1 - x; break;
                    default: sx = x; sy = y; break;
                }
                int tx = dx + x, ty = dy + y;
                if (tx >= 0 && ty >= 0 && tx < dw && ty < dh)
                    dst[ty * dw + tx] = sp[sy * tw + sx];
            }

            Object.DestroyImmediate(scaled);
        }

        private static void DilationPullPush(Texture2D tex)
        {
            // bounded multi-pass dilation (pull-push); alpha stays 0 outside islands.
            // 有界多轮外扩（pull-push）；岛外 alpha 保持 0。
            int w = tex.width, h = tex.height;
            var src = tex.GetPixels32();
            var dst = (Color32[])src.Clone();

            for (int pass = 0; pass < 2; pass++)
            {
                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var c = src[y * w + x];
                    if (c.a > 0) continue;
                    Color32 best = default; int bestA = 0;
                    TryNeighbor(src, w, h, x, y - 1, ref best, ref bestA);
                    TryNeighbor(src, w, h, x, y + 1, ref best, ref bestA);
                    TryNeighbor(src, w, h, x - 1, y, ref best, ref bestA);
                    TryNeighbor(src, w, h, x + 1, y, ref best, ref bestA);
                    if (bestA > 0) dst[y * w + x] = new Color32(best.r, best.g, best.b, 0);
                }
                var tmp = src; src = dst; dst = tmp;
            }
            tex.SetPixels32(src);
            tex.Apply(false, true);
        }

        private static void TryNeighbor(Color32[] src, int w, int h, int x, int y, ref Color32 best, ref int bestA)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return;
            var c = src[y * w + x];
            if (c.a > bestA) { best = c; bestA = c.a; }
        }
    }
}
