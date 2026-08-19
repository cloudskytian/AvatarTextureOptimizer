// ============================================================================
// AvatarTextureOptimizer (net.fosa.avatar-texture-optimizer)
// Packing/AtlasPacker.cs — 图集装箱 / Atlas packing
//
// 需求（装箱步骤）:
//  - 候选图集池: 2^n 边长(默认, min 64, max 8192/4096) 或 NPOT 64 步进(实验)。
//  - 队列: 按光栅化总面积降序；原子单位=单张贴图及其 UV 组；装不下另开队列(复用同类)。
//  - 全扫描 BLF + 面积降序 + 边长降序 + 旋转90°(位掩码转置；法线绝不旋转)。
//  - 岛形状光栅化装箱（非矩形）。
//
// 关键不变量 (Coder1/Coder2 共识):
//  同一 UV 组在不同 family 图集上的矩形（像素）必须一致，且归一化 UV = rect/图集尺寸，
//  因此: 同一 UV 组的所有 family 图集尺寸必须一致；图集尺寸一旦确定不可增长
//  （增长会使先前组的 UV 归一化错位）。→ 采用"队列"模型：放不下则开新队列
//  （所有 family 同步、同尺寸），尺寸从候选池升序取第一个能装下的。
// ============================================================================
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using api = net.fosa.avatar_texture_optimizer.editor.api;

namespace net.fosa.avatar_texture_optimizer.editor
{
    /// <summary>
    /// 装箱结果 / Packing outcome.
    /// </summary>
    public sealed class PackOutcome
    {
        public Dictionary<string, TextureFamily> families = new Dictionary<string, TextureFamily>();
        /// <summary>无法装箱的 UV 组（整图缩放兜底）/ groups that failed to pack (whole-texture fallback)</summary>
        public List<UVGroup> fallbackGroups = new List<UVGroup>();
        /// <summary>装箱岛总数 / total packed islands</summary>
        public int packedIslandCount;
    }

    /// <summary>
    /// 装箱器 / Atlas packer.
    /// </summary>
    public static class AtlasPacker
    {
        private const int MinAtlas = 64;

