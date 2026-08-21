using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Fosa.ATO;

namespace Fosa.ATO.Editor
{
    /// <summary>
    /// Candidate atlas pool + Burst bitmask BLF packer (shape, not rectangle).
    /// 候选图集池 + 位掩码 BLF 形状装箱（不用矩形装箱）。
    /// </summary>
    public static class AtoAtlas
    {
        public struct Candidate
        {
            public int W, H;
            public int Area => W * H;
            public float Aspect => Math.Max(W, H) / (float)Math.Max(1, Math.Min(W, H));
        }

        public sealed class PackedAtlas
        {
            public int W, H;
            public int Padding;
            public readonly List<AtoIsland> Islands = new List<AtoIsland>();
            public float Utilization;
            public Texture2D Texture;
            public AtoTextureClass Class;
            public string Name;
            public readonly List<Texture2D> Sources = new List<Texture2D>();
        }

        public static List<Candidate> BuildPool(AtoResolvedSettings s)
        {
            var list = new List<Candidate>();
            int min = Math.Max(64, s.minAtlasSide);
            int max = Math.Max(min, s.maxAtlasSide);
            if (s.experimentalNpot)
            {
                // Full 64-step on both axes is 128² candidates and too slow to BLF.
                // Keep 64-step on the long side, aspect ≤ 2, which still covers NPOT + MipStreaming.
                // 双轴 64 步进组合爆炸；长边 64 步进且长宽比≤2，仍覆盖 NPOT。
                var sides = new List<int>();
                for (int v = min; v <= max; v += 64) sides.Add(v);
                if (sides.Count == 0 || sides[sides.Count - 1] != max) sides.Add(max);
                foreach (var y in sides)
                foreach (var x in sides)
                {
                    float asp = Math.Max(x, y) / (float)Math.Max(1, Math.Min(x, y));
                    if (asp > 2.01f) continue;
                    list.Add(new Candidate { W = x, H = y });
                }
            }
            else
            {
                for (int y = min; y <= max; y <<= 1)
                for (int x = min; x <= max; x <<= 1)
                    list.Add(new Candidate { W = x, H = y });
            }
            return list;
        }

        public static int PaddingFor(int maxSide, int minPad)
        {
            int p = (maxSide + 127) / 128; // ceil
            if (p < minPad) p = minPad;
            if (p < 4) p = 4;
            return p;
        }

        /// <summary>
        /// Sort pool: drop those smaller than needed area, then area asc, then aspect asc (square first).
        /// 丢弃面积不足的，按面积升序、长宽比升序（更方优先）。
        /// </summary>
        public static List<Candidate> FilterSort(List<Candidate> pool, int neededArea)
        {
            var f = new List<Candidate>();
            foreach (var c in pool) if (c.Area >= neededArea) f.Add(c);
            f.Sort((a, b) =>
            {
                int k = a.Area.CompareTo(b.Area);
                if (k != 0) return k;
                return a.Aspect.CompareTo(b.Aspect);
            });
            return f;
        }

        const int Gran = 4; // 4px raster

        public static bool TryPack(List<AtoIsland> islands, Candidate cand, int padding, bool allowRotate)
        {
            int gw = cand.W / Gran, gh = cand.H / Gran;
            if (gw < 1 || gh < 1) return false;
            int stride = (gw + 63) / 64;
            var mask = new ulong[stride * gh];
            var order = new List<AtoIsland>(islands);
            // Area desc then side desc. 面积降序 + 边长降序。
            order.Sort((a, b) =>
            {
                int aa = a.PixelBounds.width * a.PixelBounds.height;
                int ba = b.PixelBounds.width * b.PixelBounds.height;
                int k = ba.CompareTo(aa);
                if (k != 0) return k;
                return Math.Max(b.PixelBounds.width, b.PixelBounds.height)
                    .CompareTo(Math.Max(a.PixelBounds.width, a.PixelBounds.height));
            });

            int padG = Math.Max(1, (padding + Gran - 1) / Gran);
            foreach (var isl in order)
            {
                EnsureCached(isl, padG);
                var shape = isl.CachedShape;
                int iw = isl.CachedIw, ih = isl.CachedIh;
                bool placed = PlaceShape(mask, stride, gw, gh, shape, iw, ih, out int px, out int py);
                bool rot = false;
                ulong[] shapeR = null;
                int rw = ih, rh = iw;
                if (!placed && allowRotate && iw != ih)
                {
                    shapeR = isl.CachedShapeRot;
                    rw = isl.CachedRw; rh = isl.CachedRh;
                    placed = PlaceShape(mask, stride, gw, gh, shapeR, rw, rh, out px, out py);
                    rot = placed;
                }
                if (!placed) return false;
                StampShape(mask, stride, rot ? shapeR : shape, rot ? rw : iw, rot ? rh : ih, px, py);
                isl.PackedX = px * Gran;
                isl.PackedY = py * Gran;
                isl.PackedW = (rot ? rw : iw) * Gran;
                isl.PackedH = (rot ? rh : ih) * Gran;
                isl.Rotated90 = rot;
            }
            return true;
        }

