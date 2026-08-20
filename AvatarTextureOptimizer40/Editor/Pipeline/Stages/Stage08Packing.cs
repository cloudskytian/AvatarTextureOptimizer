using System.Collections.Generic;
using System.Linq;
using Fosa.Ato.Editor.Analysis;
using Fosa.Ato.Editor.Packing;
using Fosa.Ato.Editor.i18n;
using Unity.Collections;
using UnityEngine;

namespace Fosa.Ato.Editor.Pipeline.Stages
{
    /// <summary>
    /// Stage 08: Pack islands into atlases per type group.
    ///
    /// The ATOMIC unit of packing is ONE SOURCE TEXTURE and all its UV groups. This is required
    /// because a material texture slot can only point at one output texture: if one source texture's
    /// islands were split across two atlases, the material reference would be wrong. The spec
    /// therefore says: "to guarantee all islands from the same texture are in a single atlas, first
    /// compute the total rasterized UV area of all textures waiting in the current queue."
    ///
    /// For each type group:
    ///  - bundle UV groups by source texture,
    ///  - sort bundles by total rasterized area desc, then longest edge desc,
    ///  - compute the queue's total UV area and discard candidate atlases smaller than it,
    ///  - sort candidates by area asc, then long/short ratio asc (most square first),
    ///  - pack each bundle atomically; the first candidate that fits every bundle becomes the atlas,
    ///  - if a bundle won't fit the remaining space of the largest atlas, open/reuse another same-type
    ///    queue and keep trying smaller bundles; if a single bundle alone doesn't fit the largest atlas,
    ///    abandon atlasing for its UV group(s) and keep scaled standalone textures (+ warning).
    /// 装箱的原子单位是【一张源贴图 + 它的全部 UV 组】。因为一个材质贴图槽只能指向一张输出贴图，
    /// 同一源贴图的岛绝不能拆到两个图集。按类型组打包、面积/边长降序、候选图集按面积与长宽比升序。
    /// </summary>
    internal sealed class Stage08Packing : IStage
    {
        public string Name => "ATO/08 Packing atlases";
        public float Weight => 5f;

        public void Run(AtoPipeline p)
        {
            if (!p.Settings.GenerateAtlas)
            {
                AtoLog.Info("Atlas generation disabled; producing scaled standalone textures. / 已关闭图集生成，输出缩放后的独立贴图。");
                return;
            }

            var platform = p.CurrentPlatform;
            int maxEdge = platform == Runtime.AtoPlatform.PC ? p.Settings.MaxAtlasSizePC : p.Settings.MaxAtlasSizeMobile;
            if (p.Settings.GetOverride(platform) is { Enabled: true } ov) maxEdge = ov.MaxAtlasSize;
            bool npot = p.Settings.ExperimentalNpot;

            var candidates = AtlasPool.Build(maxEdge, npot, platform);
            AtoLog.VIf(p.Settings.VerboseLogging,
                $"Atlas pool: {candidates.Count} candidate(s), maxEdge={maxEdge}, NPOT={npot}");

            foreach (var tg in p.TypeGroups)
            {
                p.Progress.ThrowIfCancelled();
                if (tg.Textures.Count == 0) continue;

                // Build one atomic bundle per source texture. / 每张源贴图一个原子包
                var bundles = BuildBundles(tg, p);
                if (bundles.Count == 0) continue;

                PackQueue(p, tg, bundles, candidates, maxEdge);
            }
        }

        private sealed class TextureBundle
        {
            public TextureUsage Usage;
            public readonly List<UvGroup> Groups = new();
            public int GW, GH;       // rasterized footprint in 4px cells (AABB of all its groups)
            public long Area;
            public bool AllowRotation;
        }

