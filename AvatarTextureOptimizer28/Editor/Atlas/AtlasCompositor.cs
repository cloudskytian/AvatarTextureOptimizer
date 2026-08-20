using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace net.fosa.ato.editor
{
    /// <summary>EN: One finished atlas: pixels plus the metadata the report needs. ZH: 一张完成的图集：像素与报告所需的元数据。</summary>
    public sealed class BakedAtlas
    {
        /// <summary>EN: The generated texture. ZH: 生成的贴图。</summary>
        public Texture2D Texture;
        /// <summary>EN: Semantic slot this atlas serves. ZH: 该图集服务的语义槽位。</summary>
        public TextureSlot Slot;
        /// <summary>EN: Classification driving format choice. ZH: 决定格式选择的分类。</summary>
        public TextureClass Class;
        /// <summary>EN: True when the atlas is sampled as sRGB. ZH: 该图集是否按 sRGB 采样。</summary>
        public bool SRGB;
        /// <summary>EN: Source textures replaced by this atlas. ZH: 被该图集替代的源贴图。</summary>
        public readonly List<AtoTexture> Sources = new List<AtoTexture>();
        /// <summary>EN: Islands baked into it. ZH: 烘入其中的岛数量。</summary>
        public int IslandCount;
        /// <summary>EN: Occupied fraction. ZH: 占用比例。</summary>
        public float Utilisation;
        /// <summary>EN: Mip levels actually generated. ZH: 实际生成的 mip 层数。</summary>
        public int MipCount;
    }

    /// <summary>
    /// EN: Bakes the pixels of an <see cref="AtlasLayout"/> into real textures - one per texture slot
    ///     present in the layout's type group, all sharing the identical island placement.
    ///
    ///     Mip policy: the number of mip levels is capped at log2(padding) + 1. This is a deliberate
    ///     decision. A mip level N texel averages 2^N base texels, so once 2^N exceeds the padding the
    ///     mip starts mixing neighbouring islands and produces visible colour bleeding at distance.
    ///     Rather than shipping that artefact we stop the chain where it is still correct; Unity then
    ///     samples the coarsest generated level for anything further away.
    ///
    /// ZH: 把 <see cref="AtlasLayout"/> 的像素烘焙成真实贴图——
    ///     该布局所属类型组中每个存在的槽位一张，全部共享完全相同的岛位置。
    ///
    ///     Mip 策略：mip 层数上限为 log2(padding) + 1。这是一个刻意的决定。
    ///     mip 第 N 层的一个纹素平均了 2^N 个基础纹素，因此一旦 2^N 超过 padding，
    ///     该 mip 就会开始混合相邻的岛，在远处产生可见的串色。
    ///     与其把这个瑕疵交付出去，不如在仍然正确的地方截断 mip 链；
    ///     更远的距离 Unity 会采样已生成的最粗一级。
    /// </summary>
    public sealed class AtlasCompositor
    {
        private readonly GPUTextureIO _io;
        private readonly ATOLog _log;
        private readonly ATOProgress _progress;

        /// <summary>EN: Construct. ZH: 构造。</summary>
        public AtlasCompositor(GPUTextureIO io, ATOLog log, ATOProgress progress)
        {
            _io = io;
            _log = log;
            _progress = progress;
        }

        /// <summary>
        /// EN: Bake one atlas per slot for a layout. Returns them keyed by slot.
        /// ZH: 为一个布局按槽位各烘焙一张图集，按槽位返回。
        /// </summary>
        public Dictionary<TextureSlot, BakedAtlas> Bake(AtlasLayout layout, int atlasIndex, string avatarName)
        {
            var result = new Dictionary<TextureSlot, BakedAtlas>();
            var slots = layout.Groups.SelectMany(g => g.Slots).Distinct().ToList();

            foreach (var slot in slots)
            {
                _progress.ThrowIfCancelled();
                var baked = BakeSlot(layout, slot, atlasIndex, avatarName);
                if (baked != null) result[slot] = baked;
            }
            return result;
        }

        private BakedAtlas BakeSlot(AtlasLayout layout, TextureSlot slot, int atlasIndex, string avatarName)
        {
            int w = layout.Size.Width, h = layout.Size.Height;
            var pixels = new Color[w * h];
            var valid = new bool[w * h];

            var sources = new List<AtoTexture>();
            bool srgb = false, anyAlpha = false;
            TextureClass cls = TextureClass.OpaqueColor;
            int islandCount = 0;
            bool first = true;

            foreach (var group in layout.Groups)
            {
                if (!group.Textures.TryGetValue(slot, out var texList) || texList.Count == 0) continue;

                // EN: Several textures can occupy one slot when animation swaps them. They must all share
                //     the layout, so the first one defines the atlas and the rest are written into a
                //     parallel atlas by a later call with the same layout. Here we bake the primary.
                // ZH: 动画切换时同一槽位可能有多张贴图。它们必须共享布局，
                //     因此第一张定义该图集，其余的由后续使用同一布局的调用写入并行图集。此处烘焙主贴图。
                var tex = texList[0].Representative;
                sources.Add(tex);
                if (first) { srgb = tex.SRGB; cls = tex.Class; first = false; }
                if (tex.HasAlpha) anyAlpha = true;

                var decoded = _io.Decode(tex.Source, tex.SRGB);

                foreach (var island in group.Islands)
                {
                    if (island.AtlasIndex < 0) continue;
                    islandCount++;
                    BlitIsland(decoded, tex, island, pixels, valid, w, h);
                }
            }

            if (islandCount == 0) return null;

            if (cls == TextureClass.OpaqueColor && anyAlpha) cls = TextureClass.TransparentColor;

            // EN: Extend the island colours over the whole atlas so no mip level ever samples void.
            // ZH: 把岛的颜色外扩到整张图集，使任何 mip 层都不会采样到空白。
            PullPush.Fill(pixels, valid, w, h, keepAlphaZero: cls == TextureClass.TransparentColor);

            int mipCount = MipCountFor(layout.Padding, w, h);
            var tex2d = BuildTexture(pixels, w, h, srgb, mipCount,
                $"{ATOConstants.AtlasNamePrefix}{avatarName}_{slot}_{atlasIndex}");

            var baked = new BakedAtlas
            {
                Texture = tex2d,
                Slot = slot,
                Class = slot == TextureSlot.Normal ? TextureClass.Normal : cls,
                SRGB = srgb,
                IslandCount = islandCount,
                Utilisation = layout.Utilisation,
                MipCount = mipCount,
            };
            baked.Sources.AddRange(sources);

            _log.Detail($"Baked {tex2d.name}: {w}x{h} mips={mipCount} islands={islandCount} " +
                        $"sources={sources.Count} utilisation={layout.Utilisation * 100f:F1}%");
            return baked;
        }

        /// <summary>
        /// EN: Number of mip levels that stay free of cross-island bleeding for a given padding.
        /// ZH: 在给定 padding 下不会产生跨岛渗色的 mip 层数。
        /// </summary>
        public static int MipCountFor(int paddingPx, int w, int h)
        {
            int safe = Mathf.Max(1, Mathf.FloorToInt(Mathf.Log(Mathf.Max(1, paddingPx), 2f)) + 1);
            int full = Mathf.FloorToInt(Mathf.Log(Mathf.Max(w, h), 2f)) + 1;
            return Mathf.Clamp(safe, 1, full);
        }

        private void BlitIsland(DecodedTexture src, AtoTexture tex, UVIsland island,
            Color[] dst, bool[] valid, int atlasW, int atlasH)
        {
            var srcRect = IslandScaleSolver.PixelRect(island, src.Width, src.Height);
            var dstRect = island.PackedRect;
            if (dstRect.width <= 0 || dstRect.height <= 0) return;

            var tile = ImageOps.Extract(src, srcRect);
            bool premultiply = tex.Class == TextureClass.TransparentColor;

            int tw = island.PackedRotated ? dstRect.height : dstRect.width;
            int th = island.PackedRotated ? dstRect.width : dstRect.height;

            Tile resampled;
            if (tex.Class == TextureClass.Normal)
            {
                var n = ImageOps.DecodeNormals(tile, !tex.UsedChannels.B && tex.UsedChannels.A);
                var enc = ImageOps.EncodeNormals(n, tile.W, tile.H);
                var small = ImageOps.Downsample(enc, tw, th, false);
                resampled = ImageOps.EncodeNormals(ImageOps.DecodeNormals(small, false), small.W, small.H);
            }
            else
            {
                resampled = ImageOps.Downsample(tile, tw, th, premultiply);
            }

            Parallel.For(0, dstRect.height, y =>
            {
                int ay = dstRect.y + y;
                if (ay < 0 || ay >= atlasH) return;
                for (int x = 0; x < dstRect.width; x++)
                {
                    int ax = dstRect.x + x;
                    if (ax < 0 || ax >= atlasW) continue;

                    // EN: A rotated island is written transposed; the mesh UVs are swapped to match, so
                    //     no tangent recomputation is ever required.
                    // ZH: 旋转的岛以转置方式写入；网格 UV 会做相应交换，因此永远不需要重算切线。
                    int sx = island.PackedRotated ? y : x;
                    int sy = island.PackedRotated ? x : y;
                    sx = Mathf.Clamp(sx, 0, resampled.W - 1);
                    sy = Mathf.Clamp(sy, 0, resampled.H - 1);

                    int di = ay * atlasW + ax;
                    dst[di] = resampled.P[sy * resampled.W + sx];
                    valid[di] = true;
                }
            });
        }

        /// <summary>
        /// EN: Build the Texture2D and generate the capped mip chain ourselves with an alpha-aware box
        ///     filter, instead of letting Unity do it. Unity's automatic generation would produce the
        ///     full chain and would not premultiply alpha, both of which we explicitly do not want.
        /// ZH: 自行构建 Texture2D 并用感知 alpha 的盒式滤波生成受限的 mip 链，而不是交给 Unity。
        ///     Unity 的自动生成会产出完整链且不做 alpha 预乘，这两点都是我们明确不想要的。
        /// </summary>
        private static Texture2D BuildTexture(Color[] pixels, int w, int h, bool srgb, int mipCount, string name)
        {
            var tex = new Texture2D(w, h, srgb ? TextureFormat.RGBA32 : TextureFormat.RGBA32, mipCount, linear: !srgb)
            {
                name = name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };

            var level = new Tile(w, h);
            Array.Copy(pixels, level.P, pixels.Length);

            for (int mip = 0; mip < mipCount; mip++)
            {
                var data = new Color32[level.W * level.H];
                Parallel.For(0, level.P.Length, i =>
                {
                    var c = level.P[i];
                    if (srgb)
                    {
                        data[i] = new Color32(
                            ToByte(LinearToSrgb(c.r)), ToByte(LinearToSrgb(c.g)),
                            ToByte(LinearToSrgb(c.b)), ToByte(c.a));
                    }
                    else
                    {
                        data[i] = new Color32(ToByte(c.r), ToByte(c.g), ToByte(c.b), ToByte(c.a));
                    }
                });
                tex.SetPixelData(data, mip);

                if (mip + 1 < mipCount)
                    level = ImageOps.Downsample(level, Mathf.Max(1, level.W / 2), Mathf.Max(1, level.H / 2), true);
            }

            tex.Apply(false, false);
            return tex;
        }

        private static byte ToByte(float v) => (byte)Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);

        private static float LinearToSrgb(float c)
        {
            c = Mathf.Max(0f, c);
            return c <= 0.0031308f ? c * 12.92f : 1.055f * Mathf.Pow(c, 1f / 2.4f) - 0.055f;
        }
    }
}
