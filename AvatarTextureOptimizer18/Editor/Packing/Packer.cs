using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Fosa.AvatarTextureOptimizer.Editor.Islands;

namespace Fosa.AvatarTextureOptimizer.Editor.Packing
{
    // 装箱器：Burst 光栅化（4px 位掩码）+ 全扫描 BLF + 面积/边长降序 + 90° 步进旋转（位掩码转置）+ 候选图集池。
    // Packer: Burst rasterization (4px bitmasks) + full-scan BLF + area/side descending order + 90°-step rotation (mask transpose) + candidate atlas pool.
    //
    // 规则（依据需求）：
    // - 装箱原子单位 = 贴图连通簇（共享同一张贴图的岛必须同图集），按总面积降序。
    // - 同类型组内各图集统一尺寸 → 同一 UV 组在不同图集上的归一化位置一致（UV 组锚定）。
    // - 装不下当前队列 → 另开队列（已有同类队列则复用）；最大图集也装不下 → 放弃该簇整个 UV 组图集化（fallback + warning）。
    // - padding = max(选项, max(4, ceil(最大边长/128)))，4px 粒度向上取整为单元格。
    internal static class Packer
    {
        public static void Pack(ATOContext ctx, ATOReport.Stage stage)
        {
            int maxSize = ctx.settings.ResolveMaxAtlasSize(ctx.platform);
            int basePad = Mathf.Max(4, Mathf.CeilToInt(maxSize / 128f));
            int padPx = Mathf.Max(basePad, ctx.settings.atlasPaddingPx);
            int padCells = Mathf.Max(1, Mathf.CeilToInt(padPx / 4f));

            var candidates = BuildCandidates(ctx, maxSize);
            int atlasIdGen = 0;
            int packedIslands = 0, fallbackIslands = 0;

            // 光栅化所有岛（Burst 并行）。Rasterize all islands (Burst parallel).
            var raster = RasterizeAll(ctx, padCells);

            try
            {
                foreach (var group in ctx.typeGroups)
                {
                    ctx.CheckCancelled();
                    var entities = new List<IslandEntity>();
                    foreach (var e in group.islands)
                    {
                        if (!e.noAtlasFallback && !e.whitelistedFull) entities.Add(e);
                    }
                    if (entities.Count == 0) continue;

                    // 装箱原子单位 = 贴图连通簇：共享同一张贴图的岛必须在同一图集（同队列）内。
                    // Atomic packing unit = texture-connected clusters: islands sharing a texture must land in the same atlas (same queue).
                    var clusters = BuildClusters(entities);
                    clusters.Sort((a, b) => ClusterArea(b).CompareTo(ClusterArea(a)));

                    // 队列 = 图集组（每类别一张同尺寸图集）。A queue = an atlas set (one same-size atlas per kind).
                    var atlasSets = new List<Dictionary<AtlasKind, AtlasPlan>>();

                    foreach (var cluster in clusters)
                    {
                        ctx.CheckCancelled();
                        bool placed = false;

                        // 1) 复用已有同类队列。Reuse existing same-kind queues.
                        foreach (var set in atlasSets)
                        {
                            if (TryPlaceCluster(cluster, set, group, raster, padPx))
                            {
                                placed = true;
                                break;
                            }
                        }
                        if (placed) { packedIslands += cluster.Count; continue; }

                        // 2) 新队列：候选池从小到大，取第一个能装下整个簇的尺寸。
                        // New queue: smallest candidate that fits the whole cluster.
                        foreach (var cand in candidates)
                        {
                            if (!ClusterFitsCandidate(cluster, cand)) continue;
                            var set = CreateAtlasSet(group, cand.x, cand.y, padPx, ref atlasIdGen);
                            if (TryPlaceCluster(cluster, set, group, raster, padPx))
                            {
                                atlasSets.Add(set);
                                placed = true;
                                break;
                            }
                        }
                        if (placed) { packedIslands += cluster.Count; continue; }

                        // 3) 最大图集也装不下 → 放弃该簇全部岛（整个 UV 组）的图集化。
                        // Even the largest atlas can't fit the cluster → give up atlasing its whole UV group.
                        foreach (var e in cluster)
                        {
                            e.noAtlasFallback = true;
                            e.fallbackReason = "warn.pack.fail";
                            e.typeGroupId = -1;
                            fallbackIslands++;
                            ATOLog.Warn(string.Format(ATOLocalization.Tr("warn.pack.fail"), e.ToString(), maxSize));
                        }
                    }

                    // 利用率。Utilization.
                    foreach (var set in atlasSets)
                    {
                        foreach (var plan in set.Values)
                        {
                            long used = 0;
                            foreach (var e in plan.islands)
                            {
                                used += (long)RectWidth(e) * RectHeight(e);
                            }
                            plan.utilization = (float)used / ((long)plan.width * plan.height);
                            group.atlases.GetOrAdd(plan.kind, new List<AtlasPlan>()).Add(plan);
                            ctx.atlasPlans.Add(plan);
                        }
                    }
                }

                stage.AddLine(string.Format(ATOLocalization.Tr("log.packSummary"), packedIslands, fallbackIslands, ctx.atlasPlans.Count, padPx));
            }
            finally
            {
                raster.Dispose();
            }
        }

