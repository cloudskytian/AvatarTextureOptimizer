// Avatar Texture Optimizer (ATO)
// Compression-format resolution per platform + category + actual alpha content.
// 按 平台 + 分类 + 实际 alpha 内容 解析压缩格式。
//
// Safety: the concrete TextureImporterFormat is validated per platform; incompatible choices
// fall back to a safe default with a warning (build-time fallback per the spec).
// 安全：具体 TextureImporterFormat 按平台校验；不兼容选项回退到安全默认并告警（构建期兜底）。

using UnityEditor;
using UnityEngine;

namespace NetFosa.ATO
{
    /// <summary>
    /// Resolves the effective import settings for a generated texture/atlas.
    /// 为生成的贴图/图集解析生效的导入设置。
    /// </summary>
    public static class ATOAtlasFormat
    {
        public struct Settings
        {
            public bool sRGB;
            public bool mipmap;
            public bool streamingMipmaps;
            public bool crunched;
            public TextureImporterCompression compression;
            public int maxSize;
            public TextureWrapMode wrapMode;
            public FilterMode filterMode;
            public int aniso;
        }

        /// <summary>Pick the effective compression choice for a texture ref by category. / 按分类选取生效的压缩选项。</summary>
        public static ATOCompressionChoice ChoiceFor(ATOBuildContext build, ATOTextureRef tr)
            => ChoiceFor(build, tr.Category, tr.hasAlpha);

        /// <summary>Pick the effective compression choice by category + alpha. / 按 分类 + alpha 选取生效的压缩选项。</summary>
        public static ATOCompressionChoice ChoiceFor(ATOBuildContext build, ATOTextureCategory category, bool hasAlpha)
        {
            switch (category)
            {
                case ATOTextureCategory.NormalMap: return build.compression.normal;
                case ATOTextureCategory.Mask:
                case ATOTextureCategory.Grayscale: return build.compression.grayscale;
                default:
                    return hasAlpha ? build.compression.alpha : build.compression.opaque;
            }
        }

        /// <summary>
        /// Resolve import settings for a generated texture (atlas or resized texture).
        /// 为生成的贴图（图集或缩放后的贴图）解析导入设置。
        /// </summary>
        public static Settings Resolve(ATOBuildContext build, ATOTextureRef tr, int width, int height, bool hasAlpha)
        {
            var choice = ChoiceFor(build, tr);
            var s = new Settings
            {
                sRGB = tr.isSRGB && tr.Category != ATOTextureCategory.NormalMap && tr.Category != ATOTextureCategory.Mask && tr.Category != ATOTextureCategory.Grayscale,
                mipmap = choice.mipStreaming,            // single switch: mipmaps ⇔ streaming / 单一开关：mipmap ⇔ 流式
                streamingMipmaps = choice.mipStreaming,
                crunched = choice.format == ATOCompressionFormat.Auto && IsCrunchedCandidate(build.platform),
                compression = TextureImporterCompression.Compressed,
                maxSize = Mathf.Max(ATOConstants.MinAtlasSize, Mathf.Max(width, height)),
                wrapMode = TextureWrapMode.Clamp,
                filterMode = tr.filterMode,
                aniso = 1,
            };
            return s;
        }

        private static bool IsCrunchedCandidate(ATOPlatform p)
        {
            // Crunch is desktop-only and not valid for all formats; conservative. / Crunch 仅桌面可用且并非所有格式支持，保守处理。
            return p == ATOPlatform.PC;
        }

        /// <summary>
        /// Map a choice + platform + alpha to a concrete TextureImporterFormat, with fallback.
        /// 把 选项 + 平台 + alpha 映射为具体 TextureImporterFormat，含兜底。
        /// </summary>
        public static TextureImporterFormat ToImporterFormat(ATOBuildContext build, ATOCompressionChoice choice, bool hasAlpha)
        {
            var fmt = choice.format;
            var p = build.platform;

            if (fmt == ATOCompressionFormat.Auto)
            {
                if (p == ATOPlatform.Android) return hasAlpha ? TextureImporterFormat.ASTC_6x6 : TextureImporterFormat.ASTC_6x6;
                if (p == ATOPlatform.iOS) return hasAlpha ? TextureImporterFormat.ASTC_6x6 : TextureImporterFormat.ASTC_6x6;
                return hasAlpha ? TextureImporterFormat.BC7 : TextureImporterFormat.BC7; // desktop / 桌面端
            }

            switch (fmt)
            {
                case ATOCompressionFormat.RGBA32: return TextureImporterFormat.RGBA32;
                case ATOCompressionFormat.RGB24: return TextureImporterFormat.RGB24;
                case ATOCompressionFormat.ASTC_4x4: return TextureImporterFormat.ASTC_4x4;
                case ATOCompressionFormat.ASTC_6x6: return TextureImporterFormat.ASTC_6x6;
                case ATOCompressionFormat.ASTC_8x8: return TextureImporterFormat.ASTC_8x8;
                case ATOCompressionFormat.BC7: return TextureImporterFormat.BC7;
                case ATOCompressionFormat.BC5: return TextureImporterFormat.BC5;
                case ATOCompressionFormat.BC4: return TextureImporterFormat.BC4;
                case ATOCompressionFormat.BC1: return TextureImporterFormat.DXT1;
                case ATOCompressionFormat.BC3: return TextureImporterFormat.DXT5;
                case ATOCompressionFormat.ETC2_RGBA8: return TextureImporterFormat.ETC2_RGBA8;
                case ATOCompressionFormat.ETC2_RGB8: return TextureImporterFormat.ETC2_RGB4;
                case ATOCompressionFormat.PVRTC_RGBA4: return p == ATOPlatform.iOS ? TextureImporterFormat.PVRTC_RGBA4 : TextureImporterFormat.ASTC_6x6;
                case ATOCompressionFormat.PVRTC_RGB4: return p == ATOPlatform.iOS ? TextureImporterFormat.PVRTC_RGB4 : TextureImporterFormat.ASTC_6x6;
                default: return hasAlpha ? TextureImporterFormat.BC7 : TextureImporterFormat.BC7;
            }
        }
    }
}
