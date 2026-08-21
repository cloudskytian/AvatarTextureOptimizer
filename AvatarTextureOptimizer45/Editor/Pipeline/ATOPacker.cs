using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using Unity.Collections;
using Unity.Jobs;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;

namespace net.fosa.ato
{
    /// <summary>
    /// 图集装箱 / Atlas packing (Burst-backed).
    ///
    ///  * 4px 粒度光栅位掩码(Burst 行并行) + 全扫描 BLF + 90° 步进旋转 + padding 膨胀(Burst)
    ///    / 4px-granularity raster bitmasks (Burst row-parallel) + full-scan BLF + 90° rotations + padding dilation (Burst);
    ///  * 按贴图队列装箱: 同一张贴图的所有岛尽量装入同一图集; 装不下则按候选池新建; 单张贴图都装不进最大图集
    ///    则整个UV组回退独立输出 / queue-per-texture packing: all islands of one texture go into one atlas
    ///    whenever possible; otherwise a new atlas is opened; a texture that cannot fit the largest atlas even
    ///    alone falls back to standalone output (whole UV group, cascading);
    ///  * UV 组原子性: 同一UV的所有贴图在各自图集上保持同一归一化矩形 / UV-group atomicity: all textures
    ///    of one UV keep the same normalized rect across their atlases;
    ///  * 图集整体收缩: 各图集可整体缩到其最严苛岛的个体质量下限(共享缩放留下的余量), 不再重新评估
    ///    / atlas shrink: each atlas is scaled down to the tightest individual quality bound among its islands
    ///    (the headroom left by UV-group sharing), without re-evaluating;
    ///  * 空白填充: GPU pull-push(JFA), 不可用时回退 CPU 多源BFS; 透明图集 alpha 保持 0
    ///    / gap fill: GPU pull-push (JFA) with CPU multi-source BFS fallback; transparent atlases keep alpha 0.
    /// </summary>
    internal static class ATOPacker
    {
        private static ATOConfig _cfg;

        // 图集运行时(Burst 数组) / atlas runtime (Burst arrays)
        private sealed class AtlasRuntime
        {
            public ATOAtlas atlas;
            public int cells;
            public int wWords;
            public NativeArray<int> occ;
            public NativeArray<int> profile;

            public AtlasRuntime(ATOAtlas atlas)
            {
                this.atlas = atlas;
                cells = atlas.width / 4;
                wWords = (cells * cells + 31) / 32;
                occ = new NativeArray<int>(wWords, Allocator.TempJob);
                profile = new NativeArray<int>(cells, Allocator.TempJob);
                // occ/profile 由 Dispose() 负责; 不加入 _allocated(避免双重释放) / owned by Dispose(); not in _allocated (avoids double disposal)
            }

            public void Dispose()
            {
                occ.Dispose();
                profile.Dispose();
            }
        }

        private sealed class GroupPlanner
        {
            public readonly ATOTypeGroup group;
            public readonly List<AtlasRuntime> grids = new List<AtlasRuntime>();

            public GroupPlanner(ATOTypeGroup g)
            {
                group = g;
            }
        }

        private sealed class IslandMaskData
        {
            public NativeArray<int> words; // 未膨胀掩码字 / undilated mask words
            public int mw, mh, wWords;
        }

        private static readonly Dictionary<ATOTypeGroup, GroupPlanner> Planners = new Dictionary<ATOTypeGroup, GroupPlanner>();
        private static readonly Dictionary<(ATOIsland, ATOTextureInfo), IslandMaskData> MaskCache = new Dictionary<(ATOIsland, ATOTextureInfo), IslandMaskData>();
        private static readonly List<NativeArray<int>> _allocated = new List<NativeArray<int>>();
        private static readonly Dictionary<(ATOMeshInfo, int), (NativeArray<float> x, NativeArray<float> y)> UvBuffers =
            new Dictionary<(ATOMeshInfo, int), (NativeArray<float> x, NativeArray<float> y)>();

        public static void Run(ATOBuildState state, BuildContext ctx)
        {
            Profiler.BeginSample("ATO.Pack");
            var timer = new ATOLog.StageTimer();
            timer.Start();
            _cfg = state.config;
            Planners.Clear();
            MaskCache.Clear();
            _allocated.Clear();
            UvBuffers.Clear();

            timer.BeginStep("typeGroups");
            BuildTypeGroups(state);
            timer.EndStep();

            timer.BeginStep("plan");
            Plan(state);
            timer.EndStep();

            timer.BeginStep("shrinkAtlas");
            ShrinkAtlases(state);
            timer.EndStep();

            timer.BeginStep("buildAtlasTextures");
            BuildAtlasTextures(state, ctx);
            timer.EndStep();

            // 释放全部原生缓冲 / dispose all native buffers
            foreach (var p in Planners.Values)
            {
                foreach (var g in p.grids) g.Dispose();
            }

            foreach (var m in MaskCache.Values) m.words.Dispose();
            foreach (var uv in UvBuffers.Values)
            {
                uv.x.Dispose();
                uv.y.Dispose();
            }

            // 临时缓冲(模拟拷贝/膨胀/旋转) / temporaries (sim copies, dilations, rotations)
            foreach (var a in _allocated) a.Dispose();

            Planners.Clear();
            MaskCache.Clear();
            _allocated.Clear();
            UvBuffers.Clear();

            timer.End("图集装箱 Atlas Packing");
            Profiler.EndSample();
        }

        // ------------------------------------------------------------------
        private static void BuildTypeGroups(ATOBuildState state)
        {
            foreach (var tex in state.textures)
            {
                if (tex.skip == ATOSkip.Full || tex.isStandaloneResult || tex.dedupOf != null) continue;
                string key = $"{(int)tex.category}|{tex.sRGB}|{tex.filterMode}";
                ATOTypeGroup group = state.textures.FirstOrDefault(t => t.group != null && t.group.key == key)?.group;
                if (group == null)
                {
                    group = new ATOTypeGroup
                    {
                        key = key,
                        category = tex.category,
                        sRGB = tex.sRGB,
                        filterMode = tex.filterMode
                    };
                }

                tex.group = group;
                if (!group.textures.Contains(tex)) group.textures.Add(tex);
            }

            ATOLog.InfoVerbose($"类型组 / type groups: {string.Join(", ", state.textures.Where(t => t.group != null).Select(t => t.group.key).Distinct())}");
        }

