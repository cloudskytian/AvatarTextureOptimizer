using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>Import settings snapshot; part of the dedup key (different settings = different texture). / 导入设置快照，参与去重键。</summary>
    internal class ImportInfo
    {
        internal TextureImporter importer;
        internal bool sRGB = true;
        internal bool normalMap;
        internal TextureImporterCompression compression = TextureImporterCompression.Compressed;
        internal int compressionQuality = 50;
        internal bool mipmaps = true;
        internal string signature = "";

        internal static ImportInfo Capture(Texture2D tex)
        {
            var info = new ImportInfo();
            var path = AssetDatabase.GetAssetPath(tex);
            if (!string.IsNullOrEmpty(path))
            {
                if (AssetImporter.GetAtPath(path) is TextureImporter imp)
                {
                    info.importer = imp;
                    info.sRGB = imp.sRGBTexture;
                    info.normalMap = imp.textureType == TextureImporterType.NormalMap;
                    info.compression = imp.textureCompression;
                    info.compressionQuality = imp.compressionQuality;
                    info.mipmaps = imp.mipmapEnabled;
                    info.signature = $"{imp.textureType}|{imp.sRGBTexture}|{imp.filterMode}|{imp.wrapMode}|{imp.mipmapEnabled}|{imp.textureCompression}|{imp.compressionQuality}|{imp.maxTextureSize}|{imp.npotScale}|{imp.aniso}|{imp.alphaIsTransparency}|{imp.mipmapFilter}|{imp.streamingMipmaps}";
                }
                else
                {
                    // Runtime-generated texture without importer / 无导入器的生成贴图
                    info.sRGB = !GraphicsFormatIsLinear(tex);
                    info.signature = "noinporter|" + tex.format;
                }
            }
            else
            {
                info.sRGB = !GraphicsFormatIsLinear(tex);
                info.signature = "temp|" + tex.format;
            }

            return info;
        }

        private static bool GraphicsFormatIsLinear(Texture2D tex)
        {
            try
            {
                return !UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsSRGBFormat(tex.graphicsFormat);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Texture pixel access + import info with a memory budget (LRU eviction), and content-based
    /// dedup (pixels + import settings). / 贴图像素访问（内存预算+LRU）、导入信息、内容去重。
    /// </summary>
    internal class TextureStore : IDisposable
    {
        private class Entry
        {
            internal Color32[] pixels;
            internal long lastUse;
        }

        private const long BudgetBytes = 384L * 1024 * 1024; // raw RGBA cache cap / 原始像素缓存上限
        private long _usedBytes;
        private long _clock;
        private bool _disposed;

        private readonly Dictionary<Texture2D, Entry> _cache = new Dictionary<Texture2D, Entry>();
        private readonly Dictionary<Texture2D, ImportInfo> _importCache = new Dictionary<Texture2D, ImportInfo>();

        internal int CacheCount => _cache.Count;

        internal ImportInfo GetImportInfo(Texture2D tex)
        {
            if (_importCache.TryGetValue(tex, out var info)) return info;
            info = ImportInfo.Capture(tex);
            _importCache[tex] = info;
            return info;
        }

        /// <summary>Raw stored pixels (readable path or GPU blit). Cached, LRU-evicted. / 原始像素（带LRU缓存）。</summary>
        internal Color32[] GetPixels(Texture2D tex)
        {
            if (_cache.TryGetValue(tex, out var e))
            {
                e.lastUse = ++_clock;
                return e.pixels;
            }

            var pixels = ReadPixels(tex);
            var bytes = (long)pixels.Length * 4;
            if (bytes <= BudgetBytes)
            {
                EvictIfNeeded(bytes);
                _cache[tex] = new Entry { pixels = pixels, lastUse = ++_clock };
                _usedBytes += bytes;
            }

            return pixels;
        }

        private Color32[] ReadPixels(Texture2D tex)
        {
            if (tex.isReadable)
            {
                try { return tex.GetPixels32(); }
                catch { /* fallthrough to GPU / 回退GPU */ }
            }

            var info = GetImportInfo(tex);
            return Gfx.ReadPixelsRaw(tex, info.sRGB);
        }

        private void EvictIfNeeded(long incoming)
        {
            while (_usedBytes + incoming > BudgetBytes && _cache.Count > 1)
            {
                var oldest = _cache.OrderBy(kv => kv.Value.lastUse).First();
                _cache.Remove(oldest.Key);
                _usedBytes -= (long)oldest.Value.pixels.Length * 4;
                ATOLog.Verbose($"pixel cache evicted {oldest.Key.name} / 像素缓存淘汰");
            }
        }

        // ------------------------------------------------------------------ dedup
        /// <summary>
        /// Content-based dedup: same pixels + same import settings ⇒ one canonical instance.
        /// Returns duplicate→canonical map; whitelisted canonical ⇒ result is whitelisted too
        /// (handled by caller). / 内容去重：像素+导入设置相同 ⇒ 合并为一个实例，返回映射。
        /// </summary>
        internal Dictionary<Texture2D, Texture2D> Dedup(IEnumerable<Texture2D> textures)
        {
            var map = new Dictionary<Texture2D, Texture2D>();
            var byHash = new Dictionary<string, Texture2D>();

            foreach (var tex in textures.Distinct().Where(t => t != null))
            {
                string hash;
                try
                {
                    var info = GetImportInfo(tex);
                    var pixels = GetPixels(tex);
                    hash = HashPixels(pixels, tex.width, tex.height, info.signature);
                }
                catch (Exception e)
                {
                    ATOLog.Warning($"dedup skipped for '{tex.name}': {e.Message}");
                    continue;
                }

                if (byHash.TryGetValue(hash, out var canonical))
                {
                    map[tex] = canonical;
                    ATOLog.Verbose($"dedup: '{tex.name}' → '{canonical.name}'");
                }
                else
                {
                    byHash[hash] = tex;
                }
            }

            return map;
        }

        private static string HashPixels(Color32[] pixels, int w, int h, string signature)
        {
            using var sha = SHA256.Create();
            var bytes = new byte[pixels.Length * 4];
            Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
            var h1 = sha.ComputeHash(bytes);
            var h2 = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes($"{w}x{h}|{signature}"));
            // 128-bit key is plenty / 128位足够
            return Convert.ToBase64String(h1, 0, 16) + Convert.ToBase64String(h2, 0, 8);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cache.Clear();
            _importCache.Clear();
            _usedBytes = 0;
        }
    }
}
