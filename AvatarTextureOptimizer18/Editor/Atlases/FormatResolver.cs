using UnityEditor;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor.Atlases
{
    // 压缩格式解析与安全校验：把设置枚举映射到 TextureImporterFormat，并按平台/alpha/灰度通道/NPOT 校验，
    // 非法组合回退安全格式并输出警告（保证任意选项组合都不会对材质造成错误影响）。
    // Format resolution & validation: maps the settings enum to TextureImporterFormat and validates against
    // platform/alpha/gray-channel/NPOT constraints; invalid combos fall back safely with a warning.
    internal static class FormatResolver
    {
        public static TextureImporterFormat Resolve(ATOCategorySettings categorySettings, ATOTextureCategory category,
            ATOPlatform platform, bool npot, bool atlasHasAlpha, bool grayMultiChannel, out string warning)
        {
            warning = null;
            var requested = categorySettings.format;

            // 自动 → 交给 Unity（按内容选择最优）。Auto → let Unity pick the best per content.
            if (requested == ATOCompressionFormat.Auto)
            {
                return TextureImporterFormat.Automatic;
            }

            var format = ToImporterFormat(requested);

            // 平台合法性。Platform legality.
            switch (format)
            {
                case TextureImporterFormat.PVRTC_RGBA4:
                case TextureImporterFormat.PVRTC_RGB4:
                    if (platform != ATOPlatform.iOS)
                    {
                        warning = ATOLocalization.Tr("warn.format.notForPlatform", requested.ToString(), platform.ToString());
                        return TextureImporterFormat.Automatic;
                    }
                    if (npot)
                    {
                        warning = ATOLocalization.Tr("warn.format.pvrtcNpot");
                        return TextureImporterFormat.ASTC_6x6;
                    }
                    break;
                case TextureImporterFormat.BC7:
                case TextureImporterFormat.BC5:
                case TextureImporterFormat.BC6H:
                    if (platform == ATOPlatform.iOS)
                    {
                        warning = ATOLocalization.Tr("warn.format.bcNotOnIos", requested.ToString());
                        return TextureImporterFormat.ASTC_6x6;
                    }
                    break;
                case TextureImporterFormat.ETC2_RGBA8:
                case TextureImporterFormat.ETC2_RGB4:
                    if (platform != ATOPlatform.Android)
                    {
                        warning = ATOLocalization.Tr("warn.format.etc2AndroidOnly", requested.ToString());
                        return platform == ATOPlatform.iOS ? TextureImporterFormat.ASTC_6x6 : TextureImporterFormat.Automatic;
                    }
                    break;
            }

            // alpha 安全网：含透明贴图不允许不带 alpha 的格式。Alpha safety: transparent textures must keep alpha.
            if (atlasHasAlpha && !FormatHasAlpha(format))
            {
                warning = ATOLocalization.Tr("warn.format.alphaStripped", requested.ToString());
                return TextureImporterFormat.RGBA32;
            }

            // 灰度多通道安全网：多通道灰度贴图即使选择单通道格式也以多通道保存。Gray multi-channel safety net.
            if (category == ATOTextureCategory.Grayscale && grayMultiChannel && IsSingleChannel(format))
            {
                warning = ATOLocalization.Tr("warn.format.grayMultiChannel", requested.ToString());
                return TextureImporterFormat.RGBA32;
            }

            return format;
        }

        private static bool FormatHasAlpha(TextureImporterFormat f)
        {
            switch (f)
            {
                case TextureImporterFormat.RGB24:
                case TextureImporterFormat.RGB16:
                case TextureImporterFormat.DXT1:
                case TextureImporterFormat.BC4:
                case TextureImporterFormat.R8:
                case TextureImporterFormat.R16:
                case TextureImporterFormat.RHalf:
                case TextureImporterFormat.BC6H:
                case TextureImporterFormat.PVRTC_RGB4:
                case TextureImporterFormat.ETC_RGB4:
                case TextureImporterFormat.ETC2_RGB4:
                    return false;
                default:
                    return true;
            }
        }

        private static bool IsSingleChannel(TextureImporterFormat f)
        {
            switch (f)
            {
                case TextureImporterFormat.R8:
                case TextureImporterFormat.R16:
                case TextureImporterFormat.RHalf:
                    return true;
                default:
                    return false;
            }
        }

        private static TextureImporterFormat ToImporterFormat(ATOCompressionFormat f)
        {
            switch (f)
            {
                case ATOCompressionFormat.BC7: return TextureImporterFormat.BC7;
                case ATOCompressionFormat.BC5: return TextureImporterFormat.BC5;
                case ATOCompressionFormat.ETC2_RGBA8: return TextureImporterFormat.ETC2_RGBA8;
                case ATOCompressionFormat.ASTC_4x4: return TextureImporterFormat.ASTC_4x4;
                case ATOCompressionFormat.ASTC_6x6: return TextureImporterFormat.ASTC_6x6;
                case ATOCompressionFormat.ASTC_8x8: return TextureImporterFormat.ASTC_8x8;
                case ATOCompressionFormat.ASTC_10x10: return TextureImporterFormat.ASTC_10x10;
                case ATOCompressionFormat.ASTC_12x12: return TextureImporterFormat.ASTC_12x12;
                case ATOCompressionFormat.PVRTC_4BPP_RGBA: return TextureImporterFormat.PVRTC_RGBA4;
                case ATOCompressionFormat.RGB24: return TextureImporterFormat.RGB24;
                case ATOCompressionFormat.RGBA32: return TextureImporterFormat.RGBA32;
                case ATOCompressionFormat.R8: return TextureImporterFormat.R8;
                case ATOCompressionFormat.R16: return TextureImporterFormat.R16;
                case ATOCompressionFormat.RG16: return TextureImporterFormat.RG16;
                case ATOCompressionFormat.RHalf: return TextureImporterFormat.RHalf;
                case ATOCompressionFormat.RGHalf: return TextureImporterFormat.RGHalf;
                default: return TextureImporterFormat.Automatic;
            }
        }

        // 类别 → 设置内类别字段。Category → settings field.
        public static ATOCategorySettings ForCategory(ATOFormatSettings formats, ATOTextureCategory category)
        {
            switch (category)
            {
                case ATOTextureCategory.AlphaColor: return formats.alphaColor;
                case ATOTextureCategory.NormalMap: return formats.normalMap;
                case ATOTextureCategory.Grayscale: return formats.grayscale;
                default: return formats.opaqueColor;
            }
        }

        // 图集类别 → 贴图类别。Atlas kind → texture category.
        public static ATOTextureCategory ToCategory(Islands.AtlasKind kind)
        {
            switch (kind)
            {
                case Islands.AtlasKind.AlphaColor: return ATOTextureCategory.AlphaColor;
                case Islands.AtlasKind.Normal: return ATOTextureCategory.NormalMap;
                case Islands.AtlasKind.Grayscale: return ATOTextureCategory.Grayscale;
                default: return ATOTextureCategory.OpaqueColor;
            }
        }
    }
}
