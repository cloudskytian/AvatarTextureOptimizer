using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using AvatarTextureOptimizer.Burst;

namespace AvatarTextureOptimizer.Editor
{
    /// <summary>
    /// Atlas packing. Islands are packed in UV space ([0,1]² per atlas), so a UV group's
    /// member textures (main/normal/mask) occupy the same UV rect regardless of each atlas's
    /// resolution. Enforced constraints:
    ///  1. A UV group is the atomic unit — all member textures share the same UV rect & rotation.
    ///  2. All islands of one texture must land in the same atlas.
    ///  3. Per-type-group atlases grow naturally; each atlas edge is the smallest candidate
    ///     ≥ its source texture resolution (from the candidate pool).
    /// 图集装箱。岛在 UV 空间（每图集 [0,1]²）装箱，UV 组成员贴图（主色/法线/蒙版）无论各自图集
    /// 分辨率如何都占据相同 UV 矩形。强制约束：
    ///  1. UV 组为原子单位——所有成员贴图共享相同 UV 矩形与旋转。
    ///  2. 同一张贴图的所有岛必须落在同一图集。
    ///  3. 按类型组的图集自然增长；每张图集边长 = 候选池中首个 ≥ 源贴图分辨率的候选。
    /// </summary>
    public static class AtlasPacker
    {
        private const int CellSize = 4; // rasterization granularity in pixels / 光栅化粒度（像素）

        private sealed class PackState
        {
            public int res;
            public int gridW;
            public int gridH;
            public NativeArray<byte> mask;
            public AtlasResult result;

            public void Dispose() { if (mask.IsCreated) mask.Dispose(); }
        }

        private sealed class Stamp
        {
            public TextureEntry tex;
            public PackState state;
            public NativeArray<byte> mask;
            public int iw, ih, gx, gy, rot;
        }

        /// <summary>Generate candidate atlas edge sizes (POT or NPOT). / 生成候选图集边长（POT 或 NPOT）。</summary>
        public static List<int> CandidateEdges(bool npot, int maxSize, int minEdge = 64)
        {
            var result = new List<int>();
            if (!npot)
                for (int e = minEdge; e <= maxSize; e *= 2) result.Add(e);
            else
                for (int e = minEdge; e <= maxSize; e += 64) result.Add(e);
            result.Sort();
            return result;
        }

        /// <summary>Type-group key for a texture. / 贴图的类型组键。</summary>
        public static string TypeKey(TextureEntry t) =>
            $"{(int)t.category}|{t.specialFlags}|{(t.isLinear ? 1 : 0)}|{(int)t.filterMode}";

        /// <summary>
        /// Pack all UV groups into atlases. Groups that cannot fit fall back to direct scaling.
        /// 将所有 UV 组装箱为图集。装不下的组回退直接缩放。
        /// </summary>
        public static List<AtlasResult> Pack(List<UvGroup> groups, ATOPlatformSettings settings,
            out List<UvGroup> fallback)
        {
            fallback = new List<UvGroup>();
            var results = new List<AtlasResult>();
            var edges = CandidateEdges(settings.npotAtlas, settings.maxAtlasSize);

            var texAtlas = new Dictionary<TextureEntry, AtlasResult>();
            var typeAtlases = new Dictionary<string, List<PackState>>();

            var ordered = groups
                .Where(g => g.textures.Count > 0)
                .OrderByDescending(g => g.island.area * g.scale.x * g.scale.y)
                .ToList();

            foreach (var group in ordered)
            {
                if (!PlaceGroup(group, edges, settings, texAtlas, typeAtlases, results))
                {
                    fallback.Add(group);
                    ATOLogger.Warn($"atlas packing failed for UV group '{group.id}'; falling back to direct scaling");
                }
            }

            foreach (var list in typeAtlases.Values) foreach (var s in list) s.Dispose();
            return results;
        }

