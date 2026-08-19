using System;
using System.Collections.Generic;
using System.Linq;
using NetFosa.AvatarTextureOptimizer.Editor.Analysis;
using NetFosa.AvatarTextureOptimizer.Editor.Logging;
using NetFosa.AvatarTextureOptimizer.Editor.Quality;
using NetFosa.AvatarTextureOptimizer.Editor.UV;
using UnityEngine;
using NetFosa.AvatarTextureOptimizer;

namespace NetFosa.AvatarTextureOptimizer.Editor.Atlas
{
    /// <summary>图集构建输出。</summary>
    public sealed class AtlasBuildResult
    {
        public readonly List<AtlasResult> Atlases = new List<AtlasResult>();
        /// <summary>整图缩放贴图（含对应缩放系数，按贴图维度统一）。</summary>
        public readonly Dictionary<TextureInfo, float> WholeTextureScales = new Dictionary<TextureInfo, float>();
        public readonly List<TextureInfo> UntouchedWhitelist = new List<TextureInfo>();
        public int FallbackCount;
    }

    /// <summary>
    /// 图集构建编排：
    /// 1) 为每个岛计算最终 rectUV（组级缩放）
    /// 2) 判定每张贴图的图集化资格（任何组为 noAtlas/失败/白名单 → 整图缩放）
    /// 3) 按类型组装箱（BinPacker，含跨类型组 UV 组 rect 一致性）
    /// 4) 非主色类型组图集尝试缩小（其质量需求整体低于主色时可节省体积）
    /// 5) 装箱后按实际图集分辨率复验质量，必要时收缩 rect 并重装一次
    /// </summary>
    public sealed class AtlasBuilder
    {
        private readonly EffectiveSettings _settings;
        private readonly QualityEvaluator _evaluator;
        private readonly TextureCache _cache;
        private readonly ATOLogger _logger;
        private readonly BuildReport _report;
        private readonly BinPacker _packer;

        public AtlasBuilder(EffectiveSettings settings, QualityEvaluator evaluator, TextureCache cache,
            ATOLogger logger, BuildReport report, CandidatePool pool, bool useBurst)
        {
            _settings = settings;
            _evaluator = evaluator;
            _cache = cache;
            _logger = logger;
            _report = report;
            _packer = new BinPacker(pool, useBurst, logger);
        }

