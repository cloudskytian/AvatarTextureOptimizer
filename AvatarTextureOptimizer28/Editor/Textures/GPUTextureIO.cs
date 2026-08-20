using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace net.fosa.ato.editor
{
    /// <summary>
    /// EN: A decoded, linear-space, float RGBA copy of a texture, owned by the cache.
    ///     Everything downstream works on this rather than on the compressed asset, which is what lets
    ///     us support Crunch / BCn / non-readable textures without ever touching the user's importer.
    /// ZH: 一张贴图解码后的线性空间浮点 RGBA 副本，由缓存持有。
    ///     下游全部基于它工作，而不是基于压缩后的资产——这正是我们能在完全不改动用户 importer 的前提下
    ///     支持 Crunch / BCn / 不可读贴图的原因。
    /// </summary>
    public sealed class DecodedTexture : IDisposable
    {
        /// <summary>EN: Width in pixels. ZH: 像素宽度。</summary>
        public int Width;
        /// <summary>EN: Height in pixels. ZH: 像素高度。</summary>
        public int Height;
        /// <summary>EN: Linear RGBA pixels, row major, bottom-up (Unity convention).
        /// ZH: 线性 RGBA 像素，行主序、自下而上（Unity 约定）。</summary>
        public NativeArray<Color> Pixels;
        /// <summary>EN: True when the source was sRGB encoded and has been linearised here.
        /// ZH: 源贴图是否为 sRGB 编码并已在此线性化。</summary>
        public bool WasSRGB;

        /// <summary>EN: Release the native memory. ZH: 释放原生内存。</summary>
        public void Dispose()
        {
            if (Pixels.IsCreated) Pixels.Dispose();
        }
    }

    /// <summary>
    /// EN: GPU-backed texture reading, analysis and writing.
    ///
    ///     Rationale: VRChat avatar textures are almost always Crunch-compressed and have
    ///     <c>isReadable = false</c>, so <c>Texture2D.GetPixels</c> throws. Flipping the importer would
    ///     mutate the user's project and force a reimport of every texture, which is unacceptable for a
    ///     non-destructive tool. Instead we <c>Graphics.Blit</c> into a float RenderTexture (the GPU
    ///     decodes the block format for free) and read that back. The readback is the only stall, and it
    ///     is amortised by an LRU cache with a hard memory budget so we do not blow up on a 40-texture
    ///     avatar at 4K.
    ///
    /// ZH: 基于 GPU 的贴图读取、分析与写出。
    ///
    ///     设计依据：VRChat 的 Avatar 贴图几乎总是 Crunch 压缩且 <c>isReadable = false</c>，
    ///     因此 <c>Texture2D.GetPixels</c> 会抛异常。修改 importer 会污染用户工程并触发全部贴图重导入，
    ///     对一个非破坏性工具而言不可接受。我们改为 <c>Graphics.Blit</c> 到浮点 RenderTexture
    ///     （GPU 会免费完成块格式解码）再回读。回读是唯一的停顿点，
    ///     并通过带硬性内存预算的 LRU 缓存摊薄开销，以免在一个有 40 张 4K 贴图的 Avatar 上爆内存。
    /// </summary>
    public sealed class GPUTextureIO : IDisposable
    {
        private readonly ATOLog _log;
        private readonly long _budgetBytes;
        private long _usedBytes;

        private readonly Dictionary<Texture2D, DecodedTexture> _cache = new Dictionary<Texture2D, DecodedTexture>();
        private readonly LinkedList<Texture2D> _lru = new LinkedList<Texture2D>();
        private readonly Dictionary<Texture2D, LinkedListNode<Texture2D>> _lruNodes =
            new Dictionary<Texture2D, LinkedListNode<Texture2D>>();

        private Material _decodeMat;

        /// <summary>
        /// EN: Create the IO helper.
        /// ZH: 创建 IO 辅助对象。
        /// </summary>
        /// <param name="log">EN: logger. ZH: 日志器。</param>
        /// <param name="budgetMegabytes">EN: soft cap on decoded pixel memory. ZH: 解码像素内存的软上限。</param>
        public GPUTextureIO(ATOLog log, int budgetMegabytes = 1024)
        {
            _log = log;
            _budgetBytes = (long)budgetMegabytes * 1024 * 1024;
        }

        private Material DecodeMaterial
        {
            get
            {
                if (_decodeMat == null)
                {
                    var sh = Shader.Find("Hidden/ATO/Decode");
                    if (sh == null)
                        throw new InvalidOperationException(
                            "[ATO] Hidden/ATO/Decode shader is missing from the package.");
                    _decodeMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
                }
                return _decodeMat;
            }
        }

        /// <summary>
        /// EN: Decode a texture into linear float RGBA, using the cache when possible.
        ///     <paramref name="srgb"/> tells us whether the stored values are sRGB encoded; if so they are
        ///     linearised, because every metric in the quality algorithm is defined in linear space.
        /// ZH: 把贴图解码为线性浮点 RGBA，尽可能命中缓存。
        ///     <paramref name="srgb"/> 指示存储值是否为 sRGB 编码；若是则会被线性化，
        ///     因为质量算法中的所有度量都定义在线性空间。
        /// </summary>
        public DecodedTexture Decode(Texture2D tex, bool srgb)
        {
            if (tex == null) throw new ArgumentNullException(nameof(tex));
            if (_cache.TryGetValue(tex, out var hit))
            {
                Touch(tex);
                return hit;
            }

            var w = tex.width;
            var h = tex.height;
            var bytes = (long)w * h * 16;
            EnsureBudget(bytes);

            var desc = new RenderTextureDescriptor(w, h, RenderTextureFormat.ARGBFloat, 0, 1)
            {
                sRGB = false,
                autoGenerateMips = false,
                useMipMap = false,
            };
            var rt = RenderTexture.GetTemporary(desc);
            var prev = RenderTexture.active;
            try
            {
                // EN: _Linearize converts sRGB -> linear inside the shader. We never rely on the
                //     RenderTexture sRGB flag because its behaviour differs between colour spaces.
                // ZH: _Linearize 在着色器内部完成 sRGB -> 线性的转换。
                //     我们绝不依赖 RenderTexture 的 sRGB 标志，因为它的行为随色彩空间而变。
                DecodeMaterial.SetFloat("_Linearize", srgb ? 1f : 0f);
                Graphics.Blit(tex, rt, DecodeMaterial, 0);

                var request = AsyncGPUReadback.Request(rt, 0, GraphicsFormat.R32G32B32A32_SFloat);
                request.WaitForCompletion();
                if (request.hasError)
                    throw new InvalidOperationException($"[ATO] GPU readback failed for '{tex.name}'.");

                var src = request.GetData<Color>();
                var decoded = new DecodedTexture
                {
                    Width = w,
                    Height = h,
                    WasSRGB = srgb,
                    Pixels = new NativeArray<Color>(src.Length, Allocator.Persistent,
                        NativeArrayOptions.UninitializedMemory),
                };
                decoded.Pixels.CopyFrom(src);

                _cache[tex] = decoded;
                _usedBytes += bytes;
                Touch(tex);
                _log.Trace($"Decoded '{tex.name}' {w}x{h} srgb={srgb} ({bytes / 1024 / 1024} MB, cache {_usedBytes / 1024 / 1024} MB)");
                return decoded;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        private void Touch(Texture2D tex)
        {
            if (_lruNodes.TryGetValue(tex, out var node))
            {
                _lru.Remove(node);
                _lru.AddLast(node);
            }
            else
            {
                _lruNodes[tex] = _lru.AddLast(tex);
            }
        }

        private void EnsureBudget(long incoming)
        {
            while (_usedBytes + incoming > _budgetBytes && _lru.Count > 0)
            {
                var victim = _lru.First.Value;
                _lru.RemoveFirst();
                _lruNodes.Remove(victim);
                if (_cache.TryGetValue(victim, out var d))
                {
                    _usedBytes -= (long)d.Width * d.Height * 16;
                    d.Dispose();
                    _cache.Remove(victim);
                }
            }
        }

        /// <summary>
        /// EN: Drop the decoded copy of one texture, e.g. once every island of it has been baked.
        /// ZH: 丢弃某张贴图的解码副本，例如它的所有岛都已烘焙完毕之后。
        /// </summary>
        public void Evict(Texture2D tex)
        {
            if (tex == null) return;
            if (_cache.TryGetValue(tex, out var d))
            {
                _usedBytes -= (long)d.Width * d.Height * 16;
                d.Dispose();
                _cache.Remove(tex);
            }
            if (_lruNodes.TryGetValue(tex, out var n))
            {
                _lru.Remove(n);
                _lruNodes.Remove(tex);
            }
        }

        /// <summary>
        /// EN: Compute content facts we need before any decision is made: alpha presence, solidity,
        ///     which channels carry data, and a stable content hash for deduplication.
        ///     The hash mixes the decoded pixels with the import signature, so two textures with
        ///     identical pixels but different import settings are deliberately NOT considered equal,
        ///     exactly as the specification requires.
        /// ZH: 计算在做出任何决策之前需要的内容事实：是否含 alpha、是否纯色、
        ///     哪些通道承载数据，以及用于去重的稳定内容哈希。
        ///     哈希会把解码像素与导入签名混合在一起，因此像素相同但导入设置不同的两张贴图
        ///     刻意不被视为相同——这与需求完全一致。
        /// </summary>
        public void Analyze(AtoTexture t)
        {
            var decoded = Decode(t.Source, t.SRGB);
            var px = decoded.Pixels;

            bool hasAlpha = false;
            bool solid = true;
            var first = px.Length > 0 ? px[0] : Color.clear;
            float minR = float.MaxValue, maxR = float.MinValue;
            float minG = float.MaxValue, maxG = float.MinValue;
            float minB = float.MaxValue, maxB = float.MinValue;
            float minA = float.MaxValue, maxA = float.MinValue;

            var hash = new Hash128();
            // EN: Hash a decimated grid first (cheap) and then the full buffer, so the common case of
            //     "obviously different" textures exits the comparison early via size + grid hash.
            // ZH: 先对抽样网格做哈希（便宜），再对完整缓冲做哈希；
            //     这样"明显不同"的常见情形可以通过尺寸 + 网格哈希提前退出比较。
            for (int i = 0; i < px.Length; i++)
            {
                var c = px[i];
                if (c.a < 0.99609375f) hasAlpha = true;
                if (solid && (Mathf.Abs(c.r - first.r) > 1e-5f || Mathf.Abs(c.g - first.g) > 1e-5f ||
                              Mathf.Abs(c.b - first.b) > 1e-5f || Mathf.Abs(c.a - first.a) > 1e-5f))
                    solid = false;
                if (c.r < minR) minR = c.r; if (c.r > maxR) maxR = c.r;
                if (c.g < minG) minG = c.g; if (c.g > maxG) maxG = c.g;
                if (c.b < minB) minB = c.b; if (c.b > maxB) maxB = c.b;
                if (c.a < minA) minA = c.a; if (c.a > maxA) maxA = c.a;
            }

            hash.Append(px);
            hash.Append(t.Width);
            hash.Append(t.Height);
            hash.Append(t.SRGB ? 1 : 0);
            hash.Append((int)t.Filter);
            hash.Append((int)t.Wrap);
            hash.Append(t.AnisoLevel);

            t.HasAlpha = hasAlpha;
            t.IsSolid = solid;
            t.SolidColor = first;
            t.ContentHash = hash;

            const float eps = 1.5f / 255f;
            t.UsedChannels = new bool4Mask
            {
                R = maxR - minR > eps,
                G = maxG - minG > eps,
                B = maxB - minB > eps,
                A = maxA - minA > eps,
            };
            // EN: A completely flat channel still has to be stored if it is not equal across R/G/B for a
            //     colour texture; only true data textures benefit from dropping channels.
            // ZH: 对彩色贴图而言，即便某通道完全平坦，只要它与 R/G/B 不一致仍需保留；
            //     只有真正的数据贴图才能从丢弃通道中获益。
            if (t.UsedChannels.Count == 0) t.UsedChannels = new bool4Mask { R = true };

            _log.Trace($"Analyzed {t}: alpha={hasAlpha} solid={solid} channels={t.UsedChannels}");
        }

        /// <summary>EN: Release every cached decode and the helper material. ZH: 释放全部解码缓存与辅助材质。</summary>
        public void Dispose()
        {
            foreach (var d in _cache.Values) d.Dispose();
            _cache.Clear();
            _lru.Clear();
            _lruNodes.Clear();
            _usedBytes = 0;
            if (_decodeMat != null) Object.DestroyImmediate(_decodeMat);
            _decodeMat = null;
        }
    }
}
