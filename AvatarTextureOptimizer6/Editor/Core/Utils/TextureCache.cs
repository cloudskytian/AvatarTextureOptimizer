using System;
using System.Collections.Generic;
using UnityEngine;

namespace NetFosa.AvatarTextureOptimizer.Editor.Utils
{
    /// <summary>
    /// 贴图解码缓存：避免同一张贴图被重复解码。按 Texture 实例缓存 ARGB32 像素。
    /// Caches decoded (ARGB32) pixels per texture to avoid repeated decoding.
    /// </summary>
    public sealed class TextureCache : IDisposable
    {
        private readonly Dictionary<Texture, Color32[]> _cache = new Dictionary<Texture, Color32[]>(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<Texture, Texture2D> _readableCopies = new Dictionary<Texture, Texture2D>(ReferenceEqualityComparer.Instance);
        private readonly HashSet<Texture> _decoding = new HashSet<Texture>(ReferenceEqualityComparer.Instance);

        /// <summary>获取贴图 ARGB32 像素（必要时创建可读拷贝并解码）。</summary>
        public Color32[] GetPixels(Texture texture, out int width, out int height)
        {
            if (_cache.TryGetValue(texture, out var px))
            {
                width = texture.width;
                height = texture.height;
                return px;
            }
            var readable = GetReadable(texture);
            width = readable.width;
            height = readable.height;
            px = readable.GetPixels32();
            _cache[texture] = px;
            return px;
        }

        public bool TryGetCached(Texture texture, out Color32[] px)
        {
            return _cache.TryGetValue(texture, out px);
        }

        /// <summary>创建可读 Texture2D 拷贝（处理 non-readable 原图；设置 import 时保证 readable 由调用方处理）。</summary>
        public Texture2D GetReadable(Texture texture)
        {
            if (_readableCopies.TryGetValue(texture, out var copy)) return copy;

            var tex2d = texture as Texture2D;
            if (tex2d == null)
            {
                throw new NotSupportedException($"[ATO] Texture {texture.name} is not a Texture2D ({texture.GetType().Name}); it should have been whitelisted earlier.");
            }

            Texture2D result;
            try
            {
                if (tex2d.isReadable)
                {
                    result = tex2d; // 可直接读
                }
                else
                {
                    // 复制一份可读的
                    var rt = RenderTexture.GetTemporary(tex2d.width, tex2d.height, 0, RenderTextureFormat.ARGB32,
                        tex2d.colorSpace == ColorSpace.Linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
                    Graphics.Blit(tex2d, rt);
                    var prev = RenderTexture.active;
                    RenderTexture.active = rt;
                    result = new Texture2D(tex2d.width, tex2d.height, TextureFormat.RGBA32, false, tex2d.colorSpace == ColorSpace.Linear);
                    result.ReadPixels(new Rect(0, 0, tex2d.width, tex2d.height), 0, 0);
                    result.Apply(false, false);
                    RenderTexture.active = prev;
                    RenderTexture.ReleaseTemporary(rt);
                    _readableCopies[tex2d] = result;
                }
            }
            catch (Exception e)
            {
                throw new InvalidOperationException($"[ATO] Failed to read texture {texture.name}: {e.Message}", e);
            }

            _readableCopies[texure] = result;
            return result;
        }

        public void Dispose()
        {
            foreach (var kv in _readableCopies)
            {
                var copy = kv.Value;
                if (copy != null && !ReferenceEquals(copy, kv.Key) && copy != null)
                {
                    UnityEngine.Object.DestroyImmediate(copy);
                }
            }
            _readableCopies.Clear();
            _cache.Clear();
        }
    }

    /// <summary>按引用比较的相等比较器（Texture 是 UnityEngine.Object，用实例地址）。</summary>
    internal sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
        public new bool Equals(object x, object y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
