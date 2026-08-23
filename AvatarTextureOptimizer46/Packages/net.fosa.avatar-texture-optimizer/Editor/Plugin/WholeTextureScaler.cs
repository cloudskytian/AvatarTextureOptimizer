// SPDX-License-Identifier: MIT
// EN: The no-atlas path: scale whole textures, keep UVs untouched.
// ZH: 无图集路径：整张贴图缩放，UV 保持不变。

using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using Net.Fosa.AvatarTextureOptimizer.Editor.Atlas;
using Net.Fosa.AvatarTextureOptimizer.Editor.Core;
using Net.Fosa.AvatarTextureOptimizer.Editor.Model;
using Net.Fosa.AvatarTextureOptimizer.Editor.Quality;
using Net.Fosa.AvatarTextureOptimizer.Editor.Textures;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Plugin
{
    /// <summary>
    /// EN: When atlas generation is off, ATO still downscales textures, still deduplicates them and still
    ///     applies the import/compression settings. No UV is touched and nothing is discarded.
    /// ZH: 关闭图集生成时，ATO 仍会缩小贴图、去重并应用导入/压缩设置。
    ///     不改动任何 UV，也不剔除任何内容。
    /// </summary>
    public sealed class WholeTextureScaler
    {
        private const string Stage = "Scale";

        private readonly BuildContext _ctx;
        private readonly AtoProfile _profile;
        private readonly AtoPlatform _platform;

        /// <summary>EN: Creates the scaler. ZH: 创建缩放器。</summary>
        public WholeTextureScaler(BuildContext ctx, AtoProfile profile, AtoPlatform platform)
        {
            _ctx = ctx;
            _profile = profile;
            _platform = platform;
        }

        /// <summary>
        /// EN: Runs the scaling for every optimizable texture and records the replacements.
        /// ZH: 对每张可优化贴图执行缩放，并记录替换关系。
        /// </summary>
        public void Run(AtoCollection collection, Dictionary<Texture, Texture> replacements, AtoProgress progress)
        {
            var q = _profile.EffectiveQuality;
            var entries = collection.Textures.Values.Where(e => e.IsOptimizable).ToList();

            int i = 0;
            foreach (var entry in entries)
            {
                progress?.Step(++i / (float)Mathf.Max(1, entries.Count), entry.Texture.name);

                if (q.IsLossless)
                {
                    AtoLog.Debug_(Stage, $"'{entry.Texture.name}': lossless tier, copied unchanged.");
                    continue;
                }

                var scaled = ScaleOne(entry, q);
                if (scaled == null) continue;
                replacements[entry.Texture] = scaled;
                entry.Result = scaled;
            }

            AtoLog.Info(Stage, $"scaled {replacements.Count} of {entries.Count} textures");
        }

        private Texture2D ScaleOne(TextureEntry entry, AtoQualityParameters q)
        {
            RenderTexture source = null;
            try
            {
                source = GpuTextureUtil.ToLinearRT(entry.Texture);

                var alphaMode = AtoAlphaMode.Opaque;
                float cutoff = 1f;
                foreach (var u in entry.Usages)
                {
                    if (u.AlphaMode > alphaMode) alphaMode = u.AlphaMode;
                    if (u.AlphaMode == AtoAlphaMode.Cutout) cutoff = Mathf.Min(cutoff, u.Cutoff);
                }

                var solver = new SolverTexture
                {
                    Entry = entry,
                    LinearSource = source,
                    AlphaMode = alphaMode,
                    Cutoff = alphaMode == AtoAlphaMode.Cutout ? cutoff : 0.5f,
                    NormalEncoding = entry.Kind == AtoTextureKind.Normal ? NormalEncoding.Rgb : NormalEncoding.Rgb,
                };

                // EN: Treat the whole texture as a single island and reuse the exact same solver, which
                //     keeps the two code paths perceptually consistent.
                // ZH: 把整张贴图当作一个岛并复用完全相同的求解器，使两条代码路径在感知上保持一致。
                var island = new UvIsland
                {
                    Index = 0,
                    Bounds = new RectInt(0, 0, source.width, source.height),
                    MaskWidth = 1,
                    MaskHeight = 1,
                    Mask = new[] { true },
                    CoveredCells = 1,
                    SolidColor = entry.IsSolidColor,
                    WorldAreaM2 = 0f,
                };

                IslandQualitySolver.Solve(island, new[] { solver }, q, new Vector2Int(source.width, source.height), null);

                var target = new Vector2Int(
                    Mathf.Max(4, RoundToMultipleOfFour(island.ScaledSize.x)),
                    Mathf.Max(4, RoundToMultipleOfFour(island.ScaledSize.y)));
                if (target.x >= source.width && target.y >= source.height)
                {
                    AtoLog.Debug_(Stage, $"'{entry.Texture.name}' already at the optimum size.");
                    return null;
                }

                var small = GpuTextureUtil.Downsample(source, new RectInt(0, 0, source.width, source.height), target, entry.HasAlpha);
                try
                {
                    var tex = GpuTextureUtil.ToTexture2D(small, entry.SRgb, _profile.textures.mipmapAndStreaming && entry.HasMipmaps);
                    tex.name = $"ATO_{entry.Texture.name}_{target.x}x{target.y}";
                    tex.wrapMode = entry.WrapMode;
                    tex.filterMode = entry.FilterMode;
                    tex.anisoLevel = entry.AnisoLevel;
                    Compress(tex, entry);
                    _ctx.AssetSaver.SaveAsset(tex);

                    AtoLog.Info(Stage,
                        $"'{entry.Texture.name}': {source.width}x{source.height} -> {target.x}x{target.y} " +
                        $"({100f - 100f * (target.x * (long)target.y) / (source.width * (long)source.height):F1}% fewer texels)");
                    return tex;
                }
                finally
                {
                    GpuTextureUtil.Release(small);
                }
            }
            catch (Exception e)
            {
                AtoLog.Warning(Stage, $"could not scale '{entry.Texture.name}': {e.Message}");
                return null;
            }
            finally
            {
                GpuTextureUtil.Release(source);
            }
        }

        /// <summary>
        /// EN: Block compressed formats require multiples of four; rounding here avoids a silent fallback
        ///     to RGBA32 later.
        /// ZH: 块压缩格式要求 4 的倍数；在此取整可避免之后静默回退到 RGBA32。
        /// </summary>
        private static int RoundToMultipleOfFour(int v) => Mathf.Max(4, (v + 3) / 4 * 4);

        private void Compress(Texture2D tex, TextureEntry entry)
        {
            TextureFormat format;
            switch (entry.Kind)
            {
                case AtoTextureKind.Normal:
                    format = TextureFormatResolver.ResolveNormal(_platform, _profile.textures.normalFormat, _profile.allowNpot);
                    break;
                case AtoTextureKind.Grayscale:
                    bool multi = CountBits(entry.UsedChannelMask & 0xF) > 1;
                    format = TextureFormatResolver.ResolveGrayscale(_platform, _profile.textures.grayscaleFormat, multi, _profile.allowNpot, out _);
                    break;
                default:
                    format = TextureFormatResolver.ResolveColor(_platform, entry.HasAlpha,
                        _profile.textures.colorOpaqueFormat, _profile.textures.colorAlphaFormat, _profile.allowNpot);
                    break;
            }
            EditorTextureCompressor.Compress(tex, format);
        }

        private static int CountBits(int v)
        {
            int c = 0;
            while (v != 0) { c += v & 1; v >>= 1; }
            return c;
        }
    }
}