        public AtlasBuildResult Build(List<UvGroup> groups, List<TextureTypeGroup> typeGroups)
        {
            var result = new AtlasBuildResult();

            // 1) 计算每个岛的 rectUV
            foreach (var group in groups)
            {
                if (group.failed || group.islands == null) continue;
                foreach (var island in group.islands)
                {
                    if (island.failed) continue;
                    island.atlasRect = new Rect(0f, 0f,
                        island.scaleU * island.uvBounds.width,
                        island.scaleV * island.uvBounds.height);
                }
            }

            // 2) 贴图资格判定
            var atlasable = new HashSet<TextureInfo>();
            var wholeScale = new Dictionary<TextureInfo, float>();

            foreach (var group in groups)
            {
                if (group.failed) continue;
                foreach (var gt in group.textures)
                {
                    var info = gt.info;
                    if (info == null || info.dedupTarget != null) continue;
                    if (info.EffectiveWhitelistLevel == ATOWhitelistLevel.Full)
                    {
                        result.UntouchedWhitelist.Add(info);
                        continue;
                    }
                    if (!atlasable.Contains(info)) atlasable.Add(info);
                }
            }

            foreach (var info in atlasable.ToList())
            {
                bool canAtlas = _settings.generateAtlases && info.EffectiveWhitelistLevel != ATOWhitelistLevel.NoAtlas;
                if (canAtlas)
                {
                    // 其所有组都必须可图集化
                    foreach (var group in groups)
                    {
                        if (group.failed) continue;
                        if (!group.textures.Any(t => t.info == info)) continue;
                        if (group.noAtlas || group.islands == null || group.islands.Count == 0)
                        {
                            canAtlas = false;
                            break;
                        }
                        if (group.islands.Any(i => i.failed)) { canAtlas = false; break; }
                    }
                }
                if (!canAtlas)
                {
                    atlasable.Remove(info);
                    float s = ComputeWholeTextureScale(info, groups);
                    wholeScale[info] = s;
                    result.WholeTextureScales[info] = s;
                }
            }

            // 3) 按类型组装箱
            if (_settings.generateAtlases)
            {
                var orderedGroups = typeGroups.OrderByDescending(tg => TotalUvArea(tg, atlasable, groups)).ToList();
                var fallbacks = new List<TextureInfo>();
                foreach (var tg in orderedGroups)
                {
                    var islandsByTexture = new Dictionary<TextureInfo, List<(UvIsland, Rect)>>();
                    foreach (var info in tg.textures)
                    {
                        if (!atlasable.Contains(info)) continue;
                        if (info.dedupTarget != null) continue;
                        foreach (var group in groups)
                        {
                            if (group.failed || group.noAtlas || group.islands == null) continue;
                            if (!group.textures.Any(t => t.info == info)) continue;
                            foreach (var island in group.islands)
                            {
                                if (island.failed) continue;
                                if (!islandsByTexture.TryGetValue(info, out var list))
                                {
                                    list = new List<(UvIsland, Rect)>();
                                    islandsByTexture[info] = list;
                                }
                                list.Add((island, island.atlasRect));
                            }
                        }
                    }
                    if (islandsByTexture.Count == 0) continue;

                    var atlases = _packer.PackTypeGroup(tg, islandsByTexture, fallbacks, _settings.minPadding);
                    foreach (var a in atlases)
                    {
                        // 记录岛 → 贴图
                        foreach (var p in a.placements)
                        {
                            foreach (var kv in islandsByTexture)
                            {
                                foreach (var (isl, _) in kv.Value)
                                {
                                    if (isl == p.island)
                                    {
                                        a.islandTextures[p.island] = kv.Key;
                                    }
                                }
                            }
                        }
                        result.Atlases.Add(a);
                    }
                }

                // 装箱失败回退 → 整图缩放
                foreach (var f in fallbacks)
                {
                    if (result.WholeTextureScales.ContainsKey(f)) continue;
                    float s = ComputeWholeTextureScale(f, groups);
                    result.WholeTextureScales[f] = s;
                    result.FallbackCount++;
                    _report.AddWarning($"texture '{f.texture?.name}' fell back to whole-texture scaling (atlas packing failed).");
                }

                // 4) 非主色类型组图集尝试缩小（质量需求整体低于主色 → 缩小图集节省体积）
                TryShrinkNonMainAtlases(result);
            }

            // 5) 按实际图集分辨率复验（图集小于贴图原尺寸时）
            RevalidateAtFinalResolution(result, groups);

            return result;
        }

        private static float TotalUvArea(TextureTypeGroup tg, HashSet<TextureInfo> atlasable, List<UvGroup> groups)
        {
            double area = 0;
            foreach (var info in tg.textures)
            {
                if (!atlasable.Contains(info)) continue;
                foreach (var g in groups)
                {
                    if (g.failed || g.noAtlas || g.islands == null) continue;
                    if (!g.textures.Any(t => t.info == info)) continue;
                    foreach (var i in g.islands)
                    {
                        if (i.failed) continue;
                        area += i.atlasRect.width * i.atlasRect.height;
                    }
                }
            }
            return (float)area;
        }

        /// <summary>
        /// 整图缩放系数：取该贴图全部岛缩放的最大值（S = max(s_i)；每个岛以 S 渲染时
        /// crop = S×原尺寸 ≥ s_i×原尺寸 → 质量不劣于已验证，全部岛均达标）。
        /// </summary>
        private float ComputeWholeTextureScale(TextureInfo info, List<UvGroup> groups)
        {
            float s = 0f;
            foreach (var g in groups)
            {
                if (g.islands == null) continue;
                if (!g.textures.Any(t => t.info == info)) continue;
                foreach (var i in g.islands)
                {
                    if (i.failed) continue;
                    s = Mathf.Max(s, Mathf.Max(i.scaleU, i.scaleV));
                }
            }
            return Mathf.Clamp(s, 0.01f, 1f);
        }

        // ---------------- 图集缩小 ----------------

