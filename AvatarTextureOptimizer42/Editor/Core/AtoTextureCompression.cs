using System;
using Net.Fosa.AvatarTextureOptimizer;
using UnityEditor;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Applies conservative platform-aware compression to generated textures.
    /// 对生成贴图应用保守的平台感知压缩。
    /// </summary>
    internal static class AtoTextureCompression
    {
        public static void Apply(Texture2D texture, AtoTextureSemantic semantic, AvatarTextureOptimizer component)
        {
            if (texture == null || component == null)
            {
                return;
            }

            var activeProfile = AtoAtlasPlanning.ResolveActiveProfile(component);
            var settings = activeProfile.TextureSettings;
            var hasAlpha = HasSignificantAlpha(texture);
            var policy = semantic switch
            {
                AtoTextureSemantic.Normal => settings.NormalPolicy,
                AtoTextureSemantic.Grayscale => settings.GrayscalePolicy,
                AtoTextureSemantic.Mask => settings.TransparentPolicy,
                _ => hasAlpha ? settings.TransparentPolicy : settings.OpaquePolicy,
            };

            var targetFormat = ResolveFormat(policy, semantic, hasAlpha, activeProfile.Platform);
            if (!targetFormat.HasValue)
            {
                return;
            }

            try
            {
                var quality = policy == AvatarTextureOptimizerTextureFormatPolicy.Quality
                    ? TextureCompressionQuality.Best
                    : policy == AvatarTextureOptimizerTextureFormatPolicy.Compact
                        ? TextureCompressionQuality.Fast
                        : TextureCompressionQuality.Normal;
                EditorUtility.CompressTexture(texture, targetFormat.Value, quality);
            }
            catch (Exception ex)
            {
                AtoLog.Warn($"Texture compression failed for {texture.name} -> {targetFormat.Value}: {ex.Message}");
            }
        }

        private static TextureFormat? ResolveFormat(AvatarTextureOptimizerTextureFormatPolicy policy, AtoTextureSemantic semantic, bool hasAlpha, AvatarTextureOptimizerTargetPlatform platform)
        {
            if (policy == AvatarTextureOptimizerTextureFormatPolicy.Uncompressed)
            {
                return null;
            }

            return platform switch
            {
                AvatarTextureOptimizerTargetPlatform.Android => ResolveMobile(policy, semantic, hasAlpha),
                AvatarTextureOptimizerTargetPlatform.IOS => ResolveMobile(policy, semantic, hasAlpha),
                _ => ResolvePc(policy, semantic, hasAlpha),
            };
        }

        private static TextureFormat ResolvePc(AvatarTextureOptimizerTextureFormatPolicy policy, AtoTextureSemantic semantic, bool hasAlpha)
        {
            return semantic switch
            {
                AtoTextureSemantic.Normal => policy switch
                {
                    AvatarTextureOptimizerTextureFormatPolicy.Quality => TextureFormat.BC7,
                    AvatarTextureOptimizerTextureFormatPolicy.Compact => TextureFormat.DXT5,
                    _ => TextureFormat.BC5,
                },
                AtoTextureSemantic.Grayscale => policy switch
                {
                    AvatarTextureOptimizerTextureFormatPolicy.Quality => TextureFormat.BC4,
                    AvatarTextureOptimizerTextureFormatPolicy.Compact => TextureFormat.R8,
                    _ => TextureFormat.BC4,
                },
                _ when hasAlpha => policy switch
                {
                    AvatarTextureOptimizerTextureFormatPolicy.Compact => TextureFormat.DXT5,
                    _ => TextureFormat.BC7,
                },
                _ => policy switch
                {
                    AvatarTextureOptimizerTextureFormatPolicy.Quality => TextureFormat.BC7,
                    AvatarTextureOptimizerTextureFormatPolicy.Compact => TextureFormat.DXT1,
                    _ => TextureFormat.DXT1,
                },
            };
        }

        private static TextureFormat ResolveMobile(AvatarTextureOptimizerTextureFormatPolicy policy, AtoTextureSemantic semantic, bool hasAlpha)
        {
            return policy switch
            {
                AvatarTextureOptimizerTextureFormatPolicy.Quality => TextureFormat.ASTC_4x4,
                AvatarTextureOptimizerTextureFormatPolicy.Compact => semantic == AtoTextureSemantic.Grayscale && !hasAlpha ? TextureFormat.ASTC_10x10 : TextureFormat.ASTC_8x8,
                _ => semantic == AtoTextureSemantic.Normal ? TextureFormat.ASTC_5x5 : TextureFormat.ASTC_6x6,
            };
        }

        private static bool HasSignificantAlpha(Texture2D texture)
        {
            var pixels = texture.GetPixels32();
            foreach (var color in pixels)
            {
                if (color.a is > 5 and < 250)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
