// ============================================================================
// TextureIO.cs — 贴图导入设置签名、像素读取与分类 / Texture import signature,
//                 pixel reading, and classification
// (EN) Computes the import-settings signature (dedup identity), reads pixels
//      (including non-readable textures), hashes pixel content, and classifies
//      textures (opaque/transparent/normal/grayscale). All results are cached.
// (ZH) 计算导入设置签名（去重标识）、读取像素（含不可读贴图）、哈希像素内容，
//      并分类贴图（不透明/透明/法线/灰度）。全部结果带缓存。
// ============================================================================

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    public static class ATOTextureIO
    {
        // 缓存 / caches (keyed by texture instance)
        private static readonly Dictionary<Texture2D, string> _importSigCache = new Dictionary<Texture2D, string>();
        private static readonly Dictionary<Texture2D, string> _pixelSigCache = new Dictionary<Texture2D, string>();
        private static readonly Dictionary<Texture2D, bool> _hasAlphaCache = new Dictionary<Texture2D, bool>();
        private static readonly Dictionary<Texture2D, bool> _grayCache = new Dictionary<Texture2D, bool>();

        public static void ClearCache()
        {
            _importSigCache.Clear();
            _pixelSigCache.Clear();
            _hasAlphaCache.Clear();
            _grayCache.Clear();
        }

        // ---------------------------------------------------------------------
        // 导入设置签名 / import settings signature
        // ---------------------------------------------------------------------
        public static string GetImportSignature(Texture2D tex)
        {
            if (_importSigCache.TryGetValue(tex, out var cached)) return cached;

            var sb = new System.Text.StringBuilder();
            var path = AssetDatabase.GetAssetPath(tex);
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null)
            {
                sb.Append("type=").Append(importer.textureType).Append(';');
                sb.Append("srgb=").Append(importer.sRGBTexture).Append(';');
                sb.Append("wrap=").Append(importer.wrapModeU).Append(',').Append(importer.wrapModeV).Append(',').Append(importer.wrapModeW).Append(';');
                sb.Append("filter=").Append(importer.filterMode).Append(';');
                sb.Append("mipmap=").Append(importer.mipmapEnabled).Append(';');
                sb.Append("streaming=").Append(importer.streamingMipmaps).Append(';');
                sb.Append("compression=").Append(importer.textureCompression).Append(';');
                sb.Append("maxsize=").Append(importer.maxTextureSize).Append(';');
                sb.Append("crunch=").Append(importer.crunchedCompression).Append(';');
                sb.Append("alphaSrc=").Append(importer.alphaSource).Append(';');
                sb.Append("alphaIsTransp=").Append(importer.alphaIsTransparency).Append(';');
                sb.Append("npot=").Append(importer.npotScale).Append(';');
                sb.Append("readwrite=").Append(importer.isReadable).Append(';');

                // 平台覆盖（影响 GPU 格式）/ platform overrides (affect GPU format)
                foreach (var platform in new[] { "Standalone", "Android", "iPhone" })
                {
                    var ps = importer.GetPlatformTextureSettings(platform);
                    sb.Append(platform).Append(":fmt=").Append(ps.format)
                      .Append(",size=").Append(ps.maxTextureSize)
                      .Append(",compress=").Append(ps.textureCompression)
                      .Append(",crunch=").Append(ps.crunchedCompression).Append(';');
                }
            }
            else
            {
                // 无导入器（运行时生成贴图等）：回退到贴图自身属性 / fallback to texture's own settings
                sb.Append("dim=").Append(tex.dimension).Append(';');
                sb.Append("format=").Append(tex.format).Append(';');
                sb.Append("filter=").Append(tex.filterMode).Append(';');
                sb.Append("wrap=").Append(tex.wrapMode).Append(';');
                sb.Append("mipmap=").Append(tex.mipmapCount > 1).Append(';');
            }

            _importSigCache[tex] = sb.ToString();
            return _importSigCache[tex];
        }

        // ---------------------------------------------------------------------
        // 像素内容签名 / pixel content signature (FNV-1a 64-bit over RGBA bytes)
        // ---------------------------------------------------------------------
        public static string GetPixelSignature(Texture2D tex)
        {
            if (_pixelSigCache.TryGetValue(tex, out var cached)) return cached;
            var px = ReadPixels(tex);
            ulong hash = 14695981039346656037UL; // FNV offset basis
            foreach (var c in px)
            {
                hash = Fnv(hash, c.r);
                hash = Fnv(hash, c.g);
                hash = Fnv(hash, c.b);
                hash = Fnv(hash, c.a);
            }
            var result = hash.ToString("X16");
            _pixelSigCache[tex] = result;
            return result;
        }

        private static ulong Fnv(ulong h, float f)
        {
            var bytes = System.BitConverter.GetBytes(f);
            foreach (var b in bytes)
            {
                h ^= b;
                h *= 1099511628211UL;
            }
            return h;
        }

        // ---------------------------------------------------------------------
        // 像素读取（含不可读贴图）/ read pixels (including non-readable textures)
        // ---------------------------------------------------------------------
        public static Color[] ReadPixels(Texture2D tex)
        {
            if (tex.isReadable)
                return tex.GetPixels();

            var w = tex.width;
            var h = tex.height;
            var linear = !IsSrgb(tex);
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
            try
            {
                Graphics.Blit(tex, rt);
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var copy = new Texture2D(w, h, TextureFormat.RGBA32, false, linear);
                copy.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                copy.Apply();
                RenderTexture.active = prev;
                var result = copy.GetPixels();
                Object.DestroyImmediate(copy);
                return result;
            }
            finally
            {
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        private static bool IsSrgb(Texture2D tex)
        {
            var path = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(path)) return true;
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            return importer == null || importer.sRGBTexture;
        }

        // ---------------------------------------------------------------------
        // 区域读取（内存友好）/ region read (memory-friendly)
        // (EN) Reads a pixel region. Readable textures use GetPixels(x,y,w,h);
        //      non-readable textures are blit once and cached (single entry).
        // (ZH) 读取像素区域。可读贴图用 GetPixels(x,y,w,h)；不可读贴图只 blit 一次并缓存（单条目）。
        // ---------------------------------------------------------------------
        private static Texture2D _lastBlitTex;
        private static Color[] _lastBlitPixels;

        public static Color[] ReadRegion(Texture2D tex, int x, int y, int w, int h)
        {
            x = Mathf.Clamp(x, 0, tex.width - 1);
            y = Mathf.Clamp(y, 0, tex.height - 1);
            w = Mathf.Min(w, tex.width - x);
            h = Mathf.Min(h, tex.height - y);
            if (w <= 0 || h <= 0) return new Color[0];

            if (tex.isReadable)
                return tex.GetPixels(x, y, w, h);

            // 不可读：blit 全图一次，单条目缓存 / non-readable: blit once, single-entry cache
            if (_lastBlitTex != tex)
            {
                _lastBlitPixels = null;
                _lastBlitTex = tex;
                _lastBlitPixels = ReadPixels(tex);
            }

            var result = new Color[w * h];
            int tw = tex.width;
            for (int yy = 0; yy < h; yy++)
                System.Array.Copy(_lastBlitPixels, (y + yy) * tw + x, result, yy * w, w);
            return result;
        }

        // ---------------------------------------------------------------------
        // 分类 / classification
        // ---------------------------------------------------------------------
        /// <summary>(EN) Classify a texture ref (opaque/transparent/normal/grayscale). (ZH) 分类贴图引用。</summary>
        public static ATOTextureClass Classify(ATOTextureRef texRef)
        {
            switch (texRef.Usage)
            {
                case ATOTextureUsage.NormalMap:
                    texRef.Classification = ATOTextureClass.Normal;
                    break;
                case ATOTextureUsage.Mask:
                case ATOTextureUsage.Grayscale:
                    texRef.Classification = ATOTextureClass.Grayscale;
                    break;
                default:
                    texRef.Classification = HasAlpha(texRef.Texture) ? ATOTextureClass.Transparent : ATOTextureClass.Opaque;
                    break;
            }
            return texRef.Classification;
        }

        public static bool HasAlpha(Texture2D tex)
        {
            if (_hasAlphaCache.TryGetValue(tex, out var cached)) return cached;
            var px = ReadPixels(tex);
            foreach (var c in px)
            {
                if (c.a < 0.999f) { _hasAlphaCache[tex] = true; return true; }
            }
            _hasAlphaCache[tex] = false;
            return false;
        }

        /// <summary>(EN) True if all pixels are grayscale (R≈G≈B). (ZH) 所有像素均为灰度（R≈G≈B）时为真。</summary>
        public static bool IsGrayscale(Texture2D tex)
        {
            if (_grayCache.TryGetValue(tex, out var cached)) return cached;
            var px = ReadPixels(tex);
            foreach (var c in px)
            {
                if (Mathf.Abs(c.r - c.g) > 1e-4f || Mathf.Abs(c.g - c.b) > 1e-4f)
                { _grayCache[tex] = false; return false; }
            }
            _grayCache[tex] = true;
            return true;
        }
    }
}
