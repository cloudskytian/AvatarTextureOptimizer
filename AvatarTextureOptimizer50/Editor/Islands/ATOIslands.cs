// -----------------------------------------------------------------------------
// ATOIslands.cs — UV island extraction, wrap normalization, overlap merge, rasterization.
// ATOIslands.cs — UV 岛提取、wrap 归一化、重叠合并、光栅化。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace net.fosa.ato.editor
{
    internal static class ATOIslands
    {
        // ================================================================= //
        // Extraction / 提取
        // ================================================================= //

        /// <summary>
        /// Extract islands of one mesh on one UV channel. Triangles are welded by
        /// (quantized position, quantized UV); two triangles join an island when they share
        /// a welded edge. Returns islands ordered by descending UV area.
        /// 提取一个网格在某个 UV 通道上的岛。顶点按（量化位置, 量化UV）焊接；共享焊接边的
        /// 三角形归入同一岛。返回的岛按 UV 面积降序。
        /// </summary>
        public static List<IslandInfo> Extract(RendererInfo r, int channel, ATOBuildState st)
        {
            var mesh = r.mesh;
            var uvs = GetUV2(mesh, channel);
            var verts = mesh.vertices;
            int subMeshCount = mesh.subMeshCount;
            var trianglesBySub = new List<int[]>();
            for (int s = 0; s < subMeshCount; s++) trianglesBySub.Add(mesh.GetTriangles(s));

            // ---- weld / 焊接 ----
            var weldMap = new Dictionary<(long, long, long, long, long), int>(); // pos(3)+uv(2) → id
            int vertCount = verts.Length;
            var weldId = new int[vertCount];
            for (int i = 0; i < vertCount; i++)
            {
                var key = (Q(verts[i].x), Q(verts[i].y), Q(verts[i].z), Q(uvs[i].x), Q(uvs[i].y));
                if (!weldMap.TryGetValue(key, out int id))
                {
                    id = weldMap.Count;
                    weldMap[key] = id;
                }

                weldId[i] = id;
            }

            // ---- triangles list + edge adjacency + union-find ----
            var tris = new List<(int sub, int a, int b, int c)>();
            var edgeOwner = new Dictionary<(int, int), int>(); // first triangle seen on edge / 首个占用该边的三角形
            var uf = new UnionFind();

            for (int s = 0; s < subMeshCount; s++)
            {
                var t = trianglesBySub[s];
                for (int i = 0; i < t.Length; i += 3)
                {
                    int idx = tris.Count;
                    int w0 = weldId[t[i]], w1 = weldId[t[i + 1]], w2 = weldId[t[i + 2]];
                    tris.Add((s, t[i], t[i + 1], t[i + 2]));
                    uf.Add();
                    UnionOnEdge(w0, w1, idx);
                    UnionOnEdge(w1, w2, idx);
                    UnionOnEdge(w2, w0, idx);
                }
            }

            void UnionOnEdge(int u, int v, int triIdx)
            {
                if (u == v) return;
                var key = (Mathf.Min(u, v), Mathf.Max(u, v));
                if (edgeOwner.TryGetValue(key, out int other)) uf.Union(other, triIdx);
                else edgeOwner[key] = triIdx;
            }

            // ---- group triangles into islands / 分组为岛 ----
            var islandsByRoot = new Dictionary<int, IslandInfo>();
            var islands = new List<IslandInfo>();
            for (int i = 0; i < tris.Count; i++)
            {
                int root = uf.Find(i);
                if (!islandsByRoot.TryGetValue(root, out var island))
                {
                    islandsByRoot[root] = island = new IslandInfo
                    {
                        id = islands.Count,
                        group = null, // filled by caller / 由调用方填充
                    };
                    islands.Add(island);
                }

                var (sub, a, b, c) = tris[i];
                island.triangles.Add((sub, a, b, c));
            }

            return islands.OrderByDescending(x => x.triangles.Count).ToList();
        }

        private static long Q(float v) => (long)Mathf.Round(v * 8192f);

        private static Vector2[] GetUV2(Mesh mesh, int channel)
        {
            var l2 = new List<Vector2>();
            try
            {
                mesh.GetUVs(channel, l2);
                return l2.ToArray();
            }
            catch (Exception)
            {
                // UV stored as Vector3/4 — take XY / 以 Vector3/4 存储时取 XY
                var l3 = new List<Vector3>();
                mesh.GetUVs(channel, l3);
                return l3.Select(v => (Vector2)v).ToArray();
            }
        }

        // ================================================================= //
        // Normalization & metrics / 归一化与度量
        // ================================================================= //

        /// <summary>
        /// Compute per-island UV bounds, wrap normalization and world area.
        /// Returns false-textures-to-whitelist via out list when an island crosses a wrap seam.
        /// 计算每岛 UV 包围盒、wrap 归一化与世界面积。岛跨 wrap 缝时通过 out 返回需白名单化的纹理。
        /// </summary>
        public static void Analyze(IslandInfo island, RendererInfo r, int channel,
            List<string> wrapWarnings)
        {
            var mesh = r.mesh;
            var uvs = GetUV2(mesh, channel);

            // ---- bounds over this island's vertices / 本岛顶点包围盒 ----
            RefreshVertexList(island);
            Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
            Vector2 max = new Vector2(float.MinValue, float.MinValue);
            foreach (var vi in island.vertexIndices)
            {
                var uv = uvs[vi];
                if (uv.x < min.x) min.x = uv.x;
                if (uv.y < min.y) min.y = uv.y;
                if (uv.x > max.x) max.x = uv.x;
                if (uv.y > max.y) max.y = uv.y;
            }

            // ---- wrap analysis / wrap 分析 ----
            // All UVs must lie inside one integer tile [k, k+1] for a translation to work.
            // 所有 UV 必须落在同一整数瓦片 [k, k+1] 内，整体平移才可行。
            int kx = Mathf.FloorToInt(min.x), ky = Mathf.FloorToInt(min.y);
            bool crossesSeam = (Mathf.FloorToInt(max.x) != kx) || (Mathf.FloorToInt(max.y) != ky);
            bool tooLarge = (max.x - min.x) > 1f + 1e-4f || (max.y - min.y) > 1f + 1e-4f;

            if (crossesSeam || tooLarge)
            {
                island.wrapCrossing = true;
                wrapWarnings.Add(
                    $"Island with {island.triangles.Count} tris on '{r.path}' UV{channel} crosses wrap seam " +
                    $"(UV range {min:F2}..{max:F2}) → whitelist / 跨wrap缝→白名单");
                island.uvBounds = Rect.MinMaxRect(min.x, min.y, max.x, max.y);
            }
            else
            {
                // translate into [0,1] / 平移进 [0,1]
                var offset = new Vector2(-kx, -ky);
                island.uvOffset = offset;
                island.uvBounds = Rect.MinMaxRect(min.x + offset.x, min.y + offset.y,
                    max.x + offset.x, max.y + offset.y);
            }

            // ---- UV area (approx: sum of triangle areas in UV space) / UV 面积 ----
            island.uvArea = 0f;
            foreach (var (_, a, b, c) in island.triangles)
                island.uvArea += UvTriArea(uvs[a] - uvs[b], uvs[a] - uvs[c]);
            if (island.wrapCrossing) island.uvArea = Mathf.Min(island.uvArea, 1f);

            // ---- world area with blendshapes & scale / 世界面积（含形态键与缩放） ----
            island.worldArea = ComputeWorldArea(island, r, wrapWarnings);
        }

        /// <summary>Per-island world area: max over base and per-blendshape(0/100) variants,
        /// times renderer lossy scale and animation scale factor.
        /// 岛世界面积：基础与各形态键(0/100)取最大，乘渲染器静态缩放与动画缩放系数。</summary>
        private static float ComputeWorldArea(IslandInfo island, RendererInfo r, List<string> warn)
        {
            var mesh = r.mesh;
            var verts = mesh.vertices;
            var lossy = r.renderer != null ? r.renderer.transform.lossyScale : Vector3.one;
            float baseArea = 0f;
            foreach (var (_, a, b, c) in island.triangles)
                baseArea += WorldTriArea(Scale(verts[a]), Scale(verts[b]), Scale(verts[c]));
            baseArea *= PairwiseScale(lossy) * r.scaleAreaFactor;
            float best = baseArea;

            // Blendshape variants / 形态键变体（仅0与100两态取大）
            var tracked = r.blendshapeMax;
            if (tracked.Count > 0)
            {
                var dv = new Vector3[mesh.vertexCount];
                var dn = new Vector3[mesh.vertexCount];
                var dt = new Vector3[mesh.vertexCount];

                foreach (var (name, weight) in tracked)
                {
                    int si = mesh.GetBlendShapeIndex(name);
                    if (si < 0) continue;
                    int frame = mesh.GetBlendShapeFrameCount(si) - 1; // use last frame / 用最后一帧
                    mesh.GetBlendShapeFrameVertices(si, frame, dv, dn, dt);

                    float area = 0f;
                    foreach (var (_, a, b, c) in island.triangles)
                    {
                        // Evaluate the stronger of weight & 100 → always 100 (max state)
                        // 取 0/100 两态中较大 → 直接评估 100 态
                        var pa = Scale(verts[a] + dv[a]);
                        var pb = Scale(verts[b] + dv[b]);
                        var pc = Scale(verts[c] + dv[c]);
                        area += WorldTriArea(pa, pb, pc);
                    }

                    area *= PairwiseScale(lossy) * r.scaleAreaFactor;
                    if (area > best) best = area;
                }
            }

            return best;

            Vector3 Scale(Vector3 v) => v;
        }

        private static float PairwiseScale(Vector3 s)
        {
            float ax = Mathf.Abs(s.x), ay = Mathf.Abs(s.y), az = Mathf.Abs(s.z);
            return Mathf.Max(ax * ay, Mathf.Max(ax * az, ay * az));
        }

        private static float WorldTriArea(Vector3 a, Vector3 b, Vector3 c) =>
            Vector3.Cross(b - a, c - a).magnitude * 0.5f;

        private static float UvTriArea(Vector2 e1, Vector2 e2) =>
            Mathf.Abs(e1.x * e2.y - e1.y * e2.x) * 0.5f;

        internal static void RefreshVertexList(IslandInfo island)
        {
            island.vertexIndices.Clear();
            var seen = new HashSet<int>();
            foreach (var (_, a, b, c) in island.triangles)
            {
                if (seen.Add(a)) island.vertexIndices.Add(a);
                if (seen.Add(b)) island.vertexIndices.Add(b);
                if (seen.Add(c)) island.vertexIndices.Add(c);
            }
        }

        // ================================================================= //
        // Overlap merge / 重叠合并
        // ================================================================= //

        /// <summary>
        /// Merge islands whose footprints are near-identical (classic mirrored-UV case).
        /// Duplicates are attached to the primary island; the packer places only primaries.
        /// 合并接 footprint 近乎相同的岛（经典镜像UV场景）。重复岛挂到主岛；装箱只处理主岛。
        /// </summary>
        public static int MergeOverlaps(List<IslandInfo> islands)
        {
            // Bounding-box equality + matching UV area is a strong & cheap footprint signature
            // for mirror duplicates; both islands then share the primary's raster & placement.
            // 包围盒相同 + UV 面积一致对镜像重复岛是强且廉价的签名；两岛共用主岛的光栅与放置。
            int merged = 0;
            var claimed = new HashSet<int>();
            for (int i = 0; i < islands.Count; i++)
            {
                if (claimed.Contains(i)) continue;
                var pri = islands[i];
                for (int j = i + 1; j < islands.Count; j++)
                {
                    if (claimed.Contains(j)) continue;
                    var dup = islands[j];
                    if (!pri.wrapCrossing && !dup.wrapCrossing &&
                        NearlyEqual(pri.uvBounds, dup.uvBounds) &&
                        TrianglesCongruent(pri, dup))
                    {
                        pri.mergedDuplicates.Add(dup);
                        claimed.Add(j);
                        merged++;
                    }
                }
            }

            return merged;
        }

        private static bool NearlyEqual(Rect a, Rect b)
        {
            const float eps = 1e-3f;
            return Mathf.Abs(a.xMin - b.xMin) < eps && Mathf.Abs(a.yMin - b.yMin) < eps &&
                   Mathf.Abs(a.width - b.width) < eps && Mathf.Abs(a.height - b.height) < eps;
        }

        /// <summary>Do the two islands cover (nearly) the same UV area? Uses bbox grid sampling.
        /// 两岛是否覆盖（近似）同一UV区域？用包围盒网格采样。</summary>
        private static bool TrianglesCongruent(IslandInfo a, IslandInfo b)
        {
            // Same normalized bbox + similar UV area ⇒ same footprint (mirror duplicates share area)
            // 归一化包围盒相同 + UV 面积相近 ⇒ footprint 相同（镜像重复岛面积一致）
            return Mathf.Abs(a.uvArea - b.uvArea) < Mathf.Max(1e-5f, 0.02f * Mathf.Max(a.uvArea, b.uvArea));
        }

        // ================================================================= //
        // Rasterization / 光栅化（4px 粒度）
        // ================================================================= //

        /// <summary>
        /// Rasterize an island's footprint into a cell bitmask (cell = IslandRaster.Cell px).
        /// Conservative: a cell is set when any source triangle overlaps it, plus 1-cell dilation.
        /// 将岛 footprint 光栅化为单元格位掩码（每格 IslandRaster.Cell 像素）。保守判定：
        /// 三角形接触到的格子置位，并再膨胀 1 格。
        /// </summary>
        public static IslandRaster Rasterize(IslandInfo island, int pxWidth, int pxHeight)
        {
            int cw = Mathf.Max(1, (pxWidth + IslandRaster.Cell - 1) / IslandRaster.Cell);
            int ch = Mathf.Max(1, (pxHeight + IslandRaster.Cell - 1) / IslandRaster.Cell);
            var raster = new IslandRaster { cellsW = cw, cellsH = ch, rows = new ulong[ch] };

            var mesh = island.group.owner.mesh;
            var uvs = GetUV2(mesh, island.group.channel);

            foreach (var (_, a, b, c) in island.triangles)
            {
                // island-local pixel coords / 岛内局部像素坐标
                var pa = ToCell(uvs[a]);
                var pb = ToCell(uvs[b]);
                var pc = ToCell(uvs[c]);
                RasterizeTriangle(raster, pa, pb, pc);
            }

            // dilate by 1 cell (bilinear safety) / 膨胀1格（双线性安全）
            return Dilate(raster);

            Vector2 ToCell(Vector2 uv)
            {
                // rawUV + uvOffset = normalized UV; then into the island's pixel rect.
                // 原始UV + uvOffset = 归一化UV；再映射到岛的像素矩形。
                var local = new Vector2(
                    (uv.x + island.uvOffset.x - island.uvBounds.xMin) / Mathf.Max(1e-6f, island.uvBounds.width),
                    (uv.y + island.uvOffset.y - island.uvBounds.yMin) / Mathf.Max(1e-6f, island.uvBounds.height));
                return new Vector2(local.x * (cw - 1), local.y * (ch - 1));
            }
        }

        private static void RasterizeTriangle(IslandRaster r, Vector2 a, Vector2 b, Vector2 c)
        {
            float minX = Mathf.Max(0f, Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x))));
            float maxX = Mathf.Min(r.cellsW - 1, Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x))));
            float minY = Mathf.Max(0f, Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y))));
            float maxY = Mathf.Min(r.cellsH - 1, Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y))));

            for (int y = (int)minY; y <= (int)maxY; y++)
            {
                for (int x = (int)minX; x <= (int)maxX; x++)
                {
                    if (TriangleOverlapsCell(a, b, c, x, y))
                        r.rows[y] |= 1ul << x;
                }
            }
        }

        /// <summary>Conservative triangle vs unit-cell (with 0.5 px center sampling fallback)
        /// overlap test. / 保守的三角形与单元格重叠测试（附加中心采样兜底）。</summary>
        private static bool TriangleOverlapsCell(Vector2 a, Vector2 b, Vector2 c, int x, int y)
        {
            // bbox reject / 包围盒剔除
            if (x < Min3(a.x, b.x, c.x) - 1 || x > Max3(a.x, b.x, c.x) + 1 ||
                y < Min3(a.y, b.y, c.y) - 1 || y > Max3(a.y, b.y, c.y) + 1) return false;

            // conservative: treat cell as center point plus radius 0.71 (half diagonal)
            // 保守：单元格视为中心点+半径0.71（半对角线）
            var p = new Vector2(x + 0.5f, y + 0.5f);
            return PointNearTriangle(p, a, b, c, 0.71f);
        }

        private static bool PointNearTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c, float slack)
        {
            // inside test with slack via signed distances / 带余量的符号距离内部测试
            float d1 = Cross(p - a, b - a);
            float d2 = Cross(p - b, c - b);
            float d3 = Cross(p - c, a - c);
            bool neg = d1 < -slack || d2 < -slack || d3 < -slack;
            bool pos = d1 > slack || d2 > slack || d3 > slack;
            bool inside = !(neg && pos);
            if (inside) return true;

            // distance to edges (cheap, for thin triangles) / 边距离（细长三角形兜底）
            return SegDist(p, a, b) <= slack || SegDist(p, b, c) <= slack || SegDist(p, c, a) <= slack;
        }

        private static float Cross(Vector2 u, Vector2 v) => u.x * v.y - u.y * v.x;

        private static float SegDist(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(1e-8f, ab.sqrMagnitude));
            return Vector2.Distance(p, a + ab * t);
        }

        private static float Min3(float x, float y, float z) => Mathf.Min(x, Mathf.Min(y, z));
        private static float Max3(float x, float y, float z) => Mathf.Max(x, Mathf.Max(y, z));

        /// <summary>1-cell dilation / 膨胀1格。</summary>
        public static IslandRaster Dilate(IslandRaster r) => DilateN(r, 1);

        /// <summary>Dilate by N cells / 膨胀 N 格。</summary>
        public static IslandRaster DilateN(IslandRaster r, int n)
        {
            var cur = r;
            for (int i = 0; i < Mathf.Max(0, n); i++)
            {
                var o = new IslandRaster { cellsW = cur.cellsW, cellsH = cur.cellsH, rows = new ulong[cur.cellsH] };
                for (int y = 0; y < cur.cellsH; y++)
                {
                    ulong c = cur.rows[y];
                    o.rows[y] = c | (c << 1) | (c >> 1);
                    if (y > 0) o.rows[y] |= cur.rows[y - 1];
                    if (y < cur.cellsH - 1) o.rows[y] |= cur.rows[y + 1];
                }

                cur = o;
            }

            return cur;
        }
    }

    /// <summary>Weighted quick-union / 加权并查集。</summary>
    internal sealed class UnionFind
    {
        private readonly List<int> _parent = new List<int>();
        private readonly List<int> _size = new List<int>();

        public int Add()
        {
            _parent.Add(_parent.Count);
            _size.Add(1);
            return _parent.Count - 1;
        }

        public int Find(int x)
        {
            while (_parent[x] != x)
            {
                _parent[x] = _parent[_parent[x]]; // path halving / 路径折半
                x = _parent[x];
            }

            return x;
        }

        public void Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra == rb) return;
            if (_size[ra] < _size[rb]) (ra, rb) = (rb, ra);
            _parent[rb] = ra;
            _size[ra] += _size[rb];
        }
    }
}