        // ------------------------------------------------------------------
        private static void Plan(ATOBuildState state)
        {
            // 按类型组: 按贴图排序(总光栅面积降序) / per type group: textures sorted by total raster area desc
            foreach (var kv in state.textures.Where(t => t.group != null).GroupBy(t => t.group))
            {
                var group = kv.Key;
                var textures = kv.OrderByDescending(t => TotalRasterArea(t)).ToList();
                foreach (var tex in textures)
                {
                    if (!HasUnplacedIslands(tex)) continue;
                    if (!PlaceTexture(state, tex))
                    {
                        FallbackStandalone(state, tex);
                    }
                }
            }

            state.atlasCount = Planners.Values.Sum(p => p.grids.Count);
        }

        private static float TotalRasterArea(ATOTextureInfo tex)
        {
            float total = 0;
            foreach (var island in tex.islands)
            {
                if (!island.perTexture.TryGetValue(tex, out var it)) continue;
                total += Mathf.Ceil(it.targetWidth / 4f) * Mathf.Ceil(it.targetHeight / 4f);
            }

            return total;
        }

        private static bool HasUnplacedIslands(ATOTextureInfo tex)
        {
            foreach (var island in tex.islands)
            {
                if (!island.atlasCandidate) continue;
                if (island.perTexture.TryGetValue(tex, out var it) && it.atlas == null) return true;
            }

            return false;
        }

        /// <summary>
        /// 把贴图 T 的全部未摆放岛(+UV组伙伴)装入同一图集(尽量) / Places all of T's unplaced islands
        /// (plus their UV-group partners) into one atlas whenever possible.
        /// </summary>
        private static bool PlaceTexture(ATOBuildState state, ATOTextureInfo tex)
        {
            var unplaced = tex.islands.Where(i => i.atlasCandidate
                                                  && i.perTexture.TryGetValue(tex, out var it) && it.atlas == null)
                                      .OrderByDescending(i => MaxIslandCells(i))
                                      .ToList();
            if (unplaced.Count == 0) return true;

            // T 及UV组伙伴已指派的图集 -> 优先继续使用(保持同贴图同图集)
            // Atlases already assigned to T or its UV-group partners -> preferred (keeps one texture in one atlas)
            var preferred = new List<AtlasRuntime>();
            foreach (var island in tex.islands)
            {
                foreach (var member in island.textures)
                {
                    if (member.group == null) continue;
                    if (!island.perTexture.TryGetValue(member, out var it)) continue;
                    if (it.atlas == null) continue;
                    var rt = FindRuntime(it.atlas);
                    if (rt != null && !preferred.Contains(rt)) preferred.Add(rt);
                }
            }

            var planner = GetPlanner(tex.group);

            for (int rot = 0; rot < 4; rot++)
            {
                // 候选图集: 已指派 > 现有 > 新建 / candidate atlases: preferred > existing > new
                var candidates = new List<AtlasRuntime>();
                foreach (var g in planner.grids)
                {
                    if (preferred.Contains(g) && !candidates.Contains(g)) candidates.Add(g);
                }

                foreach (var g in planner.grids)
                {
                    if (!candidates.Contains(g)) candidates.Add(g);
                }

                foreach (var grid in candidates)
                {
                    var plan = TryPackAll(state, tex, unplaced, grid, rot);
                    if (plan != null)
                    {
                        CommitPlan(plan);
                        return true;
                    }
                }

                // 新建图集 / open a new atlas
                int neededCells = 0;
                foreach (var island in unplaced)
                {
                    if (!island.perTexture.TryGetValue(tex, out var it)) continue;
                    var m = GetMask(island, tex, it);
                    var rotated = RotateWords(m, rot, out int rw, out int rh);
                    int pad = Mathf.Max(_cfg.minPadding, 8); // 预估padding / estimated padding
                    neededCells = Mathf.Max(neededCells, (rw + pad * 2) * (rh + pad * 2));
                }

                int? size = PickCandidate(neededCells);
                if (size == null)
                {
                    // 该旋转方向装不进最大图集 / this rotation cannot fit the largest atlas
                    continue;
                }

                var newGrid = CreateGrid(state, tex.group, size.Value);
                var plan2 = TryPackAll(state, tex, unplaced, newGrid, rot);
                if (plan2 != null)
                {
                    CommitPlan(plan2);
                    return true;
                }
            }

            return false;
        }

        private sealed class PackPlan
        {
            public readonly List<(AtlasRuntime runtime, NativeArray<int> occCopy, NativeArray<int> profileCopy)> grids = new();
            public readonly List<(ATOTextureInfo t, ATOIslandTexture it, ATOIsland island, AtlasRuntime runtime,
                int rot, int cellX, int cellY, Rect normRect)> placements = new();
        }

        private sealed class SimState
        {
            public readonly AtlasRuntime runtime;
            public readonly SimGrid grid;
            public readonly NativeArray<int> occ;
            public readonly NativeArray<int> profile;

            public SimState(AtlasRuntime runtime)
            {
                this.runtime = runtime;
                occ = new NativeArray<int>(runtime.occ.Length, Allocator.TempJob);
                profile = new NativeArray<int>(runtime.profile.Length, Allocator.TempJob);
                occ.CopyFrom(runtime.occ);
                profile.CopyFrom(runtime.profile);
                _allocated.Add(occ);
                _allocated.Add(profile);
                grid = new SimGrid(occ, profile, runtime.cells);
            }
        }