        private List<TextureBundle> BuildBundles(TypeGroup tg, AtoPipeline p)
        {
            int padPx = (int)p.Settings.MinPadding;
            var byTex = new Dictionary<Texture2D, TextureBundle>();

            foreach (var g in tg.UvGroups)
            {
                foreach (var isl in g.Islands)
                {
                    if (isl.SourceTexture == null || isl.SourceUsage == null) continue;
                    if (!byTex.TryGetValue(isl.SourceTexture, out var b))
                    {
                        b = new TextureBundle
                        {
                            Usage = isl.SourceUsage,
                            AllowRotation = !tg.HasNormal, // normals: never rotate / 法线绝不旋转
                        };
                        byTex[isl.SourceTexture] = b;
                    }
                    if (!b.Groups.Contains(g)) b.Groups.Add(g);
                }
            }

            foreach (var b in byTex.Values)
            {
                // The bundle footprint is the union AABB (in target pixels) of all its UV groups.
                // We pack groups of the same texture at distinct placements; AABB is an upper bound
                // sufficient for candidate filtering, while actual BLF placement uses each group.
                // 包占用为其所有 UV 组目标像素的并集 AABB（用于候选筛选上界，实际按组 BLF 放置）
                float maxX = 0, maxY = 0;
                foreach (var g in b.Groups)
                    foreach (var isl in g.Islands)
                    {
                        maxX = Mathf.Max(maxX, isl.TargetSizePx.x);
                        maxY = Mathf.Max(maxY, isl.TargetSizePx.y);
                    }
                b.GW = (Mathf.CeilToInt(maxX) + padPx + 3) / 4;
                b.GH = (Mathf.CeilToInt(maxY) + padPx + 3) / 4;
                b.Area = (long)b.GW * b.GH;
            }

            // Area desc, then longest edge desc per spec. / 按面积降序，再按边长降序
            var list = byTex.Values.ToList();
            list.Sort((a, b) =>
            {
                int c = b.Area.CompareTo(a.Area);
                if (c != 0) return c;
                return Mathf.Max(b.GW, b.GH).CompareTo(Mathf.Max(a.GW, a.GH));
            });
            return list;
        }

        /// <summary>Raster footprint for a single UV group for actual BLF placement. / 单个 UV 组的光栅占用，用于实际 BLF 放置。</summary>
        private NativeArray<ulong> RasterizeGroup(UvGroup g, int padPx, out int gw, out int gh)
        {
            float maxX = 0, maxY = 0;
            foreach (var isl in g.Islands)
            {
                maxX = Mathf.Max(maxX, isl.TargetSizePx.x);
                maxY = Mathf.Max(maxY, isl.TargetSizePx.y);
            }
            gw = (Mathf.CeilToInt(maxX) + padPx + 3) / 4;
            gh = (Mathf.CeilToInt(maxY) + padPx + 3) / 4;
            var mask = new NativeArray<ulong>(gw * gh, Allocator.TempJob);
            for (int y = 0; y < gh; y++)
                for (int x = 0; x < gw; x++)
                {
                    int idx = y * gw + (x >> 6);
                    mask[idx] |= 1UL << (x & 63);
                }
            return mask;
        }

