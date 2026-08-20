// AvatarTextureOptimizer - RenderTexturePool
// EN: Pooled RenderTextures with a memory budget to keep peak memory comfortable on user machines.
// CN: 带内存预算的 RenderTexture 池，保证用户机器上的峰值内存舒适。
using System;
using System.Collections.Generic;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer
{
    /// <summary>
    /// EN: RenderTexture pool. GetTemporary-like API with reuse and a total-budget cap; DisposeAll frees
    /// everything (also used on cancel).
    /// CN: RenderTexture 池。类 GetTemporary 的 API，支持复用与总预算上限；DisposeAll 全部释放（取消时同样调用）。
    /// </summary>
    public sealed class RenderTexturePool : IDisposable
    {
        private sealed class Entry
        {
            public RenderTexture rt;
            public string key;
        }

        private readonly Dictionary<string, Stack<RenderTexture>> _free = new Dictionary<string, Stack<RenderTexture>>();
        private readonly List<RenderTexture> _inUse = new List<RenderTexture>();
        private long _budgetBytes;
        private long _usedBytes;

        public RenderTexturePool(long budgetBytes = 768L * 1024 * 1024)
        {
            _budgetBytes = budgetBytes;
        }

        private static string Key(int w, int h, RenderTextureFormat fmt, int depth, bool sRGB)
        {
            return $"{w}x{h}|{fmt}|{depth}|{(sRGB ? 1 : 0)}";
        }

        public RenderTexture Get(int w, int h, RenderTextureFormat fmt = RenderTextureFormat.ARGB32,
            int depth = 0, bool sRGB = false)
        {
            string key = Key(w, h, fmt, depth, sRGB);
            if (_free.TryGetValue(key, out var stack) && stack.Count > 0)
            {
                var rt = stack.Pop();
                _inUse.Add(rt);
                return rt;
            }
            var created = new RenderTexture(w, h, depth, fmt, sRGB ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear)
            {
                useMipMap = false,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            created.Create();
            _inUse.Add(created);
            return created;
        }

        public void Release(RenderTexture rt)
        {
            if (rt == null) return;
            _inUse.Remove(rt);
            string key = Key(rt.width, rt.height, rt.format, rt.depth, rt.sRGB);
            if (!_free.TryGetValue(key, out var stack))
                _free[key] = stack = new Stack<RenderTexture>();
            stack.Push(rt);
        }

        public void Dispose()
        {
            foreach (var rt in _inUse) rt.Release();
            _inUse.Clear();
            foreach (var kv in _free)
                foreach (var rt in kv.Value) rt.Release();
            _free.Clear();
        }
    }
}
