using System;
using System.Collections.Generic;
using UnityEngine;

// RenderTexture pool: reuse RTs to keep memory comfortable during baking.
// RenderTexture 池：烘焙期间复用 RT，控制内存占用。

namespace Net.Fosa.AvatarTextureOptimizer.Editor
{
    public sealed class RenderTexturePool : IDisposable
    {
        private readonly Dictionary<RTKey, Stack<RenderTexture>> _pool = new Dictionary<RTKey, Stack<RenderTexture>>();
        private long _estimatedBytes;

        /// <summary>Soft memory cap for pooled RTs. 池化 RT 的软内存上限。</summary>
        public long BudgetBytes = 384L * 1024 * 1024;

        private readonly struct RTKey : IEquatable<RTKey>
        {
            public readonly int W, H;
            public readonly RenderTextureFormat Fmt;
            public readonly bool Linear;
            public RTKey(int w, int h, RenderTextureFormat fmt, bool linear) { W = w; H = h; Fmt = fmt; Linear = linear; }
            public bool Equals(RTKey other) => W == other.W && H == other.H && Fmt == other.Fmt && Linear == other.Linear;
            public override bool Equals(object obj) => obj is RTKey k && Equals(k);
            public override int GetHashCode() => (W * 397 ^ H) * 31 ^ (int)Fmt ^ (Linear ? 0x40000000 : 0);
        }

        public RenderTexture Acquire(int w, int h, RenderTextureFormat fmt = RenderTextureFormat.ARGB32, bool linear = false)
        {
            var key = new RTKey(w, h, fmt, linear);
            if (_pool.TryGetValue(key, out var stack) && stack.Count > 0)
            {
                var rt = stack.Pop();
                rt.DiscardContents();
                return rt;
            }
            var created = new RenderTexture(w, h, 0, fmt, linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.Default)
            {
                useMipMap = false,
                hideFlags = HideFlags.HideAndDontSave,
            };
            created.Create();
            _estimatedBytes += (long)w * h * 16;
            return created;
        }

        public void Release(RenderTexture rt)
        {
            if (rt == null) return;
            var key = new RTKey(rt.width, rt.height, rt.format, rt.sRGB ? false : true); // best-effort key. 尽力匹配 key。
            if (!_pool.TryGetValue(key, out var stack)) { stack = new Stack<RenderTexture>(); _pool[key] = stack; }
            if (_estimatedBytes > BudgetBytes)
            {
                rt.Release();
                _estimatedBytes -= (long)rt.width * rt.height * 16;
                return;
            }
            stack.Push(rt);
        }

        public void Dispose()
        {
            foreach (var kv in _pool)
                foreach (var rt in kv.Value)
                    if (rt != null) rt.Release();
            _pool.Clear();
            _estimatedBytes = 0;
        }
    }
}
