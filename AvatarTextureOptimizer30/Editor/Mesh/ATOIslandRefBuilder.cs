// ATOIslandRefBuilder.cs — 岛引用构建器 / Island-reference builder.
// 说明：建立"UV 岛 ↔ 贴图"映射：对每个岛收集引用它的全部（贴图 × 角色）组（聚合多个材质用途），
// 计算每份引用的裁剪像素矩形（含合并岛的子矩形合并与偏移）、越界/白名单标记。
// Note: builds the island ↔ texture mapping: collects all (texture × role) groups referencing each island
// (aggregating multiple material usages), computes per-ref crop pixel rects (incl. merged-island union rects
// and offsets), wrap-issue and whitelist flags.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Fosa.AvatarTextureOptimizer
{
    /// <summary>岛引用构建器。/ Island-reference builder.</summary>
    internal static class ATOIslandRefBuilder
    {
        /// <summary>
        /// 为全部岛构建贴图引用。
        /// Build texture references for all islands.
        /// </summary>
        public static void BuildRefs(List<ATOIsland> islands, List<ATORendererInfo> renderers,
            HashSet<Texture2D> whitelistedTextures)
        {
            foreach (var island in islands)
            {
                BuildRefsForIsland(island, renderers, whitelistedTextures);
            }
        }

        private static void BuildRefsForIsland(ATOIsland island, List<ATORendererInfo> renderers,
            HashSet<Texture2D> whitelistedTextures)
        {
            // (texture, role) → ref / (texture, role) → ref
            var refMap = new Dictionary<(Texture2D, ATORole), ATOIslandRef>();

            foreach (var renderer in renderers)
            {
                if (renderer.mesh != island.mesh) continue;
                foreach (var usage in renderer.usages)
                {
                    if (usage.uvChannel != island.channel) continue;
                    var key = (usage.texture, usage.role);
                    if (!refMap.TryGetValue(key, out var islandRef))
                    {
                        islandRef = new ATOIslandRef
                        {
                            texture = usage.texture,
                            role = usage.role,
                            category = usage.Category,
                        };
                        refMap[key] = islandRef;
                        island.refs.Add(islandRef);
                    }
                    islandRef.usages.Add(usage);
                }
            }

            // 每份引用的裁剪矩形 / crop rects per ref
            foreach (var islandRef in island.refs)
            {
                var tex = islandRef.texture;
                var w = tex.width;
                var h = tex.height;

                // 子岛（合并岛）各自矩形 → 并集 / child rects (merged islands) → union
                var children = island.merged ? island.mergedChildren : new List<ATOIsland> { island };
                var unionMin = new Vector2(float.MaxValue, float.MaxValue);
                var unionMax = new Vector2(float.MinValue, float.MinValue);
                var any = false;

                foreach (var child in children)
                {
                    // 该子岛是否被本引用覆盖：合并岛下，子岛引用由合并前的映射决定；简化处理：按几何覆盖全部子岛/
                    // whether this child is covered by this ref: for merged islands, treat as covered by geometry
                    var min = child.uvMin + child.translation;
                    var max = child.uvMax + child.translation;
                    if (min.x < unionMin.x) unionMin.x = min.x;
                    if (min.y < unionMin.y) unionMin.y = min.y;
                    if (max.x > unionMax.x) unionMax.x = max.x;
                    if (max.y > unionMax.y) unionMax.y = max.y;
                    any = true;
                }
                if (!any) continue;

                var x0 = Mathf.Clamp((int)Mathf.Floor(unionMin.x * w), 0, w - 1);
                var y0 = Mathf.Clamp((int)Mathf.Floor(unionMin.y * h), 0, h - 1);
                var x1 = Mathf.Clamp((int)Mathf.Ceil(unionMax.x * w), x0 + 1, w);
                var y1 = Mathf.Clamp((int)Mathf.Ceil(unionMax.y * h), y0 + 1, h);
                islandRef.cropRect = new RectInt(x0, y0, x1 - x0, y1 - y0);
                islandRef.nativeWidth = islandRef.cropRect.width;
                islandRef.nativeHeight = islandRef.cropRect.height;

                // 合并岛：内容偏移 = 子并集相对合并包围盒的偏移（归一化）/ merged: content offset relative to the merged bbox (normalized)
                var islandMin = island.uvMin + island.translation;
                var islandMax = island.uvMax + island.translation;
                var span = islandMax - islandMin;
                islandRef.cropOffset = new Vector2(
                    span.x > 1e-6f ? (unionMin.x - islandMin.x) / span.x : 0f,
                    span.y > 1e-6f ? (unionMin.y - islandMin.y) / span.y : 0f);

                // 白名单与越界 / whitelist & wrap
                foreach (var usage in islandRef.usages)
                {
                    if (usage.whitelisted || whitelistedTextures.Contains(tex))
                    {
                        islandRef.whitelisted = true;
                        islandRef.whitelistReason = usage.whitelistReason ?? "Texture whitelisted";
                    }
                }
                if (island.wrapIssue && !islandRef.whitelisted)
                {
                    islandRef.whitelisted = true;
                    islandRef.whitelistReason = "UV crosses wrap seam (cannot normalize into [0,1])";
                }
            }

            island.anyWhitelistedRef = false;
            foreach (var r in island.refs)
            {
                if (r.whitelisted)
                {
                    island.anyWhitelistedRef = true;
                    break;
                }
            }
        }
    }
}
