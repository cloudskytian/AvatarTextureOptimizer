// Texture Helper - Utility functions for texture operations
// 贴图辅助工具 - 贴图操作的实用函数

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace net.fosa.avatar_texture_optimizer.Editor.Core
{
    /// <summary>
    /// Utility functions for texture analysis, reading, and manipulation.
    /// 贴图分析、读取和操作的实用函数。
    /// </summary>
    public static class TextureHelper
    {
        /// <summary>
        /// Check if a texture has meaningful alpha channel data.
        /// 检查贴图是否有有意义的alpha通道数据。
        /// </summary>
        public static bool HasAlphaChannel(Texture2D texture)
        {
            if (texture == null) return false;

            var format = texture.format;
            switch (format)
            {
                case TextureFormat.RGBA32:
                case TextureFormat.RGBA64:
                case TextureFormat.RGBAHalf:
                case TextureFormat.RGBAFloat:
                case TextureFormat.BC3:
                case TextureFormat.BC7:
                case TextureFormat.DXT5:
                case TextureFormat.ARGB32:
                case TextureFormat.ARGB4444:
                case TextureFormat.RGBA4444:
                case TextureFormat.PVRTC_RGBA4:
                case TextureFormat.ETC2_RGBA8:
                case TextureFormat.ASTC_4x4:
                case TextureFormat.ASTC_5x5:
                case TextureFormat.ASTC_6x6:
                case TextureFormat.ASTC_8x8:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Read texture pixels as Color[] (handles non-readable textures via RenderTexture).
        /// 将贴图像素读取为Color[]（通过RenderTexture处理不可读贴图）。
        /// </summary>
        public static Color[] ReadPixels(Texture2D texture)
        {
            if (texture == null) return null;

            // Try direct read first
            try
            {
                return texture.GetPixels();
            }
            catch
            {
                // Texture is not readable, use RenderTexture workaround
                return ReadPixelsViaRT(texture);
            }
        }

        /// <summary>
        /// Read texture pixels via RenderTexture (for non-readable textures).
        /// 通过RenderTexture读取贴图像素（用于不可读贴图）。
        /// </summary>
        public static Color[] ReadPixelsViaRT(Texture2D texture)
        {
            var rt = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGBFloat);
            rt.filterMode = FilterMode.Point;
            Graphics.Blit(texture, rt);

            var prevRT = RenderTexture.active;
            RenderTexture.active = rt;

            var readableTex = new Texture2D(texture.width, texture.height, TextureFormat.RGBAFloat, false);
            readableTex.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
            readableTex.Apply();

            var pixels = readableTex.GetPixels();

            RenderTexture.active = prevRT;
            RenderTexture.ReleaseTemporary(rt);
            UnityEngine.Object.DestroyImmediate(readableTex);

            return pixels;
        }

        /// <summary>
        /// Read texture pixels as Color32[] (byte per channel, faster for some operations).
        /// 将贴图像素读取为Color32[]（每通道一字节，某些操作更快）。
        /// </summary>
        public static Color32[] ReadPixels32(Texture2D texture)
        {
            if (texture == null) return null;

            try
            {
                return texture.GetPixels32();
            }
            catch
            {
                var colors = ReadPixelsViaRT(texture);
                var result = new Color32[colors.Length];
                for (int i = 0; i < colors.Length; i++)
                {
                    result[i] = colors[i];
                }
                return result;
            }
        }

        /// <summary>
        /// Check if a texture region is a pure (solid) color.
        /// 检查贴图区域是否为纯色。
        /// </summary>
        public static bool IsRegionPureColor(Color[] pixels, int width, int height,
            int x, int y, int w, int h, out Color averageColor)
        {
            averageColor = Color.clear;
            if (pixels == null || x < 0 || y < 0 || x + w > width || y + h > height)
                return false;

            Color firstPixel = pixels[y * width + x];
            float totalR = 0, totalG = 0, totalB = 0, totalA = 0;
            int count = 0;

            for (int py = y; py < y + h; py++)
            {
                for (int px = x; px < x + w; px++)
                {
                    var pixel = pixels[py * width + px];
                    totalR += pixel.r;
                    totalG += pixel.g;
                    totalB += pixel.b;
                    totalA += pixel.a;
                    count++;

                    // Early exit if pixels differ significantly
                    if (Math.Abs(pixel.r - firstPixel.r) > 0.01f ||
                        Math.Abs(pixel.g - firstPixel.g) > 0.01f ||
                        Math.Abs(pixel.b - firstPixel.b) > 0.01f ||
                        Math.Abs(pixel.a - firstPixel.a) > 0.01f)
                    {
                        return false;
                    }
                }
            }

            averageColor = new Color(totalR / count, totalG / count, totalB / count, totalA / count);
            return true;
        }

        /// <summary>
        /// Create a copy of a texture with specific settings.
        /// 创建具有特定设置的贴图副本。
        /// </summary>
        public static Texture2D CreateTextureCopy(Texture2D source, string name = null)
        {
            if (source == null) return null;

            var pixels = ReadPixels(source);
            var copy = new Texture2D(source.width, source.height, TextureFormat.RGBAFloat, false);
            copy.SetPixels(pixels);
            copy.Apply();
            copy.name = name ?? source.name + "_copy";
            copy.wrapMode = source.wrapMode;
            copy.filterMode = source.filterMode;
            copy.anisoLevel = source.anisoLevel;

            return copy;
        }

        /// <summary>
        /// Compare two textures for content equality (pixel-by-pixel).
        /// 比较两个贴图的内容是否相等（逐像素）。
        /// </summary>
        public static bool AreTexturesEqual(Texture2D a, Texture2D b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (a.width != b.width || a.height != b.height) return false;

            var pixelsA = ReadPixels32(a);
            var pixelsB = ReadPixels32(b);

            if (pixelsA.Length != pixelsB.Length) return false;

            for (int i = 0; i < pixelsA.Length; i++)
            {
                if (pixelsA[i].r != pixelsB[i].r || pixelsA[i].g != pixelsB[i].g ||
                    pixelsA[i].b != pixelsB[i].b || pixelsA[i].a != pixelsB[i].a)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Generate a content hash for a texture (for deduplication).
        /// 生成贴图的内容哈希（用于去重）。
        /// </summary>
        public static string GetTextureContentHash(Texture2D texture)
        {
            if (texture == null) return "";

            var pixels = ReadPixels32(texture);
            int hash = texture.width * 31 + texture.height;
            hash = hash * 31 + texture.format.GetHashCode();

            // Sample some pixels for a quick hash (full hash is expensive)
            int sampleStep = Math.Max(1, pixels.Length / 1024);
            for (int i = 0; i < pixels.Length; i += sampleStep)
            {
                hash = hash * 31 + pixels[i].r;
                hash = hash * 31 + pixels[i].g;
                hash = hash * 31 + pixels[i].b;
                hash = hash * 31 + pixels[i].a;
            }

            return $"{texture.width}x{texture.height}_{texture.format}_{hash:X8}";
        }

        /// <summary>
        /// Calculate the physical area of a triangle in world space.
        /// 计算世界空间中三角形的物理面积。
        /// </summary>
        public static float CalculateTriangleArea(Vector3 a, Vector3 b, Vector3 c)
        {
            var ab = b - a;
            var ac = c - a;
            return 0.5f * Vector3.Cross(ab, ac).magnitude;
        }

        /// <summary>
        /// Calculate UV area of a triangle.
        /// 计算三角形的UV面积。
        /// </summary>
        public static float CalculateUVTriangleArea(Vector2 a, Vector2 b, Vector2 c)
        {
            return 0.5f * Mathf.Abs((b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y));
        }

        /// <summary>
        /// Get the bounding box of a set of UV coordinates.
        /// 获取一组UV坐标的包围盒。
        /// </summary>
        public static (Vector2 min, Vector2 max) GetUVBounds(List<Vector2> uvs)
        {
            if (uvs == null || uvs.Count == 0)
                return (Vector2.zero, Vector2.zero);

            Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
            Vector2 max = new Vector2(float.MinValue, float.MinValue);

            foreach (var uv in uvs)
            {
                min.x = Mathf.Min(min.x, uv.x);
                min.y = Mathf.Min(min.y, uv.y);
                max.x = Mathf.Max(max.x, uv.x);
                max.y = Mathf.Max(max.y, uv.y);
            }

            return (min, max);
        }
    }
}