        /// <summary>
        /// 试装: 把 T 的全部未摆放岛装入同一 grid, 并验证UV组伙伴在同归一化矩形处可放置.
        /// 试装阶段不做任何真实修改(全部延迟到 CommitPlan), 失败即丢弃.
        /// Tries packing all of T's unplaced islands into one grid, validating UV-group partners at the same
        /// normalized rects. The trial performs NO real mutations (all deferred to CommitPlan).
        /// </summary>
        private static PackPlan TryPackAll(ATOBuildState state, ATOTextureInfo tex, List<ATOIsland> unplaced,
            AtlasRuntime grid, int rot)
        {
            var plan = new PackPlan();
            var sims = new Dictionary<AtlasRuntime, SimState>();

            SimState GetSim(AtlasRuntime runtime)
            {
                if (!sims.TryGetValue(runtime, out var sim))
                {
                    sim = new SimState(runtime);
                    sims[runtime] = sim;
                    plan.grids.Add((runtime, sim.occ, sim.profile));
                }

                return sim;
            }

            int pad = PaddingFor(grid.cells * 4);
            var mainSim = GetSim(grid);

            // T 的岛 / T's islands
            var islandRects = new Dictionary<ATOIsland, Rect>();
            foreach (var island in unplaced)
            {
                if (!island.perTexture.TryGetValue(tex, out var it)) continue;
                var m = GetMask(island, tex, it);
                var rotated = RotateWords(m, rot, out int rw, out int rh);
                var dilated = Dilate(rotated, rw, rh, pad, out int dw, out int dh);
                var pos = mainSim.grid.BLF(dilated, dw, dh);
                if (pos == null) return null;

                int cellX = pos.Value.x + pad, cellY = pos.Value.y + pad;
                mainSim.grid.Occupy(dilated, dw, dh, pos.Value.x, pos.Value.y);
                var normRect = new Rect(cellX / (float)grid.cells, cellY / (float)grid.cells,
                    rw / (float)grid.cells, rh / (float)grid.cells);
                islandRects[island] = normRect;
                plan.placements.Add((tex, it, island, grid, rot, cellX, cellY, normRect));
            }

            // 伙伴(同组与异组) / partners (same group & other groups)
            foreach (var island in unplaced)
            {
                foreach (var partner in island.textures)
                {
                    if (partner == tex || partner.group == null || partner.isStandaloneResult) continue;
                    if (!island.perTexture.TryGetValue(partner, out var pit)) continue;
                    if (pit.atlas != null) continue;

                    var pm = GetMask(island, partner, pit);
                    var prot = RotateWords(pm, rot, out int prw, out int prh);
                    int ppad;

                    // 同类型组伙伴: 与 T 同一图集同矩形 / same-group partner: same atlas & rect as T
                    if (partner.group == tex.group)
                    {
                        ppad = pad;
                        var pdil = Dilate(prot, prw, prh, ppad, out int pdw, out int pdh);
                        var pSim = mainSim;
                        int pcx = Mathf.RoundToInt(islandRects[island].x * grid.cells);
                        int pcy = Mathf.RoundToInt(islandRects[island].y * grid.cells);
                        if (!pSim.grid.CanFit(pdil, pdw, pdh, pcx - ppad, pcy - ppad)) return null;
                        pSim.grid.Occupy(pdil, pdw, pdh, pcx - ppad, pcy - ppad);
                        plan.placements.Add((partner, pit, island, grid, rot, pcx, pcy,
                            new Rect(pcx / (float)grid.cells, pcy / (float)grid.cells,
                                prw / (float)grid.cells, prh / (float)grid.cells)));
                        continue;
                    }

                    // 伙伴目标图集: 已指派 > 组内现有 > 新建 / partner atlas: assigned > existing > new
                    AtlasRuntime pRuntime = null;
                    foreach (var isl in partner.islands)
                    {
                        if (isl.perTexture.TryGetValue(partner, out var it2) && it2.atlas != null)
                        {
                            pRuntime = FindRuntime(it2.atlas);
                            break;
                        }
                    }

                    if (pRuntime != null)
                    {
                        ppad = PaddingFor(pRuntime.cells * 4);
                        var pdil = Dilate(prot, prw, prh, ppad, out int pdw, out int pdh);
                        var pSim = GetSim(pRuntime);
                        int pcx = Mathf.RoundToInt(islandRects[island].x * pRuntime.cells);
                        int pcy = Mathf.RoundToInt(islandRects[island].y * pRuntime.cells);
                        if (!pSim.grid.CanFit(pdil, pdw, pdh, pcx - ppad, pcy - ppad)) return null;
                        pSim.grid.Occupy(pdil, pdw, pdh, pcx - ppad, pcy - ppad);
                        plan.placements.Add((partner, pit, island, pRuntime, rot, pcx, pcy,
                            new Rect(pcx / (float)pRuntime.cells, pcy / (float)pRuntime.cells,
                                prw / (float)pRuntime.cells, prh / (float)pRuntime.cells)));
                        continue;
                    }

                    // 尝试现有图集 / try existing atlases
                    var planner = GetPlanner(partner.group);
                    bool placed = false;
                    foreach (var g in planner.grids)
                    {
                        ppad = PaddingFor(g.cells * 4);
                        var pdil = Dilate(prot, prw, prh, ppad, out int pdw, out int pdh);
                        var pSim = GetSim(g);
                        int pcx = Mathf.RoundToInt(islandRects[island].x * g.cells);
                        int pcy = Mathf.RoundToInt(islandRects[island].y * g.cells);
                        if (!pSim.grid.CanFit(pdil, pdw, pdh, pcx - ppad, pcy - ppad)) continue;
                        pSim.grid.Occupy(pdil, pdw, pdh, pcx - ppad, pcy - ppad);
                        plan.placements.Add((partner, pit, island, g, rot, pcx, pcy,
                            new Rect(pcx / (float)g.cells, pcy / (float)g.cells,
                                prw / (float)g.cells, prh / (float)g.cells)));
                        placed = true;
                        break;
                    }

                    if (placed) continue;

                    // 新建伙伴图集 / open a new atlas for the partner
                    int padEst = Mathf.Max(_cfg.minPadding, 8);
                    int? size = PickCandidate((prw + padEst * 2) * (prh + padEst * 2));
                    if (size == null) return null;
                    var newGrid = CreateGrid(state, partner.group, size.Value);
                    ppad = PaddingFor(newGrid.cells * 4);
                    var ndil = Dilate(prot, prw, prh, ppad, out int ndw, out int ndh);
                    var nSim = GetSim(newGrid);
                    int ncx = Mathf.RoundToInt(islandRects[island].x * newGrid.cells);
                    int ncy = Mathf.RoundToInt(islandRects[island].y * newGrid.cells);
                    if (!nSim.grid.CanFit(ndil, ndw, ndh, ncx - ppad, ncy - ppad)) return null;
                    nSim.grid.Occupy(ndil, ndw, ndh, ncx - ppad, ncy - ppad);
                    plan.placements.Add((partner, pit, island, newGrid, rot, ncx, ncy,
                        new Rect(ncx / (float)newGrid.cells, ncy / (float)newGrid.cells,
                            prw / (float)newGrid.cells, prh / (float)newGrid.cells)));
                }
            }

            return plan;
        }

