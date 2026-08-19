// English: Stamp scaled islands into atlases, rotate normals, pull-push bleed, write imported PNG assets.
// 中文：把缩放后的岛盖进图集，旋转法线，pull-push 渗色，写出可导入的 PNG。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Net.Fosa.AvatarTextureOptimizer;
using Object = UnityEngine.Object;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    internal static class ATOComposer
    {
        public static List<ATOAtlasResult> ComposeBySemantic(ATOState state, ATOPacker.AtlasBuilder builder)
        {
            var result = new List<ATOAtlasResult>();
            if (builder == null || builder.Islands.Count == 0) return result;
            var buckets = new Dictionary<ATOTextureSemantic, List<ATOIsland>>();
            foreach (var isl in builder.Islands)
            {
                List<ATOIsland> list;
                if (!buckets.TryGetValue(isl.Semantic, out list))
                {
                    list = new List<ATOIsland>();
                    buckets[isl.Semantic] = list;
                }

                list.Add(isl);
            }

            ATOAtlasResult master = null;
            foreach (var kv in buckets)
            {
                var slice = new ATOPacker.AtlasBuilder(builder.W, builder.H);
                foreach (var isl in kv.Value) slice.Islands.Add(isl);
                foreach (var isl in kv.Value)
                {
                    if (isl.Source != null) slice.Sources.Add(isl.Source);
                }

                foreach (var g in builder.Groups) slice.Groups.Add(g);
                var atlas = Compose(state, slice);
                if (atlas == null) continue;
                if (kv.Key == ATOTextureSemantic.AlbedoOpaque || kv.Key == ATOTextureSemantic.AlbedoTransparent)
                    master = atlas;
                result.Add(atlas);
            }

            // English: Companion sheets with uniformly lower quality demand may be downscaled if padding still holds.
            // 中文：质量需求整体低于主色的伴侣图集，在 padding 仍满足时整体缩小。
            if (master != null)
            {
                var minPad = (int)state.Settings.minPadding;
                foreach (var atlas in result)
                {
                    if (atlas == master) continue;
                    if (atlas.Semantic != ATOTextureSemantic.Normal && atlas.Semantic != ATOTextureSemantic.Gray &&
                        atlas.Semantic != ATOTextureSemantic.Mask) continue;
                    var maxScale = 1f;
                    foreach (var isl in atlas.Islands)
                        maxScale = Mathf.Max(maxScale, Mathf.Max(isl.Scale.x, isl.Scale.y));
                    if (maxScale >= 0.999f) continue;
                    var nw = Mathf.Max(minPad * 2, Mathf.RoundToInt(atlas.Width * maxScale) / 4 * 4);
                    var nh = Mathf.Max(minPad * 2, Mathf.RoundToInt(atlas.Height * maxScale) / 4 * 4);
                    if (nw >= atlas.Width && nh >= atlas.Height) continue;
                    var smaller = ResampleTexture(state, atlas.Texture, nw, nh, false);
                    if (smaller == null) continue;
                    var ow = atlas.Width;
                    var oh = atlas.Height;
                    atlas.Texture = smaller;
                    atlas.Width = nw;
                    atlas.Height = nh;
                    foreach (var isl in atlas.Islands)
                    {
                        isl.PackX = isl.PackX * nw / Mathf.Max(1, ow);
                        isl.PackY = isl.PackY * nh / Mathf.Max(1, oh);
                        isl.PackW = Mathf.Max(1, isl.PackW * nw / Mathf.Max(1, ow));
                        isl.PackH = Mathf.Max(1, isl.PackH * nh / Mathf.Max(1, oh));
                        state.TextureReplace[isl.Source] = smaller;
                    }
                    state.Log.Info("companion atlas downscale " + atlas.Name + " -> " + nw + "x" + nh);
                }
            }

            return result;
        }

        public static ATOAtlasResult Compose(ATOState state, ATOPacker.AtlasBuilder builder)
        {
            if (builder.Islands.Count == 0) return null;
            var semantic = GuessSemantic(builder);
            var linear = false;
            var filter = FilterMode.Bilinear;
            foreach (var g in builder.Groups)
            {
                linear = linear || g.Linear;
                if (g.Filter == FilterMode.Trilinear) filter = FilterMode.Trilinear;
            }

            var tex = new Texture2D(builder.W, builder.H, TextureFormat.RGBA32, false, linear);
            var pixels = new Color32[builder.W * builder.H];
            // Transparent atlases keep alpha 0 in empty regions (user spec).
            var clear = new Color32(0, 0, 0, 0);
            for (var i = 0; i < pixels.Length; i++) pixels[i] = clear;

            foreach (var isl in builder.Islands)
            {
                StampIsland(state, pixels, builder.W, builder.H, isl, semantic);
            }

            PullPush(pixels, builder.W, builder.H, semantic == ATOTextureSemantic.AlbedoTransparent);

            tex.SetPixels32(pixels);
            tex.Apply(false, false);
            tex.filterMode = filter;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.name = UniqueName(state, semantic);

            var filled = 0;
            foreach (var isl in builder.Islands) filled += Mathf.Max(1, isl.PackW * isl.PackH);
            var util = filled / (float)(builder.W * builder.H);

            var path = WritePng(state, tex);
            Object.DestroyImmediate(tex);
            var imported = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (imported == null)
            {
                state.Log.Warn("failed to reimport atlas " + path);
                return null;
            }

            ATOImporterUtil.Apply(state, imported, semantic, linear, filter);

            var result = new ATOAtlasResult
            {
                Name = imported.name,
                Texture = imported,
                Width = builder.W,
                Height = builder.H,
                Semantic = semantic,
                Linear = linear,
                Filter = filter,
                Utilization = util
            };
            foreach (var s in builder.Sources) result.Sources.Add(s);
            foreach (var isl in builder.Islands)
            {
                isl.Atlas = result;
                result.Islands.Add(isl);
                state.TextureReplace[isl.Source] = imported;
            }

            state.Report.AtlasesBuilt++;
            state.Report.ResultPixels += (long)builder.W * builder.H;
            foreach (var s in builder.Sources)
                state.Report.SourcePixels += (long)s.width * s.height;
            var srcNames = new StringBuilder();
            foreach (var s in builder.Sources)
            {
                if (srcNames.Length > 0) srcNames.Append(',');
                srcNames.Append(s.name);
            }

            state.Report.AddAtlas(imported.name, builder.W, builder.H, util, srcNames.ToString(), builder.Islands.Count);
            state.Log.Info(string.Format("atlas {0} {1}x{2} util={3:P1} islands={4} sources={5}",
                imported.name, builder.W, builder.H, util, builder.Islands.Count, srcNames));
            state.Generated.Add(imported);
            return result;
        }

        public static Texture2D ResampleTexture(ATOState state, Texture2D src, int w, int h, bool rotate90)
        {
            var dec = state.Cache.Get(src, state.Log);
            if (dec == null) return null;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, dec.Linear);
            var px = new Color32[w * h];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    float u = (x + 0.5f) / w;
                    float v = (y + 0.5f) / h;
                    var sx = Mathf.Clamp(Mathf.FloorToInt(u * dec.Width), 0, dec.Width - 1);
                    var sy = Mathf.Clamp(Mathf.FloorToInt(v * dec.Height), 0, dec.Height - 1);
                    px[y * w + x] = dec.Get(sx, sy);
                }
            }

            tex.SetPixels32(px);
            tex.Apply(false, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            var path = WritePng(state, tex);
            Object.DestroyImmediate(tex);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static void StampIsland(ATOState state, Color32[] dest, int aw, int ah, ATOIsland isl,
            ATOTextureSemantic semantic)
        {
            var dec = state.Cache.Get(isl.Source, state.Log);
            if (dec == null) return;
            var x0 = Mathf.Clamp(Mathf.FloorToInt(isl.PixelBounds.xMin), 0, dec.Width - 1);
            var y0 = Mathf.Clamp(Mathf.FloorToInt(isl.PixelBounds.yMin), 0, dec.Height - 1);
            var sw = Mathf.Max(1, Mathf.RoundToInt(isl.PixelBounds.width));
            var sh = Mathf.Max(1, Mathf.RoundToInt(isl.PixelBounds.height));
            var dw = Mathf.Max(1, isl.PackW);
            var dh = Mathf.Max(1, isl.PackH);
            var isNormal = semantic == ATOTextureSemantic.Normal || isl.Semantic == ATOTextureSemantic.Normal;

            for (var y = 0; y < dh; y++)
            {
                for (var x = 0; x < dw; x++)
                {
                    float u, v;
                    if (isl.Rotated)
                    {
                        // 90° CCW in atlas space: (x,y) <- source (y, w-1-x) after scale.
                        u = (y + 0.5f) / dh;
                        v = 1f - (x + 0.5f) / dw;
                    }
                    else
                    {
                        u = (x + 0.5f) / dw;
                        v = (y + 0.5f) / dh;
                    }

                    var sx = x0 + u * sw;
                    var sy = y0 + v * sh;
                    var c = Bilinear(dec, sx, sy);
                    if (isNormal && isl.Rotated)
                    {
                        var n = ATOQuality.DecodeNormal((Color)c);
                        // 90° CCW: (x,y) -> (-y, x)
                        n = new Vector3(-n.y, n.x, n.z).normalized;
                        c = (Color32)(Color)ATOQuality.EncodeNormal(n);
                    }

                    var dx = isl.PackX + x;
                    var dy = isl.PackY + y;
                    if ((uint)dx < (uint)aw && (uint)dy < (uint)ah)
                        dest[dy * aw + dx] = c;
                }
            }
        }

        private static Color32 Bilinear(ATODecodedTexture dec, float x, float y)
        {
            var x0 = Mathf.Clamp(Mathf.FloorToInt(x), 0, dec.Width - 1);
            var y0 = Mathf.Clamp(Mathf.FloorToInt(y), 0, dec.Height - 1);
            var x1 = Mathf.Min(x0 + 1, dec.Width - 1);
            var y1 = Mathf.Min(y0 + 1, dec.Height - 1);
            var fx = x - x0;
            var fy = y - y0;
            var c00 = (Color)dec.Get(x0, y0);
            var c10 = (Color)dec.Get(x1, y0);
            var c01 = (Color)dec.Get(x0, y1);
            var c11 = (Color)dec.Get(x1, y1);
            return Color.Lerp(Color.Lerp(c00, c10, fx), Color.Lerp(c01, c11, fx), fy);
        }

        // English: Infinite pull-push / dilate of edge color into empty pixels. Transparent keeps alpha 0.
        // 中文：岛边缘颜色无限外扩填满空白。透明贴图 alpha 保持 0。
        private static void PullPush(Color32[] px, int w, int h, bool keepAlphaZero)
        {
            var filled = new bool[px.Length];
            var q = new Queue<int>();
            for (var i = 0; i < px.Length; i++)
            {
                if (px[i].a == 0 && px[i].r == 0 && px[i].g == 0 && px[i].b == 0) continue;
                filled[i] = true;
                q.Enqueue(i);
            }

            var dirs = new[] { 1, -1, w, -w };
            while (q.Count > 0)
            {
                var i = q.Dequeue();
                var x = i % w;
                var y = i / w;
                for (var d = 0; d < 4; d++)
                {
                    int nx = x, ny = y;
                    if (d == 0) nx++;
                    else if (d == 1) nx--;
                    else if (d == 2) ny++;
                    else ny--;
                    if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                    var ni = ny * w + nx;
                    if (filled[ni]) continue;
                    var c = px[i];
                    if (keepAlphaZero) c.a = 0;
                    px[ni] = c;
                    filled[ni] = true;
                    q.Enqueue(ni);
                }
            }
        }

        private static ATOTextureSemantic GuessSemantic(ATOPacker.AtlasBuilder b)
        {
            var hasNormal = false;
            var hasAlpha = false;
            var hasGray = false;
            foreach (var isl in b.Islands)
            {
                if (isl.Semantic == ATOTextureSemantic.Normal) hasNormal = true;
                if (isl.Semantic == ATOTextureSemantic.AlbedoTransparent) hasAlpha = true;
                if (isl.Semantic == ATOTextureSemantic.Gray || isl.Semantic == ATOTextureSemantic.Mask) hasGray = true;
            }

            if (hasNormal) return ATOTextureSemantic.Normal;
            if (hasGray && !hasAlpha) return ATOTextureSemantic.Gray;
            if (hasAlpha) return ATOTextureSemantic.AlbedoTransparent;
            return ATOTextureSemantic.AlbedoOpaque;
        }

        private static string UniqueName(ATOState state, ATOTextureSemantic sem)
        {
            return AvatarTextureOptimizer.AtlasNamePrefix + sem + "_" +
                   state.Build.AvatarRootObject.name + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        internal static string WritePng(ATOState state, Texture2D tex)
        {
            var folder = AvatarTextureOptimizer.GeneratedFolder + "/" + Sanitize(state.Build.AvatarRootObject.name);
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            var path = folder + "/" + Sanitize(tex.name) + ".png";
            File.WriteAllBytes(path, tex.EncodeToPNG());
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            return path;
        }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "ato";
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }
    }
}
