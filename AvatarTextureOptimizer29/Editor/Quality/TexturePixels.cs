// GPU readback pixel cache with an LRU byte budget (memory safety for real avatars).
// GPU 回读像素缓存 + LRU 字节预算（真实 Avatar 内存安全）。
//
// Readback pattern verified from avatar-compressor's TextureReadback:
//   blit to sRGB RT with GL.sRGBWrite=true  -> round-trips raw stored sRGB bytes
//   blit to linear RT                       -> raw stored bytes for linear textures
// Normal map channel layouts: BC5=RG, DXT5/BC7=DXTnm(AG), uncompressed=RGB
// (EditorUtility.CompressTexture does NOT swizzle; see docs/ThirdPartyNotes.md).

using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace net.fosa.ato.editor
{
    /// <summary>Storage layout of a decoded normal map's XY. / 法线贴图通道布局。</summary>
    internal enum NormalLayout { RG, AG, RGB }

    internal class CachedPixels
    {
        internal Texture2D texture;
        internal Color32[] pixels;   // raw stored bytes (see class comment) / 原始存储字节
        internal int width, height;
        internal bool srgb;          // texture flagged sRGB / 是否 sRGB
        internal NormalLayout normalLayout;

        // content analysis (lazy) / 内容分析（惰性）
        internal bool? _hasAlpha, _grayscale, _pureColor;
        internal Color32 _pureColorValue;
    }

    internal static class TexturePixels
    {
        /// <summary>LRU byte budget; over-budget oldest entries are dropped.
        /// LRU 字节预算，超预算淘汰最旧条目。</summary>
        private const long Budget = 768L * 1024 * 1024; // 768 MB; real avatars fit, larger stay cached on disk? re-read
        private static readonly LinkedList<CachedPixels> Lru = new LinkedList<CachedPixels>();
        private static readonly Dictionary<Texture2D, LinkedListNode<CachedPixels>> Map =
            new Dictionary<Texture2D, LinkedListNode<CachedPixels>>();
        private static long _used;

        internal static void DisposeAll()
        {
            Map.Clear();
            Lru.Clear();
            _used = 0;
        }

        internal static CachedPixels Get(Texture2D tex, bool isNormal = false)
        {
            if (tex == null) return null;
            if (Map.TryGetValue(tex, out var node))
            {
                Lru.Remove(node);
                Lru.AddFirst(node);
                return node.Value;
            }

            var cp = Readback(tex, isNormal);
            if (cp == null) return null;
            var n = Lru.AddFirst(cp);
            Map[tex] = n;
            _used += (long)cp.width * cp.height * 4;
            Evict();
            return cp;
        }

        private static void Evict()
        {
            while (_used > Budget && Lru.Count > 1)
            {
                var last = Lru.Last;
                Lru.RemoveLast();
                Map.Remove(last.Value.texture);
                _used -= (long)last.Value.width * last.Value.height * 4;
            }
        }

        private static CachedPixels Readback(Texture2D tex, bool isNormal)
        {
            var prevActive = RenderTexture.active;
            var prevSrgbWrite = GL.sRGBWrite;
            RenderTexture rt = null;
            try
            {
                bool srgb = IsSrgb(tex, isNormal);
                rt = new RenderTexture(tex.width, tex.height, 0, RenderTextureFormat.ARGB32,
                    srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
                rt.Create();
                GL.sRGBWrite = true;
                Graphics.Blit(tex, rt);

                var readable = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false, !srgb);
                RenderTexture.active = rt;
                readable.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
                readable.Apply(false);

                var cp = new CachedPixels
                {
                    texture = tex,
                    pixels = readable.GetPixels32(),
                    width = tex.width,
                    height = tex.height,
                    srgb = srgb,
                    normalLayout = isNormal ? DetectLayout(tex.format) : NormalLayout.RGB,
                };
                return cp;
            }
            catch (System.Exception e)
            {
                ATOLog.Warn($"readback failed for '{tex.name}': {e.Message}");
                return null;
            }
            finally
            {
                GL.sRGBWrite = prevSrgbWrite;
                RenderTexture.active = prevActive;
                if (rt != null)
                {
                    rt.Release();
                    Object.DestroyImmediate(rt);
                }
            }
        }

        internal static bool IsSrgb(Texture2D tex, bool isNormal)
        {
            if (isNormal) return false;
            // imported: ask importer / 导入贴图问 importer
            string path = UnityEditor.AssetDatabase.GetAssetPath(tex);
            if (!string.IsNullOrEmpty(path) &&
                UnityEditor.AssetImporter.GetAtPath(path) is UnityEditor.TextureImporter ti)
                return ti.sRGBTexture;
            // runtime texture: graphics format tells / 运行时贴图看图形格式
            return UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsSRGBFormat(tex.graphicsFormat);
        }

        internal static NormalLayout DetectLayout(TextureFormat f)
        {
            switch (f)
            {
                case TextureFormat.BC5: return NormalLayout.RG;
                case TextureFormat.DXT5:
                case TextureFormat.BC7:
                case TextureFormat.DXT5Crunched:
                    return NormalLayout.AG;
                default: return NormalLayout.RGB; // uncompressed RGBA / ASTC stores RGB(x,y,z?) - treat RGB
            }
        }

        // ------------------------------------------------------------------
        // Content analysis / 内容分析
        // ------------------------------------------------------------------
        internal static bool HasAlpha(CachedPixels cp)
        {
            if (cp._hasAlpha != null) return cp._hasAlpha.Value;
            bool has = false;
            var p = cp.pixels;
            for (int i = 0; i < p.Length; i++)
                if (p[i].a < 252)
                {
                    has = true;
                    break;
                }
            cp._hasAlpha = has;
            return has;
        }

        internal static bool IsGrayscale(CachedPixels cp)
        {
            if (cp._grayscale != null) return cp._grayscale.Value;
            bool gray = true;
            var p = cp.pixels;
            for (int i = 0; i < p.Length; i++)
            {
                var c = p[i];
                if (Mathf.Abs(c.r - c.g) > 2 || Mathf.Abs(c.g - c.b) > 2)
                {
                    gray = false;
                    break;
                }
            }
            cp._grayscale = gray;
            return gray;
        }

        internal static bool IsPureColor(CachedPixels cp, out Color32 value)
        {
            if (cp._pureColor != null)
            {
                value = cp._pureColorValue;
                return cp._pureColor.Value;
            }

            bool pure = true;
            var first = cp.pixels.Length > 0 ? cp.pixels[0] : default;
            var p = cp.pixels;
            for (int i = 1; i < p.Length; i++)
                if (Mathf.Abs(p[i].r - first.r) > 1 || Mathf.Abs(p[i].g - first.g) > 1 ||
                    Mathf.Abs(p[i].b - first.b) > 1 || Mathf.Abs(p[i].a - first.a) > 1)
                {
                    pure = false;
                    break;
                }
            cp._pureColor = pure;
            cp._pureColorValue = first;
            value = first;
            return pure;
        }

        /// <summary>Which channels carry distinct information (for mask textures).
        /// 通道是否承载独立信息（蒙版贴图用）。</summary>
        internal static bool[] ChannelSignificance(CachedPixels cp)
        {
            var sig = new bool[4];
            var p = cp.pixels;
            byte rMin = 255, rMax = 0, gMin = 255, gMax = 0, bMin = 255, bMax = 0, aMin = 255, aMax = 0;
            int stride = Mathf.Max(1, p.Length / 200000); // sample cap / 采样上限
            for (int i = 0; i < p.Length; i += stride)
            {
                var c = p[i];
                if (c.r < rMin) rMin = c.r; if (c.r > rMax) rMax = c.r;
                if (c.g < gMin) gMin = c.g; if (c.g > gMax) gMax = c.g;
                if (c.b < bMin) bMin = c.b; if (c.b > bMax) bMax = c.b;
                if (c.a < aMin) aMin = c.a; if (c.a > aMax) aMax = c.a;
            }
            sig[0] = rMax - rMin > 2; sig[1] = gMax - gMin > 2;
            sig[2] = bMax - bMin > 2; sig[3] = aMax - aMin > 2;
            return sig;
        }
    }
}
