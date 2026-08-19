// Stage4_Packing — candidate pools, queues, raster BLF packing / 候选池、贴图队列、光栅化 BLF 装箱
// Spec mapping (CLAUDE.md §4.1): atom = one texture + islands of its referencing slots; global island
// rect registry; pre-place for co-location across atlases; conflicts → alias queues (atlas count grows
// naturally). Rotation skipped for islands in normal-bearing groups (tangents never recomputed).<br>
// 规格映射：原子=单贴图+引用槽的岛；岛矩形全局登记；跨图集预放置共位；冲突→别名队列自然增长。
// 法线组岛不旋转（切线数据保持原样、绝不重算）。
using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Fosa.ATO.Editor
{
    internal static class Stage4_Packing
    {
        private sealed class Item
        {
            internal TextureInfo tex;
            internal List<Island> islands = new List<Island>();
            internal long rasterArea;
            internal int maxSide;
            internal AtlasDef atlas;
        }

        private sealed class MaskSet
        {
            internal ulong[] shape, shapeT;
            internal int cw, ch;
            internal long area;
            internal readonly Dictionary<int, ulong[]> dilated = new Dictionary<int, ulong[]>();
            internal readonly Dictionary<int, ulong[]> dilatedT = new Dictionary<int, ulong[]>();
        }

        internal struct Cand { internal int w, h; internal long area; internal float aspect; }

        internal static void Run(BuildContext ctx, ATOPipeContext pipe, StageProgress progress)
        {
            // ---------- per-texture type keys / 贴图粒度类型键 ----------
            ComputeTextureTypeKeys(pipe);

            // ---------- payload islands per texture / 每贴图载荷岛 ----------
            var islandsOf = PayloadIslands(pipe);

            var itemsByKey = new Dictionary<TypeGroupKey, List<Item>>();
            foreach (var info in pipe.textures)
            {
                if (info.whitelisted || !islandsOf.TryGetValue(info, out var isl) || isl.Count == 0) continue;
                if (info.typeKey.classMask == 0) continue;
                if (isl.All(i => i.group.whitelisted)) continue; // whitelist group → whole-texture path / 白名单组走整图路径
                var item = new Item { tex = info, islands = isl };
                if (!itemsByKey.TryGetValue(info.typeKey, out var list)) itemsByKey[info.typeKey] = list = new List<Item>();
                list.Add(item);
            }

            // ---------- island masks (Burst rasterization) / 岛掩码（Burst 光栅化） ----------
            var masks = new Dictionary<Island, MaskSet>();
            int mi = 0;
            foreach (var isl in pipe.islands)
            {
                mi++;
                if ((mi & 31) == 0) pipe.CancelCheck(progress, ATOL10n.T("ato.stage.packing"), 0.3f * mi / Mathf.Max(1, pipe.islands.Count));
                if (isl.unifiedSize.x <= 0 || isl.unifiedSize.y <= 0) continue;
                if (isl.group.whitelisted) continue;
                var ms = Rasterize(isl, pipe);
                if (ms != null) masks[isl] = ms;
            }

            foreach (var kv in itemsByKey)
            {
                foreach (var item in kv.Value)
                {
                    item.islands = item.islands.Where(masks.ContainsKey).ToList();
                    item.rasterArea = item.islands.Sum(i => masks[i].area * 16L);
                    item.maxSide = item.islands.Count > 0 ? item.islands.Max(i => Mathf.Max(i.unifiedSize.x, i.unifiedSize.y)) : 0;
                }
                kv.Value.RemoveAll(i => i.islands.Count == 0);
                kv.Value.Sort((a, b) => a.rasterArea != b.rasterArea
                    ? b.rasterArea.CompareTo(a.rasterArea)      // raster area desc / 光栅面积降序
                    : b.maxSide.CompareTo(a.maxSide));          // side desc / 边长降序
            }

            int maxSize = MaxAtlasFor(pipe.settings);
            var pool = BuildCandidatePool(pipe.settings, maxSize);

            // ---------- queue processing / 队列处理 ----------
            int totalItems = Mathf.Max(1, itemsByKey.Sum(k => k.Value.Count));
            int processed = 0;
            foreach (var kv in itemsByKey)
            {
                var key = kv.Key;
                var pending = new List<List<Item>> { kv.Value };
                // Co-location invariant: every atlas of one type-key queue shares the SAME size,
                // otherwise normalized rects registered for cross-atlas co-location would diverge.
                // 共位不变量：同一类型键队列的所有图集必须同尺寸，否则跨图集共位的归一化矩形会发散。
                Cand? keyCand = null;
                while (pending.Count > 0)
                {
                    var queue = pending[0]; pending.RemoveAt(0);
                    if (queue.Count == 0) continue;
                    var largest = keyCand ?? pool[pool.Count - 1];

                    // abandon items that can't fit even alone into the largest candidate / 单贴图放不进最大图集→整组放弃
                    var alive = new List<Item>();
                    foreach (var item in queue)
                    {
                        var probe = Attempt(queue: new List<Item> { item }, cand: largest, pipe: pipe, masks: masks,
                            registry: null, atlas: null, commit: false);
                        if (probe.Count == 0) alive.Add(item);
                        else
                        {
                            foreach (var isl in item.islands) isl.group.atlasAbandoned = true;
                            var wmsg = ATOL10n.T("ato.warn.atlas_abandoned", item.tex.source.name);
                            ATOLog.Warn(wmsg); pipe.warnings.Add(wmsg);
                            ErrorReport.ReportError(ATOL10n.L, ErrorSeverity.NonFatal, "ato.err.atlas_abandoned", item.tex.source.name);
                        }
                    }
                    if (alive.Count == 0) continue;
                    queue = alive;

                    long totalArea = queue.Sum(i => i.rasterArea);
                    List<Item> failed = null;
                    AtlasDef final = null;
                    foreach (var cand in pool)
                    {
                        if (keyCand.HasValue && (cand.w != keyCand.Value.w || cand.h != keyCand.Value.h)) break; // same size only / 仅同尺寸（池已按面积升序，无需继续）
                        if (cand.area < totalArea) continue; // drop too-small candidates / 丢弃面积不足候选
                        if (!FixedRectsFit(queue, pipe, cand)) continue;
                        var f = Attempt(queue, cand, pipe, masks, pipe.islandPlacement, null, commit: false);
                        if (f.Count == 0)
                        {
                            // first candidate fitting everything → winner, then commit for real / 首个装下全部→成品
                            final = NewAtlas(key, cand, pipe);
                            Attempt(queue, cand, pipe, masks, pipe.islandPlacement, final, commit: true);
                            keyCand ??= cand;
                            break;
                        }
                        failed = f;
                    }
                    if (final == null)
                    {
                        // greedy at largest candidate; leftovers go to alias queue / 最大候选贪婪装箱，剩余进别名队列
                        final = NewAtlas(key, largest, pipe);
                        failed = Attempt(queue, largest, pipe, masks, pipe.islandPlacement, final, commit: true);
                        keyCand ??= largest;
                    }
                    pipe.atlases.Add(final);
                    processed += queue.Count - failed.Count;
                    pipe.CancelCheck(progress, ATOL10n.T("ato.stage.packing"), 0.3f + 0.7f * processed / totalItems);
                    if (failed.Count > 0) pending.Add(failed); // alias queue (same type reused) / 别名队列（同类复用）
                }
            }

            ATOLog.Info(ATOL10n.T("ato.log.pack_done", pipe.atlases.Count, pipe.islandPlacement.Count,
                pipe.groups.Count(g => g.atlasAbandoned)));
            ATOEvents.Raise("packing", pipe, ctx.AvatarRootObject);
            ATOHookRegistry.Notify("packing", pipe);
        }

        // ---------------------------------------------------------------- type keys & payload
        private static void ComputeTextureTypeKeys(ATOPipeContext pipe)
        {
            var classesOf = new Dictionary<TextureInfo, HashSet<TexClass>>();
            foreach (var kv in pipe.slotRefs)
                foreach (var r in kv.Value)
                    foreach (var t in r.textures)
                    {
                        if (!pipe.infoOf.TryGetValue(t, out var info)) continue;
                        if (!classesOf.TryGetValue(info, out var set)) classesOf[info] = set = new HashSet<TexClass>();
                        set.Add(r.cls);
                    }
            foreach (var info in pipe.textures)
            {
                var set = classesOf.TryGetValue(info, out var s) ? s : new HashSet<TexClass>();
                int mask = 0;
                foreach (var c in set) mask |= TypeGroupKey.ClassBit(c);
                info.typeKey = new TypeGroupKey { classMask = mask, albedoSRGB = set.Contains(TexClass.Albedo) && info.sRGB, filterBucket = (int)info.filterMode };
            }
        }

        private static Dictionary<TextureInfo, List<Island>> PayloadIslands(ATOPipeContext pipe)
        {
            var islandsOf = new Dictionary<TextureInfo, List<Island>>();
            foreach (var kv in pipe.slotRefs)
            {
                if (!pipe.slotIslands.TryGetValue(kv.Key, out var slotIslands)) continue;
                foreach (var r in kv.Value)
                    foreach (var t in r.textures)
                    {
                        if (!pipe.infoOf.TryGetValue(t, out var info)) continue;
                        if (!islandsOf.TryGetValue(info, out var list)) islandsOf[info] = list = new List<Island>();
                        foreach (var isl in slotIslands) if (!list.Contains(isl)) list.Add(isl);
                    }
            }
            return islandsOf;
        }

        // ---------------------------------------------------------------- atlas helpers
        internal static AtlasDef NewAtlas(TypeGroupKey key, Cand cand, ATOPipeContext pipe) => new AtlasDef
        {
            width = Mathf.Max(64, cand.w), height = Mathf.Max(64, cand.h),
            padding = PadPx(pipe.settings, cand),
            key = key,
        };

        private static int PadPx(ATOSettingsSnap s, Cand c)
        {
            // ceil(maxSide/128) clamped down to 4px granularity, min = user padding / 向上取整、4px 粒度、下限为用户最小padding
            int raw = Mathf.CeilToInt(Mathf.Max(c.w, c.h) / 128f);
            raw = Mathf.CeilToInt(raw / 4f) * 4;
            return Mathf.Max(s.minPadding, raw);
        }

        private static int MaxAtlasFor(ATOSettingsSnap s)
        {
            var p = CurrentPlatform();
            var ov = s.Override(p);
            if (ov != null && ov.enabled)
                return Mathf.Min(ov.maxAtlasSize, p == ATOPlatform.PC ? AvatarTextureOptimizer.MaxAtlasSizePC : AvatarTextureOptimizer.MaxAtlasSizeMobile);
            return p == ATOPlatform.PC ? AvatarTextureOptimizer.MaxAtlasSizePC : AvatarTextureOptimizer.MaxAtlasSizeMobile;
        }

        internal static ATOPlatform CurrentPlatform()
        {
            var t = UnityEditor.EditorUserBuildSettings.activeBuildTarget;
            if (t == UnityEditor.BuildTarget.Android) return ATOPlatform.Android;
            if (t == UnityEditor.BuildTarget.iOS) return ATOPlatform.IOS;
            return ATOPlatform.PC;
        }

        private static List<Cand> BuildCandidatePool(ATOSettingsSnap s, int maxSide)
        {
            var sides = new List<int>();
            if (s.allowNPOT) { for (int v = 64; v <= maxSide; v += 64) sides.Add(v); }
            else { for (int v = 64; v <= maxSide; v *= 2) sides.Add(v); }
            var pool = new List<Cand>();
            foreach (var w in sides)
            foreach (var h in sides)
            {
                float aspect = Mathf.Max(w / (float)h, h / (float)w);
                if (aspect > 4f) continue; // near-square preferred; extreme strips excluded / 极端长条排除
                pool.Add(new Cand { w = w, h = h, area = (long)w * h, aspect = aspect });
            }
            pool.Sort((a, b) => a.area != b.area ? a.area.CompareTo(b.area) : a.aspect.CompareTo(b.aspect));
            return pool;
        }

        private static bool FixedRectsFit(List<Item> queue, ATOPipeContext pipe, Cand cand)
        {
            foreach (var item in queue)
            foreach (var isl in item.islands)
                if (pipe.islandPlacement.TryGetValue(isl, out var p))
                    if (p.rect.xMax > cand.w || p.rect.yMax > cand.h) return false;
            return true;
        }

        // ---------------------------------------------------------------- rasterization
        private static MaskSet Rasterize(Island isl, ATOPipeContext pipe)
        {
            var mesh = isl.slot.renderer is SkinnedMeshRenderer smr ? smr.sharedMesh
                : (isl.slot.renderer.TryGetComponent<MeshFilter>(out var mf) ? mf.sharedMesh : null);
            if (mesh == null) return null;
            var uvs = new List<Vector2>();
            mesh.GetUVs(isl.slot.channel, uvs);
            if (uvs.Count == 0) return null;

            int cw = Mathf.Max(1, Mathf.CeilToInt(isl.unifiedSize.x / (float)RasterJobs.CellPx));
            int ch = Mathf.Max(1, Mathf.CeilToInt(isl.unifiedSize.y / (float)RasterJobs.CellPx));
            var ms = new MaskSet { cw = cw, ch = ch };
            var corners = new NativeArray<float>(isl.triIndices.Count * 2, Allocator.TempJob);
            var span = isl.NormalizedSpan;
            for (int i = 0; i < isl.triIndices.Count; i++)
            {
                int vi = isl.triIndices[i];
                if (vi < 0 || vi >= uvs.Count) { corners[i * 2] = 0; corners[i * 2 + 1] = 0; continue; }
                var uv = uvs[vi] - new Vector2(isl.tileOffset.x, isl.tileOffset.y);
                corners[i * 2] = (uv.x - isl.nMin.x) / Mathf.Max(1e-8f, span.x) * isl.unifiedSize.x / RasterJobs.CellPx;
                corners[i * 2 + 1] = (uv.y - isl.nMin.y) / Mathf.Max(1e-8f, span.y) * isl.unifiedSize.y / RasterJobs.CellPx;
            }
            var mk = new NativeArray<ulong>(RasterJobs.WordsFor(cw) * ch, Allocator.TempJob);
            new RasterJobs.RasterIslandJob { corners = corners, cellW = cw, cellH = ch, mask = mk }.Run();
            corners.Dispose();
            ms.shape = mk.ToArray();
            mk.Dispose();
            long area = 0;
            foreach (var w in ms.shape) { ulong v = w; while (v != 0) { v &= v - 1; area++; } }
            ms.area = area;
            return ms;
        }

        private static ulong[] Dilated(MaskSet ms, int padCells, bool rotated)
        {
            var table = rotated ? ms.dilatedT : ms.dilated;
            if (table.TryGetValue(padCells, out var cached)) return cached;
            if (rotated && ms.shapeT == null)
            {
                var src = new NativeArray<ulong>(ms.shape, Allocator.TempJob);
                var dst = new NativeArray<ulong>(RasterJobs.WordsFor(ms.ch) * ms.cw, Allocator.TempJob);
                new RasterJobs.TransposeJob { src = src, cellW = ms.cw, cellH = ms.ch, dst = dst }.Run();
                ms.shapeT = dst.ToArray();
                src.Dispose(); dst.Dispose();
            }
            var baseShape = rotated ? ms.shapeT : ms.shape;
            int bw = rotated ? ms.ch : ms.cw, bh = rotated ? ms.cw : ms.ch;
            var cur = (ulong[])baseShape.Clone();
            for (int r = 0; r < padCells; r++)
            {
                var srcN = new NativeArray<ulong>(cur, Allocator.TempJob);
                var dstN = new NativeArray<ulong>(RasterJobs.WordsFor(bw) * bh, Allocator.TempJob);
                new RasterJobs.Dilate3Job { src = srcN, cellW = bw, cellH = bh, dst = dstN }.Run();
                cur = dstN.ToArray();
                srcN.Dispose(); dstN.Dispose();
            }
            table[padCells] = cur;
            return cur;
        }

        // ---------------------------------------------------------------- pack attempt
        /// <summary>Try placing all items into one candidate. Returns failed items. / 尝试将全部贴图装入候选，返回失败贴图。</summary>
        private static List<Item> Attempt(List<Item> queue, Cand cand, ATOPipeContext pipe,
            Dictionary<Island, MaskSet> masks, Dictionary<Island, IslandPlacement> registry,
            AtlasDef atlas, bool commit)
        {
            int cw = Mathf.Max(64, cand.w) / RasterJobs.CellPx;
            int ch = Mathf.Max(64, cand.h) / RasterJobs.CellPx;
            int padPx = Mathf.Max(pipe.settings.minPadding, Mathf.CeilToInt(Mathf.CeilToInt(Mathf.Max(cand.w, cand.h) / 128f) / 4f) * 4);
            int pc = padPx / RasterJobs.CellPx;
            // proof-of-work: pad must be multiple of 4 by construction / padding 构造上为4倍数

            var failed = new List<Item>();
            var canvas = new NativeArray<ulong>(RasterJobs.WordsFor(cw) * ch, Allocator.TempJob);
            // rects fixed in this attempt (incl. committed registry + local placements) / 本次尝试内已固定的矩形
            var localPlacement = new Dictionary<Island, RectInt>();
            var localRot = new Dictionary<Island, bool>();
            try
            {
                foreach (var item in queue)
                {
                    if (PlaceItem(item, pipe, masks, registry, localPlacement, localRot, canvas, cw, ch, padPx, pc, commit))
                    {
                        if (commit && atlas != null)
                        {
                            item.atlas = atlas;
                            if (!atlas.groups.Contains(item.islands[0].group)) atlas.groups.Add(item.islands[0].group);
                            foreach (var isl in item.islands)
                            {
                                var rect = localPlacement[isl];
                                var rot = localRot.TryGetValue(isl, out var r) && r;
                                atlas.entries.Add(new AtlasDef.Entry { island = isl, rect = rect, rotated = rot, tex = item.tex });
                                if (!atlas.islands.Contains(isl)) atlas.islands.Add(isl);
                            }
                        }
                    }
                    else failed.Add(item);
                }
            }
            finally { canvas.Dispose(); }
            return failed;
        }

        private static bool PlaceItem(Item item, ATOPipeContext pipe, Dictionary<Island, MaskSet> masks,
            Dictionary<Island, IslandPlacement> registry, Dictionary<Island, RectInt> localPlacement,
            Dictionary<Island, bool> localRot, NativeArray<ulong> canvas, int cw, int ch, int padPx, int pc, bool commit)
        {
            // 1) fixed islands (pre-place at registered rect for cross-atlas co-location) / 预放置共位
            foreach (var isl in item.islands)
            {
                int padOrigin = padPx;
                if (!localPlacement.TryGetValue(isl, out var rect))
                {
                    if (registry == null || !registry.TryGetValue(isl, out var placed)) continue;
                    rect = placed.rect;
                    padOrigin = placed.padPx; // anchor uses the ORIGINAL atlas padding / 锚定用原图集padding
                }
                bool rot0 = localRot.TryGetValue(isl, out var rr) && rr;
                int anchorX = Mathf.FloorToInt((rect.x - padOrigin) / (float)RasterJobs.CellPx);
                int anchorY = Mathf.FloorToInt((rect.y - padOrigin) / (float)RasterJobs.CellPx);
                var ms = masks[isl];
                var dil = Dilated(ms, pc, rot0);
                int mw = rot0 ? ms.ch : ms.cw, mh = rot0 ? ms.cw : ms.ch;
                if (!CheckAt(dil, mw, mh, canvas, cw, ch, anchorX, anchorY)) return false; // variant conflict → alias / 变体冲突→别名
                // stamp in both probe & commit so following atoms see occupied space / 探测与提交都要落笔
                Stamp(dil, mw, mh, canvas, cw, anchorX, anchorY);
                localPlacement[isl] = rect;
                localRot[isl] = rot0;
            }
            // 2) free islands via full-scan BLF / 自由岛全扫描 BLF
            var free = item.islands
                .Where(i => masks.ContainsKey(i) && !localPlacement.ContainsKey(i) && (registry == null || !registry.ContainsKey(i)))
                .OrderByDescending(i => masks[i].area)
                .ThenByDescending(i => Mathf.Max(i.unifiedSize.x, i.unifiedSize.y));
            foreach (var isl in free)
            {
                var ms = masks[isl];
                bool allowRotate = !GroupHasNormal(isl.group);
                bool placed = false;
                foreach (bool rot in allowRotate ? new[] { false, true } : new[] { false })
                {
                    var dil = Dilated(ms, pc, rot);
                    int mw = rot ? ms.ch : ms.cw, mh = rot ? ms.cw : ms.ch;
                    if (Blf(dil, mw, mh, canvas, cw, ch, out int cx, out int cy))
                    {
                        Stamp(dil, mw, mh, canvas, cw, cx, cy);
                        localPlacement[isl] = new RectInt(cx * RasterJobs.CellPx + padPx, cy * RasterJobs.CellPx + padPx,
                            rot ? isl.unifiedSize.y : isl.unifiedSize.x, rot ? isl.unifiedSize.x : isl.unifiedSize.y);
                        localRot[isl] = rot;
                        placed = true;
                        break;
                    }
                }
                if (!placed) return false;
            }
            // register globally on commit / 提交时写入全局登记
            if (commit && registry != null)
            {
                foreach (var isl in item.islands)
                {
                    if (registry.ContainsKey(isl)) continue;
                    registry[isl] = new IslandPlacement
                    {
                        rect = localPlacement[isl],
                        rotated = localRot.TryGetValue(isl, out var r) && r,
                        padPx = padPx,
                    };
                }
            }
            return true;
        }

        private static bool GroupHasNormal(PackingGroup g)
        {
            foreach (var r in g.refs) if (r.cls == TexClass.Normal) return true;
            return false;
        }

        // ---------------------------------------------------------------- canvas bit ops
        private static bool CheckAt(ulong[] mask, int mw, int mh, NativeArray<ulong> canvas, int cw, int ch, int x, int y)
        {
            if (x < 0 || y < 0 || x + mw > cw || y + mh > ch) return false;
            var m = new NativeArray<ulong>(mask, Allocator.TempJob);
            var res = new NativeArray<int>(1, Allocator.TempJob);
            new RasterJobs2.CheckFitJob
            {
                canvas = canvas, canvasCellW = cw, canvasCellH = ch,
                mask = m, maskCellW = mw, maskCellH = mh, posX = x, posY = y, result = res,
            }.Run();
            bool ok = res[0] == 1;
            m.Dispose(); res.Dispose();
            return ok;
        }

        private static bool Blf(ulong[] mask, int mw, int mh, NativeArray<ulong> canvas, int cw, int ch, out int x, out int y)
        {
            var m = new NativeArray<ulong>(mask, Allocator.TempJob);
            var res = new NativeArray<int>(3, Allocator.TempJob);
            new RasterJobs.FindFitJob
            {
                canvas = canvas, canvasW = cw, canvasH = ch,
                mask = m, maskW = mw, maskH = mh, result = res,
            }.Run();
            bool ok = res[2] == 1;
            x = res[0]; y = res[1];
            m.Dispose(); res.Dispose();
            return ok;
        }

        private static void Stamp(ulong[] mask, int mw, int mh, NativeArray<ulong> canvas, int cw, int x, int y)
        {
            var m = new NativeArray<ulong>(mask, Allocator.TempJob);
            new RasterJobs.StampJob { canvas = canvas, canvasW = cw, mask = m, maskW = mw, maskH = mh, posX = x, posY = y }.Run();
            m.Dispose();
        }
    }
}
