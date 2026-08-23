// SPDX-License-Identifier: MIT
// EN: Budgeted, on demand cache of decoded textures held on the GPU.
// ZH: 有预算约束、按需解码并驻留在 GPU 上的贴图缓存。

using System;
using System.Collections.Generic;
using Net.Fosa.AvatarTextureOptimizer.Editor.Core;
using Net.Fosa.AvatarTextureOptimizer.Editor.Model;
using UnityEngine;

namespace Net.Fosa.AvatarTextureOptimizer.Editor.Textures
{
    /// <summary>
    /// EN: Decodes textures into their UV group's reference resolution and keeps them on the GPU, but
    ///     never more than a configured byte budget at a time. When the budget is exceeded the least
    ///     recently used entries are released and re-decoded later if needed.
    ///     This is what keeps peak memory bounded on avatars with dozens of 4K textures: a naive
    ///     implementation that decodes everything up front would need many gigabytes of VRAM.
    /// ZH: 将贴图解码到其所属 UV 组的参考分辨率并驻留在 GPU 上，但同一时刻绝不超过配置的字节预算。
    ///     超出预算时会释放最久未使用的条目，需要时再重新解码。
    ///     这正是让拥有几十张 4K 贴图的 Avatar 峰值内存可控的关键：
    ///     朴素实现若一次性全部解码，会需要数 GB 显存。
    /// </summary>
    public sealed class LinearSourceCache : IDisposable
    {
        private const string Stage = "Cache";

        private sealed class Entry
        {
            public RenderTexture Texture;
            public long Bytes;
            public long LastUsed;
            public int PinCount;
        }

        private readonly Dictionary<(TextureEntry, Vector2Int), Entry> _entries = new Dictionary<(TextureEntry, Vector2Int), Entry>();
        private readonly long _budgetBytes;
        private long _currentBytes;
        private long _clock;

        /// <summary>EN: Number of times a texture had to be decoded again after eviction. ZH: 因驱逐而不得不重新解码的次数。</summary>
        public int Redecodes { get; private set; }
        /// <summary>EN: Peak resident bytes. ZH: 驻留字节数的峰值。</summary>
        public long PeakBytes { get; private set; }

        /// <summary>
        /// EN: Creates the cache. The default budget is a conservative fraction of the graphics memory
        ///     the editor reports, floored at 256 MB so it still works on machines that report nothing.
        /// ZH: 创建缓存。默认预算是编辑器报告的显存的一个保守比例，
        ///     下限 256 MB，使得在不报告显存的机器上仍能工作。
        /// </summary>
        public LinearSourceCache(long budgetBytes = 0)
        {
            if (budgetBytes <= 0)
            {
                long reported = (long)Mathf.Max(0, SystemInfo.graphicsMemorySize) * 1024L * 1024L;
                budgetBytes = Math.Max(256L * 1024 * 1024, reported / 4);
            }
            _budgetBytes = budgetBytes;
            AtoLog.Info(Stage, $"GPU source budget: {_budgetBytes / (1024 * 1024)} MB");
        }

        /// <summary>
        /// EN: Returns the decoded texture at the requested reference resolution, decoding it if needed.
        ///     The returned handle must be disposed; while it is alive the entry cannot be evicted.
        /// ZH: 返回请求的参考分辨率下的解码贴图，必要时进行解码。
        ///     返回的句柄必须释放；句柄存活期间该条目不会被驱逐。
        /// </summary>
        public Handle Acquire(TextureEntry entry, Vector2Int referenceSize)
        {
            var key = (entry, referenceSize);
            if (_entries.TryGetValue(key, out var cached) && cached.Texture != null && cached.Texture.IsCreated())
            {
                cached.LastUsed = ++_clock;
                cached.PinCount++;
                return new Handle(this, key, cached.Texture);
            }

            if (cached != null) Redecodes++;

            long bytes = (long)referenceSize.x * referenceSize.y * 8; // ARGBHalf
            EvictUntilFits(bytes);

            RenderTexture rt;
            var raw = GpuTextureUtil.ToLinearRT(entry.Texture);
            if (raw.width == referenceSize.x && raw.height == referenceSize.y)
            {
                rt = raw;
            }
            else
            {
                rt = GpuTextureUtil.Downsample(raw, new RectInt(0, 0, raw.width, raw.height), referenceSize, entry.HasAlpha);
                GpuTextureUtil.Release(raw);
            }

            var newEntry = new Entry { Texture = rt, Bytes = bytes, LastUsed = ++_clock, PinCount = 1 };
            _entries[key] = newEntry;
            _currentBytes += bytes;
            PeakBytes = Math.Max(PeakBytes, _currentBytes);
            AtoLog.Trace(Stage, $"decoded '{entry.Texture.name}' at {referenceSize.x}x{referenceSize.y} ({_currentBytes / (1024 * 1024)} MB resident)");
            return new Handle(this, key, rt);
        }

        private void Unpin((TextureEntry, Vector2Int) key)
        {
            if (_entries.TryGetValue(key, out var e) && e.PinCount > 0) e.PinCount--;
        }

        private void EvictUntilFits(long incoming)
        {
            if (_currentBytes + incoming <= _budgetBytes) return;

            var candidates = new List<KeyValuePair<(TextureEntry, Vector2Int), Entry>>();
            foreach (var kv in _entries)
                if (kv.Value.PinCount == 0)
                    candidates.Add(kv);
            candidates.Sort((a, b) => a.Value.LastUsed.CompareTo(b.Value.LastUsed));

            foreach (var kv in candidates)
            {
                if (_currentBytes + incoming <= _budgetBytes) break;
                GpuTextureUtil.Release(kv.Value.Texture);
                kv.Value.Texture = null;
                _currentBytes -= kv.Value.Bytes;
                _entries.Remove(kv.Key);
                AtoLog.Trace(Stage, $"evicted a cached source ({_currentBytes / (1024 * 1024)} MB resident)");
            }

            if (_currentBytes + incoming > _budgetBytes)
                AtoLog.Debug_(Stage, "the GPU budget is exceeded by pinned sources; continuing anyway.");
        }

        /// <summary>EN: Releases everything. ZH: 释放全部内容。</summary>
        public void Dispose()
        {
            foreach (var kv in _entries) GpuTextureUtil.Release(kv.Value.Texture);
            _entries.Clear();
            _currentBytes = 0;
            AtoLog.Info(Stage, $"source cache released, peak {PeakBytes / (1024 * 1024)} MB, {Redecodes} re-decodes");
        }

        /// <summary>
        /// EN: A pinned reference to a cached decode.
        /// ZH: 对缓存解码结果的固定引用。
        /// </summary>
        public readonly struct Handle : IDisposable
        {
            private readonly LinearSourceCache _owner;
            private readonly (TextureEntry, Vector2Int) _key;

            /// <summary>EN: The decoded texture. ZH: 解码后的贴图。</summary>
            public readonly RenderTexture Texture;

            internal Handle(LinearSourceCache owner, (TextureEntry, Vector2Int) key, RenderTexture texture)
            {
                _owner = owner;
                _key = key;
                Texture = texture;
            }

            /// <summary>EN: Unpins the entry so it may be evicted later. ZH: 解除固定，使该条目之后可被驱逐。</summary>
            public void Dispose() => _owner?.Unpin(_key);
        }
    }
}