        // 岛矩形尺寸（含 padding，像素）。Island rect size (incl. padding, in pixels).
        private static int RectWidth(IslandEntity e) { return e.rectSizePx.x; }

        private static int RectHeight(IslandEntity e) { return e.rectSizePx.y; }

        private static long RectArea(IslandEntity e)
        {
            return (long)RectWidth(e) * RectHeight(e);
        }

        // 贴图连通簇：共享贴图的岛并查集合并（同一张贴图的不同岛必须在同一图集）。
        // Texture-connected clusters: union-find islands that share a texture (all islands of one texture must share an atlas).
        private static List<List<IslandEntity>> BuildClusters(List<IslandEntity> entities)
        {
            int n = entities.Count;
            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;
            var firstSeen = new Dictionary<Analysis.TextureEntry, int>();
            for (int i = 0; i < n; i++)
            {
                foreach (var u in entities[i].uses)
                {
                    if (u.texture == null || u.whitelistLevel == Analysis.ATOWhitelistLevel.Full) continue;
                    int first;
                    if (firstSeen.TryGetValue(u.texture, out first))
                    {
                        int ra = FindRoot(parent, first), rb = FindRoot(parent, i);
                        if (ra != rb) parent[rb] = ra;
                    }
                    else
                    {
                        firstSeen[u.texture] = i;
                    }
                }
            }

            var clusters = new Dictionary<int, List<IslandEntity>>();
            for (int i = 0; i < n; i++)
            {
                int root = FindRoot(parent, i);
                List<IslandEntity> list;
                if (!clusters.TryGetValue(root, out list))
                {
                    list = new List<IslandEntity>();
                    clusters[root] = list;
                }
                list.Add(entities[i]);
            }
            return new List<List<IslandEntity>>(clusters.Values);
        }

        private static int FindRoot(int[] parent, int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }
            return i;
        }

        private static long ClusterArea(List<IslandEntity> cluster)
        {
            long area = 0;
            foreach (var e in cluster) area += RectArea(e);
            return area;
        }

        private static bool ClusterFitsCandidate(List<IslandEntity> cluster, Vector2Int cand)
        {
            long total = 0;
            foreach (var e in cluster)
            {
                int w = RectWidth(e), h = RectHeight(e);
                // 任意旋转后都必须放得下。Must fit after any rotation.
                if ((w > cand.x || h > cand.y) && (w > cand.y || h > cand.x)) return false;
                total += (long)w * h;
            }
            return total <= (long)cand.x * cand.y;
        }

        // 尝试把整个簇放进图集组（在占用掩码副本上试放，全部成功才提交）。
        // Tries placing the whole cluster (trial on occupancy copies; commits only if all fit).
        private static bool TryPlaceCluster(List<IslandEntity> cluster, Dictionary<AtlasKind, AtlasPlan> set,
            TypeGroup group, RasterData raster, int padPx)
        {
            var copies = new Dictionary<AtlasKind, BitMask>();
            foreach (var kv in set)
            {
                var c = kv.Value.occupancy;
                c.rows = (ulong[])kv.Value.occupancy.rows.Clone();
                copies[kv.Key] = c;
            }
            var placements = new List<KeyValuePair<IslandEntity, PlacementInfo>>();

            foreach (var e in cluster)
            {
                var rotations = raster.rotations[e];
                bool placed = false;
                for (int r = 0; r < 4 && !placed; r++)
                {
                    var mask = rotations[r];
                    var anchor = copies[group.kinds[0]];
                    if (mask.w > anchor.w || mask.h > anchor.h) continue;
                    for (int y = 0; y <= anchor.h - mask.h && !placed; y++)
                    {
                        for (int x = 0; x <= anchor.w - mask.w && !placed; x++)
                        {
                            if (!mask.CanPlace(in anchor, x, y)) continue;
                            if (!MirrorFitsIn(copies, group, mask, x, y)) continue;
                            foreach (var kind in group.kinds)
                            {
                                var c = copies[kind];
                                mask.Place(ref c, x, y);
                                copies[kind] = c;
                            }
                            placements.Add(new KeyValuePair<IslandEntity, PlacementInfo>(e, new PlacementInfo
                            {
                                mask = mask,
                                x = x,
                                y = y,
                                rotation = r
                            }));
                            placed = true;
                        }
                    }
                }
                if (!placed) return false;
            }

            // 全部成功 → 提交。All fit → commit.
            foreach (var kv in set)
            {
                kv.Value.occupancy = copies[kv.Key];
            }
            foreach (var p in placements)
            {
                var e = p.Key;
                var pi = p.Value;
                var anchor = set[group.kinds[0]];
                e.atlasId = anchor.id;
                e.atlasKind = anchor.kind.ToString();
                e.rotation = pi.rotation;
                e.rectPosPx = new Vector2Int(pi.x * 4, pi.y * 4);
                e.rectSizePx = new Vector2Int(pi.mask.w * 4, pi.mask.h * 4);
                e.paddingPx = padPx;
                foreach (var kind in group.kinds)
                {
                    if (!set[kind].islands.Contains(e)) set[kind].islands.Add(e);
                }
            }
            return true;
        }

