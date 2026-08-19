// English: LRU decoded-pixel cache. Avoids repeated Texture2D decode / ReadPixels.
// 中文：解码像素 LRU 缓存，避免重复解码与 ReadPixels。
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    internal sealed class ATODecodedTexture : IDisposable
    {
        public Texture2D Source;
        public int Width;
        public int Height;
        public Color32[] Pixels;
        public bool Linear;
        public bool HasAlpha;
        public bool IsNormal;
        public long Bytes
        {
            get { return Pixels == null ? 0 : (long)Pixels.Length * 4L; }
        }

        public Color32 Get(int x, int y)
        {
            x = Mathf.Clamp(x, 0, Width - 1);
            y = Mathf.Clamp(y, 0, Height - 1);
            return Pixels[y * Width + x];
        }

        public Color GetLinear(int x, int y)
        {
            var c = (Color)Get(x, y);
            if (Linear) return c;
            return c.linear;
        }

        public void Dispose()
        {
            Pixels = null;
        }
    }

    internal sealed class ATOTextureCache : IDisposable
    {
        public long BudgetBytes = 512L * 1024L * 1024L;
        private long _used;
        private readonly Dictionary<Texture2D, ATODecodedTexture> _map =
            new Dictionary<Texture2D, ATODecodedTexture>();
        private readonly LinkedList<Texture2D> _lru = new LinkedList<Texture2D>();
        private readonly Dictionary<Texture2D, LinkedListNode<Texture2D>> _nodes =
            new Dictionary<Texture2D, LinkedListNode<Texture2D>>();

        public ATODecodedTexture Get(Texture2D tex, ATOLogger log = null)
        {
            if (tex == null) return null;
            ATODecodedTexture hit;
            if (_map.TryGetValue(tex, out hit) && hit != null && hit.Pixels != null)
            {
                Touch(tex);
                return hit;
            }

            var decoded = Decode(tex, log);
            if (decoded == null) return null;
            Put(tex, decoded);
            return decoded;
        }

        public void Dispose()
        {
            foreach (var kv in _map)
            {
                if (kv.Value != null) kv.Value.Dispose();
            }

            _map.Clear();
            _lru.Clear();
            _nodes.Clear();
            _used = 0;
        }

        private void Put(Texture2D tex, ATODecodedTexture decoded)
        {
            while (_used + decoded.Bytes > BudgetBytes && _lru.Count > 0)
            {
                var old = _lru.Last.Value;
                _lru.RemoveLast();
                _nodes.Remove(old);
                ATODecodedTexture evicted;
                if (_map.TryGetValue(old, out evicted) && evicted != null)
                {
                    _used -= evicted.Bytes;
                    evicted.Dispose();
                }

                _map.Remove(old);
            }

            _map[tex] = decoded;
            _used += decoded.Bytes;
            var node = _lru.AddFirst(tex);
            _nodes[tex] = node;
        }

        private void Touch(Texture2D tex)
        {
            LinkedListNode<Texture2D> node;
            if (!_nodes.TryGetValue(tex, out node)) return;
            _lru.Remove(node);
            _lru.AddFirst(node);
        }

        private static ATODecodedTexture Decode(Texture2D tex, ATOLogger log)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            RenderTexture rt = null;
            Texture2D tmp = null;
            try
            {
                var w = tex.width;
                var h = tex.height;
                if (w <= 0 || h <= 0) return null;

                var linear = IsLinearAsset(tex);
                var desc = new RenderTextureDescriptor(w, h, RenderTextureFormat.ARGB32, 0)
                {
                    sRGB = !linear,
                    msaaSamples = 1,
                    useMipMap = false,
                    autoGenerateMips = false
                };
                rt = RenderTexture.GetTemporary(desc);
                var prev = RenderTexture.active;
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                tmp = new Texture2D(w, h, TextureFormat.RGBA32, false, linear);
                tmp.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                tmp.Apply(false, false);
                RenderTexture.active = prev;

                var decoded = new ATODecodedTexture
                {
                    Source = tex,
                    Width = w,
                    Height = h,
                    Pixels = tmp.GetPixels32(),
                    Linear = linear,
                    HasAlpha = DetectAlpha(tmp.GetPixels32()),
                    IsNormal = tex != null && (TextureImporterTypeGuess(tex) == TextureImporterType.NormalMap)
                };
                sw.Stop();
                if (log != null)
                    log.VerboseInfo("decoded " + tex.name + " " + w + "x" + h + " in " + sw.ElapsedMilliseconds + " ms linear=" + linear);
                return decoded;
            }
            catch (Exception e)
            {
                if (log != null) log.Warn("decode failed " + tex.name + ": " + e.Message);
                return null;
            }
            finally
            {
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (tmp != null) Object.DestroyImmediate(tmp);
            }
        }

        private static bool DetectAlpha(Color32[] px)
        {
            if (px == null) return false;
            for (var i = 0; i < px.Length; i++)
            {
                if (px[i].a < 250) return true;
            }

            return false;
        }

        internal static bool IsLinearAsset(Texture t)
        {
            if (t == null) return false;
            var path = AssetDatabase.GetAssetPath(t);
            if (string.IsNullOrEmpty(path)) return false;
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) return false;
            return importer.sRGBTexture == false || importer.textureType == TextureImporterType.NormalMap;
        }

        internal static TextureImporterType TextureImporterTypeGuess(Texture t)
        {
            var path = AssetDatabase.GetAssetPath(t);
            if (string.IsNullOrEmpty(path)) return TextureImporterType.Default;
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            return importer != null ? importer.textureType : TextureImporterType.Default;
        }

        internal static string ImporterFingerprint(Texture2D t)
        {
            var path = AssetDatabase.GetAssetPath(t);
            if (string.IsNullOrEmpty(path)) return "runtime|" + t.width + "x" + t.height + "|" + t.format;
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) return path;
            return path + "|" + importer.textureType + "|" + importer.sRGBTexture + "|" + importer.mipmapEnabled +
                   "|" + importer.filterMode + "|" + importer.wrapMode + "|" + importer.anisoLevel + "|" +
                   importer.maxTextureSize + "|" + importer.textureCompression + "|" + importer.crunchedCompression +
                   "|" + importer.npotScale + "|" + importer.streamingMipmaps;
        }
    }
}