        public static PackOutcome Pack(AvatarAnalysis analysis, ATOComponent cfg, int maxAtlasSize, bool npot)
        {
            var outcome = new PackOutcome();
            int padPx = PaddingFor(maxAtlasSize, cfg.paddingOption);

            // 1. 贴图类型组 / texture families
            foreach (var group in analysis.allGroups)
            {
                if (group.whitelisted) continue;
                foreach (var tref in group.textures)
                {
                    if (tref.whitelisted) continue;
                    var key = FamilyKey(tref, group);
                    if (!outcome.families.TryGetValue(key, out var family))
                    {
                        family = new TextureFamily
                        {
                            key = key,
                            role = tref.role,
                            category = tref.category,
                            sRGB = tref.sRGB,
                            filterMode = tref.filterMode,
                        };
                        outcome.families[key] = family;
                    }
                    family.groups.Add(group);
                    if (tref.source != null) family.sources.Add(tref.source);
                    group.families[key] = family;
                }
            }

            // 2. 组级准备: 位掩码、排序 / per-group prep
            foreach (var group in analysis.allGroups)
            {
                if (group.whitelisted || group.families.Count == 0) continue;

                bool hasNormal = group.families.Values.Any(f => f.role == TextureRole.Normal);
                var meshUvs = new List<Vector2>();
                group.mesh.GetUVs(group.uvChannel, meshUvs);

                foreach (var island in group.islands)
                {
                    island.noRotation = hasNormal;
                    island.shapeMask = null;
                    if (island.finalW <= 0 || island.finalH <= 0) continue;
                    var (words, bw, bh) = BitmaskRasterizer.Rasterize(meshUvs, group.mesh.triangles,
                        island.triangles, island.uvMin, island.uvMax, island.finalW, island.finalH);
                    island.shapeMask = words;
                    island.maskBw = bw;
                    island.maskBh = bh;
                    island.rasterArea = BitmaskRasterizer.Area(words);
                }

                group.islands.Sort((a, b) =>
                {
                    int c = b.rasterArea.CompareTo(a.rasterArea);
                    if (c != 0) return c;
                    long la = Mathf.Max(a.finalW, a.finalH);
                    long lb = Mathf.Max(b.finalW, b.finalH);
                    return lb.CompareTo(la);
                });
            }

            // 3. 装箱: 组按总面积降序 / pack: groups by total area desc
            var ordered = analysis.allGroups
                .Where(g => !g.whitelisted && g.families.Count > 0 && g.islands.Count > 0)
                .OrderByDescending(g =>
                {
                    long total = 0;
                    foreach (var i in g.islands) total += i.rasterArea;
                    return total;
                })
                .ToList();

            foreach (var group in ordered)
            {
                Cancel.Checkpoint();

                // 第三方装箱策略否决 → 整组走整图缩放兜底 /
                // third-party atlas strategy veto → whole-texture fallback for the group
                bool vetoed = false;
                foreach (var s in api.ATOPublicAPI.AtlasStrategies)
                {
                    if (!s.CanPack(group)) { vetoed = true; break; }
                }
                if (vetoed)
                {
                    outcome.fallbackGroups.Add(group);
                    group.whitelisted = true;
                    group.whitelistReason = "strategy-veto";
                    continue;
                }

                if (!TryPlaceGroup(group, outcome, maxAtlasSize, npot, padPx))
                {
                    outcome.fallbackGroups.Add(group);
                    group.whitelisted = true;
                    group.whitelistReason = "unpackable";
                    Log.Warning(LogFmt.Warn(LogKeys.UnpackableIsland, group.mesh.name));
                }
            }

            // 4. 利用率与统计 / utilization & stats
            foreach (var family in outcome.families.Values)
            {
                foreach (var atlas in family.atlases)
                {
                    atlas.totalBlocks = (long)atlas.bw * atlas.bh;
                    atlas.utilization = atlas.totalBlocks > 0 ? (float)atlas.usedBlocks / atlas.totalBlocks : 0f;
                    foreach (var kv in atlas.content)
                    {
                        outcome.packedIslandCount += kv.Value.Count;
                    }
                }
            }

            return outcome;
        }

        /// <summary>
        /// 剪除完全空的队列（试探性初始槽可能过小被遗弃）/
        /// Prune completely empty slots (speculative initial slots may be too small).
        /// </summary>
        private static void PruneEmptySlots(List<TextureFamily> familyList)
        {
            foreach (var family in familyList)
            {
                family.atlases.RemoveAll(a => a.islands.Count == 0 && CountSet(a.mask) == 0);
            }
        }

        private static string FamilyKey(TextureRef tref, UVGroup group)
        {
            string flags = "";
            if (tref.role == TextureRole.MainColor)
            {
                flags = (GroupHasRole(group, TextureRole.Normal, tref) ? "N" : "") +
                        (GroupHasRole(group, TextureRole.Mask, tref) ? "M" : "");
            }
            return $"{(int)tref.role}|{(tref.sRGB ? 1 : 0)}|{(int)tref.filterMode}|{flags}";
        }

