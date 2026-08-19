using System.Collections.Generic;
using UnityEngine;
using Fosa.AvatarTextureOptimizer.Editor.Islands;
using Fosa.AvatarTextureOptimizer.Editor.Analysis;

namespace Fosa.AvatarTextureOptimizer.Editor.UvGroups
{
    // UV 组构建器：把贴图使用挂接到岛实体、传播白名单/NoAtlas、按贴图种类多重集构建类型组。
    // UV-group builder: attaches texture uses to island entities, propagates whitelist/NoAtlas, builds type groups by kind multiset.
    internal static class UvGroupBuilder
    {
        public static void Build(ATOContext ctx, ATOReport.Stage stage)
        {
            // 1) 挂接使用 → 岛。Attach uses to islands.
            foreach (var slot in ctx.slots)
            {
                ctx.CheckCancelled();
                foreach (var use in slot.uses)
                {
                    if (use.texture == null) continue;
                    List<IslandEntity> list;
                    if (!ctx.entityByKey.TryGetValue(new KeyValuePair<Mesh, int>(slot.mesh, use.uvChannel), out list)) continue;
                    foreach (var e in list)
                    {
                        if (e.submesh != slot.slotIndex) continue;
                        if (e.whitelistedFull)
                        {
                            // 岛级白名单（跨缝等）→ 使用级 Full + 条目级 Full（跳过导入参数优化）。
                            // Island-level whitelist → use-level Full + entry-level Full (skips import optimization too).
                            if (use.whitelistLevel != ATOWhitelistLevel.Full)
                            {
                                use.whitelistLevel = ATOWhitelistLevel.Full;
                                use.whitelistReason = e.whitelistReason;
                            }
                            if (use.texture.whitelistLevel != ATOWhitelistLevel.Full)
                            {
                                use.texture.whitelistLevel = ATOWhitelistLevel.Full;
                                use.texture.whitelistReason = e.whitelistReason;
                            }
                            continue;
                        }
                        e.uses.Add(new IslandUse
                        {
                            texture = use.texture,
                            kind = use.kind,
                            sRGB = use.texture.sRGB,
                            filterMode = use.texture.filterMode,
                            alphaMode = use.alphaMode,
                            cutoff = use.cutoff,
                            animatedSwap = use.fromAnimatedSwap || use.animatedTextureProperty,
                            whitelistLevel = use.whitelistLevel,
                            whitelistReason = use.whitelistReason
                        });
                    }
                }
            }

            // 2) 白名单与 NoAtlas 传播：同 UV 的其他贴图跳过图集化（参与整图缩放与导入参数优化）。
            // Whitelist & NoAtlas propagation: other textures sharing the same UV skip atlasing (still whole-scaled + import-optimized).
            foreach (var e in ctx.islandEntities)
            {
                bool hasFull = false, hasNoAtlas = false;
                string reason = null;
                foreach (var u in e.uses)
                {
                    if (u.whitelistLevel == ATOWhitelistLevel.Full) { hasFull = true; reason = u.whitelistReason; }
                    else if (u.whitelistLevel == ATOWhitelistLevel.NoAtlas) hasNoAtlas = true;
                }
                foreach (var u in e.uses)
                {
                    if (hasFull && u.whitelistLevel != ATOWhitelistLevel.Full)
                    {
                        u.whitelistLevel = ATOWhitelistLevel.Full;
                        u.whitelistReason = reason;
                        if (u.texture != null && u.texture.whitelistLevel != ATOWhitelistLevel.Full)
                        {
                            u.texture.whitelistLevel = ATOWhitelistLevel.Full;
                            u.texture.whitelistReason = reason;
                        }
                    }
                    else if (!hasFull && hasNoAtlas && u.whitelistLevel == ATOWhitelistLevel.None)
                    {
                        u.whitelistLevel = ATOWhitelistLevel.NoAtlas;
                        u.whitelistReason = "warn.noAtlas.sameUV";
                    }
                }

                // 岛像素尺寸（按最大引用贴图分辨率）。Island pixel size (per the largest referencing texture).
                int pw = 0, ph = 0;
                foreach (var u in e.uses)
                {
                    var t = u.texture;
                    if (t == null) continue;
                    int w = Mathf.Max(1, Mathf.CeilToInt((e.uvMax.x - e.uvMin.x) * t.width));
                    int h = Mathf.Max(1, Mathf.CeilToInt((e.uvMax.y - e.uvMin.y) * t.height));
                    if (w * h > pw * ph) { pw = w; ph = h; }
                }
                e.pixelWidth = pw;
                e.pixelHeight = ph;
            }

            // 3) 构建类型组：岛的贴图种类多重集 + sRGB + filterMode 完全一致 → 同组。
            // Build type groups: identical kind multiset + sRGB + filterMode → same group.
            var groups = new Dictionary<string, TypeGroup>();
            int groupId = 0;
            foreach (var e in ctx.islandEntities)
            {
                ctx.CheckCancelled();
                if (e.whitelistedFull) continue;
                if (e.uses.Count == 0) continue;
                if (AllUsesWhitelisted(e))
                {
                    e.noAtlasFallback = true;
                    e.fallbackReason = "warn.noAtlas.whitelisted";
                    continue;
                }
                // NoAtlas 传播：任一使用为 NoAtlas → 全岛不图集化（同 UV 贴图跳过图集化）。
                // NoAtlas propagation: any NoAtlas use → the whole island skips atlasing.
                bool noAtlas = false;
                foreach (var u in e.uses)
                {
                    if (u.whitelistLevel == ATOWhitelistLevel.NoAtlas) { noAtlas = true; break; }
                }
                if (noAtlas)
                {
                    e.noAtlasFallback = true;
                    e.fallbackReason = "warn.noAtlas.sameUV";
                    continue;
                }
                // 尺寸一致性：同岛全部贴图必须分辨率相同（共享 UV 映射的数学前提：
                // UV' = rectMin + (uv-uvMin)·scale·texSize/atlasSize 要求 texSize 一致，否则 UV 冲突）。
                // Dimension consistency: all island textures must share identical resolution (shared-UV math requires
                // equal texSize in UV' = rectMin + (uv-uvMin)·scale·texSize/atlasSize; otherwise UVs conflict).
                bool dimsOk = true;
                int cw = -1, ch = -1;
                foreach (var u in e.uses)
                {
                    if (u.whitelistLevel == ATOWhitelistLevel.Full) continue;
                    if (cw < 0) { cw = u.texture.width; ch = u.texture.height; continue; }
                    if (u.texture.width != cw || u.texture.height != ch) { dimsOk = false; break; }
                }
                if (!dimsOk)
                {
                    e.noAtlasFallback = true;
                    e.fallbackReason = "warn.noAtlas.mixedResolutions";
                    stage.AddLine(string.Format(ATOLocalization.Tr("warn.noAtlas.mixedResolutions"), e.ToString()));
                    continue;
                }

                string key = BuildTypeKey(e, out var kinds);
                TypeGroup group;
                if (!groups.TryGetValue(key, out group))
                {
                    group = new TypeGroup { id = groupId++ };
                    group.kinds.AddRange(kinds);
                    groups[key] = group;
                }
                e.typeGroupId = group.id;
                group.islands.Add(e);
            }

            ctx.typeGroups.Clear();
            foreach (var kv in groups) ctx.typeGroups.Add(kv.Value);
            ctx.typeGroups.Sort((a, b) => a.id.CompareTo(b.id));

            stage.AddLine(string.Format(ATOLocalization.Tr("log.uvGroupSummary"), ctx.islandEntities.Count, ctx.typeGroups.Count));
        }