        private static bool PlaceGroup(UvGroup group, List<int> edges, ATOPlatformSettings settings,
            Dictionary<TextureEntry, AtlasResult> texAtlas,
            Dictionary<string, List<PackState>> typeAtlases,
            List<AtlasResult> results)
        {
            float uvW = group.island.bounds.width * group.scale.x;
            float uvH = group.island.bounds.height * group.scale.y;
            if (uvW <= 0f || uvH <= 0f) return false;

            var members = group.textures.Where(t => t != null && t.texture != null).Distinct().ToList();
            if (members.Count == 0) return false;

            TextureEntry lead = members.FirstOrDefault(t => t.category.IsColor()) ?? members[0];
            // process the lead first so follower placements can reference its UV position
            // 先处理领队，使跟随者能引用其 UV 位置
            if (lead != members[0]) { members.Remove(lead); members.Insert(0, lead); }

            var stamps = new List<Stamp>();
            int leadRes = 0, leadGX = 0, leadGY = 0, leadRot = 0;
            bool leadPlaced = false;
            int pad = Mathf.Max(1, (int)settings.padding / CellSize);

            foreach (var tex in members)
            {
                bool isLead = (tex == lead);
                string key = TypeKey(tex);

                // locked = texture already committed to an atlas (all its islands must share it)
                // locked = 贴图已锁定到某图集（其所有岛必须共享）
                bool locked = texAtlas.TryGetValue(tex, out var existing);
                PackState state;
                if (locked)
                {
                    state = FindState(typeAtlases, existing);
                    if (state == null) { Cleanup(stamps); return false; }
                }
                else
                {
                    state = GetActiveAtlas(typeAtlases, key, tex, edges, results);
                }

                // rasterize island at this atlas resolution / 按该图集分辨率光栅化岛
                int iw = Mathf.Max(1, Mathf.CeilToInt(uvW * state.res));
                int ih = Mathf.Max(1, Mathf.CeilToInt(uvH * state.res));
                int igw = Mathf.Max(1, (iw + CellSize - 1) / CellSize);
                int igh = Mathf.Max(1, (ih + CellSize - 1) / CellSize);
                var mask = Rasterize(group, igw, igh);

                int gx, gy, rot;
                if (isLead)
                {
                    if (!Blf(mask, igw, igh, state.mask, state.gridW, state.gridH, pad, out gx, out gy, out rot))
                    {
                        if (locked) { mask.Dispose(); Cleanup(stamps); return false; }
                        var fresh = CreateAtlas(key, tex, edges, results);
                        typeAtlases[key].Add(fresh);
                        mask.Dispose();
                        igw = Mathf.Max(1, (Mathf.CeilToInt(uvW * fresh.res) + CellSize - 1) / CellSize);
                        igh = Mathf.Max(1, (Mathf.CeilToInt(uvH * fresh.res) + CellSize - 1) / CellSize);
                        mask = Rasterize(group, igw, igh);
                        if (!Blf(mask, igw, igh, fresh.mask, fresh.gridW, fresh.gridH, pad, out gx, out gy, out rot))
                        { mask.Dispose(); Cleanup(stamps); return false; }
                        state = fresh;
                    }
                    leadRes = state.res; leadGX = gx; leadGY = gy; leadRot = rot;
                    leadPlaced = true;
                }
                else
                {
                    if (!leadPlaced) { mask.Dispose(); Cleanup(stamps); return false; }
                    float u = (leadGX * CellSize) / (float)leadRes;
                    float v = (leadGY * CellSize) / (float)leadRes;
                    gx = Mathf.FloorToInt(u * state.res / CellSize);
                    gy = Mathf.FloorToInt(v * state.res / CellSize);
                    rot = leadRot;

                    if (!CanPlaceAt(mask, igw, igh, state.mask, state.gridW, state.gridH, gx, gy, rot, pad))
                    {
                        if (locked) { mask.Dispose(); Cleanup(stamps); return false; }
                        var fresh = CreateAtlas(key, tex, edges, results);
                        typeAtlases[key].Add(fresh);
                        mask.Dispose();
                        igw = Mathf.Max(1, (Mathf.CeilToInt(uvW * fresh.res) + CellSize - 1) / CellSize);
                        igh = Mathf.Max(1, (Mathf.CeilToInt(uvH * fresh.res) + CellSize - 1) / CellSize);
                        mask = Rasterize(group, igw, igh);
                        gx = Mathf.FloorToInt(u * fresh.res / CellSize);
                        gy = Mathf.FloorToInt(v * fresh.res / CellSize);
                        if (!CanPlaceAt(mask, igw, igh, fresh.mask, fresh.gridW, fresh.gridH, gx, gy, rot, pad))
                        { mask.Dispose(); Cleanup(stamps); return false; }
                        state = fresh;
                    }
                }

                stamps.Add(new Stamp { tex = tex, state = state, mask = mask, iw = igw, ih = igh, gx = gx, gy = gy, rot = rot });
            }

            // ---- commit / 提交 ----
            foreach (var s in stamps)
            {
                StampAt(s.state.mask, s.state.gridW, s.state.gridH, s.mask, s.iw, s.ih, s.gx, s.gy, s.rot);
                s.mask.Dispose();

                var placement = new AtlasPlacedIsland
                {
                    island = group.island,
                    rotation = s.rot * 90,
                    source = s.tex,
                    // precise UV rect from the island's unified UV size / 由岛统一 UV 尺寸计算的精确 UV 矩形
                    dstRect = new Rect((s.gx * CellSize) / (float)s.state.res, (s.gy * CellSize) / (float)s.state.res, uvW, uvH),
                };
                group.placements[s.tex] = placement;
                s.state.result.islands.Add(placement);
                if (!s.state.result.sources.Contains(s.tex)) s.state.result.sources.Add(s.tex);
                texAtlas[s.tex] = s.state.result;
            }

            return true;
        }

        private static void Cleanup(List<Stamp> stamps)
        {
            foreach (var s in stamps) s.mask.Dispose();
        }

        private static PackState FindState(Dictionary<string, List<PackState>> typeAtlases, AtlasResult atlas)
        {
            foreach (var list in typeAtlases.Values)
                foreach (var s in list)
                    if (s.result == atlas) return s;
            return null;
        }

