using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Object = UnityEngine.Object;

namespace Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// LRU cache of linear-space decoded pixels. Caps memory and always disposes leftovers.
    /// 线性空间解码像素的 LRU 缓存。有内存上限，结束时一定释放。
    /// </summary>
    public sealed class TextureDecodeCache : IDisposable
    {
        public const long DefaultBudgetBytes = 384L * 1024L * 1024L;

        public struct Decoded
        {
            public int Width;
            public int Height;
            public Color[] Linear;
            public bool HasAlpha;
            public bool IsNormal;
            public bool IsSrgb;
        }

        readonly long _budget;
        readonly Dictionary<int, Entry> _map = new Dictionary<int, Entry>();
        readonly LinkedList<int> _lru = new LinkedList<int>();
        long _used;
        bool _disposed;

        public TextureDecodeCache(long budgetBytes = DefaultBudgetBytes)
        {
            _budget = Math.Max(64L * 1024L * 1024L, budgetBytes);
        }

        public Decoded Get(Texture2D tex, bool treatAsNormal)
        {
            if (tex == null) throw new ArgumentNullException(nameof(tex));
            var key = tex.GetInstanceID() * 2 + (treatAsNormal ? 1 : 0);
            if (_map.TryGetValue(key, out var e))
            {
                _lru.Remove(e.Node);
                _lru.AddFirst(e.Node);
                return e.Data;
            }

            var data = DecodeNow(tex, treatAsNormal);
            var bytes = (long)data.Linear.Length * 16L;
            EvictUntil(_budget - bytes);
            var node = _lru.AddFirst(key);
            _map[key] = new Entry { Data = data, Bytes = bytes, Node = node };
            _used += bytes;
            return data;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _map.Clear();
            _lru.Clear();
            _used = 0;
        }

        void EvictUntil(long target)
        {
            while (_used > target && _lru.Last != null)
            {
                var key = _lru.Last.Value;
                _lru.RemoveLast();
                if (_map.TryGetValue(key, out var e))
                {
                    _used -= e.Bytes;
                    _map.Remove(key);
                }
            }
        }

        /// <summary>
        /// Blit through a linear RT so sRGB sources are converted. Never relies on isReadable.
        /// 经线性 RT Blit，sRGB 源会被转换。不依赖 isReadable。
        /// </summary>
        public static Decoded DecodeNow(Texture2D tex, bool treatAsNormal)
        {
            var w = tex.width;
            var h = tex.height;
            var desc = new RenderTextureDescriptor(w, h, RenderTextureFormat.ARGBHalf, 0)
            {
                sRGB = false,
                msaaSamples = 1,
                useMipMap = false,
                autoGenerateMips = false
            };
            var rt = RenderTexture.GetTemporary(desc);
            var prev = RenderTexture.active;
            try
            {
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                var tmp = new Texture2D(w, h, TextureFormat.RGBAHalf, false, true);
                tmp.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                tmp.Apply(false, false);
                var pixels = tmp.GetPixels();
                Object.DestroyImmediate(tmp);

                var hasAlpha = false;
                for (int i = 0; i < pixels.Length; i++)
                {
                    if (pixels[i].a < 0.999f) { hasAlpha = true; break; }
                }

                if (treatAsNormal)
                {
                    DecodeNormalsInPlace(pixels, tex);
                }

                return new Decoded
                {
                    Width = w,
                    Height = h,
                    Linear = pixels,
                    HasAlpha = hasAlpha,
                    IsNormal = treatAsNormal,
                    IsSrgb = tex.isDataSRGB
                };
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        static void DecodeNormalsInPlace(Color[] px, Texture2D src)
        {
            // DXT5nm / AG: xy in AG, reconstruct z. XY: rg. RGBA: xyz.
            // We detect from importer when possible. / 能读到导入器时按导入器判断。
            var ag = false;
#if UNITY_EDITOR
            var path = UnityEditor.AssetDatabase.GetAssetPath(src);
            if (!string.IsNullOrEmpty(path))
            {
                var imp = UnityEditor.AssetImporter.GetAtPath(path) as UnityEditor.TextureImporter;
                if (imp != null && imp.textureType == UnityEditor.TextureImporterType.NormalMap)
                {
                    ag = true;
                }
            }
#endif
            for (int i = 0; i < px.Length; i++)
            {
                Vector3 n;
                if (ag)
                {
                    var x = px[i].a * 2f - 1f;
                    var y = px[i].g * 2f - 1f;
                    var z = Mathf.Sqrt(Mathf.Max(0f, 1f - x * x - y * y));
                    n = new Vector3(x, y, z);
                }
                else
                {
                    n = new Vector3(px[i].r * 2f - 1f, px[i].g * 2f - 1f, px[i].b * 2f - 1f);
                }

                if (n.sqrMagnitude < 1e-8f) n = new Vector3(0, 0, 1);
                n.Normalize();
                px[i] = new Color(n.x, n.y, n.z, 1f);
            }
        }

        struct Entry
        {
            public Decoded Data;
            public long Bytes;
            public LinkedListNode<int> Node;
        }
    }
}
