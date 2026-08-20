using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using Fosa.AvatarTextureOptimizer;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Writes generated atlas pixels and applies safe importer settings. / 写入生成图集像素并应用安全导入设置。
    /// </summary>
    internal static class GeneratedTextureWriter
    {
        public static Texture2D CreateAndSave(ATOBuildSession.BuildContextAdapter context, int width, int height,
            string name, Action<NativeArray<Color32>, BitArray> fill, ATOTextureCategory category,
            ATOPlatform platform, ATOPlatformOptions options, TextureAssetInfo representative, ATOLogger logger)
        {
            Texture2D memoryTexture = null;
            try
            {
                memoryTexture = new Texture2D(width, height, TextureFormat.RGBA32, false, true)
                {
                    name = name,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = representative == null ? FilterMode.Bilinear : representative.FilterMode,
                    anisoLevel = 0
                };
                NativeArray<Color32> raw = memoryTexture.GetRawTextureData<Color32>();
                BitArray covered = new BitArray(width * height);
                fill(raw, covered);
                PullPush(raw, covered, width, height, 64);
                memoryTexture.Apply(false, false);

                string containerPath = context.AssetContainer == null ? string.Empty : AssetDatabase.GetAssetPath(context.AssetContainer);
                if (!string.IsNullOrEmpty(containerPath))
                {
                    string directory = Path.GetDirectoryName(containerPath);
                    if (string.IsNullOrEmpty(directory)) return KeepInMemory(context, memoryTexture);
                    directory = directory.Replace('\\', '/');
                    string filePath = AssetDatabase.GenerateUniqueAssetPath(directory + "/" + SafeName(name) + ".png");
                    byte[] encoded = memoryTexture.EncodeToPNG();
                    File.WriteAllBytes(filePath, encoded);
                    AssetDatabase.ImportAsset(filePath, ImportAssetOptions.ForceSynchronousImport);
                    Texture2D imported = AssetDatabase.LoadAssetAtPath<Texture2D>(filePath);
                    if (imported != null)
                    {
                        ConfigureImporter(filePath, imported, category, platform, options, representative, logger);
                        UnityEngine.Object.DestroyImmediate(memoryTexture);
                        return imported;
                    }
                }
                return KeepInMemory(context, memoryTexture);
            }
            catch (Exception exception)
            {
                if (memoryTexture != null) UnityEngine.Object.DestroyImmediate(memoryTexture);
                logger.Warning("Generated atlas write failed; the affected family falls back without atlas. / 图集写入失败，相关族回退。 " + exception.Message);
                return null;
            }
        }

        private static Texture2D KeepInMemory(ATOBuildSession.BuildContextAdapter context, Texture2D texture)
        {
            context.SaveAsset(texture);
            texture.Apply(false, true);
            return texture;
        }

        private static void ConfigureImporter(string path, Texture2D imported, ATOTextureCategory category,
            ATOPlatform platform, ATOPlatformOptions options, TextureAssetInfo representative, ATOLogger logger)
        {
            TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) return;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.isReadable = false;
            importer.mipmapEnabled = options.enableMipStreaming;
            importer.streamingMipmaps = options.enableMipStreaming;
            importer.filterMode = representative == null ? FilterMode.Bilinear : representative.FilterMode;
            importer.textureType = category == ATOTextureCategory.Normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            importer.sRGBTexture = category != ATOTextureCategory.Normal && category != ATOTextureCategory.Grayscale;
            importer.maxTextureSize = Mathf.Clamp(Mathf.Max(imported.width, imported.height), 64, options.maxAtlasSize);

            if (options.allowTextureFormatOverride)
            {
                ATOFormatChoice choice = ChoiceFor(category, options);
                TextureImporterPlatformSettings settings = importer.GetDefaultPlatformTextureSettings();
                settings.name = PlatformName(platform);
                settings.overridden = true;
                settings.maxTextureSize = importer.maxTextureSize;
                settings.format = TextureFormatPolicy.Resolve(choice, category, imported, platform, options.experimentalNpotAtlases, logger);
                settings.textureCompression = TextureImporterCompression.Compressed;
                settings.crunchedCompression = false;
                importer.SetPlatformTextureSettings(settings);
            }
            EditorUtility.SetDirty(importer);
            importer.SaveAndReimport();
        }

        private static ATOFormatChoice ChoiceFor(ATOTextureCategory category, ATOPlatformOptions options)
        {
            switch (category)
            {
                case ATOTextureCategory.Transparent: return options.transparentFormat;
                case ATOTextureCategory.Normal: return options.normalFormat;
                case ATOTextureCategory.Grayscale: return options.grayscaleFormat;
                case ATOTextureCategory.Opaque: return options.opaqueFormat;
                default: return options.fallbackFormat;
            }
        }

        public static string PlatformName(ATOPlatform platform)
        {
            switch (platform)
            {
                case ATOPlatform.Android: return "Android";
                case ATOPlatform.iOS: return "iPhone";
                default: return "Standalone";
            }
        }

        private static string SafeName(string value)
        {
            if (string.IsNullOrEmpty(value)) return "ATO_Atlas";
            char[] chars = value.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (chars[i] == '/' || chars[i] == '\\' || chars[i] == ':' || chars[i] == '*' || chars[i] == '?' ||
                    chars[i] == '"' || chars[i] == '<' || chars[i] == '>' || chars[i] == '|') chars[i] = '_';
            }
            return new string(chars);
        }

        private static void PullPush(NativeArray<Color32> pixels, BitArray covered, int width, int height, int maxRadius)
        {
            // Two directional sweeps propagate edge colors across each connected empty region in O(N) time.
            // 双向扫描以 O(N) 时间将边缘颜色传播到每个连通空白区域。
            bool changed;
            for (int pass = 0; pass < 2; pass++)
            {
                changed = false;
                int yStart = pass == 0 ? 0 : height - 1;
                int yEnd = pass == 0 ? height : -1;
                int yStep = pass == 0 ? 1 : -1;
                int xStart = pass == 0 ? 0 : width - 1;
                int xEnd = pass == 0 ? width : -1;
                int xStep = pass == 0 ? 1 : -1;
                for (int y = yStart; y != yEnd; y += yStep)
                {
                    for (int x = xStart; x != xEnd; x += xStep)
                    {
                        int index = y * width + x;
                        if (covered[index]) continue;
                        int neighbor = FindDirectionalNeighbor(covered, width, height, x, y, pass == 0);
                        if (neighbor < 0) continue;
                        Color32 current = pixels[index];
                        Color32 source = pixels[neighbor];
                        current.r = source.r;
                        current.g = source.g;
                        current.b = source.b;
                        pixels[index] = current;
                        covered[index] = true;
                        changed = true;
                    }
                }
                if (!changed) break;
            }
        }

        private static int FindDirectionalNeighbor(BitArray covered, int width, int height, int x, int y, bool forward)
        {
            if (forward)
            {
                if (x > 0 && covered[y * width + x - 1]) return y * width + x - 1;
                if (y > 0 && covered[(y - 1) * width + x]) return (y - 1) * width + x;
                if (x > 0 && y > 0 && covered[(y - 1) * width + x - 1]) return (y - 1) * width + x - 1;
            }
            else
            {
                if (x + 1 < width && covered[y * width + x + 1]) return y * width + x + 1;
                if (y + 1 < height && covered[(y + 1) * width + x]) return (y + 1) * width + x;
                if (x + 1 < width && y + 1 < height && covered[(y + 1) * width + x + 1]) return (y + 1) * width + x + 1;
            }
            return -1;
        }
    }

    internal static class TextureFormatPolicy
    {
        public static TextureImporterFormat Resolve(ATOFormatChoice choice, ATOTextureCategory category, Texture2D texture,
            ATOPlatform platform, bool npot, ATOLogger logger)
        {
            bool alpha = category == ATOTextureCategory.Transparent || TextureHasAlpha(texture);
            if (alpha && (choice == ATOFormatChoice.RGB24 || choice == ATOFormatChoice.BC1 || choice == ATOFormatChoice.ETC2RGB))
            {
                logger.Warning("Requested RGB-only format for alpha texture; safely using RGBA32. / 含 alpha 纹理请求了 RGB 格式，已安全回退 RGBA32。");
                choice = ATOFormatChoice.RGBA32;
            }
            if (category == ATOTextureCategory.Normal && choice == ATOFormatChoice.R8)
            {
                logger.Warning("R8 is unsafe for normal maps; safely using RGBA32. / R8 对法线图不安全，已安全回退 RGBA32。");
                choice = ATOFormatChoice.RGBA32;
            }
            if (platform == ATOPlatform.iOS && choice == ATOFormatChoice.PVRTC_RGBA4 && texture != null &&
                (npot || texture.width % 4 != 0 || texture.height % 4 != 0))
            {
                logger.Warning("PVRTC is disabled for NPOT or incompatible dimensions; safely using RGBA32. / NPOT 或尺寸不兼容时禁用 PVRTC，已安全回退 RGBA32。");
                choice = ATOFormatChoice.RGBA32;
            }
            string enumName;
            switch (choice)
            {
                case ATOFormatChoice.BC7: enumName = "BC7"; break;
                case ATOFormatChoice.BC3: enumName = "BC3"; break;
                case ATOFormatChoice.BC1: enumName = "BC1"; break;
                case ATOFormatChoice.ETC2RGBA8: enumName = "ETC2_RGBA8"; break;
                case ATOFormatChoice.ETC2RGB: enumName = "ETC2_RGB"; break;
                case ATOFormatChoice.ASTC6x6: enumName = "ASTC_6x6"; break;
                case ATOFormatChoice.ASTC4x4: enumName = "ASTC_4x4"; break;
                case ATOFormatChoice.PVRTC_RGBA4: enumName = "PVRTC_RGBA4"; break;
                case ATOFormatChoice.RGB24: enumName = "RGB24"; break;
                case ATOFormatChoice.RG8: enumName = "RG16"; break;
                case ATOFormatChoice.R8: enumName = "R8"; break;
                case ATOFormatChoice.RGBA32:
                case ATOFormatChoice.Automatic:
                default: enumName = "RGBA32"; break;
            }
            try
            {
                return (TextureImporterFormat)Enum.Parse(typeof(TextureImporterFormat), enumName);
            }
            catch (Exception)
            {
                logger.Warning("Format " + enumName + " is unavailable on this Unity/platform; using Automatic. / 当前 Unity/平台不支持该格式，回退 Automatic。");
                return TextureImporterFormat.Automatic;
            }
        }

        private static bool TextureHasAlpha(Texture2D texture)
        {
            if (texture == null) return false;
            try
            {
                Color32[] pixels = texture.GetPixels32();
                for (int i = 0; i < pixels.Length; i++) if (pixels[i].a != 255) return true;
            }
            catch (Exception)
            {
                // Import metadata is used by the caller when pixels are not readable. / 不可读时由调用者使用导入元数据。
            }
            return false;
        }
    }
}