        private static AtlasRuntime FindRuntime(ATOAtlas atlas)
        {
            foreach (var p in Planners.Values)
            {
                foreach (var g in p.grids)
                {
                    if (g.atlas == atlas) return g;
                }
            }

            return null;
        }

        /// <summary>提交计划: 写回占用数组并记录摆放 / Commits the plan: writes occupancy back and records placements.</summary>
        private static void CommitPlan(PackPlan plan)
        {
            foreach (var g in plan.grids)
            {
                g.runtime.occ.CopyFrom(g.occCopy);
                g.runtime.profile.CopyFrom(g.profileCopy);
            }

            foreach (var p in plan.placements)
            {
                p.it.atlas = p.runtime.atlas;
                p.it.rotation = p.rot;
                p.it.atlasRect = new Rect(p.cellX * 4, p.cellY * 4, p.normRect.width * p.runtime.cells * 4, p.normRect.height * p.runtime.cells * 4);
                p.runtime.atlas.placements.Add(new ATOPlacement
                {
                    island = p.island,
                    rotation = p.rot,
                    normRect = p.normRect,
                    cellRect = new Rect(p.cellX, p.cellY,
                        Mathf.Ceil(p.normRect.width * p.runtime.cells), Mathf.Ceil(p.normRect.height * p.runtime.cells))
                });
                p.runtime.atlas.sourcePixels += (long)p.it.pixelRect.width * (long)p.it.pixelRect.height;
            }
        }

        private sealed class SimGrid
        {
            private readonly NativeArray<int> _occ;
            private readonly NativeArray<int> _profile;
            private readonly int _cells;
            private readonly int _wWords;

            public SimGrid(NativeArray<int> occ, NativeArray<int> profile, int cells)
            {
                _occ = occ;
                _profile = profile;
                _cells = cells;
                _wWords = (cells * cells + 31) / 32;
            }

            public (int x, int y)? BLF(NativeArray<int> mask, int mw, int mh)
            {
                if (mw > _cells || mh > _cells) return null;
                var result = new NativeArray<int>(2, Allocator.TempJob);
                var job = new ATOBLFJob
                {
                    occ = _occ,
                    profile = _profile,
                    mask = mask,
                    cells = _cells,
                    wWordsAtlas = _wWords,
                    mw = mw,
                    mh = mh,
                    wWordsMask = (mw + 31) / 32,
                    startX = 0,
                    result = result
                };
                job.Run();
                var r = result[0] < 0 ? ((int x, int y)?)null : (result[0], result[1]);
                result.Dispose();
                return r;
            }

            public bool CanFit(NativeArray<int> mask, int mw, int mh, int x, int y)
            {
                var result = new NativeArray<int>(1, Allocator.TempJob);
                var job = new ATOCanFitJob
                {
                    occ = _occ,
                    mask = mask,
                    cells = _cells,
                    wWordsAtlas = _wWords,
                    mw = mw,
                    mh = mh,
                    wWordsMask = (mw + 31) / 32,
                    x = x,
                    y = y,
                    result = result
                };
                job.Run();
                bool ok = result[0] == 1;
                result.Dispose();
                return ok;
            }

            public void Occupy(NativeArray<int> mask, int mw, int mh, int x, int y)
            {
                var job = new ATOOccupyJob
                {
                    occ = _occ,
                    mask = mask,
                    wWordsAtlas = _wWords,
                    mw = mw,
                    mh = mh,
                    wWordsMask = (mw + 31) / 32,
                    x = x,
                    y = y
                };
                job.Run();
                var pJob = new ATOProfileUpdateJob
                {
                    profile = _profile,
                    mask = mask,
                    mw = mw,
                    mh = mh,
                    wWordsMask = (mw + 31) / 32,
                    x = x,
                    y = y
                };
                pJob.Run(mw);
            }
        }

        // ------------------------------------------------------------------
        private static float MaxIslandCells(ATOIsland island)
        {
            float max = 0;
            foreach (var t in island.textures)
            {
                if (t.group == null) continue;
                if (island.perTexture.TryGetValue(t, out var it))
                {
                    max = Mathf.Max(max, Mathf.Ceil(it.targetWidth / 4f) * Mathf.Ceil(it.targetHeight / 4f));
                }
            }

            return max;
        }

        private static GroupPlanner GetPlanner(ATOTypeGroup group)
        {
            if (!Planners.TryGetValue(group, out var p))
            {
                p = new GroupPlanner(group);
                Planners[group] = p;
            }

            return p;
        }

        private static int PaddingFor(int atlasSize)
        {
            return Mathf.Max(_cfg.minPadding, Mathf.CeilToInt(atlasSize / 128f));
        }

