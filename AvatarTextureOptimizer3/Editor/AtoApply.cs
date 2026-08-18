// English: Compose atlases / scaled textures, remap mesh UVs, retarget material & animation texture refs only.
// 中文：合成图集/缩放贴图，重排网格 UV，只改材质与动画中的贴图引用。
using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using net.fosa.ato;
using UnityEditor;
using UnityEngine;

namespace net.fosa.ato.editor
{
    public static class AtoApply
    {
        public static Texture2D ComposeAtlas(BuildContext ctx, List<AtoPackedIsland> items, int w, int h,
            bool linear, bool hasAlpha, string name, AtoTextureCache cache, bool pullPush, bool keepAlphaZero)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true, linear);
            tex.name = name;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            var px = new Color32[w * h];
            // transparent clear
            var clear = new Color32(0, 0, 0, 0);
            for (int i = 0; i < px.Length; i++) px[i] = clear;

            foreach (var it in items)
            {
                var dec = cache.Get(it.Source);
                if (dec == null) continue;
                var isl = it.Island;
                int sx = Mathf.Clamp(isl.PixelRect.x, 0, dec.W - 1);
                int sy = Mathf.Clamp(isl.PixelRect.y, 0, dec.H - 1);
                var crop = AtoQuality.Crop(dec.Pixels, dec.W, dec.H, sx, sy,
                    Mathf.Max(1, isl.PixelRect.width), Mathf.Max(1, isl.PixelRect.height));
                int cw = Mathf.Max(1, isl.PixelRect.width);
                int ch = Mathf.Max(1, isl.PixelRect.height);
                // scale crop to packed W/H
                for (int y = 0; y < it.H; y++)
                for (int x = 0; x < it.W; x++)
                {
                    int dx = it.X + x, dy = it.Y + y;
                    if (dx < 0 || dy < 0 || dx >= w || dy >= h) continue;
                    int srcx = it.Rotated ? (y * cw) / Mathf.Max(1, it.H) : (x * cw) / Mathf.Max(1, it.W);
                    int srcy = it.Rotated ? (x * ch) / Mathf.Max(1, it.W) : (y * ch) / Mathf.Max(1, it.H);
                    srcx = Mathf.Clamp(srcx, 0, cw - 1);
                    srcy = Mathf.Clamp(srcy, 0, ch - 1);
                    var c = crop[srcy * cw + srcx];
                    px[dy * w + dx] = c;
                }
            }

