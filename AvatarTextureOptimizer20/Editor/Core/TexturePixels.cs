// GPU-assisted texture decoding & pixel cache with memory budget.
// GPU 辅助的贴图解码与带内存预算的像素缓存（避免重复解码，防止内存膨胀/泄漏）。
using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// Decodes textures (any compressed format, readable or not) into linear-space RGBA float
    /// arrays via RenderTexture blit. Normal maps are decoded (UnpackNormal incl. DXT5nm) and
    /// re-normalized. LRU eviction keeps memory bounded; everything NativeArray-backed and
    /// disposed deterministically.
    /// 通过 RT Blit 解码任意贴图为线性 RGBA float；法线走 UnpackNormal 解码并重归一化；
    /// LRU 限制内存占用，NativeArray 确定性释放，保证无泄漏。
    /// </summary>
    public sealed class TexturePixels : IDisposable
    {
        public const long DefaultBudgetBytes = 1536L * 1024 * 1024; // ~1.5GB decoded cache / 解码缓存预算

        private sealed class Entry
        {
            public NativeArray<Color> Data;
            public int W, H;
            public long Tick;
        }

        private readonly Dictionary<(Texture2D, bool), Entry> _cache = new();
        private long _tick, _bytes;
        private readonly long _budget;
        private Material _decodeMat;

        public TexturePixels(long budgetBytes = DefaultBudgetBytes) { _budget = budgetBytes; }

        private Material DecodeMat
        {
            get
            {
                if (_decodeMat == null)
                {
                    var sh = Shader.Find("Hidden/ATO/Decode");
                    if (sh == null) throw new InvalidOperationException("[ATO] Hidden/ATO/Decode shader missing");
                    _decodeMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
                }
                return _decodeMat;
            }
        }

        /// <summary>Get linear RGBA pixels. asNormal decodes tangent normals into RGB [-1,1]→[0,1]*0.5+0.5 packed.
        /// 获取线性像素；asNormal 时输出解码后的切线法线（xyz 重归一化，存回 0..1）。</summary>
        public NativeArray<Color> Get(Texture2D tex, bool asNormal, out int w, out int h)
        {
            var key = (tex, asNormal);
            if (_cache.TryGetValue(key, out var e))
            {
                e.Tick = ++_tick;
                w = e.W; h = e.H;
                return e.Data;
            }

            w = tex.width; h = tex.height;
            var colors = Decode(tex, asNormal, w, h);
            var arr = new NativeArray<Color>(colors, Allocator.Persistent);
            _bytes += (long)w * h * 16;
            _cache[key] = new Entry { Data = arr, W = w, H = h, Tick = ++_tick };
            EvictIfNeeded();
            return arr;
        }

        private Color[] Decode(Texture2D tex, bool asNormal, int w, int h)
        {
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGBFloat,
                RenderTextureReadWrite.Linear);
            var prev = RenderTexture.active;
            try
            {
                DecodeMat.SetFloat("_AsNormal", asNormal ? 1f : 0f);
                Graphics.Blit(tex, rt, DecodeMat, 0);
                RenderTexture.active = rt;
                var read = new Texture2D(w, h, TextureFormat.RGBAFloat, false, true);
                read.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                read.Apply(false);
                var px = read.GetPixels();
                UnityEngine.Object.DestroyImmediate(read);
                return px;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        private void EvictIfNeeded()
        {
            while (_bytes > _budget && _cache.Count > 1)
            {
                (Texture2D, bool) oldestKey = default;
                long oldest = long.MaxValue;
                foreach (var kv in _cache)
                    if (kv.Value.Tick < oldest) { oldest = kv.Value.Tick; oldestKey = kv.Key; }
                var e = _cache[oldestKey];
                _bytes -= (long)e.W * e.H * 16;
                if (e.Data.IsCreated) e.Data.Dispose();
                _cache.Remove(oldestKey);
                AtoLog.Debugf($"pixel cache evict: {oldestKey.Item1.name} (normal={oldestKey.Item2})");
            }
        }

        /// <summary>Raw byte hash for dedup (pixels + size + format). / 去重用像素哈希。</summary>
        public static string PixelHash(Texture2D tex)
        {
            var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Linear);
            var prev = RenderTexture.active;
            try
            {
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                var read = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false, true);
                read.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0, false);
                read.Apply(false);
                var bytes = read.GetRawTextureData<byte>();
                using var md5 = System.Security.Cryptography.MD5.Create();
                var hash = md5.ComputeHash(bytes.ToArray());
                UnityEngine.Object.DestroyImmediate(read);
                return $"{tex.width}x{tex.height}_{tex.format}_{Convert.ToBase64String(hash)}";
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        public void Dispose()
        {
            foreach (var e in _cache.Values)
                if (e.Data.IsCreated) e.Data.Dispose();
            _cache.Clear();
            _bytes = 0;
            if (_decodeMat != null) UnityEngine.Object.DestroyImmediate(_decodeMat);
        }
    }
}