        private static AtlasRuntime CreateGrid(ATOBuildState state, ATOTypeGroup group, int size)
        {
            var atlas = new ATOAtlas
            {
                group = group,
                width = size,
                height = size,
                name = $"ATO_{group.key.Replace('|', '_')}_{group.atlases.Count}"
            };
            group.atlases.Add(atlas);
            var grid = new AtlasRuntime(atlas);
            GetPlanner(group).grids.Add(grid);
            ATOLog.InfoVerbose($"新建图集 / new atlas: {atlas.name} {size}x{size} (padding={PaddingFor(size)})");
            return grid;
        }

        private static int? PickCandidate(int neededCells)
        {
            var pool = new List<int>();
            if (_cfg.enableNPOT)
            {
                for (int s = 64; s <= _cfg.maxAtlasSize; s += 64) pool.Add(s);
            }
            else
            {
                for (int s = 64; s <= _cfg.maxAtlasSize; s *= 2) pool.Add(s);
            }

            pool.Sort((a, b) => (a * a).CompareTo(b * b));
            foreach (var s in pool)
            {
                if (s * s >= neededCells) return s;
            }

            return null;
        }

        // ------------------------------------------------------------------
        // 掩码(Burst 构建) / masks (built via Burst)
        // ------------------------------------------------------------------
        private static IslandMaskData GetMask(ATOIsland island, ATOTextureInfo tex, ATOIslandTexture it)
        {
            var key = (island, tex);
            if (MaskCache.TryGetValue(key, out var cached)) return cached;

            int mw = Mathf.Max(1, Mathf.CeilToInt(it.targetWidth / 4f));
            int mh = Mathf.Max(1, Mathf.CeilToInt(it.targetHeight / 4f));
            int wWords = (mw + 31) / 32;
            var words = new NativeArray<int>(mh * wWords, Allocator.TempJob);
            // words 由 MaskCache 清理循环负责; 不加入 _allocated(避免双重释放) / owned by the MaskCache cleanup; not in _allocated

            var mi = island.owner;
            int channel = island.channel;
            if (!UvBuffers.TryGetValue((mi, channel), out var uv))
            {
                var uvList = mi.newUVs[channel];
                var x = new NativeArray<float>(uvList.Count, Allocator.TempJob);
                var y = new NativeArray<float>(uvList.Count, Allocator.TempJob);
                for (int i = 0; i < uvList.Count; i++)
                {
                    x[i] = uvList[i].x;
                    y[i] = uvList[i].y;
                }

                uv = (x, y);
                UvBuffers[(mi, channel)] = uv;
            }

            int[] tris = mi.mesh.triangles;
            var triVerts = new NativeArray<int>(island.triangles.Length * 3, Allocator.TempJob);
            for (int t = 0; t < island.triangles.Length; t++)
            {
                triVerts[t * 3] = tris[island.triangles[t] * 3];
                triVerts[t * 3 + 1] = tris[island.triangles[t] * 3 + 1];
                triVerts[t * 3 + 2] = tris[island.triangles[t] * 3 + 2];
            }

            var job = new ATOBuildMaskJob
            {
                triVerts = triVerts,
                uvX = uv.x,
                uvY = uv.y,
                words = words,
                mw = mw,
                mh = mh,
                wWords = wWords,
                texW = tex.width,
                texH = tex.height,
                rectX = it.pixelRect.x,
                rectY = it.pixelRect.y,
                scaleX = it.scale.x,
                scaleY = it.scale.y
            };
            job.Run(mh);
            triVerts.Dispose();

            var result = new IslandMaskData { words = words, mw = mw, mh = mh, wWords = wWords };
            MaskCache[key] = result;
            return result;
        }

        /// <summary>旋转掩码(经bool转置, 与写入/UV重映射同一旋转定义) / rotates the mask via bool transpose (same rotation convention as write/UV remap).</summary>
        private static NativeArray<int> RotateWords(IslandMaskData m, int rot, out int rw, out int rh)
        {
            var b = WordsToBool(m);
            var rb = RotateBool(b, m.mw, m.mh, rot, out rw, out rh);
            return BoolToWords(rb, rw, rh);
        }

        private static bool[] WordsToBool(IslandMaskData m)
        {
            var b = new bool[m.mw * m.mh];
            for (int r = 0; r < m.mh; r++)
            {
                for (int w = 0; w < m.wWords; w++)
                {
                    int word = m.words[r * m.wWords + w];
                    for (int bit = 0; bit < 32; bit++)
                    {
                        int x = w * 32 + bit;
                        if (x >= m.mw) break;
                        if ((word & (1 << bit)) != 0) b[r * m.mw + x] = true;
                    }
                }
            }

            return b;
        }

        private static bool[] RotateBool(bool[] m, int mw, int mh, int rot, out int rw, out int rh)
        {
            switch (rot & 3)
            {
                case 0:
                    rw = mw;
                    rh = mh;
                    return (bool[])m.Clone();
                case 1:
                    rw = mh;
                    rh = mw;
                    var r1 = new bool[rw * rh];
                    for (int y = 0; y < mh; y++)
                    {
                        for (int x = 0; x < mw; x++)
                        {
                            if (m[y * mw + x]) r1[x * rw + (mw - 1 - y)] = true;
                        }
                    }

                    return r1;
                case 2:
                    rw = mw;
                    rh = mh;
                    var r2 = new bool[rw * rh];
                    for (int y = 0; y < mh; y++)
                    {
                        for (int x = 0; x < mw; x++)
                        {
                            if (m[y * mw + x]) r2[(mh - 1 - y) * mw + (mw - 1 - x)] = true;
                        }
                    }

                    return r2;
                default:
                    rw = mh;
                    rh = mw;
                    var r3 = new bool[rw * rh];
                    for (int y = 0; y < mh; y++)
                    {
                        for (int x = 0; x < mw; x++)
                        {
                            if (m[y * mw + x]) r3[(mh - 1 - x) * rw + y] = true;
                        }
                    }

                    return r3;
            }
        }

