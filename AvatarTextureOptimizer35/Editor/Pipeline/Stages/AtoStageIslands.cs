using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// Stage: island extraction. / 阶段：岛提取。
    /// Per UV group: union-find island segmentation, world sizes, blend-shape factors, whitelist
    /// propagation (a whitelisted texture whitelists the whole UV group), wrap normalization
    /// (integer translation; clamp/mirror/repeat safety), overlapping-island merging. /
    /// 每个 UV 组：并查集岛分割、世界尺寸、形态键系数、白名单传播（一张白名单贴图 → 整组白名单）、
    /// wrap 归一化（整数平移；Clamp/Mirror/Repeat 安全判定）、重叠岛合并。
    /// </summary>
    internal sealed class AtoStageIslands : IAtoStage
    {
        public string I18nKey => "islands";

        public void Run(AtoContext ctx)
        {
            var state = ctx.State;
            var rendererIndex = 0;

            foreach (var data in ctx.Renderers)
            {
                state.SetProgress($"islands for {data.Renderer.name}",
                    (float)rendererIndex / Mathf.Max(1, ctx.Renderers.Count));

                // Read mesh data once per mesh. / 每网格读取一次网格数据。
                var mesh = data.Mesh;
                var vertices = mesh.vertices;
                var triangles = mesh.triangles;

                foreach (var kv in data.UvGroups.ToList())
                {
                    var channel = kv.Key;
                    var uvGroup = kv.Value;

                    // ---- whitelist propagation: one whitelisted texture → whole group ----
                    foreach (var slot in uvGroup.Slots)
                    {
                        if (ctx.IsWhitelisted(slot.Texture))
                        {
                            WhitelistGroup(ctx, uvGroup,
                                $"contains whitelisted texture {slot.Texture.name}");
                            break;
                        }
                    }

                    // ---- island extraction ----
                    var uvs = new List<Vector2>();
                    mesh.GetUVs(channel, uvs);
                    if (uvs.Count == 0)
                    {
                        // Channel not defined on this mesh: nothing to process. / 通道未定义：无可处理内容。
                        continue;
                    }
                    uvGroup.Islands = AtoIslandBuilder.Build(uvGroup, uvs, triangles);
                    if (uvGroup.Islands.Count == 0) continue;

                    // ---- per-island world size & blend-shape factor ----
                    foreach (var island in uvGroup.Islands)
                    {
                        island.WorldSize = AtoIslandBuilder.ComputeWorldSize(
                            data.Renderer.transform, island, vertices, uvGroup.MaxAnimatedScale);
                        island.BlendShapeFactor = AtoIslandBuilder.ComputeBlendShapeFactor(mesh, island, vertices);
                        // Fold the blend-shape area growth into the world size (density). /
                        // 把形态键面积增长并入世界尺寸（影响密度）。
                        var factor = Mathf.Sqrt(Mathf.Max(1f, island.BlendShapeFactor));
                        island.WorldSize = new Vector2(island.WorldSize.x * factor, island.WorldSize.y * factor);
                    }

                    // Always compute the normalization translation: the quality evaluator uses it
                    // as a coordinate offset for wrap-aware crops (never applied to the mesh for
                    // whitelisted groups). / 始终计算归一平移：质量评估器用它作为裁剪坐标偏移
                    // （白名单组绝不应用到网格）。
                    foreach (var island in uvGroup.Islands)
                    {
                        var translation = AtoIslandBuilder.GetNormalizingTranslation(island);
                        if (translation != null)
                        {
                            island.NormalizationTranslation = translation.Value;
                        }
                        // null translation (multi-tile island) keeps (0,0); such islands get a
                        // conservative no-shrink scale for whitelisted groups (handled in the
                        // quality stage). / null 平移（跨多 tile）保持 (0,0)；此类岛在白名单组中
                        // 走保守不缩放（质量阶段处理）。
                    }

                    if (uvGroup.Whitelisted) continue; // still extracted (co-UV textures scale whole textures), but no rewrite. / 仍提取（同 UV 贴图整图缩放），但不重写。

                    // ---- wrap normalization (safety whitelisting, non-whitelisted groups only) ----
                    if (!NormalizeWrap(ctx, uvGroup))
                    {
                        continue; // group whitelisted by NormalizeWrap. / NormalizeWrap 已白名单化该组。
                    }

                    // ---- overlapping island merging (same texture, mask overlap) ----
                    MergeOverlappingIslands(ctx, uvGroup, uvs);

                    ctx.State.IslandCount += uvGroup.Islands.Count;
                }
                rendererIndex++;
            }

            // Plan AAO evacuations BEFORE quality/packing so a failed evacuation whitelists early. /
            // 在质量/装箱之前规划 AAO 疏散，失败时尽早白名单化。
            AtoAaoEvacuation.Plan(ctx);

            AtoLog.Info($"[ATO] islands: {state.IslandCount} island(s) across {ctx.UvGroups.Count} UV group(s).");
        }

        private static void WhitelistGroup(AtoContext ctx, AtoUvGroup uvGroup, string reason)
        {
            if (uvGroup.Whitelisted) return;
            uvGroup.Whitelisted = true;
            uvGroup.WhitelistReason = reason;
            AtoLog.Verbose($"[ATO] UV group {uvGroup.DisplayName} whitelisted: {reason}");
        }

        /// <summary>
        /// Wrap normalization: translate islands into [0,1] when safe. Returns false if the group
        /// was whitelisted. / wrap 归一化：安全时把岛整数平移进 [0,1]。若组被白名单化返回 false。
        /// </summary>
        private static bool NormalizeWrap(AtoContext ctx, AtoUvGroup uvGroup)
        {
            foreach (var island in uvGroup.Islands)
            {
                var translation = AtoIslandBuilder.GetNormalizingTranslation(island);
                if (translation == null)
                {
                    WhitelistGroup(ctx, uvGroup, $"island spans multiple wrap tiles (repeat dependency)");
                    ctx.Warn(ctx.State.Tr("warn.uvWrapCrossing", uvGroup.DisplayName));
                    return false;
                }

                // Wrap-mode safety: Clamp textures must not be translated; Mirror needs even steps. /
                // wrap 模式安全：Clamp 贴图不可平移；Mirror 需偶数步长。
                foreach (var slot in uvGroup.Slots)
                {
                    var texture = slot.Texture;
                    if (texture == null) continue;
                    if (translation.Value.x != 0)
                    {
                        var wrap = texture.wrapModeU;
                        if (wrap == TextureWrapMode.Clamp)
                        {
                            WhitelistGroup(ctx, uvGroup,
                                $"out-of-bounds UV with Clamp-wrapped texture {texture.name}");
                            return false;
                        }
                        if (wrap == TextureWrapMode.Mirror && (translation.Value.x & 1) != 0)
                        {
                            WhitelistGroup(ctx, uvGroup,
                                $"odd mirror-wrap translation for texture {texture.name}");
                            return false;
                        }
                    }
                    if (translation.Value.y != 0)
                    {
                        var wrap = texture.wrapModeV;
                        if (wrap == TextureWrapMode.Clamp)
                        {
                            WhitelistGroup(ctx, uvGroup,
                                $"out-of-bounds UV with Clamp-wrapped texture {texture.name}");
                            return false;
                        }
                        if (wrap == TextureWrapMode.Mirror && (translation.Value.y & 1) != 0)
                        {
                            WhitelistGroup(ctx, uvGroup,
                                $"odd mirror-wrap translation for texture {texture.name}");
                            return false;
                        }
                    }
                }

                island.NormalizationTranslation = translation.Value;
            }
            return true;
        }

        /// <summary>
        /// Merge islands whose raster masks overlap on ANY texture of the group (transitively). /
        /// 若两岛的栅格掩码在组内任一张贴图上重叠则合并（传递闭包）。
        /// </summary>
        private static void MergeOverlappingIslands(AtoContext ctx, AtoUvGroup uvGroup, List<Vector2> uvs)
        {
            var islands = uvGroup.Islands;
            var n = islands.Count;
            if (n <= 1) return;

            var parent = new int[n];
            for (var i = 0; i < n; i++) parent[i] = i;

            int Find(int x)
            {
                while (parent[x] != x)
                {
                    parent[x] = parent[parent[x]];
                    x = parent[x];
                }
                return x;
            }

            // For each texture of the group: rasterize each island and union overlapping ones. /
            // 对组内每张贴图：光栅化各岛并合并重叠者。
            foreach (var texture in uvGroup.Slots.Select(s => s.Texture).Distinct())
            {
                if (texture == null) continue;
                var width = Mathf.Min(1024, texture.width);
                var height = Mathf.Min(1024, texture.height);

                var masks = new List<byte[]>(n);
                var boxes = new List<Rect>(n);
                for (var i = 0; i < n; i++)
                {
                    var island = islands[i];
                    // Quick reject first (bbox in pixels). / 先按像素包围盒快速排除。
                    var min = new Vector2(
                        (island.UvMin.x - island.NormalizationTranslation.x) * width,
                        (island.UvMin.y - island.NormalizationTranslation.y) * height);
                    var max = new Vector2(
                        (island.UvMax.x - island.NormalizationTranslation.x) * width,
                        (island.UvMax.y - island.NormalizationTranslation.y) * height);
                    boxes.Add(Rect.MinMaxRect(min.x, min.y, max.x, max.y));

                    // All islands are rasterized into the SAME texture-pixel space [0,1]→(w,h),
                    // with the island's normalization translation applied, so masks are comparable. /
                    // 所有岛光栅化到同一纹理像素空间 [0,1]→(w,h) 并应用归一平移，保证掩码可比较。
                    var mask = new byte[width * height];
                    AtoRasterizer.Rasterize(uvs, island.Triangles,
                        Vector2.zero, Vector2.one, width, height, mask,
                        new Vector2(island.NormalizationTranslation.x, island.NormalizationTranslation.y));
                    masks.Add(mask);
                }

                for (var i = 0; i < n; i++)
                {
                    for (var j = i + 1; j < n; j++)
                    {
                        if (Find(i) == Find(j)) continue;
                        if (!boxes[i].Overlaps(boxes[j])) continue;
                        if (AtoRasterizer.Overlaps(masks[i], masks[j]))
                        {
                            parent[Find(i)] = Find(j);
                        }
                    }
                }
            }

            // Apply merges. / 应用合并。
            var merged = new Dictionary<int, List<AtoIsland>>();
            for (var i = 0; i < n; i++)
            {
                var root = Find(i);
                if (!merged.TryGetValue(root, out var list)) merged[root] = list = new List<AtoIsland>();
                list.Add(islands[i]);
            }
            if (merged.Count == n) return; // nothing merged. / 无需合并。

            var newIslands = new List<AtoIsland>();
            foreach (var group in merged.Values)
            {
                if (group.Count == 1)
                {
                    newIslands.Add(group[0]);
                    continue;
                }
                var mergedIsland = new AtoIsland
                {
                    UvGroup = uvGroup,
                    Index = newIslands.Count,
                    UvMin = new Vector2(float.MaxValue, float.MaxValue),
                    UvMax = new Vector2(float.MinValue, float.MinValue),
                };
                foreach (var island in group)
                {
                    mergedIsland.Triangles.AddRange(island.Triangles);
                    mergedIsland.UvMin = Vector2.Min(mergedIsland.UvMin, island.UvMin);
                    mergedIsland.UvMax = Vector2.Max(mergedIsland.UvMax, island.UvMax);
                }
                AtoLog.Verbose($"[ATO] merged {group.Count} overlapping islands in {uvGroup.DisplayName}");
                newIslands.Add(mergedIsland);
            }
            // Re-index. / 重新编号。
            for (var i = 0; i < newIslands.Count; i++) newIslands[i].Index = i;
            uvGroup.Islands = newIslands;
        }
    }
}
