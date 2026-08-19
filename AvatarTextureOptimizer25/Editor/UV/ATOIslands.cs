// Avatar Texture Optimizer / 头像贴图优化器
// UV island construction: position welding + connected components,
// out-of-range normalization (single-cell shift, wrap-seam rejection),
// overlapping island merge, anisotropy (PCA) axes, world area.
// UV 岛构建：位置焊接 + 连通分量、越界归一（整格平移、跨 wrap 缝判定）、
// 重叠岛合并、各向异性（PCA）轴向、真实世界面积。
//
// Data model: every island bakes its own welded UV vertices (post per-island
// normalization), which makes later stages (rasterization, packing, UV rewrite)
// independent of the original mesh layout.
// 数据模型：每个岛自带焊接后的 UV 顶点（已做逐岛归一），后续阶段（光栅化、
// 装箱、UV 重写）不再依赖原始网格布局。

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace FOSA.AvatarTextureOptimizer.Editor
{
    /// <summary>Result for one UV group. / 单个 UV 组的结果。</summary>
    public sealed class ATOIslandBuildResult
    {
        public ATOUVGroup group;
        public bool ok;
        public string failureReason;
        public float uvAreaTotal; // union area coverage 0..1 / 并集覆盖面积
    }

    /// <summary>Island builder. / 岛构建器。</summary>
    public static class ATOIslands
    {
        private const float WeldEpsilon = 1e-6f;
        private const int MergeGridSize = 1024; // overlap-merge probe grid / 重叠合并探测网格

        /// <summary>
        /// Build islands for a UV group. On failure the group is reported and acts
        /// as whitelist for atlas purposes.
        /// 为一个 UV 组构建岛。失败时该组在图集阶段按白名单处理。
        /// </summary>
        public static ATOIslandBuildResult Build(ATOUVGroup group)
        {
            var result = new ATOIslandBuildResult { group = group, ok = true };
            var mesh = group.mesh;
            int ch = group.uvChannel;
            if (mesh == null || ch < 0 || ch > 7)
            {
                result.ok = false;
                result.failureReason = "invalid mesh or channel / 网格或通道非法";
                return result;
            }
            var attr = VertexAttribute.TexCoord0 + ch;
            if (!mesh.HasVertexAttribute(attr))
            {
                result.ok = false;
                result.failureReason = $"mesh has no UV{ch} / 网格缺少 UV{ch}";
                return result;
            }

            var uvs = new List<Vector2>();
            mesh.GetUVs(ch, uvs);
            var tris = mesh.GetTriangles(group.submesh);
            var verts = mesh.vertices;

            if (uvs.Count == 0 || tris.Length == 0)
            {
                result.ok = false;
                result.failureReason = $"no UV{ch} or triangles / UV{ch} 或三角形为空";
                return result;
            }

            // ---- 1) weld + connected components / 焊接 + 连通分量 ----
            int triCount = tris.Length / 3;
            var uf = new UnionFind(triCount);
            var keyToWeldId = new Dictionary<(int, int), int>();
            var weldLastTri = new Dictionary<int, int>();
            var weldPos = new List<Vector2>();
            for (int t = 0; t < triCount; t++)
            {
                for (int c = 0; c < 3; c++)
                {
                    var uv = uvs[tris[t * 3 + c]];
                    var key = (Quant(uv.x), Quant(uv.y));
                    if (!keyToWeldId.TryGetValue(key, out var wid))
                    {
                        wid = weldPos.Count;
                        keyToWeldId[key] = wid;
                        weldPos.Add(uv);
                    }
                    if (weldLastTri.TryGetValue(wid, out var lastT)) uf.Union(lastT, t);
                    weldLastTri[wid] = t;
                }
            }

            var byRoot = new Dictionary<int, List<int>>();
            for (int t = 0; t < triCount; t++)
            {
                int root = uf.Find(t);
                if (!byRoot.TryGetValue(root, out var list))
                {
                    list = new List<int>();
                    byRoot[root] = list;
                }
                list.Add(t);
            }

            // ---- 2) per-island cell check + normalized baking / 逐岛整格检查 + 归一烘焙 ----
            var islands = new List<ATOIsland>();
            foreach (var kv in byRoot)
            {
                // bounds in ORIGINAL uv space / 原始 UV 空间包围盒
                float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                foreach (var t in kv.Value)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        var uv = uvs[tris[t * 3 + c]];
                        if (uv.x < minX) minX = uv.x;
                        if (uv.y < minY) minY = uv.y;
                        if (uv.x > maxX) maxX = uv.x;
                        if (uv.y > maxY) maxY = uv.y;
                    }
                }
                int minCellX = Mathf.FloorToInt(minX);
                int minCellY = Mathf.FloorToInt(minY);
                const float eps = 1e-5f;
                int maxCellX = Mathf.FloorToInt(maxX - eps);
                int maxCellY = Mathf.FloorToInt(maxY - eps);
                if (minCellX != maxCellX || minCellY != maxCellY)
                {
                    // Island crosses a wrap seam: cannot normalize safely. / 岛跨 wrap 缝：无法安全归一。
                    result.ok = false;
                    result.failureReason = ATOLoc.T("ato:uv.seam", group.mesh.name, group.submesh, ch);
                    group.islands.Clear();
                    return result;
                }

                var shift = new Vector2(-minCellX, -minCellY);
                var island = BakeIsland(kv.Value, tris, uvs, verts, shift, keyToWeldId, weldPos);
                islands.Add(island);
            }

            // ---- 3) merge overlapping islands (same texture region referenced twice) ----
            // ---- 3) 合并重叠岛（同一贴图区域被引用两次的情况）----
            islands = MergeOverlapping(islands);
            for (int i = 0; i < islands.Count; i++) islands[i].index = i;

            // ---- 4) anisotropy + stats / 各向异性 + 统计 ----
            float union = 0f;
            foreach (var isl in islands)
            {
                ComputePCA(isl);
                union += isl.uvArea;
            }
            result.uvAreaTotal = union;
            group.islands.Clear();
            group.islands.AddRange(islands);
            group.areaFactor = Mathf.Max(1f, group.areaFactor);
            return result;
        }

        /// <summary>
        /// Bake one welded island: local bakedUVs (shifted), original vertex ids,
        /// local triangle indices, bounds and areas.
        /// 烘焙一个焊接岛：局部 bakedUVs（已平移）、原始顶点 ID、局部三角形索引、
        /// 包围盒与面积。
        /// </summary>
        private static ATOIsland BakeIsland(
            List<int> islandTris, int[] tris, List<Vector2> uvs, Vector3[] verts,
            Vector2 shift, Dictionary<(int, int), int> keyToWeldId, List<Vector2> weldPos)
        {
            var weldToLocal = new Dictionary<int, int>();
            var baked = new List<Vector2>();
            var orig = new List<int>();
            var local = new List<int>(islandTris.Count * 3);
            float uvArea = 0f, world = 0f;

            foreach (var t in islandTris)
            {
                for (int c = 0; c < 3; c++)
                {
                    int vidx = tris[t * 3 + c];
                    var uv = uvs[vidx];
                    var key = (Quant(uv.x), Quant(uv.y));
                    int wid = keyToWeldId[key];
                    if (!weldToLocal.TryGetValue(wid, out var li))
                    {
                        li = baked.Count;
                        weldToLocal[wid] = li;
                        baked.Add(uv + shift);
                        orig.Add(vidx);
                    }
                    local.Add(li);
                }
                var a = uvs[tris[t * 3]];
                var b = uvs[tris[t * 3 + 1]];
                var c2 = uvs[tris[t * 3 + 2]];
                uvArea += Mathf.Abs((b.x - a.x) * (c2.y - a.y) - (c2.x - a.x) * (b.y - a.y)) * 0.5f;
                Vector3 va = verts[tris[t * 3]];
                Vector3 vb = verts[tris[t * 3 + 1]];
                Vector3 vc = verts[tris[t * 3 + 2]];
                world += Vector3.Cross(vb - va, vc - va).magnitude * 0.5f;
            }

            var island = new ATOIsland
            {
                bakedUVs = baked.ToArray(),
                origVertexIds = orig.ToArray(),
                localTriangles = local.ToArray(),
                sourceTriangleCount = islandTris.Count,
                uvArea = uvArea,
                worldArea = world,
            };
            island.ComputeBoundsFromBaked();
            return island;
        }

        /// <summary>
        /// Merge islands that overlap in normalized UV space (they sample the same
        /// texture region). Uses a shared probe grid.
        /// 合并在归一化 UV 空间中重叠的岛（它们采样同一贴图区域）。使用共享探测网格。
        /// </summary>
        private static List<ATOIsland> MergeOverlapping(List<ATOIsland> islands)
        {
            if (islands.Count <= 1) return islands;
            int g = MergeGridSize;
            var grid = new int[g * g]; // first island id stamped (+1) / 记录首个岛编号+1
            var uf = new UnionFind(islands.Count);

            foreach (var isl in islands)
            {
                for (int t = 0; t < isl.localTriangles.Length; t += 3)
                {
                    ATORaster.RasterTriangle(
                        isl.bakedUVs[isl.localTriangles[t]],
                        isl.bakedUVs[isl.localTriangles[t + 1]],
                        isl.bakedUVs[isl.localTriangles[t + 2]],
                        g, g,
                        (x, y) =>
                        {
                            int idx = y * g + x;
                            int stamped = grid[idx];
                            if (stamped == 0) grid[idx] = isl.index + 1;
                            else if (stamped != isl.index + 1) uf.Union(stamped - 1, isl.index);
                        });
                }
            }

            var byRoot = new Dictionary<int, List<ATOIsland>>();
            foreach (var isl in islands)
            {
                int root = uf.Find(isl.index);
                if (!byRoot.TryGetValue(root, out var list))
                {
                    list = new List<ATOIsland>();
                    byRoot[root] = list;
                }
                list.Add(isl);
            }

            var merged = new List<ATOIsland>(byRoot.Count);
            foreach (var kv in byRoot)
            {
                if (kv.Value.Count == 1)
                {
                    merged.Add(kv.Value[0]);
                    continue;
                }
                // Concatenate baked geometry (offsets already baked per source island).
                // 拼接烘焙几何（平移已在各源岛烘焙时应用）。
                int vBase = 0;
                int vTotal = 0, tTotal = 0;
                foreach (var isl in kv.Value)
                {
                    vTotal += isl.bakedUVs.Length;
                    tTotal += isl.localTriangles.Length;
                }
                var baked = new Vector2[vTotal];
                var orig = new int[vTotal];
                var local = new int[tTotal];
                float uvArea = 0f, world = 0f;
                int triOut = 0;
                int srcCount = 0;
                foreach (var isl in kv.Value)
                {
                    Array.Copy(isl.bakedUVs, 0, baked, vBase, isl.bakedUVs.Length);
                    Array.Copy(isl.origVertexIds, 0, orig, vBase, isl.origVertexIds.Length);
                    for (int i = 0; i < isl.localTriangles.Length; i++)
                        local[triOut + i] = isl.localTriangles[i] + vBase;
                    triOut += isl.localTriangles.Length;
                    vBase += isl.bakedUVs.Length;
                    uvArea += isl.uvArea;
                    world += isl.worldArea;
                    srcCount += isl.sourceTriangleCount;
                }
                var ni = new ATOIsland
                {
                    bakedUVs = baked,
                    origVertexIds = orig,
                    localTriangles = local,
                    sourceTriangleCount = srcCount,
                    uvArea = uvArea,
                    worldArea = world,
                };
                ni.ComputeBoundsFromBaked();
                merged.Add(ni);
            }
            return merged;
        }

        /// <summary>Area-weighted PCA axes from baked geometry. / 基于烘焙几何的面积加权 PCA 轴。</summary>
        private static void ComputePCA(ATOIsland island)
        {
            double sumA = 0, sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
            var tris = island.localTriangles;
            var uvs = island.bakedUVs;
            for (int t = 0; t < tris.Length; t += 3)
            {
                var a = uvs[tris[t]];
                var b = uvs[tris[t + 1]];
                var c = uvs[tris[t + 2]];
                double cx = (a.x + b.x + c.x) / 3.0;
                double cy = (a.y + b.y + c.y) / 3.0;
                double w = Mathf.Abs((b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y)) * 0.5f + 1e-18;
                sumA += w;
                sx += cx * w; sy += cy * w;
                sxx += cx * cx * w; syy += cy * cy * w; sxy += cx * cy * w;
            }
            Vector2 major;
            if (sumA <= 1e-18)
            {
                major = Vector2.right;
            }
            else
            {
                double mx = sx / sumA, my = sy / sumA;
                double cxx = sxx / sumA - mx * mx;
                double cyy = syy / sumA - my * my;
                double cxy = sxy / sumA - mx * my;
                double trace = cxx + cyy;
                double det = cxx * cyy - cxy * cxy;
                double disc = Math.Sqrt(Math.Max(0, trace * trace / 4.0 - det));
                double l1 = trace / 2.0 + disc;
                major = Math.Abs(cxy) > 1e-12
                    ? new Vector2((float)(l1 - cyy), (float)cxy).normalized
                    : (cxx >= cyy ? Vector2.right : Vector2.up);
            }
            var minor = new Vector2(-major.y, major.x);
            island.axisMajor = major;
            island.axisMinor = minor;

            float mnU = float.MaxValue, mxU = float.MinValue, mnV = float.MaxValue, mxV = float.MinValue;
            foreach (var p in uvs)
            {
                float du = Vector2.Dot(p, major);
                float dv = Vector2.Dot(p, minor);
                if (du < mnU) mnU = du; if (du > mxU) mxU = du;
                if (dv < mnV) mnV = dv; if (dv > mxV) mxV = dv;
            }
            island.lenMajor = Mathf.Max(1e-6f, mxU - mnU);
            island.lenMinor = Mathf.Max(1e-6f, mxV - mnV);
        }

        private static int Quant(float f) => Mathf.RoundToInt(f / WeldEpsilon);

        /// <summary>Union-find over ints. / 整数并查集。</summary>
        public sealed class UnionFind
        {
            private readonly int[] parent;
            private readonly byte[] rank;

            public UnionFind(int n)
            {
                parent = new int[n];
                rank = new byte[n];
                for (int i = 0; i < n; i++) parent[i] = i;
            }

            public int Find(int x)
            {
                while (parent[x] != x)
                {
                    parent[x] = parent[parent[x]];
                    x = parent[x];
                }
                return x;
            }

            public void Union(int a, int b)
            {
                int ra = Find(a), rb = Find(b);
                if (ra == rb) return;
                if (rank[ra] < rank[rb]) (ra, rb) = (rb, ra);
                parent[rb] = ra;
                if (rank[ra] == rank[rb]) rank[ra]++;
            }
        }
    }

    /// <summary>Geometry helpers on <see cref="ATOIsland"/>. / <see cref="ATOIsland"/> 的几何辅助。</summary>
    public static class ATOIslandGeometryExt
    {
        /// <summary>Recompute uv bounds from baked UVs. / 从烘焙 UV 重算包围盒。</summary>
        public static void ComputeBoundsFromBaked(this ATOIsland island)
        {
            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            foreach (var p in island.bakedUVs)
            {
                min = Vector2.Min(min, p);
                max = Vector2.Max(max, p);
            }
            island.uvMin = min;
            island.uvMax = max;
        }
    }
}
