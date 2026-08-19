// AvatarTextureOptimizer
// File: Editor/Atlas/Packer.cs
//
// Packing, two-stage:
//   1) GLOBAL LAYOUT — all optimizable UV groups are packed together into
//      atlas(es) using the candidate-pool algorithm. This yields one canonical
//      atlas rect per island. Per spec, a texture queue is formed by total
//      rasterized area (desc), each atomic operation = one texture + its UV
//      group, all islands of one texture stay in the same atlas, and the first
//      candidate that fits the whole queue becomes the atlas.
//   2) TYPE-GROUP ATLASES — for every type group, atlas(es) are created with
//      the SAME dimensions and the member UV groups placed at their CANONICAL
//      rects, so the same UV lands at the same position in every atlas that
//      serves it (mandatory for UV shared by normal/non-normal materials).
//
// 两阶段装箱：
//   1) 全局布局——所有可优化 UV 组使用候选池算法一起装箱进图集。为每个岛
//      产生一个规范图集矩形。按规格：贴图队列按光栅化总面积（降序）形成，
//      每次原子操作 = 单张贴图 + 其 UV 组，同一张贴图的所有岛在同一张图集，
//      第一个能装下整个队列的候选图集即成品。
//   2) 类型组图集——为每个类型组创建图集，尺寸与全局布局相同，成员 UV 组
//      放置在它们的【规范矩形】上，使同一 UV 在所有服务它的图集中位于同一
//      位置（UV 被有法线/无法线材质同时引用时必须满足）。
//
// Placement: full-scan bottom-left-first in a Burst job; 90-degree rotation
// steps (bitmask transpose) except for normal maps (tangent data kept as-is,
// never recomputed). Padding = max(minPadding, ceil(maxSide/128)).
//
// 放置：Burst 任务全扫描左下优先；90 度旋转步进（位掩码转置），法线贴图
// 除外（切线数据保持原样、绝不重算）。padding = max(最小padding,
// ceil(最大边长/128))。

