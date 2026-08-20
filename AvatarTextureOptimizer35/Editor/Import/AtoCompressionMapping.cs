using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// Compression format mapping with safety fallbacks. / 压缩格式映射与安全回退。
    /// The safe enumeration is filtered per category & platform at build time; unsafe user
    /// combinations fall back (e.g. a transparent atlas never gets an alpha-less format; a
    /// multi-channel grayscale texture never gets a single-channel format — with a warning). /
    /// 安全枚举在构建时按分类与平台过滤；不安全的用户组合走回退（如透明图集绝不使用无 alpha 格式；
    /// 多通道灰度贴图绝不使用单通道格式——并告警）。
    /// </summary>
    internal static class AtoCompressionMapping
    {
        /// <summary>
        /// Resolve the TextureImporterFormat for a platform & category, with safety checks. /
        /// 解析某平台与分类的 TextureImporterFormat，含安全校验。
        /// </summary>
        public static TextureImporterFormat Resolve(AtoContext ctx, AtoCompressionFormat requested,
            AtoTextureCategory category, AtoTargetPlatform platform, bool npot, out string warning)
        {
            warning = null;

            // PVRTC cannot do NPOT. / PVRTC 不支持 NPOT。
            if (npot && requested is >= AtoCompressionFormat.PVRTC_RGB2 and <= AtoCompressionFormat.PVRTC_RGBA4)
            {
                warning = ctx.State.Tr("warn.npotFormatExcluded", requested.ToString(), platform.ToString());
                return DefaultFor(category, platform);
            }

            // Transparent content requires an alpha format. / 透明内容必须有 alpha 格式。
            if (category == AtoTextureCategory.Transparent && !HasAlpha(requested))
            {
                warning = $"transparent texture cannot use {requested} (no alpha channel); using platform default.";
                return DefaultFor(category, platform);
            }

            // Normal maps need at least two channels. / 法线贴图至少需要两通道。
            if (category == AtoTextureCategory.NormalMap && (requested == AtoCompressionFormat.R8 ||
                                                             requested == AtoCompressionFormat.BC4))
            {
                warning = $"normal map cannot use {requested}; using platform default.";
                return DefaultFor(category, platform);
            }

            return ToImporterFormat(requested, category, platform);
        }

        /// <summary>Platform default per category (safe optimal). / 每分类的平台默认（安全最优）。</summary>
        public static TextureImporterFormat DefaultFor(AtoTextureCategory category, AtoTargetPlatform platform)
        {
            if (platform == AtoTargetPlatform.PC)
            {
                return category switch
                {
                    AtoTextureCategory.NormalMap => TextureImporterFormat.BC5,
                    AtoTextureCategory.Grayscale => TextureImporterFormat.BC7,
                    _ => TextureImporterFormat.BC7,
                };
            }
            return category switch
            {
                AtoTextureCategory.NormalMap => TextureImporterFormat.ASTC_5x5,
                _ => TextureImporterFormat.ASTC_6x6,
            };
        }

        private static bool HasAlpha(AtoCompressionFormat format) => format switch
        {
            AtoCompressionFormat.DXT1 => false, // BC1
            AtoCompressionFormat.ETC_RGB4 => false,
            AtoCompressionFormat.ETC2_RGB4 => false,
            AtoCompressionFormat.PVRTC_RGB2 => false,
            AtoCompressionFormat.PVRTC_RGB4 => false,
            AtoCompressionFormat.R8 => false,
            AtoCompressionFormat.BC4 => false,
            AtoCompressionFormat.RGB24 => false,
            AtoCompressionFormat.RG16 => false,
            _ => true,
        };

        private static TextureImporterFormat ToImporterFormat(AtoCompressionFormat format,
            AtoTextureCategory category, AtoTargetPlatform platform)
        {
            switch (format)
            {
                case AtoCompressionFormat.Auto: return DefaultFor(category, platform);
                case AtoCompressionFormat.ASTC_4x4: return TextureImporterFormat.ASTC_4x4;
                case AtoCompressionFormat.ASTC_5x5: return TextureImporterFormat.ASTC_5x5;
                case AtoCompressionFormat.ASTC_6x6: return TextureImporterFormat.ASTC_6x6;
                case AtoCompressionFormat.ASTC_8x8: return TextureImporterFormat.ASTC_8x8;
                case AtoCompressionFormat.ASTC_10x10: return TextureImporterFormat.ASTC_10x10;
                case AtoCompressionFormat.ASTC_12x12: return TextureImporterFormat.ASTC_12x12;
                case AtoCompressionFormat.BC1: return TextureImporterFormat.DXT1;
                case AtoCompressionFormat.BC3: return TextureImporterFormat.DXT5;
                case AtoCompressionFormat.BC4: return TextureImporterFormat.BC4;
                case AtoCompressionFormat.BC5: return TextureImporterFormat.BC5;
                case AtoCompressionFormat.BC7: return TextureImporterFormat.BC7;
                case AtoCompressionFormat.ETC_RGB4: return TextureImporterFormat.ETC_RGB4;
                case AtoCompressionFormat.ETC2_RGB4: return TextureImporterFormat.ETC2_RGB4;
                case AtoCompressionFormat.ETC2_RGBA8: return TextureImporterFormat.ETC2_RGBA8;
                case AtoCompressionFormat.PVRTC_RGB2: return TextureImporterFormat.PVRTC_RGB2;
                case AtoCompressionFormat.PVRTC_RGB4: return TextureImporterFormat.PVRTC_RGB4;
                case AtoCompressionFormat.PVRTC_RGBA2: return TextureImporterFormat.PVRTC_RGBA2;
                case AtoCompressionFormat.PVRTC_RGBA4: return TextureImporterFormat.PVRTC_RGBA4;
                case AtoCompressionFormat.RGB24: return TextureImporterFormat.RGB24;
                case AtoCompressionFormat.RGBA32: return TextureImporterFormat.RGBA32;
                case AtoCompressionFormat.R8: return TextureImporterFormat.R8;
                case AtoCompressionFormat.RG16: return TextureImporterFormat.RG16;
                case AtoCompressionFormat.RGBAHalf: return TextureImporterFormat.RGBAHalf;
                case AtoCompressionFormat.RGBAFloat: return TextureImporterFormat.RGBAFloat;
                default: return DefaultFor(category, platform);
            }
        }

        /// <summary>
        /// Whether the texture content is truly single-channel (G==R && B==R everywhere), sampled
        /// on a grid of the cached raw pixels. / 贴图内容是否真为单通道（G==R 且 B==R），在缓存原始像素的
        /// 网格上采样判定。
        /// </summary>
        public static bool IsSingleChannelContent(AtoContext ctx, Texture2D texture)
        {
            if (!ctx.PixelCache.TryGet(texture, out var pixels))
            {
                pixels = ctx.PixelCache.Get(texture);
            }
            if (pixels == null || pixels.Length == 0) return false;
            var step = Mathf.Max(1, pixels.Length / 4096);
            for (var i = 0; i < pixels.Length; i += step)
            {
                var p = pixels[i];
                if (p.g != p.r || p.b != p.r) return false;
            }
            return true;
        }
    }
}