        private static NativeArray<int> BoolToWords(bool[] b, int w, int h)
        {
            int wWords = (w + 31) / 32;
            var words = new NativeArray<int>(h * wWords, Allocator.TempJob);
            _allocated.Add(words);
            for (int r = 0; r < h; r++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (b[r * w + x]) words[r * wWords + (x >> 5)] |= 1 << (x & 31);
                }
            }

            return words;
        }

        /// <summary>padding 膨胀(Burst) / padding dilation (Burst).</summary>
        private static NativeArray<int> Dilate(NativeArray<int> mask, int mw, int mh, int pad, out int dw, out int dh)
        {
            dw = mw + pad * 2;
            dh = mh + pad * 2;
            int outWords = (dw + 31) / 32;
            var dst = new NativeArray<int>(dh * outWords, Allocator.TempJob);
            _allocated.Add(dst);
            var job = new ATODilateJob
            {
                src = mask,
                dst = dst,
                mw = mw,
                mh = mh,
                inWords = (mw + 31) / 32,
                pad = pad,
                outWords = outWords,
                outW = dw
            };
            job.Run(dh);
            return dst;
        }

        // ------------------------------------------------------------------
        // 图集收缩 / atlas shrink
        // ------------------------------------------------------------------
        private static void ShrinkAtlases(ATOBuildState state)
        {
            foreach (var p in Planners.Values)
            {
                foreach (var runtime in p.grids)
                {
                    var atlas = runtime.atlas;
                    if (atlas.placements.Count == 0) continue;

                    // g = min over placements of sqrt(indArea/sharedArea) / the tightest individual-quality bound
                    float g = 1f;
                    foreach (var placement in atlas.placements)
                    {
                        foreach (var kv in placement.island.perTexture)
                        {
                            if (kv.Value.atlas != atlas) continue;
                            float sharedArea = kv.Value.scale.x * kv.Value.scale.y;
                            float indArea = kv.Value.individualScale.x * kv.Value.individualScale.y;
                            if (sharedArea <= 1e-9f) continue;
                            float ratio = indArea / sharedArea;
                            g = Mathf.Min(g, Mathf.Sqrt(ratio));
                        }
                    }

                    if (g >= 0.95f) continue;

                    int? newSize = PickCandidateSizeBelow(atlas.width, g);
                    if (newSize == null || newSize.Value >= atlas.width) continue;

                    int oldPx = atlas.width * atlas.height;
                    atlas.width = newSize.Value;
                    atlas.height = newSize.Value;
                    ATOLog.Info($"图集整体收缩 / atlas shrunk: {atlas.name} {oldPx / 1000000f:F2}MP -> {atlas.width * atlas.height / 1000000f:F2}MP (x{g:F2})");
                }
            }
        }

        private static int? PickCandidateSizeBelow(int current, float g)
        {
            int target = Mathf.Max(64, Mathf.FloorToInt(current * g));
            if (_cfg.enableNPOT)
            {
                int s = (target / 64) * 64;
                if (s < 64) s = 64;
                return s < current ? s : (int?)null;
            }
            else
            {
                int s = 64;
                while (s * 2 <= target) s *= 2;
                return s < current ? s : (int?)null;
            }
        }

        // ------------------------------------------------------------------
        private static void FallbackStandalone(ATOBuildState state, ATOTextureInfo start)
        {
            var queue = new Queue<ATOTextureInfo>();
            var seen = new HashSet<ATOTextureInfo>();
            queue.Enqueue(start);

            while (queue.Count > 0)
            {
                var t = queue.Dequeue();
                if (!seen.Add(t)) continue;
                if (t.group == null || t.isStandaloneResult) continue;

                foreach (var island in t.islands)
                {
                    if (island.perTexture.TryGetValue(t, out var it) && it.atlas != null)
                    {
                        it.atlas.placements.RemoveAll(p => p.island == island);
                        it.atlas = null;
                    }

                    foreach (var partner in island.textures)
                    {
                        if (partner.group != null && !partner.isStandaloneResult) queue.Enqueue(partner);
                    }
                }

                t.isStandaloneResult = true;
                t.group = null;
                float minAreaScale = 1f;
                foreach (var island in t.islands)
                {
                    if (island.perTexture.TryGetValue(t, out var it))
                    {
                        minAreaScale = Mathf.Min(minAreaScale, it.scale.x * it.scale.y);
                    }
                }

                t.wholeScale = Mathf.Sqrt(minAreaScale);
                ATOLog.Warn($"贴图整个UV组放弃图集化(无法装入最大图集), 按质量缩放后独立输出 / UV group of '{t.source.name}' fell back to scaled standalone output");
            }
        }

        // ------------------------------------------------------------------
        // 图集构建 / atlas texture building
        // ------------------------------------------------------------------
        private static void BuildAtlasTextures(ATOBuildState state, BuildContext ctx)
        {
            foreach (var planner in Planners.Values)
            {
                foreach (var atlas in planner.group.atlases)
                {
                    if (atlas.placements.Count == 0) continue;
                    BuildAtlasTexture(state, ctx, planner.group, atlas);
                }
            }
        }

        private static void BuildAtlasTexture(ATOBuildState state, BuildContext ctx, ATOTypeGroup group, ATOAtlas atlas)
        {
            int w = atlas.width, h = atlas.height;
            bool anyAlpha = false;

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, !group.sRGB)
            {
                name = atlas.name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = group.filterMode
            };

            var pixels = new Color32[w * h];

