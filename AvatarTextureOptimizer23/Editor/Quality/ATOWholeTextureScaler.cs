using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using FOSA.AvatarTextureOptimizer;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// No-atlas path: scale whole textures, do not cull unused UV, do not rearrange UV.
    /// 不生成图集：缩放整张贴图，不剔除未使用 UV，不重排 UV。
    /// </summary>
    internal static class ATOWholeTextureScaler
    {
        public static void Run(ATOContext ctx)
        {
            var seen = new HashSet<Texture2D>();
            foreach (var use in ctx.Uses)
            {
                var src = use.Slot.texture;
                if (src == null || !seen.Add(src)) continue;
                if (ctx.WhitelistedTextures.Contains(src)) continue;
                ctx.Progress.ThrowIfCanceled();

                if (ctx.Settings.quality.SkipUvScale)
                {
                    ctx.Log.Detail($"Whole-tex skip scale '{src.name}' (lossless)");
                    continue;
                }

                var dec = ATOTextureUtil.Decode(ctx, src);
                if (ATOTextureUtil.IsSolidColor(dec.Pixels, out _))
                {
                    var s = Math.Min(4, Math.Min(src.width, src.height));
                    Replace(ctx, src, use.Slot.category, Down(dec, s, s, use.Slot.category));
                    continue;
                }

                var bestW = src.width;
                var bestH = src.height;
                int lo = 4, hi = Math.Min(src.width, src.height);
                while (lo <= hi)
                {
                    var mid = (lo + hi) / 2;
                    var tw = Math.Max(1, src.width * mid / Math.Max(1, Math.Min(src.width, src.height)));
                    var th = Math.Max(1, src.height * mid / Math.Max(1, Math.Min(src.width, src.height)));
                    tw = Math.Min(tw, src.width);
                    th = Math.Min(th, src.height);
                    var scaled = Down(dec, tw, th, use.Slot.category);
                    if (ATOQualityMetrics.Passes(ctx, dec.Pixels, dec.Width, dec.Height, scaled, tw, th,
                            use.Slot.category, use.Slot.alphaMode, use.Slot.cutoff, ctx.Settings.quality, out _))
                    {
                        bestW = tw; bestH = th;
                        hi = mid - 1;
                    }
                    else lo = mid + 1;
                }

                if (bestW < src.width || bestH < src.height)
                {
                    Replace(ctx, src, use.Slot.category, Down(dec, bestW, bestH, use.Slot.category));
                    ctx.Log.Detail($"Whole-tex '{src.name}' {src.width}x{src.height} → {bestW}x{bestH}");
                }
            }
        }

        private static Color[] Down(ATODecodedTexture dec, int w, int h, ATOTextureCategory cat)
        {
            if (cat == ATOTextureCategory.TransparentAlbedo)
                return ATOQualityMetrics.DownsamplePremultiplied(dec.Pixels, dec.Width, dec.Height, w, h);
            return ATOQualityMetrics.DownsampleLinear(dec.Pixels, dec.Width, dec.Height, w, h);
        }

        private static void Replace(ATOContext ctx, Texture2D src, ATOTextureCategory cat, Color[] px)
        {
            // Infer size from pixel count + original aspect. / 用像素数和原宽高比反推尺寸。
            var aspect = src.width / (float)Math.Max(1, src.height);
            var h = Math.Max(1, Mathf.RoundToInt(Mathf.Sqrt(px.Length / Math.Max(1e-4f, aspect))));
            var w = Math.Max(1, px.Length / h);
            if (w * h != px.Length)
            {
                h = Math.Max(1, src.height * w / Math.Max(1, src.width));
            }

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true, ATOTextureUtil.GuessLinear(src))
            {
                name = AvatarTextureOptimizer.AtlasNamePrefix + src.name,
                filterMode = src.filterMode,
                wrapMode = src.wrapMode
            };
            tex.SetPixels(px);
            tex.Apply(true, false);

            var folder = ctx.TempFolder;
            ATOAssetUtil.EnsureFolder(folder);
            var path = $"{folder}/{tex.name}.png";
            File.WriteAllBytes(path, tex.EncodeToPNG());
            AssetDatabase.ImportAsset(path);
            var imported = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (imported != null)
            {
                ctx.TextureRemap[src] = imported;
                ctx.Build.AssetSaver.SaveAsset(imported);
                UnityEngine.Object.DestroyImmediate(tex);
            }
            else
            {
                ctx.TextureRemap[src] = tex;
                ctx.Build.AssetSaver.SaveAsset(tex);
            }
        }
    }
}
