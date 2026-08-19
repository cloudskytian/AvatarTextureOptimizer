// ATOGpu.cs
// GPU access layer: texture → NativeArray<Color32> readback via RenderTexture
// (supports non-readable & compressed textures), with an LRU cache and strict disposal.
// GPU 访问层:经 RenderTexture 将贴图读回 NativeArray<Color32>(支持不可读/压缩贴图),
// 带 LRU 缓存与严格释放。
// Copyright (c) 2026 fosa. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEngine;

namespace net.fosa.ato
{
    /// <summary>Readback of raw stored texel values. / 原始纹素读回结果。</summary>
    internal sealed class GpuReadback : IDisposable
    {
        internal NativeArray<Color32> Pixels;
        internal int Width, Height;
        /// <summary>Texels are sRGB-encoded (as stored). / 纹素为 sRGB 编码(与存储一致)。</summary>
        internal bool Srgb;

        internal Color32 Get(int x, int y) => Pixels[y * Width + x];
        internal void Set(int x, int y, Color32 c) => Pixels[y * Width + x] = c;

        public void Dispose()
        {
            if (Pixels.IsCreated) Pixels.Dispose();
            Pixels = default(NativeArray<Color32>);
        }
    }

    internal sealed class ATOGpu : IDisposable
    {
        internal static ATOGpu Instance => _instance ?? (_instance = new ATOGpu());
        private static ATOGpu _instance;

        private sealed class Entry
        {
            internal GpuReadback Data;
            internal long LastUse;
        }

        private readonly Dictionary<Texture2D, Entry> _cache = new Dictionary<Texture2D, Entry>();
        private long _clock;
        /// <summary>Soft budget in bytes (default 384 MB). / 字节软预算(默认384MB)。</summary>
        internal long CacheBudgetBytes = 384L * 1024 * 1024;
        private long _cacheBytes;

        /// <summary>Cached readback. / 带缓存的读回。</summary>
        internal GpuReadback Readback(Texture2D tex)
        {
            if (tex == null) throw new ArgumentNullException(nameof(tex));
            _clock++;
            if (_cache.TryGetValue(tex, out var e))
            {
                e.LastUse = _clock;
                return e.Data;
            }
            var data = ReadbackNow(tex);
            _cache[tex] = new Entry { Data = data, LastUse = _clock };
            _cacheBytes += (long)data.Width * data.Height * 4;
            TrimCache();
            return data;
        }

        /// <summary>Direct readback, not cached. / 直接读回,不缓存。</summary>
        internal GpuReadback ReadbackNow(Texture2D tex)
        {
            bool srgb = ShaderAnalyzer.ImportSrgb(tex);
            int w = tex.width, h = tex.height;

            var rt = RenderTexture.GetTemporary(w, h, 0,
                RenderTextureFormat.ARGB32,
                srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            var prev = RenderTexture.active;
            var readTex = new Texture2D(w, h, TextureFormat.RGBA32, false, !srgb);
            try
            {
                rt.filterMode = FilterMode.Point;
                Graphics.Blit(tex, rt); // 1:1 texel-exact copy / 1:1 纹素精确拷贝
                RenderTexture.active = rt;
                readTex.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                readTex.Apply(false, false);

                var managed = readTex.GetPixels32();
                var native = new NativeArray<Color32>(managed.Length, Allocator.Persistent);
                native.CopyFrom(managed);
                return new GpuReadback { Pixels = native, Width = w, Height = h, Srgb = srgb };
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(readTex);
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        private void TrimCache()
        {
            if (_cacheBytes <= CacheBudgetBytes) return;
            var ordered = _cache.OrderBy(kv => kv.Value.LastUse).ToList();
            foreach (var kv in ordered)
            {
                if (_cacheBytes <= CacheBudgetBytes * 3 / 4) break;
                _cacheBytes -= (long)kv.Value.Data.Width * kv.Value.Data.Height * 4;
                kv.Value.Data.Dispose();
                _cache.Remove(kv.Key);
                ATOLog.V($"gpu cache evicted '{kv.Key.name}'");
            }
        }

        internal void Evict(Texture2D tex)
        {
            if (tex != null && _cache.TryGetValue(tex, out var e))
            {
                _cacheBytes -= (long)e.Data.Width * e.Data.Height * 4;
                e.Data.Dispose();
                _cache.Remove(tex);
            }
        }

        public void Dispose()
        {
            foreach (var kv in _cache) kv.Value.Data.Dispose();
            _cache.Clear();
            _cacheBytes = 0;
        }

        internal static void Shutdown()
        {
            if (_instance != null)
            {
                _instance.Dispose();
                _instance = null;
            }
        }
    }
}
