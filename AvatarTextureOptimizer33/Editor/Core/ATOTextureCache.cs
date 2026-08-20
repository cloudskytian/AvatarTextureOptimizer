// SPDX-License-Identifier: MIT
// EN: GPU assisted texture decoding with a memory budgeted LRU cache. Textures are decoded once into
//     linear half precision RGBA, which keeps 8 bit sources bit exact while halving the memory cost.
// ZH: 借助 GPU 解码贴图，并使用带内存预算的 LRU 缓存。贴图只解码一次，存为线性半精度 RGBA，
//     对 8 位源数据无损，同时把内存占用减半。

using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// EN: A decoded texture: linear RGBA (or decoded normal XYZ) at the source resolution.
    /// ZH: 解码后的贴图：源分辨率下的线性 RGBA（法线贴图为解码后的 XYZ）。
    /// </summary>
    public sealed class ATODecodedTexture : IDisposable
    {
        public int Width;
        public int Height;
        public NativeArray<half4> Pixels;
        public bool IsNormalDecoded;
        public bool HasAlphaContent;
        public bool IsFlatColor;
        public float4 FlatColor;

        /// <summary>EN: true when the channel actually carries variation. ZH: 通道确实存在变化时为 true。</summary>
        public bool4 ChannelVarying;

        /// <summary>EN: true when alpha is 1 everywhere. ZH: alpha 处处为 1 时为 true。</summary>
        public bool AlphaIsOpaque => !ChannelVarying.w && !HasAlphaContent;
        public long ByteSize => (long)Width * Height * 8;

        public void Dispose()
        {
            if (Pixels.IsCreated) Pixels.Dispose();
        }
    }

    /// <summary>
    /// EN: Decodes and caches textures. All GPU resources are released immediately after each readback,
    ///     only the CPU side arrays are cached, and the cache honours a byte budget (LRU eviction).
    /// ZH: 解码并缓存贴图。每次回读后立即释放 GPU 资源，只缓存 CPU 侧数组，并按字节预算做 LRU 淘汰。
    /// </summary>
    public sealed class ATOTextureCache : IDisposable
    {
        private readonly ATOLog _log;
        private readonly long _budgetBytes;
        private readonly Dictionary<Texture2D, ATODecodedTexture> _cache = new Dictionary<Texture2D, ATODecodedTexture>();
        private readonly LinkedList<Texture2D> _lru = new LinkedList<Texture2D>();
        private long _used;

        public ATOTextureCache(ATOLog log, long budgetBytes = 512L * 1024 * 1024)
        {
            _log = log;
            _budgetBytes = budgetBytes;
        }

        /// <summary>EN: Current cache size in bytes. ZH: 当前缓存占用字节数。</summary>
        public long UsedBytes => _used;

        /// <summary>
        /// EN: Returns the decoded pixels of a texture, decoding on demand.
        /// ZH: 返回贴图的解码结果，按需解码。
        /// </summary>
        public ATODecodedTexture Get(Texture2D texture, bool decodeNormal)
        {
            if (texture == null) throw new ArgumentNullException(nameof(texture));

            if (_cache.TryGetValue(texture, out var cached) && cached.IsNormalDecoded == decodeNormal)
            {
                _lru.Remove(texture);
                _lru.AddLast(texture);
                return cached;
            }

            if (cached != null) Evict(texture);

            var decoded = Decode(texture, decodeNormal);
            _cache[texture] = decoded;
            _lru.AddLast(texture);
            _used += decoded.ByteSize;
            TrimToBudget();
            return decoded;
        }

        /// <summary>EN: Drops one entry. ZH: 丢弃一条缓存。</summary>
        public void Evict(Texture2D texture)
        {
            if (!_cache.TryGetValue(texture, out var d)) return;
            _used -= d.ByteSize;
            d.Dispose();
            _cache.Remove(texture);
            _lru.Remove(texture);
        }

        private void TrimToBudget()
        {
            while (_used > _budgetBytes && _lru.Count > 1)
            {
                var oldest = _lru.First.Value;
                _log.Trace("cache", $"evicting '{oldest.name}' ({_used / (1024 * 1024)} MB used)");
                Evict(oldest);
            }
        }

        public void Dispose()
        {
            foreach (var kv in _cache) kv.Value.Dispose();
            _cache.Clear();
            _lru.Clear();
            _used = 0;
        }

        // ------------------------------------------------------------------ decoding

        private ATODecodedTexture Decode(Texture2D texture, bool decodeNormal)
        {
            var w = texture.width;
            var h = texture.height;

            var rt = RenderTexture.GetTemporary(new RenderTextureDescriptor(w, h, RenderTextureFormat.ARGBFloat, 0)
            {
                sRGB = false,
                autoGenerateMips = false,
                useMipMap = false,
            });

            var prevActive = RenderTexture.active;
            Texture2D readback = null;
            try
            {
                // EN: Blit performs the sRGB -> linear conversion when the source is an sRGB texture.
                // ZH: 当源贴图为 sRGB 时，Blit 会自动做 sRGB -> 线性转换。
                Graphics.Blit(texture, rt);

                readback = new Texture2D(w, h, TextureFormat.RGBAFloat, false, true)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
                RenderTexture.active = rt;
                readback.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                readback.Apply(false, false);

                var raw = readback.GetRawTextureData<float4>();
                var pixels = new NativeArray<half4>(w * h, Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory);

                var normalMode = decodeNormal ? DetectNormalEncoding(texture) : NormalEncoding.None;
                var hasAlpha = false;
                var flat = true;
                var first = raw.Length > 0 ? raw[0] : float4.zero;
                var min = first;
                var max = first;

                for (var i = 0; i < pixels.Length; i++)
                {
                    var c = raw[i];
                    if (normalMode != NormalEncoding.None) c = DecodeNormalPixel(c, normalMode);
                    else if (c.w < 0.999f) hasAlpha = true;

                    if (flat && math.any(math.abs(c - first) > 1e-4f)) flat = false;
                    min = math.min(min, c);
                    max = math.max(max, c);
                    pixels[i] = new half4((half)c.x, (half)c.y, (half)c.z, (half)c.w);
                }

                var varying = (max - min) > 1.5f / 255f;

                _log.Trace("decode",
                    $"'{texture.name}' {w}x{h} normal={normalMode} alpha={hasAlpha} flat={flat} " +
                    $"varying=({varying.x},{varying.y},{varying.z},{varying.w})");

                return new ATODecodedTexture
                {
                    Width = w,
                    Height = h,
                    Pixels = pixels,
                    IsNormalDecoded = decodeNormal,
                    HasAlphaContent = hasAlpha,
                    IsFlatColor = flat,
                    FlatColor = first,
                    ChannelVarying = varying,
                };
            }
            finally
            {
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
                if (readback != null) UnityEngine.Object.DestroyImmediate(readback);
            }
        }

        private enum NormalEncoding
        {
            /// <summary>EN: Not a normal map. ZH: 非法线贴图。</summary>
            None,

            /// <summary>EN: X in R, Y in G, Z in B. ZH: X 在 R、Y 在 G、Z 在 B。</summary>
            XYZ,

            /// <summary>EN: X in A, Y in G (DXT5nm). ZH: X 在 A、Y 在 G（DXT5nm）。</summary>
            AG,

            /// <summary>EN: X in R, Y in G, Z reconstructed (BC5). ZH: X 在 R、Y 在 G、Z 重建（BC5）。</summary>
            RG,
        }

        private static NormalEncoding DetectNormalEncoding(Texture2D texture)
        {
            switch (texture.format)
            {
                case TextureFormat.DXT5:
                case TextureFormat.DXT5Crunched:
                    return NormalEncoding.AG;
                case TextureFormat.BC5:
                    return NormalEncoding.RG;
                case TextureFormat.ASTC_4x4:
                case TextureFormat.ASTC_5x5:
                case TextureFormat.ASTC_6x6:
                case TextureFormat.ASTC_8x8:
                case TextureFormat.ASTC_10x10:
                case TextureFormat.ASTC_12x12:
                    // EN: Unity stores mobile normal maps as XY in RG(A) too; treat alpha as X when present.
                    // ZH: Unity 在移动端同样把法线存成 RG(A)，存在 alpha 时按 X 处理。
                    return NormalEncoding.AG;
                default:
                    return NormalEncoding.XYZ;
            }
        }

        private static float4 DecodeNormalPixel(float4 c, NormalEncoding mode)
        {
            float x, y;
            switch (mode)
            {
                case NormalEncoding.AG:
                    // EN: Unity's UnpackNormalDXT5nm(): x = a, y = g. ZH: Unity 的 UnpackNormalDXT5nm()：x = a、y = g。
                    x = c.w * 2f - 1f;
                    y = c.y * 2f - 1f;
                    break;
                case NormalEncoding.RG:
                    x = c.x * 2f - 1f;
                    y = c.y * 2f - 1f;
                    break;
                default:
                    x = c.x * 2f - 1f;
                    y = c.y * 2f - 1f;
                    var z0 = c.z * 2f - 1f;
                    var v0 = math.normalizesafe(new float3(x, y, z0), new float3(0, 0, 1));
                    return new float4(v0, 1f);
            }

            var z = math.sqrt(math.max(0f, 1f - x * x - y * y));
            var v = math.normalizesafe(new float3(x, y, z), new float3(0, 0, 1));
            return new float4(v, 1f);
        }

        /// <summary>
        /// EN: Returns true when the texture importer marks the asset as a normal map.
        /// ZH: 当导入器把资产标记为法线贴图时返回 true。
        /// </summary>
        public static bool IsImportedAsNormalMap(Texture2D texture)
        {
            var path = AssetDatabase.GetAssetPath(texture);
            if (string.IsNullOrEmpty(path)) return false;
            return AssetImporter.GetAtPath(path) is TextureImporter ti && ti.textureType == TextureImporterType.NormalMap;
        }

        /// <summary>
        /// EN: Returns true when the texture is stored in an sRGB (gamma) format.
        /// ZH: 当贴图以 sRGB（gamma）方式存储时返回 true。
        /// </summary>
        public static bool IsSRGB(Texture2D texture)
        {
            var path = AssetDatabase.GetAssetPath(texture);
            if (!string.IsNullOrEmpty(path) && AssetImporter.GetAtPath(path) is TextureImporter ti)
                return ti.sRGBTexture && ti.textureType != TextureImporterType.NormalMap;

            return GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat);
        }
    }
}