        private struct PlacementInfo
        {
            public BitMask mask;
            public int x, y, rotation;
        }

        private static bool MirrorFitsIn(Dictionary<AtlasKind, BitMask> copies, TypeGroup group, BitMask mask, int x, int y)
        {
            foreach (var kind in group.kinds)
            {
                BitMask c;
                if (!copies.TryGetValue(kind, out c)) return false;
                if (!mask.CanPlace(in c, x, y)) return false;
            }
            return true;
        }

        // 光栅化全部岛并预计算 4 个旋转掩码（padding 已膨胀进掩码）。Rasterizes all islands; padding dilated into the masks.
        private sealed class RasterData : System.IDisposable
        {
            public NativeArray<float2> uvPool;
            public NativeArray<RasterInput> inputs;
            public NativeArray<ulong> masks;
            public NativeArray<int> strides;
            public readonly Dictionary<IslandEntity, BitMask[]> rotations = new Dictionary<IslandEntity, BitMask[]>();
            public int padCells;

            public void Dispose()
            {
                if (uvPool.IsCreated) uvPool.Dispose();
                if (inputs.IsCreated) inputs.Dispose();
                if (masks.IsCreated) masks.Dispose();
                if (strides.IsCreated) strides.Dispose();
            }
        }

        private static RasterData RasterizeAll(ATOContext ctx, int padCells)
        {
            var data = new RasterData { padCells = padCells };
            var entities = new List<IslandEntity>();
            foreach (var g in ctx.typeGroups)
            {
                foreach (var e in g.islands)
                {
                    if (!e.noAtlasFallback && !e.whitelistedFull) entities.Add(e);
                }
            }

            int uvCount = 0, maskTotal = 0;
            foreach (var e in entities) uvCount += e.triangles.Count;
            data.uvPool = new NativeArray<float2>(uvCount, Allocator.TempJob);
            data.inputs = new NativeArray<RasterInput>(entities.Count, Allocator.TempJob);
            data.strides = new NativeArray<int>(entities.Count, Allocator.TempJob);

            int uvCursor = 0;
            var uvChannelCache = new Dictionary<Mesh, List<Vector2>>();
            for (int i = 0; i < entities.Count; i++)
            {
                var e = entities[i];
                List<Vector2> uvs;
                if (!uvChannelCache.TryGetValue(e.mesh, out uvs))
                {
                    uvs = new List<Vector2>();
                    e.mesh.GetUVs(e.uvChannel, uvs);
                    uvChannelCache[e.mesh] = uvs;
                }
                var tex = FirstTexture(e);
                int tw = tex != null ? tex.width : 1024;
                int th = tex != null ? tex.height : 1024;
                float spanU = Mathf.Max(e.uvMax.x - e.uvMin.x, 1e-6f);
                float spanV = Mathf.Max(e.uvMax.y - e.uvMin.y, 1e-6f);
                int pw = Mathf.Max(1, Mathf.CeilToInt(spanU * e.scaleX * tw));
                int ph = Mathf.Max(1, Mathf.CeilToInt(spanV * e.scaleY * th));
                // 含 padding 的放置矩形。Placed rect incl. padding.
                e.rectSizePx = new Vector2Int(pw + padCells * 8, ph + padCells * 8);

                int maskW = Mathf.Max(1, Mathf.CeilToInt(pw / 4f));
                int maskH = Mathf.Max(1, Mathf.CeilToInt(ph / 4f));
                int stride = (maskW + 63) >> 6;
                data.strides[i] = stride;

                var input = new RasterInput
                {
                    uvStart = uvCursor,
                    triangleCount = e.triangles.Count / 3,
                    scaleX = e.scaleX,
                    scaleY = e.scaleY,
                    texW = tw,
                    texH = th,
                    maskW = maskW,
                    maskH = maskH,
                    maskOffset = maskTotal
                };
                for (int t = 0; t < e.triangles.Count; t++)
                {
                    int vi = e.triangles[t];
                    var uv = vi < uvs.Count ? uvs[vi] : Vector2.zero;
                    data.uvPool[uvCursor++] = new float2(uv.x - e.uvMin.x, uv.y - e.uvMin.y);
                }
                data.inputs[i] = input;
                maskTotal += stride * maskH;
            }

            data.masks = new NativeArray<ulong>(maskTotal, Allocator.TempJob);
            if (entities.Count > 0)
            {
                var job = new RasterizeJob
                {
                    uvPool = data.uvPool,
                    inputs = data.inputs,
                    masks = data.masks,
                    rowStrides = data.strides
                };
                job.Schedule(entities.Count, 4).Complete();
            }

            for (int i = 0; i < entities.Count; i++)
            {
                var e = entities[i];
                var input = data.inputs[i];
                int stride = data.strides[i];
                var mask = BitMask.Allocate(input.maskW, input.maskH);
                mask.stride = stride;
                for (int y = 0; y < input.maskH; y++)
                {
                    for (int wI = 0; wI < stride; wI++)
                    {
                        mask.rows[y * stride + wI] = data.masks[input.maskOffset + y * stride + wI];
                    }
                }
                var dilated = mask.Dilate(data.padCells);
                var rotations = new BitMask[4];
                rotations[0] = dilated;
                for (int r = 1; r < 4; r++) rotations[r] = rotations[r - 1].Rotate90();
                data.rotations[e] = rotations;
            }
            return data;
        }