        private static bool AllUsesWhitelisted(IslandEntity e)
        {
            foreach (var u in e.uses)
            {
                if (u.whitelistLevel != ATOWhitelistLevel.Full) return false;
            }
            return true;
        }

        // 类型组键：每种贴图的（类别, sRGB, filterMode）排序串。Type-group key: sorted per-texture (kind, sRGB, filterMode) string.
        private static string BuildTypeKey(IslandEntity e, out List<AtlasKind> kinds)
        {
            var parts = new List<string>();
            var kindSet = new HashSet<AtlasKind>();
            foreach (var u in e.uses)
            {
                if (u.whitelistLevel == ATOWhitelistLevel.Full) continue;
                var kind = ResolveKind(u);
                kindSet.Add(kind);
                parts.Add(string.Format("{0}:{1}:{2}", (int)kind, u.sRGB ? 1 : 0, (int)u.filterMode));
            }
            parts.Sort(string.CompareOrdinal);
            kinds = new List<AtlasKind>(kindSet);
            kinds.Sort((a, b) => a.CompareTo(b));
            return string.Join("|", parts.ToArray());
        }

        // 贴图类别解析：法线 > 含透明 > 灰度/蒙版 > 不透明颜色（取使用中最严苛）。
        // Kind resolution: normal > alpha > grayscale/mask > opaque color (the most demanding use wins).
        public static AtlasKind ResolveKind(IslandUse u)
        {
            if (u.kind == ATOTextureKind.NormalMap) return AtlasKind.Normal;
            if (u.alphaMode == ATOAlphaMode.Cutout || u.alphaMode == ATOAlphaMode.Blend) return AtlasKind.AlphaColor;
            if (u.kind == ATOTextureKind.Grayscale || u.kind == ATOTextureKind.Mask) return AtlasKind.Grayscale;
            return AtlasKind.OpaqueColor;
        }
    }
}
