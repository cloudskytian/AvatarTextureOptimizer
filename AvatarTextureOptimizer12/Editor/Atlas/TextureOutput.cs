// SPDX-License-Identifier: MIT
// AvatarTextureOptimizer (ATO) - Output format resolution, compression and streaming-mipmap policy.
// AvatarTextureOptimizer (ATO) - 输出格式解析、压缩与 MipStreaming 策略。

using System;
using System.Collections.Generic;
using Net.Fosa.AvatarTextureOptimizer.Editor.Analysis;
using Net.Fosa.AvatarTextureOptimizer.Editor.Core;
using UnityEditor;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Atlas
{
    /// <summary>
    /// EN: Resolves a user-selected compression format into something that is guaranteed safe for the
    ///     platform AND for the actual pixel content. Every unsafe combination is silently repaired and the
    ///     repair is reported to the NDMF console, so no option combination can ever break a material.
    /// ZH: 把用户选择的压缩格式解析为“对平台与实际像素内容都保证安全”的格式。
    ///     所有不安全的组合都会被静默修复并在 NDMF 控制台报出，
    ///     因此任何选项组合都不可能让材质出错。
    /// </summary>
    public static class TextureOutput
    {
        /// <summary>
        /// EN: Decide the final <see cref="TextureFormat"/>.
        /// ZH: 决定最终的 <see cref="TextureFormat"/>。
        /// </summary>
        public static TextureFormat Resolve(ATOCompressionFormat requested, ATOPlatform platform,
            ATOTextureClass cls, bool needsAlpha, int channelsUsed, string debugName)
        {
            var chosen = requested == ATOCompressionFormat.Auto
                ? AutoFor(platform, cls, needsAlpha)
                : requested;

            var format = Map(chosen);

            // ---- Platform validity / 平台有效性 ----
            if (!IsValidOnPlatform(format, platform))
            {
                var fallback = Map(AutoFor(platform, cls, needsAlpha));
                ATOReportUtil.Warn("ATO:warn:format_platform_fallback", debugName, format.ToString(),
                    fallback.ToString(), platform.ToString());
                format = fallback;
            }

            // ---- Alpha safety: never offer/keep an alpha-less format for a texture that needs alpha ----
            // ---- Alpha 安全性：需要 alpha 的贴图绝不会使用无 alpha 通道的格式 ----
            if (needsAlpha && !HasAlpha(format))
            {
                var fallback = Map(AutoFor(platform, cls, true));
                ATOReportUtil.Warn("ATO:warn:format_alpha_fallback", debugName, format.ToString(),
                    fallback.ToString());
                format = fallback;
            }

            // ---- Channel safety: a multi-channel "grayscale" texture must not be squashed to one channel ----
            // ---- 通道安全性：多通道的“灰度”贴图不能被压成单通道 ----
            int channelCount = CountBits(channelsUsed);
            if (channelCount > 1 && IsSingleChannel(format))
            {
                var fallback = channelCount == 2 ? TextureFormat.RG16 : Map(AutoFor(platform, cls, needsAlpha));
                ATOReportUtil.Warn("ATO:warn:format_channel_fallback", debugName, format.ToString(),
                    fallback.ToString(), channelCount);
                format = fallback;
            }

            return format;
        }

        private static ATOCompressionFormat AutoFor(ATOPlatform platform, ATOTextureClass cls, bool needsAlpha)
        {
            switch (platform)
            {
                case ATOPlatform.Android:
                case ATOPlatform.iOS:
                    // EN: ASTC is available on every VRChat-capable mobile GPU and handles alpha uniformly.
                    // ZH: ASTC 在所有能运行 VRChat 的移动 GPU 上都可用，并且统一支持 alpha。
                    return cls == ATOTextureClass.NormalMap
                        ? ATOCompressionFormat.ASTC_6x6
                        : ATOCompressionFormat.ASTC_6x6;
                default:
                    switch (cls)
                    {
                        case ATOTextureClass.NormalMap: return ATOCompressionFormat.BC5;
                        case ATOTextureClass.Grayscale: return ATOCompressionFormat.BC4;
                        default: return needsAlpha ? ATOCompressionFormat.BC7 : ATOCompressionFormat.DXT1;
                    }
            }
        }

        private static TextureFormat Map(ATOCompressionFormat f)
        {
            switch (f)
            {
                case ATOCompressionFormat.BC7: return TextureFormat.BC7;
                case ATOCompressionFormat.BC5: return TextureFormat.BC5;
                case ATOCompressionFormat.BC4: return TextureFormat.BC4;
                case ATOCompressionFormat.DXT1: return TextureFormat.DXT1;
                case ATOCompressionFormat.DXT5: return TextureFormat.DXT5;
                case ATOCompressionFormat.DXT1Crunched: return TextureFormat.DXT1Crunched;
                case ATOCompressionFormat.DXT5Crunched: return TextureFormat.DXT5Crunched;
                case ATOCompressionFormat.ASTC_4x4: return TextureFormat.ASTC_4x4;
                case ATOCompressionFormat.ASTC_5x5: return TextureFormat.ASTC_5x5;
                case ATOCompressionFormat.ASTC_6x6: return TextureFormat.ASTC_6x6;
                case ATOCompressionFormat.ASTC_8x8: return TextureFormat.ASTC_8x8;
                case ATOCompressionFormat.ASTC_10x10: return TextureFormat.ASTC_10x10;
                case ATOCompressionFormat.ASTC_12x12: return TextureFormat.ASTC_12x12;
                case ATOCompressionFormat.ETC2_RGB4: return TextureFormat.ETC2_RGB;
                case ATOCompressionFormat.ETC2_RGBA8: return TextureFormat.ETC2_RGBA8;
                case ATOCompressionFormat.ETC2_RGB4Crunched: return TextureFormat.ETC_RGB4Crunched;
                case ATOCompressionFormat.ETC2_RGBA8Crunched: return TextureFormat.ETC2_RGBA8Crunched;
                case ATOCompressionFormat.RGB24: return TextureFormat.RGB24;
                case ATOCompressionFormat.RG16: return TextureFormat.RG16;
                case ATOCompressionFormat.R8: return TextureFormat.R8;
                default: return TextureFormat.RGBA32;
            }
        }

        private static bool IsValidOnPlatform(TextureFormat f, ATOPlatform platform)
        {
            bool desktop = platform == ATOPlatform.PC;
            switch (f)
            {
                case TextureFormat.BC7:
                case TextureFormat.BC5:
                case TextureFormat.BC4:
                case TextureFormat.DXT1:
                case TextureFormat.DXT5:
                case TextureFormat.DXT1Crunched:
                case TextureFormat.DXT5Crunched:
                    return desktop;
                case TextureFormat.ASTC_4x4:
                case TextureFormat.ASTC_5x5:
                case TextureFormat.ASTC_6x6:
                case TextureFormat.ASTC_8x8:
                case TextureFormat.ASTC_10x10:
                case TextureFormat.ASTC_12x12:
                case TextureFormat.ETC2_RGB:
                case TextureFormat.ETC2_RGBA8:
                case TextureFormat.ETC_RGB4Crunched:
                case TextureFormat.ETC2_RGBA8Crunched:
                    return !desktop;
                default:
                    return true;
            }
        }

        private static bool HasAlpha(TextureFormat f)
        {
            switch (f)
            {
                case TextureFormat.DXT1:
                case TextureFormat.DXT1Crunched:
                case TextureFormat.ETC2_RGB:
                case TextureFormat.ETC_RGB4Crunched:
                case TextureFormat.RGB24:
                case TextureFormat.BC4:
                case TextureFormat.BC5:
                case TextureFormat.RG16:
                case TextureFormat.R8:
                    return false;
                default:
                    return true;
            }
        }

        private static bool IsSingleChannel(TextureFormat f) =>
            f == TextureFormat.BC4 || f == TextureFormat.R8 || f == TextureFormat.Alpha8;

        private static int CountBits(int v)
        {
            int c = 0;
            while (v != 0) { c += v & 1; v >>= 1; }
            return c;
        }

        /// <summary>
        /// EN: Crunch and NPOT: crunched formats require power-of-two dimensions, so we drop crunch when the
        ///     experimental NPOT option produced a non-power-of-two atlas.
        /// ZH: Crunch 与 NPOT：Crunch 格式要求 2 的幂尺寸，
        ///     因此当实验性 NPOT 选项产生了非 2 的幂图集时会自动去掉 Crunch。
        /// </summary>
        public static TextureFormat DropCrunchIfNpot(TextureFormat f, int w, int h, string debugName)
        {
            if (IsPow2(w) && IsPow2(h)) return f;
            switch (f)
            {
                case TextureFormat.DXT1Crunched:
                    ATOReportUtil.Warn("ATO:warn:crunch_npot", debugName);
                    return TextureFormat.DXT1;
                case TextureFormat.DXT5Crunched:
                    ATOReportUtil.Warn("ATO:warn:crunch_npot", debugName);
                    return TextureFormat.DXT5;
                case TextureFormat.ETC_RGB4Crunched:
                    ATOReportUtil.Warn("ATO:warn:crunch_npot", debugName);
                    return TextureFormat.ETC2_RGB;
                case TextureFormat.ETC2_RGBA8Crunched:
                    ATOReportUtil.Warn("ATO:warn:crunch_npot", debugName);
                    return TextureFormat.ETC2_RGBA8;
                default:
                    return f;
            }
        }

        private static bool IsPow2(int v) => v > 0 && (v & (v - 1)) == 0;

        /// <summary>
        /// EN: Compress a baked texture in place and apply the mipmap / streaming policy.
        ///     VRChat requires streaming mipmaps whenever mipmaps exist, so the two always move together.
        /// ZH: 就地压缩已烘焙的贴图并施加 mipmap / streaming 策略。
        ///     VRChat 要求存在 mipmap 时必须开启 streaming，因此二者始终绑定。
        /// </summary>
        public static Texture2D Finalise(Texture2D source, TextureFormat format, bool mipmaps, int quality,
            string debugName)
        {
            try
            {
                if (!SystemInfo.SupportsTextureFormat(format))
                {
                    ATOReportUtil.Warn("ATO:warn:format_unsupported", debugName, format.ToString());
                    return source;
                }

                EditorUtility.CompressTexture(source, format, Mathf.Clamp(quality, 0, 100));
                source.Apply(mipmaps, false);

                // EN: streamingMipmaps is a texture-level flag on generated textures.
                // ZH: 对生成的贴图而言 streamingMipmaps 是贴图级别的标志。
                if (mipmaps && source.mipmapCount > 1)
                {
                    source.streamingMipmaps = true;
                }
                else
                {
                    source.streamingMipmaps = false;
                }

                ATOLog.Debug_($"finalised '{debugName}' as {format} mips={mipmaps} " +
                              $"({source.width}x{source.height})");
            }
            catch (Exception e)
            {
                ATOReportUtil.Warn("ATO:warn:compress_failed", debugName, e.Message);
            }
            return source;
        }

        /// <summary>
        /// EN: Estimated VRAM footprint in bytes, used by the final report.
        /// ZH: 估算的显存占用（字节），供最终报告使用。
        /// </summary>
        public static long EstimateBytes(int width, int height, TextureFormat format, bool mipmaps)
        {
            double bpp;
            switch (format)
            {
                case TextureFormat.DXT1:
                case TextureFormat.DXT1Crunched:
                case TextureFormat.BC4:
                case TextureFormat.ETC2_RGB:
                case TextureFormat.ETC_RGB4Crunched:
                    bpp = 4; break;
                case TextureFormat.BC7:
                case TextureFormat.BC5:
                case TextureFormat.DXT5:
                case TextureFormat.DXT5Crunched:
                case TextureFormat.ETC2_RGBA8:
                case TextureFormat.ETC2_RGBA8Crunched:
                case TextureFormat.ASTC_4x4:
                    bpp = 8; break;
                case TextureFormat.ASTC_5x5: bpp = 5.12; break;
                case TextureFormat.ASTC_6x6: bpp = 3.56; break;
                case TextureFormat.ASTC_8x8: bpp = 2.0; break;
                case TextureFormat.ASTC_10x10: bpp = 1.28; break;
                case TextureFormat.ASTC_12x12: bpp = 0.89; break;
                case TextureFormat.R8: bpp = 8; break;
                case TextureFormat.RG16: bpp = 16; break;
                case TextureFormat.RGB24: bpp = 24; break;
                default: bpp = 32; break;
            }

            double bytes = width * (double)height * bpp / 8.0;
            if (mipmaps) bytes *= 4.0 / 3.0;
            return (long)bytes;
        }
    }
}