        private static Analysis.TextureEntry FirstTexture(IslandEntity e)
        {
            foreach (var u in e.uses)
            {
                if (u.texture != null) return u.texture;
            }
            return null;
        }

        // 创建图集组（每类别一张同尺寸图集）。Creates an atlas set (one same-size atlas per kind).
        private static Dictionary<AtlasKind, AtlasPlan> CreateAtlasSet(TypeGroup group, int w, int h, int padPx, ref int idGen)
        {
            var set = new Dictionary<AtlasKind, AtlasPlan>();
            foreach (var kind in group.kinds)
            {
                var plan = new AtlasPlan
                {
                    id = idGen++,
                    kind = kind,
                    width = w,
                    height = h,
                    group = group,
                    paddingPx = padPx
                };
                plan.occupancy = BitMask.Allocate(Mathf.Max(1, w / 4), Mathf.Max(1, h / 4));
                set[kind] = plan;
            }
            return set;
        }

        // 候选图集池：POT（2 的幂，64 起）或 NPOT（64 步进）；按面积升序、长边/短边升序（最接近正方形优先）。
        // Candidate pool: POT (powers of two from 64) or NPOT (64-step); sorted by area then aspect ascending (square-ish first).
        private static List<Vector2Int> BuildCandidates(ATOContext ctx, int maxSize)
        {
            var list = new List<Vector2Int>();
            bool npot = ctx.settings.ResolveNpotAtlases(ctx.platform);
            int v = ATOConstants.MinAtlasSize;
            while (v <= maxSize)
            {
                list.Add(new Vector2Int(v, v));
                v = npot ? v + ATOConstants.NpotSideStep : v * 2;
            }
            list.Sort((a, b) =>
            {
                long aa = (long)a.x * a.y, ab = (long)b.x * b.y;
                if (aa != ab) return aa.CompareTo(ab);
                float ar = (float)Mathf.Max(a.x, a.y) / Mathf.Max(1, Mathf.Min(a.x, a.y));
                float br = (float)Mathf.Max(b.x, b.y) / Mathf.Max(1, Mathf.Min(b.x, b.y));
                return ar.CompareTo(br);
            });
            return list;
        }
    }

    // Dictionary 扩展：GetOrAdd。Dictionary extension: GetOrAdd.
    internal static class DictExtensions
    {
        public static TValue GetOrAdd<TKey, TValue>(this Dictionary<TKey, TValue> dict, TKey key, TValue defaultValue)
        {
            TValue v;
            if (dict.TryGetValue(key, out v)) return v;
            dict[key] = defaultValue;
            return defaultValue;
        }
    }
}