            // 摆放记录 -> 最终矩形(normRect × 最终尺寸) / placements -> final rects (normRect × final size)
            foreach (var placement in atlas.placements)
            {
                int rot = placement.rotation;
                int outW = Mathf.Max(1, Mathf.RoundToInt(((rot & 1) == 1 ? placement.normRect.height : placement.normRect.width) * w));
                int outH = Mathf.Max(1, Mathf.RoundToInt(((rot & 1) == 1 ? placement.normRect.width : placement.normRect.height) * h));
                placement.cellRect = new Rect(
                    Mathf.RoundToInt(placement.normRect.x * (w / 4f)),
                    Mathf.RoundToInt(placement.normRect.y * (h / 4f)),
                    Mathf.Ceil(outW / 4f),
                    Mathf.Ceil(outH / 4f));

                ATOTextureInfo source = null;
                ATOIslandTexture it = null;
                foreach (var kv in placement.island.perTexture)
                {
                    if (kv.Value.atlas == atlas) { source = kv.Key; it = kv.Value; break; }
                }

                if (source == null || it == null) continue;
                if (source.hasAlpha) anyAlpha = true;
                WriteIsland(source, it, pixels, w, h, outW, outH);
            }

            // GPU pull-push 外扩(CPU BFS 回退) / GPU pull-push fill (CPU BFS fallback)
            FillGaps(state, pixels, w, h, anyAlpha);

            tex.SetPixels32(pixels);
            tex.Apply(false, false);

            atlas.hasAlpha = anyAlpha;
            atlas.result = tex;
            atlas.outputHash = ATOPackerIO.HashPixels32(pixels);

            foreach (var placement in atlas.placements)
            {
                foreach (var kv in placement.island.perTexture)
                {
                    if (kv.Value.atlas == atlas) kv.Key.result = tex;
                }
            }

            // 利用率 / utilization
            long placed = 0;
            foreach (var p in atlas.placements)
            {
                placed += (long)p.cellRect.width * (long)p.cellRect.height;
            }

            atlas.utilization = (float)placed / Mathf.Max(1, (atlas.width / 4) * (atlas.height / 4));

            // 图集被使用通道 = 全部来源的并集 / atlas used channels = union over sources
            int usedChannels = 0b1111;
            if (group.category == ATOTextureCategory.Grayscale || group.category == ATOTextureCategory.Mask)
            {
                usedChannels = 0;
                foreach (var placement in atlas.placements)
                {
                    foreach (var kv in placement.island.perTexture)
                    {
                        if (kv.Value.atlas == atlas) usedChannels |= kv.Key.usedChannels;
                    }
                }

                if (usedChannels == 0) usedChannels = 0b1111;
            }

            ATOImportConfig.SaveAndConfigure(state, ctx, tex, group.category, group.sRGB, anyAlpha, null, atlas, usedChannels);
            state.totalOutputPixels += (long)atlas.width * atlas.height;

