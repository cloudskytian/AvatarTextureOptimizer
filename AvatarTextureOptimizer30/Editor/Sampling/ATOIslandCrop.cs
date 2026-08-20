// ATOIslandCrop.cs — 源贴图像素缓存与裁剪采样 / Source pixel cache and crop sampling.
// 说明：按贴图缓存像素（Color32，按需加载/释放以控制内存）；裁剪读取时转换为线性空间 float4
// （sRGB 解码；线性贴图直读；透明按需预乘；法线贴图解码为单位法线存于 xyz）。
// 输出路径：线性 float4 → sRGB/线性字节（法线编码为 DXT5nm 风格 AG 通道）。
// Note: per-texture pixel cache (Color32, loaded/released on demand to bound memory); crops are converted
// to linear float4 (sRGB decoded; linear textures read raw; premultiply when needed; normals decoded to unit
// normals in xyz). Output path: linear float4 → sRGB/linear bytes (normals encoded AG-style like DXT5nm).

using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>源贴图像素缓存。/ Source texture pixel cache.</summary>
    internal sealed class ATOSourceCache : IDisposable
    {
        private readonly Dictionary<Texture2D, Color32[]> _cache = new Dictionary<Texture2D, Color32[]>();

        /// <summary>获取（或加载）整张贴图像素。/ Get (or load) the full texture pixels.</summary>
        public Color32[] Get(Texture2D texture)
        {
            if (!_cache.TryGetValue(texture, out var pixels))
            {
                pixels = texture.GetPixels32(0);
                _cache[texture] = pixels;
            }
            return pixels;
        }

        /// <summary>是否已缓存。/ Whether cached.</summary>
        public bool Contains(Texture2D t) => _cache.ContainsKey(t);

        /// <summary>释放一张贴图的缓存。/ Release one texture's cache.</summary>
        public void Release(Texture2D texture)
        {
            _cache.Remove(texture);
        }

        public void Dispose()
        {
            _cache.Clear();
        }
    }

    /// <summary>裁剪采样与合成工具。/ Crop sampling & compositing utilities.</summary>
    internal static class ATOIslandCrop
    {
        /// <summary>法线编码方式（源贴图读取时使用）。/ Normal encoding (used when reading source crops).</summary>
        public enum NormalEncoding
        {
            None = 0,    // 非法线 / not a normal map
            DXT5nm = 1,  // DXT5nm（X 在 A，Y 在 G；编辑器桌面导入格式）/ DXT5nm (X in A, Y in G; desktop editor import format)
            RGB = 2,     // 直读 RGB 编码（ASTC/未标记类型的法线贴图）/ plain RGB encoding (ASTC / untyped normal maps)
        }

        /// <summary>
        /// 读取裁剪区域为线性 float4（按需 sRGB 解码/预乘/法线解码）。
        /// Read a crop rect as linear float4 (sRGB decode / premultiply / normal decode as needed).
        /// </summary>
        public static NativeArray<float4> LoadCrop(ATOSourceCache cache, Texture2D texture, RectInt rect,
            bool isSRGB, bool premultiply, NormalEncoding normalEncoding, Allocator alloc)
        {
            var src = cache.Get(texture);
            var w = texture.width;
            var h = texture.height;
            var dst = new NativeArray<float4>(rect.width * rect.height, alloc);
            int i = 0;
            for (int y = rect.y; y < rect.y + rect.height; y++)
            {
                var row = y * w;
                for (int x = rect.x; x < rect.x + rect.width; x++)
                {
                    var c = src[row + x];
                    var f = new float4(c.r, c.g, c.b, c.a) * (1f / 255f);
                    if (isSRGB) f.xyz = ATOMetrics.SrgbToLinear(f.xyz);
                    if (normalEncoding == NormalEncoding.DXT5nm)
                    {
                        // DXT5nm：X 在 A，Y 在 G，Z 由单位化恢复 / DXT5nm: X in A, Y in G, Z recovered from unit length
                        var nx = f.w * 2f - 1f;
                        var ny = f.y * 2f - 1f;
                        var nz2 = 1f - nx * nx - ny * ny;
                        f = new float4(nx, ny, nz2 > 0f ? math.sqrt(nz2) : 0f, 1f);
                    }
                    else if (normalEncoding == NormalEncoding.RGB)
                    {
                        var n = new float3(f.x * 2f - 1f, f.y * 2f - 1f, f.z * 2f - 1f);
                        n = math.normalize(n);
                        f = new float4(n, 1f);
                    }
                    else if (premultiply)
                    {
                        f.xyz *= f.w;
                    }
                    dst[i++] = f;
                }
            }
            return dst;
        }

        /// <summary>
        /// 线性 float4 → Color32（sRGB 编码或线性字节；法线编码：RGB 法线字节，由 Unity 导入器
        /// 按平台编码为 DXT5nm/ASTC —— 不直接写 AG，避免导入器把 AG 误当 RGB）。
        /// Linear float4 → Color32 (sRGB encode or linear bytes; normal encode: RGB normal bytes — the Unity
        /// importer re-encodes per platform to DXT5nm/ASTC; never write AG directly or the importer misreads it as RGB).
        /// </summary>
        public static Color32[] LinearToColor32(NativeArray<float4> src, bool toSRGB, bool normalEncode)
        {
            var dst = new Color32[src.Length];
            for (int i = 0; i < src.Length; i++)
            {
                var f = src[i];
                if (normalEncode)
                {
                    var n = math.normalize(new float3(f.x, f.y, f.z));
                    dst[i] = new Color32(
                        ToByte(n.x * 0.5f + 0.5f),
                        ToByte(n.y * 0.5f + 0.5f),
                        ToByte(n.z * 0.5f + 0.5f),
                        255);
                }
                else
                {
                    var c = toSRGB ? new float3(
                        ATOMetrics.LinearToSrgb(math.clamp(f.x, 0f, 1f)),
                        ATOMetrics.LinearToSrgb(math.clamp(f.y, 0f, 1f)),
                        ATOMetrics.LinearToSrgb(math.clamp(f.z, 0f, 1f))) : math.saturate(f.xyz);
                    dst[i] = new Color32(ToByte(c.x), ToByte(c.y), ToByte(c.z), ToByte(math.saturate(f.w)));
                }
            }
            return dst;
        }

        private static byte ToByte(float v) => (byte)math.clamp(math.round(v * 255f), 0, 255);

        /// <summary>
        /// 将裁剪结果写入图集缓冲（x/y 为目标左上角，按行拷贝；目标区域外忽略）。
        /// Stamp a scaled crop into the atlas buffer (x/y = target top-left; row-wise copy).
        /// </summary>
        public static void StampCrop(NativeArray<float4> atlas, int atlasW, int atlasH,
            NativeArray<float4> crop, int cropW, int cropH, int x, int y)
        {
            for (int row = 0; row < cropH; row++)
            {
                var ty = y + row;
                if (ty < 0 || ty >= atlasH) continue;
                var dstRow = ty * atlasW;
                var srcRow = row * cropW;
                for (int col = 0; col < cropW; col++)
                {
                    var tx = x + col;
                    if (tx < 0 || tx >= atlasW) continue;
                    atlas[dstRow + tx] = crop[srcRow + col];
                }
            }
        }

        /// <summary>检测裁剪是否纯色（返回颜色）。/ Detect whether a crop is solid (returns the color).</summary>
        public static bool TryGetSolidColor(NativeArray<float4> src, out float4 color)
        {
            color = default;
            if (src.Length == 0) return true;
            var first = src[0];
            for (int i = 1; i < src.Length; i++)
            {
                if (math.any(math.abs(src[i] - first) > 1e-6f)) return false;
            }
            color = first;
            return true;
        }

        /// <summary>释放裁剪缓冲的便捷方法。/ Convenience dispose.</summary>
        public static void SafeDispose(NativeArray<float4> buffer)
        {
            if (buffer.IsCreated) buffer.Dispose();
        }
    }
}
