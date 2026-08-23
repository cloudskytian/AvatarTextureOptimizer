// Texture importer configuration for generated atlases / textures.
// Handles: category-based compression, per-platform overrides, mipmap <-> MipStreaming binding,
// Clamp wrap, Read/Write off, NPOT format legality, alpha-channel safety fallbacks.
// / 生成图集/贴图的导入器配置：按类别压缩、各平台覆盖、Mipmap 与 MipStreaming 绑定、
// Clamp 环绕、关闭 Read/Write、NPOT 格式合法性、alpha 通道安全回退。

using System;
using UnityEditor;
using UnityEngine;
using net.fosa.avatar_texture_optimizer.editor.pipeline;
using net.fosa.avatar_texture_optimizer.runtime;

namespace net.fosa.avatar_texture_optimizer.editor.baking
{
    /// <summary>
    /// Applies import settings to a generated texture asset. / 为生成的贴图资产应用导入设置。
    /// </summary>
    public static class TextureImporterSetup
    {
        /// <summary>Apply settings and reimport. / 应用设置并重新导入。</summary>
        public static void Apply(string path, bool normalMap, bool srgb, bool hasAlpha,
            bool mipmap, int maxSize, AvatarTextureOptimizer.CompressionFormat globalFormat,
            bool npot, BuildTargetHint hint, AvatarTextureOptimizer.PlatformSettings platform,
            AvatarTextureOptimizer.CategorySettings categories, string categoryName)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) return;

            importer.textureType = normalMap ? TextureImporterType.NormalMap : TextureImporterType.Default;
            importer.sRGBTexture = srgb;
            importer.alphaIsTransparency = hasAlpha;
            importer.mipmapEnabled = mipmap;
            // VRChat requires MipStreaming whenever Mipmap is on; we bind them with one switch. / Mipmap 与 MipStreaming 绑定
            importer.streamingMipmaps = mipmap;
            importer.isReadable = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.npotScale = npot ? TextureImporterNPOTScale.None : importer.npotScale;
            importer.maxTextureSize = Mathf.Max(1, maxSize);

            // Determine formats per platform. Global format is the default; user overrides replace it. / 按平台确定格式。
            var globalFmt = MapFormat(globalFormat, hasAlpha, hint, npot, out var warn1);
            if (warn1 != null) AtoLog.Warn(warn1 + " (" + path + ")");

            importer.textureCompression = IsUncompressed(globalFmt)
                ? TextureImporterCompression.Uncompressed
                : TextureImporterCompression.Compressed;

            // Always pin the three supported platforms to the chosen format (deterministic). / 始终固定三个平台为选定格式。
            ApplyPlatform(importer, "Standalone", globalFmt, maxSize);
            ApplyPlatform(importer, "Android", globalFmt, maxSize);
            ApplyPlatform(importer, "iPhone", globalFmt, maxSize);

            // User per-platform overrides / 用户各平台覆盖
            if (platform.enableOverrides)
            {
                if (platform.pc != null && platform.pc.enabled)
                {
                    var f = MapFormat(platform.pc.opaque, hasAlpha, hint, npot, out var w);
                    if (w != null) AtoLog.Warn(w + " (PC/" + path + ")");
                    ApplyPlatform(importer, "Standalone", f, platform.pc.maxAtlasSize > 0 ? platform.pc.maxAtlasSize : maxSize);
                }
                if (platform.android != null && platform.android.enabled)
                {
                    var f = MapFormat(platform.android.opaque, hasAlpha, BuildTargetHint.Android, npot, out var w);
                    if (w != null) AtoLog.Warn(w + " (Android/" + path + ")");
                    ApplyPlatform(importer, "Android", f, platform.android.maxAtlasSize > 0 ? platform.android.maxAtlasSize : maxSize);
                }
                if (platform.ios != null && platform.ios.enabled)
                {
                    var f = MapFormat(platform.ios.opaque, hasAlpha, BuildTargetHint.iOS, npot, out var w);
                    if (w != null) AtoLog.Warn(w + " (iOS/" + path + ")");
                    ApplyPlatform(importer, "iPhone", f, platform.ios.maxAtlasSize > 0 ? platform.ios.maxAtlasSize : maxSize);
                }
            }

