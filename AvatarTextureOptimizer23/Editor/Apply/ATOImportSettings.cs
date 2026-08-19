using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using FOSA.AvatarTextureOptimizer;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Applies safe importer settings to generated atlases / scaled textures.
    /// Invalid user choices are replaced and warned.
    /// 给生成的图集/缩放贴图写安全导入设置。非法用户选项会被替换并 warning。
    /// </summary>
    internal static class ATOImportSettings
    {
        public static void Run(ATOContext ctx)
        {
            var seen = new HashSet<Texture2D>();
            foreach (var kv in ctx.TextureRemap)
            {
                var tex = kv.Value;
                if (tex == null || !seen.Add(tex)) continue;
                var cat = Guess(ctx, kv.Key, tex);
                Apply(ctx, tex, cat);
            }
        }

        private static ATOTextureCategory Guess(ATOContext ctx, Texture2D src, Texture2D dst)
        {
            foreach (var tg in ctx.TypeGroups)
            foreach (var a in tg.Atlases)
                if (a.Atlas == dst) return a.Category;
            foreach (var use in ctx.Uses)
                if (use.Slot.texture == src) return use.Slot.category;
            return ATOTextureCategory.OpaqueAlbedo;
        }

        private static void Apply(ATOContext ctx, Texture2D tex, ATOTextureCategory cat)
        {
            var path = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(path)) return;
            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp == null) return;

            var choice = ctx.Settings.FormatFor(cat);
            choice = Sanitize(ctx, tex, cat, choice, imp);

            imp.wrapMode = TextureWrapMode.Clamp;
            imp.isReadable = false;
            var mip = ctx.Settings.MipStreamingFor(cat);
            imp.mipmapEnabled = mip;
            imp.streamingMipmaps = mip;
            if (cat == ATOTextureCategory.Normal) imp.textureType = TextureImporterType.NormalMap;
            imp.sRGBTexture = cat != ATOTextureCategory.Normal && cat != ATOTextureCategory.Gray;

            var plat = ATOPlatformUtil.UnityPlatformName(ctx.Settings.platform);
            var ps = imp.GetPlatformTextureSettings(plat);
            ps.overridden = true;
            ps.maxTextureSize = ctx.Settings.MaxAtlasEdge;
            ps.format = ToImporterFormat(choice, cat, ctx.Settings.platform);
            if (ctx.Settings.experimentalNpot && IsPvrtc(ps.format))
            {
                ctx.Log.Warn($"NPOT: dropped PVRTC on {tex.name}");
                ATOLoc.Report(nadena.dev.ndmf.ErrorSeverity.NonFatal, "ato.warn.npot_format", tex.name);
                ps.format = cat == ATOTextureCategory.TransparentAlbedo
                    ? TextureImporterFormat.ASTC_6x6
                    : TextureImporterFormat.ASTC_6x6;
            }
            imp.SetPlatformTextureSettings(ps);
            imp.SaveAndReimport();
        }

        private static ATOCompressionChoice Sanitize(
            ATOContext ctx, Texture2D tex, ATOTextureCategory cat,
            ATOCompressionChoice choice, TextureImporter imp)
        {
            if (cat == ATOTextureCategory.TransparentAlbedo && IsOpaqueFormat(choice))
            {
                ctx.Log.Warn($"Transparent '{tex.name}' cannot use opaque format {choice}, fallback Auto.");
                ATOLoc.Report(nadena.dev.ndmf.ErrorSeverity.NonFatal, "ato.warn.alpha_format", tex.name);
                return ATOCompressionChoice.Auto;
            }
            if (cat == ATOTextureCategory.Gray && IsSingleChannel(choice))
            {
                // Peek pixels: if more than one channel is used, refuse single-channel.
                // 看像素：若实际用了多通道，拒绝单通道格式。
                var dec = ATOTextureUtil.Decode(ctx, tex);
                if (UsesMultipleChannels(dec.Pixels))
                {
                    ctx.Log.Warn($"Gray '{tex.name}' uses multiple channels, keep multi-channel format.");
                    ATOLoc.Report(nadena.dev.ndmf.ErrorSeverity.NonFatal, "ato.warn.gray_multichannel", tex.name);
                    return ATOCompressionChoice.Auto;
                }
            }
            if (ctx.Settings.platform == ATOPlatform.iOS && ctx.Settings.experimentalNpot &&
                (choice == ATOCompressionChoice.PVRTC_RGB4 || choice == ATOCompressionChoice.PVRTC_RGBA4))
            {
                return ATOCompressionChoice.ASTC_6x6;
            }
            return choice;
        }

        private static bool UsesMultipleChannels(Color[] px)
        {
            if (px == null || px.Length == 0) return false;
            bool r = false, g = false, b = false;
            var step = System.Math.Max(1, px.Length / 2048);
            for (int i = 0; i < px.Length; i += step)
            {
                if (px[i].r > 0.01f && px[i].r < 0.99f) r = true;
                if (px[i].g > 0.01f && px[i].g < 0.99f) g = true;
                if (px[i].b > 0.01f && px[i].b < 0.99f) b = true;
            }
            var n = (r ? 1 : 0) + (g ? 1 : 0) + (b ? 1 : 0);
            return n > 1;
        }

        private static bool IsOpaqueFormat(ATOCompressionChoice c)
        {
            return c == ATOCompressionChoice.DXT1_BC1 || c == ATOCompressionChoice.ETC2_RGB ||
                   c == ATOCompressionChoice.PVRTC_RGB4;
        }

        private static bool IsSingleChannel(ATOCompressionChoice c)
        {
            return c == ATOCompressionChoice.BC4 || c == ATOCompressionChoice.R8 || c == ATOCompressionChoice.Alpha8;
        }

        private static bool IsPvrtc(TextureImporterFormat f)
        {
            return f == TextureImporterFormat.PVRTC_RGB2 || f == TextureImporterFormat.PVRTC_RGB4 ||
                   f == TextureImporterFormat.PVRTC_RGBA2 || f == TextureImporterFormat.PVRTC_RGBA4;
        }

        private static TextureImporterFormat ToImporterFormat(ATOCompressionChoice c, ATOTextureCategory cat, ATOPlatform plat)
        {
            if (c == ATOCompressionChoice.Auto)
            {
                switch (plat)
                {
                    case ATOPlatform.Android:
                    case ATOPlatform.iOS:
                        return TextureImporterFormat.ASTC_6x6;
                    default:
                        if (cat == ATOTextureCategory.Normal) return TextureImporterFormat.BC5;
                        if (cat == ATOTextureCategory.Gray) return TextureImporterFormat.BC4;
                        return TextureImporterFormat.BC7;
                }
            }
            switch (c)
            {
                case ATOCompressionChoice.Uncompressed:
                    return cat == ATOTextureCategory.TransparentAlbedo
                        ? TextureImporterFormat.RGBA32
                        : TextureImporterFormat.RGB24;
                case ATOCompressionChoice.DXT1_BC1: return TextureImporterFormat.DXT1;
                case ATOCompressionChoice.DXT5_BC3: return TextureImporterFormat.DXT5;
                case ATOCompressionChoice.BC4: return TextureImporterFormat.BC4;
                case ATOCompressionChoice.BC5: return TextureImporterFormat.BC5;
                case ATOCompressionChoice.BC7: return TextureImporterFormat.BC7;
                case ATOCompressionChoice.ETC2_RGB: return TextureImporterFormat.ETC2_RGB4;
                case ATOCompressionChoice.ETC2_RGBA: return TextureImporterFormat.ETC2_RGBA8;
                case ATOCompressionChoice.ASTC_4x4: return TextureImporterFormat.ASTC_4x4;
                case ATOCompressionChoice.ASTC_6x6: return TextureImporterFormat.ASTC_6x6;
                case ATOCompressionChoice.ASTC_8x8: return TextureImporterFormat.ASTC_8x8;
                case ATOCompressionChoice.PVRTC_RGB4: return TextureImporterFormat.PVRTC_RGB4;
                case ATOCompressionChoice.PVRTC_RGBA4: return TextureImporterFormat.PVRTC_RGBA4;
                case ATOCompressionChoice.R8: return TextureImporterFormat.R8;
                case ATOCompressionChoice.Alpha8: return TextureImporterFormat.Alpha8;
                default: return TextureImporterFormat.Automatic;
            }
        }
    }
}
