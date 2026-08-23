// Packing plan: unified layout canvas (UV-consistent across type groups), type-group atlas
// assignment with optional uniform downscale for non-main type groups, whole-texture scaling
// for fallback / no-atlas paths.
// / 装箱计划：统一布局画布（保证 UV 跨类型组一致）、类型组图集分配（非主色类型组可整体等比缩小）、
// 回退/无图集路径的整图缩放。

using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using net.fosa.avatar_texture_optimizer.editor.analysis;
using net.fosa.avatar_texture_optimizer.editor.pipeline;
using net.fosa.avatar_texture_optimizer.runtime;

namespace net.fosa.avatar_texture_optimizer.editor.packing
{
    /// <summary>One island drawn into a type-group atlas. / 绘制进类型组图集的一个岛。</summary>
    public sealed class AtlasEntry
    {
        public Island Island;
        public GroupTexture Texture;
        public int X, Y, W, H;          // final atlas rect (post-downscale) / 最终图集矩形（缩放后）
        public bool Rotated90;
    }

    /// <summary>One atlas (a type group). / 一张图集（一个类型组）。</summary>
    public sealed class AtlasPlan
    {
        public string TypeGroupKey;
        public int CanvasSize;
        public readonly List<AtlasEntry> Entries = new List<AtlasEntry>();
        public bool HasMainColor;
    }

    /// <summary>Packing result. / 装箱结果。</summary>
    public sealed class PackingResult
    {
        public readonly List<AtlasPlan> Atlases = new List<AtlasPlan>();
        public readonly List<TexRecord> WholeScaleRecords = new List<TexRecord>(); // records needing whole-texture scaling / 需要整图缩放的记录
        public int LayoutSize = 1;
    }

    /// <summary>
    /// Builds the packing plan. / 构建装箱计划。
    /// </summary>
    public static class PackingPlanner
    {
        /// <summary>Compute the type-group key for a texture usage. / 计算贴图的类型组键。</summary>
        public static string TypeGroupKey(TexRecord record, GroupTexture gt)
        {
            bool main = gt.Roles.Contains(TextureRole.MainColor) || gt.Roles.Contains(TextureRole.Other);
            bool normal = gt.Roles.Contains(TextureRole.Normal);
            bool mask = gt.Roles.Contains(TextureRole.Mask);
            string rolePart = main ? "main" : normal ? "normal" : mask ? "mask" : "main";
            if (main && normal) rolePart = "main_normal";
            if (main && mask) rolePart = "main_mask";
            if (normal && mask) rolePart = "normal_mask";
            if (main && normal && mask) rolePart = "main_normal_mask";
            return rolePart + "|" + (record.IsSrgb ? "srgb" : "linear") + "|" + record.FilterMode.ToString().ToLowerInvariant();
        }

        /// <summary>Candidate atlas sizes (POT or NPOT). / 候选图集边长（POT 或 NPOT）。</summary>
        public static List<int> CandidateSizes(int minSize, int maxSize, bool npot)
        {
            var list = new List<int>();
            if (npot)
            {
                for (int s = minSize; s <= maxSize; s += 64) list.Add(s);
            }
            else
            {
                int s = 64;
                while (s < minSize) s *= 2;
                for (; s <= maxSize; s *= 2) list.Add(s);
            }
            if (list.Count == 0) list.Add(maxSize);
            return list;
        }

