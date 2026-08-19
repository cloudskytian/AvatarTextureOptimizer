using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using FOSA.AvatarTextureOptimizer;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Blits scaled islands into an atlas and runs GPU pull-push bleed (alpha stays 0 for transparent).
    /// 把缩放后的岛画进图集，再做 GPU pull-push 渗色（透明贴图 alpha 保持 0）。
    /// </summary>
    internal static class ATOAtlasComposer
    {
        private static int _serial;

        public static ATOAtlasResult Compose(
            ATOContext ctx, Texture2D src, ATOTextureCategory cat,
            int aw, int ah, int pad, List<ATOUvGroup> groups)
        {
            var dec = ATOTextureUtil.Decode(ctx, src);
            var pixels = new Color[aw * ah];
            var coverage = new bool[aw * ah];
            var islandCount = 0;

            foreach (var g in groups)
            {
                foreach (var island in g.Islands)
                {
                    if (!island.Packed) continue;
                    if (!IslandUsesTexture(ctx, island, src)) continue;
                    BlitIsland(dec, cat, island, pixels, coverage, aw, ah);
                    islandCount++;
                }
            }

            PullPush(pixels, coverage, aw, ah, cat);

            var tex = new Texture2D(aw, ah, TextureFormat.RGBA32, true, dec.Linear)
            {
                name = $"{AvatarTextureOptimizer.AtlasNamePrefix}{src.name}_{++_serial}",
                filterMode = src.filterMode,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = Math.Max(src.anisoLevel, 1)
            };
            var cols32 = new Color32[pixels.Length];
            for (int i = 0; i < pixels.Length; i++) cols32[i] = pixels[i];
            tex.SetPixels32(cols32);
            tex.Apply(true, false);

            var path = WritePng(ctx, tex);
            var imported = ImportAsAtlas(ctx, path, src, cat, dec.Linear);
            if (imported == null) imported = tex;
            else
            {
                imported.name = tex.name;
                UnityEngine.Object.DestroyImmediate(tex);
            }

            ctx.Build.AssetSaver.SaveAsset(imported);
            ObjectRegistrySafe(src, imported);

            var used = 0;
            foreach (var c in coverage) if (c) used++;
            var result = new ATOAtlasResult
            {
                Source = src,
                Atlas = imported,
                Category = cat,
                Width = aw,
                Height = ah,
                Padding = pad,
                Utilization = used / (float)Math.Max(1, aw * ah),
                IslandCount = islandCount,
                OriginalBytes = ATOAssetUtil.EstimateTextureBytes(src),
                AtlasBytes = ATOAssetUtil.EstimateTextureBytes(imported),
                Name = imported.name
            };
            ctx.Report.BytesIn += result.OriginalBytes;
            ctx.Report.BytesOut += result.AtlasBytes;
            return result;
        }

        private static bool IslandUsesTexture(ATOContext ctx, ATOIsland island, Texture2D src)
        {
            foreach (var use in ctx.Uses)
            {
                if (use.Slot.texture != src) continue;
                if (use.Renderer != island.Renderer) continue;
                if (use.Slot.submeshIndex != island.Submesh) continue;
                if (use.Slot.uvChannel != island.UvChannel) continue;
                return true;
            }
            return false;
        }

        private static void BlitIsland(
            ATODecodedTexture dec, ATOTextureCategory cat, ATOIsland island,
            Color[] dest, bool[] coverage, int aw, int ah)
        {
            var crop = ATOQualityScaler.Crop(dec, island);
            var cw = Math.Max(1, Mathf.RoundToInt(island.UvSize.x * dec.Width));
            var ch = Math.Max(1, Mathf.RoundToInt(island.UvSize.y * dec.Height));
            if (crop.Length != cw * ch)
            {
                cw = Math.Max(1, island.OriginalPixelW);
                ch = Math.Max(1, island.OriginalPixelH);
            }

            var dw = Math.Max(1, island.Rotated ? island.ScaledH : island.ScaledW);
            var dh = Math.Max(1, island.Rotated ? island.ScaledW : island.ScaledH);
            // After rotation flag, ScaledW/H already store post-rotation size from packer.
            // 旋转标记之后，ScaledW/H 已被装箱器写成旋转后尺寸。
            dw = Math.Max(1, island.ScaledW);
            dh = Math.Max(1, island.ScaledH);

            Color[] scaled;
            if (cat == ATOTextureCategory.Normal)
            {
                var tmp = ATOQualityMetrics.DownsampleLinear(crop, cw, ch, island.Rotated ? dh : dw, island.Rotated ? dw : dh);
                scaled = island.Rotated ? Rotate90CwNormal(tmp, island.Rotated ? dh : dw, island.Rotated ? dw : dh) : tmp;
                Renormalize(scaled);
            }
            else if (cat == ATOTextureCategory.TransparentAlbedo)
            {
                var tw = island.Rotated ? dh : dw;
                var th = island.Rotated ? dw : dh;
                var tmp = ATOQualityMetrics.DownsamplePremultiplied(crop, cw, ch, tw, th);
                scaled = island.Rotated ? Rotate90Cw(tmp, tw, th) : tmp;
            }
            else
            {
                var tw = island.Rotated ? dh : dw;
                var th = island.Rotated ? dw : dh;
                var tmp = ATOQualityMetrics.DownsampleLinear(crop, cw, ch, tw, th);
                scaled = island.Rotated ? Rotate90Cw(tmp, tw, th) : tmp;
            }

            var sw = dw;
            var sh = dh;
            for (int y = 0; y < sh; y++)
            for (int x = 0; x < sw; x++)
            {
                var dx = island.PackedX + x;
                var dy = island.PackedY + y;
                if ((uint)dx >= (uint)aw || (uint)dy >= (uint)ah) continue;
                var di = dy * aw + dx;
                dest[di] = scaled[y * sw + x];
                coverage[di] = true;
            }
        }

        private static Color[] Rotate90Cw(Color[] src, int w, int h)
        {
            var dst = new Color[w * h];
            // src is w x h; dest is h x w. / 源 w×h，目标 h×w。
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var nx = h - 1 - y;
                var ny = x;
                dst[ny * h + nx] = src[y * w + x];
            }
            return dst;
        }

        private static Color[] Rotate90CwNormal(Color[] src, int w, int h)
        {
            var dst = Rotate90Cw(src, w, h);
            for (int i = 0; i < dst.Length; i++) dst[i] = ATOTextureUtil.SwizzleNormal90Cw(dst[i]);
            return dst;
        }

        private static void Renormalize(Color[] px)
        {
            for (int i = 0; i < px.Length; i++)
            {
                var n = new Vector3(px[i].r * 2f - 1f, px[i].g * 2f - 1f, px[i].b * 2f - 1f);
                if (n.sqrMagnitude < 1e-8f) n = Vector3.forward;
                n.Normalize();
                px[i] = new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f, px[i].a);
            }
        }

        /// <summary>
        /// Cheap pull-push: repeatedly dilate covered colors into empty neighbors (infinite expand).
        /// 廉价 pull-push：反复把已覆盖颜色扩到空邻域（无限外扩）。
        /// Transparent atlases keep alpha = 0 on empty pixels.
        /// 透明图集空白像素的 alpha 保持 0。
        /// </summary>
        private static void PullPush(Color[] px, bool[] cov, int w, int h, ATOTextureCategory cat)
        {
            var keepAlphaZero = cat == ATOTextureCategory.TransparentAlbedo;
            var next = new bool[cov.Length];
            bool grew = true;
            int guard = w + h;
            while (grew && guard-- > 0)
            {
                grew = false;
                Array.Copy(cov, next, cov.Length);
                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var i = y * w + x;
                    if (cov[i]) continue;
                    Color acc = default;
                    int n = 0;
                    Acc(x - 1, y); Acc(x + 1, y); Acc(x, y - 1); Acc(x, y + 1);
                    if (n == 0) continue;
                    acc /= n;
                    if (keepAlphaZero) acc.a = 0f;
                    px[i] = acc;
                    next[i] = true;
                    grew = true;

                    void Acc(int xx, int yy)
                    {
                        if ((uint)xx >= (uint)w || (uint)yy >= (uint)h) return;
                        var j = yy * w + xx;
                        if (!cov[j]) return;
                        acc += px[j];
                        n++;
                    }
                }
                Array.Copy(next, cov, cov.Length);
            }
        }

        private static string WritePng(ATOContext ctx, Texture2D tex)
        {
            var folder = ctx.TempFolder;
            ATOAssetUtil.EnsureFolder(folder);
            var path = $"{folder}/{tex.name}.png";
            File.WriteAllBytes(path, tex.EncodeToPNG());
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            return path;
        }

        private static Texture2D ImportAsAtlas(ATOContext ctx, string path, Texture2D src, ATOTextureCategory cat, bool linear)
        {
            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp == null) return AssetDatabase.LoadAssetAtPath<Texture2D>(path);

            imp.textureType = cat == ATOTextureCategory.Normal
                ? TextureImporterType.NormalMap
                : TextureImporterType.Default;
            imp.sRGBTexture = !linear && cat != ATOTextureCategory.Normal;
            imp.mipmapEnabled = ctx.Settings.MipStreamingFor(cat);
            imp.streamingMipmaps = imp.mipmapEnabled;
            imp.wrapMode = TextureWrapMode.Clamp;
            imp.filterMode = src.filterMode;
            imp.anisoLevel = Math.Max(src.anisoLevel, 1);
            imp.npotScale = ctx.Settings.experimentalNpot ? TextureImporterNPOTScale.None : TextureImporterNPOTScale.ToNearest;
            imp.isReadable = false;
            imp.alphaIsTransparency = cat == ATOTextureCategory.TransparentAlbedo;
            imp.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static void ObjectRegistrySafe(UnityEngine.Object a, UnityEngine.Object b)
        {
            try { nadena.dev.ndmf.ObjectRegistry.RegisterReplacedObject(a, b); }
            catch { /* ignore */ }
        }
    }
}
