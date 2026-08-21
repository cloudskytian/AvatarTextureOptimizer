using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// Texture decode cache: decodes any imported texture to linear-space float RGBA once, with an LRU
// budget so memory stays comfortable. Islands and atlases read from here.
// 贴图解码缓存：将任意导入贴图解为线性空间 float RGBA（每张贴图一次），带 LRU 内存预算。

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public sealed class TextureDecodeCache : IDisposable
    {
        private sealed class Entry
        {
            public float[] LinearRGBA;   // linear float RGBA (0..1). 线性 float RGBA。
            public Color32[] RawRGBA;    // raw decoded bytes (for dedup). 原始解码字节（用于去重）。
            public bool HasAlpha;        // any pixel alpha < 0.995. 是否存在有效 alpha。
            public bool IsGrayscale;     // sampled R==G==B. 是否灰度。
            public long LastUsed;
        }

        private readonly Dictionary<int, Entry> _entries = new Dictionary<int, Entry>();
        private readonly RenderTexturePool _rtPool;
        private long _budgetBytes;
        private long _usedBytes;
        private long _clock;

        public TextureDecodeCache(RenderTexturePool rtPool, long budgetBytes = 384L * 1024 * 1024)
        {
            _rtPool = rtPool;
            _budgetBytes = budgetBytes;
        }

        public Entry Get(Texture2D tex)
        {
            int id = tex.GetInstanceID();
            if (_entries.TryGetValue(id, out var e)) { e.LastUsed = ++_clock; return e; }

            int w = tex.width, h = tex.height;
            long bytes = (long)w * h * 16;
            EvictIfNeeded(bytes);

            var raw = DecodeRaw(tex, out bool sRGB);
            var linear = new float[w * h * 4];
            bool hasAlpha = false;
            bool isGray = true;
            int n = w * h;
            int stride = Mathf.Max(1, n / 4096); // sample at most ~4096 px for classification. 分类采样上限约 4096 像素。
            for (int i = 0; i < n; i++)
            {
                var c = raw[i];
                float r = c.r / 255f, g = c.g / 255f, b = c.b / 255f, a = c.a / 255f;
                if (sRGB) { r = SrgbToLinear(r); g = SrgbToLinear(g); b = SrgbToLinear(b); }
                int o = i * 4;
                linear[o] = r; linear[o + 1] = g; linear[o + 2] = b; linear[o + 3] = a;
                if ((i % stride) == 0)
                {
                    if (a < 0.995f) hasAlpha = true;
                    if (Mathf.Abs(r - g) > 0.004f || Mathf.Abs(g - b) > 0.004f || Mathf.Abs(r - b) > 0.004f) isGray = false;
                }
            }

            var entry = new Entry { LinearRGBA = linear, RawRGBA = raw, HasAlpha = hasAlpha, IsGrayscale = isGray, LastUsed = ++_clock };
            _entries[id] = entry;
            _usedBytes += bytes;
            return entry;
        }

        private void EvictIfNeeded(long need)
        {
            while (_usedBytes + need > _budgetBytes && _entries.Count > 1)
            {
                int oldestId = -1; long oldest = long.MaxValue;
                foreach (var kv in _entries)
                    if (kv.Value.LastUsed < oldest) { oldest = kv.Value.LastUsed; oldestId = kv.Key; }
                if (oldestId < 0) break;
                if (_entries.TryGetValue(oldestId, out var evicted))
                    _usedBytes -= (long)evicted.LinearRGBA.Length * 4;
                _entries.Remove(oldestId);
            }
        }

        public static float SrgbToLinear(float c) => c <= 0.04045f ? c / 12.92f : Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);

        /// <summary>
        /// Decodes a texture to Color32 via GPU blit + ReadPixels (works for non-readable assets too).
        /// 通过 GPU blit + ReadPixels 将贴图解为 Color32（不可读资源同样可用）。
        /// </summary>
        private Color32[] DecodeRaw(Texture2D tex, out bool sRGB)
        {
            int w = tex.width, h = tex.height;
            sRGB = IsSRGB(tex);

            var rt = _rtPool.Acquire(w, h, RenderTextureFormat.ARGB32, linear: true);
            var prev = RenderTexture.active;
            Graphics.Blit(tex, rt);
            RenderTexture.active = rt;
            var tex2 = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex2.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
            tex2.Apply(false, true);
            var pixels = tex2.GetPixels32();
            UnityEngine.Object.DestroyImmediate(tex2);
            RenderTexture.active = prev;
            _rtPool.Release(rt);
            return pixels;
        }

        public static bool IsSRGB(Texture2D tex)
        {
            var path = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(path)) return tex.isDataSRGB;
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) return tex.isDataSRGB;
            return importer.sRGBTexture;
        }

        public void Dispose()
        {
            _entries.Clear();
            _usedBytes = 0;
        }
    }
}
