using System;
using System.Collections.Generic;
using UnityEngine;

namespace Fosa.ATO.Editor
{
    /// <summary>
    /// LRU decode cache. Caps RAM so baking a 20-texture avatar does not keep every RGBAFloat copy.
    /// 解码 LRU 缓存，限制峰值内存。
    /// </summary>
    public sealed class AtoCache : IDisposable
    {
        readonly long _budget;
        readonly Dictionary<int, Entry> _map = new Dictionary<int, Entry>();
        readonly LinkedList<int> _lru = new LinkedList<int>();
        long _used;

        public AtoCache(long budgetBytes = 256L * 1024 * 1024)
        {
            _budget = Math.Max(32L * 1024 * 1024, budgetBytes);
        }

        sealed class Entry
        {
            public Color[] Pixels;
            public int W, H;
            public long Bytes;
            public LinkedListNode<int> Node;
        }

        public Color[] Get(Texture2D tex)
        {
            if (tex == null) return Array.Empty<Color>();
            int id = tex.GetInstanceID();
            if (_map.TryGetValue(id, out var e))
            {
                _lru.Remove(e.Node);
                e.Node = _lru.AddFirst(id);
                return e.Pixels;
            }
            var px = AtoTextureUtil.ReadPixels(tex);
            long bytes = (long)px.Length * 16; // Color is 16 bytes
            Evict(bytes);
            var n = _lru.AddFirst(id);
            _map[id] = new Entry { Pixels = px, W = tex.width, H = tex.height, Bytes = bytes, Node = n };
            _used += bytes;
            AtoLog.Detail("cache decode " + tex.name + " " + tex.width + "x" + tex.height
                          + " used=" + AtoLog.Bytes(_used) + "/" + AtoLog.Bytes(_budget));
            return px;
        }

        public void EvictAll()
        {
            _map.Clear();
            _lru.Clear();
            _used = 0;
        }

        void Evict(long incoming)
        {
            while (_used + incoming > _budget && _lru.Last != null)
            {
                int id = _lru.Last.Value;
                _lru.RemoveLast();
                if (_map.TryGetValue(id, out var e))
                {
                    _used -= e.Bytes;
                    _map.Remove(id);
                }
            }
        }

        public void Dispose() => EvictAll();
    }
}
