using System;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;

namespace net.fosa.ato
{
    // ============================================================================
    // 导入参数配置 / Import settings configuration.
    // 图集: Read/Write 强制关闭, 强制 Clamp, 其余参数取所有来源贴图质量最高者.
    // Atlases: Read/Write forced off, Clamp forced, other params take the best of all sources.
    // 构建期安全校验: 平台能力 + NPOT 剔除 PVRTC + 单通道格式与像素内容兜底.
    // Build-time safety validation: platform capability + NPOT excludes PVRTC + single-channel fallback.
    // ============================================================================
    internal static class ATOImportConfig
    {
        public static TextureImporterFormat? ResolveFormat(ATOCompressionFormat f)
        {
            switch (f)
            {
                case ATOCompressionFormat.Auto: return null;
                case ATOCompressionFormat.Uncompressed: return TextureImporterFormat.RGBA32;
                case ATOCompressionFormat.R8: return TextureImporterFormat.R8;
                case ATOCompressionFormat.BC1: return TextureImporterFormat.DXT1; // BC1 == DXT1
                case ATOCompressionFormat.BC7: return TextureImporterFormat.BC7;
                case ATOCompressionFormat.BC5: return TextureImporterFormat.BC5;
                case ATOCompressionFormat.ETC2_RGB4: return TextureImporterFormat.ETC2_RGB4;
                case ATOCompressionFormat.ETC2_RGBA8: return TextureImporterFormat.ETC2_RGBA8;
                case ATOCompressionFormat.EAC_R: return TextureImporterFormat.EAC_R;
                case ATOCompressionFormat.EAC_RG: return TextureImporterFormat.EAC_RG;
                case ATOCompressionFormat.ASTC_4x4: return TextureImporterFormat.ASTC_4x4;
                case ATOCompressionFormat.ASTC_6x6: return TextureImporterFormat.ASTC_6x6;
                case ATOCompressionFormat.ASTC_8x8: return TextureImporterFormat.ASTC_8x8;
                case ATOCompressionFormat.ASTC_10x10: return TextureImporterFormat.ASTC_10x10;
                case ATOCompressionFormat.PVRTC_RGB4: return TextureImporterFormat.PVRTC_RGB4;
                case ATOCompressionFormat.PVRTC_RGBA4: return TextureImporterFormat.PVRTC_RGBA4;
                default: return null;
            }
        }

        /// <summary>是否为单通道格式 / Whether the format is single-channel.</summary>
        private static bool IsSingleChannel(TextureImporterFormat f)
        {
            return f == TextureImporterFormat.R8 || f == TextureImporterFormat.EAC_R;
        }

        /// <summary>保存贴图资产并配置导入参数 / Saves the texture asset and configures import settings.</summary>
        public static void SaveAndConfigure(ATOBuildState state, BuildContext ctx, Texture2D tex,
            ATOTextureCategory category, bool sRGB, bool hasAlpha, ATOTextureInfo sourceOf, ATOAtlas atlas,
            int usedChannels)
        {
            var cfg = state.config;
            ctx.AssetSaver.SaveAsset(tex);

            var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(tex)) as TextureImporter;
            if (importer == null)
            {
                ATOLog.Warn($"无法获取贴图导入器, 保留默认设置 / could not get TextureImporter for {tex.name}");
                return;
            }

            bool normal = category == ATOTextureCategory.Normal;
            importer.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            importer.sRGBTexture = sRGB;

            // 图集强制Clamp; 独立贴图保持来源的wrap模式 / atlases force Clamp; standalone textures keep the source wrap mode
            if (atlas != null)
            {
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.wrapModeU = TextureWrapMode.Clamp;
                importer.wrapModeV = TextureWrapMode.Clamp;
            }
            else if (sourceOf != null)
            {
                importer.wrapModeU = sourceOf.wrapU;
                importer.wrapModeV = sourceOf.wrapV;
            }