            importer.SaveAndReimport();
        }

        private static bool IsUncompressed(TextureFormat f)
        {
            return f == TextureFormat.RGBA32 || f == TextureFormat.RGB24;
        }

        private static void ApplyPlatform(TextureImporter importer, string platformName,
            TextureFormat format, int maxSize)
        {
            importer.SetPlatformTextureSettings(new TextureImporterPlatformSettings
            {
                overridden = true,
                format = format,
                maxTextureSize = maxSize,
                name = platformName,
            });
        }

        /// <summary>Map a user compression enum to a TextureFormat with platform legality checks and fallbacks. / 压缩枚举映射为 TextureFormat，含平台合法性与回退。</summary>
        public static TextureFormat MapFormat(AvatarTextureOptimizer.CompressionFormat fmt, bool hasAlpha,
            BuildTargetHint hint, bool npot, ref string warning)
        {
            switch (fmt)
            {
                case AvatarTextureOptimizer.CompressionFormat.BC1:
                    if (hasAlpha) { warning = "Opaque-only format (BC1) used for a texture with alpha; using BC7 instead. / 带 alpha 的贴图使用了无 alpha 格式 BC1，已改用 BC7。"; return TextureFormat.BC7; }
                    return hint == BuildTargetHint.Android || hint == BuildTargetHint.iOS
                        ? FallbackMobile(ref warning) : TextureFormat.DXT1;
                case AvatarTextureOptimizer.CompressionFormat.BC7:
                    if (hint == BuildTargetHint.Android || hint == BuildTargetHint.iOS)
                        return FallbackMobile(ref warning);
                    return TextureFormat.BC7;
                case AvatarTextureOptimizer.CompressionFormat.ETC2_RGB:
                    if (hasAlpha) { warning = "ETC2_RGB has no alpha; using ETC2_RGBA instead. / ETC2_RGB 无 alpha，已改用 ETC2_RGBA。"; return TextureFormat.ETC2_RGBA8; }
                    return TextureFormat.ETC2_RGB4;
                case AvatarTextureOptimizer.CompressionFormat.ETC2_RGBA:
                    return TextureFormat.ETC2_RGBA8;
                case AvatarTextureOptimizer.CompressionFormat.ASTC_4x4:
                    return TextureFormat.ASTC_4x4;
                case AvatarTextureOptimizer.CompressionFormat.ASTC_6x6:
                    return TextureFormat.ASTC_6x6;
                case AvatarTextureOptimizer.CompressionFormat.ASTC_8x8:
                    return TextureFormat.ASTC_8x8;
                case AvatarTextureOptimizer.CompressionFormat.RGBA32:
                    return TextureFormat.RGBA32;
                case AvatarTextureOptimizer.CompressionFormat.RGB24:
                    if (hasAlpha) { warning = "RGB24 has no alpha; using RGBA32 instead. / RGB24 无 alpha，已改用 RGBA32。"; return TextureFormat.RGBA32; }
                    return TextureFormat.RGB24;
                case AvatarTextureOptimizer.CompressionFormat.PVRTC_RGB4:
                    if (hasAlpha) { warning = "PVRTC_RGB4 has no alpha; using PVRTC_RGBA4 instead. / PVRTC_RGB4 无 alpha，已改用 PVRTC_RGBA4。"; return TextureFormat.PVRTC_RGBA4; }
                    if (hint == BuildTargetHint.Android) { warning = "PVRTC unsupported on Android; using ASTC instead. / Android 不支持 PVRTC，已改用 ASTC。"; return TextureFormat.ASTC_6x6; }
                    if (npot) { warning = "PVRTC requires POT; using ASTC instead. / PVRTC 需要 POT，已改用 ASTC。"; return TextureFormat.ASTC_6x6; }
                    return TextureFormat.PVRTC_RGB4;
                case AvatarTextureOptimizer.CompressionFormat.PVRTC_RGBA4:
                    if (hint == BuildTargetHint.Android) { warning = "PVRTC unsupported on Android; using ASTC instead. / Android 不支持 PVRTC，已改用 ASTC。"; return TextureFormat.ASTC_6x6; }
                    if (npot) { warning = "PVRTC requires POT; using ASTC instead. / PVRTC 需要 POT，已改用 ASTC。"; return TextureFormat.ASTC_6x6; }
                    return TextureFormat.PVRTC_RGBA4;
                default:
                    // Auto: best for platform / 自动：按平台最优
                    switch (hint)
                    {
                        case BuildTargetHint.Android:
                        case BuildTargetHint.iOS:
                            return hasAlpha ? TextureFormat.ASTC_6x6 : TextureFormat.ASTC_8x8;
                        default:
                            return hasAlpha ? TextureFormat.BC7 : TextureFormat.DXT1;
                    }
            }
        }

        private static TextureFormat FallbackMobile(ref string warning)
        {
            warning = "BC formats unsupported on mobile; using ASTC instead. / 移动端不支持 BC 格式，已改用 ASTC。";
            return TextureFormat.ASTC_6x6;
        }
    }
}
