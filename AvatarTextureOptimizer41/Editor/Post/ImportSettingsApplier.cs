using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

// Import-settings applier: writes generated textures to PNG assets and configures the TextureImporter
// (type, sRGB, Clamp, mip+streaming binding, max size, per-platform compression formats, read/write off).
// 导入参数应用器：将生成贴图写入 PNG 资产并配置 TextureImporter
// （类型、sRGB、Clamp、mip+streaming 联动、最大尺寸、各平台压缩格式、关闭读写）。

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public static class ImportSettingsApplier
    {
        private const string GeneratedRoot = "Assets/AvatarTextureOptimizer_Generated";

        /// <summary>
        /// Writes an RGBA32 Texture2D to a PNG asset and returns its path.
        /// 将 RGBA32 贴图写入 PNG 资产并返回路径。
        /// </summary>
        public static string WritePngAsset(Texture2D tex, string fileName)
        {
            if (!Directory.Exists(GeneratedRoot)) Directory.CreateDirectory(GeneratedRoot);
            string path = Path.Combine(GeneratedRoot, fileName + ".png");
            File.WriteAllBytes(path, tex.EncodeToPNG());
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            return path;
        }

        /// <summary>
        /// Applies the full import-settings policy to a generated texture asset.
        /// 对生成的贴图资产应用完整导入参数策略。
        /// </summary>
        public static void Apply(Texture2D textureAsset, string path, TextureClass cls, ATOSettingsData data, ATOPlatform platform)
        {
            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp == null) return;

            bool hasAlpha = cls == TextureClass.ColorAlpha;
            bool isNormal = cls == TextureClass.Normal;

            imp.textureType = isNormal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            imp.sRGBTexture = !(isNormal || cls == TextureClass.Mask); // normal & mask stay linear. 法线与蒙版保持线性。
            imp.alphaIsTransparency = hasAlpha;
            imp.alphaSource = hasAlpha ? TextureImporterAlphaSource.FromInput : TextureImporterAlphaSource.None;
            imp.wrapMode = TextureWrapMode.Clamp;                       // forced Clamp, not user-editable. 强制 Clamp，不可改。
            imp.mipmapEnabled = false;                                  // set below via category. 由分类设置。
            imp.isReadable = false;                                     // default off. 默认关闭读写。
            imp.npotScale = TextureImporterNPOTScale.ToNearest;
            imp.maxTextureSize = Mathf.Max(textureAsset.width, textureAsset.height);

            // Mip + MipStreaming binding: single switch controls both (VRChat requires streaming when mip on).
            // Mip 与 MipStreaming 联动：一个开关同时控制二者（VRChat 要求开 mip 必须开 streaming）。
            MipMode mip = data.MipFor(cls);
            imp.mipmapEnabled = mip == MipMode.On;
            imp.streamingMipmaps = mip == MipMode.On;

            // Per-platform compression. 各平台压缩。
            var format = data.CompressionFor(cls);
            string platformName = PlatformName(platform);
            var ps = imp.GetPlatformTextureSettings(platformName);
            if (platform == ATOPlatform.PC)
                ps = imp.GetPlatformTextureSettings("Standalone");
            ps.overridden = true;
            ps.maxTextureSize = imp.maxTextureSize;
            ApplyFormat(ps, format, platform, hasAlpha);

            // NPOT mode excludes unsupported formats (e.g. iOS PVRTC requires POT).
            // NPOT 模式剔除不支持的格式（如 iOS PVRTC 要求 POT）。
            if (data.atlasSizeMode == AtlasSizeMode.NonPowerOfTwo && platform == ATOPlatform.iOS)
            {
                if (ps.format == TextureImporterFormat.PVRTC_RGB4 || ps.format == TextureImporterFormat.PVRTC_RGBA4)
                    ps.format = hasAlpha ? TextureImporterFormat.ASTC_4x4 : TextureImporterFormat.ASTC_4x4;
            }

            // Grayscale single-channel fallback: if the texture actually has multiple channels, keep RGBA and warn.
            // 灰度单通道回退：若贴图实际多通道，仍按多通道保存并警告。
            if (cls == TextureClass.Mask && IsMultiChannel(textureAsset))
            {
                if (ps.format == TextureImporterFormat.Alpha8)
                {
                    ps.format = hasAlpha ? TextureImporterFormat.RGBA32 : TextureImporterFormat.RGB24;
                    ATOLog.Warn($"texture {textureAsset.name}: grayscale single-channel format requested but content is multi-channel; saved multi-channel");
                }
            }

            imp.SetPlatformTextureSettings(ps);
            EditorUtility.SetDirty(imp);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        }

        private static bool IsMultiChannel(Texture2D tex)
        {
            var pixels = tex.GetPixels32();
            int n = pixels.Length;
            int stride = Mathf.Max(1, n / 1024);
            for (int i = 0; i < n; i += stride)
            {
                var c = pixels[i];
                if (c.r != c.g || c.g != c.b) return true;
            }
            return false;
        }

        private static string PlatformName(ATOPlatform p)
        {
            switch (p)
            {
                case ATOPlatform.Android: return "Android";
                case ATOPlatform.iOS: return "iPhone";
                default: return "Standalone";
            }
        }

        private static void ApplyFormat(TextureImporterPlatformSettings ps, ATOCompressionFormat fmt, ATOPlatform platform, bool hasAlpha)
        {
            switch (fmt)
            {
                case ATOCompressionFormat.Automatic:
                    ps.textureCompression = TextureImporterCompression.Automatic;
                    ps.format = TextureImporterFormat.Automatic;
                    return;
                case ATOCompressionFormat.RGBA32: ps.format = TextureImporterFormat.RGBA32; break;
                case ATOCompressionFormat.RGB24: ps.format = hasAlpha ? TextureImporterFormat.RGBA32 : TextureImporterFormat.RGB24; break;
                case ATOCompressionFormat.BC7: ps.format = TextureImporterFormat.BC7; break;
                case ATOCompressionFormat.BC4: ps.format = TextureImporterFormat.BC4; break;
                case ATOCompressionFormat.BC5: ps.format = TextureImporterFormat.BC5; break;
                case ATOCompressionFormat.DXT1: ps.format = hasAlpha ? TextureImporterFormat.DXT5 : TextureImporterFormat.DXT1; break;
                case ATOCompressionFormat.DXT5: ps.format = TextureImporterFormat.DXT5; break;
                case ATOCompressionFormat.ETC2_RGB: ps.format = hasAlpha ? TextureImporterFormat.ETC2_RGBA8 : TextureImporterFormat.ETC2_RGB; break;
                case ATOCompressionFormat.ETC2_RGBA8: ps.format = TextureImporterFormat.ETC2_RGBA8; break;
                case ATOCompressionFormat.ASTC_4x4: ps.format = TextureImporterFormat.ASTC_4x4; break;
                case ATOCompressionFormat.ASTC_6x6: ps.format = TextureImporterFormat.ASTC_6x6; break;
                case ATOCompressionFormat.ASTC_8x8: ps.format = TextureImporterFormat.ASTC_8x8; break;
                case ATOCompressionFormat.ASTC_10x10: ps.format = TextureImporterFormat.ASTC_10x10; break;
                case ATOCompressionFormat.ASTC_12x12: ps.format = TextureImporterFormat.ASTC_12x12; break;
                case ATOCompressionFormat.PVRTC_RGB4: ps.format = hasAlpha ? TextureImporterFormat.PVRTC_RGBA4 : TextureImporterFormat.PVRTC_RGB4; break;
                case ATOCompressionFormat.PVRTC_RGBA4: ps.format = TextureImporterFormat.PVRTC_RGBA4; break;
            }
            ps.textureCompression = TextureImporterCompression.Compressed;
            // Safety: BC formats are PC-only; ASTC/ETC2 for Android; PVRTC for iOS. 安全规则：平台相关格式校验。
            if (platform == ATOPlatform.PC && (IsMobileFormat(ps.format))) ps.format = TextureImporterFormat.BC7;
            if (platform == ATOPlatform.Android && (ps.format == TextureImporterFormat.BC7 || ps.format == TextureImporterFormat.BC4 ||
                                                    ps.format == TextureImporterFormat.BC5 || ps.format == TextureImporterFormat.PVRTC_RGB4 ||
                                                    ps.format == TextureImporterFormat.PVRTC_RGBA4))
                ps.format = hasAlpha ? TextureImporterFormat.ASTC_6x6 : TextureImporterFormat.ASTC_6x6;
            if (platform == ATOPlatform.iOS && (ps.format == TextureImporterFormat.BC7 || ps.format == TextureImporterFormat.BC4 ||
                                                ps.format == TextureImporterFormat.BC5 || ps.format == TextureImporterFormat.DXT1 ||
                                                ps.format == TextureImporterFormat.DXT5))
                ps.format = hasAlpha ? TextureImporterFormat.PVRTC_RGBA4 : TextureImporterFormat.PVRTC_RGB4;
        }

        private static bool IsMobileFormat(TextureImporterFormat f)
        {
            switch (f)
            {
                case TextureImporterFormat.ETC_RGB4:
                case TextureImporterFormat.ETC2_RGB:
                case TextureImporterFormat.ETC2_RGBA8:
                case TextureImporterFormat.PVRTC_RGB2:
                case TextureImporterFormat.PVRTC_RGBA2:
                case TextureImporterFormat.PVRTC_RGB4:
                case TextureImporterFormat.PVRTC_RGBA4:
                case TextureImporterFormat.ASTC_4x4:
                case TextureImporterFormat.ASTC_6x6:
                case TextureImporterFormat.ASTC_8x8:
                case TextureImporterFormat.ASTC_10x10:
                case TextureImporterFormat.ASTC_12x12:
                    return true;
                default: return false;
            }
        }
    }
}
