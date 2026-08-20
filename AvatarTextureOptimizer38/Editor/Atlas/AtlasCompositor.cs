using System;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Copies island pixels into an atlas and GPU/CPU pull-push bleeds into empty texels.
    /// 把岛像素拷进图集，并对空白做 pull-push 渗色（透明贴图 alpha 保持 0）。
    /// </summary>
    public static class AtlasCompositor
    {
        public static Texture2D Compose(int w, int h, System.Collections.Generic.List<UvIsland> islands,
            System.Collections.Generic.List<AtlasPacker.Place> places, Texture2D source, TextureUsageKind usage,
            bool hasAlpha, FilterMode filter, ColorSpace cs)
        {
            var dst = new Color32[w * h];
            var filled = new bool[w * h];
            var srcPx = TextureDecodeCache.GetPixels(source, out var sw, out var sh);

            for (int i = 0; i < islands.Count; i++)
            {
                var isl = islands[i];
                var p = places[i];
                if (!p.Ok) continue;
                int x0 = Mathf.Clamp(Mathf.FloorToInt(isl.UvMin.x * sw), 0, sw - 1);
                int y0 = Mathf.Clamp(Mathf.FloorToInt(isl.UvMin.y * sh), 0, sh - 1);
                int iw = Math.Max(1, isl.OrigPixelW);
                int ih = Math.Max(1, isl.OrigPixelH);
                int dw = Math.Max(1, Mathf.RoundToInt(iw * isl.Scale.x));
                int dh = Math.Max(1, Mathf.RoundToInt(ih * isl.Scale.y));
                dw = Math.Min(dw, p.W);
                dh = Math.Min(dh, p.H);

                var crop = Crop(srcPx, sw, sh, x0, y0, iw, ih);
                Color32[] scaled = (dw == iw && dh == ih)
                    ? crop
                    : QualityEval.Downsample(crop, iw, ih, dw, dh, hasAlpha);

                if (p.Rot90) scaled = Rotate90Cw(scaled, ref dw, ref dh, usage);

                for (int y = 0; y < dh; y++)
                for (int x = 0; x < dw; x++)
                {
                    int dx = p.X + x;
                    int dy = p.Y + y;
                    if ((uint)dx >= (uint)w || (uint)dy >= (uint)h) continue;
                    dst[dy * w + dx] = scaled[y * dw + x];
                    filled[dy * w + dx] = true;
                }

                isl.PackedX = p.X; isl.PackedY = p.Y; isl.PackedW = dw; isl.PackedH = dh;
                isl.Rotated90 = p.Rot90;
            }

            PullPushCpu(dst, filled, w, h, hasAlpha);

            var tex = new Texture2D(w, h, hasAlpha ? TextureFormat.RGBA32 : TextureFormat.RGB24, true, cs == ColorSpace.Linear);
            tex.name = AvatarTextureOptimizer.AtlasNamePrefix + source.name;
            tex.filterMode = filter;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.SetPixels32(Expand(dst, w, h, hasAlpha));
            tex.Apply(true, false);
            return tex;
        }

        private static Color32[] Crop(Color32[] src, int sw, int sh, int x0, int y0, int w, int h)
        {
            var d = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                int sy = Mathf.Clamp(y0 + y, 0, sh - 1);
                for (int x = 0; x < w; x++)
                {
                    int sx = Mathf.Clamp(x0 + x, 0, sw - 1);
                    d[y * w + x] = src[sy * sw + sx];
                }
            }
            return d;
        }

        private static Color32[] Rotate90Cw(Color32[] src, ref int w, ref int h, TextureUsageKind usage)
        {
            var dst = new Color32[w * h];
            int nw = h, nh = w;
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var c = src[y * w + x];
                if (usage == TextureUsageKind.Normal) c = QualityEval.RotateNormal90Cw(c);
                // (x,y) -> (h-1-y, x)
                dst[x * nw + (h - 1 - y)] = c;
            }
            w = nw; h = nh;
            return dst;
        }

        private static void PullPushCpu(Color32[] px, bool[] filled, int w, int h, bool keepAlphaZero)
        {
            // Iterate until no empty 4-neighborhood remains or max iterations. / 迭代直到填满或达到上限。
            var tmp = new Color32[px.Length];
            var tmpF = new bool[filled.Length];
            int empty = 0;
            for (int i = 0; i < filled.Length; i++) if (!filled[i]) empty++;
            int guard = 64;
            while (empty > 0 && guard-- > 0)
            {
                Array.Copy(px, tmp, px.Length);
                Array.Copy(filled, tmpF, filled.Length);
                int filledNow = 0;
                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (filled[i]) continue;
                    int r = 0, g = 0, b = 0, n = 0;
                    Acc(x - 1, y); Acc(x + 1, y); Acc(x, y - 1); Acc(x, y + 1);
                    if (n == 0) continue;
                    tmp[i] = new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), keepAlphaZero ? (byte)0 : (byte)255);
                    tmpF[i] = true;
                    filledNow++;

                    void Acc(int xx, int yy)
                    {
                        if ((uint)xx >= (uint)w || (uint)yy >= (uint)h) return;
                        int j = yy * w + xx;
                        if (!filled[j]) return;
                        r += px[j].r; g += px[j].g; b += px[j].b; n++;
                    }
                }
                Array.Copy(tmp, px, px.Length);
                Array.Copy(tmpF, filled, filled.Length);
                empty -= filledNow;
                if (filledNow == 0) break;
            }
        }

        private static Color32[] Expand(Color32[] src, int w, int h, bool hasAlpha)
        {
            if (hasAlpha) return src;
            return src;
        }
    }
}
