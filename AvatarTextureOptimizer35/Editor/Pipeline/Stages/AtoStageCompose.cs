using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// Stage: atlas composition + whole-texture scaling. / 阶段：图集合成 + 整图缩放。
    /// Produces the atlas textures (PNG in the output folder) and the whole-texture fallbacks;
    /// registers all texture remaps. / 生成图集贴图（输出目录 PNG）与整图 fallback；登记全部贴图重映射。
    /// </summary>
    internal sealed class AtoStageCompose : IAtoStage
    {
        public string I18nKey => "compose";

        public void Run(AtoContext ctx)
        {
            var settings = ctx.State.Settings;
            var nearLossless = settings.IsNearLossless();
            var evaluator = new AtoQualityEvaluator(ctx);

            var atlasedTextures = new HashSet<Texture2D>();
            var islandLookup = BuildIslandLookup(ctx);

            // ---- 1. atlases ----
            if (settings.generateAtlases)
            {
                foreach (var group in ctx.TypeGroups)
                {
                    foreach (var atlas in group.Atlases)
                    {
                        ctx.State.ThrowIfCancelled();
                        AtoLog.Info($"[ATO] composing atlas {atlas.Name} ({atlas.Width}x{atlas.Height}, {atlas.Placed.Count} island(s))");
                        var texture = AtoAtlasCompositor.Compose(ctx, atlas, evaluator, nearLossless);

                        foreach (var sourceTexture in atlas.SourceTextures)
                        {
                            if (!ctx.Textures.TryGetValue(sourceTexture, out var record)) continue;
                            record.Result = texture;
                            record.InAtlas = true;
                            atlasedTextures.Add(sourceTexture);
                            ctx.Remapper.Register(sourceTexture, texture);
                        }

                        var before = atlas.SourceTextures.Sum(t => AtoTextureIO.EstimateBytes(t));
                        var after = (long)atlas.Width * atlas.Height * 4;
                        ctx.State.AtlasRecords.Add(new AtoAtlasReportRecord
                        {
                            Name = atlas.Name,
                            Category = atlas.Group.DisplayName,
                            Width = atlas.Width,
                            Height = atlas.Height,
                            IslandCount = atlas.Placed.Count,
                            SourceTextureCount = atlas.SourceTextures.Count,
                            Utilization = atlas.Utilization,
                            SavedPercent = before > 0 ? (1f - (float)after / before) * 100f : 0f,
                        });
                        ctx.State.BytesBefore += before;
                        ctx.State.BytesAfter += after;
                    }
                }
            }

            // ---- 2. whole-texture fallback path ----
            foreach (var record in ctx.Textures.Values.ToList())
            {
                ctx.State.ThrowIfCancelled();

                if (record.Whitelisted)
                {
                    record.Result = record.Texture; // untouched. / 不动。
                    continue;
                }
                if (record.InAtlas) continue;

                // s_tex = min over the texture's islands (wooden barrel, whole texture). /
                // s_tex = 该贴图全部岛取最小（木桶效应，整图）。
                var scale = Vector2.one;
                var hasScale = false;
                if (islandLookup.TryGetValue(record.Texture, out var islands))
                {
                    foreach (var island in islands)
                    {
                        if (island.PerTextureScale.TryGetValue(record.Texture, out var s))
                        {
                            scale.x = Mathf.Min(scale.x, s.x);
                            scale.y = Mathf.Min(scale.y, s.y);
                            hasScale = true;
                        }
                    }
                }

                if (!hasScale || nearLossless || (scale.x >= 1f && scale.y >= 1f))
                {
                    // No resize needed; the import stage applies the requested import parameters. /
                    // 无需缩放；导入阶段应用请求的导入参数。
                    record.Result = record.Texture;
                    continue;
                }

                var dstW = Mathf.Max(1, Mathf.RoundToInt(record.Texture.width * scale.x));
                var dstH = Mathf.Max(1, Mathf.RoundToInt(record.Texture.height * scale.y));
                AtoLog.Info($"[ATO] whole-texture scale {record.Texture.name}: {record.Texture.width}x{record.Texture.height} -> {dstW}x{dstH}");

                var importSettings = AtoTextureIO.GetImportSettings(record.Texture);
                var isNormal = record.Slots.Any(s => s.Usage.Kind == AtoTextureKind.Normal);
                var pixels = AtoWholeTextureScaler.Resize(record.Texture, importSettings.SrgbTexture, isNormal, dstW, dstH);

                var newTexture = new Texture2D(dstW, dstH, TextureFormat.RGBA32, true, false)
                {
                    name = "ATO_" + record.Texture.name,
                };
                newTexture.SetPixels32(pixels);
                newTexture.Apply(false, false);

                var imported = AtoAssetIO.SaveTexturePng(ctx, newTexture, newTexture.name);
                UnityEngine.Object.DestroyImmediate(newTexture);
                if (imported == null) continue;

                var before = AtoTextureIO.EstimateBytes(record.Texture);
                var after = (long)dstW * dstH * 4;
                record.Result = imported;
                record.BytesBefore = before;
                record.BytesAfter = after;
                ctx.Remapper.Register(record.Texture, imported);
                ctx.State.BytesBefore += before;
                ctx.State.BytesAfter += after;
                ctx.State.TextureRecords.Add(new AtoTextureReportRecord
                {
                    Name = record.Texture.name,
                    BytesBefore = before,
                    BytesAfter = after,
                    SavedPercent = before > 0 ? (1f - (float)after / before) * 100f : 0f,
                    Reason = "whole-texture scaling",
                });
            }

            AtoLog.Info($"[ATO] compose: {ctx.State.AtlasRecords.Count} atlas(es), " +
                        $"{ctx.State.TextureRecords.Count} whole-scaled texture(s).");
        }

        /// <summary>
        /// Build texture → its islands lookup (across all UV groups). / 构建 贴图 → 其全部岛 的查找表。
        /// </summary>
        private static Dictionary<Texture2D, List<AtoIsland>> BuildIslandLookup(AtoContext ctx)
        {
            var lookup = new Dictionary<Texture2D, List<AtoIsland>>();
            foreach (var uvGroup in ctx.UvGroups)
            {
                foreach (var slot in uvGroup.Slots)
                {
                    if (slot.Texture == null) continue;
                    if (!lookup.TryGetValue(slot.Texture, out var list))
                    {
                        lookup[slot.Texture] = list = new List<AtoIsland>();
                    }
                    foreach (var island in uvGroup.Islands)
                    {
                        if (!list.Contains(island)) list.Add(island);
                    }
                }
            }
            return lookup;
        }
    }
}
