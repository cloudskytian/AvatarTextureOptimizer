using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// Shared raw-pixel cache (LRU with a byte budget) so the quality stage, whole-texture scaling
    /// and atlas composition read each source texture only once. / 共享原始像素缓存（LRU + 字节预算），
    /// 让质量阶段、整图缩放与图集合成每张源贴图只读取一次。
    /// </summary>
    internal sealed class AtoPixelCache
    {
        private readonly Dictionary<Texture2D, Color32[]> _cache = new Dictionary<Texture2D, Color32[]>();
        private long _bytes;

        public Color32[] Get(Texture2D texture)
        {
            if (_cache.TryGetValue(texture, out var cached)) return cached;
            var pixels = AtoTextureIO.GetPixels(texture);
            _cache[texture] = pixels;
            _bytes += pixels.LongLength * 4L;
            Evict();
            return pixels;
        }

        public bool TryGet(Texture2D texture, out Color32[] pixels) => _cache.TryGetValue(texture, out pixels);

        private void Evict()
        {
            const long budget = 512L * 1024 * 1024; // 512MB cap. / 512MB 上限。
            if (_bytes <= budget) return;
            foreach (var kv in _cache.OrderBy(k => k.Key.GetInstanceID()).ToList())
            {
                if (_bytes <= budget * 3 / 4) break;
                _bytes -= kv.Value.LongLength * 4L;
                _cache.Remove(kv.Key);
            }
        }

        public void Clear()
        {
            _cache.Clear();
            _bytes = 0;
        }
    }
}