            importer.mipmapEnabled = cfg.enableMipmaps;     // Mipmap与MipStreaming绑定 / mipmap bound with mip streaming
            importer.streamingMipmaps = cfg.enableMipmaps;
            importer.isReadable = false;                    // Read/Write 强制关闭 / Read/Write forced off
            importer.filterMode = atlas != null && atlas.group != null ? atlas.group.filterMode
                : (sourceOf != null ? sourceOf.filterMode : FilterMode.Bilinear);

            // 各向异性取来源最高 / aniso takes the highest of sources
            int aniso = 1;
            if (atlas != null)
            {
                foreach (var p in atlas.placements)
                {
                    foreach (var kv in p.island.perTexture)
                    {
                        if (kv.Value.atlas != atlas) continue;
                        var imp = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(kv.Key.source)) as TextureImporter;
                        if (imp != null) aniso = Mathf.Max(aniso, imp.anisoLevel);
                    }
                }
            }
            else if (sourceOf != null)
            {
                var imp = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(sourceOf.source)) as TextureImporter;
                if (imp != null) aniso = Mathf.Max(aniso, imp.anisoLevel);
            }

            importer.anisoLevel = Mathf.Min(aniso, 16);

            // 压缩格式(按类别与平台) / compression format (per category and platform)
            var format = PickFormat(cfg, category, hasAlpha);

            // 单通道格式兜底: 被使用通道超出R通道 -> 回退多通道并报warning / single-channel fallback
            if (format != null && IsSingleChannel(format.Value)
                && (category == ATOTextureCategory.Grayscale || category == ATOTextureCategory.Mask))
            {
                if (usedChannels != 0b0001)
                {
                    ATOLog.Warn($"灰度贴图 '{tex.name}' 被使用的通道不止R通道(0b{Convert.ToString(usedChannels, 2)}), 单通道格式回退为多通道 / grayscale texture uses channels beyond R; single-channel format falls back to multi-channel");
                    format = null;
                }
            }

            bool npot = cfg.enableNPOT;

            foreach (var target in new[] { BuildTarget.StandaloneWindows64, BuildTarget.StandaloneLinux64, BuildTarget.Android, BuildTarget.iOS })
            {
                var f = format;
                if (f != null)
                {
                    if (npot && (f.Value == TextureImporterFormat.PVRTC_RGB4 || f.Value == TextureImporterFormat.PVRTC_RGBA4
                                 || f.Value == TextureImporterFormat.PVRTC_RGB2 || f.Value == TextureImporterFormat.PVRTC_RGBA2))
                    {
                        ATOLog.Warn($"NPOT 图集不支持 PVRTC, 回退自动格式 / NPOT atlases do not support PVRTC; falling back to automatic for {target}");
                        f = null;
                    }

                    if (!TextureImporter.IsPlatformTextureFormatValid(TextureImporterType.Default, target, f.Value))
                    {
                        ATOLog.Warn($"格式 {f.Value} 不受 {target} 支持, 回退自动格式 / format {f.Value} unsupported on {target}; falling back to automatic");
                        f = null;
                    }
                }

                int maxSize = Mathf.Min(cfg.maxAtlasSize, atlas?.width ?? cfg.maxAtlasSize);
                importer.SetPlatformTextureSettings(target.ToString(), maxSize, f ?? TextureImporterFormat.Automatic);
            }

            importer.SaveAndReimport();
        }

        private static TextureImporterFormat? PickFormat(ATOConfig cfg, ATOTextureCategory category, bool hasAlpha)
        {
            var f = category switch
            {
                ATOTextureCategory.Normal => cfg.normalFormat,
                ATOTextureCategory.Mask => cfg.grayscaleFormat,
                ATOTextureCategory.Grayscale => cfg.grayscaleFormat,
                _ => hasAlpha ? cfg.transparentFormat : cfg.opaqueFormat
            };
            return ResolveFormat(f);
        }
    }
}
