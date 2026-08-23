// SPDX-License-Identifier: MIT
// EN: Chooses a safe compression format for every generated texture, with fallbacks that can never
//     change how a material renders.
// ZH: 为每张生成的贴图选择安全的压缩格式，并提供绝不会改变材质渲染结果的回退。

using System;
using Net.Fosa.AvatarTextureOptimizer.Editor.Core;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Atlas
{
    /// <summary>
    /// EN: Resolves user format choices into concrete <see cref="TextureFormat"/> values.
    /// ZH: 将用户的格式选择解析为具体的 <see cref="TextureFormat"/> 值。
    /// </summary>
    public static class TextureFormatResolver
    {
        private const string Stage = "Format";

        /// <summary>
        /// EN: The maximum atlas edge allowed on a platform. VRChat's limits for Quest and iOS are
        ///     stricter than the desktop limit.
        /// ZH: 某平台允许的最大图集边长。VRChat 对 Quest 与 iOS 的限制比桌面端更严格。
        /// </summary>
        public static int MaxEdge(AtoPlatform platform)
            => platform == AtoPlatform.PC ? 8192 : 4096;

        /// <summary>
        /// EN: Resolves the format for a colour atlas. When the atlas contains alpha, formats without an
        ///     alpha channel are never offered and never chosen, even if the user asked for one.
        /// ZH: 解析颜色图集的格式。若图集包含 alpha，则绝不提供也绝不选择无 alpha 通道的格式，
        ///     即使用户如此要求。
        /// </summary>
        public static TextureFormat ResolveColor(AtoPlatform platform, bool hasAlpha,
            AtoColorOpaqueFormat opaqueChoice, AtoColorAlphaFormat alphaChoice, bool npot)
        {
            if (hasAlpha)
            {
                var f = alphaChoice switch
                {
                    AtoColorAlphaFormat.BC7 => TextureFormat.BC7,
                    AtoColorAlphaFormat.DXT5 => TextureFormat.DXT5,
                    AtoColorAlphaFormat.DXT5Crunched => TextureFormat.DXT5Crunched,
                    AtoColorAlphaFormat.ASTC4x4 => TextureFormat.ASTC_4x4,
                    AtoColorAlphaFormat.ASTC5x5 => TextureFormat.ASTC_5x5,
                    AtoColorAlphaFormat.ASTC6x6 => TextureFormat.ASTC_6x6,
                    AtoColorAlphaFormat.ASTC8x8 => TextureFormat.ASTC_8x8,
                    AtoColorAlphaFormat.ETC2RGBA8 => TextureFormat.ETC2_RGBA8,
                    AtoColorAlphaFormat.Uncompressed => TextureFormat.RGBA32,
                    _ => DefaultColorAlpha(platform),
                };
                return Validate(f, platform, npot, true);
            }

            var g = opaqueChoice switch
            {
                AtoColorOpaqueFormat.BC7 => TextureFormat.BC7,
                AtoColorOpaqueFormat.DXT1 => TextureFormat.DXT1,
                AtoColorOpaqueFormat.DXT1Crunched => TextureFormat.DXT1Crunched,
                AtoColorOpaqueFormat.ASTC4x4 => TextureFormat.ASTC_4x4,
                AtoColorOpaqueFormat.ASTC5x5 => TextureFormat.ASTC_5x5,
                AtoColorOpaqueFormat.ASTC6x6 => TextureFormat.ASTC_6x6,
                AtoColorOpaqueFormat.ASTC8x8 => TextureFormat.ASTC_8x8,
                AtoColorOpaqueFormat.ETC2RGB4 => TextureFormat.ETC2_RGB,
                AtoColorOpaqueFormat.Uncompressed => TextureFormat.RGBA32,
                _ => DefaultColorOpaque(platform),
            };
            return Validate(g, platform, npot, false);
        }

        /// <summary>EN: Resolves the format for a normal atlas. ZH: 解析法线图集的格式。</summary>
        public static TextureFormat ResolveNormal(AtoPlatform platform, AtoNormalFormat choice, bool npot)
        {
            var f = choice switch
            {
                AtoNormalFormat.BC5 => TextureFormat.BC5,
                AtoNormalFormat.BC7 => TextureFormat.BC7,
                AtoNormalFormat.DXT5 => TextureFormat.DXT5,
                AtoNormalFormat.ASTC4x4 => TextureFormat.ASTC_4x4,
                AtoNormalFormat.ASTC5x5 => TextureFormat.ASTC_5x5,
                AtoNormalFormat.ASTC6x6 => TextureFormat.ASTC_6x6,
                AtoNormalFormat.Uncompressed => TextureFormat.RGBA32,
                _ => platform == AtoPlatform.PC ? TextureFormat.BC5 : TextureFormat.ASTC_6x6,
            };
            return Validate(f, platform, npot, true);
        }

        /// <summary>
        /// EN: Resolves the format for a grayscale/mask atlas. A single channel format is silently
        ///     upgraded when the mask actually carries several channels, and a warning is reported so the
        ///     user learns why their choice was not honoured.
        /// ZH: 解析灰度/蒙版图集的格式。当蒙版实际携带多个通道时，单通道格式会被静默升级，
        ///     并报告一条警告，让用户明白自己的选择为何未被采纳。
        /// </summary>
        public static TextureFormat ResolveGrayscale(AtoPlatform platform, AtoGrayscaleFormat choice,
            bool multiChannel, bool npot, out bool downgraded)
        {
            downgraded = false;
            if (choice == AtoGrayscaleFormat.BC4 && multiChannel)
            {
                downgraded = true;
                AtoLog.Warning(Stage, "BC4 requested for a multi channel mask atlas; using BC7 instead to preserve every channel.");
                choice = AtoGrayscaleFormat.BC7;
            }

            var f = choice switch
            {
                AtoGrayscaleFormat.BC4 => TextureFormat.BC4,
                AtoGrayscaleFormat.BC7 => TextureFormat.BC7,
                AtoGrayscaleFormat.DXT1 => TextureFormat.DXT1,
                AtoGrayscaleFormat.ASTC4x4 => TextureFormat.ASTC_4x4,
                AtoGrayscaleFormat.ASTC6x6 => TextureFormat.ASTC_6x6,
                AtoGrayscaleFormat.ASTC8x8 => TextureFormat.ASTC_8x8,
                AtoGrayscaleFormat.ETC2RGB4 => TextureFormat.ETC2_RGB,
                AtoGrayscaleFormat.Uncompressed => TextureFormat.RGBA32,
                _ => platform == AtoPlatform.PC
                    ? (multiChannel ? TextureFormat.BC7 : TextureFormat.BC4)
                    : TextureFormat.ASTC_6x6,
            };
            return Validate(f, platform, npot, multiChannel);
        }

        private static TextureFormat DefaultColorOpaque(AtoPlatform platform)
            => platform == AtoPlatform.PC ? TextureFormat.DXT1 : TextureFormat.ASTC_6x6;

        private static TextureFormat DefaultColorAlpha(AtoPlatform platform)
            => platform == AtoPlatform.PC ? TextureFormat.BC7 : TextureFormat.ASTC_6x6;

        /// <summary>
        /// EN: Final safety net. Formats that are not supported on the platform, or that would silently
        ///     drop alpha, are replaced by the closest safe alternative.
        /// ZH: 最后的安全网。平台不支持、或会静默丢弃 alpha 的格式，会被替换为最接近的安全替代品。
        /// </summary>
        private static TextureFormat Validate(TextureFormat format, AtoPlatform platform, bool npot, bool needsAlpha)
        {
            // EN: PVRTC requires square power of two textures and is unusable with the NPOT pool; it is
            //     never generated by ATO for that reason.
            // ZH: PVRTC 要求正方形二次幂贴图，无法与 NPOT 池共存；因此 ATO 从不生成该格式。
            if (platform == AtoPlatform.PC)
            {
                if (format.ToString().StartsWith("ASTC", StringComparison.Ordinal) ||
                    format.ToString().StartsWith("ETC", StringComparison.Ordinal))
                {
                    AtoLog.Warning(Stage, $"{format} is a mobile format; falling back to a desktop format.");
                    format = needsAlpha ? TextureFormat.BC7 : TextureFormat.DXT1;
                }
            }
            else
            {
                if (format.ToString().StartsWith("DXT", StringComparison.Ordinal) ||
                    format.ToString().StartsWith("BC", StringComparison.Ordinal))
                {
                    AtoLog.Warning(Stage, $"{format} is a desktop format; falling back to ASTC 6x6.");
                    format = TextureFormat.ASTC_6x6;
                }
            }

            if (needsAlpha && (format == TextureFormat.DXT1 || format == TextureFormat.DXT1Crunched || format == TextureFormat.ETC2_RGB))
            {
                AtoLog.Warning(Stage, $"{format} has no alpha channel but the atlas needs one; upgrading.");
                format = platform == AtoPlatform.PC ? TextureFormat.DXT5 : TextureFormat.ETC2_RGBA8;
            }

            if (!SystemInfo.SupportsTextureFormat(format))
            {
                AtoLog.Warning(Stage, $"{format} is not supported by the editor's graphics device; using RGBA32.");
                format = TextureFormat.RGBA32;
            }

            return format;
        }
    }
}
