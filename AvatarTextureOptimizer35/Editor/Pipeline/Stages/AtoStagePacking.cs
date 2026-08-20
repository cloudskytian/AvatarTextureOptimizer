using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// Stage: atlas packing. / 阶段：装箱生成图集。
    /// Per type group: textures sorted by raster area (desc) form the queue; each queue is packed
    /// into the smallest fitting candidate (area asc, closest-to-square first); the atom is one
    /// texture with its UV groups; textures that do not fit even the largest atlas alone are
    /// skipped (atlas-skipped → whole-texture path) with a warning. Atlas count is unlimited. /
    /// 每个类型组：贴图按光栅面积降序形成队列；每个队列装入最小可装下的候选（面积升序、最接近正方形优先）；
    /// 装箱原子 = 单张贴图及其 UV 组；单张贴图连最大图集都装不下则放弃图集化（走整图路径）并告警。
    /// 图集数量不限。
    /// </summary>
    internal sealed class AtoStagePacking : IAtoStage
    {
        public string I18nKey => "packing";

        public void Run(AtoContext ctx)
        {
            var settings = ctx.State.Settings;
            if (!settings.generateAtlases)
            {
                AtoLog.Info("[ATO] atlas generation disabled: all textures take the whole-texture path.");
                return;
            }

            var packer = new AtoAtlasPacker(ctx);

            // Main-color groups first: their placements become the shared reference for other groups. /
            // 主色组优先：其放置成为其他组的共享参考。
            var orderedGroups = ctx.TypeGroups
                .OrderByDescending(g => g.Slots.Any(s => s.Usage.Kind == AtoTextureKind.Main))
                .ThenByDescending(g => TotalGroupArea(g))
                .ToList();

            foreach (var group in orderedGroups)
            {
                ctx.State.ThrowIfCancelled();

                var allowRotation = !group.ContainsTangentData;

                // Textures of this group, with their islands (only non-whitelisted UV groups atlased). /
                // 该组的贴图及其岛（仅非白名单 UV 组参与图集化）。
                var textureIslands = new Dictionary<Texture2D, List<AtoIsland>>();
                foreach (var uvGroup in group.UvGroups)
                {
                    if (uvGroup.Whitelisted) continue;
                    foreach (var island in uvGroup.Islands)
                    {
                        foreach (var slot in uvGroup.Slots)
                        {
                            if (slot.Texture == null || ctx.IsWhitelisted(slot.Texture)) continue;
                            if (!group.Slots.Contains(slot)) continue;
                            if (!textureIslands.TryGetValue(slot.Texture, out var list))
                            {
                                textureIslands[slot.Texture] = list = new List<AtoIsland>();
                            }
                            if (!list.Contains(island)) list.Add(island);
                        }
                    }
                }

                var remaining = textureIslands.Keys
                    .OrderByDescending(t => TextureArea(t, textureIslands[t]))
                    .ToList();

                var atlasIndex = 0;
                while (remaining.Count > 0)
                {
                    ctx.State.ThrowIfCancelled();
                    var packed = false;

                    // Try the largest possible subset first (queue fits one atlas). / 先尝试最大子集（队列装进一张图集）。
                    for (var k = remaining.Count; k >= 1; k--)
                    {
                        var subset = remaining.Take(k).ToList();
                        var islandSources = new Dictionary<AtoIsland, Texture2D>();
                        var conflict = false;
                        foreach (var texture in subset)
                        {
                            foreach (var island in textureIslands[texture])
                            {
                                if (islandSources.TryGetValue(island, out var other) && other != texture)
                                {
                                    // Two textures sharing an island (animation swap partners) cannot
                                    // share one atlas: the same UV rect cannot hold two contents. /
                                    // 共享同一岛的两张贴图（动画切换伙伴）不能共用一张图集：同一 UV 矩形无法容纳两份内容。
                                    conflict = true;
                                    break;
                                }
                                islandSources[island] = texture;
                            }
                            if (conflict) break;
                        }
                        if (conflict) continue; // try a smaller subset. / 尝试更小的子集。

                        var (minW, minH) = RequiredSides(subset, textureIslands);
                        if (minW > packer.MaxSide || minH > packer.MaxSide) continue;

                        if (packer.TryPack(islandSources, minW, minH, allowRotation,
                                out var width, out var height, out var newPlacements))
                        {
                            var atlas = new AtoAtlas
                            {
                                Group = group,
                                Name = $"ATO_{group.Key.KindSignature}_{group.Atlases.Count}_{width}x{height}",
                                Width = width,
                                Height = height,
                            };

                            foreach (var kv in islandSources)
                            {
                                var island = kv.Key;
                                AtoPlacedIsland placed;
                                if (ctx.PlacedIslands.TryGetValue(island, out var existing))
                                {
                                    placed = existing;
                                }
                                else
                                {
                                    placed = CreatePlacement(island, newPlacements[island]);
                                    // Register so all later type groups reuse the same position. /
                                    // 注册，让后续所有类型组复用同一位置。
                                    ctx.PlacedIslands[island] = placed;
                                }
                                atlas.Placed.Add(placed);
                                atlas.SourceByIsland[island] = kv.Value;
                                atlas.SourceTextures.Add(kv.Value);
                            }
                            group.Atlases.Add(atlas);
                            ctx.State.AtlasCount++;

                            // Remove the packed subset from the queue. / 从队列移除已装箱子集。
                            remaining = remaining.Skip(k).ToList();
                            packed = true;
                            break;
                        }
                    }

                    if (!packed)
                    {
                        // The single largest texture does not fit the max atlas → skip atlasing for it. /
                        // 单张最大贴图连最大图集都装不下 → 放弃其图集化。
                        var tooBig = remaining[0];
                        foreach (var island in textureIslands[tooBig])
                        {
                            island.UvGroup.AtlasSkipped = true;
                        }
                        ctx.Warn(ctx.State.Tr("warn.tooLargeForAtlas", tooBig.name, packer.MaxSide));
                        remaining.RemoveAt(0);
                    }
                    atlasIndex++;
                }
            }

            AtoLog.Info($"[ATO] packing: {ctx.State.AtlasCount} atlas(es) planned across {orderedGroups.Count} type group(s).");
        }

        private static AtoPlacedIsland CreatePlacement(AtoIsland island, (Vector2 origin, int rotation) placement)
        {
            var placed = new AtoPlacedIsland
            {
                Island = island,
                UvOrigin = placement.origin,
                Rotation = placement.rotation,
            };
            return placed;
        }

        /// <summary>
        /// Required minimum atlas sides: for each island, atlas width must satisfy
        /// W ≥ T_w × s_i^t / s_i (its quality-passed pixel size); same for height. /
        /// 所需的图集最小边长：每个岛须满足 W ≥ T_w × s_i^t / s_i（其质量达标像素尺寸）；高度同理。
        /// </summary>
        private static (int, int) RequiredSides(List<Texture2D> textures,
            Dictionary<Texture2D, List<AtoIsland>> textureIslands)
        {
            var minW = 1;
            var minH = 1;
            foreach (var texture in textures)
            {
                foreach (var island in textureIslands[texture])
                {
                    var uvSize = island.UvMax - island.UvMin;
                    var finalSize = island.FinalUvMax - island.FinalUvMin;
                    if (finalSize.x <= 1e-6f || finalSize.y <= 1e-6f) continue;
                    var sx = finalSize.x / Mathf.Max(1e-6f, uvSize.x);
                    var sy = finalSize.y / Mathf.Max(1e-6f, uvSize.y);
                    if (island.PerTextureScale.TryGetValue(texture, out var scale))
                    {
                        var reqW = Mathf.CeilToInt(texture.width * scale.x / Mathf.Max(1e-6f, sx));
                        var reqH = Mathf.CeilToInt(texture.height * scale.y / Mathf.Max(1e-6f, sy));
                        minW = Mathf.Max(minW, reqW);
                        minH = Mathf.Max(minH, reqH);
                    }
                }
            }
            return (minW, minH);
        }

        private static double TextureArea(Texture2D texture, List<AtoIsland> islands)
        {
            double area = 0;
            foreach (var island in islands)
            {
                var uvSize = island.FinalUvMax - island.FinalUvMin;
                area += (double)uvSize.x * texture.width * (double)uvSize.y * texture.height;
            }
            return area;
        }

        private static double TotalGroupArea(AtoTypeGroup group)
        {
            double area = 0;
            foreach (var uvGroup in group.UvGroups)
            {
                foreach (var island in uvGroup.Islands)
                {
                    var uvSize = island.FinalUvMax - island.FinalUvMin;
                    area += (double)uvSize.x * uvSize.y;
                }
            }
            return area;
        }
    }
}
