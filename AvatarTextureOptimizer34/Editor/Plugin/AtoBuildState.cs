// AvatarTextureOptimizer - AtoBuildState
// EN: Per-build mutable state shared by all stages (via BuildContext.GetState<T>()).
// CN: 一次构建内所有阶段共享的可变状态（经 BuildContext.GetState<T>() 获取）。
using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using Unity.Collections;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.Plugin
{
    /// <summary>
    /// EN: All data produced by the analysis stages and consumed by the baking/remap stages.
    /// CN: 分析阶段产出、烘焙/重映射阶段消费的全部数据。
    /// </summary>
    public class AtoBuildState : IDisposable
    {
        public BuildContext Ctx;
        public AvatarTextureOptimizer Component;
        public PlatformProfile Profile;      // 有效平台配置
        public AtoPlatform Platform;

        public bool Cancelled;

        // ---- Analysis results ----
        public List<Renderer> Renderers = new List<Renderer>();            // 参与优化的渲染器
        public List<MeshUvData> MeshUvData = new List<MeshUvData>();       // 每 (mesh, channel)
        public List<TextureRef> Textures = new List<TextureRef>();         // 去重后的贴图实例（引用）
        public List<TypeGroup> TypeGroups = new List<TypeGroup>();
        public List<UvGroup> UvGroups = new List<UvGroup>();
        public List<AnimationData> Animations = new List<AnimationData>(); // 动画分析结果

        // ---- Whitelist ----
        public HashSet<UnityEngine.Object> WhitelistObjects = new HashSet<UnityEngine.Object>();
        public HashSet<Texture> WhitelistedTextures = new HashSet<Texture>();

        // ---- Decode cache (LRU, memory budget) ----
        public TextureDecoder Decoder;

        // ---- Dedup ----
        public TextureRegistry Registry;
        public readonly Dictionary<Texture2D, Texture2D> TextureRemap = new Dictionary<Texture2D, Texture2D>();

        // ---- Combined material usage (classifier + animation) ----
        public readonly Dictionary<Material, MaterialUsage> MaterialUsages = new Dictionary<Material, MaterialUsage>();

        // ---- Native resources owned by this build (disposed at end / on cancel) ----
        public readonly List<NativeArray<byte>> NativeArrays = new List<NativeArray<byte>>();

        // ---- GPU metric self-test ----
        public bool GpuMetricsEnabled;

        public void Dispose()
        {
            Decoder?.Dispose();
            foreach (var na in NativeArrays)
            {
                if (na.IsCreated) na.Dispose();
            }
            NativeArrays.Clear();
        }

        /// <summary>EN: All textures referenced by optimized renderers (for dedup / report). / CN: 所有被优化渲染器引用的贴图。</summary>
        public IEnumerable<Texture> AllTextures()
        {
            var seen = new HashSet<Texture>();
            foreach (var t in Textures) if (t.texture != null && seen.Add(t.texture)) yield return t.texture;
        }
    }

    /// <summary>
    /// EN: Texture decode cache with a memory budget (LRU eviction). Decodes once per build.
    /// CN: 带内存预算的贴图解码缓存（LRU 淘汰），每次构建只解码一次。
    /// </summary>
    public sealed class TextureDecoder : IDisposable
    {
        private readonly long _budgetBytes;
        private long _usedBytes;
        private readonly LinkedList<Texture2D> _lru = new LinkedList<Texture2D>();
        private readonly Dictionary<Texture2D, LinkedListNode<Texture2D>> _map =
            new Dictionary<Texture2D, LinkedListNode<Texture2D>>();

        public TextureDecoder(long budgetBytes = 256L * 1024 * 1024)
        {
            _budgetBytes = budgetBytes;
        }

        /// <summary>
        /// EN: Returns a readable RGBA32 Texture2D copy (sRGB data as stored in the texture).
        /// CN: 返回可读的 RGBA32 副本（sRGB 原始数据）。
        /// </summary>
        public Texture2D Decode(Texture2D tex)
        {
            if (tex == null) return null;
            if (_map.TryGetValue(tex, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value;
            }
            var copy = DecodeToRgba32(tex);
            if (copy == null) return null;
            long size = (long)copy.width * copy.height * 4;
            _usedBytes += size;
            var n = _lru.AddFirst(copy);
            _map[tex] = n;
            EvictIfNeeded();
            return copy;
        }

        private void EvictIfNeeded()
        {
            while (_usedBytes > _budgetBytes && _lru.Count > 1)
            {
                var last = _lru.Last;
                long size = (long)last.Value.width * last.Value.height * 4;
                _usedBytes -= size;
                _map.Remove(last.Value);
                _lru.RemoveLast();
                UnityEngine.Object.DestroyImmediate(last.Value);
            }
        }

        public void Clear()
        {
            foreach (var t in _lru) UnityEngine.Object.DestroyImmediate(t);
            _lru.Clear();
            _map.Clear();
            _usedBytes = 0;
        }

        public void Dispose() => Clear();

        // EN: Readable copy via RenderTexture (handles non-readable & any format). The RT read-write space matches
        // the source's data space so the stored byte values survive the round trip unchanged.
        // CN: 经 RenderTexture 生成可读副本（支持不可读与任意格式）。RT 读写空间与源数据空间一致，
        //     保证存储字节在往返中不变。
        private static Texture2D DecodeToRgba32(Texture2D src)
        {
            try
            {
                int w = src.width, h = src.height;
                bool srgbData = src.isDataSRGB();
                var rw = srgbData ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear;
                var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, rw);
                var prev = RenderTexture.active;
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                var copy = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
                copy.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                copy.Apply();
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                return copy;
            }
            catch (Exception e)
            {
                AtoLog.Warn($"Failed to decode texture {src.name}: {e.Message}");
                return null;
            }
        }
    }
}
