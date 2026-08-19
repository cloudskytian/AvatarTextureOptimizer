// AtlasPacker.cs
// Packs UV-group components into atlas families: candidate pool (POT / experimental NPOT),
// area-ascending + most-square-first candidate order, BLF full-scan with 4px bitmask,
// 90° rotation (mask transpose), texture-atomic placement, open-queue reuse.
// 将 UV 组组件装箱:候选池(POT/实验性NPOT)、面积升序+最接近正方形优先、4px位掩码
// BLF 全扫描、90°旋转(掩码转置)、贴图原子性、开放队列复用。
// Copyright (c) 2026 fosa. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace net.fosa.ato
{
    internal sealed partial class ATOProcessor
    {
        private sealed class AtlasFamily
        {
            internal int W, H;                     // atlas pixels / 图集像素
            internal int CellW, CellH;             // 4px cells / 格数
            internal int Stride;                   // words per row / 行字数
            internal ulong[] Occupancy;            // dilated occupancy / 已膨胀占位
            internal List<PlacedIsland> Placed = new List<PlacedIsland>();
            internal List<UvGroup> Components = new List<UvGroup>();
            internal UvGroupSignature Signature;
        }

        private const int CellPx = 4;

        private void PackAtlases()
        {
            int pad = EffectivePadding();
            int padCells = Mathf.CeilToInt(pad / 2f / CellPx); // each island claims pad/2 → gap ≥ pad / 每岛占 pad/2 → 间距≥pad
            ATOLog.V($"packing: effective padding {pad}px (padCells={padCells})");

            var bySignature = _d.UvGroups.Where(g => g.Textures.Count > 0)
                .GroupBy(g => g.Signature).ToList();

            int totalIslands = _d.UvGroups.Sum(g => g.Islands.Count);
            int packed = 0, done = 0;

            foreach (var sigGroup in bySignature)
            {
                // Whitelist-contaminated components fall back to whole-texture scaling.
                // 白名单污染组件回退整图缩放。
                foreach (var comp in sigGroup.Where(g => g.FallbackWhitelist))
                {
                    comp.PackFailed = true;
                    ATOLog.V($"component fallback (whitelist contamination): " +
                             $"{string.Join(",", comp.Textures.Select(t => t.Tex.name).Take(4))}");
                }

                // Desc by rasterized area / 按光栅化面积降序
                var queue = sigGroup.Where(g => !g.FallbackWhitelist)
                    .OrderByDescending(AreaOfComponent).ToList();
                var openFamilies = new List<AtlasFamily>();

                foreach (var comp in queue)
                {
                    Tick($"ATO: packing ({done}/{totalIslands})", 0.5f + 0.2f * done / Mathf.Max(1, totalIslands));
                    done += comp.Islands.Count;

                    bool placed = false;
                    foreach (var fam in openFamilies)
                        if (TryPlaceComponent(fam, comp, padCells))
                        {
                            placed = true;
                            break;
                        }

                    if (!placed)
                    {
                        // open a new family / 新开族
                        float pendingArea = PendingArea(openFamilies, queue, comp);
                        var fam = CreateFamily(sigGroup.Key, pendingArea, comp, padCells);
                        if (fam == null)
                        {
                            comp.PackFailed = true;
                            ATOLog.Warn($"component with {comp.Islands.Count} islands (textures: " +
                                        $"{string.Join(",", comp.Textures.Select(t => t.Tex.name).Take(4))}) " +
                                        $"exceeds max atlas size; falling back to standalone scaling");
                            _d.ReportDetails.Add($"pack fallback: {string.Join(",", comp.Textures.Select(t => t.Tex.name).Take(4))}");
                            continue;
                        }
                        openFamilies.Add(fam);
                    }
                    packed += comp.Islands.Count;
                }

                // Emit plans / 输出计划
                foreach (var fam in openFamilies) EmitPlans(fam, pad);
            }

            ATOLog.Info($"packing: {packed}/{totalIslands} islands placed into {_d.AtlasPlans.Count} atlas layers");

            _placementIndex.Clear();
            foreach (var plan in _d.AtlasPlans)
                foreach (var pi in plan.Placed)
                    _placementIndex.Add(ATOBuildData.Key(pi.SetId, pi.IslandId));
            MarkAtlasedNodes();
        }

        private int EffectivePadding()
        {
            var p = _d.EffectiveProfile;
            int user = (int)p.padding;
            int maxEdge = AvatarTextureOptimizer.MaxAtlasEdge(_d.Platform);
            int pad = Mathf.Max(user, Mathf.CeilToInt(maxEdge / 128f));
            return Mathf.Max(4, pad);
        }

        private float AreaOfComponent(UvGroup g)
        {
            double area = 0;
            foreach (var iref in g.Islands)
            {
                var dec = FindDecision(g, iref);
                if (dec == null) continue;
                var island = _d.IslandSets[iref.SetId].Islands[iref.IslandId];
                var tex = LargestTextureOf(iref);
                if (tex == null) continue;
                int bw = Mathf.Max(1, Mathf.CeilToInt(island.UvBounds.width * tex.width * dec.Sx));
                int bh = Mathf.Max(1, Mathf.CeilToInt(island.UvBounds.height * tex.height * dec.Sy));
                var m = RasterizeScaled(iref, dec.Sx, dec.Sy);
                area += m.SetCount() * CellPx * CellPx;
            }
            return (float)area;
        }

        private IslandScaleDecision FindDecision(UvGroup g, IslandRef iref)
        {
            if (g.ScaleDecisions == null) return null;
            foreach (var d in g.ScaleDecisions)
                if (d.SetId == iref.SetId && d.IslandId == iref.IslandId) return d;
            return null;
        }

        private Texture2D LargestTextureOf(IslandRef iref)
        {
            Texture2D best = null;
            List<TextureNode> list;
            if (!_d.IslandTextures.TryGetValue(iref.Key, out list)) return null;
            foreach (var n in list)
                if (best == null || n.Tex.width * n.Tex.height > best.width * best.height) best = n.Tex;
            return best;
        }

        private float PendingArea(List<AtlasFamily> open, List<UvGroup> queue, UvGroup current)
        {
            // total raster area not yet placed (this type group) / 本类型组尚未放置的总面积
            double area = 0;
            var placedSet = new HashSet<UvGroup>();
            foreach (var f in open) foreach (var c in f.Components) placedSet.Add(c);
            foreach (var q in queue)
                if (!placedSet.Contains(q)) area += AreaOfComponent(q);
            return (float)area;
        }

        /// <summary>Try every candidate atlas from smallest area/most square; first that fits wins. / 依次尝试候选;第一个能装下的即成品。</summary>
        private AtlasFamily CreateFamily(UvGroupSignature sig, float pendingArea, UvGroup first, int padCells)
        {
            var candidates = BuildCandidatePool(pendingArea);
            foreach (var cand in candidates)
            {
                var fam = new AtlasFamily
                {
                    W = cand.W, H = cand.H,
                    CellW = cand.W / CellPx, CellH = cand.H / CellPx,
                    Stride = (cand.W / CellPx + 63) / 64,
                    Signature = sig,
                };
                fam.Occupancy = new ulong[fam.CellH * fam.Stride];
                if (TryPlaceComponent(fam, first, padCells))
                {
                    ATOLog.V($"new atlas family {cand.w}x{cand.h} for signature {sig}");
                    return fam;
                }
            }
            return null; // even max atlas can't fit / 最大图集也装不下
        }

        private struct AtlasCandidate
        {
            internal int W, H;
            internal AtlasCandidate(int w, int h) { W = w; H = h; }
        }

        private List<AtlasCandidate> BuildCandidatePool(float neededAreaPx)
        {
            var result = new List<AtlasCandidate>();
            int maxEdge = AvatarTextureOptimizer.MaxAtlasEdge(_d.Platform);
            bool npot = _d.EffectiveProfile.experimentalNpotAtlas;
            var edges = new List<int>();
            if (npot)
            {
                for (int e = 64; e <= maxEdge; e += 64) edges.Add(e);
            }
            else
            {
                for (int e = 64; e <= maxEdge; e *= 2) edges.Add(e);
            }

            foreach (var w in edges)
            foreach (var h in edges)
                if ((long)w * h >= neededAreaPx) result.Add(new AtlasCandidate(w, h));

            // area asc, then long/short asc / 面积升序,长宽比升序
            result.Sort((a, b) =>
            {
                long areaA = (long)a.W * a.H, areaB = (long)b.W * b.H;
                int c = areaA.CompareTo(areaB);
                if (c != 0) return c;
                float ra = Mathf.Max(a.W, a.H) / (float)Mathf.Min(a.W, a.H);
                float rb = Mathf.Max(b.W, b.H) / (float)Mathf.Min(b.W, b.H);
                return ra.CompareTo(rb);
            });

            if (result.Count > 16) result.RemoveRange(16, result.Count - 16);
            if (result.Count == 0)
                result.Add(new AtlasCandidate(maxEdge, maxEdge)); // oversized need: try max anyway / 超大需求:仍尝试最大
            return result;
        }

        /// <summary>All-or-nothing placement of one component. / 组件整体放置(全有或全无)。</summary>
        private bool TryPlaceComponent(AtlasFamily fam, UvGroup comp, int padCells)
        {
            var newPlacements = new List<PlacedIsland>();

            foreach (var iref in comp.Islands)
            {
                var dec = FindDecision(comp, iref);
                if (dec == null) continue;
                var island = _d.IslandSets[iref.SetId].Islands[iref.IslandId];
                var tex = LargestTextureOf(iref);
                if (tex == null) continue;

                var raw = RasterizeScaled(iref, dec.Sx, dec.Sy);
                var dilated = IslandRasterizer.Dilate(raw, padCells);
                var placement = FindPlacement(fam, raw, dilated);
                if (placement.Found)
                {
                    // effective occupancy mask (rotated → transposed) / 有效占位掩码(旋转→转置)
                    var occMask = placement.Rotated ? IslandRasterizer.Transpose(dilated) : dilated;
                    int c = dilated.ContentOffset;
                    // content cells inside the placed dilated mask / 放置后掩码内的内容格
                    int contentW = placement.Rotated ? raw.H : raw.W;
                    int contentH = placement.Rotated ? raw.W : raw.H;
                    int cellX = placement.X + c, cellY = placement.Y + c;

                    var pi = new PlacedIsland
                    {
                        SetId = iref.SetId, IslandId = iref.IslandId,
                        Source = tex,
                        Rect = new RectInt(cellX * CellPx, cellY * CellPx, contentW * CellPx, contentH * CellPx),
                        Sx = dec.Sx, Sy = dec.Sy,
                        Rotated = placement.Rotated,
                        SourceUvBounds = island.UvBounds,
                        RectN = new Rect(
                            cellX * CellPx / (float)fam.W,
                            cellY * CellPx / (float)fam.H,
                            contentW * CellPx / (float)fam.W,
                            contentH * CellPx / (float)fam.H),
                    };
                    WriteOccupancy(fam, occMask, placement.X, placement.Y);
                    newPlacements.Add(pi);
                }
                else
                {
                    // rollback / 回滚
                    foreach (var pi in newPlacements)
                    {
                        var d = FindAnyDecision(pi.SetId, pi.IslandId);
                        if (d == null) continue;
                        var raw2 = RasterizeScaled(new IslandRef(pi.SetId, pi.IslandId), d.Sx, d.Sy);
                        var dil2 = IslandRasterizer.Dilate(raw2, padCells);
                        var occ2 = pi.Rotated ? IslandRasterizer.Transpose(dil2) : dil2;
                        int cx2 = (pi.Rect.x / CellPx) - dil2.ContentOffset;
                        int cy2 = (pi.Rect.y / CellPx) - dil2.ContentOffset;
                        RemoveOccupancy(fam, occ2, cx2, cy2);
                    }
                    return false;
                }
            }

            if (newPlacements.Count == 0) return false;
            fam.Placed.AddRange(newPlacements);
            fam.Components.Add(comp);
            comp.Packed = true;
            return true;
        }

        private IslandScaleDecision FindAnyDecision(int setId, int islandId)
        {
            foreach (var g in _d.UvGroups)
                foreach (var d in g.ScaleDecisions)
                    if (d.SetId == setId && d.IslandId == islandId) return d;
            return null;
        }

        private struct PlaceResult
        {
            internal bool Found; internal int X, Y; internal bool Rotated;
        }

        private PlaceResult FindPlacement(AtlasFamily fam, IslandRasterMask raw, IslandRasterMask dilated)
        {
            var r0 = BlfScan(fam, dilated);
            if (r0.Found) return new PlaceResult { Found = true, X = r0.X, Y = r0.Y };
            var dt = IslandRasterizer.Transpose(dilated);
            var r1 = BlfScan(fam, dt);
            if (r1.Found) return new PlaceResult { Found = true, X = r1.X, Y = r1.Y, Rotated = true };
            return new PlaceResult();
        }

        private BlfResult BlfScan(AtlasFamily fam, IslandRasterMask dilated)
        {
            if (dilated.W > fam.CellW || dilated.H > fam.CellH) return new BlfResult();
            int stride = (dilated.W + 63) / 64;
            var occ = new NativeArray<ulong>(fam.Occupancy, Allocator.TempJob);
            var mask = new NativeArray<ulong>(dilated.Words, Allocator.TempJob);
            var result = new NativeArray<int>(3, Allocator.TempJob);
            try
            {
                var job = new BlfScanJob
                {
                    Occupancy = occ, OccStride = fam.Stride, OccW = fam.CellW, OccH = fam.CellH,
                    Mask = mask, MaskStride = stride, MaskW = dilated.W, MaskH = dilated.H,
                    PadCells = 0, Result = result,
                };
                job.Schedule().Complete();
                return new BlfResult { Found = result[0] == 1, X = result[1], Y = result[2] };
            }
            finally
            {
                occ.Dispose(); mask.Dispose(); result.Dispose();
            }
        }

        private void WriteOccupancy(AtlasFamily fam, IslandRasterMask dilated, int cx, int cy)
        {
            SetOccupancy(fam, dilated, cx, cy, true);
        }

        private struct BlfResult
        {
            internal bool Found; internal int X, Y;
        }

        private void RemoveOccupancy(AtlasFamily fam, int cx, int cy, IslandRasterMask dilated)
        {
            SetOccupancy(fam, dilated, cx, cy, false);
        }

        private void SetOccupancy(AtlasFamily fam, IslandRasterMask dilated, int cx, int cy, bool set)
        {
            int wordIdx = cx >> 6, bitOff = cx & 63;
            int mstride = (dilated.W + 63) / 64;
            for (int r = 0; r < dilated.H; r++)
            {
                for (int w = 0; w < mstride; w++)
                {
                    ulong m = dilated.Words[r * mstride + w];
                    if (m == 0) continue;
                    int idx = (cy + r) * fam.Stride + wordIdx + w;
                    if (bitOff == 0)
                    {
                        if (set) fam.Occupancy[idx] |= m; else fam.Occupancy[idx] &= ~m;
                    }
                    else
                    {
                        if (set) fam.Occupancy[idx] |= m << bitOff;
                        else fam.Occupancy[idx] &= ~(m << bitOff);
                        if (idx + 1 < fam.Occupancy.Length)
                        {
                            if (set) fam.Occupancy[idx + 1] |= m >> (64 - bitOff);
                            else fam.Occupancy[idx + 1] &= ~(m >> (64 - bitOff));
                        }
                    }
                }
            }
        }

        // ------------------------------------------------------------------ //
        // Scaled-mask cache / 缩放掩码缓存
        // ------------------------------------------------------------------ //
        private readonly Dictionary<long, IslandRasterMask> _scaledMaskCache = new Dictionary<long, IslandRasterMask>();

        private IslandRasterMask RasterizeScaled(IslandRef iref, float sx, float sy)
        {
            // cache key includes scale quantization / 缓存键含量化缩放
            long sKey = (long)(Mathf.RoundToInt(sx * 512f) * 1024 + Mathf.RoundToInt(sy * 512f));
            long key = iref.Key * 1024 + (sKey & 0x3FFFFF);
            IslandRasterMask m;
            if (_scaledMaskCache.TryGetValue(key, out m)) return m;

            var set = _d.IslandSets[iref.SetId];
            var island = set.Islands[iref.IslandId];
            var tex = LargestTextureOf(iref);
            int bw = Mathf.Max(1, Mathf.CeilToInt(island.UvBounds.width * tex.width * sx));
            int bh = Mathf.Max(1, Mathf.CeilToInt(island.UvBounds.height * tex.height * sy));
            // virtual texture dims: uvBounds maps onto exactly (bw,bh) px / 虚拟贴图尺寸:uvBounds 恰好映射为 (bw,bh)
            int vw = Mathf.CeilToInt(bw / Mathf.Max(1e-9f, island.UvBounds.width));
            int vh = Mathf.CeilToInt(bh / Mathf.Max(1e-9f, island.UvBounds.height));
            m = IslandRasterizer.RasterizePixels(set.NormalizedUvs, island.Triangles, island.UvBounds, vw, vh, CellPx);
            _scaledMaskCache[key] = m;
            return m;
        }

        // ------------------------------------------------------------------ //
        // Plan emission / 计划输出
        // ------------------------------------------------------------------ //
        private void EmitPlans(AtlasFamily fam, int pad)
        {
            // layers: distinct (role, colorLayer) → one atlas per layer / 层:(角色,颜色层)→每层一张
            var layerGroups = new Dictionary<string, List<PlacedIsland>>();
            var layerMeta = new Dictionary<string, (TexRole role, int colorLayer, bool srgb, FilterMode filter)>();

            foreach (var pi in fam.Placed)
            {
                List<TextureNode> textures;
                if (!_d.IslandTextures.TryGetValue(ATOBuildData.Key(pi.SetId, pi.IslandId), out textures)) continue;
                foreach (var node in textures)
                {
                    string layerKey = node.PrimaryRole + "/" + node.ColorLayer + "/" + node.Srgb + "/" + node.Filter;
                    if (!layerGroups.ContainsKey(layerKey))
                    {
                        layerGroups[layerKey] = new List<PlacedIsland>();
                        layerMeta[layerKey] = (node.PrimaryRole, node.ColorLayer, node.Srgb, node.Filter);
                    }
                    layerGroups[layerKey].Add(new PlacedIsland
                    {
                        SetId = pi.SetId, IslandId = pi.IslandId,
                        Source = node.Tex,
                        Rect = pi.Rect, Sx = pi.Sx, Sy = pi.Sy, Rotated = pi.Rotated,
                        SourceUvBounds = pi.SourceUvBounds,
                    });
                }
            }

            int famId = _d.AtlasPlans.Count;
            foreach (var kv in layerGroups)
            {
                var meta = layerMeta[kv.Key];
                var plan = new AtlasPlan
                {
                    Name = "ATO_" + _d.Ctx.AvatarRootObject.name + "_" + famId + "_" + meta.role + (meta.colorLayer > 0 ? "_" + meta.colorLayer : ""),
                    Width = fam.W, Height = fam.H,
                    Role = meta.role, Srgb = meta.srgb, Filter = meta.filter,
                    LayerIndex = meta.colorLayer, FamilyId = famId,
                    Placed = kv.Value,
                };
                ComputeUtilization(plan);
                _d.AtlasPlans.Add(plan);
            }
        }

        private void ComputeUtilization(AtlasPlan plan)
        {
            long covered = 0;
            foreach (var pi in plan.Placed)
            {
                var m = RasterizeScaled(new IslandRef(pi.SetId, pi.IslandId), pi.Sx, pi.Sy);
                covered += m.SetCount() * CellPx * CellPx;
            }
            plan.Utilization = (float)((double)covered / ((double)plan.Width * plan.Height));
        }
    }
}
