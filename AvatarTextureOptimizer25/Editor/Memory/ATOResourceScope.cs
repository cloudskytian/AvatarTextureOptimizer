// Avatar Texture Optimizer / 头像贴图优化器
// Scoped resource management: RenderTexture pooling + native/Unity object
// tracking. Every temporary RenderTexture, NativeArray/NativeList, Texture2D,
// Material and RenderTexture is registered here and deterministically released
// when the scope (or child scope) is disposed -- including on cancellation,
// keeping memory pressure and leaks under control on real user machines.
// 作用域化资源管理：RenderTexture 池 + Native/Unity 对象跟踪。所有临时
// RenderTexture、Native 容器、Texture2D、Material 都会登记，并在作用域
// Dispose 时（含取消路径）确定性释放，控制真实用户机器上的内存压力与泄漏。

using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Deterministic lifetime tracking for temporary build resources.
    /// 构建期临时资源的确定性生命周期管理。
    /// </summary>
    public sealed class ATOResourceScope : IDisposable
    {
        private readonly List<IDisposable> _disposables = new List<IDisposable>();
        private readonly List<Object> _unityObjects = new List<Object>();
        private readonly List<RenderTexture> _rts = new List<RenderTexture>();
        private bool _disposed;

        private static readonly Stack<ATOResourceScope> _stack = new Stack<ATOResourceScope>();

        /// <summary>Current scope (nullable). / 当前作用域（可空）。</summary>
        public static ATOResourceScope Current => _stack.Count > 0 ? _stack.Peek() : null;

        /// <summary>Push a new scope as current. / 推入新的当前作用域。</summary>
        public static ATOResourceScope Push()
        {
            var s = new ATOResourceScope();
            _stack.Push(s);
            return s;
        }

        /// <summary>Register an IDisposable (incl. NativeArray via wrapper). / 登记 IDisposable（NativeArray 请用包装器）。</summary>
        public T Track<T>(T disposable) where T : IDisposable
        {
            if (disposable != null) _disposables.Add(disposable);
            return disposable;
        }

        /// <summary>Register a temporary UnityEngine.Object to destroy on dispose. / 登记销毁的临时 Unity 对象。</summary>
        public T Track<T>(T obj) where T : Object
        {
            if (obj != null) _unityObjects.Add(obj);
            return obj;
        }

        /// <summary>Register a RenderTexture (released, not destroyed unless owned). / 登记 RenderTexture（释放而非销毁）。</summary>
        public RenderTexture TrackRT(RenderTexture rt, bool destroyOnDispose = true)
        {
            if (rt == null) return null;
            _rts.Add(rt);
            if (destroyOnDispose) _unityObjects.Add(rt);
            return rt;
        }

        /// <summary>Adopt an existing NativeArray into a disposable wrapper. / 接管 NativeArray 的释放。</summary>
        public NativeArrayGuard<T> TrackNative<T>(NativeArray<T> arr) where T : struct
        {
            var g = new NativeArrayGuard<T>(arr);
            _disposables.Add(g);
            return g;
        }

        /// <summary>Guard wrapper making NativeArray disposable through scope tracking. / 让 NativeArray 可被作用域跟踪的包装。</summary>
        public sealed class NativeArrayGuard<T> : IDisposable where T : struct
        {
            public NativeArray<T> Array;
            public NativeArrayGuard(NativeArray<T> a) { Array = a; }
            public void Dispose() { if (Array.IsCreated) Array.Dispose(); Array = default; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            for (int i = _disposables.Count - 1; i >= 0; i--)
            {
                try { _disposables[i].Dispose(); } catch (Exception e) { ATOLog.Verbose("dispose error: " + e); }
            }
            foreach (var rt in _rts)
            {
                try { if (rt != null && rt.IsCreated()) { rt.Release(); } } catch { /* best effort */ }
            }
            foreach (var o in _unityObjects)
            {
                try { if (o != null) Object.DestroyImmediate(o); } catch { /* best effort */ }
            }
            _disposables.Clear();
            _unityObjects.Clear();
            _rts.Clear();
            // Only pop ourselves if we are the current scope; be tolerant of misuse.
            // 只有自己位于栈顶时才弹出；对误用保持宽容。
            if (_stack.Count > 0 && ReferenceEquals(_stack.Peek(), this)) _stack.Pop();
        }
    }

    /// <summary>
    /// Very small RenderTexture pool to avoid repeated allocations between the
    /// hundreds of island evaluations in a single build.
    /// 小型 RenderTexture 池：避免单次构建中数百次岛评估反复分配显存。
    /// </summary>
    public sealed class ATORtPool : IDisposable
    {
        private readonly Dictionary<string, Stack<RenderTexture>> _pool = new Dictionary<string, Stack<RenderTexture>>();
        private readonly List<RenderTexture> _all = new List<RenderTexture>();
        private long _budgetBytes;
        private long _usedBytes;

        /// <summary>VRAM budget for pooled RTs in bytes. / 池显存预算（字节）。</summary>
        public long BudgetBytes
        {
            get => _budgetBytes;
            set => _budgetBytes = Math.Max(0, value);
        }

        public ATORtPool(long budgetBytes)
        {
            _budgetBytes = budgetBytes;
        }

        private static string Key(int w, int h, RenderTextureFormat fmt, RenderTextureReadWrite rw, int depth, bool mips)
            => $"{w}x{h}|{fmt}|{rw}|d{depth}|m{(mips ? 1 : 0)}";

        private static long EstimateBytes(int w, int h, RenderTextureFormat fmt)
        {
            int bpp;
            switch (fmt)
            {
                case RenderTextureFormat.ARGBFloat: bpp = 16; break;
                case RenderTextureFormat.RGFloat: bpp = 8; break;
                case RenderTextureFormat.RFloat: bpp = 4; break;
                case RenderTextureFormat.ARGBHalf: bpp = 8; break;
                default: bpp = 4; break; // ARGB32 and friends / ARGB32 等
            }
            return (long)w * h * bpp * 4 / 3; // +mips / 含 mipmap 余量
        }

        /// <summary>Rent a temporary RenderTexture. / 租借临时 RenderTexture。</summary>
        public RenderTexture Rent(int w, int h, RenderTextureFormat fmt = RenderTextureFormat.ARGB32,
            RenderTextureReadWrite rw = RenderTextureReadWrite.sRGB, int depth = 0, bool mips = false)
        {
            w = Mathf.Max(1, w);
            h = Mathf.Max(1, h);
            var key = Key(w, h, fmt, rw, depth, mips);
            if (_pool.TryGetValue(key, out var stack) && stack.Count > 0)
            {
                var rt0 = stack.Pop();
                if (rt0 != null && rt0.IsCreated())
                {
                    rt0.DiscardContents();
                    // Renters may mutate sampling state (e.g. the composer's
                    // viewport blit switches to Point); reset to pool defaults.
                    // 租用方可能改动采样状态（如合成器视口 blit 切成 Point），
                    // 归还后重置为池默认。
                    rt0.filterMode = FilterMode.Bilinear;
                    rt0.wrapMode = TextureWrapMode.Clamp;
                    return rt0;
                }
            }
            var est = EstimateBytes(w, h, fmt);
            if (_usedBytes + est > _budgetBytes)
            {
                Trim();
            }
            var nrt = new RenderTexture(w, h, depth, fmt, rw)
            {
                useMipMap = mips,
                autoGenerateMips = mips,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            nrt.Create();
            _all.Add(nrt);
            _usedBytes += est;
            return nrt;
        }

        /// <summary>Return an RT to the pool. / 归还 RenderTexture。</summary>
        public void Return(RenderTexture rt)
        {
            if (rt == null) return;
            var key = Key(rt.width, rt.height, rt.format, rt.sRGB ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear,
                rt.depth, rt.useMipMap);
            if (!_pool.TryGetValue(key, out var stack))
            {
                stack = new Stack<RenderTexture>();
                _pool[key] = stack;
            }
            rt.DiscardContents();
            stack.Push(rt);
        }

        /// <summary>Drop half of the pooled RTs (simple budget trim). / 释放掉一半池内 RT（简单预算裁剪）。</summary>
        public void Trim()
        {
            foreach (var kv in _pool)
            {
                var stack = kv.Value;
                int keep = stack.Count / 2;
                while (stack.Count > keep)
                {
                    var rt = stack.Pop();
                    if (rt != null)
                    {
                        _usedBytes -= EstimateBytes(rt.width, rt.height, rt.format);
                        rt.Release();
                        Object.DestroyImmediate(rt);
                        _all.Remove(rt);
                    }
                }
            }
        }

        public void Dispose()
        {
            foreach (var rt in _all)
            {
                if (rt == null) continue;
                try { rt.Release(); Object.DestroyImmediate(rt); } catch { /* best effort */ }
            }
            _all.Clear();
            _pool.Clear();
            _usedBytes = 0;
        }
    }
}
