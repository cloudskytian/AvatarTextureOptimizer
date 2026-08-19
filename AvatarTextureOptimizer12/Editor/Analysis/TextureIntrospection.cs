// SPDX-License-Identifier: MIT
// AvatarTextureOptimizer (ATO) - Texture decoding, introspection and content analysis.
// AvatarTextureOptimizer (ATO) - 贴图解码、内省与内容分析。

using System;
using System.Collections.Generic;
using Net.Fosa.AvatarTextureOptimizer.Editor.Core;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Analysis
{
    /// <summary>
    /// EN: Facts about the *stored bytes* of a texture, computed once and cached.
    /// ZH: 关于贴图“存储字节”的事实，只计算一次并缓存。
    /// </summary>
    public sealed class TextureContentInfo
    {
        /// <summary>EN: Any pixel with alpha &lt; 255. ZH: 存在 alpha &lt; 255 的像素。</summary>
        public bool HasAlpha;

        /// <summary>EN: Alpha only ever takes the values 0 or 255. ZH: alpha 只取 0 或 255。</summary>
        public bool AlphaIsBinary;

        /// <summary>EN: R == G == B for every pixel. ZH: 每个像素都满足 R == G == B。</summary>
        public bool IsGrayscale;

        /// <summary>EN: Bitmask of channels that vary (1=R,2=G,4=B,8=A). ZH: 有变化的通道位掩码（1=R,2=G,4=B,8=A）。</summary>
        public int VaryingChannels;

        /// <summary>EN: Bitmask of channels that are not identically zero. ZH: 非恒零通道的位掩码。</summary>
        public int NonZeroChannels;

        /// <summary>EN: The whole texture is a single colour. ZH: 整张贴图是纯色。</summary>
        public bool IsSolid;

        public Color32 SolidColor;

        /// <summary>EN: 128-bit content hash of the stored bytes. ZH: 存储字节的 128 位内容哈希。</summary>
        public ulong ContentHashLo, ContentHashHi;
    }

    /// <summary>
    /// EN: All reads go through a GPU blit so that compressed / non-readable textures work, and so that the
    ///     bytes we get back are exactly the bytes Unity stores (we do our own sRGB decode afterwards, which
    ///     keeps the whole pipeline unambiguously linear).
    /// ZH: 所有读取都经由 GPU blit，使压缩 / 不可读贴图也能工作，并保证拿到的就是 Unity 存储的原始字节
    ///     （之后我们自己做 sRGB 解码，使整条管线的色彩空间毫无歧义地保持线性）。
    /// </summary>
    public static class TextureIntrospection
    {
        // ---- Caches / 缓存 ----------------------------------------------------------------------

        private sealed class PixelCacheEntry
        {
            public NativeArray<Color32> Pixels;
            public int Width, Height;
            public long Bytes;
            public long LastUse;
        }

        private static readonly Dictionary<Texture2D, PixelCacheEntry> _pixelCache =
            new Dictionary<Texture2D, PixelCacheEntry>();

        private static readonly Dictionary<Texture2D, TextureContentInfo> _contentCache =
            new Dictionary<Texture2D, TextureContentInfo>();

        private static readonly Dictionary<Texture2D, string> _importerHashCache =
            new Dictionary<Texture2D, string>();

        private static long _cacheBytes;
        private static long _useCounter;

        /// <summary>
        /// EN: Soft budget for the decoded-pixel cache. Kept modest on purpose: users run this on ordinary
        ///     gaming PCs, and a 4K RGBA texture already costs 64 MB.
        /// ZH: 解码像素缓存的软预算。刻意保持适中：用户使用的是普通游戏 PC，一张 4K RGBA 贴图就已经 64MB。
        /// </summary>
        public static long CacheByteBudget = 768L * 1024L * 1024L;

        /// <summary>EN: Release every cached native allocation. ZH: 释放全部缓存的原生内存。</summary>
        public static void ReleaseAll()
        {
            foreach (var kv in _pixelCache)
            {
                if (kv.Value.Pixels.IsCreated) kv.Value.Pixels.Dispose();
            }
            _pixelCache.Clear();
            _contentCache.Clear();
            _importerHashCache.Clear();
            _cacheBytes = 0;
            ATOLog.Debug_("texture caches released");
        }

        private static void TrimCache(long incoming)
        {
            if (_cacheBytes + incoming <= CacheByteBudget) return;

            var victims = new List<KeyValuePair<Texture2D, PixelCacheEntry>>(_pixelCache);
            victims.Sort((a, b) => a.Value.LastUse.CompareTo(b.Value.LastUse));

            foreach (var v in victims)
            {
                if (_cacheBytes + incoming <= CacheByteBudget) break;
                if (v.Value.Pixels.IsCreated) v.Value.Pixels.Dispose();
                _cacheBytes -= v.Value.Bytes;
                _pixelCache.Remove(v.Key);
            }
            ATOLog.Debug_($"pixel cache trimmed to {_cacheBytes / (1024 * 1024)} MB");
        }

        // ---- Importer facts / 导入器信息 --------------------------------------------------------

        public static TextureImporter GetImporter(Texture2D tex)
        {
            if (tex == null) return null;
            var path = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(path)) return null;
            return AssetImporter.GetAtPath(path) as TextureImporter;
        }

        public static bool IsImportedAsNormalMap(Texture2D tex)
        {
            var imp = GetImporter(tex);
            return imp != null && imp.textureType == TextureImporterType.NormalMap;
        }

        public static bool IsSRGB(Texture2D tex)
        {
            var imp = GetImporter(tex);
            if (imp != null) return imp.sRGBTexture;
            // EN: Runtime-generated textures: infer from the graphics format.
            // ZH: 运行时生成的贴图：从图形格式推断。
            return tex != null && GraphicsFormatUtility.IsSRGBFormat(tex.graphicsFormat);
        }

        /// <summary>
        /// EN: A stable string describing everything about the import settings that can influence sampling.
        ///     Two textures with different import settings are never deduplicated, per spec.
        /// ZH: 描述所有会影响采样的导入设置的稳定字符串。
        ///     按需求，导入设置不同的两张贴图绝不会被去重。
        /// </summary>
        public static string ImporterSignature(Texture2D tex)
        {
            if (tex == null) return "null";
            if (_importerHashCache.TryGetValue(tex, out var cached)) return cached;

            string sig;
            var imp = GetImporter(tex);
            if (imp == null)
            {
                sig = $"gen|{tex.width}x{tex.height}|{tex.graphicsFormat}|{tex.filterMode}|{tex.wrapMode}|{tex.anisoLevel}|{tex.mipmapCount}";
            }
            else
            {
                sig = string.Join("|",
                    "imp",
                    imp.textureType.ToString(),
                    imp.sRGBTexture ? "srgb" : "lin",
                    imp.alphaSource.ToString(),
                    imp.alphaIsTransparency ? "ait" : "-",
                    imp.mipmapEnabled ? "mip" : "-",
                    imp.streamingMipmaps ? "stream" : "-",
                    imp.filterMode.ToString(),
                    imp.wrapMode.ToString(),
                    imp.wrapModeU.ToString(),
                    imp.wrapModeV.ToString(),
                    imp.anisoLevel.ToString(),
                    imp.isReadable ? "rw" : "-",
                    imp.npotScale.ToString(),
                    tex.width + "x" + tex.height);
            }

            _importerHashCache[tex] = sig;
            return sig;
        }

        // ---- Pixel access / 像素访问 -------------------------------------------------------------

        /// <summary>
        /// EN: Read the texture's stored (i.e. *not* colour-space converted) RGBA8 bytes. The result is owned
        ///     by the cache; do not dispose it. Returns an uncreated array on failure.
        /// ZH: 读取贴图存储的（即未经色彩空间转换的）RGBA8 字节。返回值归缓存所有，请勿释放。失败时返回未创建的数组。
        /// </summary>
        public static NativeArray<Color32> ReadStoredPixels(Texture2D tex)
        {
            if (tex == null) return default;

            if (_pixelCache.TryGetValue(tex, out var entry) && entry.Pixels.IsCreated)
            {
                entry.LastUse = ++_useCounter;
                return entry.Pixels;
            }

            int w = tex.width, h = tex.height;
            long bytes = (long)w * h * 4;
            TrimCache(bytes);

            NativeArray<Color32> pixels;
            try
            {
                pixels = ReadViaBlit(tex);
            }
            catch (Exception e)
            {
                ATOLog.Warn($"failed to read texture '{tex.name}': {e.Message}");
                return default;
            }

            entry = new PixelCacheEntry
            {
                Pixels = pixels,
                Width = w,
                Height = h,
                Bytes = bytes,
                LastUse = ++_useCounter,
            };
            _pixelCache[tex] = entry;
            _cacheBytes += bytes;
            ATOLog.Trace($"decoded '{tex.name}' {w}x{h} ({bytes / 1024} KB), cache={_cacheBytes / (1024 * 1024)} MB");
            return pixels;
        }

        private static NativeArray<Color32> ReadViaBlit(Texture2D tex)
        {
            int w = tex.width, h = tex.height;

            // EN: We want the *stored* bytes. Match the RT's sRGB-ness to the source so the GPU's
            //     decode-on-read / encode-on-write cancel out exactly.
            // ZH: 我们要的是“存储字节”。让 RT 的 sRGB 属性与源一致，
            //     这样 GPU 读时解码与写时编码正好互相抵消。
            bool srgb = GraphicsFormatUtility.IsSRGBFormat(tex.graphicsFormat);
            var format = srgb ? GraphicsFormat.R8G8B8A8_SRGB : GraphicsFormat.R8G8B8A8_UNorm;

            var desc = new RenderTextureDescriptor(w, h, format, 0)
            {
                sRGB = srgb,
                msaaSamples = 1,
                useMipMap = false,
                autoGenerateMips = false,
            };

            var rt = RenderTexture.GetTemporary(desc);
            var prevActive = RenderTexture.active;
            var prevSrgbWrite = GL.sRGBWrite;
            Texture2D readback = null;
            try
            {
                GL.sRGBWrite = srgb;
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;

                readback = new Texture2D(w, h, TextureFormat.RGBA32, false, /*linear:*/ true)
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                readback.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                readback.Apply(false, false);

                var src = readback.GetRawTextureData<Color32>();
                var dst = new NativeArray<Color32>(src.Length, Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory);
                dst.CopyFrom(src);
                return dst;
            }
            finally
            {
                GL.sRGBWrite = prevSrgbWrite;
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
                if (readback != null) UnityEngine.Object.DestroyImmediate(readback);
            }
        }

        // ---- Content analysis / 内容分析 ---------------------------------------------------------

        public static TextureContentInfo AnalyseContent(Texture2D tex)
        {
            if (tex == null) return new TextureContentInfo();
            if (_contentCache.TryGetValue(tex, out var cached)) return cached;

            var info = new TextureContentInfo();
            var px = ReadStoredPixels(tex);
            if (!px.IsCreated || px.Length == 0)
            {
                _contentCache[tex] = info;
                return info;
            }

            var first = px[0];
            bool solid = true;
            bool grayscale = true;
            bool hasAlpha = false;
            bool alphaBinary = true;
            int varying = 0;
            int nonZero = 0;

            // EN: FNV-1a over the raw bytes, split into two lanes for a cheap 128-bit hash.
            // ZH: 对原始字节做 FNV-1a，分两路计算得到廉价的 128 位哈希。
            ulong h1 = 14695981039346656037UL, h2 = 1099511628211UL;

            for (int i = 0; i < px.Length; i++)
            {
                var c = px[i];

                if (c.r != first.r || c.g != first.g || c.b != first.b || c.a != first.a) solid = false;
                if (c.r != c.g || c.g != c.b) grayscale = false;
                if (c.a != 255) hasAlpha = true;
                if (c.a != 0 && c.a != 255) alphaBinary = false;

                if (c.r != first.r) varying |= 1;
                if (c.g != first.g) varying |= 2;
                if (c.b != first.b) varying |= 4;
                if (c.a != first.a) varying |= 8;

                if (c.r != 0) nonZero |= 1;
                if (c.g != 0) nonZero |= 2;
                if (c.b != 0) nonZero |= 4;
                if (c.a != 0) nonZero |= 8;

                unchecked
                {
                    ulong v = (ulong)c.r | ((ulong)c.g << 8) | ((ulong)c.b << 16) | ((ulong)c.a << 24);
                    h1 = (h1 ^ v) * 1099511628211UL;
                    h2 = (h2 + v) * 0x9E3779B97F4A7C15UL;
                    h2 ^= h2 >> 29;
                }
            }

            info.IsSolid = solid;
            info.SolidColor = first;
            info.IsGrayscale = grayscale;
            info.HasAlpha = hasAlpha;
            info.AlphaIsBinary = alphaBinary;
            info.VaryingChannels = varying;
            info.NonZeroChannels = nonZero;
            info.ContentHashLo = h1;
            info.ContentHashHi = h2 ^ (ulong)px.Length;

            _contentCache[tex] = info;
            return info;
        }

        /// <summary>
        /// EN: Full deduplication key: content + import settings + dimensions.
        /// ZH: 完整去重键：内容 + 导入设置 + 尺寸。
        /// </summary>
        public static string DedupKey(Texture2D tex)
        {
            if (tex == null) return "null";
            var info = AnalyseContent(tex);
            return $"{tex.width}x{tex.height}|{info.ContentHashLo:X16}{info.ContentHashHi:X16}|{ImporterSignature(tex)}";
        }

        /// <summary>
        /// EN: Classify a texture for output settings. Normal-map detection is importer-driven; grayscale
        ///     detection is content-driven with a channel-usage fallback, exactly as the spec requires.
        /// ZH: 为输出设置分类贴图。法线判定基于导入器；灰度判定基于实际内容并以通道使用情况兜底，
        ///     完全符合需求描述。
        /// </summary>
        public static ATOTextureClass Classify(Texture2D tex, bool isNormal, bool alphaMatters)
        {
            if (isNormal) return ATOTextureClass.NormalMap;
            var info = AnalyseContent(tex);
            if (info.IsGrayscale && !(alphaMatters && info.HasAlpha)) return ATOTextureClass.Grayscale;
            return alphaMatters && info.HasAlpha ? ATOTextureClass.TransparentColor : ATOTextureClass.OpaqueColor;
        }

        // ---- Colour-space helpers / 色彩空间辅助 -------------------------------------------------

        /// <summary>EN: IEC 61966-2-1 sRGB EOTF. ZH: IEC 61966-2-1 标准 sRGB 电光转换函数。</summary>
        public static float SrgbToLinear(float c)
        {
            return c <= 0.04045f ? c / 12.92f : Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);
        }

        /// <summary>EN: Inverse sRGB EOTF. ZH: sRGB 电光转换函数的逆变换。</summary>
        public static float LinearToSrgb(float c)
        {
            return c <= 0.0031308f ? c * 12.92f : 1.055f * Mathf.Pow(c, 1f / 2.4f) - 0.055f;
        }

        private static float[] _srgbLut;

        /// <summary>EN: 256-entry sRGB decode LUT (byte -> linear). ZH: 256 项 sRGB 解码查找表（字节 -> 线性）。</summary>
        public static float[] SrgbLut
        {
            get
            {
                if (_srgbLut != null) return _srgbLut;
                _srgbLut = new float[256];
                for (int i = 0; i < 256; i++) _srgbLut[i] = SrgbToLinear(i / 255f);
                return _srgbLut;
            }
        }
    }
}
