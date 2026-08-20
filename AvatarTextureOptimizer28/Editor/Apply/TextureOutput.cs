using System;
using UnityEditor;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// EN: Resolves the final compression format for a generated texture and applies it.
    ///
    ///     Two safety rules are enforced here and cannot be overridden by the user, because violating
    ///     them silently corrupts materials:
    ///       * A texture with meaningful alpha is never written to a format without an alpha channel.
    ///       * A data texture that actually uses more than one channel is never written to a
    ///         single-channel format, even if the user selected BC4.
    ///     Both downgrades are reported to the NDMF console rather than applied silently.
    ///
    /// ZH: 为生成的贴图解析最终压缩格式并应用。
    ///
    ///     这里强制执行两条用户无法覆盖的安全规则，因为违反它们会静默破坏材质：
    ///       * 含有效 alpha 的贴图绝不会被写入没有 alpha 通道的格式。
    ///       * 实际使用了多于一个通道的数据贴图绝不会被写入单通道格式，即便用户选择了 BC4。
    ///     两种降级都会上报到 NDMF 控制台，而不是静默处理。
    /// </summary>
    public static class TextureOutput
    {
        /// <summary>
        /// EN: Compress <paramref name="tex"/> in place according to the profile and platform.
        /// ZH: 按配置与平台对 <paramref name="tex"/> 原地压缩。
        /// </summary>
        public static void Apply(Texture2D tex, TextureClass cls, bool hasAlpha, bool4Mask usedChannels,
            PlatformProfile profile, ATOPlatform platform, bool npot, ATOLog log)
        {
            // EN: Atlases are always clamped and never readable. These are not user options: a repeating
            //     atlas would sample a neighbouring island, and Read/Write doubles the memory cost.
            // ZH: 图集恒为 Clamp 且不可读。这不是用户选项：repeat 的图集会采样到相邻的岛，
            //     而 Read/Write 会让内存开销翻倍。
            tex.wrapMode = TextureWrapMode.Clamp;

            var format = Resolve(cls, hasAlpha, usedChannels, profile, platform, npot, log, tex.name);

            try
            {
                EditorUtility.CompressTexture(tex, format, profile.output.compressorQuality);
            }
            catch (Exception e)
            {
                log.Warn($"Compression of '{tex.name}' to {format} failed ({e.Message}); leaving it uncompressed.");
            }

            // EN: VRChat requires Streaming Mip Maps whenever mip maps exist, so the two are bound.
            // ZH: VRChat 要求只要存在 mipmap 就必须开启 Mip Streaming，因此两者绑定。
            if (tex.mipmapCount > 1 && profile.output.mipmapAndStreaming)
                tex.streamingMipmaps = true;
            else
                tex.streamingMipmaps = false;

            // EN: The CPU copy is intentionally kept here. Output deduplication compares the compressed
            //     bytes via GetRawTextureData, which throws once the texture is no longer readable, so
            //     the release happens in one final sweep after dedup (see ATOPass.ReleaseCpuCopies).
            // ZH: 这里刻意保留 CPU 副本。输出去重要通过 GetRawTextureData 比较压缩后的字节，
            //     一旦贴图不可读就会抛异常；因此释放动作放在去重之后统一进行
            //     （见 ATOPass.ReleaseCpuCopies）。
            tex.Apply(false, false);
        }

        /// <summary>EN: Pick the format, applying every safety downgrade. ZH: 选择格式并施加所有安全降级。</summary>
        public static TextureFormat Resolve(TextureClass cls, bool hasAlpha, bool4Mask usedChannels,
            PlatformProfile profile, ATOPlatform platform, bool npot, ATOLog log, string name)
        {
            bool mobile = platform != ATOPlatform.PC;

            switch (cls)
            {
                case TextureClass.Normal:
                {
                    var want = profile.output.normalFormat;
                    if (want == ATONormalFormat.Auto) want = mobile ? ATONormalFormat.ASTC6x6 : ATONormalFormat.BC5;
                    if (mobile && (want == ATONormalFormat.BC5 || want == ATONormalFormat.BC7 || want == ATONormalFormat.DXT5nm))
                    {
                        log.Warn(ATOLocalizer.Tr("ato.warn.formatDowngraded", name, "ASTC 6x6"));
                        want = ATONormalFormat.ASTC6x6;
                    }
                    if (!mobile && want >= ATONormalFormat.ASTC4x4 && want <= ATONormalFormat.ASTC8x8)
                    {
                        log.Warn(ATOLocalizer.Tr("ato.warn.formatDowngraded", name, "BC5"));
                        want = ATONormalFormat.BC5;
                    }
                    switch (want)
                    {
                        case ATONormalFormat.BC5: return TextureFormat.BC5;
                        case ATONormalFormat.BC7: return TextureFormat.BC7;
                        case ATONormalFormat.DXT5nm: return TextureFormat.DXT5;
                        case ATONormalFormat.ASTC4x4: return TextureFormat.ASTC_4x4;
                        case ATONormalFormat.ASTC5x5: return TextureFormat.ASTC_5x5;
                        case ATONormalFormat.ASTC6x6: return TextureFormat.ASTC_6x6;
                        case ATONormalFormat.ASTC8x8: return TextureFormat.ASTC_8x8;
                        default: return TextureFormat.RGBA32;
                    }
                }

                case TextureClass.Grayscale:
                {
                    var want = profile.output.grayscaleFormat;
                    if (want == ATOGrayscaleFormat.Auto)
                        want = mobile ? ATOGrayscaleFormat.ASTC6x6
                                      : (usedChannels.Count > 1 ? ATOGrayscaleFormat.BC7 : ATOGrayscaleFormat.BC4);

                    // EN: Hard safety rule - never collapse a multi-channel data texture into one channel.
                    // ZH: 硬性安全规则——绝不把多通道数据贴图塌缩成单通道。
                    if (usedChannels.Count > 1 && (want == ATOGrayscaleFormat.BC4 || want == ATOGrayscaleFormat.R8))
                    {
                        log.Warn(ATOLocalizer.Tr("ato.warn.formatDowngraded", name,
                            mobile ? "ASTC 6x6" : "BC7"));
                        want = mobile ? ATOGrayscaleFormat.ASTC6x6 : ATOGrayscaleFormat.BC7;
                    }
                    if (usedChannels.A && want == ATOGrayscaleFormat.DXT1)
                    {
                        log.Warn(ATOLocalizer.Tr("ato.warn.formatDowngraded", name, "DXT5"));
                        want = ATOGrayscaleFormat.DXT5;
                    }
                    if (mobile && want <= ATOGrayscaleFormat.DXT5 && want != ATOGrayscaleFormat.Auto)
                    {
                        log.Warn(ATOLocalizer.Tr("ato.warn.formatDowngraded", name, "ASTC 6x6"));
                        want = ATOGrayscaleFormat.ASTC6x6;
                    }
                    switch (want)
                    {
                        case ATOGrayscaleFormat.BC4: return TextureFormat.BC4;
                        case ATOGrayscaleFormat.BC7: return TextureFormat.BC7;
                        case ATOGrayscaleFormat.DXT1: return TextureFormat.DXT1;
                        case ATOGrayscaleFormat.DXT5: return TextureFormat.DXT5;
                        case ATOGrayscaleFormat.ASTC4x4: return TextureFormat.ASTC_4x4;
                        case ATOGrayscaleFormat.ASTC6x6: return TextureFormat.ASTC_6x6;
                        case ATOGrayscaleFormat.ASTC8x8: return TextureFormat.ASTC_8x8;
                        case ATOGrayscaleFormat.ETC2RGB: return TextureFormat.ETC2_RGB;
                        case ATOGrayscaleFormat.R8: return TextureFormat.R8;
                        default: return TextureFormat.RGBA32;
                    }
                }

                default:
                {
                    var want = hasAlpha ? profile.output.transparentColorFormat : profile.output.opaqueColorFormat;
                    if (want == ATOColorFormat.Auto)
                        want = mobile ? ATOColorFormat.ASTC6x6
                                      : (hasAlpha ? ATOColorFormat.DXT5Crunched : ATOColorFormat.DXT1Crunched);

                    // EN: Hard safety rule - alpha must survive.
                    // ZH: 硬性安全规则——alpha 必须保留。
                    if (hasAlpha && (want == ATOColorFormat.DXT1 || want == ATOColorFormat.DXT1Crunched ||
                                     want == ATOColorFormat.ETC2RGB))
                    {
                        var to = want == ATOColorFormat.DXT1Crunched ? ATOColorFormat.DXT5Crunched
                               : want == ATOColorFormat.DXT1 ? ATOColorFormat.DXT5
                               : ATOColorFormat.ETC2RGBA8;
                        log.Warn(ATOLocalizer.Tr("ato.warn.formatDowngraded", name, to.ToString()));
                        want = to;
                    }

                    // EN: Crunch requires a power-of-two-friendly block layout in some Unity versions and
                    //     cannot be combined with the experimental NPOT sizes on mobile.
                    // ZH: 某些 Unity 版本下 Crunch 需要对 2 次幂友好的块布局，
                    //     在移动端也无法与实验性 NPOT 尺寸组合。
                    if (npot && mobile && (want == ATOColorFormat.DXT1Crunched || want == ATOColorFormat.DXT5Crunched))
                    {
                        log.Warn(ATOLocalizer.Tr("ato.warn.npotFormatRemoved", want.ToString(), platform.ToString()));
                        want = ATOColorFormat.ASTC6x6;
                    }

                    if (mobile && want < ATOColorFormat.ASTC4x4)
                    {
                        log.Warn(ATOLocalizer.Tr("ato.warn.formatDowngraded", name, "ASTC 6x6"));
                        want = ATOColorFormat.ASTC6x6;
                    }
                    if (!mobile && want >= ATOColorFormat.ASTC4x4 && want <= ATOColorFormat.ETC2RGBA8)
                    {
                        var to = hasAlpha ? ATOColorFormat.DXT5Crunched : ATOColorFormat.DXT1Crunched;
                        log.Warn(ATOLocalizer.Tr("ato.warn.formatDowngraded", name, to.ToString()));
                        want = to;
                    }

                    switch (want)
                    {
                        case ATOColorFormat.DXT1: return TextureFormat.DXT1;
                        case ATOColorFormat.DXT5: return TextureFormat.DXT5;
                        case ATOColorFormat.BC7: return TextureFormat.BC7;
                        case ATOColorFormat.DXT1Crunched: return TextureFormat.DXT1Crunched;
                        case ATOColorFormat.DXT5Crunched: return TextureFormat.DXT5Crunched;
                        case ATOColorFormat.ASTC4x4: return TextureFormat.ASTC_4x4;
                        case ATOColorFormat.ASTC5x5: return TextureFormat.ASTC_5x5;
                        case ATOColorFormat.ASTC6x6: return TextureFormat.ASTC_6x6;
                        case ATOColorFormat.ASTC8x8: return TextureFormat.ASTC_8x8;
                        case ATOColorFormat.ASTC10x10: return TextureFormat.ASTC_10x10;
                        case ATOColorFormat.ASTC12x12: return TextureFormat.ASTC_12x12;
                        case ATOColorFormat.ETC2RGB: return TextureFormat.ETC2_RGB;
                        case ATOColorFormat.ETC2RGBA8: return TextureFormat.ETC2_RGBA8;
                        default: return TextureFormat.RGBA32;
                    }
                }
            }
        }

        /// <summary>EN: Approximate on-GPU byte size of a texture including mips. ZH: 贴图（含 mip）在 GPU 上的近似字节大小。</summary>
        public static long EstimateBytes(Texture2D t)
        {
            if (t == null) return 0;
            double bpp;
            switch (t.format)
            {
                case TextureFormat.DXT1: case TextureFormat.DXT1Crunched:
                case TextureFormat.BC4: case TextureFormat.ETC2_RGB: bpp = 4; break;
                case TextureFormat.DXT5: case TextureFormat.DXT5Crunched:
                case TextureFormat.BC5: case TextureFormat.BC7:
                case TextureFormat.ETC2_RGBA8: case TextureFormat.ASTC_4x4: bpp = 8; break;
                case TextureFormat.ASTC_5x5: bpp = 5.12; break;
                case TextureFormat.ASTC_6x6: bpp = 3.56; break;
                case TextureFormat.ASTC_8x8: bpp = 2.0; break;
                case TextureFormat.ASTC_10x10: bpp = 1.28; break;
                case TextureFormat.ASTC_12x12: bpp = 0.89; break;
                case TextureFormat.R8: bpp = 8; break;
                default: bpp = 32; break;
            }
            double baseBytes = (double)t.width * t.height * bpp / 8.0;
            double total = 0, factor = 1;
            for (int i = 0; i < Mathf.Max(1, t.mipmapCount); i++) { total += baseBytes * factor; factor *= 0.25; }
            return (long)total;
        }
    }
}
