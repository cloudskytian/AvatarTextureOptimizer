// Copyright (c) fosa. Licensed under the MIT License.
// Applies compression, mipmap/streaming and platform-specific settings to generated atlases.
// 为生成的图集应用压缩、mipmap/流式加载与平台相关设置。

using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Finalises a generated atlas texture into its shipping form.
    /// 将生成的图集贴图定型为最终交付形态。
    /// </summary>
    public static class TextureOutput
    {
        /// <summary>Prefix required on all generated assets. / 所有生成资产必须使用的前缀。</summary>
        public const string NamePrefix = "ATO_";

        /// <summary>
        /// Chooses the best compression format for a category on a platform. Normal maps and
        /// single-channel data get dedicated formats because generic colour formats waste bits
        /// and introduce visible artefacts.
        /// 为某平台上的某个分类选择最佳压缩格式。
        /// 法线与单通道数据使用专用格式，因为通用颜色格式既浪费位宽又会引入可见瑕疵。
        /// </summary>
        public static TextureFormat ResolveFormat(
            ATOCompressionFormat requested,
            TextureCategory category,
            ATOPlatform platform,
            bool hasAlpha)
        {
            if (requested != ATOCompressionFormat.Auto)
            {
                return MapExplicit(requested);
            }

            switch (platform)
            {
                case ATOPlatform.Android:
                case ATOPlatform.iOS:
                    // Mobile: ASTC across the board. ASTC handles normals and alpha well and is
                    // supported on every VRChat-capable mobile device.
                    // 移动端：全面使用 ASTC。ASTC 对法线与 alpha 支持良好，
                    // 且所有可运行 VRChat 的移动设备都支持。
                    switch (category)
                    {
                        case TextureCategory.NormalMap:
                            return TextureFormat.ASTC_6x6;
                        case TextureCategory.Grayscale:
                            return TextureFormat.ASTC_8x8;
                        default:
                            return TextureFormat.ASTC_6x6;
                    }

                default:
                    switch (category)
                    {
                        case TextureCategory.NormalMap:
                            // BC5 stores two channels at high precision and reconstructs Z.
                            // BC5 以高精度存储两个通道并重建 Z。
                            return TextureFormat.BC5;
                        case TextureCategory.Grayscale:
                            // BC4 is single channel; never waste a 4-channel format on it.
                            // BC4 为单通道；绝不为其浪费 4 通道格式。
                            return TextureFormat.BC4;
                        default:
                            return TextureFormat.BC7;
                    }
            }
        }

        private static TextureFormat MapExplicit(ATOCompressionFormat f)
        {
            switch (f)
            {
                case ATOCompressionFormat.Uncompressed: return TextureFormat.RGBA32;
                case ATOCompressionFormat.BC7: return TextureFormat.BC7;
                case ATOCompressionFormat.DXT1: return TextureFormat.DXT1;
                case ATOCompressionFormat.DXT5: return TextureFormat.DXT5;
                case ATOCompressionFormat.BC5: return TextureFormat.BC5;
                case ATOCompressionFormat.BC4: return TextureFormat.BC4;
                case ATOCompressionFormat.DXT1Crunched: return TextureFormat.DXT1Crunched;
                case ATOCompressionFormat.DXT5Crunched: return TextureFormat.DXT5Crunched;
                case ATOCompressionFormat.ASTC_4x4: return TextureFormat.ASTC_4x4;
                case ATOCompressionFormat.ASTC_6x6: return TextureFormat.ASTC_6x6;
                case ATOCompressionFormat.ASTC_8x8: return TextureFormat.ASTC_8x8;
                case ATOCompressionFormat.ETC2_RGBA8: return TextureFormat.ETC2_RGBA8;
                default: return TextureFormat.BC7;
            }
        }

        /// <summary>
        /// Applies the category settings to a finished atlas: naming, wrap mode, mipmaps,
        /// streaming and compression.
        /// 为已完成的图集应用分类设置：命名、包裹模式、mipmap、流式加载与压缩。
        /// </summary>
        public static void Finalise(
            Texture2D atlas,
            CategoryOutputSettings settings,
            TextureCategory category,
            ATOPlatform platform,
            bool hasAlpha,
            ATOLogger log)
        {
            if (atlas == null) return;

            if (!atlas.name.StartsWith(NamePrefix, StringComparison.Ordinal))
            {
                atlas.name = NamePrefix + atlas.name;
            }

            // Atlases must clamp: wrapping would sample a neighbouring island.
            // 图集必须使用 Clamp：Repeat 会采样到相邻的岛。
            atlas.wrapMode = TextureWrapMode.Clamp;

            var format = ResolveFormat(settings.format, category, platform, hasAlpha);

            try
            {
                if (format != TextureFormat.RGBA32)
                {
                    EditorUtility.CompressTexture(atlas, format, settings.compressionQuality);
                }
            }
            catch (Exception e)
            {
                log?.Warning(
                    $"Compression to {format} failed for {atlas.name}: {e.Message}. " +
                    "Leaving uncompressed.");
            }

            // Streaming mip maps only make sense when mip maps exist at all, so the two settings
            // are bound together exactly as specified.
            // 只有存在 mipmap 时流式 mipmap 才有意义，因此按需求将两个设置绑定在一起。
            atlas.SetStreamingMipMapSettings(settings.mipmapAndStreaming && atlas.mipmapCount > 1);

            atlas.Apply(false, false);
        }

        /// <summary>
        /// Returns true when a format cannot be produced for the given platform, so the caller
        /// can warn instead of silently shipping a broken texture.
        /// 当某格式无法在指定平台上生成时返回 true，
        /// 使调用方可以发出警告，而不是静默交付损坏的贴图。
        /// </summary>
        public static bool IsFormatSupported(TextureFormat format, ATOPlatform platform)
        {
            switch (platform)
            {
                case ATOPlatform.Android:
                case ATOPlatform.iOS:
                    switch (format)
                    {
                        case TextureFormat.BC7:
                        case TextureFormat.BC5:
                        case TextureFormat.BC4:
                        case TextureFormat.DXT1:
                        case TextureFormat.DXT5:
                        case TextureFormat.DXT1Crunched:
                        case TextureFormat.DXT5Crunched:
                            return false;
                        default:
                            return true;
                    }

                default:
                    switch (format)
                    {
                        case TextureFormat.ASTC_4x4:
                        case TextureFormat.ASTC_6x6:
                        case TextureFormat.ASTC_8x8:
                        case TextureFormat.ETC2_RGBA8:
                            return false;
                        default:
                            return true;
                    }
            }
        }
    }
}
