// UV island extraction.
// UV 岛提取：
//  - per (mesh, uv channel): triangles of eligible usages
//  - whole-translate UV normalization into [0,1]; cross-seam detection -> whitelist+warning
//  - island connectivity via shared UV edges (quantized, seam-vertex safe)
//  - overlap merge within and across meshes (union-find)
//  - packing components = connected components of texture<->island bipartite graph
//
// Multi-channel UV: each channel handled independently. / 多通道UV独立处理。

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace net.fosa.ato.editor
{
    internal static class IslandExtractor
    {
        internal static void Extract(AtoSession s)
        {
            using var _ = ATOLog.Scope("ExtractIslands");

            // per renderer: mesh triangle tables / 每渲染器的三角形表
            var meshData = new Dictionary<Mesh, MeshData>();
            MeshData GetMD(RendererInfo ri)
            {
                if (!meshData.TryGetValue(ri.mesh, out var md))
                {
                    md = BuildMeshData(ri, s);
                    meshData[ri.mesh] = md;
                }
                return md;
            }

            int islandId = 0;
            // 1) per (renderer, channel) groups / 逐(渲染器,通道)分组
            foreach (var ri in s.renderers)
            {
                var md = GetMD(ri);
                if (md == null) continue;

                // collect eligible uses on this renderer grouped by channel / 按通道归组合格引用
                var byChannel = new Dictionary<int, List<(TexUse use, List<int> tris)>>();
                foreach (var info in s.texInfos.Values)
                {
                    if (info.whitelisted) continue;
                    foreach (var use in info.uses)
                    {
                        if (use.renderer != ri.renderer || use.specialUse || use.stTransformed) continue;
                        if (!byChannel.TryGetValue(use.uvChannel, out var list))
                            byChannel[use.uvChannel] = list = new List<(TexUse, List<int>)>();
                        list.Add((use, TrianglesOf(md, use.slot)));
                    }
                }

                foreach (var kv in byChannel)
                {
                    int channel = kv.Key;
                    if (channel < 0 || channel > 3) continue; // out of range -> cannot process / 越界通道
                    var uv = md.uvs[channel];
                    if (uv == null) continue;

                    // ---- normalization & seam check (whole group) / 整组归一与跨缝检查 ----
                    var allTris = kv.Value.SelectMany(t => t.tris).Distinct().ToList();
                    var bounds = UvBoundsOf(md, allTris, uv);
                    if (bounds.width > 1f + 1e-4f || bounds.height > 1f + 1e-4f || float.IsNaN(bounds.width))
                    {
                        // cross-seam / repeated sampling: whitelist every involved texture
                        // 跨缝/重复采样：相关贴图全部白名单
                        foreach (var (use, _) in kv.Value)
                        {
                            var ti = s.texInfos[use.texture];
                            if (!ti.whitelisted)
                            {
                                ti.whitelisted = true;
                                ti.whiteReason = "UV cross-seam";
                                s.warnings.Add(string.Format(ATOL10n.Get("warn.crossSeam"), use.texture.name));
                            }
                        }
                        continue;
                    }

                    Vector2 offset = new Vector2(Mathf.Floor(bounds.xMin), Mathf.Floor(bounds.yMin));
                    // record for later UV rewrite / 供后续UV重写使用
                    md.uvOffsets[channel] = offset;
                    s.uvOffsets[(ri.mesh, channel)] = offset;

                    // ---- island connectivity via UV edges / UV边连通 ----
                    var islands = ConnectivityByUvEdges(md, allTris, uv, offset);
                    // ---- overlap merge / 重叠合并 ----
                    MergeOverlapping(md, islands, uv, offset, allTris);
                    // ---- build island groups / 建岛 ----
                    BuildIslands(s, ri, md, channel, kv.Value, islands, uv, offset, ref islandId);
                }
            }

            // 2) cross-mesh overlap merge via shared textures / 跨网格重叠合并（同贴图）
            MergeAcrossMeshes(s, ref islandId);

            // 3) packing components / 连通分量
            BuildComponents(s);

            // 4) whitelist-shared islands drop eligible textures to non-atlas / 白名单共UV处理
            ApplyWhitelistSharedRules(s);

            ATOLog.Info($"islands: {s.islands.Count}, components: {s.components.Count}, " +
                        $"non-atlas textures: {s.texInfos.Values.Count(t => t.forceNoAtlas)}");
        }

        // ------------------------------------------------------------------
        private class MeshData
        {
            internal Mesh mesh;
            internal Vector3[] vertices;
            internal Vector2[][] uvs = new Vector2[4][];
            internal Vector2[] uvOffsets = new Vector2[4];
            internal int[][] submeshTris;          // global triangle ids per submesh / 每子网格全局三角形号
            internal int submeshTriStart;          // submesh index base / 子网格起始
            internal float[] triWorldArea;         // edit-time world area / 编辑期世界面积
            internal float[] triBlendFactor;       // max blendshape area factor (>=1) / 形态键面积因子
            internal int triCount;
        }

        private static MeshData BuildMeshData(RendererInfo ri, AtoSession s)
        {
            var mesh = ri.mesh;
            if (mesh == null) return null;
            var md = new MeshData
            {
                mesh = mesh,
                vertices = mesh.vertices,
                triCount = mesh.triangles.Length / 3,
            };
            for (int c = 0; c < 4; c++)
            {
                var list = new List<Vector2>();
                mesh.GetUVs(c, list);
                md.uvs[c] = list.Count == mesh.vertexCount ? list.ToArray() : null;
            }

            var tris = mesh.triangles;
            md.submeshTris = new int[mesh.subMeshCount][];
            var global = new int[md.triCount];
            // map local submesh triangles to global triangle ids / 子网格三角形到全局号
            var globalIdxOfLocal = new Dictionary<long, int>(md.triCount);
            for (int t = 0; t < md.triCount; t++)
                globalIdxOfLocal[((long)tris[t * 3] << 40) + ((long)tris[t * 3 + 1] << 20) + tris[t * 3 + 2]] = t;
            for (int sm = 0; sm < mesh.subMeshCount; sm++)
            {
                var st = mesh.GetTriangles(sm);
                var arr = new int[st.Length / 3];
                int miss = 0;
                for (int t = 0; t < arr.Length; t++)
                {
                    long key = ((long)st[t * 3] << 40) + ((long)st[t * 3 + 1] << 20) + st[t * 3 + 2];
                    if (globalIdxOfLocal.TryGetValue(key, out int g)) arr[t] = g;
                    else arr[t] = -1, miss++;
                }
                if (miss > 0)
                    ATOLog.DebugL($"mesh {mesh.name} submesh {sm}: {miss} triangles unmatched (degenerate/duplicated)");
                md.submeshTris[sm] = arr;
            }

            // world areas / 世界面积
            var l2w = ri.renderer.transform.localToWorldMatrix;
            md.triWorldArea = new float[md.triCount];
            for (int t = 0; t < md.triCount; t++)
                md.triWorldArea[t] = TriArea(l2w.MultiplyPoint3x4(md.vertices[tris[t * 3]]),
                    l2w.MultiplyPoint3x4(md.vertices[tris[t * 3 + 1]]),
                    l2w.MultiplyPoint3x4(md.vertices[tris[t * 3 + 2]]));

            // blendshape factors (per shape max(0,100)) / 形态键因子
            md.triBlendFactor = MeshAreaFactors.BlendshapeFactors(mesh, md.vertices, tris, md.triCount, md.triWorldArea, l2w);
            return md;
        }

        private static List<int> TrianglesOf(MeshData md, int slot)
        {
            var set = new List<int>();
            if (slot >= 0)
            {
                if (slot < md.submeshTris.Length)
                    foreach (var t in md.submeshTris[slot])
                        if (t >= 0) set.Add(t);
            }
            else
            {
                for (int sm = 0; sm < md.submeshTris.Length; sm++)
                    foreach (var t in md.submeshTris[sm])
                        if (t >= 0) set.Add(t);
            }
            return set;
        }

        private static Rect UvBoundsOf(MeshData md, List<int> tris, Vector2[] uv)
        {
            var b = new Rect();
            bool first = true;
            var tri = md.mesh.triangles;
            foreach (var t in tris)
            {
                for (int k = 0; k < 3; k++)
                {
                    var p = uv[tri[t * 3 + k]];
                    if (first) { b = new Rect(p, Vector2.zero); first = false; }
                    else b = RectMinMax(b, p);
                }
            }
            return b;
        }

        private static Rect RectMinMax(Rect r, Vector2 p)
        {
            float x0 = Mathf.Min(r.xMin, p.x), y0 = Mathf.Min(r.yMin, p.y);
            float x1 = Mathf.Max(r.xMax, p.x), y1 = Mathf.Max(r.yMax, p.y);
            return Rect.MinMaxRect(x0, y0, x1, y1);
        }

        /// <summary>Union triangles connected by identical UV edges (seam-safe).
        /// 通过相同UV边连通三角形（接缝安全）。</summary>
        private static List<List<int>> ConnectivityByUvEdges(MeshData md, List<int> allTris, Vector2[] uv, Vector2 offset)
        {
            var tri = md.mesh.triangles;
            var uf = new UnionFind(allTris.Count);
            var index = allTris.Select((t, i) => (t, i)).ToDictionary(x => x.t, x => x.i);

            // edge key -> first triangle / 边键 -> 首个三角形
            var edgeOwner = new Dictionary<(long, long), int>(allTris.Count * 3);
            const float q = 1e5f; // quantization / 量化
            foreach (var t in allTris)
            {
                int i = index[t];
                var a = Quant(uv[tri[t * 3]] - offset, q);
                var b = Quant(uv[tri[t * 3 + 1]] - offset, q);
                var c = Quant(uv[tri[t * 3 + 2]] - offset, q);
                foreach (var (e0, e1) in new[] { (a, b), (b, c), (c, a) })
                {
                    if (e0 == e1) continue; // degenerate / 退化
                    long pa = PackPoint(e0), pb = PackPoint(e1);
                    var key = pa < pb ? (pa, pb) : (pb, pa);
                    if (edgeOwner.TryGetValue(key, out int other)) uf.Union(i, other);
                    else edgeOwner[key] = i;
                }
            }

            return allTris.Select((t, i) => (t, i))
                .GroupBy(x => uf.Find(x.i))
                .Select(g => g.Select(x => x.t).ToList())
                .ToList();
        }

        private static Vector2 Quant(Vector2 v, float q) => new Vector2(Mathf.Round(v.x * q), Mathf.Round(v.y * q));

        private static long PackPoint(Vector2 v) => ((long)v.x << 32) | (uint)(int)v.y;

        /// <summary>Merge islands whose UV coverage overlaps (same or different textures).
        /// UV覆盖重叠的岛合并（不区分贴图）。</summary>
        private static void MergeOverlapping(MeshData md, List<List<int>> islands, Vector2[] uv,
            Vector2 offset, List<int> allTris)
        {
            const int G = 256; // coarse grid for overlap / 粗网格
            var cells = new Dictionary<int, int>(); // cell -> island id
            var uf = new UnionFind(islands.Count);
            var tri = md.mesh.triangles;
            for (int isl = 0; isl < islands.Count; isl++)
            {
                foreach (var t in islands[isl])
                {
                    var a = uv[tri[t * 3]] - offset;
                    var b = uv[tri[t * 3 + 1]] - offset;
                    var c = uv[tri[t * 3 + 2]] - offset;
                    // conservative: rasterize bbox of each triangle / 保守：三角形bbox光栅
                    int x0 = ClampCell(Mathf.Min(a.x, Mathf.Min(b.x, c.x)), G);
                    int x1 = ClampCell(Mathf.Max(a.x, Mathf.Max(b.x, c.x)), G);
                    int y0 = ClampCell(Mathf.Min(a.y, Mathf.Min(b.y, c.y)), G);
                    int y1 = ClampCell(Mathf.Max(a.y, Mathf.Max(b.y, c.y)), G);
                    for (int y = y0; y <= y1; y++)
                        for (int x = x0; x <= x1; x++)
                        {
                            int cell = y * G + x;
                            if (cells.TryGetValue(cell, out int other) && other != isl) uf.Union(isl, other);
                            else cells[cell] = isl;
                        }
                }
            }

            if (uf.ComponentCount == islands.Count) return;
            var merged = islands.Select((lst, i) => (lst, i))
                .GroupBy(x => uf.Find(x.i))
                .Select(g => g.SelectMany(x => x.lst).ToList())
                .ToList();
            islands.Clear();
            islands.AddRange(merged);
        }

        private static int ClampCell(float v, int g) => Mathf.Clamp(Mathf.FloorToInt(v * g), 0, g - 1);

        private static void BuildIslands(AtoSession s, RendererInfo ri, MeshData md, int channel,
            List<(TexUse use, List<int> tris)> uses, List<List<int>> islands, Vector2[] uv, Vector2 offset,
            ref int islandId)
        {
            var tri = md.mesh.triangles;
            foreach (var triList in islands)
            {
                var set = triList.ToHashSet();
                var island = new UvIsland { id = islandId++ };
                float worldArea = 0f, uvArea = 0f;
                var b = new Rect();
                bool first = true;
                foreach (var t in triList)
                {
                    worldArea += md.triWorldArea[t] * md.triBlendFactor[t];
                    var uvA = uv[tri[t * 3]] - offset;
                    var uvB = uv[tri[t * 3 + 1]] - offset;
                    var uvC = uv[tri[t * 3 + 2]] - offset;
                    uvArea += Mathf.Abs((uvB.x - uvA.x) * (uvC.y - uvA.y) - (uvC.x - uvA.x) * (uvB.y - uvA.y)) * 0.5f;
                    foreach (var p in new[] { uvA, uvB, uvC })
                    {
                        if (first) { b = new Rect(p, Vector2.zero); first = false; }
                        else b = RectMinMax(b, p);
                    }
                }

                var g = new IslandGroup
                {
                    ri = ri, channel = channel, triangles = triList.ToArray(),
                    textures = new HashSet<Texture2D>(),
                };
                foreach (var (use, useTris) in uses)
                {
                    if (useTris.Exists(t => set.Contains(t))) // shares any triangle / 共享任一三角形
                    {
                        g.textures.Add(use.texture);
                        island.textures.Add(use.texture);
                    }
                }

                island.groups.Add(g);
                island.uvBounds = b;
                island.uvArea = uvArea;
                // animated scale: worst pairwise product / 动画缩放最坏两两乘积
                var f = ri.animatedScaleFactor;
                island.worldArea = worldArea * Mathf.Max(f.x * f.y, Mathf.Max(f.y * f.z, f.z * f.x));
                s.islands.Add(island);
            }
        }

        /// <summary>Cross-mesh merge: islands sharing a texture with overlapping UV footprint.
        /// 跨网格合并：共享贴图且UV足迹重叠的岛。</summary>
        private static void MergeAcrossMeshes(AtoSession s, ref int islandId)
        {
            var byTexture = new Dictionary<Texture2D, List<UvIsland>>();
            foreach (var isl in s.islands)
                foreach (var t in isl.textures)
                {
                    if (!byTexture.TryGetValue(t, out var list)) byTexture[t] = list = new List<UvIsland>();
                    list.Add(isl);
                }

            var uf = new UnionFind(s.islands.Count);
            var idOf = s.islands.Select((isl, i) => (isl, i)).ToDictionary(x => x.isl, x => x.i);
            const int G = 256;
            foreach (var kv in byTexture)
            {
                var cells = new Dictionary<int, int>();
                foreach (var isl in kv.Value)
                {
                    int self = idOf[isl];
                    var x0 = Mathf.FloorToInt(isl.uvBounds.xMin * G); var x1 = Mathf.FloorToInt(isl.uvBounds.xMax * G);
                    var y0 = Mathf.FloorToInt(isl.uvBounds.yMin * G); var y1 = Mathf.FloorToInt(isl.uvBounds.yMax * G);
                    for (int y = y0; y <= y1; y++)
                        for (int x = x0; x <= x1; x++)
                        {
                            int cell = y * G + x;
                            if (cells.TryGetValue(cell, out int otherIdx) && otherIdx != self)
                                uf.Union(self, otherIdx);
                            else cells[cell] = self;
                        }
                }
            }

            if (uf.ComponentCount == s.islands.Count) return;

            var groups = s.islands.GroupBy(isl => uf.Find(idOf[isl])).ToList();
            s.islands.Clear();
            foreach (var g in groups)
            {
                var merged = g.First();
                foreach (var extra in g.Skip(1))
                {
                    merged.groups.AddRange(extra.groups);
                    foreach (var t in extra.textures) merged.textures.Add(t);
                    merged.uvBounds = RectMinMax(merged.uvBounds, extra.uvBounds.min);
                    merged.uvBounds = RectMinMax(merged.uvBounds, extra.uvBounds.max);
                    merged.uvArea += extra.uvArea;
                    merged.worldArea += extra.worldArea;
                }
                s.islands.Add(merged);
            }

            // renumber / 重编号
            for (int i = 0; i < s.islands.Count; i++) s.islands[i].id = i;
        }

        /// <summary>Packing components = connected components over texture<->island graph.
        /// 装箱分量 = 纹理↔岛图连通分量。</summary>
        private static void BuildComponents(AtoSession s)
        {
            var texUf = new UnionFind(s.texInfos.Count);
            var texIdx = new Dictionary<Texture2D, int>();
            int i = 0;
            foreach (var t in s.texInfos.Keys) texIdx[t] = i++;

            foreach (var isl in s.islands)
            {
                Texture2D first = null;
                foreach (var t in isl.textures)
                {
                    if (first == null) first = t;
                    else texUf.Union(texIdx[first], texIdx[t]);
                }
            }

            var comp = new Dictionary<int, PackingComponent>();
            int cid = 0;
            foreach (var isl in s.islands)
            {
                Texture2D anchor = null;
                foreach (var t in isl.textures) { anchor = t; break; }
                if (anchor == null) continue; // island without textures (shouldn't happen) / 无贴图岛
                int root = texUf.Find(texIdx[anchor]);
                if (!comp.TryGetValue(root, out var pc))
                {
                    comp[root] = pc = new PackingComponent { id = cid++ };
                    s.components.Add(pc);
                }
                pc.islands.Add(isl);
                foreach (var t in isl.textures)
                    if (!t.whitelisted && s.texInfos.TryGetValue(t, out var ti) && ti.eligibleForAtlas)
                        pc.textures.Add(t);
            }

            // signature / 签名
            foreach (var pc in s.components)
            {
                var sample = pc.textures.FirstOrDefault();
                if (sample != null) pc.srgb = TexturePixels.IsSrgb(sample, false);
                pc.filterMode = sample != null ? sample.filterMode : FilterMode.Bilinear;
                pc.hasNormal = pc.textures.Any(t => s.texInfos[t].category == AtoTexCategory.Normal);
                pc.hasMask = pc.textures.Any(t => s.texInfos[t].category == AtoTexCategory.Gray);
            }
        }

        /// <summary>Whitelist-shared islands: eligible textures drop to non-atlas (whole-image scale).
        /// 白名单共UV：合格贴图退回非图集路径（整图缩放）。</summary>
        private static void ApplyWhitelistSharedRules(AtoSession s)
        {
            foreach (var isl in s.islands)
            {
                bool hasWhite = isl.textures.Any(t => s.texInfos.TryGetValue(t, out var ti2) && ti2.whitelisted);
                if (!hasWhite) continue;
                foreach (var t in isl.textures)
                    if (s.texInfos.TryGetValue(t, out var ti) && !ti.whitelisted && !ti.forceNoAtlas)
                    {
                        ti.forceNoAtlas = true;
                        s.warnings.Add(string.Format(ATOL10n.Get("warn.noAtlasing"), t.name));
                    }
            }

            foreach (var pc in s.components)
            {
                pc.islands.RemoveAll(isl =>
                    isl.textures.Any(t => s.texInfos.TryGetValue(t, out var ti3) && ti3.whitelisted));
                var dead = pc.textures.Where(t => !pc.islands.Exists(i => i.textures.Contains(t))).ToList();
                foreach (var d in dead) pc.textures.Remove(d);
            }

            s.components.RemoveAll(pc => pc.textures.Count == 0);
        }
    }

    /// <summary>Weighted quick-union. / 并查集。</summary>
    internal class UnionFind
    {
        private readonly int[] _parent, _size;
        private int _count;

        internal UnionFind(int n)
        {
            _parent = new int[n];
            _size = new int[n];
            for (int i = 0; i < n; i++) { _parent[i] = i; _size[i] = 1; }
            _count = n;
        }

        internal int Find(int x)
        {
            while (_parent[x] != x) { _parent[x] = _parent[_parent[x]]; x = _parent[x]; }
            return x;
        }

        internal void Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra == rb) return;
            if (_size[ra] < _size[rb]) (ra, rb) = (rb, ra);
            _parent[rb] = ra;
            _size[ra] += _size[rb];
            _count--;
        }

        internal int ComponentCount => _count;
    }
}
