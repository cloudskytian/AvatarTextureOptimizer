using System;
using UnityEditor;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// Texture IO: import settings reading, pixel readback (CPU + GPU fallback), content hashing. /
    /// 贴图 IO：导入设置读取、像素读回（CPU + GPU 兜底）、内容哈希。
    /// </summary>
    internal static class AtoTextureIO
    {
        /// <summary>
        /// Read the subset of import settings relevant for ATO decisions. / 读取与 ATO 决策相关的导入设置子集。
        /// </summary>
        public static AtoImportSettings GetImportSettings(Texture2D texture)
        {
            var settings = new AtoImportSettings();
            var path = AssetDatabase.GetAssetPath(texture);
            if (!string.IsNullOrEmpty(path))
            {
                if (AssetImporter.GetAtPath(path) is TextureImporter importer)
                {
                    settings.SrgbTexture = importer.sRGBTexture;
                    settings.FilterMode = importer.filterMode;
                    settings.WrapModeU = importer.wrapModeU;
                    settings.WrapModeV = importer.wrapModeV;
                    settings.AnisoLevel = importer.anisoLevel;
                    settings.MipMapEnabled = importer.mipmapEnabled;
                    settings.StreamingMipmaps = importer.streamingMipmaps;
                    settings.Compression = importer.textureCompression;
                    settings.CrunchCompression = importer.crunchedCompression;
                    settings.CrunchCompressionQuality = importer.compressionQuality;
                    settings.IsReadable = importer.isReadable;
                    settings.AlphaIsTransparency = importer.alphaIsTransparency;
                    settings.MaxTextureSize = importer.maxTextureSize;
                    settings.NpotScale = importer.npotScale;
                    var platformSettings = importer.GetPlatformTextureSettings(
                        AtoPlatformUtil.CurrentPlatformName());
                    settings.PcFormat = importer.GetPlatformTextureSettings("Standalone").format;
                    settings.AndroidFormat = importer.GetPlatformTextureSettings("Android").format;
                    settings.IosFormat = importer.GetPlatformTextureSettings("iPhone").format;
                    return settings;
                }
            }
            // Fallback for runtime-created textures (no importer). / 运行时贴图（无 importer）的兜底。
            settings.SrgbTexture = ShaderAnalyzer.IsSrgbTexture(texture);
            settings.FilterMode = texture.filterMode;
            settings.WrapModeU = texture.wrapModeU;
            settings.WrapModeV = texture.wrapModeV;
            settings.AnisoLevel = texture.anisoLevel;
            settings.MipMapEnabled = texture.mipmapCount > 1;
            settings.IsReadable = texture.isReadable;
            settings.MaxTextureSize = Mathf.Max(texture.width, texture.height);
            return settings;
        }

        /// <summary>
        /// Get the raw stored RGBA32 pixels of a texture (no color space conversion), using a GPU
        /// copy for non-readable textures. The returned buffer must be released via
        /// AtoRuntimeCache or left to GC (managed). / 获取贴图存储的原始 RGBA32 像素（不做色彩空间转换），
        /// 不可读贴图走 GPU 拷贝。返回的托管缓冲交给 GC 或 AtoRuntimeCache。
        /// </summary>
        public static Color32[] GetPixels(Texture2D texture)
        {
            if (texture == null) return Array.Empty<Color32>();
            if (texture.isReadable)
            {
                return texture.GetPixels32();
            }

            // GPU copy path. / GPU 拷贝路径。
            var srgb = GetImportSettings(texture).SrgbTexture;
            var rt = RenderTexture.GetTemporary(texture.width, texture.height, 0,
                RenderTextureFormat.ARGB32, srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            try
            {
                var previous = RenderTexture.active;
                Graphics.Blit(texture, rt);
                RenderTexture.active = rt;
                var copy = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false, false);
                try
                {
                    copy.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
                    copy.Apply(false, false);
                    return copy.GetPixels32();
                }
                finally
                {
                    RenderTexture.active = previous;
                    UnityEngine.Object.DestroyImmediate(copy);
                }
            }
            finally
            {
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        /// <summary>
        /// Compute a content hash of the texture pixels (FNV-1a 64 over raw RGBA32). / 计算贴图像素内容哈希（FNV-1a 64，原始 RGBA32）。
        /// </summary>
        public static string HashPixels(Color32[] pixels)
        {
            unchecked
            {
                ulong hash = 14695981039346656037UL; // FNV offset basis
                foreach (var pixel in pixels)
                {
                    hash ^= pixel.r;
                    hash *= 1099511628211UL;
                    hash ^= pixel.g;
                    hash *= 1099511628211UL;
                    hash ^= pixel.b;
                    hash *= 1099511628211UL;
                    hash ^= pixel.a;
                    hash *= 1099511628211UL;
                }
                return hash.ToString("x16");
            }
        }

        /// <summary>
        /// Compute the dedupe key of a texture: content + import settings. Different import
        /// settings are treated as different textures (requirement). / 计算贴图去重键：内容+导入设置。
        /// 导入设置不同直接视为不同贴图（需求要求）。
        /// </summary>
        public static string GetDedupeKey(Texture2D texture, AtoImportSettings settings)
        {
            string contentHash;
            try
            {
                var imageHash = texture.imageContentsHash;
                if (imageHash.isValid) contentHash = imageHash.ToString();
                else contentHash = HashPixels(GetPixels(texture));
            }
            catch (Exception e)
            {
                AtoLog.Verbose($"[ATO] imageContentsHash failed for {texture.name}: {e.Message}");
                contentHash = HashPixels(GetPixels(texture));
            }
            return settings.BuildKey(contentHash);
        }

        /// <summary>
        /// Estimate the uncompressed byte size of a texture. / 估算贴图的未压缩字节体积。
        /// </summary>
        public static long EstimateBytes(Texture2D texture)
        {
            var channels = 4; // conservative RGBA32. / 保守按 RGBA32。
            return (long)texture.width * texture.height * channels;
        }
    }
}
