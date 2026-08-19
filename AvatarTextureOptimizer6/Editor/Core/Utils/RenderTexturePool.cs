using System;
using System.Collections.Generic;
using UnityEngine;

namespace NetFosa.AvatarTextureOptimizer.Editor.Utils
{
    /// <summary>
    /// RenderTexture 池：避免高频创建/释放 RT 带来的抖动与 GC。
    /// RenderTexture pool to avoid repeated allocation churn.
    /// </summary>
    public sealed class RenderTexturePool : IDisposable
    {
        private readonly Dictionary<(int, int, RenderTextureFormat, bool), Stack<RenderTexture>> _pool =
            new Dictionary<(int, int, RenderTextureFormat, bool), Stack<RenderTexture>>();

        private readonly List<RenderTexture> _all = new List<RenderTexture>();
        private readonly HashSet<RenderTexture> _leased = new HashSet<RenderTexture>();

        public RenderTexture Get(int width, int height, RenderTextureFormat format = RenderTextureFormat.ARGB32,
            bool linear = false)
        {
            var key = (width, height, format, linear);
            if (_pool.TryGetValue(key, out var stack) && stack.Count > 0)
            {
                var rt = stack.Pop();
                _leased.Add(rt);
                return rt;
            }

            var created = new RenderTexture(width, height, 0, format,
                linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB)
            {
                useMipMap = false,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };
            created.Create();
            _all.Add(created);
            _leased.Add(created);
            return created;
        }

        public void Release(RenderTexture rt)
        {
            if (rt == null) return;
            if (!_leased.Remove(rt)) return;
            var key = (rt.width, rt.height, rt.format, !rt.sRGB);
            if (!_pool.TryGetValue(key, out var stack))
            {
                stack = new Stack<RenderTexture>();
                _pool[key] = stack;
            }
            stack.Push(rt);
        }

        public void Dispose()
        {
            foreach (var rt in _all)
            {
                if (rt != null) rt.Release();
            }
            _all.Clear();
            _pool.Clear();
            _leased.Clear();
        }
    }

    /// <summary>
    /// 原生数组池（NativeArray/byte[] 复用），降低 GC 压力。
    /// </summary>
    public static class NativeArrayPool
    {
        private static readonly Dictionary<(int, int), Stack<byte[]>> _pools = new Dictionary<(int, int), Stack<byte[]>>();
        // (elementSize, length)

        public static byte[] Rent(int size)
        {
            var key = (1, size);
            if (_pools.TryGetValue(key, out var stack) && stack.Count > 0) return stack.Pop();
            return new byte[size];
        }

        public static void Return(byte[] arr)
        {
            var key = (1, arr.Length);
            if (!_pools.TryGetValue(key, out var stack))
            {
                stack = new Stack<byte[]>();
                _pools[key] = stack;
            }
            if (stack.Count < 16) stack.Push(arr);
        }
    }
}
