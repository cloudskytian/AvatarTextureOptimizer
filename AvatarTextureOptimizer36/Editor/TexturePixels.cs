using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Managed pixel buffer used by CPU reference metrics and atlas generation. / CPU 参考指标与图集生成使用的托管像素缓冲。
    /// </summary>
    internal sealed class TexturePixelData : IDisposable
    {
        public readonly int Width;
        public readonly int Height;
        public readonly Color32[] Pixels;

        public TexturePixelData(int width, int height, Color32[] pixels)
        {
            Width = width;
            Height = height;
            Pixels = pixels ?? throw new ArgumentNullException(nameof(pixels));
        }

        public Color32 Get(int x, int y)
        {
            x = Mathf.Clamp(x, 0, Width - 1);
            y = Mathf.Clamp(y, 0, Height - 1);
            return Pixels[y * Width + x];
        }

        public void Dispose()
        {
            // Managed arrays are reclaimed by GC; the explicit method makes ownership visible to the build session.
            // 托管数组由 GC 回收；显式方法用于清晰表达构建阶段的所有权。
        }
    }

    /// <summary>
    /// Bounded per-build pixel cache; it never survives a build. / 有上限的单次构建像素缓存，不跨构建存活。
    /// </summary>
    internal sealed class TexturePixelCache : IDisposable
    {
        private readonly long _budgetBytes;
        private readonly Dictionary<Texture2D, TexturePixelData> _cache = new Dictionary<Texture2D, TexturePixelData>();
        private long _usedBytes;

        public TexturePixelCache(long budgetBytes)
        {
            _budgetBytes = Math.Max(16 * 1024 * 1024, budgetBytes);
        }

        public TexturePixelData Get(Texture2D texture, ATOLogger logger)
        {
            if (texture == null) return null;
            TexturePixelData cached;
            if (_cache.TryGetValue(texture, out cached)) return cached;
            TexturePixelData data = TexturePixelReader.Read(texture, logger);
            if (data == null) return null;
            long bytes = (long)data.Pixels.Length * 4L;
            if (bytes <= _budgetBytes && _usedBytes + bytes <= _budgetBytes)
            {
                _cache[texture] = data;
                _usedBytes += bytes;
            }
            return data;
        }

        public void Clear()
        {
            foreach (TexturePixelData data in _cache.Values) data.Dispose();
            _cache.Clear();
            _usedBytes = 0;
        }

        public void Dispose()
        {
            Clear();
        }
    }

    internal static class TexturePixelReader
    {
        public static TexturePixelData Read(Texture2D texture, ATOLogger logger)
        {
            if (texture == null || texture.width <= 0 || texture.height <= 0) return null;
            try
            {
                Color32[] pixels = texture.GetPixels32();
                if (pixels != null && pixels.Length == texture.width * texture.height)
                    return new TexturePixelData(texture.width, texture.height, pixels);
            }
            catch (Exception)
            {
                // Imported non-readable textures use the GPU fallback below. / 不可读导入纹理使用下面的 GPU 回退。
            }

            RenderTexture temporary = null;
            Texture2D readable = null;
            RenderTexture prior = RenderTexture.active;
            try
            {
                RenderTextureFormat format = RenderTextureFormat.ARGB32;
                temporary = RenderTexture.GetTemporary(texture.width, texture.height, 0, format,
                    texture.graphicsFormat == GraphicsFormat.R8G8B8A8_SRGB ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
                temporary.filterMode = FilterMode.Point;
                Graphics.Blit(texture, temporary);
                RenderTexture.active = temporary;
                readable = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false, true);
                readable.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0, false);
                readable.Apply(false, true);
                return new TexturePixelData(texture.width, texture.height, readable.GetPixels32());
            }
            catch (Exception exception)
            {
                logger?.Warning("GPU readback failed for texture '" + texture.name + "'; texture is skipped safely. / GPU 读回失败，纹理已安全跳过。 " + exception.Message);
                return null;
            }
            finally
            {
                RenderTexture.active = prior;
                if (temporary != null) RenderTexture.ReleaseTemporary(temporary);
                if (readable != null) UnityEngine.Object.DestroyImmediate(readable);
            }
        }

        public static string Hash(Texture2D texture, TextureImportFingerprint fingerprint, ATOLogger logger)
        {
            TexturePixelData data = Read(texture, logger);
            if (data == null) return string.Empty;
            using (SHA256 sha = SHA256.Create())
            {
                byte[] header = System.Text.Encoding.UTF8.GetBytes(
                    fingerprint.Width + ":" + fingerprint.Height + ":" + fingerprint.WrapMode + ":" +
                    fingerprint.FilterMode + ":" + fingerprint.Mipmap + ":" + fingerprint.Streaming + ":" +
                    fingerprint.SRGB + ":" + fingerprint.Compression + ":" + fingerprint.MaxSize + ":");
                sha.TransformBlock(header, 0, header.Length, header, 0);
                byte[] bytes = new byte[data.Pixels.Length * 4];
                for (int i = 0; i < data.Pixels.Length; i++)
                {
                    int offset = i * 4;
                    Color32 color = data.Pixels[i];
                    bytes[offset] = color.r;
                    bytes[offset + 1] = color.g;
                    bytes[offset + 2] = color.b;
                    bytes[offset + 3] = color.a;
                }
                sha.TransformFinalBlock(bytes, 0, bytes.Length);
                return BitConverter.ToString(sha.Hash).Replace("-", string.Empty);
            }
        }
    }
}