        private void TryShrinkNonMainAtlases(AtlasBuildResult result)
        {
            var shrinkable = new List<AtlasResult>();
            foreach (var a in result.Atlases)
            {
                if (a.category == ATOTextureCategory.MainOpaque || a.category == ATOTextureCategory.MainTransparent) continue;
                if (a.width <= 64 && a.height <= 64) continue;
                shrinkable.Add(a);
            }

            foreach (var a in shrinkable)
            {
                int nw = Math.Max(64, a.width / 2);
                int nh = Math.Max(64, a.height / 2);
                bool ok = true;
                // 缩小后每个岛的有效缩放系数 = rectUV × 新图集宽 / 原岛像素宽（仅当仍 ≥ 各自阈值时可缩）
                    foreach (var p in a.placements)
                    {
                        var group = p.island.group;
                        foreach (var gt in group.textures)
                        {
                            var info = gt.info;
                            if (info == null || info.texture == null) continue;
                            if (info.EffectiveWhitelistLevel != ATOWhitelistLevel.Normal) continue;
                            // 只检查属于本图集类别的贴图（跨类型组的岛 rect 共享，但各自的图集宽度不同）
                            if (info.category != a.category) continue;

                            // 有效缩放（缩图集后）＝ rectUV×newW / (aabb×texW)
                            float effU = (p.island.atlasRect.width * nw) / (p.island.uvBounds.width * info.texture.width);
                            float effV = (p.island.atlasRect.height * nh) / (p.island.uvBounds.height * info.texture.height);
                            if (effU < 0.75f || effV < 0.75f)
                            {
                                // 太激进则放弃缩小
                                ok = false;
                                break;
                            }
                        }
                        if (!ok) break;
                    }
                if (ok)
                {
                    a.width = nw;
                    a.height = nh;
                    _logger.Info($"Atlas '{a.sources[0]}...' shrunk to {nw}x{nh} (non-main type group).");
                }
            }
        }

        // ---------------- 复验 ----------------

        private void RevalidateAtFinalResolution(AtlasBuildResult result, List<UvGroup> groups)
        {
            if (_settings.quality.IsNearLossless) return;

            var failedTextures = new HashSet<TextureInfo>();

            foreach (var a in result.Atlases)
            {
                foreach (var p in a.placements)
                {
                    var island = p.island;
                    // 只复验"本图集来源贴图"（该岛的 rectUV 由本图集分辨率决定实际渲染精度）
                    if (!a.islandTextures.TryGetValue(island, out var info)) continue;
                    if (info == null || info.texture == null) continue;
                    if (info.EffectiveWhitelistLevel != ATOWhitelistLevel.Normal) continue;
                    if (failedTextures.Contains(info)) continue;

                    int texW = info.texture.width, texH = info.texture.height;
                    if (a.width >= texW && a.height >= texH) continue; // 图集不小于原贴图 → 无需复验

                    float effU = (island.atlasRect.width * a.width) / (island.uvBounds.width * texW);
                    float effV = (island.atlasRect.height * a.height) / (island.uvBounds.height * texH);
                    if (effU >= island.scaleU - 1e-3f && effV >= island.scaleV - 1e-3f) continue;

                    var gt = island.group.textures.FirstOrDefault(t => t.info == info);
                    if (gt == null) continue;
                    var r = _evaluator.Evaluate(info, gt, island, texW, texH, effU, effV, _settings.quality);
                    if (!r.pass)
                    {
                        _logger.Warn($"[ATO] Island {island.id} of '{info.texture?.name}' fails quality at final atlas resolution ({a.width}x{a.height}); moving texture to whole-texture scaling.");
                        island.failed = true;
                        island.failReason = "quality revalidation at final atlas resolution failed; whole-texture scaling fallback";
                        failedTextures.Add(info);
                    }
                }
            }

            if (failedTextures.Count == 0) return;

            // 失败贴图：从所有图集中移除（避免 UV 引用图集内容错乱），转整图缩放
            foreach (var a in result.Atlases)
            {
                var toRemove = new List<UvIsland>();
                foreach (var kv in a.islandTextures)
                {
                    if (failedTextures.Contains(kv.Value))
                    {
                        toRemove.Add(kv.Key);
                        kv.Key.failed = true;
                        kv.Key.failReason = "texture fell back to whole-texture scaling";
                    }
                }
                foreach (var island in toRemove) a.islandTextures.Remove(island);
            }

            foreach (var info in failedTextures)
            {
                if (result.WholeTextureScales.ContainsKey(info)) continue;
                float s = ComputeWholeTextureScale(info, groups);
                result.WholeTextureScales[info] = s;
                result.FallbackCount++;
            }
        }
    }
}