        private static PackState GetActiveAtlas(Dictionary<string, List<PackState>> typeAtlases,
            string key, TextureEntry tex, List<int> edges, List<AtlasResult> results)
        {
            if (!typeAtlases.TryGetValue(key, out var list) || list.Count == 0)
            {
                var atlas = CreateAtlas(key, tex, edges, results);
                typeAtlases[key] = new List<PackState> { atlas };
                return atlas;
            }
            return list[list.Count - 1];
        }

        /// <summary>
        /// Create an atlas whose edge is the smallest candidate ≥ the texture resolution,
        /// so a source texture's islands keep their pixel fidelity (scaled by the island scale).
        /// 创建边长 = 首个 ≥ 贴图分辨率的候选的图集，使源贴图的岛保持像素保真（按岛缩放比例缩放）。
        /// </summary>
        private static PackState CreateAtlas(string key, TextureEntry tex, List<int> edges, List<AtlasResult> results)
        {
            int required = tex != null ? Mathf.Max(tex.width, tex.height) : edges[0];
            int res = edges[edges.Count - 1];
            foreach (var e in edges)
                if (e >= required) { res = e; break; }

            var result = new AtlasResult
            {
                name = $"ATO_{Sanitize(key)}",
                width = res, height = res,
                category = tex != null ? tex.category : ATOTextureCategory.OpaqueColor,
                hasAlpha = tex != null && tex.hasAlpha,
            };
            var state = new PackState
            {
                res = res,
                gridW = res / CellSize,
                gridH = res / CellSize,
                mask = new NativeArray<byte>(res / CellSize * res / CellSize, Allocator.Persistent),
                result = result,
            };
            results.Add(result);
            return state;
        }

        private static string Sanitize(string key) => key.Replace('|', '_');

        private static NativeArray<byte> Rasterize(UvGroup group, int igw, int igh)
        {
            var mask = new NativeArray<byte>(igw * igh, Allocator.Persistent);
            var uvs = new NativeArray<Unity.Mathematics.float2>(group.island.normalizedUV.Count, Allocator.TempJob);
            for (int i = 0; i < group.island.normalizedUV.Count; i++)
                uvs[i] = new Unity.Mathematics.float2(group.island.normalizedUV[i].x, group.island.normalizedUV[i].y);

            var job = new RasterizeIslandJob
            {
                uvs = uvs,
                widthPx = igw * CellSize,
                heightPx = igh * CellSize,
                cellSize = CellSize,
                gridW = igw,
                gridH = igh,
                mask = mask,
            };
            job.Schedule().Complete();
            uvs.Dispose();
            return mask;
        }

        private static bool Blf(NativeArray<byte> islandMask, int iw, int ih,
            NativeArray<byte> atlasMask, int aw, int ah, int pad,
            out int gx, out int gy, out int rot)
        {
            var result = new NativeArray<int>(3, Allocator.TempJob);
            var job = new PackIslandJob
            {
                islandMask = islandMask, islandW = iw, islandH = ih,
                atlasMask = atlasMask, atlasW = aw, atlasH = ah, padding = pad,
                result = result,
            };
            job.Schedule().Complete();
            gx = result[0]; gy = result[1]; rot = result[2];
            result.Dispose();
            return gx >= 0;
        }

        private static bool CanPlaceAt(NativeArray<byte> islandMask, int iw, int ih,
            NativeArray<byte> atlasMask, int aw, int ah, int gx, int gy, int rot, int pad)
        {
            int w = (rot % 2 == 0) ? iw : ih;
            int h = (rot % 2 == 0) ? ih : iw;
            if (gx < 0 || gy < 0 || gx + w > aw || gy + h > ah) return false;
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int sx, sy;
                switch (rot)
                {
                    case 1: sx = y; sy = iw - 1 - x; break;
                    case 2: sx = iw - 1 - x; sy = ih - 1 - y; break;
                    case 3: sx = ih - 1 - y; sy = x; break;
                    default: sx = x; sy = y; break;
                }
                if (islandMask[sy * iw + sx] == 0) continue;
                for (int py = -pad; py <= pad; py++)
                for (int px = -pad; px <= pad; px++)
                {
                    int ax = gx + x + px, ay = gy + y + py;
                    if (ax < 0 || ay < 0 || ax >= aw || ay >= ah) return false;
                    if (atlasMask[ay * aw + ax] != 0) return false;
                }
            }
            return true;
        }

        private static void StampAt(NativeArray<byte> atlasMask, int aw, int ah,
            NativeArray<byte> islandMask, int iw, int ih, int gx, int gy, int rot)
        {
            int w = (rot % 2 == 0) ? iw : ih;
            int h = (rot % 2 == 0) ? ih : iw;
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int sx, sy;
                switch (rot)
                {
                    case 1: sx = y; sy = iw - 1 - x; break;
                    case 2: sx = iw - 1 - x; sy = ih - 1 - y; break;
                    case 3: sx = ih - 1 - y; sy = x; break;
                    default: sx = x; sy = y; break;
                }
                if (islandMask[sy * iw + sx] != 0) atlasMask[(gy + y) * aw + (gx + x)] = 1;
            }
        }
    }
}
