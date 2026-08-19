using System;
using NetFosa.AvatarTextureOptimizer.Editor.Logging;
using UnityEditor;
using UnityEngine;
using NetFosa.AvatarTextureOptimizer;

namespace NetFosa.AvatarTextureOptimizer.Editor.Processing
{
    /// <summary>
    /// 压缩/导入参数应用器。为生成的图集与 fallback 贴图设置导入设置：
    /// - 按类别（不透明/透明/法线/灰度）选择压缩格式（安全枚举，平台受限选项自动剔除）
    /// - Mipmap 与 MipStreaming 绑定为单一开关（VRC 要求）
    /// - 强制 Clamp、默认关闭 Read/Write、其余参数取质量最高
    /// - 安全兜底：含 alpha 不给无 alpha 格式；灰度单通道误设 → 多通道保存并警告；NPOT 剔除 PVRTC
    /// </summary>
    public static class CompressionApplier
    {
        public static void Apply(string path, int width, int height, ATOTextureCategory category,
            ATOColorSpace colorSpace, ATOFilterMode filterMode, bool hasAlpha, bool npot,
            EffectiveSettings settings, BuildReport report)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) return;

            var format = settings.compression.Get(category);

            // 安全兜底：含 alpha 的贴图不给无 alpha 格式
            if (hasAlpha)
            {
                var noAlpha = format == ATOCompressionFormat.DXT1 || format == ATOCompressionFormat.ETC2_RGB
                              || format == ATOCompressionFormat.PVRTC_RGB4 || format == ATOCompressionFormat.RGB24
                              || format == ATOCompressionFormat.ETC_RGB4 || format == ATOCompressionFormat.CrunchDXT1
                              || format == ATOCompressionFormat.CrunchETC2_RGB;
                if (noAlpha)
                {
                    report.AddWarning($"texture '{path}' has alpha but format {format} has no alpha channel; using RGBA fallback.");
                    format = settings.compression.Get(ATOTextureCategory.MainTransparent);
                    if (noAlpha)
                    {
                        format = ATOCompressionFormat.Auto;
                    }
                }
            }

            // NPOT 时剔除 PVRTC
            if (npot && (format == ATOCompressionFormat.PVRTC_RGB4 || format == ATOCompressionFormat.PVRTC_RGBA4))
            {
                report.AddWarning($"texture '{path}' is NPOT; PVRTC requires power-of-two, falling back to Auto.");
                format = ATOCompressionFormat.Auto;
            }

            importer.textureType = category == ATOTextureCategory.Normal
                ? TextureImporterType.NormalMap
                : TextureImporterType.Default;
            importer.sRGBTexture = colorSpace == ATOColorSpace.SRGB;
            importer.alphaIsTransparency = hasAlpha && category != ATOTextureCategory.Normal;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.wrapMode = TextureWrapMode.Clamp; // 强制 Clamp，不开放给用户
            importer.isReadable = false;              // 默认关闭 Read/Write

            switch (filterMode)
            {
                case ATOFilterMode.Point: importer.filterMode = FilterMode.Point; break;
                case ATOFilterMode.Trilinear: importer.filterMode = FilterMode.Trilinear; break;
                default: importer.filterMode = FilterMode.Bilinear; break;
            }

            bool mip = settings.mipmaps.Get(category);
            importer.mipmapEnabled = mip;
            importer.streamingMipmaps = mip; // 绑定：Mipmap ⇔ MipStreaming

            int maxSize = Mathf.Max(width, height);

            // 平台相关格式设置（当前平台 + 全部平台统一安全映射）
            var platformName = GetPlatformName(settings.platform);
            var mapped = MapFormat(format, settings.platform, hasAlpha, npot, out bool crunched, report, path);

            var ps = new TextureImporterPlatformSettings
            {
                name = platformName,
                overridden = true,
                format = mapped,
                maxTextureSize = maxSize,
                compressionQuality = 50,
                crunchedCompression = crunched,
            };
            importer.SetPlatformTextureSettings(ps);

            // 其他平台也设置合理值（保守：优先用户选择，不支持则自动）
            ApplyOtherPlatforms(importer, format, maxSize, settings.platform, hasAlpha, npot, report);