using System.Collections.Generic;
using System.Linq;
using net.fosa.avatar_texture_optimizer.editor.logging;
using net.fosa.avatar_texture_optimizer.editor.model;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor.atlas
{
    public static class Packer
    {
        public static void Pack(ATOBuildState state)
        {
            var component = state.Component;
            if (component == null || !component.GenerateAtlas) return;

            var atlasCfg = component.Atlas;
            var stopwatch = new ATOStopwatch("Packer.Pack");

            // Rasterize island shapes once per UV group (cached for reuse).
            // 为每个 UV 组一次性光栅化岛形状（缓存复用）。
            foreach (var group in state.UVGroups)
            {
                if (group.Whitelisted || group.SkippedAtlas) continue;
                stopwatch.Begin($"rasterize {group.Space}");
                RasterizeGroup(group, atlasCfg.RasterGranularity);
                stopwatch.End($"rasterize {group.Space}");
            }

            stopwatch.Begin("global layout");
            BuildGlobalLayout(state, atlasCfg);
            stopwatch.End("global layout");

            stopwatch.Begin("type group atlases");
            BuildTypeGroupAtlases(state, atlasCfg);
            stopwatch.End("type group atlases");

            ATOLog.Info($"[ATO] Packed {state.Atlases.Count} atlases. / 装箱了 {state.Atlases.Count} 张图集。");
        }

        // ====================================================================
        // Rasterization / 光栅化
        // ====================================================================

        private static void RasterizeGroup(UVGroup group, int granularity)
        {
            if (group.Mesh == null || group.UVChannelData == null || group.SubmeshIndices == null) return;
            var uvs = group.UVChannelData;
            var indices = group.SubmeshIndices;
            var tex = group.Textures.Count > 0 ? group.Textures[0].Texture : null;
            int texW = tex != null ? tex.width : 1024;
            int texH = tex != null ? tex.height : 1024;

            foreach (var island in group.Islands)
            {
                if (!island.Normalizable) continue;
                int sw = island.ScaledRect.width;
                int sh = island.ScaledRect.height;
                if (sw < 1 || sh < 1) continue;

                int cw = Mathf.Max(1, Mathf.CeilToInt(sw / (float)granularity));
                int ch = Mathf.Max(1, Mathf.CeilToInt(sh / (float)granularity));
                var mask = new RasterMask(cw, ch);

                float minU = island.BoundsUV.xMin;
                float minV = island.BoundsUV.yMin;
                float du = island.BoundsUV.width;
                float dv = island.BoundsUV.height;
                float scaleU = du > 1e-6f ? (sw / (float)granularity) / (du * texW) : 0f;
                float scaleV = dv > 1e-6f ? (sh / (float)granularity) / (dv * texH) : 0f;

                var coords = new float[island.Triangles.Count * 6];
                int ci = 0;
                foreach (var t in island.Triangles)
                {
                    int baseIdx = t * 3;
                    if (baseIdx + 2 >= indices.Length) continue;
                    for (int k = 0; k < 3; k++)
                    {
                        int vi = indices[baseIdx + k];
                        if (vi < 0 || vi >= uvs.Count) { ci += 2; continue; }
                        var uv = uvs[vi];
                        coords[ci++] = (uv.x - minU) * texW * scaleU;
                        coords[ci++] = (uv.y - minV) * texH * scaleV;
                    }
                }
                mask.RasterizeTriangles(coords);
                island.Raster = mask;
            }
        }

        // ====================================================================
        // Stage 1: global layout / 阶段 1：全局布局
        // ====================================================================

        private static void BuildGlobalLayout(ATOBuildState state, AtlasSettings cfg)
        {
            var groups = state.UVGroups
                .Where(g => !g.Whitelisted && !g.SkippedAtlas && g.Islands.Any(i => i.Raster != null))
                .ToList();
            if (groups.Count == 0) return;

            var queue = groups.Select(g => (Group: g, Area: GroupArea(g)))
                .OrderByDescending(q => q.Area) // 光栅化总面积降序
                .ToList();

            int maxSize = MaxSizeFor(state, cfg);

            while (queue.Count > 0)
            {
                long totalArea = queue.Sum(q => q.Area);
                var candidates = BuildCandidates(cfg, maxSize, totalArea);

                bool placedAll = false;
                foreach (var cand in candidates)
                {
                    var layout = new PackerLayoutRef { Width = cand.Width, Height = cand.Height };
                    if (TryPackGroups(layout, queue.Select(q => q.Group).ToList(), cfg))
                    {
                        CommitLayout(state, layout);
                        placedAll = true;
                        break;
                    }
                }

                if (placedAll)
                {
                    queue.Clear();
                    continue;
                }

                // Split path: biggest group alone in the biggest candidate.
                // 拆分路径：最大的组单独装入最大候选。
                var cand2 = BuildCandidates(cfg, maxSize, 1).Last();
                var layout2 = new PackerLayoutRef { Width = cand2.Width, Height = cand2.Height };
                var first = queue[0];
                if (TryPackGroups(layout2, new List<UVGroup> { first.Group }, cfg))
                {
                    CommitLayout(state, layout2);
                }
                else
                {
                    first.Group.SkippedAtlas = true;
                    state.Warn($"[ATO] UV group {first.Group.Space}: islands too large for the maximum atlas -> atlasization skipped, quality scaling applied. / 岛过大无法装入最大图集，跳过图集化，按质量缩放。");
                }
                queue.RemoveAt(0);
            }
        }

        private static long GroupArea(UVGroup g)
        {
            long area = 0;
            foreach (var i in g.Islands)
                if (i.Raster != null) area += (long)i.Raster.WidthCells * i.Raster.HeightCells;
            return area;
        }

        private static void CommitLayout(ATOBuildState state, PackerLayoutRef layout)
        {
            int layoutIndex = state.Layouts.Count;
            foreach (var group in layout.Groups)
            {
                // The canonical layout index is recorded on the group.
                // 规范布局索引记录在组上。
                group.AtlasIndex = layoutIndex;
            }
            state.Layouts.Add(layout);
            ATOLog.Info($"[ATO] Layout: {layout.Width}x{layout.Height}, {layout.Groups.Count} UV groups, utilization {LayoutUtilization(layout):P1}. / 布局：{layout.Width}x{layout.Height}，{layout.Groups.Count} 个 UV 组，利用率 {LayoutUtilization(layout):P1}。");
        }

        private static float LayoutUtilization(PackerLayoutRef layout)
        {
            long used = 0;
            foreach (var g in layout.Groups)
                foreach (var i in g.Islands)
                    if (i.Raster != null) used += (long)i.Raster.WidthCells * i.Raster.HeightCells;
            return (float)((double)used / ((long)layout.Width * layout.Height));
        }

        private static bool TryPackGroups(PackerLayoutRef layout, List<UVGroup> groups, AtlasSettings cfg)
        {
            var occupancy = new RasterMask(
                Mathf.CeilToInt(layout.Width / (float)cfg.RasterGranularity),
                Mathf.CeilToInt(layout.Height / (float)cfg.RasterGranularity));
            int padCells = Mathf.Max(1, Mathf.CeilToInt(PaddingFor(layout, cfg) / (float)cfg.RasterGranularity));
            var placements = new List<Placement>();

            foreach (var group in groups)
            {
                var islands = group.Islands
                    .Where(i => i.Raster != null && i.Raster.WidthCells > 0)
                    .OrderByDescending(i => (long)i.Raster.WidthCells * i.Raster.HeightCells)   // 面积降序
                    .ThenByDescending(i => Mathf.Max(i.Raster.WidthCells, i.Raster.HeightCells)) // 边长降序
                    .ToList();

                bool ok = true;
                foreach (var island in islands)
                {
                    bool placed = false;
                    int orients = (allowRotation(group) && island.Raster.WidthCells != island.Raster.HeightCells) ? 2 : 1;
                    for (int o = 0; o < orients && !placed; o++)
                    {
                        bool rotated = o == 1;
                        var m = rotated ? island.Raster.Transposed() : island.Raster;
                        var padded = Pad(m, padCells);
                        if (occupancy.FindPlacement(padded, out var px, out var py))
                        {
                            occupancy.Or(padded, px, py);
                            placements.Add(new Placement
                            {
                                Island = island,
                                Rect = new RectInt(px * cfg.RasterGranularity, py * cfg.RasterGranularity,
                                    Mathf.Max(1, m.WidthCells * cfg.RasterGranularity),
                                    Mathf.Max(1, m.HeightCells * cfg.RasterGranularity)),
                                Rotated = rotated,
                                PaddedMask = padded,
                                PlaceX = px,
                                PlaceY = py,
                            });
                            placed = true;
                        }
                    }
                    if (!placed) { ok = false; break; }
                }

                if (ok)
                {
                    // Record canonical island rects. / 记录规范岛矩形。
                    foreach (var p in placements)
                    {
                        p.Island.RotatedInAtlas = p.Rotated;
                        p.Island.ScaledRect = p.Rotated
                            ? new RectInt(p.Rect.x, p.Rect.y, p.Rect.height, p.Rect.width)
                            : p.Rect;
                    }
                    layout.Groups.Add(group);
                }
                else
                {
                    // Rollback the whole layout (groups are atomic).
                    // 回滚整个布局（组是原子的）。
                    foreach (var p in placements) ClearPadded(occupancy, p);
                    placements.Clear();
                    return false;
                }
            }
            return true;
        }

        private static bool allowRotation(UVGroup group)
        {
            // Normal-map companion textures must not rotate (tangent data kept
            // as-is). 法线伴随贴图禁止旋转（切线数据保持原样）。
            foreach (var u in group.Textures)
                if (u.Type == TextureUsageType.NormalMap) return false;
            return true;
        }

        private static int PaddingFor(PackerLayoutRef layout, AtlasSettings cfg)
        {
            return cfg.ComputePadding(Mathf.Max(layout.Width, layout.Height));
        }

        // ====================================================================
        // Stage 2: type-group atlases / 阶段 2：类型组图集
        // ====================================================================

        private static void BuildTypeGroupAtlases(ATOBuildState state, AtlasSettings cfg)
        {
            foreach (var typeGroup in state.TypeGroups)
            {
                if (typeGroup.Textures.Count == 0) continue;

                // Member UV groups: groups referencing ANY texture of this type
                // group (all textures of one UV group share the canonical rects;
                // the atlas is drawn from the type group's own texture).
                // 成员 UV 组：引用该类型组任意贴图的组（一个 UV 组的所有贴图
                // 共享规范矩形；图集从类型组自己的贴图绘制）。
                var memberGroups = state.UVGroups
                    .Where(g => !g.Whitelisted && !g.SkippedAtlas && g.Textures.Count > 0
                                && g.Textures.Any(u => typeGroup.Textures.Contains(u.Texture)))
                    .ToList();
                if (memberGroups.Count == 0) continue;

                // Group members by their canonical layout atlas.
                // 按规范布局图集对成员分组。
                var byLayout = memberGroups.GroupBy(g => g.AtlasIndex).OrderBy(grp => grp.Key);
                foreach (var grp in byLayout)
                {
                    int layoutIndex = grp.Key;
                    var layout = state.Layouts.FirstOrDefault(l =>
                        l.Groups.Any(g => g.AtlasIndex == layoutIndex));
                    if (layout == null) continue;

                    var atlas = new AtlasEntry
                    {
                        Index = state.Atlases.Count,
                        LayoutIndex = layoutIndex,
                        TypeGroup = typeGroup,
                        Width = layout.Width,
                        Height = layout.Height,
                        Padding = cfg.ComputePadding(Mathf.Max(layout.Width, layout.Height)),
                    };

                    // Reuse the canonical rects (same positions). / 复用规范矩形（相同位置）。
                    long used = 0;
                    foreach (var group in grp)
                    {
                        foreach (var island in group.Islands)
                        {
                            used += (long)island.ScaledRect.width * island.ScaledRect.height;
                        }
                        // Track which textures of THIS type group contribute.
                        foreach (var usage in group.Textures)
                        {
                            if (usage.Texture == null || !typeGroup.Textures.Contains(usage.Texture)) continue;
                            if (!atlas.Sources.ContainsKey(usage.Texture)) atlas.Sources[usage.Texture] = 0;
                            atlas.Sources[usage.Texture]++;
                        }
                        var tgTex = group.Textures.FirstOrDefault(u => typeGroup.Textures.Contains(u.Texture));
                        if (tgTex != null)
                        {
                            if (!state.TextureToAtlases.TryGetValue(tgTex.Texture, out var list))
                                state.TextureToAtlases[tgTex.Texture] = list = new List<AtlasEntry>();
                            list.Add(atlas);
                        }
                    }
                    atlas.UsedArea = used;
                    state.Atlases.Add(atlas);
                    ATOLog.Info($"[ATO] Atlas {atlas.Name}: {atlas.Width}x{atlas.Height}, utilization {atlas.Utilization:P1}, {atlas.Sources.Count} sources. / 图集 {atlas.Name}：{atlas.Width}x{atlas.Height}，利用率 {atlas.Utilization:P1}，{atlas.Sources.Count} 个来源。");
                }
            }
        }

        // ====================================================================
        // Shared / 共享
        // ====================================================================

        private sealed class Placement
        {
            public UVIsland Island;
            public RectInt Rect;
            public bool Rotated;
            public RasterMask PaddedMask;
            public int PlaceX, PlaceY;
        }

        private static void ClearPadded(RasterMask occupancy, Placement p)
        {
            for (int y = 0; y < p.PaddedMask.HeightCells; y++)
                for (int x = 0; x < p.PaddedMask.WidthCells; x++)
                    if (p.PaddedMask.Get(x, y))
                    {
                        int ax = p.PlaceX + x, ay = p.PlaceY + y;
                        if (ax < 0 || ay < 0 || ax >= occupancy.WidthCells || ay >= occupancy.HeightCells) continue;
                        int idx = ay * occupancy.WidthCells + ax;
                        occupancy.RawBits[idx >> 3] &= (byte)~(1 << (ax & 7));
                    }
        }

        private static RasterMask Pad(RasterMask mask, int padCells)
        {
            var padded = new RasterMask(mask.WidthCells + padCells * 2, mask.HeightCells + padCells * 2);
            for (int y = 0; y < mask.HeightCells; y++)
                for (int x = 0; x < mask.WidthCells; x++)
                    if (mask.Get(x, y)) padded.Set(x + padCells, y + padCells);
            return padded;
        }

        private static int MaxSizeFor(ATOBuildState state, AtlasSettings cfg)
        {
            bool mobile = state.Platform == ATOBuildPlatform.Android || state.Platform == ATOBuildPlatform.iOS;
            return mobile ? cfg.MaxSizeMobile : cfg.MaxSizePC;
        }

        private sealed class CandidateSize
        {
            public int Width, Height;
            public long Area => (long)Width * Height;
            public float Aspect => Mathf.Max(Width, Height) / (float)Mathf.Min(Width, Height);
        }

        private static List<CandidateSize> BuildCandidates(AtlasSettings cfg, int maxSize, long minArea)
        {
            var sizes = new List<int>();
            if (cfg.EnableNPOT)
            {
                for (int s = 64; s <= maxSize; s += 64) sizes.Add(s);
            }
            else
            {
                for (int s = 64; s <= maxSize; s *= 2) sizes.Add(s);
            }
            if (sizes.Count == 0) sizes.Add(maxSize);

            var candidates = new List<CandidateSize>();
            foreach (var w in sizes)
            {
                foreach (var h in sizes)
                {
                    long area = (long)w * h;
                    if (area < minArea) continue; // 丢弃面积小于 UV 总面积的候选
                    float aspect = Mathf.Max(w, h) / (float)Mathf.Min(w, h);
                    if (aspect > 2f) continue; // 保持近正方形候选
                    candidates.Add(new CandidateSize { Width = w, Height = h });
                }
            }

            // 按面积从小到大，边长由长边除短边的数值升序排序（最接近正方形最优先）
            var sorted = candidates
                .OrderBy(c => c.Area)
                .ThenBy(c => c.Aspect)
                .ToList();

            if (sorted.Count > cfg.MaxCandidates)
                sorted = sorted.GetRange(0, cfg.MaxCandidates);
            if (sorted.Count == 0)
                sorted.Add(new CandidateSize { Width = maxSize, Height = maxSize });

            return sorted;
        }
    }
}