        static void EnsureCached(AtoIsland isl, int padG)
        {
            if (isl.CachedPadG == padG && isl.CachedShape != null) return;
            isl.CachedShape = RasterShape(isl, padG, false, out isl.CachedIw, out isl.CachedIh);
            isl.CachedShapeRot = RasterShape(isl, padG, true, out isl.CachedRw, out isl.CachedRh);
            isl.CachedPadG = padG;
        }

        /// <summary>
        /// Rasterize the island's triangles (plus padding) into a 4px bitmask. Not a bounding rectangle.
        /// 按三角形光栅化岛形状（含 padding），不是包围矩形。
        /// </summary>
        static ulong[] RasterShape(AtoIsland isl, int padG, bool rot90, out int iw, out int ih)
        {
            int pw = Math.Max(1, (Mathf.CeilToInt(isl.PixelBounds.width * isl.ScaleU) + Gran - 1) / Gran);
            int ph = Math.Max(1, (Mathf.CeilToInt(isl.PixelBounds.height * isl.ScaleV) + Gran - 1) / Gran);
            iw = (rot90 ? ph : pw) + padG;
            ih = (rot90 ? pw : ph) + padG;
            int stride = (iw + 63) / 64;
            var bits = new ulong[Math.Max(1, stride * ih)];
            float uvW = Mathf.Max(1e-8f, isl.Max.x - isl.Min.x);
            float uvH = Mathf.Max(1e-8f, isl.Max.y - isl.Min.y);
            var mesh = isl.Mesh;
            var uvs = new List<Vector2>();
            if (mesh != null) mesh.GetUVs(isl.UvChannel, uvs);
            var tris = isl.Triangles;
            if (uvs.Count > 0 && tris.Count >= 48)
            {
                try
                {
                    var burst = RasterBurst(isl, uvs, tris, pw, ph, rot90, iw, ih);
                    if (burst != null) return burst;
                }
                catch (Exception e)
                {
                    AtoLog.Detail("Burst raster fallback: " + e.Message);
                }
            }
            for (int t = 0; t + 2 < tris.Count; t += 3)
            {
                if (uvs.Count == 0) break;
                int i0 = tris[t], i1 = tris[t + 1], i2 = tris[t + 2];
                if ((uint)i0 >= (uint)uvs.Count || (uint)i1 >= (uint)uvs.Count || (uint)i2 >= (uint)uvs.Count) continue;
                Vector2 p0 = Map(uvs[i0] + isl.Translate, isl, pw, ph, rot90);
                Vector2 p1 = Map(uvs[i1] + isl.Translate, isl, pw, ph, rot90);
                Vector2 p2 = Map(uvs[i2] + isl.Translate, isl, pw, ph, rot90);
                int minx = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(p0.x, Mathf.Min(p1.x, p2.x))), 0, iw - 1);
                int maxx = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(p0.x, Mathf.Max(p1.x, p2.x))), 0, iw - 1);
                int miny = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(p0.y, Mathf.Min(p1.y, p2.y))), 0, ih - 1);
                int maxy = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(p0.y, Mathf.Max(p1.y, p2.y))), 0, ih - 1);
                for (int y = miny; y <= maxy; y++)
                for (int x = minx; x <= maxx; x++)
                {
                    if (!PointInTri(x + 0.5f, y + 0.5f, p0, p1, p2)) continue;
                    bits[y * stride + (x >> 6)] |= 1ul << (x & 63);
                }
            }
            // If mesh UVs were unavailable, fall back to a filled rect so packing still progresses.
            // 拿不到 UV 时回退实心矩形，保证装箱仍能进行。
            if (uvs.Count == 0)
            {
                for (int y = 0; y < ih; y++)
                for (int x = 0; x < iw; x++)
                    bits[y * stride + (x >> 6)] |= 1ul << (x & 63);
            }
            return bits;
        }

        static ulong[] RasterBurst(
            AtoIsland isl, List<Vector2> uvs, List<int> tris,
            int pw, int ph, bool rot90, int iw, int ih)
        {
            int triCount = tris.Count / 3;
            var uv = new NativeArray<float2>(triCount * 3, Allocator.TempJob);
            int n = 0;
            for (int t = 0; t + 2 < tris.Count; t += 3)
            {
                int i0 = tris[t], i1 = tris[t + 1], i2 = tris[t + 2];
                if ((uint)i0 >= (uint)uvs.Count || (uint)i1 >= (uint)uvs.Count || (uint)i2 >= (uint)uvs.Count)
                    continue;
                var p0 = Map(uvs[i0] + isl.Translate, isl, pw, ph, rot90);
                var p1 = Map(uvs[i1] + isl.Translate, isl, pw, ph, rot90);
                var p2 = Map(uvs[i2] + isl.Translate, isl, pw, ph, rot90);
                uv[n++] = new float2(p0.x, p0.y);
                uv[n++] = new float2(p1.x, p1.y);
                uv[n++] = new float2(p2.x, p2.y);
            }
            int written = n / 3;
            int stride = (iw + 63) / 64;
            var bits = new NativeArray<ulong>(Math.Max(1, stride * ih), Allocator.TempJob);
            try
            {
                new AtoRasterJob
                {
                    Uv = uv,
                    TriCount = written,
                    W = iw,
                    H = ih,
                    Bits = bits
                }.Run();
                var managed = new ulong[bits.Length];
                bits.CopyTo(managed);
                return managed;
            }
            finally
            {
                uv.Dispose();
                bits.Dispose();
            }
        }

        static Vector2 Map(Vector2 uv, AtoIsland isl, int pw, int ph, bool rot90)
        {
            float nx = (uv.x - isl.Min.x) / Mathf.Max(1e-8f, isl.Max.x - isl.Min.x) * pw;
            float ny = (uv.y - isl.Min.y) / Mathf.Max(1e-8f, isl.Max.y - isl.Min.y) * ph;
            return rot90 ? new Vector2(ny, nx) : new Vector2(nx, ny);
        }

        static bool PointInTri(float px, float py, Vector2 a, Vector2 b, Vector2 c)
        {
            float s = a.y * c.x - a.x * c.y + (c.y - a.y) * px + (a.x - c.x) * py;
            float t = a.x * b.y - a.y * b.x + (a.y - b.y) * px + (b.x - a.x) * py;
            if ((s < 0) != (t < 0) && s != 0 && t != 0) return false;
            float A = -b.y * c.x + a.y * (c.x - b.x) + a.x * (b.y - c.y) + b.x * c.y;
            return A < 0 ? (s <= 0 && s + t >= A) : (s >= 0 && s + t <= A);
        }

        static bool PlaceShape(ulong[] atlas, int aStride, int gw, int gh, ulong[] shape, int iw, int ih, out int ox, out int oy)
        {
            ox = oy = 0;
            if (iw > gw || ih > gh) return false;
            int sStride = (iw + 63) / 64;
            for (int y = 0; y <= gh - ih; y++)
            for (int x = 0; x <= gw - iw; x++)
            {
                if (!Overlaps(atlas, aStride, shape, sStride, x, y, iw, ih))
                {
                    ox = x; oy = y; return true;
                }
            }
            return false;
        }

        static bool Overlaps(ulong[] atlas, int aStride, ulong[] shape, int sStride, int x, int y, int iw, int ih)
        {
            for (int yy = 0; yy < ih; yy++)
            {
                int ar = (y + yy) * aStride;
                int sr = yy * sStride;
                for (int xx = 0; xx < iw; xx++)
                {
                    if ((shape[sr + (xx >> 6)] & (1ul << (xx & 63))) == 0) continue;
                    int ax = x + xx;
                    if ((atlas[ar + (ax >> 6)] & (1ul << (ax & 63))) != 0) return true;
                }
            }
            return false;
        }

        static void StampShape(ulong[] atlas, int aStride, ulong[] shape, int iw, int ih, int x, int y)
        {
            int sStride = (iw + 63) / 64;
            for (int yy = 0; yy < ih; yy++)
            {
                int ar = (y + yy) * aStride;
                int sr = yy * sStride;
                for (int xx = 0; xx < iw; xx++)
                {
                    if ((shape[sr + (xx >> 6)] & (1ul << (xx & 63))) == 0) continue;
                    int ax = x + xx;
                    atlas[ar + (ax >> 6)] |= 1ul << (ax & 63);
                }
            }
        }

        /// <summary>
        /// Stamp island pixels into atlas and GPU pull-push fill empty regions.
        /// 把岛像素写入图集，并用 pull-push 填满空白（透明贴图 alpha 保持 0）。
        /// </summary>
        public static Texture2D Compose(
            string name, int w, int h, List<AtoIsland> islands,
            Func<AtoIsland, Color[]> readIsland,
            bool linear, bool mips, bool keepAlphaZero)
        {
            var px = new Color[w * h];
            var filled = new bool[w * h];
            foreach (var isl in islands)
            {
                var src = readIsland(isl);
                int sw = Math.Max(1, Mathf.RoundToInt(isl.PixelBounds.width * isl.ScaleU));
                int sh = Math.Max(1, Mathf.RoundToInt(isl.PixelBounds.height * isl.ScaleV));
                if (src == null || src.Length < sw * sh) continue;
                for (int y = 0; y < sh; y++)
                for (int x = 0; x < sw; x++)
                {
                    int dx = isl.PackedX + x;
                    int dy = isl.PackedY + y;
                    if (isl.Rotated90)
                    {
                        dx = isl.PackedX + y;
                        dy = isl.PackedY + x;
                    }
                    if ((uint)dx >= (uint)w || (uint)dy >= (uint)h) continue;
                    px[dy * w + dx] = src[y * sw + x];
                    filled[dy * w + dx] = true;
                }
            }
            PullPushCpu(px, filled, w, h, keepAlphaZero);
            var tex = AtoTextureUtil.Create(name, w, h, px, linear, mips);
            tex.wrapMode = TextureWrapMode.Clamp;
            return tex;
        }

        static void PullPushCpu(Color[] px, bool[] filled, int w, int h, bool keepAlphaZero)
        {
            // Multi-resolution pull then push. 多分辨率 pull 再 push。
            var cur = px;
            var curF = filled;
            int cw = w, ch = h;
            var stackW = new List<int>();
            var stackH = new List<int>();
            var stackPx = new List<Color[]>();
            var stackF = new List<bool[]>();
            stackW.Add(cw); stackH.Add(ch); stackPx.Add(cur); stackF.Add(curF);
            while (cw > 2 && ch > 2)
            {
                int nw = Math.Max(1, cw / 2), nh = Math.Max(1, ch / 2);
                var np = new Color[nw * nh];
                var nf = new bool[nw * nh];
                for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                {
                    Color acc = Color.clear; int n = 0;
                    for (int oy = 0; oy < 2; oy++)
                    for (int ox = 0; ox < 2; ox++)
                    {
                        int sx = Math.Min(x * 2 + ox, cw - 1);
                        int sy = Math.Min(y * 2 + oy, ch - 1);
                        int i = sy * cw + sx;
                        if (curF[i]) { acc += cur[i]; n++; }
                    }
                    if (n > 0) { np[y * nw + x] = acc / n; nf[y * nw + x] = true; }
                }
                stackW.Add(nw); stackH.Add(nh); stackPx.Add(np); stackF.Add(nf);
                cur = np; curF = nf; cw = nw; ch = nh;
            }
            for (int level = stackPx.Count - 2; level >= 0; level--)
            {
                var hi = stackPx[level];
                var hf = stackF[level];
                int hw = stackW[level], hh = stackH[level];
                var lo = stackPx[level + 1];
                int lw = stackW[level + 1], lh = stackH[level + 1];
                for (int y = 0; y < hh; y++)
                for (int x = 0; x < hw; x++)
                {
                    int i = y * hw + x;
                    if (hf[i]) continue;
                    int lx = Math.Min(x / 2, lw - 1);
                    int ly = Math.Min(y / 2, lh - 1);
                    var c = lo[ly * lw + lx];
                    if (keepAlphaZero) c.a = 0;
                    hi[i] = c;
                    hf[i] = true;
                }
            }
        }

        /// <summary>
        /// Downscale a finished atlas (secondary maps whose quality bar is lower than albedo).
        /// 副图集整体缩小（质量需求低于主色时）。UV 仍是 0-1，安全。
        /// </summary>
        public static Texture2D DownscaleWhole(Texture2D src, int nw, int nh, bool linear, bool mips)
        {
            nw = Math.Max(4, nw); nh = Math.Max(4, nh);
            var px = src.GetPixels();
            var down = AtoGpu.ResampleOrCpu(px, src.width, src.height, nw, nh, false, linear);
            var t = AtoTextureUtil.Create(src.name, nw, nh, down, linear, mips);
            t.filterMode = src.filterMode;
            t.anisoLevel = src.anisoLevel;
            t.wrapMode = TextureWrapMode.Clamp;
            return t;
        }

        public static float Utilization(List<AtoIsland> islands, int w, int h)
        {
            double used = 0;
            foreach (var i in islands) used += Math.Max(1, i.PackedW) * (double)Math.Max(1, i.PackedH);
            return (float)(used / Math.Max(1, (double)w * h));
        }
    }
}