        private static bool GroupHasRole(UVGroup group, TextureRole role, TextureRef tref)
        {
            foreach (var slot in tref.referencingSlots)
            {
                foreach (var t in slot.textures)
                {
                    if (t.role == role) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 尝试放置整个 UV 组（先试现有同尺寸当前队列；失败则开新队列）/
        /// Try to place the whole UV group (existing uniform current slots first; else new queues).
        /// </summary>
        private static bool TryPlaceGroup(UVGroup group, PackOutcome outcome, int maxAtlasSize, bool npot, int padPx)
        {
            var familyList = group.families.Values.ToList();

            // 确保每个 family 至少有一个队列 / ensure every family has a slot
            foreach (var family in familyList)
            {
                if (family.atlases.Count == 0)
                {
                    family.atlases.Add(NewSlot(family, MinAtlas));
                }
            }

            // 尝试现有当前队列（仅当尺寸全部一致，保证归一化 UV 一致）/
            // try existing current slots (only when sizes are uniform, so normalized UVs stay consistent)
            int curSize = familyList[0].atlases[familyList[0].atlases.Count - 1].width;
            bool uniform = true;
            foreach (var family in familyList)
            {
                if (family.atlases[family.atlases.Count - 1].width != curSize) { uniform = false; break; }
            }
            if (uniform)
            {
                var current = familyList.Select(f => f.atlases[f.atlases.Count - 1]).ToList();
                var rollback = new List<(AtlasResult slot, int word, ulong old)>();
                if (TryPlaceIntoSlots(group, current, padPx, rollback))
                {
                    CommitContent(group, familyList, current, padPx, maxAtlasSize, npot);
                    PruneEmptySlots(familyList);
                    return true;
                }
                foreach (var (slot, word, old) in rollback) slot.mask[word] = old;
            }

            // 开新队列: 候选尺寸升序试第一个能装下的 / open new queues: first candidate size that fits
            int need = 1;
            foreach (var island in group.islands)
            {
                need = System.Math.Max(need, island.finalW);
                need = System.Math.Max(need, island.finalH);
            }
            need += padPx * 2;

            int size = NextCandidateSize(need - 1, maxAtlasSize, npot);
            int guard = 0;
            while (size <= maxAtlasSize && guard++ < 64)
            {
                var newSlots = familyList.Select(f => NewSlot(f, size)).ToList();
                var rollback = new List<(AtlasResult slot, int word, ulong old)>();
                if (TryPlaceIntoSlots(group, newSlots, padPx, rollback))
                {
                    for (int i = 0; i < familyList.Count; i++)
                    {
                        familyList[i].atlases.Add(newSlots[i]);
                    }
                    CommitContent(group, familyList, newSlots, padPx, maxAtlasSize, npot);
                    PruneEmptySlots(familyList);
                    return true;
                }
                size = NextCandidateSize(size, maxAtlasSize, npot);
            }

            return false;
        }

        /// <summary>
        /// 把组内全部岛放置进给定槽集合（镜像：所有槽同位置）/
        /// Place all islands of the group into the given slots (mirrored positions).
        /// </summary>
        private static bool TryPlaceIntoSlots(UVGroup group, List<AtlasResult> slots, int padPx,
            List<(AtlasResult slot, int word, ulong old)> rollback)
        {
            int padB = padPx / BitmaskRasterizer.Granularity;

            foreach (var island in group.islands)
            {
                if (island.shapeMask == null) continue;

                bool placed = false;
                int rotCount = island.noRotation ? 1 : 2;
                for (int rotIdx = 0; rotIdx < rotCount && !placed; rotIdx++)
                {
                    bool rotated = rotIdx == 1;
                    int ibw = island.maskBw, ibh = island.maskBh;
                    if (rotated) { ibw = island.maskBh; ibh = island.maskBw; }

                    ulong[] dilated = Dilate(island.shapeMask, island.maskBw, island.maskBh, padB);
                    if (rotated)
                    {
                        dilated = BitmaskRasterizer.Rotate90(dilated, island.maskBw + 2 * padB, island.maskBh + 2 * padB);
                    }

                    int xMax = int.MaxValue, yMax = int.MaxValue;
                    foreach (var slot in slots)
                    {
                        xMax = System.Math.Min(xMax, slot.bw - ibw - 2 * padB);
                        yMax = System.Math.Min(yMax, slot.bh - ibh - 2 * padB);
                    }
                    if (xMax < 0 || yMax < 0) continue;

                    for (int y = 0; y <= yMax && !placed; y++)
                    {
                        for (int x = 0; x <= xMax && !placed; x++)
                        {
                            if (FitsAll(slots, dilated, ibw, ibh, padB, x, y))
                            {
                                PlaceAll(island, slots, dilated, ibw, ibh, padB, x, y, rotated, padPx, rollback);
                                placed = true;
                            }
                        }
                    }
                }
                if (!placed) return false;
            }
            return true;
        }

        /// <summary>
        /// 内容分配: (group,family) 第一个贴图进布局队列，其余开同尺寸新队列共享 rect /
        /// Content assignment: first texture per (group,family) into the layout queue;
        /// extra textures get new same-size queues sharing the rects.
        /// </summary>
        private static void CommitContent(UVGroup group, List<TextureFamily> familyList, List<AtlasResult> layoutSlots,
            int padPx, int maxAtlasSize, bool npot)
        {
            for (int fi = 0; fi < familyList.Count; fi++)
            {
                var family = familyList[fi];
                var layoutQueue = layoutSlots[fi];

                var texes = group.textures
                    .Where(t => !t.whitelisted && t.source != null && FamilyKey(t, group) == family.key)
                    .ToList();
                if (texes.Count == 0) continue;

                for (int ti = 0; ti < texes.Count; ti++)
                {
                    var tref = texes[ti];
                    if (ti == 0)
                    {
                        layoutQueue.content[tref] = new List<Island>(group.islands);
                        layoutQueue.sources.Add(tref.source);
                    }
                    else
                    {
                        // 其余贴图: 同尺寸新队列, 复制 rect / extra textures: same-size new queue with copied rects
                        var q = NewSlot(family, layoutQueue.width);
                        family.atlases.Add(q);
                        q.content[tref] = new List<Island>(group.islands);
                        q.sources.Add(tref.source);
                        foreach (var island in group.islands)
                        {
                            ReserveRect(q, island);
                            q.islands.Add(island);
                        }
                        q.usedBlocks += CountSet(q.mask);
                    }
                }
            }
        }

        /// <summary>把岛的 rect 位写入队列掩码（内容复制用） / write island rect bits into a queue mask</summary>
        private static void ReserveRect(AtlasResult q, Island island)
        {
            int x0 = island.atlasX / BitmaskRasterizer.Granularity;
            int y0 = island.atlasY / BitmaskRasterizer.Granularity;
            int w = island.rotated ? island.finalH : island.finalW;
            int h = island.rotated ? island.finalW : island.finalH;
            int bw = (w + BitmaskRasterizer.Granularity - 1) / BitmaskRasterizer.Granularity;
            int bh = (h + BitmaskRasterizer.Granularity - 1) / BitmaskRasterizer.Granularity;
            for (int by = 0; by < bh; by++)
            {
                for (int bx = 0; bx < bw; bx++)
                {
                    int idx = (y0 + by) * q.bw + (x0 + bx);
                    q.mask[idx >> 6] |= 1UL << (idx & 63);
                }
            }
        }

        private static bool FitsAll(List<AtlasResult> slots, ulong[] islandMask, int ibw, int ibh, int padB, int x, int y)
        {
            foreach (var slot in slots)
            {
                if (!Fits(slot, islandMask, ibw, ibh, padB, x, y)) return false;
            }
            return true;
        }

        private static bool Fits(AtlasResult slot, ulong[] islandMask, int ibw, int ibh, int padB, int x, int y)
        {
            int dbw = ibw + 2 * padB, dbh = ibh + 2 * padB;
            if (x + dbw > slot.bw || y + dbh > slot.bh) return false;
            for (int by = 0; by < dbh; by++)
            {
                for (int bx = 0; bx < dbw; bx++)
                {
                    if (GetBit(islandMask, bx, by, dbw) && GetBit(slot.mask, x + bx, y + by, slot.bw))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private static void PlaceAll(Island island, List<AtlasResult> slots, ulong[] mask, int ibw, int ibh,
            int padB, int x, int y, bool rotated, int padPx,
            List<(AtlasResult slot, int word, ulong old)> rollback)
        {
            foreach (var slot in slots)
            {
                PlaceInto(slot, mask, ibw + 2 * padB, ibh + 2 * padB, x, y, rollback);
                slot.usedBlocks += CountSet(mask);
                slot.islands.Add(island);
            }

            island.packed = true;
            island.rotated = rotated;
            if (rotated)
            {
                island.finalRect = new RectInt(x * BitmaskRasterizer.Granularity + padPx,
                    y * BitmaskRasterizer.Granularity + padPx, island.finalH, island.finalW);
            }
            else
            {
                island.finalRect = new RectInt(x * BitmaskRasterizer.Granularity + padPx,
                    y * BitmaskRasterizer.Granularity + padPx, island.finalW, island.finalH);
            }
            island.atlasX = island.finalRect.x;
            island.atlasY = island.finalRect.y;
            island.atlas = slots[0];
        }

        private static void PlaceInto(AtlasResult slot, ulong[] islandMask, int maskW, int maskH, int x, int y,
            List<(AtlasResult slot, int word, ulong old)> rollback)
        {
            for (int by = 0; by < maskH; by++)
            {
                for (int bx = 0; bx < maskW; bx++)
                {
                    if (!GetBit(islandMask, bx, by, maskW)) continue;
                    int idx = (y + by) * slot.bw + (x + bx);
                    int word = idx >> 6;
                    ulong bit = 1UL << (idx & 63);
                    if ((slot.mask[word] & bit) == 0)
                    {
                        rollback.Add((slot, word, slot.mask[word]));
                        slot.mask[word] |= bit;
                    }
                }
            }
        }

        private static ulong[] Dilate(ulong[] words, int bw, int bh, int padB)
        {
            int dbw = bw + 2 * padB, dbh = bh + 2 * padB;
            var d = new ulong[((dbw * dbh) + 63) / 64];
            for (int y = 0; y < bh; y++)
            {
                for (int x = 0; x < bw; x++)
                {
                    if (GetBit(words, x, y, bw))
                    {
                        for (int dy = -padB; dy <= padB; dy++)
                        {
                            for (int dx = -padB; dx <= padB; dx++)
                            {
                                SetBit(d, x + padB + dx, y + padB + dy, dbw);
                            }
                        }
                    }
                }
            }
            return d;
        }

        private static bool GetBit(ulong[] words, int x, int y, int bw)
        {
            int idx = y * bw + x;
            return (words[idx >> 6] & (1UL << (idx & 63))) != 0;
        }

        private static void SetBit(ulong[] words, int x, int y, int bw)
        {
            int idx = y * bw + x;
            words[idx >> 6] |= 1UL << (idx & 63);
        }

        private static long CountSet(ulong[] words)
        {
            long c = 0;
            foreach (var w in words)
            {
                ulong v = w;
                while (v != 0) { c += (long)(v & 1); v >>= 1; }
            }
            return c;
        }

        private static AtlasResult NewSlot(TextureFamily family, int size)
        {
            int bw = size / BitmaskRasterizer.Granularity;
            return new AtlasResult
            {
                width = size,
                height = size,
                bw = bw,
                bh = bw,
                mask = new ulong[((bw * bw) + 63) / 64],
                family = family,
            };
        }

        private static int NextCandidateSize(int current, int maxAtlasSize, bool npot)
        {
            if (npot)
            {
                int size = ((current / 64) + 1) * 64;
                return System.Math.Min(size, maxAtlasSize);
            }
            int s = MinAtlas;
            while (s <= current && s < maxAtlasSize) s *= 2;
            return System.Math.Min(s, maxAtlasSize);
        }

        private static int PaddingFor(int atlasSize, int paddingOption)
        {
            int p = (atlasSize + 127) / 128;
            return System.Math.Max(p, paddingOption);
        }
    }
}
