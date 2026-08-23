using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Fosa.AvatarTextureOptimizer.Editor.Core
{
    /// <summary>
    /// EN: Owns scratch CPU/GPU resources; committed generated assets are explicitly detached.
    /// ZH: 管理临时 CPU/GPU 资源；提交的生成资产会被显式移出管理。
    /// </summary>
    internal sealed class ResourceScope : IDisposable
    {
        private readonly HashSet<Object> _objects = new HashSet<Object>();
        private readonly List<IDisposable> _disposables = new List<IDisposable>();
        private bool _disposed;

        public T Own<T>(T obj) where T : Object { if (obj != null) _objects.Add(obj); return obj; }
        public T Own<T>(T value) where T : IDisposable { if (value != null) _disposables.Add(value); return value; }
        public void Commit(Object obj) { if (obj != null) _objects.Remove(obj); }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            for (var i = _disposables.Count - 1; i >= 0; i--)
            {
                try { _disposables[i]?.Dispose(); } catch (Exception ex) { Debug.LogWarning("[ATO] Dispose failed: " + ex.Message); }
            }
            foreach (var obj in _objects)
            {
                if (obj == null) continue;
                if (obj is RenderTexture renderTexture && renderTexture.IsCreated()) renderTexture.Release();
                Object.DestroyImmediate(obj);
            }
            _objects.Clear();
            _disposables.Clear();
        }
    }
}
