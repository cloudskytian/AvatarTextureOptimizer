// Copyright (c) fosa. Licensed under the MIT License.
// Decoded-pixel cache with a hard memory budget. Decoding a 4K RGBA texture costs 64 MB as
// floats, so avatars with hundreds of textures must not hold them all at once.
// 带硬性内存预算的解码像素缓存。4K RGBA 贴图以 float 形式解码需 64MB，
// 因此拥有数百张贴图的 Avatar 绝不能同时持有全部数据。

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Object = UnityEngine.Object;

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Linear-space RGBA pixel data for one texture, plus the metadata needed to re-encode it.
    /// 某张贴图的线性空间 RGBA 像素数据，以及重新编码所需的元数据。
    /// </summary>
    public sealed class DecodedTexture
    {
        /// <summary>Width in pixels. / 宽度（像素）。</summary>
        public int Width;

        /// <summary>Height in pixels. / 高度（像素）。</summary>
        public int Height;

        /// <summary>
        /// Linear-space RGBA, row-major, bottom-up to match Unity's GetPixels convention.
        /// 线性空间 RGBA，行主序，自下而上以匹配 Unity 的 GetPixels 约定。
        /// </summary>
        public Color[] Pixels;

        /// <summary>True when the source asset was sRGB encoded. / 源资产为 sRGB 编码时为 true。</summary>
        public bool WasSRGB;

        /// <summary>True when any pixel has alpha below 1. / 存在 alpha 小于 1 的像素时为 true。</summary>
        public bool HasAlpha;

        /// <summary>Approximate heap cost in bytes. / 近似堆内存占用（字节）。</summary>
        public long ByteSize => (long)Width * Height * 16;
    }

    /// <summary>
    /// Decodes textures to linear float RGBA on the GPU and caches the result under a memory
    /// budget, evicting least-recently-used entries. All perceptual metrics operate in linear
    /// space, which is why decoding centralises the sRGB conversion.
    /// 在 GPU 上将贴图解码为线性 float RGBA 并在内存预算内缓存，按最近最少使用淘汰。
    /// 所有感知指标都在线性空间计算，因此在此集中处理 sRGB 转换。
    /// </summary>
    public sealed class TextureCache : IDisposable
    {
        private readonly Dictionary<Texture2D, DecodedTexture> _cache =
            new Dictionary<Texture2D, DecodedTexture>();

        private readonly LinkedList<Texture2D> _lru = new LinkedList<Texture2D>();
        private readonly Dictionary<Texture2D, LinkedListNode<Texture2D>> _lruNodes =
            new Dictionary<Texture2D, LinkedListNode<Texture2D>>();

        private readonly ATOLogger _log;
        private long _currentBytes;

        /// <summary>
        /// Memory budget in bytes. Defaults to 1 GiB, which comfortably holds a working set of
        /// several 4K textures while leaving room for the Unity editor itself.
        /// 内存预算（字节），默认 1 GiB，足以容纳数张 4K 贴图的工作集，同时为 Unity 编辑器留出空间。
        /// </summary>
        public long BudgetBytes { get; set; } = 1024L * 1024L * 1024L;

        /// <summary>Number of cache hits, for the report. / 缓存命中次数，用于报告。</summary>
        public int Hits { get; private set; }

        /// <summary>Number of cache misses, for the report. / 缓存未命中次数，用于报告。</summary>
        public int Misses { get; private set; }

        /// <summary>Creates a cache bound to a logger. / 创建绑定到日志器的缓存。</summary>
        public TextureCache(ATOLogger log)
        {
            _log = log;
        }

        /// <summary>
        /// Returns linear RGBA pixels for a texture, decoding on first use.
        /// 返回贴图的线性 RGBA 像素，首次使用时解码。
        /// </summary>
        public DecodedTexture Get(Texture2D texture)
        {
            if (texture == null) return null;

            if (_cache.TryGetValue(texture, out var hit))
            {
                Hits++;
                Touch(texture);
                return hit;
            }

            Misses++;
            var decoded = Decode(texture);
            if (decoded == null) return null;

            Insert(texture, decoded);
            return decoded;
        }

        /// <summary>
        /// Decodes a texture through a temporary RenderTexture. This works regardless of the
        /// source compression format or its Read/Write flag, which is why we never call
        /// GetPixels on the source asset directly.
        /// 通过临时 RenderTexture 解码贴图。此法不受源压缩格式与 Read/Write 标志影响，
        /// 因此我们从不直接对源资产调用 GetPixels。
        /// </summary>
        private DecodedTexture Decode(Texture2D texture)
        {
            var w = texture.width;
            var h = texture.height;
            if (w <= 0 || h <= 0) return null;

            RenderTexture rt = null;
            Texture2D readback = null;
            var prevActive = RenderTexture.active;

            try
            {
                // A linear RenderTexture makes the GPU undo sRGB encoding during the blit, so the
                // values we read back are already linear.
                // 线性 RenderTexture 会让 GPU 在 blit 过程中撤销 sRGB 编码，因此回读到的值已是线性的。
                rt = RenderTexture.GetTemporary(
                    w, h, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);

                Graphics.Blit(texture, rt);
                RenderTexture.active = rt;

                readback = new Texture2D(w, h, TextureFormat.RGBAFloat, false, true);
                readback.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                readback.Apply(false, false);

                var pixels = readback.GetPixels();
                var hasAlpha = false;
                for (var i = 0; i < pixels.Length; i++)
                {
                    if (pixels[i].a < 0.999f)
                    {
                        hasAlpha = true;
                        break;
                    }
                }

                return new DecodedTexture
                {
                    Width = w,
                    Height = h,
                    Pixels = pixels,
                    WasSRGB = IsSRGB(texture),
                    HasAlpha = hasAlpha,
                };
            }
            catch (Exception e)
            {
                _log?.Warning($"Failed to decode texture '{texture.name}': {e.Message}");
                return null;
            }
            finally
            {
                RenderTexture.active = prevActive;
                if (readback != null) Object.DestroyImmediate(readback);
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
            }
        }

        /// <summary>
        /// Reports whether a texture's data is sRGB encoded.
        /// 报告贴图数据是否为 sRGB 编码。
        /// </summary>
        public static bool IsSRGB(Texture2D texture)
        {
            if (texture == null) return false;
            return texture.isDataSRGB;
        }

        private void Insert(Texture2D key, DecodedTexture value)
        {
            EvictUntilFits(value.ByteSize);
            _cache[key] = value;
            _currentBytes += value.ByteSize;
            var node = _lru.AddFirst(key);
            _lruNodes[key] = node;
        }

        private void Touch(Texture2D key)
        {
            if (!_lruNodes.TryGetValue(key, out var node)) return;
            _lru.Remove(node);
            _lru.AddFirst(node);
        }

        private void EvictUntilFits(long incoming)
        {
            while (_currentBytes + incoming > BudgetBytes && _lru.Count > 0)
            {
                var oldest = _lru.Last;
                if (oldest == null) break;
                var key = oldest.Value;
                _lru.RemoveLast();
                _lruNodes.Remove(key);

                if (_cache.TryGetValue(key, out var evicted))
                {
                    _currentBytes -= evicted.ByteSize;
                    evicted.Pixels = null;
                    _cache.Remove(key);
                }
            }
        }

        /// <summary>Drops every cached entry and frees the managed arrays. / 丢弃所有缓存条目并释放托管数组。</summary>
        public void Clear()
        {
            foreach (var kv in _cache) kv.Value.Pixels = null;
            _cache.Clear();
            _lru.Clear();
            _lruNodes.Clear();
            _currentBytes = 0;
        }

        /// <inheritdoc />
        public void Dispose() => Clear();
    }
}