            var outInfo = new ATOTextureInfo
            {
                source = tex,
                result = tex,
                width = w,
                height = h,
                sRGB = group.sRGB,
                filterMode = group.filterMode,
                category = group.category,
                hasAlpha = anyAlpha,
                isStandaloneResult = false,
                outputHash = atlas.outputHash
            };
            state.outputTextures.Add(outInfo);
        }

        private static void WriteIsland(ATOTextureInfo source, ATOIslandTexture it, Color32[] pixels, int aw, int ah, int outW, int outH)
        {
            var crop = ATOTextureIO.ReadRect(source, it.pixelRect);
            if (crop == null) return;
            int cw = Mathf.Clamp(Mathf.CeilToInt(it.pixelRect.width), 1, Mathf.Max(1, source.width));
            int ch = Mathf.Clamp(Mathf.CeilToInt(it.pixelRect.height), 1, Mathf.Max(1, source.height));
            if (crop.Length != cw * ch) return;

            int ox = Mathf.RoundToInt(it.atlasRect.x);
            int oy = Mathf.RoundToInt(it.atlasRect.y);
            int rot = it.rotation;
            bool premul = source.hasAlpha;

            for (int y = 0; y < outH; y++)
            {
                for (int x = 0; x < outW; x++)
                {
                    float su, sv;
                    switch (rot & 3)
                    {
                        case 1:
                            su = (y + 0.5f) * cw / outH;
                            sv = (outW - 1 - x + 0.5f) * ch / outW;
                            break;
                        case 2:
                            su = (outW - 1 - x + 0.5f) * cw / outW;
                            sv = (outH - 1 - y + 0.5f) * ch / outH;
                            break;
                        case 3:
                            su = (outH - 1 - y + 0.5f) * cw / outH;
                            sv = (x + 0.5f) * ch / outW;
                            break;
                        default:
                            su = (x + 0.5f) * cw / outW;
                            sv = (y + 0.5f) * ch / outH;
                            break;
                    }

                    int sx0 = Mathf.Clamp(Mathf.FloorToInt(su - 0.5f), 0, cw - 1);
                    int sy0 = Mathf.Clamp(Mathf.FloorToInt(sv - 0.5f), 0, ch - 1);
                    int sx1 = Mathf.Clamp(sx0 + 1, 0, cw - 1);
                    int sy1 = Mathf.Clamp(sy0 + 1, 0, ch - 1);
                    float tx = Mathf.Clamp01(su - 0.5f - sx0);
                    float ty = Mathf.Clamp01(sv - 0.5f - sy0);

                    var c00 = crop[sy0 * cw + sx0];
                    var c10 = crop[sy0 * cw + sx1];
                    var c01 = crop[sy1 * cw + sx0];
                    var c11 = crop[sy1 * cw + sx1];

                    float a = Lerp4(c00.a, c10.a, c01.a, c11.a, tx, ty);
                    byte outA = (byte)Mathf.RoundToInt(a);
                    if (outA == 0) continue;

                    byte r, g, b;
                    if (premul)
                    {
                        float rP = Lerp4(c00.r * c00.a, c10.r * c10.a, c01.r * c01.a, c11.r * c11.a, tx, ty) / 255f;
                        float gP = Lerp4(c00.g * c00.a, c10.g * c10.a, c01.g * c01.a, c11.g * c11.a, tx, ty) / 255f;
                        float bP = Lerp4(c00.b * c00.a, c10.b * c10.a, c01.b * c01.a, c11.b * c11.a, tx, ty) / 255f;
                        r = (byte)Mathf.Clamp(Mathf.RoundToInt(rP * 255f / Mathf.Max(a, 1f)), 0, 255);
                        g = (byte)Mathf.Clamp(Mathf.RoundToInt(gP * 255f / Mathf.Max(a, 1f)), 0, 255);
                        b = (byte)Mathf.Clamp(Mathf.RoundToInt(bP * 255f / Mathf.Max(a, 1f)), 0, 255);
                    }
                    else
                    {
                        r = (byte)Mathf.RoundToInt(Lerp4(c00.r, c10.r, c01.r, c11.r, tx, ty));
                        g = (byte)Mathf.RoundToInt(Lerp4(c00.g, c10.g, c01.g, c11.g, tx, ty));
                        b = (byte)Mathf.RoundToInt(Lerp4(c00.b, c10.b, c01.b, c11.b, tx, ty));
                    }

                    int px = ox + x, py = oy + y;
                    if (px >= 0 && py >= 0 && px < aw && py < ah)
                    {
                        pixels[py * aw + px] = new Color32(r, g, b, outA);
                    }
                }
            }
        }

        private static float Lerp4(float a, float b, float c, float d, float tx, float ty)
        {
            return (a * (1 - tx) + b * tx) * (1 - ty) + (c * (1 - tx) + d * tx) * ty;
        }

        /// <summary>GPU pull-push(优先) / CPU 多源BFS(回退) / GPU pull-push (preferred), CPU multi-source BFS (fallback).</summary>
        private static void FillGaps(ATOBuildState state, Color32[] pixels, int w, int h, bool transparent)
        {
            if (ATOGpu.PullPushAvailable)
            {
                try
                {
                    var desc = new RenderTextureDescriptor(w, h, RenderTextureFormat.ARGB32, 0);
                    desc.enableRandomWrite = true;
                    var rt = RenderTexture.GetTemporary(desc);
                    var prev = RenderTexture.active;
                    var cpu = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
                    cpu.SetPixels32(pixels);
                    cpu.Apply(false, false);
                    Graphics.Blit(cpu, rt);
                    UnityEngine.Object.DestroyImmediate(cpu);
                    if (ATOGpu.PullPushFill(rt, transparent))
                    {
                        RenderTexture.active = rt;
                        var back = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
                        back.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                        back.Apply(false, false);
                        var filled = back.GetPixels32();
                        Array.Copy(filled, pixels, filled.Length);
                        UnityEngine.Object.DestroyImmediate(back);
                        RenderTexture.active = prev;
                        RenderTexture.ReleaseTemporary(rt);
                        return;
                    }

                    RenderTexture.active = prev;
                    RenderTexture.ReleaseTemporary(rt);
                }
                catch (Exception e)
                {
                    ATOLog.Warn($"GPU pull-push 失败, 回退CPU / GPU pull-push failed, falling back to CPU: {e.Message}");
                }
            }

            CpuBfsFill(pixels, w, h, transparent);
        }

        /// <summary>CPU 多源BFS 等价实现 / CPU multi-source BFS equivalent.</summary>
        private static void CpuBfsFill(Color32[] pixels, int w, int h, bool transparent)
        {
            int cw = w / 4, ch = h / 4;
            var filled = new bool[cw * ch];
            var visited = new bool[cw * ch];
            var cellColor = new Color32[cw * ch];
            var queue = new Queue<int>();

            for (int cy = 0; cy < ch; cy++)
            {
                for (int cx = 0; cx < cw; cx++)
                {
                    bool any = false;
                    for (int y = cy * 4; y < cy * 4 + 4 && y < h; y++)
                    {
                        for (int x = cx * 4; x < cx * 4 + 4 && x < w; x++)
                        {
                            var c = pixels[y * w + x];
                            if (c.a != 0 || c.r != 0 || c.g != 0 || c.b != 0)
                            {
                                any = true;
                                break;
                            }
                        }

                        if (any) break;
                    }

                    int idx = cy * cw + cx;
                    if (any)
                    {
                        filled[idx] = true;
                        visited[idx] = true;
                        cellColor[idx] = pixels[(cy * 4 + 2) * w + cx * 4 + 2];
                        queue.Enqueue(idx);
                    }
                }
            }

            while (queue.Count > 0)
            {
                int idx = queue.Dequeue();
                int cx = idx % cw, cy = idx / cw;
                var c = cellColor[idx];
                var neighbors = new[] { (1, 0), (-1, 0), (0, 1), (0, -1) };
                foreach (var (dx, dy) in neighbors)
                {
                    int nx = cx + dx, ny = cy + dy;
                    if (nx < 0 || ny < 0 || nx >= cw || ny >= ch) continue;
                    int nidx = ny * cw + nx;
                    if (!visited[nidx] && !filled[nidx])
                    {
                        visited[nidx] = true;
                        cellColor[nidx] = c;
                        queue.Enqueue(nidx);
                    }
                }
            }

            for (int cy = 0; cy < ch; cy++)
            {
                for (int cx = 0; cx < cw; cx++)
                {
                    int idx = cy * cw + cx;
                    if (filled[idx]) continue;
                    var c = cellColor[idx];
                    if (c.a == 0 && c.r == 0 && c.g == 0 && c.b == 0) continue;
                    if (transparent) c.a = 0;
                    for (int y = cy * 4; y < cy * 4 + 4 && y < h; y++)
                    {
                        for (int x = cx * 4; x < cx * 4 + 4 && x < w; x++)
                        {
                            pixels[y * w + x] = c;
                        }
                    }
                }
            }
        }
    }

    /// <summary>像素哈希(最终去重用) / Pixel hashing (final dedup).</summary>
    internal static class ATOPackerIO
    {
        public static string HashPixels32(Color32[] pixels)
        {
            using var md5 = System.Security.Cryptography.MD5.Create();
            var bytes = new byte[pixels.Length * 4];
            Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
            var hash = md5.ComputeHash(bytes);
            var sb = new System.Text.StringBuilder();
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
