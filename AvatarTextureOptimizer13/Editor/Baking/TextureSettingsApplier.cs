// ATO — Avatar Texture Optimizer
// Saves generated textures (atlases / whole-texture-scaled) as PNG assets and applies
// import settings: compression tier, sRGB, alpha source, mipmaps + MipStreaming, forced
// Clamp, Read/Write off. Original source assets are never modified.
// 将生成的贴图（图集 / 整图缩放）保存为 PNG 资产并应用导入设置：压缩挡位、sRGB、alpha 来源、
// Mipmap + MipStreaming、强制 Clamp、关闭 Read/Write。绝不修改原始源资产。

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using net.fosa.ato;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// Applies texture import settings to generated textures. 为生成贴图应用导入设置。
    /// </summary>
    public static class TextureSettingsApplier
    {
        /// <summary>
        /// Save an atlas texture as a PNG asset and apply its import settings. Returns the imported asset.
        /// 将图集贴图保存为 PNG 资产并应用导入设置，返回导入后的资产。
        /// </summary>
        public static Texture2D SaveAtlas(ATOBuildContext bc, ATOAtlas atlas, bool transparent, List<Texture2D> sources)
        {
            var settings = bc.Settings;
            var folder = ContainerFolder(bc);
            string path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{atlas.name}.png");

            WritePNG(atlas.texture, path);

            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
            {
                ATOLog.Warn($"[Texture] could not configure importer for '{path}'.");
                return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            }

            bool srgb = atlas.kind == ATOTextureKind.Color || atlas.kind == ATOTextureKind.Emission;
            importer.textureType = atlas.kind == ATOTextureKind.NormalMap
                ? TextureImporterType.NormalMap
                : TextureImporterType.Default;
            importer.sRGBTexture = srgb;
            importer.mipmapEnabled = settings.mipmapsEnabled;
            importer.streamingMipmaps = settings.mipmapsEnabled; // VRChat: mipmap ⇔ streaming. VRChat：mipmap ⇔ streaming。
            importer.isReadable = false;                          // Read/Write off, not user-configurable. 关闭 Read/Write。
            importer.wrapMode = TextureWrapMode.Clamp;            // forced Clamp. 强制 Clamp。
            importer.alphaIsTransparency = transparent;
            importer.alphaSource = transparent ? TextureImporterAlphaSource.FromInput : TextureImporterAlphaSource.None;

            // Compression tier per kind. 按类别的压缩挡位。
            var choice = CompressionChoice(settings.compression, atlas.kind, transparent);
            ApplyCompression(importer, choice, atlas.kind);

            // NPOT safety: exclude PVRTC on iOS (PVRTC requires POT). 移动端 NPOT 安全：剔除 PVRTC。
            if (atlas.npot)
            {
                ExcludeNpotIncompatibleFormats(importer);
            }

            // Grayscale single-channel safety: a "grayscale" atlas that actually contains
            // multiple channels must stay multi-channel (spec #23). 灰度单通道安全：实际含多通道的灰度图集仍以多通道保存（#23）。
            if (kind == ATOTextureKind.Mask && settings.compression.grayscaleForceSingleChannel &&
                HasMultipleChannels(atlas.texture))
            {
                ATOLog.Warn(ATOI18n.T(ATOI18nKeys.WarnGrayscaleMultiChannel, atlas.name));
            }

            // Alpha safety: a transparent atlas must never lose its alpha channel. 透明图集绝不可丢失 alpha 通道。
            if (transparent && kind != ATOTextureKind.Color)
            {
                ATOLog.Warn(ATOI18n.T(ATOI18nKeys.WarnAlphaFormatMissing, atlas.name));
            }

            // Highest-quality filter / aniso among sources. 来源中的最高质量过滤/各向异性。
            importer.filterMode = HighestFilterMode(sources);
            importer.anisoLevel = HighestAniso(sources);

            importer.SaveAndReimport();
            var imported = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            imported.name = atlas.name;
            return imported;
        }

        /// <summary>Exclude NPOT-incompatible compression formats (e.g. PVRTC on iOS). 剔除 NPOT 不兼容的压缩格式（如 iOS 的 PVRTC）。</summary>
        private static void ExcludeNpotIncompatibleFormats(TextureImporter importer)
        {
            switch (EditorUserBuildSettings.activeBuildTarget)
            {
                case BuildTarget.iOS:
                    var ios = importer.GetPlatformTextureSettings("iPhone");
                    ios.overridden = true;
                    ios.format = TextureImporterFormat.ASTC_6x6; // NPOT-safe. NPOT 安全。
                    importer.SetPlatformTextureSettings(ios);
                    ATOLog.Warn(ATOI18n.T(ATOI18nKeys.WarnNpotFormatExcluded, "PVRTC"));
                    break;
                case BuildTarget.Android:
                    // ETC2 / ASTC both support NPOT; no exclusion needed. ETC2/ASTC 均支持 NPOT，无需剔除。
                    break;
            }
        }

        private static bool HasMultipleChannels(Texture2D tex)
        {
            var px = tex.GetPixels32();
            foreach (var p in px)
            {
                if (p.g != p.r || p.b != p.r) return true;
            }
            return false;
        }

        /// <summary>
        /// Save a whole-texture-scaled copy of a texture. 保存整图缩放后的贴图副本。
        /// </summary>
        public static Texture2D SaveScaled(ATOBuildContext bc, ATOTextureRef texRef, Color[] linearPixels, int newW, int newH)
        {
            var src = texRef.texture;
            bool srgb = ATOTextureIO.IsSRGB(src);
            var rgba = new Color32[newW * newH];
            for (int i = 0; i < rgba.Length; i++)
            {
                var c = linearPixels[i];
                float a = c.a;
                if (a > 1e-6f) c = new Color(c.r / a, c.g / a, c.b / a, a); else c = new Color(0, 0, 0, 0);
                if (srgb)
                    rgba[i] = new Color32(
                        (byte)Mathf.RoundToInt(QualityMath.LinearToSRgb(Clamp01(c.r)) * 255f),
                        (byte)Mathf.RoundToInt(QualityMath.LinearToSRgb(Clamp01(c.g)) * 255f),
                        (byte)Mathf.RoundToInt(QualityMath.LinearToSRgb(Clamp01(c.b)) * 255f),
                        (byte)Mathf.RoundToInt(Clamp01(c.a) * 255f));
                else
                    rgba[i] = new Color32(
                        (byte)Mathf.RoundToInt(Clamp01(c.r) * 255f),
                        (byte)Mathf.RoundToInt(Clamp01(c.g) * 255f),
                        (byte)Mathf.RoundToInt(Clamp01(c.b) * 255f),
                        (byte)Mathf.RoundToInt(Clamp01(c.a) * 255f));
            }

            var tex = new Texture2D(newW, newH, TextureFormat.RGBA32, false, false);
            tex.SetPixels32(rgba);
            tex.Apply(false, false);

            var folder = ContainerFolder(bc);
            string path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/ATO_{Sanitize(src.name)}.png");
            WritePNG(tex, path);
            UnityEngine.Object.DestroyImmediate(tex);

            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null)
            {
                var orig = ATOTextureIO.GetImporter(src);
                importer.textureType = orig != null ? orig.textureType : TextureImporterType.Default;
                importer.sRGBTexture = srgb;
                importer.mipmapEnabled = bc.Settings.mipmapsEnabled;
                importer.streamingMipmaps = bc.Settings.mipmapsEnabled;
                importer.isReadable = false;
                importer.wrapMode = src.wrapMode;
                importer.filterMode = src.filterMode;
                importer.SaveAndReimport();
            }
            var imported = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (imported != null) imported.name = "ATO_" + src.name;
            return imported;
        }

        private static void WritePNG(Texture2D tex, string path)
        {
            byte[] png = tex.EncodeToPNG();
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? "Assets");
            File.WriteAllBytes(path, png);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        }

        private static string ContainerFolder(ATOBuildContext bc)
        {
            // Generated textures are saved next to the NDMF asset container. 生成贴图保存在 NDMF 资产容器旁。
            return string.IsNullOrEmpty(bc.AssetFolder) ? "Assets/ATO_Generated" : bc.AssetFolder;
        }

        private static ATOSafeCompression CompressionChoice(ATOCompressionSettings s, ATOTextureKind kind, bool transparent)
        {
            switch (kind)
            {
                case ATOTextureKind.NormalMap: return s.normal;
                case ATOTextureKind.Mask:
                case ATOTextureKind.Grayscale: return s.grayscale;
                default:
                    return transparent ? s.colorTransparent : s.color;
            }
        }

        private static void ApplyCompression(TextureImporter importer, ATOSafeCompression choice, ATOTextureKind kind)
        {
            switch (choice)
            {
                case ATOSafeCompression.NoCompression:
                    importer.textureCompression = TextureImporterCompression.Uncompressed;
                    break;
                case ATOSafeCompression.LowCompression:
                    importer.textureCompression = TextureImporterCompression.CompressedLQ;
                    break;
                case ATOSafeCompression.HighCompression:
                    importer.textureCompression = TextureImporterCompression.CompressedHQ;
                    break;
                case ATOSafeCompression.Auto:
                case ATOSafeCompression.NormalCompression:
                case ATOSafeCompression.NormalMapCompression:
                default:
                    importer.textureCompression = TextureImporterCompression.Compressed;
                    break;
            }
        }

        private static FilterMode HighestFilterMode(List<Texture2D> sources)
        {
            var mode = FilterMode.Bilinear;
            foreach (var s in sources)
            {
                if (s == null) continue;
                if ((int)s.filterMode > (int)mode) mode = s.filterMode;
            }
            return mode;
        }

        private static int HighestAniso(List<Texture2D> sources)
        {
            int aniso = 1;
            foreach (var s in sources)
            {
                if (s == null) continue;
                aniso = Mathf.Max(aniso, s.anisoLevel);
            }
            return aniso;
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        private static string Sanitize(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }
    }
}
