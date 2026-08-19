// ============================================================================
// AvatarTextureOptimizer (net.fosa.avatar-texture-optimizer)
// Output/ScaledTextureBuilder.cs — 整图缩放贴图生成 / Whole-texture scaled texture builder
//
// 用途: 图集关闭模式、或无法装箱的兜底组——按目标质量缩放整张贴图。
// 共识: 复用图集构建器的 GPU 重采样；近无损直接拷贝原始像素。
// ============================================================================
using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// 整图缩放生成器 / Whole-texture scaler builder.
    /// </summary>
    public static class ScaledTextureBuilder
    {
        /// <summary>
        /// 为指定贴图生成缩放副本（in-memory；后续由 ImportSettingsApplier 持久化）/
        /// Build scaled copies for the given textures (in-memory; persisted later).
        /// </summary>
        /// <param name="textures">需要整图缩放的贴图引用 / texture refs to scale</param>
        /// <param name="ctx">缩放上下文 / scaling context</param>
        /// <returns>old → scaled in-memory texture</returns>
        public static Dictionary<Texture2D, Texture2D> Build(List<TextureRef> textures, ScalerContext ctx)
        {
            var result = new Dictionary<Texture2D, Texture2D>();
            var seen = new HashSet<Texture2D>();

            foreach (var tref in textures)
            {
                if (tref == null || tref.whitelisted || tref.source == null) continue;
                if (!seen.Add(tref.source)) continue;

                var (tw, th) = IslandScaler.ScaleWholeTexture(tref, ctx);
                if (tw <= 0 || th <= 0) continue;

                var copy = ctx.cache.GetCopy(tref.source, tref.sRGB);
                if (copy == null) continue;

                Texture2D scaled;
                if (tw == copy.width && th == copy.height)
                {
                    scaled = new Texture2D(tw, th, TextureFormat.RGBA32, false, linear: !tref.sRGB);
                    scaled.SetPixels32(copy.GetPixels32());
                    scaled.Apply(false, false);
                }
                else
                {
                    var pixels = AtlasBuilder.ResampleRegion(copy.GetPixels32(), copy.width, copy.height,
                        tw, th, tref.sRGB);
                    scaled = new Texture2D(tw, th, TextureFormat.RGBA32, false, linear: !tref.sRGB);
                    scaled.SetPixels32(pixels);
                    scaled.Apply(false, false);
                }
                scaled.name = $"{tref.source.name}_ATO_scaled";
                scaled.hideFlags = HideFlags.HideAndDontSave;

                tref.hasAlpha = ctx.cache.UsesAlpha(tref.source, tref.sRGB);
                if (tref.role == TextureRole.MainColor && tref.hasAlpha)
                {
                    tref.category = TextureCategory.Transparent;
                }

                result[tref.source] = scaled;
            }

            return result;
        }
    }
}