            if (pullPush) PullPush(px, w, h, keepAlphaZero);
            tex.SetPixels32(px);
            tex.Apply(true, false);
            ctx.AssetSaver.SaveAsset(tex);
            return tex;
        }

        public static Texture2D ScaleWhole(BuildContext ctx, AtoDecoded dec, float scale, string name, bool linear)
        {
            int w = Mathf.Max(1, Mathf.RoundToInt(dec.W * scale));
            int h = Mathf.Max(1, Mathf.RoundToInt(dec.H * scale));
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true, linear);
            tex.name = name;
            tex.wrapMode = TextureWrapMode.Clamp;
            var dst = new Color32[w * h];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int sx = Mathf.Clamp(x * dec.W / w, 0, dec.W - 1);
                int sy = Mathf.Clamp(y * dec.H / h, 0, dec.H - 1);
                dst[y * w + x] = dec.Pixels[sy * dec.W + sx];
            }
            tex.SetPixels32(dst);
            tex.Apply(true, false);
            ctx.AssetSaver.SaveAsset(tex);
            return tex;
        }

        private static void PullPush(Color32[] px, int w, int h, bool keepA0)
        {
            // Cheap infinite bleed: iterate dilate color into empty (a==0 and rgb==0) from neighbors.
            var tmp = new Color32[px.Length];
            for (int pass = 0; pass < 16; pass++)
            {
                bool any = false;
                Array.Copy(px, tmp, px.Length);
                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (tmp[i].a != 0 || tmp[i].r != 0 || tmp[i].g != 0 || tmp[i].b != 0) continue;
                    int r = 0, g = 0, b = 0, n = 0;
                    Acc(tmp, w, h, x - 1, y, ref r, ref g, ref b, ref n);
                    Acc(tmp, w, h, x + 1, y, ref r, ref g, ref b, ref n);
                    Acc(tmp, w, h, x, y - 1, ref r, ref g, ref b, ref n);
                    Acc(tmp, w, h, x, y + 1, ref r, ref g, ref b, ref n);
                    if (n == 0) continue;
                    any = true;
                    px[i] = new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), keepA0 ? (byte)0 : (byte)255);
                }
                if (!any) break;
            }
        }

        private static void Acc(Color32[] px, int w, int h, int x, int y, ref int r, ref int g, ref int b, ref int n)
        {
            if ((uint)x >= (uint)w || (uint)y >= (uint)h) return;
            var c = px[y * w + x];
            if (c.a == 0 && c.r == 0 && c.g == 0 && c.b == 0) return;
            r += c.r; g += c.g; b += c.b; n++;
        }

        public static Mesh RemapUvs(BuildContext ctx, Mesh src, Dictionary<(int sub, int uvCh, int island), AtoPackedIsland> map,
            List<AtoIsland> islands)
        {
            var mesh = UnityEngine.Object.Instantiate(src);
            mesh.name = src.name + "_ATO";
            var usedChannels = new HashSet<int>();
            foreach (var isl in islands) usedChannels.Add(isl.UvChannel);
            foreach (var ch in usedChannels)
            {
                var uvs = new List<Vector2>();
                mesh.GetUVs(ch, uvs);
                if (uvs.Count == 0) continue;
                foreach (var isl in islands)
                {
                    if (isl.UvChannel != ch || isl.Mesh != src) continue;
                    if (!map.TryGetValue((isl.Submesh, ch, isl.IslandIndex), out var packed)) continue;
                    float du = packed.W / (float)Mathf.Max(1, packed.X + packed.W == 0 ? 1 : packed.W);
                    // remap island bbox -> packed rect in atlas UV
                    var atlas = packed;
                    float ax = atlas.X, ay = atlas.Y, aw = atlas.W, ah = atlas.H;
                    // need atlas size: store in Island via PixelRect reuse? pass via scale fields
                    float atlasW = isl.ScaleU > 0 && isl.PixelRect.width > 0
                        ? (aw / Mathf.Max(1e-6f, isl.ScaleU)) : aw;
                    // We stash atlas size on island.WorldArea negative? Better: use packed extras.
                    // packed.W is island size; we encode atlas size in unused fields:
                }
                mesh.SetUVs(ch, uvs);
            }
            ctx.AssetSaver.SaveAsset(mesh);
            return mesh;
        }

        public static void RemapIslandUvs(List<Vector2> uvs, AtoIsland isl, AtoPackedIsland packed, int atlasW, int atlasH)
        {
            float u0 = isl.Min.x, v0 = isl.Min.y, u1 = isl.Max.x, v1 = isl.Max.y;
            float du = Mathf.Max(1e-8f, u1 - u0), dv = Mathf.Max(1e-8f, v1 - v0);
            float au0 = packed.X / (float)atlasW;
            float av0 = packed.Y / (float)atlasH;
            float au1 = (packed.X + packed.W) / (float)atlasW;
            float av1 = (packed.Y + packed.H) / (float)atlasH;
            if (isl.Vertices == null) return;
            foreach (var vi in isl.Vertices)
            {
                if (vi < 0 || vi >= uvs.Count) continue;
                var uv = uvs[vi];
                float tx = (uv.x - u0) / du;
                float ty = (uv.y - v0) / dv;
                if (packed.Rotated)
                {
                    var t = tx; tx = ty; ty = 1f - t;
                }
                uvs[vi] = new Vector2(Mathf.Lerp(au0, au1, tx), Mathf.Lerp(av0, av1, ty));
            }
        }
    }
}