        /// <summary>Build the plan. / 构建计划。</summary>
        public static PackingResult Plan(AnalysisResult analysis, AvatarTextureOptimizer component,
            BuildTargetHint platformHint, bool mobile, ProgressScope progress)
        {
            var result = new PackingResult();

            if (!component.generateAtlas)
            {
                // No atlas mode: scale whole textures / 无图集模式：整图缩放
                foreach (var record in analysis.Textures)
                {
                    if (record.Whitelisted) continue;
                    result.WholeScaleRecords.Add(record);
                }
                return result;
            }

            int maxSize = component.EffectiveMaxAtlasSize(platformHint, mobile);
            var candidates = CandidateSizes(component.packing.minAtlasSize, maxSize, component.packing.allowNPOT);

            // Decide participation / 决定参与
            var participating = new List<UVGroup>();
            foreach (var group in analysis.UvGroups)
            {
                if (group.Whitelisted)
                {
                    // same-UV other textures skip atlas; whitelisted ones untouched / 同 UV 其他贴图跳过图集化
                    foreach (var gt in group.Textures)
                    {
                        if (!gt.Record.Whitelisted && !result.WholeScaleRecords.Contains(gt.Record))
                            result.WholeScaleRecords.Add(gt.Record);
                    }
                    continue;
                }
                participating.Add(group);
            }

            if (participating.Count == 0) return result;

            // Build items and try to pack into the unified canvas / 构建项并尝试装入统一画布
            var allItems = new List<RasterPacker.PackItem>();
            var groupItems = new Dictionary<UVGroup, List<RasterPacker.PackItem>>();
            long totalArea = 0;
            foreach (var group in participating)
            {
                var items = new List<RasterPacker.PackItem>();
                foreach (var iso in group.Islands)
                {
                    var item = new RasterPacker.PackItem { Island = iso, W = iso.AtlasW, H = iso.AtlasH };
                    items.Add(item);
                    allItems.Add(item);
                    totalArea += (long)iso.AtlasW * iso.AtlasH;
                }
                groupItems[group] = items;
            }

            var masks = RasterPacker.Rasterize(allItems);

            // Try candidates ascending, skipping too-small area / 按面积升序尝试候选，跳过面积过小的
            int layoutSize = 0;
            var placements = new Dictionary<int, RasterPacker.Placement>();
            var packedGroups = new HashSet<UVGroup>();
            bool ok = false;

            foreach (var size in candidates)
            {
                long canvasArea = (long)size * size;
                if (canvasArea < totalArea) continue;   // discard candidates too small / 丢弃面积小于总面积的候选
                int pad = PaddingFor(size, component.packing.minPadding);
                if (RasterPacker.TryPack(allItems, masks, size, pad, out placements))
                {
                    layoutSize = size;
                    packedGroups.UnionWith(participating);
                    ok = true;
                    break;
                }
            }

            if (!ok)
            {
                // Fallback: drop the largest groups until it fits / 回退：逐个丢弃面积最大的组直至装下
                var remaining = new List<UVGroup>(participating);
                remaining.Sort((a, b) => GroupArea(b).CompareTo(GroupArea(a)));
                var active = new List<UVGroup>(remaining);
                foreach (var size in candidates)
                {
                    long canvasArea = (long)size * size;
                    if (canvasArea < totalArea) continue;
                    while (active.Count > 0)
                    {
                        if (RasterPacker.TryPack(CollectItems(active), RasterizeItems(active), size,
                                PaddingFor(size, component.packing.minPadding), out placements))
                        {
                            layoutSize = size;
                            packedGroups.UnionWith(active);
                            ok = true;
                            break;
                        }
                        var drop = active[0];
                        active.RemoveAt(0);
                        var droppedItems = groupItems[drop];
                        foreach (var it in droppedItems)
                        {
                            foreach (var gt in drop.Textures)
                            {
                                if (!gt.Record.Whitelisted && !result.WholeScaleRecords.Contains(gt.Record))
                                {
                                    result.WholeScaleRecords.Add(gt.Record);
                                    gt.Record.Skipped = true;
                                    gt.Record.SkipReason = "island group too large for max atlas / 岛组超过最大图集";
                                    AtoLog.Warn("UV group on '" + drop.Mesh.Renderer.name +
                                        "' cannot fit the maximum atlas; falling back to whole-texture scaling. / 无法装入最大图集，回退为整图缩放。");
                                }
                            }
                        }
                        totalArea -= GroupArea(drop);
                    }
                    if (ok) break;
                }
                if (!ok)
                {
                    foreach (var group in participating)
                    {
                        if (packedGroups.Contains(group)) continue;
                        foreach (var gt in group.Textures)
                        {
                            if (!gt.Record.Whitelisted && !result.WholeScaleRecords.Contains(gt.Record))
                            {
                                result.WholeScaleRecords.Add(gt.Record);
                                gt.Record.Skipped = true;
                                gt.Record.SkipReason = "packing failed / 装箱失败";
                            }
                        }
                    }
                }
            }

            if (!ok)
            {
                AtoLog.Warn("Atlas packing failed entirely; all textures fall back to whole-texture scaling. / 图集装箱整体失败，全部回退为整图缩放。");
                foreach (var record in analysis.Textures)
                {
                    if (!record.Whitelisted && !result.WholeScaleRecords.Contains(record))
                        result.WholeScaleRecords.Add(record);
                }
                return result;
            }

            result.LayoutSize = layoutSize;

            // Apply placements to islands / 把放置结果写回岛
            for (int i = 0; i < allItems.Count; i++)
            {
                if (!placements.TryGetValue(i, out var pl)) continue;
                var iso = allItems[i].Island;
                iso.AtlasX = pl.X; iso.AtlasY = pl.Y;
                iso.Rotated90 = pl.Rotated90;
            }

            // Group into type-group atlases / 按类型组形成图集
            var byKey = new Dictionary<string, AtlasPlan>();
            foreach (var group in participating)
            {
                if (!packedGroups.Contains(group) && groupItems.ContainsKey(group)) continue;
                foreach (var gt in group.Textures)
                {
                    if (gt.Record.Whitelisted) continue;
                    if (gt.Record.Skipped) continue;
                    string key = TypeGroupKey(gt.Record, gt);
                    if (!byKey.TryGetValue(key, out var plan))
                    {
                        plan = new AtlasPlan { TypeGroupKey = key, CanvasSize = layoutSize };
                        byKey[key] = plan;
                    }
                    if (gt.Roles.Contains(TextureRole.MainColor) || gt.Roles.Contains(TextureRole.Other)) plan.HasMainColor = true;
                    foreach (var iso in group.Islands)
                    {
                        plan.Entries.Add(new AtlasEntry
                        {
                            Island = iso,
                            Texture = gt,
                            X = iso.AtlasX, Y = iso.AtlasY,
                            W = iso.AtlasW, H = iso.AtlasH,
                            Rotated90 = iso.Rotated90,
                        });
                    }
                }
            }

            // Optional uniform downscale for non-main type groups / 非主色类型组可选整体缩小
            foreach (var plan in byKey.Values)
            {
                if (plan.Entries.Count == 0) continue;
                if (!plan.HasMainColor)
                {
                    int maxX = 0, maxY = 0;
                    foreach (var e in plan.Entries)
                    {
                        maxX = Mathf.Max(maxX, e.X + e.W);
                        maxY = Mathf.Max(maxY, e.Y + e.H);
                    }
                    int content = Mathf.Max(maxX, maxY);
                    int pad = PaddingFor(layoutSize, component.packing.minPadding);
                    foreach (var size in candidates)
                    {
                        if (size >= content + pad * 2 && size < layoutSize)
                        {
                            float k = size / (float)layoutSize;
                            // keep min padding after scaling / 保证缩放后仍满足最小 padding
                            float minK = component.packing.minPadding / (float)pad;
                            if (k < minK) continue;
                            plan.CanvasSize = size;
                            foreach (var e in plan.Entries)
                            {
                                e.X = (int)(e.X * k); e.Y = (int)(e.Y * k);
                                e.W = System.Math.Max(1, (int)(e.W * k)); e.H = System.Math.Max(1, (int)(e.H * k));
                            }
                            break;
                        }
                    }
                }
                result.Atlases.Add(plan);
            }

            return result;
        }

        private static long GroupArea(UVGroup g)
        {
            long area = 0;
            foreach (var iso in g.Islands) area += (long)iso.AtlasW * iso.AtlasH;
            return area;
        }

        private static List<RasterPacker.PackItem> CollectItems(List<UVGroup> groups)
        {
            var items = new List<RasterPacker.PackItem>();
            foreach (var g in groups)
            {
                foreach (var iso in g.Islands)
                {
                    items.Add(new RasterPacker.PackItem { Island = iso, W = iso.AtlasW, H = iso.AtlasH });
                }
            }
            return items;
        }

        private static List<RasterPacker.MaskData> RasterizeItems(List<UVGroup> groups)
        {
            return RasterPacker.Rasterize(CollectItems(groups));
        }

        /// <summary>padding = max(minPadding, ceil(size/128)), clamped to at least 4. / padding = max(minPadding, ceil(size/128))，下限 4。</summary>
        public static int PaddingFor(int size, int minPadding)
        {
            int auto = (size + 127) / 128;
            return Mathf.Max(4, Mathf.Max(minPadding, auto));
        }
    }
}