            importer.SaveAndReimport();
        }

        private static void ApplyOtherPlatforms(TextureImporter importer, ATOCompressionFormat format, int maxSize,
            ATOPlatform current, bool hasAlpha, bool npot, BuildReport report)
        {
            foreach (var p in new[] { ATOPlatform.PC, ATOPlatform.Android, ATOPlatform.iOS })
            {
                if (p == current) continue;
                var mapped = MapFormat(format, p, hasAlpha, npot, out bool crunched, report, importer.assetPath);
                var ps = new TextureImporterPlatformSettings
                {
                    name = GetPlatformName(p),
                    overridden = true,
                    format = mapped,
                    maxTextureSize = maxSize,
                    compressionQuality = 50,
                    crunchedCompression = crunched,
                };
                importer.SetPlatformTextureSettings(ps);
            }
        }

        private static string GetPlatformName(ATOPlatform p)
        {
            switch (p)
            {
                case ATOPlatform.Android: return "Android";
                case ATOPlatform.iOS: return "iPhone";
                default: return "Standalone";
            }
        }

        private static TextureImporterFormat MapFormat(ATOCompressionFormat format, ATOPlatform platform,
            bool hasAlpha, bool npot, out bool crunched, BuildReport report, string path)
        {
            crunched = false;

            // Auto：按平台最优
            if (format == ATOCompressionFormat.Auto)
            {
                switch (platform)
                {
                    case ATOPlatform.Android:
                        return hasAlpha ? TextureImporterFormat.ASTC_6x6 : TextureImporterFormat.ASTC_6x6;
                    case ATOPlatform.iOS:
                        return hasAlpha ? TextureImporterFormat.ASTC_6x6 : TextureImporterFormat.ASTC_6x6;
                    default:
                        return hasAlpha ? TextureImporterFormat.BC7 : TextureImporterFormat.BC7;
                }
            }

            switch (format)
            {
                case ATOCompressionFormat.None:
                    return hasAlpha ? TextureImporterFormat.RGBA32 : TextureImporterFormat.RGB24;
                case ATOCompressionFormat.RGBA32: return TextureImporterFormat.RGBA32;
                case ATOCompressionFormat.RGB24: return TextureImporterFormat.RGB24;
                case ATOCompressionFormat.DXT1:
                    if (platform == ATOPlatform.PC) return TextureImporterFormat.DXT1;
                    break; // 移动端不支持 → fallthrough
                case ATOCompressionFormat.DXT5:
                    if (platform == ATOPlatform.PC) return TextureImporterFormat.DXT5;
                    break;
                case ATOCompressionFormat.BC7:
                    if (platform == ATOPlatform.PC) return TextureImporterFormat.BC7;
                    break;
                case ATOCompressionFormat.ETC2_RGB: return TextureImporterFormat.ETC2_RGB4;
                case ATOCompressionFormat.ETC2_RGBA: return TextureImporterFormat.ETC2_RGBA8;
                case ATOCompressionFormat.ASTC_4x4: return TextureImporterFormat.ASTC_4x4;
                case ATOCompressionFormat.ASTC_6x6: return TextureImporterFormat.ASTC_6x6;
                case ATOCompressionFormat.ASTC_8x8: return TextureImporterFormat.ASTC_8x8;
                case ATOCompressionFormat.ASTC_10x10: return TextureImporterFormat.ASTC_10x10;
                case ATOCompressionFormat.ASTC_12x12: return TextureImporterFormat.ASTC_12x12;
                case ATOCompressionFormat.ETC_RGB4:
                    if (platform != ATOPlatform.PC) return TextureImporterFormat.ETC_RGB4;
                    break;
                case ATOCompressionFormat.PVRTC_RGB4:
                    if (platform == ATOPlatform.iOS && !npot) return TextureImporterFormat.PVRTC_RGB4;
                    break;
                case ATOCompressionFormat.PVRTC_RGBA4:
                    if (platform == ATOPlatform.iOS && !npot) return TextureImporterFormat.PVRTC_RGBA4;
                    break;
                case ATOCompressionFormat.CrunchDXT1:
                    if (platform == ATOPlatform.PC) { crunched = true; return TextureImporterFormat.DXT1; }
                    break;
                case ATOCompressionFormat.CrunchDXT5:
                    if (platform == ATOPlatform.PC) { crunched = true; return TextureImporterFormat.DXT5; }
                    break;
                case ATOCompressionFormat.CrunchETC2_RGB:
                    if (platform != ATOPlatform.PC) { crunched = true; return TextureImporterFormat.ETC2_RGB4; }
                    break;
                case ATOCompressionFormat.CrunchETC2_RGBA:
                    if (platform != ATOPlatform.PC) { crunched = true; return TextureImporterFormat.ETC2_RGBA8; }
                    break;
                case ATOCompressionFormat.CrunchASTC_4x4:
                    crunched = true; return TextureImporterFormat.ASTC_4x4;
                case ATOCompressionFormat.CrunchASTC_6x6:
                    crunched = true; return TextureImporterFormat.ASTC_6x6;
                case ATOCompressionFormat.CrunchASTC_8x8:
                    crunched = true; return TextureImporterFormat.ASTC_8x8;
                case ATOCompressionFormat.CrunchASTC_12x12:
                    crunched = true; return TextureImporterFormat.ASTC_12x12;
            }

            // 不支持 → 自动兜底
            if (report != null)
                report.AddWarning($"format {format} not supported on {platform}; falling back to Auto.");
            return MapFormat(ATOCompressionFormat.Auto, platform, hasAlpha, npot, out crunched, report, path);
        }
    }
}