        private void PackQueue(AtoPipeline p, TypeGroup tg, List<TextureBundle> bundles,
            List<AtlasPool.Dim> candidates, int maxEdge)
        {
            long totalArea = bundles.Sum(b => b.Area);
            int padCells = Mathf.Max(1, Mathf.CeilToInt(maxEdge / 128f));
            padCells = Mathf.Max(padCells, (int)p.Settings.MinPadding / BitmaskAtlasPacker.Cell);

            // Discard candidates smaller than the queue's total UV area. / 丢弃小于队列总 UV 面积的候选
            var dims = candidates.Where(d => d.Area >= totalArea).ToList();
            // Sort area asc, then ratio asc. / 面积升序、长宽比升序
            dims.Sort((a, b) =>
            {
                int c = a.Area.CompareTo(b.Area);
                if (c != 0) return c;
                return a.Ratio.CompareTo(b.Ratio);
            });
            if (dims.Count == 0) dims = candidates;

            foreach (var dim in dims)
            {
                p.Progress.ThrowIfCancelled();
                using var packer = new BitmaskAtlasPacker(dim.W, dim.H, Allocator.TempJob);
                var placements = new List<PlacedIsland>();
                var placedGroups = new HashSet<UvGroup>();
                bool ok = true;

                foreach (var b in bundles)
                {
                    // Try to place EVERY group of this bundle; if any group fails the bundle cannot be
                    // split, so roll back this bundle's placements for this candidate.
                    // 必须放入该贴图的全部组；任一失败则整包不能拆，回滚本包在此候选中的放置
                    var bundlePlacements = new List<(UvGroup g, RectInt rect, bool rot, NativeArray<ulong> mask)>();
                    bool bundleOk = true;
                    foreach (var g in b.Groups)
                    {
                        var mask = RasterizeGroup(g, (int)p.Settings.MinPadding, out int gw, out int gh);
                        if (packer.TryPlace(mask, gw, gh, padCells, b.AllowRotation, out var rect, out var rot))
                            bundlePlacements.Add((g, rect, rot, mask));
                        else { mask.Dispose(); bundleOk = false; break; }
                    }

                    if (!bundleOk)
                    {
                        foreach (var x in bundlePlacements) x.mask.Dispose();
                        // If this is the first bundle and even the largest atlas can't hold it, drop
                        // atlasing for this bundle (standalone). Else, break and open another queue.
                        // 第一个包且最大图集都装不下 -> 放弃图集化；否则另开队列
                        if (dim.W >= maxEdge && dim.H >= maxEdge && placements.Count == 0)
                        {
                            MarkStandalone(p, b);
                            // Continue trying remaining bundles in this atlas candidate.
                            // 继续尝试本候选中其余包
                            continue;
                        }
                        ok = false; break;
                    }

                    foreach (var x in bundlePlacements)
                    {
                        x.mask.Dispose();
                        if (placedGroups.Add(x.g))
                            foreach (var isl in x.g.Islands)
                                placements.Add(new PlacedIsland
                                {
                                    Island = isl, Group = x.g, PixelRect = x.rect, Rotated = x.rot,
                                });
                    }
                }

                if (ok || placements.Count > 0)
                {
                    var atlas = new AtlasResult
                    {
                        Name = $"ATO_{tg.GetHashCode():X8}_{p.Atlases.Count}",
                        Width = dim.W, Height = dim.H,
                        Placements = placements, Group = tg,
                        Kind = placements.Count > 0 ? placements[0].Island.SourceUsage.Kind : TextureKind.Color,
                        Utilization = packer.Utilization(),
                    };
                    p.Atlases.Add(atlas);
                    AtoLog.VIf(p.Settings.VerboseLogging,
                        $"Atlas {atlas.Name}: {dim.W}x{dim.H} util={packer.Utilization():P1} bundles={bundles.Count} islands={placements.Count}");
                    return;
                }
            }

            // No single candidate fit the whole queue. Split: pack the largest bundle into its own
            // atlas/queue, then recurse with the remainder (reusing a same-type queue).
            // 没有候选能装下整队列：最大包单独开队列，剩余递归
            if (bundles.Count > 1)
            {
                var first = bundles[0];
                var rest = bundles.Skip(1).ToList();
                PackQueue(p, tg, new List<TextureBundle> { first }, candidates, maxEdge);
                PackQueue(p, tg, rest, candidates, maxEdge);
            }
            else
            {
                MarkStandalone(p, bundles[0]);
            }
        }

        private static void MarkStandalone(AtoPipeline p, TextureBundle b)
        {
            string name = b.Usage?.Texture != null ? b.Usage.Texture.name : "?";
            AtoLog.Warn(Localizer.T("warn.tooBigForAtlas", name));
            foreach (var g in b.Groups)
                foreach (var isl in g.Islands)
                    if (isl.SourceUsage != null)
                    {
                        isl.SourceUsage.AtlasAllowed = false;
                        p.Report.SkippedCount++;
                    }
        }
    }
}
