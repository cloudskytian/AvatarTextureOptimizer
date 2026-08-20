using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// EN: The no-atlas path. Unused UV space is kept, UVs are untouched, and each texture is simply
    ///     rescaled as a whole to the largest island scale it needs, then re-imported with the chosen
    ///     format and mip policy. This is also the fallback for UV groups that could not be atlased.
    /// ZH: 不生成图集的路径。保留未使用的 UV 空间、不改动 UV，只把每张贴图整体缩放到它所需的
    ///     最大岛缩放，然后按选定格式与 mip 策略重新导入。
    ///     这同时也是无法图集化的 UV 组的回退路径。
    /// </summary>
    public static class WholeTexturePipeline
    {
        /// <summary>EN: Execute the whole-texture path. ZH: 执行整图缩放路径。</summary>
        public static void Run(List<UVGroup> groups, PlatformProfile profile, ATOPlatform platform,
            GPUTextureIO io, ATOLog log, ATOProgress progress, ATOReport report,
            Dictionary<Texture2D, Texture2D> remap, List<Texture2D> generated)
        {
            var quality = profile.EffectiveQuality;
            // EN: A texture already replaced by an atlas must not be replaced a second time, or the
            //     material would point at a plain rescale while the mesh UVs point into the atlas.
            // ZH: 已被图集替换的贴图绝不能再被替换第二次，
            //     否则材质会指向普通缩放图，而网格 UV 却指向图集。
            var alreadyHandled = new HashSet<Texture2D>(remap.Keys);

            // EN: One scale per texture: the largest any island of any group using it demands.
            // ZH: 每张贴图一个缩放值：取使用它的所有组的所有岛中最大的需求。
            var scaleOf = new Dictionary<AtoTexture, float>();
            foreach (var group in groups)
            {
                if (group.FullyWhitelisted) continue;
                float need = group.Islands.Count == 0
                    ? 1f
                    : group.Islands.Max(i => Mathf.Max(i.ScaleU, i.ScaleV));
                foreach (var t in group.Textures.SelectMany(kv => kv.Value))
                {
                    var rep = t.Representative;
                    if (rep.Whitelisted) continue;
                    scaleOf[rep] = Mathf.Max(scaleOf.TryGetValue(rep, out var s) ? s : 0f, need);
                }
            }

            int i2 = 0;
            foreach (var kv in scaleOf)
            {
                progress.ThrowIfCancelled();
                progress.Report(0.66f + 0.18f * (++i2 / (float)Math.Max(1, scaleOf.Count)));

                var tex = kv.Key;
                if (alreadyHandled.Contains(tex.Source)) continue;
                float scale = Mathf.Clamp01(kv.Value);

                int w = Mathf.Max(1, Mathf.RoundToInt(tex.Width * scale));
                int h = Mathf.Max(1, Mathf.RoundToInt(tex.Height * scale));

                // EN: Compression block formats want multiples of four; rounding up is free and avoids a
                //     silent fallback to an uncompressed format.
                // ZH: 压缩块格式要求 4 的倍数；向上取整没有代价，且能避免静默回退到未压缩格式。
                w = Mathf.Max(4, (w + 3) / 4 * 4);
                h = Mathf.Max(4, (h + 3) / 4 * 4);

                bool identical = w == tex.Width && h == tex.Height;
                if (identical && quality.IsLossless) continue;

                var decoded = io.Decode(tex.Source, tex.SRGB);
                var full = ImageOps.Extract(decoded, new RectInt(0, 0, decoded.Width, decoded.Height));

                Tile scaled;
                if (tex.Class == TextureClass.Normal)
                {
                    var enc = ImageOps.EncodeNormals(
                        ImageOps.DecodeNormals(full, !tex.UsedChannels.B && tex.UsedChannels.A), full.W, full.H);
                    var small = ImageOps.Downsample(enc, w, h, false);
                    scaled = ImageOps.EncodeNormals(ImageOps.DecodeNormals(small, false), small.W, small.H);
                }
                else
                {
                    scaled = ImageOps.Downsample(full, w, h, tex.Class == TextureClass.TransparentColor);
                }

                int mipCount = profile.output.mipmapAndStreaming
                    ? Mathf.FloorToInt(Mathf.Log(Mathf.Max(w, h), 2f)) + 1
                    : 1;

                var outTex = BuildTexture(scaled, tex.SRGB, mipCount, tex.Source.name + " (ATO)");
                outTex.filterMode = tex.Filter;
                outTex.wrapMode = tex.Wrap;
                outTex.anisoLevel = tex.AnisoLevel;

                TextureOutput.Apply(outTex, tex.Class, tex.HasAlpha, tex.UsedChannels,
                    profile, platform, profile.experimentalNPOT, log);

                remap[tex.Source] = outTex;
                generated.Add(outTex);
                io.Evict(tex.Source);

                log.Detail($"Rescaled '{tex.Source.name}': {tex.Width}x{tex.Height} -> {w}x{h} " +
                           $"({TextureOutput.EstimateBytes(tex.Source) / 1024} KB -> {TextureOutput.EstimateBytes(outTex) / 1024} KB)");
            }
        }

        private static Texture2D BuildTexture(Tile tile, bool srgb, int mipCount, string name)
        {
            var tex = new Texture2D(tile.W, tile.H, TextureFormat.RGBA32, mipCount, linear: !srgb) { name = name };
            var level = tile;
            for (int mip = 0; mip < mipCount; mip++)
            {
                var data = new Color32[level.W * level.H];
                for (int i = 0; i < data.Length; i++)
                {
                    var c = level.P[i];
                    if (srgb)
                        data[i] = new Color32(B(S(c.r)), B(S(c.g)), B(S(c.b)), B(c.a));
                    else
                        data[i] = new Color32(B(c.r), B(c.g), B(c.b), B(c.a));
                }
                tex.SetPixelData(data, mip);
                if (mip + 1 < mipCount)
                    level = ImageOps.Downsample(level, Mathf.Max(1, level.W / 2), Mathf.Max(1, level.H / 2), true);
            }
            tex.Apply(false, false);
            return tex;
        }

        private static byte B(float v) => (byte)Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);

        private static float S(float c)
        {
            c = Mathf.Max(0f, c);
            return c <= 0.0031308f ? c * 12.92f : 1.055f * Mathf.Pow(c, 1f / 2.4f) - 0.055f;
        }
    }
}
